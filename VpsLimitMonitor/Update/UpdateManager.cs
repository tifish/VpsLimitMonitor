using System.Diagnostics;
using System.IO.Compression;
using Avalonia.Threading;
using JeekTools;
using Microsoft.Extensions.Logging;
using VpsLimitMonitor.Core;
using VpsLimitMonitor.Settings;
using ZLogger;

namespace VpsLimitMonitor.Update;

/// <summary>
///     自动更新：拉取 GitHub Release 的 version.txt 与本地版本（commit 数量）比对，
///     经镜像下载 zip 解压后，调用 PowerShell 脚本替换文件并重启，保留用户数据。
/// </summary>
public static class UpdateManager
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(UpdateManager));

    private const string ReleaseBaseUrl =
        "https://github.com/tifish/VpsLimitMonitor/releases/latest/download/";
    private const string ZipName = "VpsLimitMonitor-win-x64.zip";

    /// <summary>调试用下载地址覆盖（以 / 结尾）。非空时开发版也执行检查与更新。</summary>
    public static string? OverrideBaseUrl { get; set; }

    /// <summary>本地版本号（主版本位 = commit 数量），开发版为 0。</summary>
    public static int LocalVersion { get; } =
        typeof(UpdateManager).Assembly.GetName().Version?.Major ?? 0;

    public static string LocalVersionText { get; } =
        LocalVersion == 0 ? "开发版" : $"v{LocalVersion}";

    public static string LastCheckResult { get; private set; } = "尚未检查";
    public static bool Updating { get; private set; }

    private static MonitorController _controller = null!;
    private static DateTime _lastCheck = DateTime.MinValue;

    public static void Start(MonitorController controller)
    {
        _controller = controller;
        _ = CheckLoopAsync();
    }

    private static async Task CheckLoopAsync()
    {
        // 启动时检查，稍作延迟避免拖慢启动
        await Task.Delay(TimeSpan.FromSeconds(15));
        await CheckForUpdateAsync();

        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(10));

            TimeSpan? interval = SettingsManager.Settings.UpdateCheckInterval switch
            {
                "Every6Hours" => TimeSpan.FromHours(6),
                "Daily" => TimeSpan.FromDays(1),
                "Weekly" => TimeSpan.FromDays(7),
                _ => null,
            };
            if (interval != null && DateTime.Now - _lastCheck >= interval)
                await CheckForUpdateAsync();
        }
    }

    /// <summary>检查更新；apply 为 true 时发现新版本立即下载并重启更新。返回结果描述。</summary>
    public static async Task<string> CheckForUpdateAsync(bool apply = true)
    {
        _lastCheck = DateTime.Now;

        if (LocalVersion == 0 && OverrideBaseUrl == null)
        {
            LastCheckResult = "开发版不检查更新";
            return LastCheckResult;
        }

        if (Updating)
            return "更新已在进行中";

        try
        {
            var baseUrl = OverrideBaseUrl ?? ReleaseBaseUrl;
            var remoteVersion = await FetchRemoteVersionAsync(baseUrl);

            if (remoteVersion <= LocalVersion)
            {
                LastCheckResult = $"已是最新版本（本地 {LocalVersionText}，远程 v{remoteVersion}）";
                Log.ZLogInformation($"Update check: up to date (local {LocalVersion}, remote {remoteVersion})");
                return LastCheckResult;
            }

            LastCheckResult = $"发现新版本 v{remoteVersion}（当前 {LocalVersionText}）";
            Log.ZLogInformation($"Update check: new version {remoteVersion} available (local {LocalVersion})");

            if (apply)
                await DownloadAndApplyAsync(baseUrl, remoteVersion);

            return LastCheckResult;
        }
        catch (Exception ex)
        {
            LastCheckResult = $"检查更新失败：{ex.Message}";
            Log.ZLogWarning($"Update check failed: {ex.Message}");
            return LastCheckResult;
        }
    }

    private static async Task<int> FetchRemoteVersionAsync(string baseUrl)
    {
        Exception? lastError = null;
        foreach (var url in GitHubMirrors.GetMirrors(baseUrl + "version.txt").Distinct())
        {
            try
            {
                using var client = HttpHelper.GetHttpClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                var text = await client.GetStringAsync(url);
                return int.Parse(text.Trim());
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException($"无法获取 version.txt：{lastError?.Message}");
    }

    private static async Task DownloadAndApplyAsync(string baseUrl, int remoteVersion)
    {
        Updating = true;
        try
        {
            var zipUrl = baseUrl + ZipName;
            var mirrorUrl = await GitHubMirrors.GetFastestMirror(zipUrl);
            if (mirrorUrl == "")
                mirrorUrl = zipUrl;

            var updateDir = Path.Combine(Path.GetTempPath(), "VpsLimitMonitor-Update");
            if (Directory.Exists(updateDir))
                Directory.Delete(updateDir, true);
            Directory.CreateDirectory(updateDir);

            Log.ZLogInformation($"Downloading update from {mirrorUrl}");
            var zipPath = Path.Combine(updateDir, ZipName);
            using (var client = HttpHelper.GetHttpClient())
            {
                client.Timeout = TimeSpan.FromMinutes(10);
                await using var source = await client.GetStreamAsync(mirrorUrl);
                await using var target = File.Create(zipPath);
                await source.CopyToAsync(target);
            }

            var payloadDir = Path.Combine(updateDir, "Payload");
            ZipFile.ExtractToDirectory(zipPath, payloadDir);
            File.Delete(zipPath);

            var scriptPath = Path.Combine(updateDir, "Update.ps1");
            await File.WriteAllTextAsync(scriptPath, UpdateScript);

            _controller.Alerts.ShowToast(
                "VPS 流量监视器",
                $"正在更新到 v{remoteVersion}，程序将自动重启"
            );
            Log.ZLogInformation($"Launching update script for v{remoteVersion}");

            Process.Start(
                new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    ArgumentList =
                    {
                        "-NoProfile",
                        "-ExecutionPolicy",
                        "Bypass",
                        "-File",
                        scriptPath,
                        "-ProcessId",
                        Environment.ProcessId.ToString(),
                        "-AppDir",
                        AppContext.BaseDirectory,
                        "-PayloadDir",
                        payloadDir,
                        "-ExePath",
                        Environment.ProcessPath!,
                    },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            );

            // 留出时间让调用方（托盘/MCP）拿到返回结果后再退出
            await Task.Delay(1000);
            Dispatcher.UIThread.Post(() => _controller.Exit());
        }
        catch
        {
            Updating = false;
            throw;
        }
    }

    // 等待进程退出后镜像同步新文件（清理旧版本多余文件，保留日志与便携配置），再重启
    private const string UpdateScript = """
        param([int]$ProcessId, [string]$AppDir, [string]$PayloadDir, [string]$ExePath)
        Wait-Process -Id $ProcessId -ErrorAction SilentlyContinue
        robocopy $PayloadDir $AppDir /MIR /XD Logs Config /R:10 /W:1 | Out-Null
        Start-Process $ExePath -WorkingDirectory $AppDir
        Remove-Item $PSScriptRoot -Recurse -Force -ErrorAction SilentlyContinue
        """;
}

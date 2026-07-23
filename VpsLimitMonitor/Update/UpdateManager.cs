using Avalonia.Threading;
using JeekTools;
using Microsoft.Extensions.Logging;
using VpsLimitMonitor.Core;
using VpsLimitMonitor.Settings;
using ZLogger;

namespace VpsLimitMonitor.Update;

/// <summary>
///     自动更新：基于 JeekTools 的 AutoUpdater（version.txt 各镜像竞速比对版本、
///     zip 镜像限速换源下载、应用内暂存校验），最后调用 bin 目录里的
///     AutoUpdate.ps1 替换文件并重启，保留用户数据。
/// </summary>
public static class UpdateManager
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(UpdateManager));

    private const string ReleaseBaseUrl =
        "https://github.com/tifish/VpsLimitMonitor/releases/latest/download/";
    private const string ZipName = "VpsLimitMonitor-win-x64.zip";
    private const string ExeName = "VpsLimitMonitor.exe";

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

    // 并行 Debug 实例各用自己的暂存目录，避免争抢；按安装目录哈希命名，
    // 同一安装位置反复启动不会累积新目录
    private static readonly string TempRoot = Path.Combine(
        Path.GetTempPath(),
        $"{SettingsManager.AppName}-{Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToLowerInvariant())
            )
        )[..8]}"
    );

    private static AutoUpdater CreateUpdater()
    {
        var baseUrl = OverrideBaseUrl ?? ReleaseBaseUrl;
        Directory.CreateDirectory(TempRoot);

        return new AutoUpdater(
            new AutoUpdaterOptions
            {
                AppExeName = ExeName,
                ReleaseZipUrl = baseUrl + ZipName,
                VersionTxtUrl = baseUrl + "version.txt",
                UserAgent = SettingsManager.AppName,
                Disabled = LocalVersion == 0 && OverrideBaseUrl == null,
                TempRoot = TempRoot,
                GetLocalVersion = () => LocalVersion,
                // 覆盖地址用于本地模拟发布，此时允许开发版（版本 0）更新
                MinimumValidLocalVersion = OverrideBaseUrl != null ? 0 : 10,
            }
        );
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

        var updater = CreateUpdater();
        var outcome = await updater.HasUpdateAsync();
        switch (outcome)
        {
            case UpdateCheckOutcome.UpToDate:
                LastCheckResult =
                    $"已是最新版本（本地 {LocalVersionText}，远程 v{updater.RemoteVersion}）";
                return LastCheckResult;

            case UpdateCheckOutcome.Failed:
                LastCheckResult = $"检查更新失败：{updater.FailureReason}";
                return LastCheckResult;
        }

        LastCheckResult = $"发现新版本 v{updater.RemoteVersion}（当前 {LocalVersionText}）";
        if (apply)
            await DownloadAndApplyAsync(updater);

        return LastCheckResult;
    }

    private static async Task DownloadAndApplyAsync(AutoUpdater updater)
    {
        Updating = true;
        try
        {
            var stagedDir = await updater.DownloadAndStageAsync();
            if (stagedDir == null)
            {
                LastCheckResult = $"下载更新失败：{updater.FailureReason}";
                Updating = false;
                return;
            }

            _controller.Alerts.ShowToast(
                "VPS 流量监视器",
                $"正在更新到 v{updater.RemoteVersion}，程序将自动重启"
            );

            if (!updater.LaunchInstall(stagedDir))
            {
                LastCheckResult = "启动更新脚本失败";
                Updating = false;
                return;
            }

            Log.ZLogInformation($"Update launched for v{updater.RemoteVersion}, exiting");

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
}

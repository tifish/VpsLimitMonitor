using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace VpsLimitMonitor.Settings;

/// <summary>本机相关设置：存储模式与自定义目录，始终放在 %LocalAppData%。</summary>
public class MachineSettings
{
    public StorageLocation StorageLocation { get; set; } = StorageLocation.UserDirectory;
    public string CustomDir { get; set; } = "";
}

/// <summary>
///     可漫游设置存储，基于 JeekTools 的 SettingsStorage / JsonSettingsFile：
///     程序目录存在 Config 时强制便携模式；保存用三方合并原子写；
///     监控 Config 目录变化，静默 10 秒后重新加载。
/// </summary>
public static class SettingsManager
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(SettingsManager));
    private const int CurrentBuiltInAccountsVersion = 1;

    public const string AppName = "VpsLimitMonitor";

    public static SettingsStorage Storage { get; } = new(AppName);

    public static StorageLocation Location { get; private set; } = StorageLocation.UserDirectory;
    public static string CustomDir { get; private set; } = "";
    public static string RoamingConfigDir { get; private set; } = "";
    public static AppSettings Settings { get; private set; } = new();

    /// <summary>配置文件在程序外部被修改并重新加载后触发（UI 线程）。</summary>
    public static event Action? SettingsReloaded;

    private static AppSettings _baseline = new();
    private static string _lastSavedJson = "";
    private static FileSystemWatcher? _watcher;
    private static Timer? _reloadTimer;
    private static bool _pendingReload;

    public static void Init()
    {
        var machine = LoadMachineSettings();
        CustomDir = machine.CustomDir;
        Location = Storage.ResolveEffectiveLocation(machine.StorageLocation);
        RoamingConfigDir = Storage.ResolveConfigRoot(Location, CustomDir);

        Directory.CreateDirectory(RoamingConfigDir);
        Load();

        if (EnsureBuiltInAccounts())
            Save();

        StartWatcher();
        Log.ZLogInformation($"Settings loaded from {RoamingConfigDir} (location: {Location})");
    }

    /// <summary>
    ///     切换存储模式。moveFiles 为 true 时整体移动 Config 目录（原目录不保留）；
    ///     为 false 时直接加载目标位置已有的配置。离开便携模式时在此处强制要求移动。
    /// </summary>
    public static void SwitchStorageLocation(
        StorageLocation location,
        string? customDir,
        bool moveFiles
    )
    {
        if (location == StorageLocation.CustomDirectory && string.IsNullOrWhiteSpace(customDir))
            throw new ArgumentException(
                "A custom directory is required for CustomDirectory.",
                nameof(customDir)
            );
        if (
            Location == StorageLocation.ProgramDirectory
            && location != StorageLocation.ProgramDirectory
            && !moveFiles
        )
        {
            throw new InvalidOperationException(
                "Leaving portable storage requires moving the configuration files."
            );
        }

        var newCustomDir = location == StorageLocation.CustomDirectory ? customDir ?? "" : CustomDir;
        var newConfigDir = Storage.ResolveConfigRoot(location, newCustomDir);
        var oldConfigDir = RoamingConfigDir;
        if (string.Equals(newConfigDir, oldConfigDir, StringComparison.OrdinalIgnoreCase))
            return;

        StopWatcher();
        try
        {
            if (moveFiles)
                SettingsStorage.MoveConfigRoot(oldConfigDir, newConfigDir);
            else
                Directory.CreateDirectory(newConfigDir);

            Location = location;
            CustomDir = newCustomDir;
            RoamingConfigDir = newConfigDir;

            SaveMachineSettings();
            Load();
            if (EnsureBuiltInAccounts())
                Save();

            Log.ZLogInformation(
                $"Storage switched to {location} at {newConfigDir} (moved: {moveFiles})"
            );
            SettingsReloaded?.Invoke();
        }
        finally
        {
            StartWatcher();
        }
    }

    private static string SettingsFilePath => Storage.ResolveSettingsPath(Location, CustomDir);

    private static MachineSettings LoadMachineSettings()
    {
        JsonSettingsFile.TryLoad(Storage.MachineSettingsPath, out MachineSettings machine);
        return machine;
    }

    private static void SaveMachineSettings()
    {
        var machine = new MachineSettings { StorageLocation = Location, CustomDir = CustomDir };
        Directory.CreateDirectory(Storage.LocalConfigDir);
        SharedDataFile.WriteAllTextAtomic(
            Storage.MachineSettingsPath,
            JsonSettingsFile.Serialize(machine)
        );
    }

    private static void Normalize(AppSettings settings)
    {
        settings.Accounts ??= [];
        settings.PollIntervalMinutes = Math.Max(1, settings.PollIntervalMinutes);
        settings.AlertRemainingPercent = Math.Clamp(settings.AlertRemainingPercent, 0, 100);
        settings.StockMonitorIntervalMinutes = Math.Max(1, settings.StockMonitorIntervalMinutes);
        settings.StockMonitorUrl = settings.StockMonitorUrl?.Trim() ?? "";
        if (
            !Uri.TryCreate(settings.StockMonitorUrl, UriKind.Absolute, out var stockUri)
            || stockUri.Scheme is not ("http" or "https")
        )
        {
            settings.StockMonitorUrl = AppSettings.DefaultStockMonitorUrl;
        }
        if (settings.Theme is not ("System" or "Light" or "Dark"))
            settings.Theme = "System";
        if (settings.UpdateCheckInterval is not ("Every6Hours" or "Daily" or "Weekly" or "None"))
            settings.UpdateCheckInterval = "Daily";
    }

    private static bool EnsureBuiltInAccounts()
    {
        if (Settings.BuiltInAccountsVersion >= CurrentBuiltInAccountsVersion)
            return false;

        if (Settings.Accounts.Count == 0)
        {
            Settings.Accounts.Add(
                new AccountConfig
                {
                    Name = "NovixLink",
                    Type = "WhmcsCubeCloud",
                    BaseUrl = "https://www.novixlink.com",
                }
            );
        }

        if (
            !Settings.Accounts.Any(account =>
                string.Equals(
                    account.BaseUrl.TrimEnd('/'),
                    "https://my.hostyun.com",
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
        {
            Settings.Accounts.Add(
                new AccountConfig
                {
                    Name = "HostYun",
                    Type = "IdcSystemKvm",
                    BaseUrl = "https://my.hostyun.com",
                }
            );
        }

        Settings.BuiltInAccountsVersion = CurrentBuiltInAccountsVersion;
        return true;
    }

    private static void Load()
    {
        JsonSettingsFile.TryLoad(SettingsFilePath, out AppSettings settings);
        Normalize(settings);
        Settings = settings;
        _baseline = JsonSettingsFile.Clone(settings);
        _lastSavedJson = JsonSettingsFile.Serialize(settings);
    }

    public static void Save(bool forceAllLocal = false)
    {
        if (
            !JsonSettingsFile.TryMergeAndWrite(
                SettingsFilePath,
                _baseline,
                Settings,
                Normalize,
                forceAllLocal,
                out var merged
            )
        )
        {
            Log.ZLogError($"Failed to save settings to {SettingsFilePath}");
            return;
        }

        Settings = merged;
        _baseline = JsonSettingsFile.Clone(merged);
        _lastSavedJson = JsonSettingsFile.Serialize(merged);
    }

    private static void StartWatcher()
    {
        _watcher = new FileSystemWatcher(RoamingConfigDir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnConfigFileChanged;
        _watcher.Created += OnConfigFileChanged;
        _watcher.Renamed += OnConfigFileChanged;

        // 静默期检查：每次变化重置 10 秒计时，到期后加载。
        _reloadTimer = new Timer(
            _ =>
            {
                if (!_pendingReload)
                    return;
                _pendingReload = false;
                ReloadIfChanged();
            },
            null,
            Timeout.Infinite,
            Timeout.Infinite
        );
    }

    private static void StopWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        _reloadTimer?.Dispose();
        _reloadTimer = null;
        _pendingReload = false;
    }

    private static void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!string.Equals(e.Name, "settings.json", StringComparison.OrdinalIgnoreCase))
            return;

        _pendingReload = true;
        _reloadTimer?.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
    }

    private static void ReloadIfChanged()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
                return;

            var json = File.ReadAllText(SettingsFilePath);
            if (json == _lastSavedJson)
                return; // 是自己写入的，忽略

            if (!JsonSettingsFile.TryLoad(SettingsFilePath, out AppSettings settings))
                return;

            Normalize(settings);
            Settings = settings;
            _baseline = JsonSettingsFile.Clone(settings);
            _lastSavedJson = json;
            Log.ZLogInformation($"Settings reloaded from external change");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => SettingsReloaded?.Invoke());
        }
        catch (Exception ex)
        {
            Log.ZLogError($"Failed to reload settings: {ex.Message}");
        }
    }
}

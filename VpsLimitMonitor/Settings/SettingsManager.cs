using System.Text.Json;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace VpsLimitMonitor.Settings;

public enum StorageMode
{
    Default,
    Portable,
    Custom,
}

/// <summary>
///     可漫游设置存储。默认放 %AppData%\VpsLimitMonitor\Config；
///     若程序目录下存在 Config 目录则强制便携模式；自定义目录记录在本机配置中。
///     监控 Config 目录变化，静默 10 秒后重新加载。
/// </summary>
public static class SettingsManager
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(SettingsManager));

    public const string AppName = "VpsLimitMonitor";
    private const string SettingsFileName = "Settings.json";
    private const string StorageModeFileName = "StorageMode.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string LocalConfigDir { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName,
            "Config"
        );

    public static StorageMode Mode { get; private set; } = StorageMode.Default;
    public static string RoamingConfigDir { get; private set; } = "";
    public static AppSettings Settings { get; private set; } = new();

    /// <summary>配置文件在程序外部被修改并重新加载后触发（UI 线程）。</summary>
    public static event Action? SettingsReloaded;

    private static FileSystemWatcher? _watcher;
    private static Timer? _reloadTimer;
    private static bool _pendingReload;
    private static string _lastSavedJson = "";

    private class StorageModeConfig
    {
        public StorageMode Mode { get; set; } = StorageMode.Default;
        public string CustomDir { get; set; } = "";
    }

    public static void Init()
    {
        var portableDir = Path.Combine(AppContext.BaseDirectory, "Config");
        if (Directory.Exists(portableDir))
        {
            Mode = StorageMode.Portable;
            RoamingConfigDir = portableDir;
        }
        else
        {
            var modeConfig = LoadStorageModeConfig();
            Mode = modeConfig.Mode;
            RoamingConfigDir = Mode switch
            {
                StorageMode.Custom when modeConfig.CustomDir != "" => Path.Combine(
                    modeConfig.CustomDir,
                    "Config"
                ),
                StorageMode.Portable => portableDir,
                _ => Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    AppName,
                    "Config"
                ),
            };
        }

        Directory.CreateDirectory(RoamingConfigDir);
        Load();

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
            Save();
        }

        StartWatcher();
        Log.ZLogInformation($"Settings loaded from {RoamingConfigDir} (mode: {Mode})");
    }

    private static StorageModeConfig LoadStorageModeConfig()
    {
        try
        {
            var path = Path.Combine(LocalConfigDir, StorageModeFileName);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<StorageModeConfig>(File.ReadAllText(path))
                    ?? new StorageModeConfig();
        }
        catch (Exception ex)
        {
            Log.ZLogWarning($"Failed to load storage mode config: {ex.Message}");
        }

        return new StorageModeConfig();
    }

    private static string SettingsFilePath => Path.Combine(RoamingConfigDir, SettingsFileName);

    private static void Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                _lastSavedJson = json;
            }
        }
        catch (Exception ex)
        {
            Log.ZLogError($"Failed to load settings: {ex.Message}");
        }
    }

    public static void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            _lastSavedJson = json;
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (Exception ex)
        {
            Log.ZLogError($"Failed to save settings: {ex.Message}");
        }
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

    private static void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!string.Equals(e.Name, SettingsFileName, StringComparison.OrdinalIgnoreCase))
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

            Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? Settings;
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

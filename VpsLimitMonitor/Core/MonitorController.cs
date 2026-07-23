using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using JeekTools;
using Microsoft.Extensions.Logging;
using VpsLimitMonitor.Providers;
using VpsLimitMonitor.Settings;
using VpsLimitMonitor.Tray;
using VpsLimitMonitor.Update;
using VpsLimitMonitor.Web;
using ZLogger;

namespace VpsLimitMonitor.Core;

/// <summary>程序主控制器：账号状态、轮询循环、托盘与报警的协调。全部在 UI 线程上运行。</summary>
public class MonitorController
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(MonitorController));

    public List<AccountState> Accounts { get; } = [];
    public AlertManager Alerts { get; } = new();
    public TrayIconManager Tray { get; private set; } = null!;
    public DateTime? LastRefresh { get; private set; }
    public bool Refreshing { get; private set; }

    private StatusWindow? _statusWindow;
    private CancellationTokenSource _pollDelayCts = new();

    public async Task StartAsync()
    {
        SettingsManager.Init();
        SettingsManager.SettingsReloaded += OnSettingsReloaded;
        ApplyTheme();
        BuildAccounts();

        Tray = new TrayIconManager(this);
        Tray.Update();

#if DEBUG
        McpDebug.McpDebugServer.Start(this);
#endif

        UpdateManager.Start(this);
        _ = PollLoopAsync();
        await Task.CompletedTask;
    }

    private void BuildAccounts()
    {
        Accounts.Clear();
        foreach (var config in SettingsManager.Settings.Accounts)
        {
            var session = new WebSession(config.BaseUrl, config.Name);
            var provider = ProviderFactory.Create(config, session);
            var account = new AccountState(config, session, provider);
            session.LoginWindowClosed += () => _ = OnLoginWindowClosedAsync(account);
            Accounts.Add(account);
        }
    }

    private async Task PollLoopAsync()
    {
        while (true)
        {
            await RefreshAllAsync();

            var interval = TimeSpan.FromMinutes(
                Math.Max(1, SettingsManager.Settings.PollIntervalMinutes)
            );
            try
            {
                await Task.Delay(interval, _pollDelayCts.Token);
            }
            catch (OperationCanceledException)
            {
                // 设置变更或手动刷新，立即进入下一轮
                _pollDelayCts = new CancellationTokenSource();
            }
        }
    }

    public async Task RefreshAllAsync()
    {
        if (Refreshing)
            return;

        Refreshing = true;
        try
        {
            foreach (var account in Accounts)
                await RefreshAccountAsync(account);

            LastRefresh = DateTime.Now;
        }
        finally
        {
            Refreshing = false;
            Tray.Update();
            _statusWindow?.Rebuild();
        }
    }

    private async Task RefreshAccountAsync(AccountState account)
    {
        try
        {
            await account.Session.EnsureInitializedAsync();

            if (account.Services.Count == 0)
            {
                var services = await account.Provider.ListServicesAsync();
                account.Services.AddRange(services.Select(s => new ServiceState(s)));
                Log.ZLogInformation(
                    $"{account.Config.Name}: discovered {services.Count} services"
                );
            }

            foreach (var svc in account.Services)
            {
                if (svc.Simulated)
                    continue;

                try
                {
                    svc.Traffic = await account.Provider.GetTrafficAsync(svc.Service);
                    svc.LastUpdate = DateTime.Now;
                    svc.Error = null;
                }
                catch (SessionExpiredException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    svc.Error = ex.Message;
                    Log.ZLogWarning(
                        $"{account.Config.Name} {svc.Service.Label}: fetch failed: {ex.Message}"
                    );
                }
            }

            account.LoggedIn = true;
            account.LoginNotified = false;
            account.Error = null;
            account.LastPoll = DateTime.Now;
            Alerts.Evaluate(account);
        }
        catch (SessionExpiredException)
        {
            account.LoggedIn = false;
            Log.ZLogWarning($"{account.Config.Name}: session expired");
            Alerts.NotifyLoginNeeded(account);
        }
        catch (Exception ex)
        {
            account.Error = ex.Message;
            Log.ZLogError($"{account.Config.Name}: refresh failed: {ex}");
        }
    }

    public void TriggerRefresh()
    {
        _pollDelayCts.Cancel();
    }

    public async Task ShowLoginAsync(AccountState account)
    {
        await account.Session.ShowLoginWindowAsync(account.Provider.LoginUrl);
    }

    private async Task OnLoginWindowClosedAsync(AccountState account)
    {
        // 用户关掉登录窗口后立即重试抓取
        await RefreshAccountAsync(account);
        Tray.Update();
        _statusWindow?.Rebuild();
    }

    public void ToggleStatusWindow()
    {
        if (_statusWindow?.IsVisible == true)
        {
            _statusWindow.Hide();
            return;
        }

        _statusWindow ??= new StatusWindow(this);
        _statusWindow.Rebuild();
        _statusWindow.ShowNearTray();
    }

    public void RebuildStatusWindow()
    {
        _statusWindow?.Rebuild();
    }

    private void OnSettingsReloaded()
    {
        ApplyTheme();
        TriggerRefresh();
        Log.ZLogInformation($"Settings change applied");
    }

    public void ApplyTheme()
    {
        var app = Application.Current;
        if (app == null)
            return;

        app.RequestedThemeVariant = SettingsManager.Settings.Theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    public void SetTheme(string theme)
    {
        SettingsManager.Settings.Theme = theme;
        SettingsManager.Save();
        ApplyTheme();
        Tray.UpdateThemeChecks();
    }

    public void SetUpdateCheckInterval(string interval)
    {
        SettingsManager.Settings.UpdateCheckInterval = interval;
        SettingsManager.Save();
        Tray.UpdateUpdateIntervalChecks();
    }

    public async Task SwitchStorageLocationAsync(StorageLocation location)
    {
        if (location == SettingsManager.Location && location != StorageLocation.CustomDirectory)
        {
            Tray.UpdateStorageChecks();
            return;
        }

        string? customDir = null;
        if (location == StorageLocation.CustomDirectory)
        {
            customDir = await PickFolderAsync("选择配置存储目录");
            if (customDir == null)
            {
                Tray.UpdateStorageChecks();
                return;
            }
        }

        var newConfigDir = SettingsManager.Storage.ResolveConfigRoot(location, customDir);
        bool moveFiles;
        if (SettingsManager.Location == StorageLocation.ProgramDirectory)
        {
            // 便携模式下程序目录的 Config 会强制便携，离开时必须移动
            var choice = await ConfirmDialog.ShowAsync(
                "切换配置存储",
                $"离开便携模式必须移动配置文件，否则下次启动仍会回到便携模式。\n\n新位置：{newConfigDir}",
                "移动并切换",
                "取消"
            );
            if (choice != 0)
            {
                Tray.UpdateStorageChecks();
                return;
            }
            moveFiles = true;
        }
        else
        {
            var choice = await ConfirmDialog.ShowAsync(
                "切换配置存储",
                $"是否将现有配置文件移动到新位置？\n\n新位置：{newConfigDir}",
                "移动",
                "不移动",
                "取消"
            );
            if (choice is null or 2)
            {
                Tray.UpdateStorageChecks();
                return;
            }
            moveFiles = choice == 0;
        }

        try
        {
            SettingsManager.SwitchStorageLocation(location, customDir, moveFiles);
            Alerts.ShowToast("配置存储", $"已切换到：{SettingsManager.RoamingConfigDir}");
        }
        catch (Exception ex)
        {
            Log.ZLogError($"Failed to switch storage location: {ex}");
            Alerts.ShowToast("配置存储", $"切换失败：{ex.Message}");
        }

        Tray.UpdateStorageChecks();
    }

    private static async Task<string?> PickFolderAsync(string title)
    {
        // 文件夹选择器需要 TopLevel，托盘应用临时开一个不可见窗口承载
        var owner = new Window
        {
            Width = 1,
            Height = 1,
            Opacity = 0,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        owner.Show();
        try
        {
            var result = await owner.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = title, AllowMultiple = false }
            );
            return result.Count > 0 ? result[0].Path.LocalPath : null;
        }
        finally
        {
            owner.Close();
        }
    }

    public async Task CheckUpdateManuallyAsync()
    {
        var result = await UpdateManager.CheckForUpdateAsync();
        // 有新版本时更新流程自己会弹 Toast；这里只反馈无更新/失败的情况
        if (!UpdateManager.Updating)
            Alerts.ShowToast("检查更新", result);
    }

    public void Exit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    public void ShowStatusWindowFromIpc()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_statusWindow?.IsVisible != true)
                ToggleStatusWindow();
        });
    }
}

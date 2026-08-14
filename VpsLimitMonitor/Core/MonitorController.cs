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
    public StockMonitor Stock { get; private set; } = null!;
    public TrayIconManager Tray { get; private set; } = null!;
    public DateTime? LastRefresh { get; private set; }
    public bool Refreshing { get; private set; }
    public int ServerCount => Accounts.Sum(account => account.Services.Count);
    public string ServerCountText => $"服务器总数：{ServerCount}";

    private StatusWindow? _statusWindow;
    private CancellationTokenSource _pollDelayCts = new();

    public async Task StartAsync()
    {
        SettingsManager.Init();
        SettingsManager.SettingsReloaded += OnSettingsReloaded;
        ApplyTheme();
        BuildAccounts();
        Stock = new StockMonitor(this);

        Tray = new TrayIconManager(this);
        Tray.Update();

#if DEBUG
        McpDebug.McpDebugServer.Start(this);
#endif

        UpdateManager.Start(this);
        _ = PollLoopAsync();
        Stock.Start();
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
            session.LoginWindowNavigated += () => _ = OnLoginWindowNavigatedAsync(account);
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
        await NotifyUiAsync();
        try
        {
            var anyRefreshed = false;
            foreach (var account in Accounts)
                anyRefreshed |= await RefreshAccountAsync(account);

            // 全部账号都没刷到数据（如会话失效）时不更新时间，避免误导
            if (anyRefreshed)
                LastRefresh = DateTime.Now;
        }
        finally
        {
            Refreshing = false;
            await NotifyUiAsync();
        }
    }

    /// <summary>刷新单个账号，返回是否真正取到了数据。</summary>
    internal async Task<bool> RefreshAccountAsync(AccountState account)
    {
        try
        {
            if (account.SimulateExpired)
                throw new SessionExpiredException();

            await account.Session.EnsureInitializedAsync();

            // 每轮都重新拉服务列表：续费后到期时间、新增/删除的服务都要跟上
            var services = await account.Provider.ListServicesAsync();
            foreach (var service in services)
            {
                var existing = account.Services.FirstOrDefault(s =>
                    s.Service.Id == service.Id
                );
                if (existing == null)
                {
                    account.Services.Add(new ServiceState(service));
                    Log.ZLogInformation(
                        $"{account.Config.Name}: discovered service {service.Label}"
                    );
                }
                else if (!existing.Simulated)
                {
                    existing.Service = service;
                }
            }
            account.Services.RemoveAll(s =>
                !s.Simulated && services.All(x => x.Id != s.Service.Id)
            );

            // 服务列表到位后先刷一次面板，让各卡显示「等待数据…」
            account.LoggedIn = true;
            account.Error = null;
            await NotifyUiAsync();

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

                // 每拿到一台服务器就刷新面板与托盘，不必等整轮结束
                await NotifyUiAsync();
            }

            account.LoginNotified = false;
            account.LastPoll = DateTime.Now;
            Alerts.Evaluate(account);

            // 登录 cookie 是会话级的，重启即丢；抓取成功说明会话有效，趁机转成持久 cookie
            try
            {
                await account.Session.PersistSessionCookiesAsync();
            }
            catch (Exception ex)
            {
                Log.ZLogWarning(
                    $"{account.Config.Name}: persist session cookies failed: {ex.Message}"
                );
            }

            return true;
        }
        catch (SessionExpiredException)
        {
            account.LoggedIn = false;
            Log.ZLogWarning($"{account.Config.Name}: session expired");
            Alerts.NotifyLoginNeeded(account);
            await NotifyUiAsync();
            return false;
        }
        catch (Exception ex)
        {
            account.Error = ex.Message;
            Log.ZLogError($"{account.Config.Name}: refresh failed: {ex}");
            await NotifyUiAsync();
            return false;
        }
    }

    /// <summary>把当前内存状态同步到托盘图标与状态面板，并让出一帧以便立刻绘制。</summary>
    private async Task NotifyUiAsync()
    {
        Tray.Update();
        _statusWindow?.Rebuild();
        // 刷新循环在 UI 线程上跑：若不主动让出，Avalonia 要等下一次网络 await 才有机会布局绘制
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
    }

    private void NotifyUi()
    {
        Tray.Update();
        _statusWindow?.Rebuild();
    }

    public void TriggerRefresh()
    {
        _pollDelayCts.Cancel();
    }

    /// <summary>
    ///     用内置浏览器打开账号站点。站点只允许单处登录，外部浏览器登录会踢掉
    ///     监控会话，所以浏览和登录都走同一个 WebView2 窗口。
    /// </summary>
    public async Task OpenSiteAsync(AccountState account)
    {
        var title = account.LoggedIn
            ? account.Config.Name
            : $"登录 - {account.Config.Name}";
        await account.Session.ShowWindowAsync(account.Provider.LoginUrl, title);
    }

    public async Task OpenServiceAsync(AccountState account, VpsService service)
    {
        await account.Session.OpenNewWindowAsync(
            account.Provider.GetServiceUrl(service),
            $"{service.Label} - {account.Config.Name}"
        );
    }

    private async Task OnLoginWindowNavigatedAsync(AccountState account)
    {
        // 登录窗口里每次页面跳转都探测一次：登录成功后无需等窗口关闭即恢复状态
        if (account.LoggedIn || account.CheckingLogin)
            return;

        account.CheckingLogin = true;
        try
        {
            if (!await account.Provider.IsLoggedInAsync())
                return;
        }
        catch
        {
            return;
        }
        finally
        {
            account.CheckingLogin = false;
        }

        account.SimulateExpired = false;
        if (await RefreshAccountAsync(account))
        {
            LastRefresh = DateTime.Now;
            account.Session.HideLoginWindow();
            Alerts.ShowToast("VPS 流量监视", $"{account.Config.Name} 登录成功，已恢复监控");
        }
        NotifyUi();
    }

    private async Task OnLoginWindowClosedAsync(AccountState account)
    {
        // 用户关掉登录窗口后立即重试抓取
        if (await RefreshAccountAsync(account))
            LastRefresh = DateTime.Now;
        NotifyUi();
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
        BuildAccounts();
        ApplyTheme();
        TriggerRefresh();
        Stock.TriggerCheck();
        Tray.UpdateStockChecks();
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

    public void SetStockMonitorEnabled(bool enabled)
    {
        SettingsManager.Settings.StockMonitorEnabled = enabled;
        SettingsManager.Save();
        Tray.UpdateStockChecks();
        if (enabled)
            Stock.TriggerCheck();
        RebuildStatusWindow();
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

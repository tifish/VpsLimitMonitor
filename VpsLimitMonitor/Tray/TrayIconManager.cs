using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using JeekTools;
using VpsLimitMonitor.Core;
using VpsLimitMonitor.Update;
using VpsLimitMonitor.Settings;

namespace VpsLimitMonitor.Tray;

/// <summary>托盘图标：动态数字图标、tooltip、右键菜单。</summary>
public class TrayIconManager
{
    private readonly MonitorController _controller;
    private readonly TrayIcon _trayIcon;
    private readonly NativeMenuItem _themeSystemItem;
    private readonly NativeMenuItem _themeLightItem;
    private readonly NativeMenuItem _themeDarkItem;
    private readonly NativeMenuItem _update6HoursItem;
    private readonly NativeMenuItem _updateDailyItem;
    private readonly NativeMenuItem _updateWeeklyItem;
    private readonly NativeMenuItem _updateNoneItem;
    private readonly NativeMenuItem _storageDefaultItem;
    private readonly NativeMenuItem _storagePortableItem;
    private readonly NativeMenuItem _storageCustomItem;

    public TrayIconManager(MonitorController controller)
    {
        _controller = controller;

        _themeSystemItem = CreateThemeItem("跟随系统", "System");
        _themeLightItem = CreateThemeItem("亮色", "Light");
        _themeDarkItem = CreateThemeItem("暗色", "Dark");

        var themeMenu = new NativeMenuItem("主题")
        {
            Menu = [_themeSystemItem, _themeLightItem, _themeDarkItem],
        };

        var checkUpdateItem = new NativeMenuItem($"立即检查（当前 {UpdateManager.LocalVersionText}）");
        checkUpdateItem.Click += (_, _) => _ = _controller.CheckUpdateManuallyAsync();

        _update6HoursItem = CreateUpdateIntervalItem("每 6 小时检查", "Every6Hours");
        _updateDailyItem = CreateUpdateIntervalItem("每天检查", "Daily");
        _updateWeeklyItem = CreateUpdateIntervalItem("每周检查", "Weekly");
        _updateNoneItem = CreateUpdateIntervalItem("不自动检查", "None");

        var updateMenu = new NativeMenuItem("自动更新")
        {
            Menu =
            [
                checkUpdateItem,
                new NativeMenuItemSeparator(),
                _update6HoursItem,
                _updateDailyItem,
                _updateWeeklyItem,
                _updateNoneItem,
            ],
        };

        _storageDefaultItem = CreateStorageItem("默认（AppData）", StorageLocation.UserDirectory);
        _storagePortableItem = CreateStorageItem("便携（程序目录）", StorageLocation.ProgramDirectory);
        _storageCustomItem = CreateStorageItem("自定义目录…", StorageLocation.CustomDirectory);

        var storageMenu = new NativeMenuItem("配置存储")
        {
            Menu = [_storageDefaultItem, _storagePortableItem, _storageCustomItem],
        };

        var refreshItem = new NativeMenuItem("立即刷新");
        refreshItem.Click += (_, _) => _controller.TriggerRefresh();

        var statusItem = new NativeMenuItem("状态面板");
        statusItem.Click += (_, _) => _controller.ToggleStatusWindow();

        var exitItem = new NativeMenuItem("退出");
        exitItem.Click += (_, _) => _controller.Exit();

        var menu = new NativeMenu();
        menu.Items.Add(statusItem);
        menu.Items.Add(refreshItem);
        menu.Items.Add(new NativeMenuItemSeparator());

        foreach (var account in _controller.Accounts)
        {
            var loginItem = new NativeMenuItem($"登录 {account.Config.Name}");
            loginItem.Click += (_, _) => _ = _controller.ShowLoginAsync(account);
            menu.Items.Add(loginItem);
        }

        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(themeMenu);
        menu.Items.Add(storageMenu);
        menu.Items.Add(updateMenu);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            Menu = menu,
            ToolTipText = "VPS 流量监视器",
            IsVisible = true,
        };
        _trayIcon.Clicked += (_, _) => _controller.ToggleStatusWindow();

        TrayIcon.SetIcons(Application.Current!, [_trayIcon]);
        UpdateThemeChecks();
        UpdateUpdateIntervalChecks();
        UpdateStorageChecks();
    }

    private NativeMenuItem CreateStorageItem(string header, StorageLocation location)
    {
        var item = new NativeMenuItem(header) { ToggleType = MenuItemToggleType.Radio };
        item.Click += (_, _) => _ = _controller.SwitchStorageLocationAsync(location);
        return item;
    }

    public void UpdateStorageChecks()
    {
        var location = SettingsManager.Location;
        _storageDefaultItem.IsChecked = location == StorageLocation.UserDirectory;
        _storagePortableItem.IsChecked = location == StorageLocation.ProgramDirectory;
        _storageCustomItem.IsChecked = location == StorageLocation.CustomDirectory;
    }

    private NativeMenuItem CreateThemeItem(string header, string theme)
    {
        var item = new NativeMenuItem(header) { ToggleType = MenuItemToggleType.Radio };
        item.Click += (_, _) => _controller.SetTheme(theme);
        return item;
    }

    private NativeMenuItem CreateUpdateIntervalItem(string header, string interval)
    {
        var item = new NativeMenuItem(header) { ToggleType = MenuItemToggleType.Radio };
        item.Click += (_, _) => _controller.SetUpdateCheckInterval(interval);
        return item;
    }

    public void UpdateThemeChecks()
    {
        var theme = SettingsManager.Settings.Theme;
        _themeSystemItem.IsChecked = theme == "System";
        _themeLightItem.IsChecked = theme == "Light";
        _themeDarkItem.IsChecked = theme == "Dark";
    }

    public void UpdateUpdateIntervalChecks()
    {
        var interval = SettingsManager.Settings.UpdateCheckInterval;
        _update6HoursItem.IsChecked = interval == "Every6Hours";
        _updateDailyItem.IsChecked = interval == "Daily";
        _updateWeeklyItem.IsChecked = interval == "Weekly";
        _updateNoneItem.IsChecked = interval == "None";
    }

    public void Update()
    {
        var threshold = SettingsManager.Settings.AlertRemainingPercent;
        var loggedOut = _controller.Accounts.Where(a => !a.LoggedIn).ToList();
        var states = _controller
            .Accounts.SelectMany(a => a.Services)
            .Where(s => s.Traffic != null)
            .ToList();

        string text;
        System.Drawing.Color color;
        string tooltip;

        if (loggedOut.Count > 0)
        {
            text = "!";
            color = System.Drawing.Color.FromArgb(217, 48, 37); // 红
            tooltip = $"需要登录：{string.Join("、", loggedOut.Select(a => a.Config.Name))}";
        }
        else if (states.Count == 0)
        {
            text = "…";
            color = System.Drawing.Color.FromArgb(120, 120, 120); // 灰
            tooltip = "VPS 流量监视器（等待数据）";
        }
        else
        {
            var worst = states.MinBy(s => s.Traffic!.RemainingPercent)!;
            var pct = worst.Traffic!.RemainingPercent;
            text = Math.Round(pct).ToString("F0");
            color =
                pct < threshold ? System.Drawing.Color.FromArgb(217, 48, 37) // 红
                : pct < 25 ? System.Drawing.Color.FromArgb(232, 145, 0) // 橙
                : System.Drawing.Color.FromArgb(24, 128, 56); // 绿
            tooltip =
                $"最低 {worst.Service.Label}：剩 {worst.Traffic.RemainingGB:F0} GB"
                + $" / {worst.Traffic.TotalGB:F0} GB（{pct:F1}%）";
            if (_controller.LastRefresh is { } last)
                tooltip += $"\n更新于 {last:HH:mm}";
        }

        using var png = IconRenderer.RenderPng(text, color);
        _trayIcon.Icon = new WindowIcon(new Bitmap(png));
        _trayIcon.ToolTipText = tooltip.Length > 120 ? tooltip[..120] : tooltip;
    }
}

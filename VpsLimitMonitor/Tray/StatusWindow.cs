using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VpsLimitMonitor.Core;
using VpsLimitMonitor.Update;

namespace VpsLimitMonitor.Tray;

/// <summary>点击托盘图标弹出的状态面板，列出每台 VPS 的流量情况。</summary>
public class StatusWindow : Window
{
    private readonly MonitorController _controller;

    public StatusWindow(MonitorController controller)
    {
        _controller = controller;

        Title = "VPS 流量监视器";
        FontSize = 14;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Deactivated += (_, _) => Hide();
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    public void ShowNearTray()
    {
        Show();
        Activate();

        // 定位到主屏工作区右下角（托盘附近）
        var screen = Screens.Primary;
        if (screen == null)
            return;

        var wa = screen.WorkingArea;
        var scale = RenderScaling;
        var width = (int)(Bounds.Width * scale);
        var height = (int)(Bounds.Height * scale);
        Position = new PixelPoint(wa.Right - width - 12, wa.Bottom - height - 12);
    }

    public void Rebuild()
    {
        var root = new StackPanel { Margin = new Thickness(16), Spacing = 10 };

        foreach (var account in _controller.Accounts)
        {
            var header = new DockPanel();
            header.Children.Add(
                new TextBlock
                {
                    Text = account.Config.Name,
                    FontSize = 18,
                    FontWeight = FontWeight.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );

            if (!account.LoggedIn)
            {
                var loginButton = new Button
                {
                    Content = "重新登录",
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                loginButton.Click += (_, _) => _ = _controller.ShowLoginAsync(account);
                DockPanel.SetDock(loginButton, Dock.Right);
                header.Children.Insert(0, loginButton);
            }

            root.Children.Add(header);

            if (!account.LoggedIn)
            {
                root.Children.Add(
                    new TextBlock
                    {
                        Text = "登录已失效，请重新登录",
                        Foreground = Brushes.OrangeRed,
                    }
                );
            }
            else if (account.Error != null)
            {
                root.Children.Add(
                    new TextBlock
                    {
                        Text = $"刷新失败：{account.Error}",
                        Foreground = Brushes.OrangeRed,
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
            }
            else if (account.Services.Count == 0)
            {
                root.Children.Add(new TextBlock { Text = "正在获取服务列表…" });
            }

            foreach (var svc in account.Services)
                root.Children.Add(BuildServiceRow(svc));
        }

        if (Settings.SettingsManager.Settings.StockMonitorEnabled)
            root.Children.Add(BuildStockSection());

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        footer.Children.Add(
            new TextBlock
            {
                Text = UpdateManager.LocalVersionText,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.4,
            }
        );

        var refreshButton = new Button { Content = "立即刷新" };
        refreshButton.Click += (_, _) => _controller.TriggerRefresh();
        footer.Children.Add(refreshButton);

        if (_controller.LastRefresh is { } last)
        {
            footer.Children.Insert(
                1,
                new TextBlock
                {
                    Text = _controller.Refreshing ? "刷新中…" : $"更新于 {last:HH:mm:ss}",
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.6,
                }
            );
        }

        root.Children.Add(footer);
        Content = new ScrollViewer { Content = root, MaxHeight = 640 };
    }

    private Control BuildStockSection()
    {
        var panel = new StackPanel { Spacing = 3 };
        var stock = _controller.Stock;

        var title = "库存监控";
        if (stock.Simulated)
            title += "（模拟数据）";
        panel.Children.Add(
            new TextBlock
            {
                Text = title,
                FontSize = 18,
                FontWeight = FontWeight.Bold,
            }
        );

        if (stock.Error != null)
        {
            panel.Children.Add(
                new TextBlock
                {
                    Text = $"检查失败：{stock.Error}",
                    Foreground = Brushes.OrangeRed,
                    TextWrapping = TextWrapping.Wrap,
                }
            );
        }
        else if (stock.Plans.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "等待检查…", Opacity = 0.6 });
        }
        else
        {
            foreach (var plan in stock.Plans)
            {
                var line = new TextBlock
                {
                    Text = $"{plan.Name}：{(plan.InStock ? "有货！" : "售罄")}",
                    Opacity = plan.InStock ? 1 : 0.6,
                };
                if (plan.InStock)
                {
                    line.Foreground = Brushes.Green;
                    line.FontWeight = FontWeight.Bold;
                }
                panel.Children.Add(line);
            }
        }

        if (stock.LastCheck is { } check)
            panel.Children.Add(
                new TextBlock
                {
                    Text = $"检查于 {check:HH:mm:ss}",
                    FontSize = 12,
                    Opacity = 0.6,
                }
            );

        return panel;
    }

    private Control BuildServiceRow(ServiceState svc)
    {
        var panel = new StackPanel { Spacing = 3 };

        var title = $"{svc.Service.Label}";
        if (svc.Service.Ip != null)
            title += $"  {svc.Service.Ip}";
        if (svc.Traffic is { IsOnline: false })
            title += "（关机）";
        if (svc.Simulated)
            title += "（模拟数据）";

        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });

        if (svc.Traffic is { } traffic)
        {
            var usedPct = traffic.TotalGB > 0 ? traffic.UsedGB / traffic.TotalGB * 100 : 0;
            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = Math.Clamp(usedPct, 0, 100),
                Height = 8,
            };
            if (
                traffic.RemainingPercent
                < Settings.SettingsManager.Settings.AlertRemainingPercent
            )
                bar.Foreground = Brushes.OrangeRed;
            panel.Children.Add(bar);
            var line =
                $"已用 {traffic.UsedGB:F1} / {traffic.TotalGB:F0} GB"
                + $" · 剩 {traffic.RemainingGB:F1} GB（{traffic.RemainingPercent:F1}%）";
            panel.Children.Add(new TextBlock { Text = line });

            if (traffic.ResetNotice is { } reset)
                panel.Children.Add(
                    new TextBlock
                    {
                        Text = $"下次重置：{reset}",
                        FontSize = 12,
                        Opacity = 0.6,
                    }
                );
        }
        else if (svc.Error != null)
        {
            panel.Children.Add(
                new TextBlock
                {
                    Text = $"获取失败：{svc.Error}",
                    Foreground = Brushes.OrangeRed,
                    TextWrapping = TextWrapping.Wrap,
                }
            );
        }
        else
        {
            panel.Children.Add(new TextBlock { Text = "等待数据…", Opacity = 0.6 });
        }

        if (svc.Service.DueDate is { } due)
        {
            var days = due.DayNumber - DateOnly.FromDateTime(DateTime.Now).DayNumber;
            var text = days switch
            {
                < 0 => $"到期：{due:yyyy-MM-dd}（已过期 {-days} 天）",
                0 => $"到期：{due:yyyy-MM-dd}（今天）",
                _ => $"到期：{due:yyyy-MM-dd}（剩 {days} 天）",
            };
            var line = new TextBlock { Text = text, FontSize = 12, Opacity = 0.6 };
            if (days <= AlertManager.RenewalReminderDays)
            {
                line.Foreground = Brushes.OrangeRed;
                line.Opacity = 1;
                line.FontWeight = FontWeight.SemiBold;
            }
            panel.Children.Add(line);
        }

        return panel;
    }
}

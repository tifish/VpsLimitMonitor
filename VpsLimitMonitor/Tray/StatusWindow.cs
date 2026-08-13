using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        Icon = App.AppIcon;
        FontSize = 14;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Deactivated += (_, _) => Hide();
        // 数据刷新会改变面板高度，重新贴底定位，避免底部伸到任务栏下面
        SizeChanged += (_, _) =>
        {
            if (IsVisible)
                PositionNearTray();
        };
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
        PositionNearTray();
    }

    /// <summary>定位到主屏工作区右下角（托盘附近）。</summary>
    private void PositionNearTray()
    {
        var screen = Screens.Primary;
        if (screen == null)
            return;

        var wa = screen.WorkingArea;
        var scale = RenderScaling;
        var width = (int)(Bounds.Width * scale);
        var height = (int)(Bounds.Height * scale);
        Position = new PixelPoint(wa.Right - width - 12, wa.Bottom - height - 12);
    }

    /// <summary>每台服务器卡片占用的固定宽度，多列排布的列宽。</summary>
    private const double ServiceCardWidth = 400;

    public void Rebuild()
    {
        // 尺寸上限跟随屏幕工作区，避免内容被截断出现滚动条
        var maxHeight = double.PositiveInfinity;
        if (Screens.Primary is { } s)
        {
            maxHeight = (s.WorkingArea.Height - 24) / s.Scaling;
            MaxHeight = maxHeight;
            MaxWidth = (s.WorkingArea.Width - 24) / s.Scaling;
        }

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 10 };

        // 顶栏：刷新按钮、刷新时间与版本号
        var topBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var refreshButton = new Button { Content = "立即刷新" };
        refreshButton.Click += (_, _) => _controller.TriggerRefresh();
        topBar.Children.Add(refreshButton);
        if (_controller.Refreshing)
        {
            topBar.Children.Add(
                new TextBlock
                {
                    Text = "刷新中…",
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.6,
                }
            );
        }
        else if (_controller.LastRefresh is { } last)
        {
            topBar.Children.Add(
                new TextBlock
                {
                    Text = $"更新于 {last:HH:mm:ss}",
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.6,
                }
            );
        }
        topBar.Children.Add(
            new TextBlock
            {
                Text = UpdateManager.LocalVersionText,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.4,
            }
        );
        root.Children.Add(topBar);

        var stockEnabled = Settings.SettingsManager.Settings.StockMonitorEnabled;
        // 服务器区实际宽度（列数决定），库存行以此为宽度上限折行，避免反过来撑宽窗口
        var contentWidth = ServiceCardWidth;

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
                loginButton.Click += (_, _) => _ = _controller.OpenSiteAsync(account);
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

            if (account.Services.Count == 0)
                continue;

            // 多列排布：量出上方内容高度（给库存行预留一行），剩余空间即每列高度，放不下时向右换列
            root.Measure(Size.Infinity);
            var stockReserve = stockEnabled ? 60 : 0;
            var wrap = new WrapPanel
            {
                Orientation = Orientation.Vertical,
                ItemWidth = ServiceCardWidth,
                MaxHeight = Math.Max(
                    160,
                    maxHeight - root.DesiredSize.Height - stockReserve - 16
                ),
            };
            foreach (var svc in account.Services)
            {
                var card = BuildServiceRow(account, svc);
                card.Margin = new Thickness(0, 0, 16, 6);
                wrap.Children.Add(card);
            }
            root.Children.Add(wrap);

            wrap.Measure(Size.Infinity);
            contentWidth = Math.Max(contentWidth, wrap.DesiredSize.Width);
        }

        if (stockEnabled)
        {
            var stockSection = BuildStockSection();
            stockSection.MaxWidth = contentWidth;
            stockSection.HorizontalAlignment = HorizontalAlignment.Left;
            root.Children.Insert(1, stockSection);
        }

        Content = new ScrollViewer
        {
            Content = root,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    /// <summary>库存区：标题行带检查时间，套餐按固定格宽（半张卡片）对齐排布，窄窗口两列、宽窗口一行。</summary>
    private Control BuildStockSection()
    {
        var stock = _controller.Stock;
        var panel = new StackPanel { Spacing = 3 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var title = "库存监控";
        if (stock.Simulated)
            title += "（模拟数据）";
        header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.Bold });
        if (stock.LastCheck is { } check)
            header.Children.Add(
                new TextBlock
                {
                    Text = $"检查于 {check:HH:mm:ss}",
                    FontSize = 12,
                    Opacity = 0.6,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
        panel.Children.Add(header);

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
            var wrap = new WrapPanel { ItemWidth = ServiceCardWidth / 2 };
            foreach (var plan in stock.Plans)
            {
                var item = new TextBlock
                {
                    Text = $"{plan.Name} {(plan.InStock ? "有货！" : "售罄")}",
                    Opacity = plan.InStock ? 1 : 0.6,
                };
                if (plan.InStock)
                {
                    item.Foreground = Brushes.Green;
                    item.FontWeight = FontWeight.Bold;
                }
                wrap.Children.Add(item);
            }
            panel.Children.Add(wrap);
        }

        return panel;
    }

    private Control BuildServiceRow(AccountState account, ServiceState svc)
    {
        var panel = new StackPanel { Spacing = 3 };

        var title = $"{svc.Service.Label}";
        if (svc.Service.Ip != null)
            title += $"  {svc.Service.Ip}";
        if (svc.Traffic is { IsOnline: false })
            title += "（关机）";
        if (svc.Simulated)
            title += "（模拟数据）";

        var titleRow = new DockPanel();
        var alert = false;
        if (svc.Traffic is { } t)
        {
            alert =
                t.RemainingPercent
                < Settings.SettingsManager.Settings.AlertRemainingPercent;
            var pctLabel = new TextBlock
            {
                Text = $"{t.UsedPercent:F1}%",
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (alert)
                pctLabel.Foreground = Brushes.OrangeRed;
            DockPanel.SetDock(pctLabel, Dock.Right);
            titleRow.Children.Add(pctLabel);
        }
        titleRow.Children.Add(
            new TextBlock
            {
                Text = title,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        panel.Children.Add(titleRow);

        if (svc.Traffic is { } traffic)
        {
            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = Math.Clamp(traffic.UsedPercent, 0, 100),
                Height = 8,
            };
            if (alert)
                bar.Foreground = Brushes.OrangeRed;

            panel.Children.Add(bar);
            panel.Children.Add(
                new TextBlock
                {
                    Text =
                        $"已用 {traffic.UsedGB:F1} / {traffic.TotalGB:F0} GB"
                        + $" · 剩 {traffic.RemainingGB:F1} GB",
                }
            );

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

        var button = new Button
        {
            Content = panel,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsTabStop = false,
        };
        button.Click += (_, _) => _ = _controller.OpenServiceAsync(account, svc.Service);
        return button;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using VpsLimitMonitor.Core;
using VpsLimitMonitor.Update;

namespace VpsLimitMonitor.Tray;

/// <summary>点击托盘图标弹出的状态面板，列出每台 VPS 的流量情况。</summary>
public class StatusWindow : Window
{
    private readonly MonitorController _controller;
    private bool _preserveOnNextDeactivate;

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

        Deactivated += (_, _) =>
        {
            if (_preserveOnNextDeactivate)
            {
                _preserveOnNextDeactivate = false;
                return;
            }

            Hide();
        };
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

    public void PreserveOnNextDeactivate() => _preserveOnNextDeactivate = true;

    public void CancelPreserveOnNextDeactivate() => _preserveOnNextDeactivate = false;

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
    private const double ServiceCardWidth = 320;
    private const double TrafficValueWidth = 112;
    private const double UsagePercentWidth = 48;

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

        var root = new StackPanel { Margin = new Thickness(12), Spacing = 6 };

        // 顶栏：刷新按钮、刷新时间、服务器总数与版本号
        var topBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var refreshButton = new Button
        {
            Content = "立即刷新",
            Padding = new Thickness(8, 4),
            FontSize = 13,
        };
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
                Text = _controller.ServerCountText,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.6,
            }
        );
        topBar.Children.Add(
            new TextBlock
            {
                Text = UpdateManager.LocalVersionText,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.4,
            }
        );
        root.Children.Add(topBar);

        const double columnSpacing = 8;
        var columnWidth = ServiceCardWidth + columnSpacing;

        root.Measure(Size.Infinity);
        var accountColumnsMaxHeight = Math.Max(160, maxHeight - root.DesiredSize.Height - 24);
        var accountsPanel = new StackPanel
        {
            Name = "AccountColumns",
            Orientation = Orientation.Horizontal,
        };

        for (var accountIndex = 0; accountIndex < _controller.Accounts.Count; accountIndex++)
        {
            var account = _controller.Accounts[accountIndex];
            if (accountIndex > 0)
            {
                accountsPanel.Children.Add(new Border
                {
                    Name = $"AccountSeparator{accountIndex}",
                    Width = 1,
                    Margin = new Thickness(8, 0, 24, 0),
                    Background = Brushes.Gray,
                    Opacity = 0.5,
                    VerticalAlignment = VerticalAlignment.Stretch,
                });
            }

            var accountGroup = new StackPanel
            {
                Name = $"AccountGroup{accountIndex}",
                Spacing = 4,
            };
            var serviceColumns = new WrapPanel
            {
                Name = $"AccountColumn{accountIndex}",
                Orientation = Orientation.Vertical,
                ItemWidth = columnWidth,
            };
            var header = new DockPanel
            {
                Name = $"AccountHeader{accountIndex}",
                MinWidth = ServiceCardWidth,
                MinHeight = 28,
                Margin = new Thickness(0, 0, columnSpacing, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            header.Children.Add(
                new TextBlock
                {
                    Text = account.TitleText,
                    FontSize = 16,
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

            accountGroup.Children.Add(header);
            if (BuildStockSection(account) is { } stockSection)
                accountGroup.Children.Add(stockSection);

            accountGroup.Measure(Size.Infinity);
            serviceColumns.MaxHeight = Math.Max(
                100,
                accountColumnsMaxHeight - accountGroup.DesiredSize.Height - accountGroup.Spacing
            );

            if (!account.LoggedIn)
            {
                serviceColumns.Children.Add(new TextBlock
                {
                    Width = ServiceCardWidth,
                    Margin = new Thickness(0, 0, columnSpacing, 4),
                    Text = "登录已失效，请重新登录",
                    Foreground = Brushes.OrangeRed,
                });
            }
            else if (account.Error != null)
            {
                serviceColumns.Children.Add(new TextBlock
                {
                    Width = ServiceCardWidth,
                    Margin = new Thickness(0, 0, columnSpacing, 4),
                    Text = $"刷新失败：{account.Error}",
                    Foreground = Brushes.OrangeRed,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            else if (account.Services.Count == 0)
            {
                serviceColumns.Children.Add(new TextBlock
                {
                    Width = ServiceCardWidth,
                    Margin = new Thickness(0, 0, columnSpacing, 4),
                    Text = "正在获取服务列表…",
                });
            }

            foreach (var svc in account.Services)
            {
                var card = BuildServiceRow(account, svc);
                card.Width = ServiceCardWidth;
                card.Margin = new Thickness(0, 0, columnSpacing, 4);
                serviceColumns.Children.Add(card);
            }

            accountGroup.Children.Add(serviceColumns);
            accountsPanel.Children.Add(accountGroup);
        }

        root.Children.Add(accountsPanel);

        Content = new ScrollViewer
        {
            Content = root,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    public object GetLayoutSnapshot()
    {
        var cards = this
            .GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.Classes.Contains("service-card"))
            .ToArray();
        var trafficRows = this
            .GetVisualDescendants()
            .OfType<Grid>()
            .Where(grid => grid.Classes.Contains("traffic-row"))
            .ToArray();
        var serviceTitles = this
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(text => text.Classes.Contains("service-title"))
            .ToArray();

        return new
        {
            visible = IsVisible,
            width = Bounds.Width,
            height = Bounds.Height,
            configuredCardWidth = ServiceCardWidth,
            serviceCardCount = cards.Length,
            cardBounds = cards
                .Take(12)
                .Select(card => new
                {
                    width = card.Bounds.Width,
                    height = card.Bounds.Height,
                })
                .ToArray(),
            trafficLayout = trafficRows
                .Take(12)
                .Select(row =>
                {
                    var bar = row.Children
                        .OfType<ProgressBar>()
                        .FirstOrDefault(control => control.Classes.Contains("traffic-bar"));
                    var summary = row.Children
                        .OfType<TextBlock>()
                        .FirstOrDefault(control =>
                            control.Classes.Contains("traffic-percent")
                        );
                    var barRight = bar == null ? 0 : bar.Bounds.X + bar.Bounds.Width;
                    var summaryLeft =
                        summary == null ? row.Bounds.Width : summary.Bounds.X;
                    return new
                    {
                        width = row.Bounds.Width,
                        barWidth = bar?.Bounds.Width ?? 0,
                        percentWidth = summary?.Bounds.Width ?? 0,
                        gap = summaryLeft - barRight,
                    };
                })
                .ToArray(),
            titleLayout = serviceTitles
                .Take(12)
                .Select(title => new
                {
                    text = title.Text,
                    trimming = title.TextTrimming.ToString(),
                    wrapping = title.TextWrapping.ToString(),
                    height = title.Bounds.Height,
                })
                .ToArray(),
        };
    }

    /// <summary>供应商库存目标与独立开关，显示在对应供应商标题下方。</summary>
    private Control? BuildStockSection(AccountState account)
    {
        var stock = _controller.Stock;
        var source = stock.FindSource(account);
        if (source == null)
            return null;

        var enabled = stock.IsEnabled(source);
        var panel = new StackPanel
        {
            Name = $"StockMonitor{source.ProviderName}",
            MinWidth = ServiceCardWidth,
            Margin = new Thickness(0, 0, 8, 0),
            Spacing = 1,
        };
        var header = new DockPanel();
        var toggle = new CheckBox
        {
            Name = $"StockToggle{source.ProviderName}",
            Content = $"监控 {source.TargetName} 库存",
            IsChecked = enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        toggle.Click += (_, _) =>
            _controller.SetStockMonitorEnabled(source.ProviderName, toggle.IsChecked == true);
        header.Children.Add(toggle);

        if (source.LastCheck is { } check)
        {
            var checkedAt = new TextBlock
            {
                Text = $"{check:HH:mm:ss}",
                FontSize = 12,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(checkedAt, Dock.Right);
            header.Children.Insert(0, checkedAt);
        }
        panel.Children.Add(header);

        if (!enabled)
        {
            panel.Children.Add(new TextBlock { Text = "已关闭", Opacity = 0.6 });
        }
        else if (!account.LoggedIn)
        {
            panel.Children.Add(
                new TextBlock
                {
                    Name = $"StockStatus{source.ProviderName}",
                    Text = "登录已失效",
                    Foreground = Brushes.OrangeRed,
                }
            );
        }
        else if (source.Checking)
        {
            panel.Children.Add(new TextBlock { Text = "检查中…", Opacity = 0.6 });
        }
        else if (source.Error != null)
        {
            panel.Children.Add(
                new TextBlock
                {
                    Text = $"检查失败：{source.Error}",
                    Foreground = Brushes.OrangeRed,
                    TextWrapping = TextWrapping.Wrap,
                }
            );
        }
        else if (source.Plans.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "等待检查…", Opacity = 0.6 });
        }
        else
        {
            foreach (var plan in source.Plans)
            {
                var suffix = source.Simulated ? "（模拟数据）" : "";
                var item = new TextBlock
                {
                    Text = $"{plan.Name}：{(plan.InStock ? "有货" : "售罄")}{suffix}",
                    Opacity = plan.InStock ? 1 : 0.6,
                    TextDecorations = TextDecorations.Underline,
                    TextWrapping = TextWrapping.Wrap,
                };
                if (plan.InStock)
                {
                    item.Foreground = Brushes.Green;
                    item.FontWeight = FontWeight.Bold;
                }
                var link = new Button
                {
                    Name = $"StockLink{source.ProviderName}",
                    Content = item,
                    Background = Brushes.Transparent,
                    BorderBrush = Brushes.Transparent,
                    Padding = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    IsTabStop = false,
                };
                link.Click += (_, _) =>
                    _ = _controller.OpenStockPageAsync(source.ProviderName);
                panel.Children.Add(link);
            }
        }

        return panel;
    }

    private Control BuildServiceRow(AccountState account, ServiceState svc)
    {
        var panel = new StackPanel { Spacing = 1 };

        var title = svc.Service.Ip ?? "无 IP";
        if (svc.Traffic is { IsOnline: false })
            title += "（关机）";
        if (svc.Simulated)
            title += "（模拟数据）";

        var titleRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,112"),
            MinHeight = 20,
        };
        var alert = false;
        if (svc.Traffic is { } t)
        {
            alert =
                t.RemainingPercent
                < Settings.SettingsManager.Settings.AlertRemainingPercent;
            var trafficValue = new TextBlock
            {
                Classes = { "traffic-value" },
                Text = $"{t.UsedGB:F1} / {t.TotalGB:F0} GB",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextAlignment = TextAlignment.Right,
                Width = TrafficValueWidth,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(trafficValue, 1);
            titleRow.Children.Add(trafficValue);
        }
        titleRow.Children.Insert(
            0,
            new TextBlock
            {
                Classes = { "service-title" },
                Text = title,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.None,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        panel.Children.Add(titleRow);

        if (svc.Traffic is { } traffic)
        {
            var trafficRow = new Grid
            {
                Classes = { "traffic-row" },
                ColumnDefinitions = new ColumnDefinitions("*,48"),
                MinHeight = 18,
            };
            var bar = new ProgressBar
            {
                Classes = { "traffic-bar" },
                Minimum = 0,
                Maximum = 100,
                Value = Math.Clamp(traffic.UsedPercent, 0, 100),
                Height = 6,
                MinWidth = 48,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 8, 0),
            };
            if (alert)
                bar.Foreground = Brushes.OrangeRed;
            Grid.SetColumn(bar, 0);
            trafficRow.Children.Add(bar);
            var pctLabel = new TextBlock
            {
                Classes = { "traffic-percent" },
                Text = $"{traffic.UsedPercent:F1}%",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextAlignment = TextAlignment.Right,
                Width = UsagePercentWidth,
            };
            if (alert)
                pctLabel.Foreground = Brushes.OrangeRed;
            Grid.SetColumn(pctLabel, 1);
            trafficRow.Children.Add(pctLabel);
            panel.Children.Add(trafficRow);
        }
        else if (svc.Error != null)
        {
            panel.Children.Add(
                new TextBlock
                {
                    Text = $"获取失败：{svc.Error}",
                    FontSize = 12,
                    Foreground = Brushes.OrangeRed,
                    TextWrapping = TextWrapping.Wrap,
                }
            );
        }
        else
        {
            panel.Children.Add(
                new TextBlock
                {
                    Text = "等待数据…",
                    FontSize = 12,
                    Opacity = 0.6,
                }
            );
        }

        var auxiliaryText = new List<string>();
        if (svc.Traffic?.ResetNotice is { } reset)
            auxiliaryText.Add($"重置 {reset}");

        TextBlock? dueLine = null;
        var dueIsAlert = false;
        if (svc.Service.DueDate is { } due)
        {
            var days = due.DayNumber - DateOnly.FromDateTime(DateTime.Now).DayNumber;
            var text = days switch
            {
                < 0 => $"到期 {due:yyyy-MM-dd}（已过期 {-days} 天）",
                0 => $"到期 {due:yyyy-MM-dd}（今天）",
                _ => $"到期 {due:yyyy-MM-dd}（剩 {days} 天）",
            };
            dueLine = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Opacity = 0.6,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (days <= AlertManager.RenewalReminderDays)
            {
                dueIsAlert = true;
                dueLine.Foreground = Brushes.OrangeRed;
                dueLine.Opacity = 1;
                dueLine.FontWeight = FontWeight.SemiBold;
            }
        }

        if (dueLine != null && auxiliaryText.Count > 0)
        {
            auxiliaryText.Add(dueLine.Text ?? "");
            dueLine = null;
        }

        if (auxiliaryText.Count > 0)
        {
            var auxiliaryLine = new TextBlock
            {
                Text = string.Join("  |  ", auxiliaryText),
                FontSize = 11,
                Opacity = dueIsAlert ? 1 : 0.6,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (dueIsAlert)
            {
                auxiliaryLine.Foreground = Brushes.OrangeRed;
                auxiliaryLine.FontWeight = FontWeight.SemiBold;
            }
            panel.Children.Add(auxiliaryLine);
        }
        else if (dueLine != null)
        {
            panel.Children.Add(dueLine);
        }

        var button = new Button
        {
            Classes = { "service-card" },
            Content = panel,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(4, 2),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsTabStop = false,
        };
        button.Click += (_, _) => _ = _controller.OpenServiceAsync(account, svc.Service);
        return button;
    }
}

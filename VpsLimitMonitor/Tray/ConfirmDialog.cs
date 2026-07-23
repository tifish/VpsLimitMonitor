using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace VpsLimitMonitor.Tray;

/// <summary>纯托盘应用无主窗口，用独立置顶小窗做确认对话框。</summary>
public static class ConfirmDialog
{
    /// <summary>显示消息与一排按钮，返回被点击按钮的下标；关闭窗口返回 null。</summary>
    public static Task<int?> ShowAsync(string title, string message, params string[] buttons)
    {
        var tcs = new TaskCompletionSource<int?>();

        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Topmost = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var button = new Button { Content = buttons[i], MinWidth = 80 };
            button.Click += (_, _) =>
            {
                tcs.TrySetResult(index);
                window.Close();
            };
            buttonPanel.Children.Add(button);
        }

        window.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                buttonPanel,
            },
        };

        window.Closed += (_, _) => tcs.TrySetResult(null);
        window.Show();
        window.Activate();

        return tcs.Task;
    }
}

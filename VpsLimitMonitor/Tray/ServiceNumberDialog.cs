using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace VpsLimitMonitor.Tray;

public sealed record ServiceNumberDialogResult(bool Confirmed, int? Number);

public static class ServiceNumberDialog
{
    public static Task<ServiceNumberDialogResult?> ShowAsync(string title, int? currentNumber)
    {
        var tcs = new TaskCompletionSource<ServiceNumberDialogResult?>();
        var window = new Window
        {
            Title = title,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Topmost = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        var input = new TextBox
        {
            Text = currentNumber?.ToString("D2") ?? "",
            MaxLength = 2,
            PlaceholderText = "01 - 99",
        };
        var error = new TextBlock
        {
            Foreground = Brushes.OrangeRed,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        void Complete(ServiceNumberDialogResult result)
        {
            tcs.TrySetResult(result);
            window.Close();
        }

        var save = new Button { Content = "保存", MinWidth = 80 };
        save.Click += (_, _) =>
        {
            if (!int.TryParse(input.Text, out var number) || number is < 1 or > 99)
            {
                error.Text = "请输入 01 到 99 之间的序号。";
                error.IsVisible = true;
                return;
            }

            Complete(new ServiceNumberDialogResult(true, number));
        };
        var clear = new Button { Content = "清除", MinWidth = 80 };
        clear.Click += (_, _) => Complete(new ServiceNumberDialogResult(true, null));
        var cancel = new Button { Content = "取消", MinWidth = 80 };
        cancel.Click += (_, _) => Complete(new ServiceNumberDialogResult(false, null));
        buttons.Children.Add(save);
        buttons.Children.Add(clear);
        buttons.Children.Add(cancel);

        window.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "服务器序号", FontWeight = FontWeight.SemiBold },
                input,
                error,
                buttons,
            },
        };
        window.Closed += (_, _) => tcs.TrySetResult(null);
        window.Show();
        window.Activate();
        input.Focus();
        input.SelectAll();

        return tcs.Task;
    }
}

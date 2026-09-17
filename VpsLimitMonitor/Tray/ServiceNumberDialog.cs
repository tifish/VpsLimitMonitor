using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VpsLimitMonitor.Settings;

namespace VpsLimitMonitor.Tray;

public sealed record ServiceNumberDialogResult(bool Confirmed, string? Number);

public static class ServiceNumberDialog
{
    public static Task<ServiceNumberDialogResult?> ShowAsync(string title, string? currentNumber)
    {
        var tcs = new TaskCompletionSource<ServiceNumberDialogResult?>();
        var window = new Window
        {
            Name = "ServiceIdDialog",
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
            Name = "ServiceIdInput",
            Text = currentNumber ?? "",
            PlaceholderText = "例如 JP 01、香港备用、001",
        };
        var error = new TextBlock
        {
            Name = "ServiceIdError",
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

        var save = new Button { Name = "SaveServiceId", Content = "保存", MinWidth = 80 };
        save.Click += (_, _) =>
        {
            string? number;
            try
            {
                number = ServerNumberStore.NormalizeId(input.Text);
                if (number == null)
                    throw new ArgumentException("请输入服务器 ID，或点击“清除”。");
            }
            catch (ArgumentException ex)
            {
                error.Text = ex.Message;
                error.IsVisible = true;
                return;
            }

            Complete(new ServiceNumberDialogResult(true, number));
        };
        var clear = new Button { Name = "ClearServiceId", Content = "清除", MinWidth = 80 };
        clear.Click += (_, _) => Complete(new ServiceNumberDialogResult(true, null));
        var cancel = new Button { Name = "CancelServiceId", Content = "取消", MinWidth = 80 };
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
                new TextBlock { Text = "服务器 ID", FontWeight = FontWeight.SemiBold },
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

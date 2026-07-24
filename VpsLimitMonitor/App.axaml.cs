using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using VpsLimitMonitor.Core;

namespace VpsLimitMonitor;

public partial class App : Application
{
    public static MonitorController Controller { get; private set; } = null!;

    private static WindowIcon? _appIcon;

    /// <summary>应用图标，供各窗口标题栏/任务栏使用。</summary>
    public static WindowIcon AppIcon =>
        _appIcon ??= new WindowIcon(AssetLoader.Open(new Uri("avares://VpsLimitMonitor/Assets/app.ico")));

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Controller = new MonitorController();
            _ = Controller.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

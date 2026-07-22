using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VpsLimitMonitor.Core;

namespace VpsLimitMonitor;

public partial class App : Application
{
    public static MonitorController Controller { get; private set; } = null!;

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

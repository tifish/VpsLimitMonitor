using Avalonia;
using JeekTools;

namespace VpsLimitMonitor;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var singleInstance = new SingleInstance("VpsLimitMonitor");
        if (singleInstance.IsRunning)
        {
            singleInstance.SendMessage("Show");
            return;
        }

        singleInstance.StartIPCServer(_ => App.Controller?.ShowStatusWindowFromIpc());

        LogManager.EnableLogging();
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
#if DEBUG
            McpDebug.McpDebugServer.Stop();
#endif
            LogManager.Shutdown();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
    }
}

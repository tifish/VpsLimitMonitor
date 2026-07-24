using Avalonia;
using Avalonia.Media;
using JeekTools;

namespace VpsLimitMonitor;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
#if !DEBUG
        // Debug 版不限制单实例，方便多个 worktree 并行开发调试
        using var singleInstance = new SingleInstance("VpsLimitMonitor");
        if (singleInstance.IsRunning)
        {
            singleInstance.SendMessage("Show");
            return;
        }

        singleInstance.StartIPCServer(_ => App.Controller?.ShowStatusWindowFromIpc());
#endif

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
        return AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = "Microsoft YaHei UI" })
            .LogToTrace();
    }
}

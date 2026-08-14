using VpsLimitMonitor.Settings;
using VpsLimitMonitor.Web;

namespace VpsLimitMonitor.Providers;

public static class ProviderFactory
{
    public static IVpsProvider Create(AccountConfig config, WebSession session)
    {
        return config.Type switch
        {
            "WhmcsCubeCloud" => new WhmcsCubeCloudProvider(session),
            "IdcSystemKvm" => new IdcSystemKvmProvider(session),
            _ => throw new NotSupportedException($"Unknown provider type: {config.Type}"),
        };
    }
}

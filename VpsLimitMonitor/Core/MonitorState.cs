using VpsLimitMonitor.Providers;
using VpsLimitMonitor.Settings;
using VpsLimitMonitor.Web;

namespace VpsLimitMonitor.Core;

public class ServiceState(VpsService service)
{
    public VpsService Service { get; set; } = service;
    public TrafficInfo? Traffic { get; set; }
    public DateTime? LastUpdate { get; set; }
    public string? Error { get; set; }
    public bool Alerted { get; set; }

    /// <summary>true 表示当前数据是 MCP 调试接口注入的模拟值，下次轮询前不被覆盖。</summary>
    public bool Simulated { get; set; }
}

public class AccountState(AccountConfig config, WebSession session, IVpsProvider provider)
{
    public AccountConfig Config { get; } = config;
    public WebSession Session { get; } = session;
    public IVpsProvider Provider { get; } = provider;
    public List<ServiceState> Services { get; } = [];
    public bool LoggedIn { get; set; } = true;
    public bool LoginNotified { get; set; }
    public DateTime? LastPoll { get; set; }
    public string? Error { get; set; }
}

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

    /// <summary>最近一次续费提醒的日期，保证临期后每天只提醒一次。</summary>
    public DateOnly? RenewalRemindedOn { get; set; }

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

    /// <summary>true 表示 MCP 调试接口模拟会话失效，刷新时直接按失效处理，直到 clear_simulation。</summary>
    public bool SimulateExpired { get; set; }

    /// <summary>登录窗口导航触发的登录探测正在进行，避免重入。</summary>
    public bool CheckingLogin { get; set; }
    public DateTime? LastPoll { get; set; }
    public string? Error { get; set; }
}

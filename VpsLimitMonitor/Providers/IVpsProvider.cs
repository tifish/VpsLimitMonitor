namespace VpsLimitMonitor.Providers;

public record VpsService(string Id, string Name, string Label, string? Ip, DateOnly? DueDate);

public record TrafficInfo(double UsedGB, double TotalGB, string? ResetNotice, bool IsOnline)
{
    public double UsedPercent => TotalGB > 0 ? Math.Clamp(UsedGB / TotalGB * 100, 0, 100) : 0;
    public double RemainingGB => Math.Max(0, TotalGB - UsedGB);
    public double RemainingPercent => TotalGB > 0 ? RemainingGB / TotalGB * 100 : 0;
}

/// <summary>会话已失效（需要重新登录）。</summary>
public class SessionExpiredException : Exception
{
    public SessionExpiredException()
        : base("Session expired, login required") { }
}

/// <summary>
///     VPS 站点适配器。核心层只依赖这个接口；
///     新站点只需实现一个适配器并在 ProviderFactory 注册。
/// </summary>
public interface IVpsProvider
{
    string TypeName { get; }

    /// <summary>会话失效时登录窗口打开的地址。</summary>
    string LoginUrl { get; }

    Task<bool> IsLoggedInAsync();

    /// <summary>发现该账号下的所有 VPS 服务。</summary>
    Task<IReadOnlyList<VpsService>> ListServicesAsync();

    /// <summary>抓取单台 VPS 的流量信息。会话失效时抛 SessionExpiredException。</summary>
    Task<TrafficInfo> GetTrafficAsync(VpsService service);
}

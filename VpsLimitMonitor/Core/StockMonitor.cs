using JeekTools;
using Microsoft.Extensions.Logging;
using VpsLimitMonitor.Settings;
using ZLogger;

namespace VpsLimitMonitor.Core;

/// <summary>单个套餐的库存状态。</summary>
public class StockPlan(string name, bool inStock)
{
    public string Name { get; set; } = name;
    public bool InStock { get; set; } = inStock;

    /// <summary>本轮放货已提醒过，售罄后复位，避免每次轮询重复报警。</summary>
    public bool Alerted { get; set; }
}

/// <summary>商店页面库存监控：定时抓取页面，套餐从售罄变为可购买时弹 Toast 提醒。</summary>
public class StockMonitor(MonitorController controller)
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(StockMonitor));

    private const string SoldOutMarker = "全部售罄";

    // 直连兜底用。注意站点若有 Cloudflare 按 TLS 指纹拦截，HttpClient 过不去，
    // 优先复用同站账号的 WebView2 会话（真实浏览器指纹）
    private static readonly HttpClient Http = CreateHttpClient();

    public List<StockPlan> Plans { get; } = [];
    public DateTime? LastCheck { get; private set; }
    public string? Error { get; private set; }
    public bool Simulated { get; set; }
    public bool Checking { get; private set; }

    public bool AnyInStock => Plans.Any(p => p.InStock);

    private CancellationTokenSource _delayCts = new();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
                + " (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"
        );
        return client;
    }

    public void Start()
    {
        _ = LoopAsync();
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            if (SettingsManager.Settings.StockMonitorEnabled)
                await CheckAsync();

            var interval = TimeSpan.FromMinutes(
                Math.Max(1, SettingsManager.Settings.StockMonitorIntervalMinutes)
            );
            try
            {
                await Task.Delay(interval, _delayCts.Token);
            }
            catch (OperationCanceledException)
            {
                _delayCts = new CancellationTokenSource();
            }
        }
    }

    /// <summary>打断当前等待，立即进入下一轮检查（开关打开或设置变更时调用）。</summary>
    public void TriggerCheck()
    {
        _delayCts.Cancel();
    }

    public async Task CheckAsync()
    {
        if (Checking || Simulated)
            return;

        Checking = true;
        try
        {
            var url = SettingsManager.Settings.StockMonitorUrl;
            var html = await FetchPageAsync(url);
            var parsed = ParsePlans(html);
            if (parsed.Count == 0)
                throw new InvalidOperationException("页面中未找到任何套餐，可能页面结构已变化");

            MergePlans(parsed);
            LastCheck = DateTime.Now;
            Error = null;
            EvaluateAlerts();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.ZLogWarning($"Stock check failed: {ex.Message}");
        }
        finally
        {
            Checking = false;
            controller.RebuildStatusWindow();
        }
    }

    private async Task<string> FetchPageAsync(string url)
    {
        // www 与裸域视为同站；WebView2 里 fetch 必须同源，匹配后把 host 改写成账号的
        var host = NormalizeHost(new Uri(url).Host);
        AccountState? account = null;
        Uri? accountBase = null;
        foreach (var a in controller.Accounts)
        {
            if (
                Uri.TryCreate(a.Config.BaseUrl, UriKind.Absolute, out var baseUri)
                && string.Equals(
                    NormalizeHost(baseUri.Host),
                    host,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                account = a;
                accountBase = baseUri;
                break;
            }
        }

        if (account == null || accountBase == null)
            return await Http.GetStringAsync(url);

        var sameOriginUrl = new UriBuilder(url)
        {
            Host = accountBase.Host,
            Scheme = accountBase.Scheme,
            Port = accountBase.IsDefaultPort ? -1 : accountBase.Port,
        }.Uri.ToString();

        await account.Session.EnsureInitializedAsync();
        var res = await account.Session.FetchAsync(sameOriginUrl);
        if (res.Status is < 200 or >= 300)
            throw new InvalidOperationException($"HTTP {res.Status}");
        return res.Body;
    }

    private static string NormalizeHost(string host)
    {
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }

    /// <summary>
    ///     解析商店页面：每个套餐卡片以 &lt;h3 class="package-title"&gt; 开头，
    ///     卡片内出现“全部售罄”即无货。
    /// </summary>
    public static List<(string Name, bool InStock)> ParsePlans(string html)
    {
        var result = new List<(string, bool)>();
        const string titleMarker = "package-title\">";

        var pos = html.IndexOf(titleMarker, StringComparison.Ordinal);
        while (pos >= 0)
        {
            var nameStart = pos + titleMarker.Length;
            var nameEnd = html.IndexOf('<', nameStart);
            if (nameEnd < 0)
                break;

            var name = html[nameStart..nameEnd].Trim();
            var next = html.IndexOf(titleMarker, nameEnd, StringComparison.Ordinal);
            var cardEnd = next >= 0 ? next : html.Length;
            var inStock = !html[nameEnd..cardEnd].Contains(SoldOutMarker, StringComparison.Ordinal);

            if (name.Length > 0)
                result.Add((name, inStock));
            pos = next;
        }

        return result;
    }

    private void MergePlans(List<(string Name, bool InStock)> parsed)
    {
        // 按名字合并，保留已有的 Alerted 状态；页面下架的套餐移除
        var merged = new List<StockPlan>();
        foreach (var (name, inStock) in parsed)
        {
            var plan = Plans.FirstOrDefault(p => p.Name == name) ?? new StockPlan(name, inStock);
            plan.InStock = inStock;
            merged.Add(plan);
        }

        Plans.Clear();
        Plans.AddRange(merged);
    }

    /// <summary>对新放货的套餐弹 Toast，套餐售罄后复位提醒标记。</summary>
    public void EvaluateAlerts()
    {
        var fresh = new List<string>();
        foreach (var plan in Plans)
        {
            if (plan.InStock && !plan.Alerted)
            {
                plan.Alerted = true;
                fresh.Add(plan.Name);
            }
            else if (!plan.InStock)
            {
                plan.Alerted = false;
            }
        }

        if (fresh.Count == 0)
            return;

        Log.ZLogInformation($"Stock available: {string.Join(", ", fresh)}");
        controller.Alerts.ShowToast(
            "库存提醒：有套餐放货了！",
            $"{string.Join("、", fresh)} 现在可以购买\n{SettingsManager.Settings.StockMonitorUrl}"
        );
    }
}

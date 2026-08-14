using System.Globalization;
using System.Text.Json;
using JeekTools;
using Microsoft.Extensions.Logging;
using VpsLimitMonitor.Providers;
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

/// <summary>一个供应商下需要独立检查和开关的库存目标。</summary>
public class StockSourceState(string providerName, string providerType, string targetName)
{
    public string ProviderName { get; } = providerName;
    public string ProviderType { get; } = providerType;
    public string TargetName { get; } = targetName;
    public List<StockPlan> Plans { get; } = [];
    public DateTime? LastCheck { get; set; }
    public string? Error { get; set; }
    public bool Simulated { get; set; }
    public bool Checking { get; set; }
    public bool AnyInStock => Plans.Any(plan => plan.InStock);
}

/// <summary>按供应商定时检查指定套餐库存，并在售罄转为有货时提醒。</summary>
public class StockMonitor(MonitorController controller)
{
    public const string NovixLinkProviderName = "NovixLink";
    public const string HostYunProviderName = "HostYun";

    private static readonly ILogger Log = LogManager.CreateLogger(nameof(StockMonitor));
    private const string SoldOutMarker = "全部售罄";
    private const string HostYunTargetId = "186";

    // 直连兜底用。站点若按浏览器指纹拦截，则优先复用同站账号的 WebView2 会话。
    private static readonly HttpClient Http = CreateHttpClient();

    public List<StockSourceState> Sources { get; } =
    [
        new(NovixLinkProviderName, "WhmcsCubeCloud", "Basic"),
        new(HostYunProviderName, "IdcSystemKvm", "套餐 B"),
    ];

    public bool AnyInStock => Sources.Any(source => source.AnyInStock);
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
            await CheckAsync(enabledOnly: true);

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

    public StockSourceState? FindSource(string providerName) =>
        Sources.FirstOrDefault(source =>
            string.Equals(source.ProviderName, providerName, StringComparison.OrdinalIgnoreCase)
        );

    public StockSourceState? FindSource(AccountState account) =>
        Sources.FirstOrDefault(source =>
            string.Equals(source.ProviderType, account.Config.Type, StringComparison.OrdinalIgnoreCase)
        );

    public bool IsEnabled(StockSourceState source) =>
        source.ProviderName switch
        {
            NovixLinkProviderName => SettingsManager.Settings.StockMonitorEnabled,
            HostYunProviderName => SettingsManager.Settings.HostYunStockMonitorEnabled,
            _ => false,
        };

    public string GetUrl(StockSourceState source) =>
        source.ProviderName switch
        {
            NovixLinkProviderName => SettingsManager.Settings.StockMonitorUrl,
            HostYunProviderName => SettingsManager.Settings.HostYunStockMonitorUrl,
            _ => "",
        };

    /// <summary>检查指定供应商；providerName 为空时检查全部目标。</summary>
    public async Task CheckAsync(string? providerName = null, bool enabledOnly = false)
    {
        var sources = providerName == null
            ? Sources
            :
            [
                FindSource(providerName)
                    ?? throw new InvalidOperationException(
                        $"Stock provider not found: {providerName}"
                    ),
            ];

        foreach (var source in sources)
        {
            if (!enabledOnly || IsEnabled(source))
                await CheckSourceAsync(source);
        }
    }

    private async Task CheckSourceAsync(StockSourceState source)
    {
        if (source.Checking || source.Simulated)
            return;

        source.Checking = true;
        try
        {
            List<(string Name, bool InStock)> parsed;
            if (source.ProviderName == NovixLinkProviderName)
            {
                var html = await FetchPageAsync(GetUrl(source));
                parsed =
                [
                    .. ParseNovixLinkPlans(html).Where(plan =>
                        plan.Name.Contains("Basic", StringComparison.OrdinalIgnoreCase)
                    ),
                ];
            }
            else if (source.ProviderName == HostYunProviderName)
            {
                parsed = await FetchHostYunPlanAsync(source);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unsupported stock provider: {source.ProviderName}"
                );
            }

            if (parsed.Count == 0)
                throw new InvalidOperationException(
                    $"页面中未找到 {source.TargetName}，可能页面结构已变化"
                );

            MergePlans(source, parsed);
            source.LastCheck = DateTime.Now;
            source.Error = null;
            EvaluateAlerts(source);
        }
        catch (SessionExpiredException)
        {
            source.Error = null;
            if (FindAccount(source) is { } account)
            {
                account.LoggedIn = false;
                controller.Alerts.NotifyLoginNeeded(account);
                controller.Tray.Update();
            }
            Log.ZLogWarning($"{source.ProviderName} stock check: session expired");
        }
        catch (Exception ex)
        {
            source.Error = ex.Message;
            Log.ZLogWarning($"{source.ProviderName} stock check failed: {ex.Message}");
        }
        finally
        {
            source.Checking = false;
            controller.RebuildStatusWindow();
        }
    }

    private async Task<List<(string Name, bool InStock)>> FetchHostYunPlanAsync(
        StockSourceState source
    )
    {
        var account = FindAccount(source)
            ?? throw new InvalidOperationException("未找到 HostYun 账号");
        await account.Session.EnsureInitializedAsync();

        var pageUri = new Uri(GetUrl(source));
        var productUrl = new Uri(
            pageUri,
            "/?c=ajax&dt=product&id=-1&p1=42&p2=all&rt=json"
        ).ToString();
        var response = await account.Session.FetchAsync(productUrl);
        if (response.Status is < 200 or >= 300)
            throw new InvalidOperationException($"HostYun HTTP {response.Status}");
        if (
            response.Url.Contains("page.aspx?c=login", StringComparison.OrdinalIgnoreCase)
            || response.Body.Contains("page.aspx?c=login", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new SessionExpiredException();
        }

        using var document = JsonDocument.Parse(response.Body);
        foreach (var product in document.RootElement.EnumerateArray())
        {
            if (product.GetProperty("pid").GetString() != HostYunTargetId)
                continue;

            var stockText = product.GetProperty("pstock").GetString() ?? "0";
            var inStock =
                double.TryParse(
                    stockText,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var stock
                ) && stock > 0;
            return
            [
                (
                    product.GetProperty("pname").GetString() ?? "洛杉矶1024M 套餐B",
                    inStock
                ),
            ];
        }

        return [];
    }

    private AccountState? FindAccount(StockSourceState source) =>
        controller.Accounts.FirstOrDefault(account =>
            string.Equals(
                account.Config.Type,
                source.ProviderType,
                StringComparison.OrdinalIgnoreCase
            )
        );

    private async Task<string> FetchPageAsync(string url)
    {
        var host = NormalizeHost(new Uri(url).Host);
        AccountState? account = null;
        Uri? accountBase = null;
        foreach (var candidate in controller.Accounts)
        {
            if (
                Uri.TryCreate(candidate.Config.BaseUrl, UriKind.Absolute, out var baseUri)
                && string.Equals(
                    NormalizeHost(baseUri.Host),
                    host,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                account = candidate;
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
        var response = await account.Session.FetchAsync(sameOriginUrl);
        if (response.Status is < 200 or >= 300)
            throw new InvalidOperationException($"HTTP {response.Status}");
        return response.Body;
    }

    private static string NormalizeHost(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    /// <summary>解析 NovixLink 套餐卡片；卡片内出现“全部售罄”即无货。</summary>
    public static List<(string Name, bool InStock)> ParseNovixLinkPlans(string html)
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

    private static void MergePlans(
        StockSourceState source,
        List<(string Name, bool InStock)> parsed
    )
    {
        var merged = new List<StockPlan>();
        foreach (var (name, inStock) in parsed)
        {
            var plan = source.Plans.FirstOrDefault(plan => plan.Name == name)
                ?? new StockPlan(name, inStock);
            plan.InStock = inStock;
            merged.Add(plan);
        }

        source.Plans.Clear();
        source.Plans.AddRange(merged);
    }

    /// <summary>对指定来源的新放货套餐弹 Toast，售罄后复位提醒标记。</summary>
    public void EvaluateAlerts(StockSourceState source)
    {
        var fresh = new List<string>();
        foreach (var plan in source.Plans)
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

        Log.ZLogInformation(
            $"{source.ProviderName} stock available: {string.Join(", ", fresh)}"
        );
        controller.Alerts.ShowToast(
            $"{source.ProviderName} 库存提醒",
            $"{string.Join("、", fresh)} 现在可以购买\n{GetUrl(source)}"
        );
    }

    public void ClearSimulation()
    {
        foreach (var source in Sources)
            source.Simulated = false;
    }
}

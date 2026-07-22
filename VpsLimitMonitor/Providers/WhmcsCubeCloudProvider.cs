using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using VpsLimitMonitor.Web;

namespace VpsLimitMonitor.Providers;

/// <summary>
///     WHMCS + CubeCloud 服务器模块的适配器（NovixLink 及同款面板的商家通用）。
///     服务列表来自 clientarea.php?action=services，
///     流量数据来自 clientarea.php?action=productdetails&amp;id=X&amp;getJSON。
/// </summary>
public partial class WhmcsCubeCloudProvider(WebSession session) : IVpsProvider
{
    public string TypeName => "WhmcsCubeCloud";

    public string LoginUrl => $"{session.BaseUrl}/clientarea.php";

    [GeneratedRegex(@"VM-[A-Za-z0-9]+")]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"IP:\s*([0-9a-fA-F.:]+)")]
    private static partial Regex IpRegex();

    [GeneratedRegex(@"^([\d.]+)\s*(MB|GB|TB)?", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    public async Task<bool> IsLoggedInAsync()
    {
        var rows = await FetchServiceRowsAsync();
        return rows != null;
    }

    public async Task<IReadOnlyList<VpsService>> ListServicesAsync()
    {
        var rows = await FetchServiceRowsAsync() ?? throw new SessionExpiredException();

        var services = new List<VpsService>();
        foreach (var row in rows)
        {
            var id = row.GetProperty("serviceId").GetString() ?? "";
            var text = row.GetProperty("text").GetString() ?? "";

            var label = LabelRegex().Match(text) is { Success: true } lm ? lm.Value : $"#{id}";
            var ip = IpRegex().Match(text) is { Success: true } im ? im.Groups[1].Value : null;
            var name = text;
            var labelIndex = text.IndexOf(label, StringComparison.Ordinal);
            if (labelIndex > 0)
                name = text[..labelIndex].Trim().TrimEnd('-').Trim();

            services.Add(new VpsService(id, name, label, ip));
        }

        // Lagom 主题的服务表格由 JS 动态填充，原始 HTML 里没有行数据，改走它的 JSON API
        if (services.Count == 0)
            services.AddRange(await ListServicesFromLagomApiAsync());

        return services;
    }

    private async Task<List<VpsService>> ListServicesFromLagomApiAsync()
    {
        var res = await session.FetchAsync(
            "/modules/addons/RSThemes/src/Api/clientApi.php"
                + "?controller=ClientData&method=getClientServices&draw=1&start=0&length=500"
        );

        var services = new List<VpsService>();
        var body = res.Body.Trim();
        if (!body.StartsWith('{'))
            return services;

        using var doc = JsonDocument.Parse(body);
        if (
            !doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
        )
            return services;

        foreach (var row in data.EnumerateArray())
        {
            var id = row.GetProperty("id").GetRawText().Trim('"');
            var label = row.TryGetProperty("domain", out var domain)
                ? domain.GetString() ?? $"#{id}"
                : $"#{id}";
            var name = row.TryGetProperty("productName", out var product)
                ? product.GetString() ?? ""
                : "";
            var ip = row.TryGetProperty("dedicatedip", out var dedicatedIp)
                ? dedicatedIp.GetString()
                : null;
            services.Add(new VpsService(id, name, label, ip));
        }

        return services;
    }

    /// <summary>拉取服务列表页并在浏览器里用 DOM 解析出行数据。未登录时返回 null。</summary>
    private async Task<List<JsonElement>?> FetchServiceRowsAsync()
    {
        var script = """
            fetch("/clientarea.php?action=services", { credentials: "include" })
                .then(function (r) { return r.text().then(function (t) { return { url: r.url, html: t }; }); })
                .then(function (res) {
                    var doc = new DOMParser().parseFromString(res.html, "text/html");
                    var loggedOut =
                        res.url.indexOf("login") >= 0 ||
                        !!doc.querySelector("input[type='password']");
                    var rows = [];
                    doc.querySelectorAll("table tbody tr").forEach(function (tr) {
                        var link = tr.querySelector("a[href*='productdetails']");
                        if (!link) return;
                        var m = (link.getAttribute("href") || "").match(/id=(\d+)/);
                        if (!m) return;
                        rows.push({
                            serviceId: m[1],
                            text: (tr.textContent || "").replace(/\s+/g, " ").trim(),
                        });
                    });
                    __post({ loggedOut: loggedOut, rows: rows });
                })
                .catch(function (e) { __post({ error: String(e) }); });
            """;

        var result = await session.RunAsyncScriptAsync(script);
        if (result.GetProperty("loggedOut").GetBoolean())
            return null;

        return [.. result.GetProperty("rows").EnumerateArray()];
    }

    public async Task<TrafficInfo> GetTrafficAsync(VpsService service)
    {
        var res = await session.FetchAsync(
            $"/clientarea.php?action=productdetails&id={service.Id}&getJSON"
        );

        var body = res.Body.Trim();
        if (!body.StartsWith('{'))
            throw new SessionExpiredException();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var usedGB = ParseSizeGB(GetStringOrNumber(root, "trafficUsed"));
        var totalGB = ParseSizeGB(GetStringOrNumber(root, "trafficTotal"));
        var resetNotice = root.TryGetProperty("trafficResetNotice", out var notice)
            ? notice.GetString()
            : null;
        var isOnline =
            root.TryGetProperty("status", out var status) && status.GetString() == "on";

        return new TrafficInfo(usedGB, totalGB, resetNotice, isOnline);
    }

    private static string GetStringOrNumber(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
            return "";

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetDouble().ToString(CultureInfo.InvariantCulture),
            _ => "",
        };
    }

    /// <summary>解析 "82.08 GB" / "1000" 这类流量值，统一为 GB。无单位按 GB 处理。</summary>
    private static double ParseSizeGB(string text)
    {
        var match = SizeRegex().Match(text.Trim());
        if (!match.Success)
            return 0;

        var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return match.Groups[2].Value.ToUpperInvariant() switch
        {
            "MB" => value / 1024,
            "TB" => value * 1024,
            _ => value,
        };
    }
}

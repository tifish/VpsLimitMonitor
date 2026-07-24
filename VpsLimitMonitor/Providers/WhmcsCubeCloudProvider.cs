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

    [GeneratedRegex(@"\b(\d{4}-\d{2}-\d{2})\b")]
    private static partial Regex IsoDateRegex();

    [GeneratedRegex(@"(\d{4}-\d{2}-\d{2} \d{2}:\d{2}(?::\d{2})?)\s*美东时间")]
    private static partial Regex EasternTimeRegex();

    /// <summary>站点返回的重置时间是美东时间，转成本地时区；解析不了就原样保留。</summary>
    private static string? ToLocalResetNotice(string? notice)
    {
        if (notice == null)
            return null;

        var match = EasternTimeRegex().Match(notice);
        if (!match.Success)
            return notice;

        try
        {
            var text = match.Groups[1].Value;
            var eastern = DateTime.ParseExact(
                text,
                text.Length > 16 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture
            );
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var local = TimeZoneInfo.ConvertTime(eastern, zone, TimeZoneInfo.Local);
            return $"{local:yyyy-MM-dd HH:mm}";
        }
        catch (Exception)
        {
            return notice;
        }
    }

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

            // 行文本里日期格式随 WHMCS 设置变化，只认无歧义的 ISO 格式（即下次到期日列）
            DateOnly? dueDate =
                IsoDateRegex().Match(text) is { Success: true } dm
                && DateOnly.TryParseExact(dm.Groups[1].Value, "yyyy-MM-dd", out var parsed)
                    ? parsed
                    : null;

            services.Add(new VpsService(id, name, label, ip, dueDate));
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
            DateOnly? dueDate =
                row.TryGetProperty("normalisednextduedate", out var due)
                && DateOnly.TryParseExact(due.GetString(), "yyyy-MM-dd", out var parsed)
                    ? parsed
                    : null;
            services.Add(new VpsService(id, name, label, ip, dueDate));
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
            ? ToLocalResetNotice(notice.GetString())
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

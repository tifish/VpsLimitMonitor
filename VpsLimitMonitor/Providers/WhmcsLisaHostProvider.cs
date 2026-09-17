using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using VpsLimitMonitor.Web;

namespace VpsLimitMonitor.Providers;

/// <summary>LisaHost's CubeCloud variant renders traffic in HTML; getJSON only reports power state.</summary>
public partial class WhmcsLisaHostProvider(WebSession session) : IVpsProvider
{
    public string TypeName => "WhmcsLisaHost";
    public string LoginUrl => $"{session.BaseUrl}/clientarea.php";
    public string GetServiceUrl(VpsService service) =>
        $"{session.BaseUrl}/clientarea.php?action=productdetails&id={Uri.EscapeDataString(service.Id)}";

    // Shared with the Debug MCP regression probe so it exercises the production DOM selectors.
    internal const string ParserScript = """
        function text(el) { return el ? (el.textContent || "").replace(/\s+/g, " ").trim() : ""; }
        function page(html, url, status) {
            var doc = new DOMParser().parseFromString(html, "text/html");
            if (/\/login(?:[/?#]|$)/i.test(url) || doc.querySelector("#frmLogin, form[action*='dologin']") ||
                (doc.querySelector("input[type='password']") && doc.querySelector("input[type='email'], input[name='username']")))
                return null;
            if (status !== 200) throw new Error("LisaHost request failed: HTTP " + status);
            return doc;
        }
        function serviceRows(html, url, status) {
            var doc = page(html, url, status);
            if (!doc) return { loggedOut: true };
            var table = doc.querySelector("#tableServicesList");
            if (!table) throw new Error("LisaHost service table was not found");
            var rows = [];
            table.querySelectorAll("tbody tr").forEach(function (tr) {
                var link = tr.querySelector("a[href*='productdetails']");
                var id = link && (link.getAttribute("href") || "").match(/[?&]id=(\d+)/);
                if (!id) return;
                var main = text(tr.querySelector(".service-main"));
                var ip = main.match(/(?:主)?IP:\s*([0-9a-fA-F.:]+)/i);
                var label = main.split(/\s+/)[0];
                var due = "";
                tr.querySelectorAll("td").forEach(function (td) {
                    // WHMCS repeats the date in a hidden sorting span, with no separating whitespace.
                    var match = text(td).match(/^(\d{4}-\d{2}-\d{2})/);
                    if (match && !due) due = match[1];
                });
                rows.push({ id: id[1], name: text(tr.querySelector("strong")),
                    label: label || "#" + id[1], ip: ip ? ip[1] : null, dueDate: due });
            });
            return { loggedOut: false, rows: rows };
        }
        function trafficFields(html, url, status) {
            var doc = page(html, url, status);
            if (!doc) return { loggedOut: true };
            var used = doc.querySelector(".top-info .used");
            if (!used) throw new Error("LisaHost traffic summary was not found");
            var summary = text(used.parentElement);
            var parts = summary.split("/");
            if (parts.length !== 2) throw new Error("LisaHost traffic summary has an unexpected format");
            return { loggedOut: false, used: text(used), total: parts[1].trim() };
        }
        """;

    public async Task<bool> IsLoggedInAsync() => await FetchServiceRowsAsync() != null;

    private async Task<JsonElement?> FetchServiceRowsAsync()
    {
        var result = await session.RunAsyncScriptAsync(ParserScript + """

            fetch("/clientarea.php?action=services", { credentials: "include" })
                .then(r => r.text().then(t => serviceRows(t, r.url, r.status)))
                .then(__post).catch(e => __post({ error: String(e) }));
            """);
        return result.GetProperty("loggedOut").GetBoolean() ? null : result.GetProperty("rows");
    }

    public async Task<IReadOnlyList<VpsService>> ListServicesAsync()
    {
        var rows = await FetchServiceRowsAsync() ?? throw new SessionExpiredException();
        return rows.EnumerateArray().Select(row => new VpsService(
            row.GetProperty("id").GetString()!,
            row.GetProperty("name").GetString()!,
            row.GetProperty("label").GetString()!,
            row.GetProperty("ip").GetString(),
            DateOnly.TryParseExact(row.GetProperty("dueDate").GetString(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var due) ? due : null
        )).ToArray();
    }

    public async Task<TrafficInfo> GetTrafficAsync(VpsService service)
    {
        var url = JsonSerializer.Serialize(GetServiceUrl(service));
        // Extract only traffic fields from the detail HTML; it also contains server credentials.
        var result = await session.RunAsyncScriptAsync(ParserScript + $$"""

            var url = {{url}};
            fetch(url, { credentials: "include" })
                .then(r => r.text().then(t => trafficFields(t, r.url, r.status)))
                .then(async function (traffic) {
                    if (traffic.loggedOut) return __post(traffic);
                    var response = await fetch(url + "&getJSON", { credentials: "include" });
                    var body = await response.text();
                    var power;
                    try { power = JSON.parse(body); }
                    catch (e) {
                        if (!page(body, response.url, response.status)) return __post({ loggedOut: true });
                        throw new Error("LisaHost power status response is not JSON");
                    }
                    if (response.status !== 200 || !power || (power.status !== "on" && power.status !== "off"))
                        throw new Error("LisaHost power status is unavailable");
                    traffic.online = power.status === "on";
                    __post(traffic);
                }).catch(e => __post({ error: String(e) }));
            """);
        if (result.GetProperty("loggedOut").GetBoolean())
            throw new SessionExpiredException();

        return ParseTraffic(result);
    }

    internal static TrafficInfo ParseTraffic(JsonElement fields)
    {
        var used = ParseSizeGB(fields.GetProperty("used").GetString() ?? "");
        var total = ParseSizeGB(fields.GetProperty("total").GetString() ?? "");
        if (total <= 0)
            throw new InvalidOperationException("LisaHost traffic limit must be greater than zero");
        return new TrafficInfo(used, total, null, fields.GetProperty("online").GetBoolean());
    }

    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)\s*(B|KB|MB|GB|TB)$", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    internal static double ParseSizeGB(string text)
    {
        var match = SizeRegex().Match(text.Trim());
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new InvalidOperationException("LisaHost traffic value is missing or invalid");

        var gb = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "B" => value / (1024 * 1024 * 1024),
            "KB" => value / (1024 * 1024),
            "MB" => value / 1024,
            "TB" => value * 1024,
            _ => value,
        };
        if (!double.IsFinite(gb))
            throw new InvalidOperationException("LisaHost traffic value is out of range");
        return gb;
    }
}

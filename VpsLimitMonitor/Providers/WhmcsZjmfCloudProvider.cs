using System.Globalization;
using System.Text.Json;
using VpsLimitMonitor.Web;

namespace VpsLimitMonitor.Providers;

/// <summary>
///     WHMCS + 魔方云（zjmfcloud）服务器模块的适配器，CstoneCloud 使用。
///     服务列表来自 clientarea.php?action=services 的服务端渲染表格；
///     流量来自 productdetails&amp;ac=traffictotal（单位 MB），电源状态来自 ac=default 的 status 调用。
/// </summary>
public class WhmcsZjmfCloudProvider(WebSession session) : IVpsProvider
{
    public string TypeName => "WhmcsZjmfCloud";

    public string LoginUrl => $"{session.BaseUrl}/clientarea.php";

    public string GetServiceUrl(VpsService service) =>
        $"{session.BaseUrl}/clientarea.php?action=productdetails&id={Uri.EscapeDataString(service.Id)}";

    public async Task<bool> IsLoggedInAsync()
    {
        return await FetchServiceRowsAsync() != null;
    }

    public async Task<IReadOnlyList<VpsService>> ListServicesAsync()
    {
        var rows = await FetchServiceRowsAsync() ?? throw new SessionExpiredException();
        var services = new List<VpsService>();

        foreach (var row in rows)
        {
            var id = row.GetProperty("serviceId").GetString() ?? "";
            var product = row.GetProperty("product").GetString() ?? "";
            var hostname = row.GetProperty("hostname").GetString() ?? "";
            var ip = row.GetProperty("ip").GetString();
            DateOnly? dueDate =
                DateOnly.TryParseExact(
                    row.GetProperty("dueDate").GetString(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed
                )
                    ? parsed
                    : null;

            services.Add(
                new VpsService(
                    id,
                    product,
                    hostname == "" ? $"#{id}" : hostname,
                    string.IsNullOrEmpty(ip) ? null : ip,
                    dueDate
                )
            );
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
                        var cells = tr.querySelectorAll("td");
                        var strong = tr.querySelector("strong");
                        var text = function (el) { return el ? (el.textContent || "").replace(/\s+/g, " ").trim() : ""; };
                        var ip = "";
                        var dueDate = "";
                        cells.forEach(function (td) {
                            var t = text(td);
                            var ipMatch = t.match(/^(?:\d{1,3}\.){3}\d{1,3}$/);
                            if (!ip && ipMatch) ip = ipMatch[0];
                            var dateMatch = t.match(/^(\d{4}-\d{2}-\d{2})/);
                            if (!dueDate && dateMatch) dueDate = dateMatch[1];
                        });
                        rows.push({
                            serviceId: m[1],
                            product: text(strong),
                            hostname: text(link),
                            ip: ip,
                            dueDate: dueDate,
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
        // ac=product 的响应里带有 root 密码等敏感字段，只在页面里取出重置日，不回传到进程日志。
        var script = $$"""
            var base = "/clientarea.php?action=productdetails&id={{Uri.EscapeDataString(service.Id)}}";
            var readText = function (r) { return r.text().then(function (t) { return { url: r.url, body: t }; }); };
            var parse = function (res) { try { return JSON.parse(res.body); } catch (e) { return null; } };
            Promise.all([
                fetch(base + "&ac=traffictotal", { credentials: "include" }).then(readText),
                fetch(base + "&ac=default", {
                    method: "POST",
                    credentials: "include",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ id: {{JsonSerializer.Serialize(service.Id)}}, func: "status" })
                }).then(readText),
                fetch(base + "&ac=product", { credentials: "include" }).then(readText)
            ])
                .then(function (all) {
                    var traffic = parse(all[0]);
                    var power = parse(all[1]);
                    var product = parse(all[2]);
                    var hostData = product && product.data && product.data.host_data;
                    __post({
                        loggedOut: !traffic && all[0].url.indexOf("login") >= 0,
                        traffic: traffic,
                        trafficBody: traffic ? "" : all[0].body.slice(0, 300),
                        powerStatus: power && power.status === 200 && power.data ? String(power.data.status) : "",
                        resetDay: hostData && hostData.reset_flow_day ? Number(hostData.reset_flow_day) : 0
                    });
                })
                .catch(function (e) { __post({ error: String(e) }); });
            """;

        var result = await session.RunAsyncScriptAsync(script);
        if (result.GetProperty("loggedOut").GetBoolean())
            throw new SessionExpiredException();

        var traffic = result.GetProperty("traffic");
        if (traffic.ValueKind != JsonValueKind.Object)
        {
            // 会话失效时 WHMCS 会返回登录页 HTML 而非 JSON
            var body = result.GetProperty("trafficBody").GetString() ?? "";
            if (body.Contains("password", StringComparison.OrdinalIgnoreCase))
                throw new SessionExpiredException();
            throw new InvalidOperationException($"CstoneCloud traffic request failed: {body}");
        }

        if (
            !traffic.TryGetProperty("status", out var status)
            || status.GetString() != "success"
            || !traffic.TryGetProperty("data", out var data)
        )
            throw new InvalidOperationException(
                $"CstoneCloud traffic request failed: {traffic.GetRawText()}"
            );

        var resetDay = result.GetProperty("resetDay").GetInt32();
        return new TrafficInfo(
            GetNumber(data, "bwusage") / 1024,
            GetNumber(data, "bwlimit") / 1024,
            resetDay > 0 ? $"每月{resetDay}日清零" : null,
            result.GetProperty("powerStatus").GetString() == "on"
        );
    }

    private static double GetNumber(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String
                when double.TryParse(
                    value.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsed
                ) => parsed,
            _ => 0,
        };
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using VpsLimitMonitor.Web;

namespace VpsLimitMonitor.Providers;

/// <summary>
///     IDCSystem KVM panel adapter used by HostYun. The service list is parsed from
///     idcsystem.aspx?c=myservice; service details load traffic asynchronously, so a hidden
///     same-origin frame is allowed to finish the panel's own script before its DOM is read.
/// </summary>
public partial class IdcSystemKvmProvider(WebSession session) : IVpsProvider
{
    public string TypeName => "IdcSystemKvm";

    public string LoginUrl => $"{session.BaseUrl}/page.aspx?c=login";

    public string GetServiceUrl(VpsService service) =>
        $"{session.BaseUrl}/idcsystem.aspx?c=myservice&id={Uri.EscapeDataString(service.Id)}";

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ipv4Regex();

    [GeneratedRegex(@"page\.aspx\?c=(?:login|logout)", RegexOptions.IgnoreCase)]
    private static partial Regex LoginRedirectRegex();

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
            var label = row.GetProperty("label").GetString() ?? $"#{id}";
            var description = row.GetProperty("description").GetString() ?? "";
            var dueDateText = row.GetProperty("dueDate").GetString();
            var ip = Ipv4Regex().Match(description) is { Success: true } ipMatch
                ? ipMatch.Value
                : null;
            DateOnly? dueDate =
                DateOnly.TryParseExact(
                    dueDateText,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed
                )
                    ? parsed
                    : null;

            services.Add(new VpsService(id, description, label, ip, dueDate));
        }

        return services;
    }

    private async Task<List<JsonElement>?> FetchServiceRowsAsync()
    {
        var script = """
            fetch("/idcsystem.aspx?c=myservice", { credentials: "include", redirect: "follow" })
                .then(function (r) { return r.text().then(function (t) { return { url: r.url, html: t }; }); })
                .then(function (res) {
                    var doc = new DOMParser().parseFromString(res.html, "text/html");
                    var loggedOut =
                        /page\.aspx\?c=(?:login|logout)/i.test(res.url) ||
                        /page\.aspx\?c=(?:login|logout)/i.test(res.html) ||
                        !!doc.querySelector("input[type='password']");
                    var rows = [];
                    doc.querySelectorAll("tr[id^='tr']").forEach(function (tr) {
                        var match = tr.id.match(/^tr(\d+)$/);
                        var serviceCell = tr.querySelector("td[name='ssid']");
                        var nameCell = tr.querySelector("td[name='sname']");
                        var cells = tr.querySelectorAll("td");
                        if (!match || !serviceCell || !nameCell || cells.length < 6) return;

                        var serviceParts = (serviceCell.textContent || "").trim().split("_");
                        if (serviceParts[0] !== "6") return;
                        var description =
                            (nameCell.textContent || "").trim() ||
                            (nameCell.getAttribute("title") || "").trim();
                        rows.push({
                            serviceId: match[1],
                            label: "KVM" + (serviceParts[1] || match[1]),
                            description: description.replace(/\s+/g, " ").trim(),
                            dueDate: (cells[5].textContent || "").trim(),
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
            "/idcsystem.aspx?c=module"
                + $"&serviceid={Uri.EscapeDataString(service.Id)}"
                + "&action=custom_action&show=text&subaction=vmcontrol&vaction=state"
        );

        var body = res.Body.Trim();
        if (LoginRedirectRegex().IsMatch(res.Url) || LoginRedirectRegex().IsMatch(body))
            throw new SessionExpiredException();

        var parts = body.Split('|', 3);
        if (parts.Length != 3 || parts[0] != "0" || !parts[2].TrimStart().StartsWith('{'))
            throw new InvalidOperationException($"HostYun state request failed: {body}");

        using var doc = JsonDocument.Parse(parts[2]);
        var root = doc.RootElement;
        return new TrafficInfo(
            GetNumber(root, "bwusage"),
            GetNumber(root, "plantraffic"),
            "每月月初清零",
            string.Equals(parts[1], "running", StringComparison.OrdinalIgnoreCase)
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

#if DEBUG
using System.Text.Json;
using VpsLimitMonitor.Providers;
using VpsLimitMonitor.Web;

namespace VpsLimitMonitor.McpDebug;

internal static class LisaHostProbe
{
    public static async Task<object> RunAsync(WebSession session)
    {
        var dom = await session.RunAsyncScriptAsync(WhmcsLisaHostProvider.ParserScript + """

            var checks = [];
            function check(name, condition) {
                if (!condition) throw new Error("Regression failed: " + name);
                checks.push(name);
            }
            function rejects(name, fn) {
                var rejected = false;
                try { fn(); } catch (e) { rejected = true; }
                check(name, rejected);
            }
            var url = "https://lisahost.com/clientarea.php?action=services";
            var row = '<tr><td>未命名<button>修改备注</button></td>' +
                '<td><strong>Example VPS</strong><div class="service-main">C202609111234 主IP: 192.0.2.1</div></td>' +
                '<td>61.20元 每月</td><td><span class="hidden">2026-10-11</span>2026-10-11</td>' +
                '<td>有效的</td><td><a href="clientarea.php?action=productdetails&amp;id=123">详情</a></td></tr>';
            var list = serviceRows('<table id="tableServicesList"><tbody>' + row + '</tbody></table>', url, 200);
            var item = list.rows[0];
            check("service metadata", list.rows.length === 1 && item.id === "123" && item.name === "Example VPS" &&
                item.label === "C202609111234" && item.ip === "192.0.2.1");
            check("duplicated hidden due date", item.dueDate === "2026-10-11");
            var ipv6 = serviceRows('<table id="tableServicesList"><tbody>' + row.replace('192.0.2.1', '2001:db8::1') + '</tbody></table>', url, 200);
            check("IPv6 address", ipv6.rows[0].ip === "2001:db8::1");
            check("empty account", serviceRows('<table id="tableServicesList"><tbody><tr><td>No services</td></tr></tbody></table>', url, 200).rows.length === 0);
            check("login form", serviceRows('<form id="frmLogin"><input type="password"></form>', url, 200).loggedOut);
            check("login redirect", trafficFields('', 'https://lisahost.com/login', 200).loggedOut);
            rejects("missing service table", () => serviceRows('<h1>Please wait</h1>', url, 200));
            rejects("HTTP failure", () => serviceRows('<table id="tableServicesList"></table>', url, 503));
            var traffic = trafficFields('<div class="top-info"><span class="used">111.92 MB</span> / 3000 GB</div>' +
                '<form action="clientarea.php?action=productdetails#tabChangepw"><input type="password"></form>', url, 200);
            check("traffic and product password form", !traffic.loggedOut && traffic.used === "111.92 MB" && traffic.total === "3000 GB");
            rejects("missing traffic is not zero", () => trafficFields('<h1>Suspended</h1>', url, 200));
            rejects("malformed summary", () => trafficFields('<div class="top-info"><span class="used">0 GB</span></div>', url, 200));
            __post({ checks: checks });
            """);

        var checks = dom.GetProperty("checks").EnumerateArray().Select(x => x.GetString()!).ToList();
        foreach (var (text, expected) in new[]
        {
            ("14.8 GB", 14.8), ("111.92 MB", 111.92 / 1024), ("1.5 TB", 1536d),
            ("1024 KB", 1d / 1024), ("1073741824 B", 1d), ("0 MB", 0d),
        })
        {
            if (Math.Abs(WhmcsLisaHostProvider.ParseSizeGB(text) - expected) > 1e-10)
                throw new InvalidOperationException($"Unit conversion failed: {text}");
            checks.Add($"units: {text}");
        }
        foreach (var invalid in new[] { "", "unknown", "-1 GB", "NaN GB", "14.8", "14.8 GB invalid" })
        {
            try
            {
                WhmcsLisaHostProvider.ParseSizeGB(invalid);
            }
            catch (InvalidOperationException)
            {
                checks.Add($"rejected invalid size: {invalid}");
                continue;
            }
            throw new InvalidOperationException($"Invalid size was accepted: {invalid}");
        }
        using var zero = JsonDocument.Parse("""{"used":"0 GB","total":"0 GB","online":true}""");
        var rejectedZero = false;
        try { WhmcsLisaHostProvider.ParseTraffic(zero.RootElement); }
        catch (InvalidOperationException) { rejectedZero = true; }
        if (!rejectedZero) throw new InvalidOperationException("Zero limit was accepted");
        checks.Add("zero limit cannot trigger a false low-traffic alert");
        return new { passed = checks.Count, checks };
    }
}
#endif

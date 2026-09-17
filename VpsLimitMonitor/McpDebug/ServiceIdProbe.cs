#if DEBUG
using System.Text;
using VpsLimitMonitor.Core;
using VpsLimitMonitor.Providers;
using VpsLimitMonitor.Settings;
using VpsLimitMonitor.Web;

namespace VpsLimitMonitor.McpDebug;

internal static class ServiceIdProbe
{
    public static object Run()
    {
        var config = new AccountConfig { Name = "__debug_service_ids_" + Guid.NewGuid().ToString("N") };
        var path = ServerNumberStore.GetFilePath(config.Name);
        if (File.Exists(path))
            throw new InvalidOperationException("Temporary ID file already exists");
        var checks = new List<string>();
        void Check(string name, bool passed)
        {
            if (!passed) throw new InvalidOperationException($"Server ID regression failed: {name}");
            checks.Add(name);
        }

        try
        {
            File.WriteAllText(path, "id\tip\nJP 01\t192.0.2.1\n01\t192.0.2.2\nid\t192.0.2.3\n", new UTF8Encoding(false));
            Check("load string with spaces", ServerNumberStore.Get(config, "192.0.2.1") == "JP 01");
            Check("legacy two-digit ID", ServerNumberStore.Get(config, "192.0.2.2") == "01");
            Check("literal id is not a header", ServerNumberStore.Get(config, "192.0.2.3") == "id");
            Check("IP mismatch stays unassigned", ServerNumberStore.Get(config, "192.0.2.4") == null);
            ServerNumberStore.Set(config, "192.0.2.4", "  香港备用  ");
            ServerNumberStore.Set(config, "192.0.2.5", "001");
            ServerNumberStore.Set(config, "192.0.2.6", "1000");
            ServerNumberStore.Initialize(SettingsManager.RoamingConfigDir, SettingsManager.Settings.Accounts);
            Check("Chinese ID survives reload", ServerNumberStore.Get(config, "192.0.2.4") == "香港备用");
            Check("leading zeros survive reload", ServerNumberStore.Get(config, "192.0.2.5") == "001");
            Check("no numeric range limit", ServerNumberStore.Get(config, "192.0.2.6") == "1000");
            Check("saving preserves other string IDs", ServerNumberStore.Get(config, "192.0.2.1") == "JP 01");
            var original = File.ReadAllBytes(path);
            foreach (var invalid in new[] { "JP\t01", "JP\n01", "JP\r01", "JP\0" })
            {
                var rejected = false;
                try { ServerNumberStore.Set(config, "192.0.2.1", invalid); }
                catch (ArgumentException) { rejected = true; }
                Check("control character rejected without changing file", rejected && File.ReadAllBytes(path).SequenceEqual(original));
            }
            var session = new WebSession("https://lisahost.com", config.Name);
            var account = new AccountState(config, session, new WhmcsLisaHostProvider(session));
            var service = new VpsService("test", "Example", "Example", "192.0.2.1", null);
            Check("title shows string ID verbatim", account.GetServiceTitle(service) == "JP 01 192.0.2.1");
            ServerNumberStore.Set(config, "192.0.2.1", null);
            Check("clear ID", account.GetServiceTitle(service) == "192.0.2.1");
            ServerNumberStore.Set(config, "192.0.2.4", "  ");
            Check("blank clears ID", ServerNumberStore.Get(config, "192.0.2.4") == null);
            Check("clearing preserves other entries", ServerNumberStore.Get(config, "192.0.2.5") == "001");
            return new { passed = checks.Count, checks };
        }
        finally
        {
            File.Delete(path);
            ServerNumberStore.Initialize(SettingsManager.RoamingConfigDir, SettingsManager.Settings.Accounts);
        }
    }
}
#endif

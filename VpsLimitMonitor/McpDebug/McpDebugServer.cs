#if DEBUG
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekTools;
using Microsoft.Extensions.Logging;
using VpsLimitMonitor.Core;
using VpsLimitMonitor.Providers;
using VpsLimitMonitor.Settings;
using VpsLimitMonitor.Update;
using ZLogger;

namespace VpsLimitMonitor.McpDebug;

/// <summary>
///     Debug 版专用的 MCP 调试接口，基于 JeekTools 的 McpHost + ObjectGraph：
///     标准工具（describe / get_value / set_value / invoke / list_members / read_logs）
///     加应用工具，通过命名管道监听（管道名含 worktree 实例 id），由
///     bin\VpsLimitMonitorMcp.exe stdio 适配器转发给 agent。
/// </summary>
public static class McpDebugServer
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(McpDebugServer));

    private static MonitorController _controller = null!;
    private static McpHost? _host;

    private static string WorkspaceRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (
                directory != null
                && !File.Exists(Path.Combine(directory.FullName, "VpsLimitMonitor.slnx"))
            )
            {
                directory = directory.Parent;
            }

            return directory?.FullName
                ?? throw new DirectoryNotFoundException(
                    $"Cannot find the VpsLimitMonitor workspace above '{AppContext.BaseDirectory}'."
                );
        }
    }

    public static void Start(MonitorController controller)
    {
        _controller = controller;

        var graph = new ObjectGraph(
            new ObjectGraphOptions
            {
                ResolveRoot = name => name switch
                {
                    "Controller" => _controller,
                    "Settings" => SettingsManager.Settings,
                    "App" => Application.Current
                        ?? throw new InvalidOperationException("App not initialized"),
                    _ => throw new InvalidOperationException(
                        $"Unknown root: {name}. Roots: Controller, Settings, App"
                    ),
                },
                RootNamesHelp = "Controller, Settings, App",
                FindNamedChild = (parent, name) =>
                    (parent as Visual)
                        ?.GetVisualDescendants()
                        .OfType<Control>()
                        .FirstOrDefault(c => c.Name == name),
            }
        );

        _host = new McpHost(
            new McpHostOptions
            {
                ServerName = "vpslimitmonitor-debug",
                ServerTitle = "VpsLimitMonitor Debug",
                Graph = graph,
                GetVersion = () => UpdateManager.LocalVersion.ToString(),
                PipeName = McpPipeNames.Debug(McpPipeNames.InstanceId(AppContext.BaseDirectory)),
                DefaultPort = 0,
                UiInvoker = async func =>
                    await Dispatcher
                        .UIThread.InvokeAsync(func)
                        .GetTask()
                        .WaitAsync(TimeSpan.FromSeconds(15)),
                Describe = () =>
                    "VPS 流量监视器调试接口。对象路径根：Controller（主控制器，含 Accounts/Alerts/Tray）、"
                    + "Settings（当前设置）、App（Avalonia Application）。"
                    + $"应用工具：{string.Join(", ", McpDebugContract.AppTools.Select(t => t.Name))}。",
                ToolListProvider = McpDebugContract.BuildToolList,
            }
        );

        foreach (var tool in McpDebugContract.AppTools)
        {
            var name = tool.Name;
            _host.AddTool(
                name,
                async args =>
                    McpHost.ToolText(
                        await Dispatcher
                            .UIThread.InvokeAsync(() => CallToolAsync(name, args))
                            .WaitAsync(TimeSpan.FromSeconds(120))
                    )
            );
        }

        _host.Start();
        WriteDiscoveryFile();
    }

    public static void Stop()
    {
        _host?.Stop();
        DeleteOwnedDiscoveryFile();
    }

    private static string DiscoveryFilePath =>
        Path.Combine(WorkspaceRoot, "bin", "debug-mcp.json");

    // 发现文件只用于人工排查（管道名、进程号），不再是连接的必要条件：
    // 适配器从自身目录推导同一个管道名。
    private static void WriteDiscoveryFile()
    {
        try
        {
            var executablePath =
                Environment.ProcessPath
                ?? throw new InvalidOperationException("The current executable path is unavailable.");
            SharedDataFile.WriteAllTextAtomic(
                DiscoveryFilePath,
                JsonSerializer.Serialize(
                    new McpDebugDiscovery
                    {
                        PipeName = _host?.PipeName ?? "",
                        ProcessId = Environment.ProcessId,
                        ExecutablePath = Path.GetFullPath(executablePath),
                        WorkspaceRoot = WorkspaceRoot,
                    },
                    new JsonSerializerOptions { WriteIndented = true }
                )
            );
        }
        catch (Exception ex)
        {
            Log.ZLogWarning($"Failed to update MCP discovery file: {ex.Message}");
        }
    }

    private static void DeleteOwnedDiscoveryFile()
    {
        try
        {
            if (!File.Exists(DiscoveryFilePath))
                return;

            var discovery = JsonSerializer.Deserialize<McpDebugDiscovery>(
                File.ReadAllText(DiscoveryFilePath)
            );
            if (discovery?.ProcessId == Environment.ProcessId)
                File.Delete(DiscoveryFilePath);
        }
        catch (Exception ex)
        {
            Log.ZLogWarning($"Failed to remove MCP discovery file: {ex.Message}");
        }
    }

    private static async Task<string> CallToolAsync(string name, JsonObject? args)
    {
        switch (name)
        {
            case "get_status":
                return BuildStatusJson();

            case "get_tray_icon":
                return BuildTrayIconJson();

            case "refresh":
                _controller.TriggerRefresh();
                await _controller.RefreshAllAsync();
                return BuildStatusJson();

            case "simulate_traffic":
                {
                    var account = FindAccount(args?["account"]?.GetValue<string>());
                    var serviceId = args?["serviceId"]?.GetValue<string>();
                    var svc =
                        (serviceId != null
                            ? account.Services.FirstOrDefault(s => s.Service.Id == serviceId)
                            : account.Services.FirstOrDefault())
                        ?? throw new InvalidOperationException("Service not found (先刷新一次以发现服务)");

                    var usedGB =
                        args?["usedGB"]?.GetValue<double>()
                        ?? throw new InvalidOperationException("usedGB is required");
                    var totalGB = args?["totalGB"]?.GetValue<double>() ?? svc.Traffic?.TotalGB ?? 1000;

                    svc.Traffic = new TrafficInfo(
                        usedGB,
                        totalGB,
                        svc.Traffic?.ResetNotice,
                        true
                    );
                    svc.LastUpdate = DateTime.Now;
                    svc.Simulated = true;
                    _controller.Alerts.Evaluate(account);
                    _controller.Tray.Update();
                    _controller.RebuildStatusWindow();
                    return BuildStatusJson();
                }

            case "simulate_due_date":
                {
                    var account = FindAccount(args?["account"]?.GetValue<string>());
                    var serviceId = args?["serviceId"]?.GetValue<string>();
                    var svc =
                        (serviceId != null
                            ? account.Services.FirstOrDefault(s => s.Service.Id == serviceId)
                            : account.Services.FirstOrDefault())
                        ?? throw new InvalidOperationException("Service not found (先刷新一次以发现服务)");

                    DateOnly due;
                    if (args?["daysFromNow"]?.GetValue<double>() is { } days)
                        due = DateOnly.FromDateTime(DateTime.Now).AddDays((int)days);
                    else if (
                        args?["date"]?.GetValue<string>() is { } text
                        && DateOnly.TryParseExact(text, "yyyy-MM-dd", out var parsed)
                    )
                        due = parsed;
                    else
                        throw new InvalidOperationException(
                            "daysFromNow or date (yyyy-MM-dd) is required"
                        );

                    svc.Service = svc.Service with { DueDate = due };
                    svc.Simulated = true;
                    svc.RenewalRemindedOn = null;
                    _controller.Alerts.Evaluate(account);
                    _controller.Tray.Update();
                    _controller.RebuildStatusWindow();
                    return BuildStatusJson();
                }

            case "simulate_services":
                {
                    var account = FindAccount(args?["account"]?.GetValue<string>());
                    var count = (int)(
                        args?["count"]?.GetValue<double>()
                        ?? throw new InvalidOperationException("count is required")
                    );

                    var rnd = new Random(42);
                    for (var i = 0; i < count; i++)
                    {
                        var used = Math.Round(rnd.NextDouble() * 500, 1);
                        account.Services.Add(
                            new ServiceState(
                                new VpsService(
                                    $"sim-{i}",
                                    "模拟套餐",
                                    $"SIM-{i:D2}",
                                    $"10.0.0.{i + 1}",
                                    DateOnly.FromDateTime(DateTime.Now).AddDays(3 + i)
                                )
                            )
                            {
                                Traffic = new TrafficInfo(used, 500, "2026-08-01 00:00", true),
                                LastUpdate = DateTime.Now,
                                Simulated = true,
                            }
                        );
                    }
                    _controller.Tray.Update();
                    _controller.RebuildStatusWindow();
                    return BuildStatusJson();
                }

            case "clear_simulation":
                foreach (var acc in _controller.Accounts)
                {
                    acc.SimulateExpired = false;
                    foreach (var svc in acc.Services)
                        svc.Simulated = false;
                }
                _controller.Stock.Simulated = false;
                _controller.TriggerRefresh();
                _controller.Stock.TriggerCheck();
                return "Simulation cleared, refresh triggered";

            case "simulate_session_expired":
                {
                    var account = FindAccount(args?["account"]?.GetValue<string>());
                    account.SimulateExpired = true;
                    account.LoggedIn = false;
                    account.LoginNotified = false;
                    _controller.Alerts.NotifyLoginNeeded(account);
                    _controller.Tray.Update();
                    _controller.RebuildStatusWindow();
                    return BuildStatusJson();
                }

            case "show_login_window":
                {
                    var account = FindAccount(args?["account"]?.GetValue<string>());
                    await _controller.OpenSiteAsync(account);
                    return "Login window shown";
                }

            case "open_service_window":
                {
                    var account = FindAccount(args?["account"]?.GetValue<string>());
                    var serviceId = args?["serviceId"]?.GetValue<string>();
                    var service =
                        (serviceId == null
                            ? account.Services.FirstOrDefault()
                            : account.Services.FirstOrDefault(s => s.Service.Id == serviceId))
                        ?? throw new InvalidOperationException("Service not found");
                    await _controller.OpenServiceAsync(account, service.Service);
                    return JsonSerializer.Serialize(account.Session.BrowserWindows, PrettyJson);
                }

            case "get_browser_windows":
                return JsonSerializer.Serialize(
                    _controller.Accounts.ToDictionary(a => a.Config.Name, a => a.Session.BrowserWindows),
                    PrettyJson
                );

            case "set_settings":
                {
                    if (args?["pollIntervalMinutes"]?.GetValue<double>() is { } interval)
                        SettingsManager.Settings.PollIntervalMinutes = (int)interval;
                    if (args?["alertRemainingPercent"]?.GetValue<double>() is { } threshold)
                        SettingsManager.Settings.AlertRemainingPercent = threshold;
                    SettingsManager.Save();
                    return JsonSerializer.Serialize(SettingsManager.Settings);
                }

            case "get_storage_info":
                return JsonSerializer.Serialize(
                    new
                    {
                        location = SettingsManager.Location.ToString(),
                        roamingConfigDir = SettingsManager.RoamingConfigDir,
                        customDir = SettingsManager.CustomDir,
                        localConfigDir = SettingsManager.Storage.LocalConfigDir,
                        programConfigDir = SettingsManager.Storage.ProgramConfigDir,
                        isPortableForced = SettingsManager.Storage.IsPortable,
                    },
                    PrettyJson
                );

            case "set_storage_mode":
                {
                    var locationText =
                        args?["location"]?.GetValue<string>()
                        ?? throw new InvalidOperationException("location is required");
                    if (!Enum.TryParse<StorageLocation>(locationText, true, out var location))
                        throw new InvalidOperationException(
                            "location must be UserDirectory | ProgramDirectory | CustomDirectory"
                        );

                    var customDir = args?["customDir"]?.GetValue<string>();
                    if (location == StorageLocation.CustomDirectory && string.IsNullOrEmpty(customDir))
                        throw new InvalidOperationException(
                            "customDir is required for CustomDirectory"
                        );

                    var moveFiles = args?["moveFiles"]?.GetValue<bool>() ?? true;
                    SettingsManager.SwitchStorageLocation(location, customDir, moveFiles);
                    _controller.Tray.UpdateStorageChecks();
                    return $"Storage switched to {SettingsManager.Location}: {SettingsManager.RoamingConfigDir}";
                }

            case "get_cookies":
                {
                    var account = FindAccount(args?["account"]?.GetValue<string>());
                    var cookies = await account.Session.GetCookiesAsync();
                    return JsonSerializer.Serialize(cookies, PrettyJson);
                }

            case "fetch_url":
                {
                    var account = FindAccount(args?["account"]?.GetValue<string>());
                    var url =
                        args?["url"]?.GetValue<string>()
                        ?? throw new InvalidOperationException("url is required");
                    var maxLength = (int)(args?["maxLength"]?.GetValue<double>() ?? 3000);

                    await account.Session.EnsureInitializedAsync();
                    var res = await account.Session.FetchAsync(url);
                    var body =
                        maxLength > 0 && res.Body.Length > maxLength
                            ? res.Body[..maxLength] + $"\n...(truncated, total {res.Body.Length})"
                            : res.Body;
                    return $"Status: {res.Status}\nUrl: {res.Url}\n\n{body}";
                }

            case "check_stock":
                await _controller.Stock.CheckAsync();
                return BuildStockJson();

            case "simulate_stock":
                {
                    var stock = _controller.Stock;
                    if (stock.Plans.Count == 0)
                        await stock.CheckAsync();
                    if (stock.Plans.Count == 0)
                        stock.Plans.Add(new StockPlan("模拟套餐", false));

                    var planName = args?["plan"]?.GetValue<string>();
                    var plan =
                        (planName != null
                            ? stock.Plans.FirstOrDefault(p =>
                                p.Name.Contains(planName, StringComparison.OrdinalIgnoreCase)
                            )
                            : stock.Plans.FirstOrDefault())
                        ?? throw new InvalidOperationException($"Plan not found: {planName}");

                    stock.Simulated = true;
                    plan.InStock = true;
                    plan.Alerted = false;
                    stock.EvaluateAlerts();
                    _controller.RebuildStatusWindow();
                    return BuildStockJson();
                }

            case "get_alerts":
                return _controller.Alerts.RecentAlerts.Count == 0
                    ? "(no alerts)"
                    : string.Join("\n", _controller.Alerts.RecentAlerts);

            case "check_update":
                {
                    if (args?["baseUrl"]?.GetValue<string>() is { } baseUrl)
                        UpdateManager.OverrideBaseUrl = baseUrl == "" ? null : baseUrl;
                    var apply = args?["apply"]?.GetValue<bool>() ?? false;
                    var result = await UpdateManager.CheckForUpdateAsync(apply);
                    return $"Result: {result}\nLocalVersion: {UpdateManager.LocalVersion}\n"
                        + $"Updating: {UpdateManager.Updating}";
                }

            case "get_update_status":
                return JsonSerializer.Serialize(
                    new
                    {
                        localVersion = UpdateManager.LocalVersion,
                        localVersionText = UpdateManager.LocalVersionText,
                        overrideBaseUrl = UpdateManager.OverrideBaseUrl,
                        updateCheckInterval = SettingsManager.Settings.UpdateCheckInterval,
                        lastCheckResult = UpdateManager.LastCheckResult,
                        updating = UpdateManager.Updating,
                    },
                    PrettyJson
                );

            default:
                throw new InvalidOperationException($"Unknown tool: {name}");
        }
    }

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static AccountState FindAccount(string? name)
    {
        if (name == null)
            return _controller.Accounts.FirstOrDefault()
                ?? throw new InvalidOperationException("No accounts configured");

        return _controller.Accounts.FirstOrDefault(a =>
                string.Equals(a.Config.Name, name, StringComparison.OrdinalIgnoreCase)
            ) ?? throw new InvalidOperationException($"Account not found: {name}");
    }

    private static string BuildStockJson()
    {
        var stock = _controller.Stock;
        return JsonSerializer.Serialize(
            new
            {
                enabled = SettingsManager.Settings.StockMonitorEnabled,
                url = SettingsManager.Settings.StockMonitorUrl,
                intervalMinutes = SettingsManager.Settings.StockMonitorIntervalMinutes,
                lastCheck = stock.LastCheck?.ToString("yyyy-MM-dd HH:mm:ss"),
                error = stock.Error,
                simulated = stock.Simulated,
                anyInStock = stock.AnyInStock,
                plans = stock.Plans.Select(p => new
                {
                    name = p.Name,
                    inStock = p.InStock,
                    alerted = p.Alerted,
                }),
            },
            PrettyJson
        );
    }

    private static string BuildStatusJson()
    {
        var payload = new
        {
            lastRefresh = _controller.LastRefresh?.ToString("yyyy-MM-dd HH:mm:ss"),
            refreshing = _controller.Refreshing,
            serverCount = _controller.ServerCount,
            serverCountText = _controller.ServerCountText,
            tray = new
            {
                displayText = _controller.Tray.DisplayText,
                toolTipText = _controller.Tray.ToolTipText,
                backgroundColor = _controller.Tray.BackgroundColor,
            },
            settings = SettingsManager.Settings,
            storageLocation = SettingsManager.Location.ToString(),
            accounts = _controller.Accounts.Select(a => new
            {
                name = a.Config.Name,
                baseUrl = a.Config.BaseUrl,
                loggedIn = a.LoggedIn,
                simulateExpired = a.SimulateExpired,
                error = a.Error,
                lastPoll = a.LastPoll?.ToString("yyyy-MM-dd HH:mm:ss"),
                services = a.Services.Select(s => new
                {
                    id = s.Service.Id,
                    label = s.Service.Label,
                    name = s.Service.Name,
                    ip = s.Service.Ip,
                    dueDate = s.Service.DueDate?.ToString("yyyy-MM-dd"),
                    renewalRemindedOn = s.RenewalRemindedOn?.ToString("yyyy-MM-dd"),
                    usedGB = s.Traffic?.UsedGB,
                    usedPercent = s.Traffic?.UsedPercent,
                    totalGB = s.Traffic?.TotalGB,
                    remainingGB = s.Traffic?.RemainingGB,
                    remainingPercent = s.Traffic?.RemainingPercent,
                    resetNotice = s.Traffic?.ResetNotice,
                    online = s.Traffic?.IsOnline,
                    lastUpdate = s.LastUpdate?.ToString("yyyy-MM-dd HH:mm:ss"),
                    error = s.Error,
                    alerted = s.Alerted,
                    simulated = s.Simulated,
                }),
            }),
            stock = new
            {
                enabled = SettingsManager.Settings.StockMonitorEnabled,
                lastCheck = _controller.Stock.LastCheck?.ToString("yyyy-MM-dd HH:mm:ss"),
                error = _controller.Stock.Error,
                simulated = _controller.Stock.Simulated,
                plans = _controller.Stock.Plans.Select(p => new
                {
                    name = p.Name,
                    inStock = p.InStock,
                }),
            },
            recentAlerts = _controller.Alerts.RecentAlerts.TakeLast(10),
        };

        return JsonSerializer.Serialize(payload, PrettyJson);
    }

    private static string BuildTrayIconJson()
    {
        var tray = _controller.Tray;
        return JsonSerializer.Serialize(
            new
            {
                displayText = tray.DisplayText,
                toolTipText = tray.ToolTipText,
                backgroundColor = tray.BackgroundColor,
                pngBase64 = Convert.ToBase64String(tray.IconPng),
            },
            PrettyJson
        );
    }
}
#endif

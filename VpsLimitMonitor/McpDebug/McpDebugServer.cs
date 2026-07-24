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
///     Debug 版专用的 MCP 调试接口，基于 JeekTools 的 DebugMcpHost + ObjectGraph：
///     标准工具（describe / get_value / set_value / invoke / list_members / read_logs）
///     加应用工具，端口从 28217 起扫描并写发现文件。
/// </summary>
public static class McpDebugServer
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(McpDebugServer));

    public const int DefaultPort = 28217;

    private static MonitorController _controller = null!;
    private static DebugMcpHost? _host;

    private static string DiscoveryDir =>
        Path.Combine(SettingsManager.Storage.LocalDir, "DebugMcp");

    private record ToolDef(string Name, string Description, JsonObject InputSchema);

    private static readonly List<ToolDef> AppTools =
    [
        new(
            "get_status",
            "获取全部账号、服务、流量、报警与托盘状态",
            EmptySchema()
        ),
        new(
            "refresh",
            "立即执行一次完整轮询并返回最新状态",
            EmptySchema()
        ),
        new(
            "simulate_traffic",
            "注入模拟流量数据（触发报警逻辑），serviceId 省略时作用于第一个服务",
            Schema(
                ("account", "string", "账号名，省略为第一个账号"),
                ("serviceId", "string", "服务 ID，省略为该账号第一个服务"),
                ("usedGB", "number", "已用流量 GB（必填）"),
                ("totalGB", "number", "总流量 GB，省略保持原值")
            )
        ),
        new(
            "simulate_due_date",
            "注入模拟到期时间（触发续费提醒逻辑），serviceId 省略时作用于第一个服务。持续到 clear_simulation",
            Schema(
                ("account", "string", "账号名，省略为第一个账号"),
                ("serviceId", "string", "服务 ID，省略为该账号第一个服务"),
                ("daysFromNow", "number", "距今天数（可为负表示已过期），与 date 二选一"),
                ("date", "string", "到期日期 yyyy-MM-dd，与 daysFromNow 二选一")
            )
        ),
        new(
            "simulate_services",
            "注入 N 台模拟服务器（测试面板多列布局），持续到 clear_simulation",
            Schema(
                ("account", "string", "账号名，省略为第一个账号"),
                ("count", "number", "注入数量（必填）")
            )
        ),
        new(
            "clear_simulation",
            "清除所有模拟数据并触发真实刷新",
            EmptySchema()
        ),
        new(
            "simulate_session_expired",
            "模拟会话失效（触发登录提醒流程），刷新时该账号持续按失效处理，直到 clear_simulation",
            Schema(("account", "string", "账号名，省略为第一个账号"))
        ),
        new(
            "show_login_window",
            "用内置浏览器打开指定账号的网站（未登录时即登录窗口）",
            Schema(("account", "string", "账号名，省略为第一个账号"))
        ),
        new(
            "set_settings",
            "修改轮询间隔或报警阈值",
            Schema(
                ("pollIntervalMinutes", "number", "轮询间隔（分钟）"),
                ("alertRemainingPercent", "number", "剩余流量报警阈值（百分比）")
            )
        ),
        new(
            "get_alerts",
            "获取最近的报警记录",
            EmptySchema()
        ),
        new(
            "check_update",
            "检查自动更新。baseUrl 可覆盖下载地址（以 / 结尾，用于本地模拟发布）；apply 为 true 时发现新版本会真正下载、退出并重启程序",
            Schema(
                ("baseUrl", "string", "覆盖版本与 zip 的下载基地址（调试用）"),
                ("apply", "boolean", "是否实际执行更新，默认 false 仅检查")
            )
        ),
        new(
            "get_update_status",
            "获取本地版本号、更新设置与最近一次检查结果",
            EmptySchema()
        ),
        new(
            "get_storage_info",
            "获取配置存储模式与各候选目录",
            EmptySchema()
        ),
        new(
            "set_storage_mode",
            "切换配置存储模式（绕过 UI 对话框，测试用）",
            Schema(
                ("location", "string", "UserDirectory | ProgramDirectory | CustomDirectory"),
                ("customDir", "string", "自定义基目录，location 为 CustomDirectory 时必填"),
                ("moveFiles", "boolean", "是否移动现有 Config 目录，默认 true")
            )
        ),
        new(
            "get_cookies",
            "列出账号会话当前站点的 cookie（名称/域/是否会话级/过期时间，调试用）",
            Schema(("account", "string", "账号名，省略为第一个账号"))
        ),
        new(
            "check_stock",
            "立即执行一次库存检查并返回库存状态（不受开关限制）",
            EmptySchema()
        ),
        new(
            "simulate_stock",
            "注入模拟库存（触发放货提醒），plan 省略时标记第一个套餐有货；无真实数据时先自动检查一次。持续到 clear_simulation",
            Schema(("plan", "string", "套餐名（子串匹配），省略为第一个套餐"))
        ),
        new(
            "fetch_url",
            "用账号会话 fetch 一个同站 URL，返回状态、最终 URL 和响应体（调试用）",
            Schema(
                ("account", "string", "账号名，省略为第一个账号"),
                ("url", "string", "相对或绝对 URL（必填）"),
                ("maxLength", "number", "响应体截断长度，默认 3000，0 为不截断")
            )
        ),
    ];

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

        _host = new DebugMcpHost(
            new DebugMcpHostOptions
            {
                ServerName = "vpslimitmonitor-debug",
                ServerTitle = "VpsLimitMonitor Debug",
                Graph = graph,
                GetVersion = () => UpdateManager.LocalVersion.ToString(),
                DefaultPort = DefaultPort,
                PortScanCount = 20,
                PortEnvironmentVariable = "VPSLIMITMONITOR_MCP_PORT",
                UiInvoker = async func =>
                    await Dispatcher
                        .UIThread.InvokeAsync(func)
                        .GetTask()
                        .WaitAsync(TimeSpan.FromSeconds(15)),
                Describe = () =>
                    "VPS 流量监视器调试接口。对象路径根：Controller（主控制器，含 Accounts/Alerts/Tray）、"
                    + "Settings（当前设置）、App（Avalonia Application）。"
                    + $"应用工具：{string.Join(", ", AppTools.Select(t => t.Name))}。",
                ToolListProvider = BuildToolList,
                UrlChanged = OnUrlChanged,
            }
        );

        foreach (var tool in AppTools)
        {
            var name = tool.Name;
            _host.AddTool(
                name,
                async args =>
                    DebugMcpHost.ToolText(
                        await Dispatcher
                            .UIThread.InvokeAsync(() => CallToolAsync(name, args))
                            .WaitAsync(TimeSpan.FromSeconds(120))
                    )
            );
        }

        CleanStaleDiscoveryFiles();
        _host.Start();
    }

    public static void Stop()
    {
        _host?.Stop();
    }

    private static string DiscoveryFilePath =>
        Path.Combine(DiscoveryDir, $"{Environment.ProcessId}.json");

    private static void OnUrlChanged(string url)
    {
        try
        {
            if (url == "")
            {
                File.Delete(DiscoveryFilePath);
                return;
            }

            Directory.CreateDirectory(DiscoveryDir);
            File.WriteAllText(
                DiscoveryFilePath,
                JsonSerializer.Serialize(
                    new
                    {
                        url,
                        pid = Environment.ProcessId,
                        baseDirectory = AppContext.BaseDirectory,
                        startTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
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

    private static void CleanStaleDiscoveryFiles()
    {
        try
        {
            if (!Directory.Exists(DiscoveryDir))
                return;

            foreach (var file in Directory.GetFiles(DiscoveryDir, "*.json"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var pid))
                    continue;

                try
                {
                    System.Diagnostics.Process.GetProcessById(pid);
                }
                catch (ArgumentException)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 清理失败不影响启动
        }
    }

    private static JsonArray BuildToolList()
    {
        var tools = new JsonArray();

        void Add(string name, string description, JsonObject schema)
        {
            tools.Add(
                new JsonObject
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["inputSchema"] = schema,
                }
            );
        }

        Add("describe", "描述本调试接口、对象路径根与可用工具", EmptySchema());
        Add(
            "get_value",
            "按对象路径读取值，如 Controller.Accounts[0].LoggedIn",
            Schema(
                ("path", "string", "对象路径（必填），根：Controller / Settings / App"),
                ("depth", "number", "展开深度 0-5，默认 1")
            )
        );
        Add(
            "set_value",
            "按对象路径写入属性、字段或列表元素",
            Schema(
                ("path", "string", "对象路径（必填）"),
                ("value", "string", "新值（JSON，任意类型）")
            )
        );
        Add(
            "invoke",
            "按对象路径调用方法或 ICommand（在 UI 线程上执行）",
            Schema(
                ("path", "string", "方法路径（必填），如 Controller.TriggerRefresh"),
                ("args", "array", "参数数组，支持 {\"$path\": ...} 活对象引用"),
                ("depth", "number", "结果展开深度 0-5，默认 1")
            )
        );
        Add(
            "list_members",
            "列出对象路径处的成员（属性、字段、方法）",
            Schema(("path", "string", "对象路径（必填）"))
        );
        Add(
            "read_logs",
            "读取应用日志尾部",
            Schema(
                ("lines", "number", "行数，默认 200，最大 2000"),
                ("filter", "string", "只保留包含此子串的行")
            )
        );

        foreach (var tool in AppTools)
            Add(tool.Name, tool.Description, (JsonObject)tool.InputSchema.DeepClone());

        return tools;
    }

    private static async Task<string> CallToolAsync(string name, JsonObject? args)
    {
        switch (name)
        {
            case "get_status":
                return BuildStatusJson();

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

    private static JsonObject EmptySchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
        };
    }

    private static JsonObject Schema(params (string Name, string Type, string Description)[] props)
    {
        var properties = new JsonObject();
        foreach (var (propName, type, description) in props)
            properties[propName] = new JsonObject
            {
                ["type"] = type,
                ["description"] = description,
            };

        return new JsonObject { ["type"] = "object", ["properties"] = properties };
    }
}
#endif

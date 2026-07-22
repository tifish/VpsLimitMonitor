#if DEBUG
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using JeekTools;
using Microsoft.Extensions.Logging;
using VpsLimitMonitor.Core;
using VpsLimitMonitor.Update;
using VpsLimitMonitor.Providers;
using VpsLimitMonitor.Settings;
using ZLogger;

namespace VpsLimitMonitor.McpDebug;

/// <summary>
///     Debug 版专用的 MCP 调试接口（Streamable HTTP，仅 127.0.0.1）。
///     供 AI 查询内部状态、强制刷新、注入模拟流量来测试报警等。
/// </summary>
public static class McpDebugServer
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(McpDebugServer));

    public const int Port = 28217;

    private static MonitorController _controller = null!;
    private static HttpListener? _listener;

    private record ToolDef(string Name, string Description, JsonObject InputSchema);

    private static readonly List<ToolDef> Tools =
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
            "clear_simulation",
            "清除所有模拟数据并触发真实刷新",
            EmptySchema()
        ),
        new(
            "simulate_session_expired",
            "模拟会话失效（触发登录提醒流程）",
            Schema(("account", "string", "账号名，省略为第一个账号"))
        ),
        new(
            "show_login_window",
            "弹出指定账号的登录窗口",
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
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/mcp/");

        try
        {
            _listener.Start();
        }
        catch (Exception ex)
        {
            Log.ZLogError($"MCP debug server failed to start: {ex.Message}");
            return;
        }

        _ = Task.Run(ListenLoopAsync);
        Log.ZLogInformation($"MCP debug server listening on http://127.0.0.1:{Port}/mcp");
    }

    private static async Task ListenLoopAsync()
    {
        while (_listener is { IsListening: true })
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(context));
        }
    }

    private static async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;

            if (request.HttpMethod == "DELETE")
            {
                response.StatusCode = 200;
                response.Close();
                return;
            }

            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                response.Close();
                return;
            }

            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var message = JsonNode.Parse(body)!.AsObject();

            var method = message["method"]?.GetValue<string>() ?? "";
            var id = message["id"];

            // 通知消息无需应答内容
            if (id == null)
            {
                response.StatusCode = 202;
                response.Close();
                return;
            }

            JsonNode result;
            try
            {
                result = await DispatchAsync(method, message["params"]?.AsObject());
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(
                    response,
                    new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id.DeepClone(),
                        ["error"] = new JsonObject
                        {
                            ["code"] = -32603,
                            ["message"] = ex.Message,
                        },
                    }
                );
                return;
            }

            await WriteJsonAsync(
                response,
                new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id.DeepClone(),
                    ["result"] = result,
                }
            );
        }
        catch (Exception ex)
        {
            Log.ZLogError($"MCP request failed: {ex.Message}");
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
                // ignored
            }
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, JsonObject payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        response.StatusCode = 200;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static async Task<JsonNode> DispatchAsync(string method, JsonObject? params_)
    {
        switch (method)
        {
            case "initialize":
                return new JsonObject
                {
                    ["protocolVersion"] =
                        params_?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "VpsLimitMonitor Debug",
                        ["version"] = "1.0",
                    },
                };

            case "ping":
                return new JsonObject();

            case "tools/list":
            {
                var tools = new JsonArray();
                foreach (var tool in Tools)
                    tools.Add(
                        new JsonObject
                        {
                            ["name"] = tool.Name,
                            ["description"] = tool.Description,
                            ["inputSchema"] = tool.InputSchema.DeepClone(),
                        }
                    );
                return new JsonObject { ["tools"] = tools };
            }

            case "tools/call":
            {
                var name = params_?["name"]?.GetValue<string>() ?? "";
                var args = params_?["arguments"]?.AsObject();
                var text = await Dispatcher.UIThread.InvokeAsync(() => CallToolAsync(name, args));
                return new JsonObject
                {
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = text }
                    ),
                };
            }

            default:
                throw new InvalidOperationException($"Unknown method: {method}");
        }
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

            case "clear_simulation":
                foreach (var acc in _controller.Accounts)
                foreach (var svc in acc.Services)
                    svc.Simulated = false;
                _controller.TriggerRefresh();
                return "Simulation cleared, refresh triggered";

            case "simulate_session_expired":
            {
                var account = FindAccount(args?["account"]?.GetValue<string>());
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
                await _controller.ShowLoginAsync(account);
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
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System
                            .Text
                            .Encodings
                            .Web
                            .JavaScriptEncoder
                            .UnsafeRelaxedJsonEscaping,
                    }
                );

            default:
                throw new InvalidOperationException($"Unknown tool: {name}");
        }
    }

    private static AccountState FindAccount(string? name)
    {
        if (name == null)
            return _controller.Accounts.FirstOrDefault()
                ?? throw new InvalidOperationException("No accounts configured");

        return _controller.Accounts.FirstOrDefault(a =>
                string.Equals(a.Config.Name, name, StringComparison.OrdinalIgnoreCase)
            ) ?? throw new InvalidOperationException($"Account not found: {name}");
    }

    private static string BuildStatusJson()
    {
        var payload = new
        {
            lastRefresh = _controller.LastRefresh?.ToString("yyyy-MM-dd HH:mm:ss"),
            refreshing = _controller.Refreshing,
            settings = SettingsManager.Settings,
            accounts = _controller.Accounts.Select(a => new
            {
                name = a.Config.Name,
                baseUrl = a.Config.BaseUrl,
                loggedIn = a.LoggedIn,
                error = a.Error,
                lastPoll = a.LastPoll?.ToString("yyyy-MM-dd HH:mm:ss"),
                services = a.Services.Select(s => new
                {
                    id = s.Service.Id,
                    label = s.Service.Label,
                    name = s.Service.Name,
                    ip = s.Service.Ip,
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
            recentAlerts = _controller.Alerts.RecentAlerts.TakeLast(10),
        };

        return JsonSerializer.Serialize(
            payload,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }
        );
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

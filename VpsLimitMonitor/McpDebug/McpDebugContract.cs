using System.Text.Json.Nodes;

namespace VpsLimitMonitor.McpDebug;

public sealed class McpDebugDiscovery
{
    public string PipeName { get; set; } = "";
    public int ProcessId { get; set; }
    public string ExecutablePath { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
}

public sealed record McpDebugTool(string Name, string Description, JsonObject InputSchema);

public static class McpDebugContract
{
    public static IReadOnlyList<McpDebugTool> AppTools { get; } =
    [
        Tool("get_status", "Get accounts, services, traffic, alerts, and settings."),
        Tool("get_tray_icon", "Get the rendered tray icon and its display properties."),
        Tool("refresh", "Run a full poll immediately and return the latest status."),
        Tool(
            "simulate_traffic",
            "Inject traffic data until clear_simulation.",
            [
                Prop("account", "string", "Account name; defaults to the first account."),
                Prop("serviceId", "string", "Service ID; defaults to the first service."),
                Prop("usedGB", "number", "Used traffic in GB."),
                Prop("totalGB", "number", "Total traffic in GB."),
            ],
            ["usedGB"]
        ),
        Tool(
            "simulate_due_date",
            "Inject a due date until clear_simulation.",
            [
                Prop("account", "string", "Account name; defaults to the first account."),
                Prop("serviceId", "string", "Service ID; defaults to the first service."),
                Prop("daysFromNow", "number", "Days relative to today."),
                Prop("date", "string", "Date in yyyy-MM-dd format."),
            ]
        ),
        Tool(
            "simulate_services",
            "Inject fake services to test the multi-column status layout.",
            [
                Prop("account", "string", "Account name; defaults to the first account."),
                Prop("count", "number", "Number of services to inject."),
            ],
            ["count"]
        ),
        Tool("clear_simulation", "Clear all simulated data and poll real data."),
        Tool(
            "simulate_session_expired",
            "Treat an account session as expired until clear_simulation.",
            [Prop("account", "string", "Account name; defaults to the first account.")]
        ),
        Tool(
            "show_login_window",
            "Open the WebView2 login window for an account.",
            [Prop("account", "string", "Account name; defaults to the first account.")]
        ),
        Tool(
            "set_settings",
            "Change polling and traffic alert settings.",
            [
                Prop("pollIntervalMinutes", "number", "Polling interval in minutes."),
                Prop("alertRemainingPercent", "number", "Remaining-traffic alert threshold."),
            ]
        ),
        Tool("get_alerts", "Get recent toast alert records."),
        Tool(
            "check_update",
            "Check for an update and optionally apply it.",
            [
                Prop("baseUrl", "string", "Optional release download base URL."),
                Prop("apply", "boolean", "Download and apply an available update."),
            ]
        ),
        Tool("get_update_status", "Get local version and the latest update-check status."),
        Tool("get_storage_info", "Get the active settings storage mode and candidate paths."),
        Tool(
            "set_storage_mode",
            "Switch settings storage without UI dialogs.",
            [
                Prop(
                    "location",
                    "string",
                    "UserDirectory, ProgramDirectory, or CustomDirectory."
                ),
                Prop("customDir", "string", "Required for CustomDirectory."),
                Prop("moveFiles", "boolean", "Move current files; defaults to true."),
            ],
            ["location"]
        ),
        Tool(
            "get_cookies",
            "List cookies for an account site.",
            [Prop("account", "string", "Account name; defaults to the first account.")]
        ),
        Tool("check_stock", "Run a stock check immediately."),
        Tool(
            "simulate_stock",
            "Mark a plan as in stock until clear_simulation.",
            [Prop("plan", "string", "Plan-name substring; defaults to the first plan.")]
        ),
        Tool(
            "fetch_url",
            "Fetch a same-origin URL with an account session.",
            [
                Prop("account", "string", "Account name; defaults to the first account."),
                Prop("url", "string", "Relative or absolute URL."),
                Prop("maxLength", "number", "Response-body limit; defaults to 3000."),
            ],
            ["url"]
        ),
    ];

    public static JsonArray BuildToolList()
    {
        var tools = new JsonArray
        {
            ToolNode("describe", "Describe the debug interface and object roots."),
            ToolNode(
                "get_value",
                "Read a value by object path.",
                [
                    Prop("path", "string", "Path rooted at Controller, Settings, or App."),
                    Prop("depth", "number", "Expansion depth from 0 to 5."),
                ],
                ["path"]
            ),
            ToolNode(
                "set_value",
                "Write a property, field, or list item by object path.",
                [
                    Prop("path", "string", "Object path to write."),
                    new KeyValuePair<string, JsonNode?>(
                        "value",
                        new JsonObject { ["description"] = "New JSON value." }
                    ),
                ],
                ["path", "value"]
            ),
            ToolNode(
                "invoke",
                "Invoke a method or ICommand on the UI thread.",
                [
                    Prop("path", "string", "Method or command path."),
                    new KeyValuePair<string, JsonNode?>(
                        "args",
                        new JsonObject { ["type"] = "array", ["description"] = "JSON arguments." }
                    ),
                    Prop("depth", "number", "Result expansion depth from 0 to 5."),
                ],
                ["path"]
            ),
            ToolNode(
                "list_members",
                "List properties, fields, and methods at an object path.",
                [Prop("path", "string", "Object path to inspect.")],
                ["path"]
            ),
            ToolNode(
                "read_logs",
                "Read the application log tail.",
                [
                    Prop("lines", "number", "Number of lines; defaults to 200."),
                    Prop("filter", "string", "Optional substring filter."),
                ]
            ),
        };

        foreach (var tool in AppTools)
        {
            tools.Add(
                new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["inputSchema"] = tool.InputSchema.DeepClone(),
                }
            );
        }

        return tools;
    }

    private static McpDebugTool Tool(
        string name,
        string description,
        KeyValuePair<string, JsonNode?>[]? properties = null,
        string[]? required = null
    )
    {
        return new McpDebugTool(name, description, Schema(properties, required));
    }

    private static JsonObject ToolNode(
        string name,
        string description,
        KeyValuePair<string, JsonNode?>[]? properties = null,
        string[]? required = null
    )
    {
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = Schema(properties, required),
        };
    }

    private static JsonObject Schema(
        KeyValuePair<string, JsonNode?>[]? properties,
        string[]? required
    )
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(properties ?? []),
        };
        if (required is { Length: > 0 })
        {
            schema["required"] = new JsonArray(
                required.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()
            );
        }
        return schema;
    }

    private static KeyValuePair<string, JsonNode?> Prop(
        string name,
        string type,
        string description
    )
    {
        return new KeyValuePair<string, JsonNode?>(
            name,
            new JsonObject { ["type"] = type, ["description"] = description }
        );
    }
}

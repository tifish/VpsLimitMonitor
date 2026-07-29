using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using VpsLimitMonitor.McpDebug;

// VpsLimitMonitor MCP stdio 适配器。
//
// agent 把本可执行文件当作普通 stdio MCP server 启动，它把 JSON-RPC 经命名管道转发给
// 正在运行的程序。这里不涉及端口，客户端配置永不过期：
//
//   { "command": "cmd", "args": ["/c", ".\\bin\\VpsLimitMonitorMcp.exe", "--surface", "debug"] }
//
// 适配器与程序同目录，从自身目录推导实例 id，因此只会连到同目录的那个程序——
// 并行 Debug worktree 互不串线。

var options = AdapterOptions.Parse(args);

using var stdin = new StreamReader(Console.OpenStandardInput(), AdapterText.Utf8);
await using var stdout = new StreamWriter(Console.OpenStandardOutput(), AdapterText.Utf8) { AutoFlush = true };

using var connection = new PipeConnection(options);

while (await stdin.ReadLineAsync().ConfigureAwait(false) is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
        continue;

    JsonNode? message;
    try
    {
        message = JsonNode.Parse(line);
    }
    catch (Exception ex)
    {
        await stdout.WriteLineAsync(
            AdapterText.RpcError(null, -32700, $"Parse error: {ex.Message}").ToJsonString()).ConfigureAwait(false);
        continue;
    }

    if (message is not null)
        await HandleAsync(message).ConfigureAwait(false);
}

async Task HandleAsync(JsonNode message)
{
    var envelope = message as JsonObject;
    var method = envelope?["method"]?.GetValue<string>();
    var id = envelope?["id"]?.DeepClone();

    // 只有真正的工具调用才值得把 GUI 拉起来：MCP 客户端在会话开始时就会打开 stdio
    // server，若每次会话开始都弹窗口就太扰人了。
    var mayLaunch = options.AutoLaunch && method == "tools/call";

    string? response;
    try
    {
        response = await connection
            .SendAsync(message, AdapterText.ExpectsResponse(message), mayLaunch)
            .ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        await stdout.WriteLineAsync(OfflineResponse(method, id, ex.Message).ToJsonString()).ConfigureAwait(false);
        return;
    }

    if (response is not null)
        await stdout.WriteLineAsync(response).ConfigureAwait(false);
}

// 程序不可达时保持会话可用而不是让握手失败：客户端保持连接状态，
// 只有真正的工具调用才报告原因。
JsonNode OfflineResponse(string? method, JsonNode? id, string reason) => method switch
{
    "initialize" => AdapterText.RpcResult(id, new JsonObject
    {
        ["protocolVersion"] = "2025-06-18",
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = options.ServerName,
            ["title"] = "VpsLimitMonitor",
            ["version"] = "1",
        },
    }),
    "ping" => AdapterText.RpcResult(id, new JsonObject()),
    "tools/list" => AdapterText.RpcResult(id, new JsonObject { ["tools"] = new JsonArray() }),
    "tools/call" => AdapterText.RpcResult(id, new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = $"VpsLimitMonitor is not reachable on {options.DescribePipes()}. "
                       + $"Start the app and retry. Details: {reason}",
        }),
        ["isError"] = true,
    }),
    _ => AdapterText.RpcError(id, -32601, $"Method not available while VpsLimitMonitor is closed: {method}"),
};

/// <summary>两端共享的 JSON-RPC 辅助与编码。</summary>
internal static class AdapterText
{
    internal static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    internal static bool ExpectsResponse(JsonNode message) => message switch
    {
        JsonObject single => single["id"] is not null,
        JsonArray batch => batch.Any(item => item is JsonObject entry && entry["id"] is not null),
        _ => true,
    };

    internal static JsonObject RpcResult(JsonNode? id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    internal static JsonObject RpcError(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}

/// <summary>适配器命令行。</summary>
internal sealed record AdapterOptions(
    IReadOnlyList<string> PipeNames,
    string Surface,
    bool AutoLaunch,
    string AppPath)
{
    public string ServerName => IsDebugSurface
        ? "vpslimitmonitor-debug"
        : "vpslimitmonitor";

    public bool IsDebugSurface => Surface.Equals("debug", StringComparison.OrdinalIgnoreCase);

    public string DescribePipes() =>
        string.Join(" or ", PipeNames.Select(name => $@"\\.\pipe\{name}"));

    public static AdapterOptions Parse(string[] args)
    {
        var surface = "debug";
        string? pipe = null;
        string? instance = null;
        string? appPath = null;
        bool? launch = null;

        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--surface" when value is not null:
                    surface = value;
                    i++;
                    break;
                case "--pipe" when value is not null:
                    pipe = value;
                    i++;
                    break;
                case "--instance" when value is not null:
                    instance = value;
                    i++;
                    break;
                case "--app" when value is not null:
                    appPath = value;
                    i++;
                    break;
                case "--launch":
                    launch = true;
                    break;
                case "--no-launch":
                    launch = false;
                    break;
            }
        }

        var baseDirectory = AppContext.BaseDirectory;
        appPath ??= Path.Combine(baseDirectory, "VpsLimitMonitor.exe");

        // Release 注册裸管道名，Debug 加目录哈希后缀，而适配器无法得知旁边是哪种构建——
        // 先试带后缀的名字，再回退到裸名。
        List<string> pipes;
        if (pipe is { Length: > 0 })
        {
            pipes = [pipe];
        }
        else
        {
            var derived = McpPipeNames.Resolve(surface, instance ?? McpPipeNames.InstanceId(baseDirectory));
            var bare = McpPipeNames.Resolve(surface, null);
            pipes = derived == bare ? [bare] : [derived, bare];
        }

        // Debug worktree 由开发者驱动，程序本来就开着；只有产品接口才按需拉起程序。
        launch ??= !surface.Equals("debug", StringComparison.OrdinalIgnoreCase);

        return new AdapterOptions(pipes, surface, launch.Value, appPath);
    }
}

/// <summary>懒连接、自愈的命名管道客户端。</summary>
internal sealed class PipeConnection(AdapterOptions options) : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    /// <summary>
    ///     转发一条消息并返回对应的响应行；通知返回 null。管道断开时重试一次，
    ///     程序重启不会终结 agent 的会话。
    /// </summary>
    public async Task<string?> SendAsync(JsonNode message, bool expectsResponse, bool mayLaunch)
    {
        var payload = message.ToJsonString();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var (reader, writer) = await ConnectAsync(mayLaunch).ConfigureAwait(false);
                await writer.WriteLineAsync(payload).ConfigureAwait(false);
                if (!expectsResponse)
                    return null;

                // 跳过服务端主动发出的通知，避免把它误当成本次请求的响应
                // （管道是全双工的，程序可能随时推送）。
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (line.Length == 0)
                        continue;
                    if (JsonNode.Parse(line) is JsonObject reply && reply["id"] is null)
                        continue;
                    return line;
                }

                throw new IOException("The app closed the pipe before replying.");
            }
            catch (Exception) when (attempt == 0)
            {
                Reset();
            }
        }
    }

    private async Task<(StreamReader Reader, StreamWriter Writer)> ConnectAsync(bool mayLaunch)
    {
        if (_reader is { } reader && _writer is { } writer && _pipe?.IsConnected == true)
            return (reader, writer);

        Reset();

        try
        {
            await OpenAsync(500).ConfigureAwait(false);
        }
        catch (Exception) when (mayLaunch)
        {
            LaunchApp();
            // GUI 要启动并注册管道，多等一会儿。
            await OpenAsync(30000).ConfigureAwait(false);
        }

        return (_reader!, _writer!);
    }

    private async Task OpenAsync(int timeoutMilliseconds)
    {
        Exception? lastError = null;
        foreach (var name in options.PipeNames)
        {
            var pipe = new NamedPipeClientStream(
                ".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(timeoutMilliseconds / options.PipeNames.Count + 1).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex;
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _pipe = pipe;
            _reader = new StreamReader(pipe, AdapterText.Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            _writer = new StreamWriter(pipe, AdapterText.Utf8, leaveOpen: true) { AutoFlush = true };
            return;
        }

        throw lastError ?? new IOException($"Could not connect to {options.DescribePipes()}.");
    }

    private void LaunchApp()
    {
        if (!File.Exists(options.AppPath))
            throw new FileNotFoundException("VpsLimitMonitor executable not found.", options.AppPath);

        Process.Start(new ProcessStartInfo
        {
            FileName = options.AppPath,
            WorkingDirectory = Path.GetDirectoryName(options.AppPath) ?? Environment.CurrentDirectory,
            UseShellExecute = true,
        });
    }

    private void Reset()
    {
        try { _reader?.Dispose(); } catch { /* torn down */ }
        try { _writer?.Dispose(); } catch { /* torn down */ }
        try { _pipe?.Dispose(); } catch { /* torn down */ }
        _reader = null;
        _writer = null;
        _pipe = null;
    }

    public void Dispose() => Reset();
}

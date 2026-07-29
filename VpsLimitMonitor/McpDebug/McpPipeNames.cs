using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VpsLimitMonitor.McpDebug;

/// <summary>
///     应用与 <c>VpsLimitMonitorMcp</c> stdio 适配器共享的命名管道命名约定。
///     两端各自编译本文件（适配器工程 Compile Include 链接），因此不可能对不上。
///
///     管道取代回环 HTTP 端点：无需分配端口，名字永久稳定，可写死在客户端配置里。
///     实例 id 由可执行文件目录哈希得出，并行 worktree 的实例互不应答。
/// </summary>
public static class McpPipeNames
{
    /// <summary>产品接口（暂未提供）：程序功能开放给最终用户的 AI。</summary>
    public const string ProductBase = "VpsLimitMonitor.Mcp";

    /// <summary>调试接口：对象图、模拟工具、探针。仅 Debug 版本监听。</summary>
    public const string DebugBase = "VpsLimitMonitor.Mcp.Debug";

    /// <summary>
    ///     安装实例的稳定 12 位十六进制标识，由可执行文件目录哈希得出。
    ///     适配器与程序同目录，因此无需指定实例即可推导出同一个值。
    /// </summary>
    public static string InstanceId(string executableDirectory) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Normalize(executableDirectory))))[..12].ToLowerInvariant();

    public static string Product(string? instanceId) => Compose(ProductBase, instanceId);

    public static string Debug(string? instanceId) => Compose(DebugBase, instanceId);

    /// <summary>把 "product"/"debug" 加可选实例 id 解析成管道名。</summary>
    public static string Resolve(string surface, string? instanceId) =>
        surface.Equals("debug", StringComparison.OrdinalIgnoreCase)
            ? Debug(instanceId)
            : Product(instanceId);

    private static string Compose(string baseName, string? instanceId) =>
        string.IsNullOrWhiteSpace(instanceId) || instanceId == "release"
            ? baseName
            : $"{baseName}.{instanceId.Trim()}";

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
}

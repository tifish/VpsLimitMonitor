using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using JeekTools;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using VpsLimitMonitor.Settings;
using ZLogger;

namespace VpsLimitMonitor.Web;

public record FetchResult(int Status, string Url, string Body);

/// <summary>
///     基于 WebView2 的站点会话。每个账号一个独立 Profile（cookie 隔离），
///     平时窗口隐藏，在已登录页面上下文里用 fetch 抓数据；需要登录时把窗口显示出来。
///     所有方法必须在 UI 线程调用。
/// </summary>
public class WebSession(string baseUrl, string profileName)
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(WebSession));

    private static CoreWebView2Environment? _environment;
    private static readonly SemaphoreSlim EnvironmentLock = new(1, 1);

    public string BaseUrl { get; } = baseUrl.TrimEnd('/');

    private Window? _window;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _webView;
    private Task? _initTask;

    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pendingMessages = [];

    /// <summary>登录窗口被用户关闭（隐藏）后触发。</summary>
    public event Action? LoginWindowClosed;

    /// <summary>登录窗口可见期间页面完成一次导航（如登录成功跳转）后触发。</summary>
    public event Action? LoginWindowNavigated;

    public bool IsLoginWindowVisible => _window?.IsVisible == true;

    public Task EnsureInitializedAsync()
    {
        return _initTask ??= InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await EnvironmentLock.WaitAsync();
        try
        {
            if (_environment == null)
            {
                var userDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    SettingsManager.AppName,
                    "WebView2"
                );
                Directory.CreateDirectory(userDataDir);
                _environment = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            }
        }
        finally
        {
            EnvironmentLock.Release();
        }

        _window = new Window
        {
            Title = $"登录 - {profileName}",
            Icon = App.AppIcon,
            Width = 1000,
            Height = 760,
            ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        _window.Closing += (_, e) =>
        {
            // 关闭登录窗口只是隐藏，会话继续存活
            e.Cancel = true;
            HideLoginWindow();
            LoginWindowClosed?.Invoke();
        };
        _window.SizeChanged += (_, _) => UpdateControllerBounds();

        var handle =
            _window.TryGetPlatformHandle()?.Handle
            ?? throw new InvalidOperationException("Failed to get window handle");

        var options = _environment.CreateCoreWebView2ControllerOptions();
        options.ProfileName = SanitizeProfileName(profileName);
        options.IsInPrivateModeEnabled = false;

        _controller = await _environment.CreateCoreWebView2ControllerAsync(handle, options);
        _controller.IsVisible = false;
        _webView = _controller.CoreWebView2;
        _webView.WebMessageReceived += OnWebMessageReceived;
        _webView.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess && IsLoginWindowVisible)
                LoginWindowNavigated?.Invoke();
        };

        Log.ZLogInformation($"WebView2 session initialized for {profileName} ({BaseUrl})");
    }

    private static string SanitizeProfileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (!doc.RootElement.TryGetProperty("id", out var idProp))
                return;

            var id = idProp.GetString() ?? "";
            if (_pendingMessages.Remove(id, out var tcs))
                tcs.TrySetResult(doc.RootElement.Clone());
        }
        catch (Exception ex)
        {
            Log.ZLogWarning($"Bad web message: {ex.Message}");
        }
    }

    private async Task NavigateAndWaitAsync(string url, int timeoutSeconds = 60)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            _webView!.NavigationCompleted -= Handler;
            tcs.TrySetResult(e.IsSuccess);
        }

        _webView!.NavigationCompleted += Handler;
        _webView.Navigate(url);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        await using var reg = cts.Token.Register(() =>
        {
            _webView.NavigationCompleted -= Handler;
            tcs.TrySetCanceled();
        });
        await tcs.Task;
    }

    /// <summary>确保 WebView 当前停在目标站点上（同源），fetch 才能带上会话 cookie。</summary>
    private async Task EnsureOnSiteAsync()
    {
        var source = _webView!.Source ?? "";
        if (!source.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase))
            await NavigateAndWaitAsync($"{BaseUrl}/clientarea.php");
    }

    /// <summary>
    ///     在页面上下文里执行异步脚本。脚本内使用 __post(obj) 回传结果对象。
    /// </summary>
    public async Task<JsonElement> RunAsyncScriptAsync(string scriptBody, int timeoutSeconds = 30)
    {
        await EnsureInitializedAsync();
        await EnsureOnSiteAsync();

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _pendingMessages[id] = tcs;

        var script = $$"""
            (function () {
                var __post = function (obj) {
                    obj.id = "{{id}}";
                    window.chrome.webview.postMessage(obj);
                };
                try {
                    {{scriptBody}}
                } catch (e) {
                    __post({ error: String(e) });
                }
            })();
            """;

        await _webView!.ExecuteScriptAsync(script);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        await using var reg = cts.Token.Register(() =>
        {
            _pendingMessages.Remove(id);
            tcs.TrySetCanceled();
        });

        var result = await tcs.Task;
        if (result.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"Script error: {error.GetString()}");

        return result;
    }

    /// <summary>在已登录会话里 fetch 一个同站 URL，返回状态码、最终 URL 和响应体。</summary>
    public async Task<FetchResult> FetchAsync(string relativeUrl, int timeoutSeconds = 30)
    {
        var url = relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relativeUrl
            : BaseUrl + relativeUrl;

        var script = $$"""
            fetch("{{url}}", { credentials: "include", redirect: "follow" })
                .then(function (r) {
                    return r.text().then(function (t) {
                        __post({ status: r.status, url: r.url, body: t });
                    });
                })
                .catch(function (e) {
                    __post({ error: String(e) });
                });
            """;

        var result = await RunAsyncScriptAsync(script, timeoutSeconds);
        return new FetchResult(
            result.GetProperty("status").GetInt32(),
            result.GetProperty("url").GetString() ?? "",
            result.GetProperty("body").GetString() ?? ""
        );
    }

    /// <summary>
    ///     把当前站点的会话级 cookie 改写为持久 cookie（30 天，每次调用顺延）。
    ///     WHMCS 的登录 cookie 不带过期时间，Chromium 重启即丢弃，导致每次启动都要重新登录。
    /// </summary>
    public async Task PersistSessionCookiesAsync()
    {
        if (_webView == null)
            return;

        var manager = _webView.CookieManager;
        var cookies = await manager.GetCookiesAsync(BaseUrl);
        foreach (var cookie in cookies.Where(c => c.IsSession))
        {
            cookie.Expires = DateTime.Now.AddDays(30);
            manager.AddOrUpdateCookie(cookie);
        }
    }

    public record CookieInfo(
        string Name,
        string Domain,
        string Path,
        bool IsSession,
        DateTime? Expires,
        bool IsHttpOnly
    );

    /// <summary>调试用：列出当前站点的 cookie 概要。</summary>
    public async Task<List<CookieInfo>> GetCookiesAsync()
    {
        await EnsureInitializedAsync();
        var cookies = await _webView!.CookieManager.GetCookiesAsync(BaseUrl);
        return
        [
            .. cookies.Select(c => new CookieInfo(
                c.Name,
                c.Domain,
                c.Path,
                c.IsSession,
                c.IsSession ? null : c.Expires,
                c.IsHttpOnly
            )),
        ];
    }

    public async Task ShowLoginWindowAsync(string url)
    {
        await EnsureInitializedAsync();
        _window!.Show();
        _window.Activate();
        _controller!.IsVisible = true;
        UpdateControllerBounds();
        await NavigateAndWaitAsync(url);
    }

    public void HideLoginWindow()
    {
        if (_controller != null)
            _controller.IsVisible = false;
        _window?.Hide();
    }

    private void UpdateControllerBounds()
    {
        if (_window == null || _controller == null)
            return;

        var scale = _window.RenderScaling;
        _controller.Bounds = new System.Drawing.Rectangle(
            0,
            0,
            (int)(_window.ClientSize.Width * scale),
            (int)(_window.ClientSize.Height * scale)
        );
    }
}

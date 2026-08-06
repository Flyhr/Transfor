using System.Text.Json.Nodes;

namespace Transfor;

// CDP 目标会话：创建并挂接页面 target，启用 Page/Network/Runtime 域；
// 提供导航（轮询 readyState）、脚本求值、Cookie 读取与主 frame 获取；
// 事件经连接分发并带 sessionId，本类仅做协议封装
internal sealed class CdpTargetSession
{
    private const int NavigationTimeoutSeconds = 30;
    private const int ReadyPollMilliseconds = 300;

    private readonly CdpConnection connection;
    private readonly string targetId;
    private readonly string sessionId;
    private bool domainsEnabled;
    private string? frameId;

    private CdpTargetSession(CdpConnection connection, string targetId, string sessionId)
    {
        this.connection = connection;
        this.targetId = targetId;
        this.sessionId = sessionId;
    }

    public string SessionId => sessionId;

    public string TargetId => targetId;

    // 会话级事件透传（下载器订阅 Fetch 事件等）
    public event Action<string, JsonNode?, string?>? EventReceived
    {
        add => connection.EventReceived += value;
        remove => connection.EventReceived -= value;
    }

    // 创建空白 target 并挂接（flatten 模式返回 sessionId）
    public static async Task<CdpTargetSession> CreateAsync(CdpConnection connection, CancellationToken cancellationToken)
    {
        var create = await connection.CommandAsync("Target.createTarget", new { url = "about:blank" }, null, cancellationToken);
        var targetId = create?["targetId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("创建 CDP target 失败。");
        var attach = await connection.CommandAsync("Target.attachToTarget", new { targetId, flatten = true }, null, cancellationToken);
        var sessionId = attach?["sessionId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("挂接 CDP target 失败。");
        return new CdpTargetSession(connection, targetId, sessionId);
    }

    public async Task EnableDomainsAsync(CancellationToken cancellationToken)
    {
        if (domainsEnabled)
        {
            return;
        }
        await connection.CommandAsync("Page.enable", null, sessionId, cancellationToken);
        await connection.CommandAsync("Network.enable", null, sessionId, cancellationToken);
        await connection.CommandAsync("Runtime.enable", null, sessionId, cancellationToken);
        domainsEnabled = true;
    }

    // 导航并等待页面 readyState 为 complete；检查导航错误与最终 URL（拒绝错误页）；
    // 超时抛出明确错误；每次导航后主 frame 可能变化，需重新获取
    public async Task NavigateAsync(Uri url, CancellationToken cancellationToken, int timeoutSeconds = NavigationTimeoutSeconds)
    {
        var navigation = await connection.CommandAsync("Page.navigate", new { url = url.ToString() }, sessionId, cancellationToken);
        var errorText = navigation?["errorText"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(errorText))
        {
            throw new InvalidOperationException($"页面导航失败：{errorText}");
        }

        // 导航后主 frame 可能变化，frameId 缓存失效
        frameId = null;

        var ready = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await EvaluateAsync<string>(connection, sessionId, "document.readyState", cancellationToken);
            if (string.Equals(state, "complete", StringComparison.Ordinal))
            {
                ready = true;
                break;
            }
            await Task.Delay(ReadyPollMilliseconds, cancellationToken);
        }

        if (!ready)
        {
            throw new TimeoutException("页面加载超时。");
        }

        // readyState=complete 也可能落在 Edge 错误页（chrome-error://）：拒绝视为导航失败
        var finalUrl = await EvaluateAsync<string>(connection, sessionId, "location.href", cancellationToken);
        if (string.IsNullOrWhiteSpace(finalUrl)
            || finalUrl.StartsWith("chrome-error://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"页面进入 Edge 错误页：{finalUrl}");
        }
    }

    public async Task<T?> EvaluateAsync<T>(string expression, CancellationToken cancellationToken)
    {
        // awaitPromise=true：允许表达式返回 Promise（异步滚动/等待脚本），同步表达式不受影响
        var result = await connection.CommandAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, sessionId, cancellationToken);
        var value = result?["result"]?["value"];
        if (value is null)
        {
            return default;
        }
        return value.GetValue<T>();
    }

    // 主 frame id（Network.loadNetworkResource 需要）
    public async Task<string> GetFrameIdAsync(CancellationToken cancellationToken)
    {
        if (frameId is not null)
        {
            return frameId;
        }
        var tree = await connection.CommandAsync("Page.getFrameTree", null, sessionId, cancellationToken);
        frameId = tree?["frameTree"]?["frame"]?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("获取页面 frame 失败。");
        return frameId;
    }

    // 读取与目标 URL 匹配的 Cookie（浏览器网络栈的实际会话）
    public async Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(Uri requestUri, CancellationToken cancellationToken)
    {
        var result = await connection.CommandAsync("Network.getCookies", new { urls = new[] { requestUri.ToString() } }, sessionId, cancellationToken);
        var cookies = result?["cookies"]?.AsArray();
        if (cookies is null)
        {
            return Array.Empty<BrowserCookie>();
        }

        var list = new List<BrowserCookie>(cookies.Count);
        foreach (var item in cookies)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }
            list.Add(new BrowserCookie(
                obj["domain"]?.GetValue<string>() ?? string.Empty,
                obj["path"]?.GetValue<string>() ?? "/",
                obj["name"]?.GetValue<string>() ?? string.Empty,
                obj["value"]?.GetValue<string>() ?? string.Empty,
                obj["secure"]?.GetValue<bool>() ?? false));
        }
        return list;
    }

    // 发送带会话的原始命令（下载器等需要）
    public Task<JsonNode?> CommandAsync(string method, object? parameters, CancellationToken cancellationToken, int timeoutSeconds = 60)
        => connection.CommandAsync(method, parameters, sessionId, cancellationToken, timeoutSeconds);

    private static async Task<T?> EvaluateAsync<T>(
        CdpConnection connection,
        string sessionId,
        string expression,
        CancellationToken cancellationToken)
    {
        var result = await connection.CommandAsync("Runtime.evaluate", new { expression, returnByValue = true }, sessionId, cancellationToken);
        var value = result?["result"]?["value"];
        if (value is null)
        {
            return default;
        }
        return value.GetValue<T>();
    }
}

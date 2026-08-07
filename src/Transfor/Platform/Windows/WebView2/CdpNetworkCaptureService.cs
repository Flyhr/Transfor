using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace Transfor;

// CDP 网络捕获服务（Phase 4C）：经 WebView2 的 DevTools Protocol（Network 域）
// 监听真实网络流量并读取作品详情接口的响应体——
// WebResourceResponseReceived 拿不到响应体（4B 缺口在此补齐）；
// 登录态下详情接口的 JSON 响应是最可靠的作品数据来源（aweme_detail 完整数据）
internal sealed class CdpNetworkCaptureService : IDisposable
{
    private const string ResponseReceivedEvent = "Network.responseReceived";
    private const string LoadingFinishedEvent = "Network.loadingFinished";

    private readonly CoreWebView2 core;
    private readonly Dictionary<string, TaskCompletionSource<string?>> pendingBodies = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private CoreWebView2DevToolsProtocolEventReceiver? responseReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? finishedReceiver;
    private bool enabled;

    public CdpNetworkCaptureService(CoreWebView2 core)
    {
        this.core = core ?? throw new ArgumentNullException(nameof(core));
    }

    // 在 UI 线程调用：启用 Network 域并订阅 CDP 事件
    public async Task EnableAsync()
    {
        if (enabled)
        {
            return;
        }

        responseReceiver = core.GetDevToolsProtocolEventReceiver(ResponseReceivedEvent);
        finishedReceiver = core.GetDevToolsProtocolEventReceiver(LoadingFinishedEvent);
        responseReceiver.DevToolsProtocolEventReceived += OnResponseReceived;
        finishedReceiver.DevToolsProtocolEventReceived += OnLoadingFinished;
        await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
        enabled = true;
    }

    // 等待首个作品详情接口响应体（最多 timeout）；超时或无捕获返回 null
    public async Task<string?> WaitForDetailResponseAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task<string?>[] pending;
        lock (sync)
        {
            pending = pendingBodies.Values.Select(tcs => tcs.Task).ToArray();
        }

        if (pending.Length == 0)
        {
            return null;
        }

        var first = await Task.WhenAny(pending).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        return first.Result;
    }

    // CDP 事件回调（UI 线程）：详情接口响应登记
    private void OnResponseReceived(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            var node = JsonNode.Parse(e.ParameterObjectAsJson);
            var url = node?["response"]?["url"]?.GetValue<string>();
            var requestId = node?["requestId"]?.GetValue<string>();
            if (url is not null
                && requestId is not null
                && DouyinDetailEndpointMatcher.IsDetailEndpoint(url, resourceType: null))
            {
                lock (sync)
                {
                    if (!pendingBodies.ContainsKey(requestId))
                    {
                        pendingBodies[requestId] = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }
            }
        }
        catch
        {
            // CDP 事件解析失败忽略（不阻断捕获主流程）
        }
    }

    // CDP 事件回调（UI 线程）：详情接口加载完成时读取响应体
    private void OnLoadingFinished(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            var node = JsonNode.Parse(e.ParameterObjectAsJson);
            var requestId = node?["requestId"]?.GetValue<string>();
            if (requestId is null)
            {
                return;
            }

            TaskCompletionSource<string?>? tcs;
            lock (sync)
            {
                pendingBodies.TryGetValue(requestId, out tcs);
            }

            if (tcs is not null)
            {
                _ = FetchBodyAsync(requestId, tcs);
            }
        }
        catch
        {
            // CDP 事件解析失败忽略
        }
    }

    // 读取已加载完成请求的响应体（Network.getResponseBody）
    private async Task FetchBodyAsync(string requestId, TaskCompletionSource<string?> tcs)
    {
        try
        {
            var result = await core.CallDevToolsProtocolMethodAsync(
                "Network.getResponseBody",
                JsonSerializer.Serialize(new { requestId }));
            tcs.TrySetResult(ParseResponseBody(result));
        }
        catch
        {
            // 响应体不可读（如缓存策略拒绝）：视为未捕获
            tcs.TrySetResult(null);
        }
    }

    // 响应体解析（纯函数，可离线测试）：body + base64Encoded → 文本
    internal static string? ParseResponseBody(string resultJson)
    {
        try
        {
            var node = JsonNode.Parse(resultJson);
            var body = node?["body"]?.GetValue<string>();
            if (body is null)
            {
                return null;
            }

            var base64 = node?["base64Encoded"]?.GetValue<bool>() ?? false;
            return base64
                ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(body))
                : body;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (!enabled)
        {
            return;
        }

        enabled = false;
        if (responseReceiver is not null)
        {
            responseReceiver.DevToolsProtocolEventReceived -= OnResponseReceived;
            responseReceiver = null;
        }

        if (finishedReceiver is not null)
        {
            finishedReceiver.DevToolsProtocolEventReceived -= OnLoadingFinished;
            finishedReceiver = null;
        }

        try
        {
            _ = core.CallDevToolsProtocolMethodAsync("Network.disable", "{}");
        }
        catch
        {
            // 禁用失败不影响后续
        }

        lock (sync)
        {
            foreach (var tcs in pendingBodies.Values)
            {
                tcs.TrySetResult(null);
            }
            pendingBodies.Clear();
        }
    }
}

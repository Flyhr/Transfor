using Microsoft.Web.WebView2.Core;

namespace Transfor;

// 网络捕获服务（Phase 4B）：挂接解析控件的 WebResourceResponseReceived，
// 记录页面加载期间的真实网络请求（URL/Method/Content-Type/Status）；
// 按 URL 去重并设上限防内存膨胀；解析会话内启停；
// 响应体不可读（读响应体是 4C 的 CDP 能力）
internal sealed class NetworkCaptureService : IDisposable
{
    public const int MaxRecords = 500;

    private readonly CoreWebView2 core;
    private readonly List<NetworkResourceRecord> records = new();
    private readonly HashSet<string> seenUrls = new(StringComparer.Ordinal);
    private bool enabled;

    public NetworkCaptureService(CoreWebView2 core)
    {
        this.core = core ?? throw new ArgumentNullException(nameof(core));
    }

    // 开始监听：清空上次记录并订阅事件；幂等
    public void Enable()
    {
        if (enabled)
        {
            return;
        }

        records.Clear();
        seenUrls.Clear();
        core.WebResourceResponseReceived += OnResponseReceived;
        enabled = true;
    }

    // 停止监听并返回已记录的快照（去重、限容）
    public IReadOnlyList<NetworkResourceRecord> DisableAndSnapshot()
    {
        if (enabled)
        {
            core.WebResourceResponseReceived -= OnResponseReceived;
            enabled = false;
        }

        return records.AsReadOnly();
    }

    public void Dispose() => DisableAndSnapshot();

    private void OnResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (!enabled)
        {
            return;
        }

        var record = Parse(e.Request.Uri, e.Request.Method, e.Response.Headers.GetHeader("Content-Type"), (int)e.Response.StatusCode);
        if (!seenUrls.Add(record.Uri.ToString()))
        {
            return;
        }

        if (records.Count >= MaxRecords)
        {
            return;
        }

        records.Add(record);
    }

    // 事件原始值 → 记录（纯函数，便于离线测试）；Content-Type 去掉参数部分
    internal static NetworkResourceRecord Parse(string url, string method, string? contentType, int statusCode)
    {
        Uri.TryCreate(url, UriKind.Absolute, out var uri);
        return new NetworkResourceRecord(
            uri ?? new Uri("about:blank"),
            method,
            string.IsNullOrWhiteSpace(contentType) ? null : contentType.Split(';')[0].Trim(),
            statusCode);
    }
}

using System.Text.Json.Nodes;

namespace Transfor;

// 页面媒体预取器：在页面上下文注入真实 <img>/<video> 元素（请求形态与页面自身加载一致，
// Sec-Fetch-Dest: image/media），监听 Network 事件，在 loadingFinished 后通过
// Network.getResponseBody 取得响应体写入本地缓存；
// 尽力而为：任何失败都不影响主流程
internal static class MediaPagePrefetcher
{
    private const int ResponseWaitTimeoutSeconds = 15;

    public static async Task PrefetchAsync(
        CdpTargetSession session,
        MediaCache cache,
        IReadOnlyList<(Uri Uri, MediaKind Kind)> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        // 去重并限制数量（防止页面包含大量媒体时预取过久）
        var mediaItems = items
            .DistinctBy(item => item.Uri)
            .Take(20)
            .ToList();
        var pending = mediaItems.ToDictionary(
            item => item.Uri.ToString(),
            _ => new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));

        void OnEvent(string method, JsonNode? parameters, string? eventSessionId)
        {
            if (method != "Network.responseReceived" || eventSessionId != session.SessionId)
            {
                return;
            }

            // 图片响应 type=Image；视频响应可能为 Media 或 Other，按 URL 匹配即可
            var url = parameters?["response"]?["url"]?.GetValue<string>();
            var requestId = parameters?["requestId"]?.GetValue<string>();
            if (url is not null && requestId is not null && pending.TryGetValue(url, out var tcs))
            {
                tcs.TrySetResult(requestId);
            }
        }

        session.EventReceived += OnEvent;
        try
        {
            // 注入真实媒体元素（隐藏于视口外）：图片 <img>，视频 <video preload=auto>
            foreach (var item in mediaItems)
            {
                var expression = item.Kind == MediaKind.Video
                    ? BuildVideoElementScript(item.Uri)
                    : BuildImgElementScript(item.Uri);
                _ = session.CommandAsync("Runtime.evaluate", new
                {
                    expression,
                    returnByValue = false,
                }, cancellationToken);
            }

            // 等待每个媒体响应到达并缓存
            foreach (var (url, tcs) in pending)
            {
                string? requestId;
                try
                {
                    requestId = await tcs.Task.WaitAsync(
                        TimeSpan.FromSeconds(ResponseWaitTimeoutSeconds), cancellationToken);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                if (requestId is null)
                {
                    continue;
                }

                var body = await session.CommandAsync("Network.getResponseBody", new { requestId }, cancellationToken);
                var data = body?["body"]?.GetValue<string>();
                var base64 = body?["base64Encoded"]?.GetValue<bool>() ?? false;
                if (data is null)
                {
                    continue;
                }

                var bytes = base64 ? Convert.FromBase64String(data) : System.Text.Encoding.UTF8.GetBytes(data);
                using var stream = new MemoryStream(bytes);
                await cache.SaveAsync(new Uri(url), stream, cancellationToken);
            }
        }
        finally
        {
            session.EventReceived -= OnEvent;
        }
    }

    internal static string BuildImgElementScript(Uri uri)
    {
        var serialized = System.Text.Json.JsonSerializer.Serialize(uri.ToString());
        return $"(() => {{ const i = document.createElement('img'); i.style.position='fixed'; i.style.left='-10000px'; i.style.top='-10000px'; i.src={serialized}; document.body.appendChild(i); return true; }})()";
    }

    internal static string BuildVideoElementScript(Uri uri)
    {
        var serialized = System.Text.Json.JsonSerializer.Serialize(uri.ToString());
        return $"(() => {{ const v = document.createElement('video'); v.style.position='fixed'; v.style.left='-10000px'; v.style.top='-10000px'; v.preload='auto'; v.src={serialized}; document.body.appendChild(v); return true; }})()";
    }
}

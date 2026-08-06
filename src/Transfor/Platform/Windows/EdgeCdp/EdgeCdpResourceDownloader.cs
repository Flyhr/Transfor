using System.Text;
using System.Text.Json.Nodes;

namespace Transfor;

// Edge CDP 资源下载器：在真实 Edge 网络栈中请求媒体并流式写入 .part；
// 首选 Network.loadNetworkResource + IO.read（带浏览器 Cookie）；
// 失败时回退 Fetch 域：拦截页面内 fetch 请求 → takeResponseBodyAsStream → IO.read；
// 单文件下载；由调用方负责 .part 清理与最终落盘校验
internal static class EdgeCdpResourceDownloader
{
    private const int ReadChunkSize = 64 * 1024;
    private const int EventWaitTimeoutSeconds = 30;

    public static async Task<long> DownloadAsync(
        CdpTargetSession session,
        Uri mediaUri,
        string partPath,
        CancellationToken cancellationToken,
        IProgress<long>? progress = null)
    {
        var frameId = await session.GetFrameIdAsync(cancellationToken);
        var result = await session.CommandAsync("Network.loadNetworkResource", new
        {
            frameId,
            url = mediaUri.ToString(),
            options = new { disableCache = true, includeCredentials = true },
        }, cancellationToken, timeoutSeconds: 60);

        var resource = result?["resource"] as JsonObject;
        var stream = resource?["stream"]?.GetValue<string>();
        if (stream is not null)
        {
            return await ReadStreamToFileAsync(session, stream, partPath, cancellationToken, progress);
        }

        var netError = resource?["netError"]?.GetValue<int>();
        return await FetchFallbackAsync(session, mediaUri, partPath, cancellationToken, progress, netError);
    }

    // 通过 IO.read 把浏览器网络栈收到的响应流式写入文件
    private static async Task<long> ReadStreamToFileAsync(
        CdpTargetSession session,
        string streamHandle,
        string partPath,
        CancellationToken cancellationToken,
        IProgress<long>? progress)
    {
        try
        {
            await using var destination = new FileStream(
                partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, ReadChunkSize, useAsync: true);
            long total = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = await session.CommandAsync("IO.read", new { handle = streamHandle, size = ReadChunkSize }, cancellationToken);
                var data = chunk?["data"]?.GetValue<string>() ?? string.Empty;
                var base64 = chunk?["base64Encoded"]?.GetValue<bool>() ?? false;
                var bytes = base64 ? Convert.FromBase64String(data) : Encoding.UTF8.GetBytes(data);
                if (bytes.Length == 0)
                {
                    break;
                }

                await destination.WriteAsync(bytes, cancellationToken);
                total += bytes.Length;
                progress?.Report(total);
                if (chunk?["eof"]?.GetValue<bool>() == true)
                {
                    break;
                }
            }
            return total;
        }
        finally
        {
            try
            {
                await session.CommandAsync("IO.close", new { handle = streamHandle }, CancellationToken.None);
            }
            catch
            {
                // 流已关闭等场景忽略
            }
        }
    }

    // 回退：在页面上下文发起 fetch，用 Fetch 域捕获响应流
    private static async Task<long> FetchFallbackAsync(
        CdpTargetSession session,
        Uri mediaUri,
        string partPath,
        CancellationToken cancellationToken,
        IProgress<long>? progress,
        int? netError)
    {
        var paused = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEvent(string method, JsonNode? parameters, string? eventSessionId)
        {
            if (method == "Fetch.requestPaused"
                && eventSessionId == session.SessionId
                && parameters?["request"]?["url"]?.GetValue<string>() == mediaUri.ToString())
            {
                paused.TrySetResult(parameters["requestId"]!.GetValue<string>());
            }
        }

        session.EventReceived += OnEvent;
        try
        {
            await session.CommandAsync("Fetch.enable", new
            {
                patterns = new[]
                {
                    new { urlPattern = mediaUri.ToString(), requestStage = "Response" },
                },
            }, cancellationToken);

            // 页面上下文发起 fetch（携带页面 Cookie/Referer）；no-cors 避免 CORS 拦截
            _ = session.CommandAsync("Runtime.evaluate", new
            {
                expression = $"fetch({System.Text.Json.JsonSerializer.Serialize(mediaUri.ToString())}, {{ credentials: 'include', mode: 'no-cors' }})",
                returnByValue = false,
            }, cancellationToken);

            string requestId;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(EventWaitTimeoutSeconds));
                requestId = await paused.Task.WaitAsync(timeout.Token);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException($"媒体请求未触发浏览器拦截（netError={netError}）。");
            }

            var streamResult = await session.CommandAsync("Fetch.takeResponseBodyAsStream", new { requestId }, cancellationToken);
            var stream = streamResult?["stream"]?.GetValue<string>()
                ?? throw new InvalidOperationException("浏览器未能提供媒体响应流。");
            return await ReadStreamToFileAsync(session, stream, partPath, cancellationToken, progress);
        }
        finally
        {
            session.EventReceived -= OnEvent;
            try
            {
                await session.CommandAsync("Fetch.disable", null, CancellationToken.None);
            }
            catch
            {
                // 域未启用等场景忽略
            }
        }
    }
}

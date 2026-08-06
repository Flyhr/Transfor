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

        return await FetchFallbackAsync(session, mediaUri, partPath, cancellationToken, progress);
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

    // 回退：在页面上下文发起 fetch，用 Fetch 域拦截响应流；
    // 统一从 Request 阶段拦截（流式/大响应时 Response 阶段拦截可能回退），
    // 对 Request 阶段暂停使用 continueRequest(interceptResponse) 两阶段模式，
    // 在 Response 阶段再次暂停后取响应体流
    private static async Task<long> FetchFallbackAsync(
        CdpTargetSession session,
        Uri mediaUri,
        string partPath,
        CancellationToken cancellationToken,
        IProgress<long>? progress)
    {
        var firstPause = new TaskCompletionSource<(string RequestId, bool AtResponse, string? ErrorReason)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPause = new TaskCompletionSource<(string RequestId, string? ErrorReason)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var url = mediaUri.ToString();

        void OnEvent(string method, JsonNode? parameters, string? eventSessionId)
        {
            if (method != "Fetch.requestPaused" || eventSessionId != session.SessionId)
            {
                return;
            }
            if (parameters?["request"]?["url"]?.GetValue<string>() != url)
            {
                return;
            }

            var requestId = parameters["requestId"]!.GetValue<string>();
            var atResponse = parameters["responseStatusCode"] is not null;
            var errorReason = parameters["responseErrorReason"]?.GetValue<string>();
            if (!firstPause.Task.IsCompleted)
            {
                firstPause.TrySetResult((requestId, atResponse, errorReason));
            }
            else
            {
                secondPause.TrySetResult((requestId, errorReason));
            }
        }

        session.EventReceived += OnEvent;
        try
        {
            await session.CommandAsync("Fetch.enable", new
            {
                patterns = new[]
                {
                    new { urlPattern = url, requestStage = "Request" },
                },
            }, cancellationToken);

            // 页面上下文发起 fetch（携带页面 Cookie/Referer）；no-cors 避免 CORS 拦截
            _ = session.CommandAsync("Runtime.evaluate", new
            {
                expression = $"fetch({System.Text.Json.JsonSerializer.Serialize(url)}, {{ credentials: 'include', mode: 'no-cors' }})",
                returnByValue = false,
            }, cancellationToken);

            var (requestId, atResponse, errorReason) = await firstPause.Task.WaitAsync(
                TimeSpan.FromSeconds(EventWaitTimeoutSeconds), cancellationToken);

            if (!atResponse)
            {
                // Request 阶段暂停：两阶段模式，续传并拦截响应
                await session.CommandAsync("Fetch.continueRequest", new { requestId, interceptResponse = true }, cancellationToken);
                (requestId, errorReason) = await secondPause.Task.WaitAsync(
                    TimeSpan.FromSeconds(EventWaitTimeoutSeconds), cancellationToken);
            }

            if (errorReason is not null)
            {
                throw new InvalidOperationException($"媒体请求被网络层拒绝（{errorReason}）。");
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

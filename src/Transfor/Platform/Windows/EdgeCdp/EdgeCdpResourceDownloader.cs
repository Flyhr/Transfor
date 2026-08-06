using System.Text;
using System.Text.Json.Nodes;

namespace Transfor;

// Edge CDP 资源下载器：在真实 Edge 网络栈中请求媒体并流式写入 .part；
// 首选 Network.loadNetworkResource + IO.read（带浏览器 Cookie）；
// 失败时回退页面元素形态：注入真实 <img>/<video> 元素（与页面自身加载同形态），
// 经 Fetch 域两阶段拦截 → takeResponseBodyAsStream → IO.read；
// 单文件下载；由调用方负责 .part 清理与最终落盘校验
internal static class EdgeCdpResourceDownloader
{
    private const int ReadChunkSize = 64 * 1024;
    private const int EventWaitTimeoutSeconds = 30;

    public static async Task<long> DownloadAsync(
        CdpTargetSession session,
        Uri mediaUri,
        MediaKind kind,
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

        // 完整读取诊断字段：success/netError/netErrorName/httpStatusCode/stream
        var resource = result?["resource"] as JsonObject
            ?? throw new InvalidOperationException("Network.loadNetworkResource 未返回 resource。");
        var success = resource["success"]?.GetValue<bool>() ?? false;
        var stream = resource["stream"]?.GetValue<string>();
        var netError = resource["netError"]?.GetValue<int>();
        var netErrorName = resource["netErrorName"]?.GetValue<string>();
        var httpStatusCode = resource["httpStatusCode"]?.GetValue<int>();

        if (!success || string.IsNullOrWhiteSpace(stream))
        {
            // 主链路失败：保留诊断信息，尝试页面元素形态回退；
            // 回退再失败时错误信息同时包含主链路与回退链路原因
            var primaryInfo = $"success={success}，netError={netError?.ToString() ?? "-"}，netErrorName={netErrorName ?? "-"}，httpStatus={httpStatusCode?.ToString() ?? "-"}";
            try
            {
                return await PageElementFallbackAsync(session, mediaUri, kind, partPath, cancellationToken, progress);
            }
            catch (Exception fallbackException)
            {
                throw new InvalidOperationException(
                    $"Edge 资源请求失败：{primaryInfo}（页面元素回退失败：{fallbackException.Message}）。");
            }
        }

        return await ReadStreamToFileAsync(session, stream, partPath, cancellationToken, progress);
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

    // 回退：注入真实 <img>/<video> 页面元素（与页面自身加载同形态），用 Fetch 域拦截响应流；
    // 统一从 Request 阶段拦截（流式/大响应时 Response 阶段拦截可能回退），
    // 对 Request 阶段暂停使用 continueRequest(interceptResponse) 两阶段模式，
    // 在 Response 阶段再次暂停后取响应体流；
    // 读取完成后显式 failRequest 结束被拦截请求（不能裸 Fetch.disable）
    private static async Task<long> PageElementFallbackAsync(
        CdpTargetSession session,
        Uri mediaUri,
        MediaKind kind,
        string partPath,
        CancellationToken cancellationToken,
        IProgress<long>? progress)
    {
        var firstPause = new TaskCompletionSource<(string RequestId, bool AtResponse, string? ErrorReason)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPause = new TaskCompletionSource<(string RequestId, string? ErrorReason)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var url = mediaUri.ToString();
        var finalRequestId = (string?)null;

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

            // 注入真实页面元素（图片 <img>，视频 <video preload=auto>），
            // 请求形态与页面自身加载一致（Sec-Fetch-Dest: image/media）
            var script = kind == MediaKind.Image
                ? MediaPagePrefetcher.BuildImgElementScript(mediaUri)
                : MediaPagePrefetcher.BuildVideoElementScript(mediaUri);
            _ = session.CommandAsync("Runtime.evaluate", new
            {
                expression = script,
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

            finalRequestId = requestId;

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
            // 取走响应体后必须明确结束被拦截请求（failRequest），否则请求悬挂
            if (finalRequestId is not null)
            {
                try
                {
                    await session.CommandAsync("Fetch.failRequest", new { requestId = finalRequestId, errorReason = "Aborted" }, CancellationToken.None);
                }
                catch
                {
                    // 请求已结束等场景忽略
                }
            }
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

using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Transfor;

// 浏览器网络栈下载（Phase 4A）：经 WebView2 的 DevTools Protocol 直取媒体资源——
// Network.loadNetworkResource（带浏览器 Cookie 与真实指纹）+ IO.read 流式写入 .part。
// 不依赖页面 fetch（避免 CORS 拦截）也不依赖 ExecuteScriptAsync 的异步结果
// （本机 WebView2 内核 151 下对 Promise 返回空对象）；
// 必须在 UI 线程调用（由隐藏宿主调度）
internal static class BrowserDownloadController
{
    // IO.read 单次读取字节数（base64 传输开销约 1.33x，64KB 块可控）
    private const int ReadChunkSize = 64 * 1024;

    // 下载媒体到 partPath；返回 null 表示成功，否则错误信息；必须在 UI 线程调用
    public static async Task<string?> DownloadAsync(
        CoreWebView2 core,
        Uri mediaUri,
        string partPath,
        CancellationToken cancellationToken,
        Action<long, long?>? progress,
        long? maxBytes)
    {
        // 第一步：获取当前 frameId（loadNetworkResource 必填）
        string frameId;
        try
        {
            var frameJson = await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}").ConfigureAwait(true);
            frameId = ParseFrameId(frameJson)
                ?? throw new InvalidOperationException("无法获取页面 frame。");
        }
        catch (Exception ex)
        {
            return $"浏览器下载失败：{ErrorChainFormatter.Format(ex)}";
        }

        // 第二步：浏览器网络栈直取媒体资源（带 Cookie；无 CORS 限制）
        string loadJson;
        try
        {
            loadJson = await core.CallDevToolsProtocolMethodAsync(
                "Network.loadNetworkResource",
                JsonSerializer.Serialize(new
                {
                    frameId,
                    url = mediaUri.ToString(),
                    options = new { disableCache = true, includeCredentials = true },
                })).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            return $"浏览器下载失败：{ErrorChainFormatter.Format(ex)}";
        }

        var resource = ParseLoadResource(loadJson);
        if (!resource.Success || resource.Stream is null)
        {
            return $"浏览器下载失败：资源请求被拒（netError={resource.NetErrorName ?? "-"}，HTTP {(resource.HttpStatusCode ?? 0)}）。";
        }

        // 第三步：流式读取响应体并写入 .part
        try
        {
            await using var stream = new FileStream(
                partPath, FileMode.Create, FileAccess.Write, FileShare.None, ReadChunkSize, useAsync: true);
            long position = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunkJson = await core.CallDevToolsProtocolMethodAsync(
                    "IO.read",
                    JsonSerializer.Serialize(new { handle = resource.Stream, size = ReadChunkSize })).ConfigureAwait(true);
                var chunk = ParseIoChunk(chunkJson);
                if (chunk.Data.Length == 0)
                {
                    return "浏览器下载中断：媒体流读取为空。";
                }

                byte[] bytes;
                try
                {
                    bytes = chunk.Base64Encoded ? Convert.FromBase64String(chunk.Data) : System.Text.Encoding.UTF8.GetBytes(chunk.Data);
                }
                catch (FormatException)
                {
                    return "浏览器下载中断：媒体流数据损坏。";
                }

                if (bytes.Length == 0)
                {
                    return "浏览器下载中断：媒体流无数据。";
                }

                position += bytes.Length;
                if (maxBytes is not null && position > maxBytes)
                {
                    return "媒体内容超过大小限制。";
                }

                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(true);
                progress?.Invoke(position, resource.ContentLength);
                if (chunk.Eof)
                {
                    break;
                }
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"浏览器下载中断：{ErrorChainFormatter.Format(ex)}";
        }
        finally
        {
            try
            {
                await core.CallDevToolsProtocolMethodAsync(
                    "IO.close",
                    JsonSerializer.Serialize(new { handle = resource.Stream })).ConfigureAwait(true);
            }
            catch
            {
                // 流已关闭等场景忽略
            }
        }

        return null;
    }

    // Page.getFrameTree 响应 → 主 frame id（纯函数，可离线测试）
    internal static string? ParseFrameId(string frameJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(frameJson);
            return doc.RootElement.TryGetProperty("frameTree", out var tree)
                && tree.ValueKind == JsonValueKind.Object
                && tree.TryGetProperty("frame", out var frame)
                && frame.ValueKind == JsonValueKind.Object
                && frame.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // .NET 10 TryGetProperty 对非对象会抛，防御兜底
            return null;
        }
    }

    // Network.loadNetworkResource 响应 → 资源结果（纯函数，可离线测试）
    internal static LoadResourceResult ParseLoadResource(string loadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(loadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("resource", out var resource)
                || resource.ValueKind != JsonValueKind.Object)
            {
                return LoadResourceResult.Failed;
            }

            var success = resource.TryGetProperty("success", out var successElement)
                && successElement.ValueKind == JsonValueKind.True;
            var stream = resource.TryGetProperty("stream", out var streamElement)
                && streamElement.ValueKind == JsonValueKind.String
                    ? streamElement.GetString()
                    : null;
            var netErrorName = resource.TryGetProperty("netErrorName", out var netErrorElement)
                && netErrorElement.ValueKind == JsonValueKind.String
                    ? netErrorElement.GetString()
                    : null;
            var httpStatus = resource.TryGetProperty("httpStatusCode", out var httpElement)
                && httpElement.ValueKind == JsonValueKind.Number
                    ? httpElement.GetInt32()
                    : (int?)null;
            var contentLength = resource.TryGetProperty("contentLength", out var lengthElement)
                && lengthElement.ValueKind == JsonValueKind.Number
                    ? lengthElement.GetInt64()
                    : (long?)null;
            return new LoadResourceResult(success, stream, netErrorName, httpStatus, contentLength);
        }
        catch (JsonException)
        {
            return LoadResourceResult.Failed;
        }
        catch (InvalidOperationException)
        {
            return LoadResourceResult.Failed;
        }
    }

    // IO.read 响应 → 数据块（纯函数，可离线测试）
    internal static IoChunk ParseIoChunk(string chunkJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(chunkJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return IoChunk.Empty;
            }

            var data = doc.RootElement.TryGetProperty("data", out var dataElement)
                && dataElement.ValueKind == JsonValueKind.String
                    ? dataElement.GetString() ?? string.Empty
                    : string.Empty;
            var base64 = doc.RootElement.TryGetProperty("base64Encoded", out var base64Element)
                && base64Element.ValueKind == JsonValueKind.True;
            var eof = doc.RootElement.TryGetProperty("eof", out var eofElement)
                && eofElement.ValueKind == JsonValueKind.True;
            return new IoChunk(data, base64, eof);
        }
        catch (JsonException)
        {
            return IoChunk.Empty;
        }
        catch (InvalidOperationException)
        {
            return IoChunk.Empty;
        }
    }

    internal sealed record LoadResourceResult(
        bool Success,
        string? Stream,
        string? NetErrorName,
        int? HttpStatusCode,
        long? ContentLength)
    {
        public static LoadResourceResult Failed => new(false, null, null, null, null);
    }

    internal sealed record IoChunk(string Data, bool Base64Encoded, bool Eof)
    {
        public static IoChunk Empty => new(string.Empty, false, false);
    }
}

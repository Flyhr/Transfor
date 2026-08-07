using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Transfor;

// 浏览器网络栈下载（Phase 4A）：在隐藏宿主的下载控件内以页面 fetch
// （credentials: include，自动携带浏览器 Cookie/Referer）拉取媒体，
// 分块经 ExecuteScriptAsync 传回 C# 流式写入 .part；
// 真实浏览器网络栈规避抖音对非浏览器 TLS 客户端的指纹拦截；
// 必须在 UI 线程调用（由隐藏宿主调度）
internal static class BrowserDownloadController
{
    // 单次分块字节数（base64 传输开销约 1.33x，2MB 块可控）
    private const int ChunkBytes = 2 * 1024 * 1024;

    private const string FetchMediaScript = @"(async () => {
        try {
            const response = await fetch(URL, { credentials: 'include' });
            if (!response.ok) return JSON.stringify({ error: 'HTTP ' + response.status });
            const bytes = new Uint8Array(await response.arrayBuffer());
            window.__mediaDownload = bytes;
            window.__mediaDownloadPos = 0;
            return JSON.stringify({ total: bytes.length });
        } catch (e) {
            return JSON.stringify({ error: String(e) });
        }
    })()";

    private const string ChunkScriptTemplate = @"(() => {
        const bytes = window.__mediaDownload;
        if (!bytes) return 'null';
        const start = window.__mediaDownloadPos;
        const end = Math.min(start + CHUNK, bytes.length);
        if (start >= end) return '';
        const slice = bytes.subarray(start, end);
        let binary = '';
        const step = 0x8000;
        for (let i = 0; i < slice.length; i += step) {
            binary += String.fromCharCode.apply(null, slice.subarray(i, i + step));
        }
        window.__mediaDownloadPos = end;
        return btoa(binary);
    })()";

    // 下载媒体到 partPath；返回 null 表示成功，否则错误信息；必须在 UI 线程调用
    public static async Task<string?> DownloadAsync(
        CoreWebView2 core,
        Uri mediaUri,
        string partPath,
        CancellationToken cancellationToken,
        Action<long, long?>? progress,
        long? maxBytes)
    {
        // 第一步：页面 fetch 拉取媒体到浏览器内存
        var fetchScript = FetchMediaScript.Replace("URL", JsonSerializer.Serialize(mediaUri.ToString()), StringComparison.Ordinal);
        var startRaw = await core.ExecuteScriptAsync(fetchScript).ConfigureAwait(false);
        long total;
        try
        {
            using var startDoc = JsonDocument.Parse(startRaw);
            if (startDoc.RootElement.TryGetProperty("error", out var errorElement))
            {
                return $"浏览器下载失败：{errorElement.GetString()}";
            }

            if (!startDoc.RootElement.TryGetProperty("total", out var totalElement))
            {
                return "浏览器下载失败：无法读取媒体大小。";
            }

            total = totalElement.GetInt64();
        }
        catch
        {
            return "浏览器下载失败：响应无法解析。";
        }

        if (total <= 0)
        {
            return "浏览器下载失败：媒体内容为空。";
        }

        if (maxBytes is not null && total > maxBytes)
        {
            return "媒体内容超过大小限制。";
        }

        // 第二步：分块取回 base64 并流式写入 .part
        var chunkScript = ChunkScriptTemplate.Replace("CHUNK", ChunkBytes.ToString(), StringComparison.Ordinal);
        await using (var stream = new FileStream(
            partPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
        {
            var position = 0L;
            while (position < total)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var raw = await core.ExecuteScriptAsync(chunkScript).ConfigureAwait(false);
                string? chunk;
                try
                {
                    // ExecuteScriptAsync 结果按 JSON 编码：字符串带引号，null 为 "null"
                    chunk = raw == "null" ? null : JsonSerializer.Deserialize<string>(raw);
                }
                catch (JsonException)
                {
                    return "浏览器下载中断：分块数据损坏。";
                }

                if (string.IsNullOrEmpty(chunk))
                {
                    return "浏览器下载中断。";
                }

                byte[] data;
                try
                {
                    data = Convert.FromBase64String(chunk);
                }
                catch (FormatException)
                {
                    return "浏览器下载中断：分块数据损坏。";
                }

                if (data.Length == 0)
                {
                    return "浏览器下载中断：无分块数据。";
                }

                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                position += data.Length;
                progress?.Invoke(position, total);
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return null;
    }
}

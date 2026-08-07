using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Transfor;

// 浏览器网络栈下载（Phase 4A）：在隐藏宿主的下载控件内以页面 fetch
// （credentials: include，自动携带浏览器 Cookie/Referer）拉取媒体，
// 经 response.body 流式逐块读取（每块转 base64 经 ExecuteScriptAsync 传回 C# 写盘）；
// 不整体加载进浏览器内存（大视频文件内存可控）；
// 真实浏览器网络栈规避抖音对非浏览器 TLS 客户端的指纹拦截；
// 必须在 UI 线程调用（由隐藏宿主调度）
internal static class BrowserDownloadController
{
    // 单次分块字节数（base64 传输开销约 1.33x，2MB 块可控）
    private const int ChunkBytes = 2 * 1024 * 1024;

    // 第一步：fetch 建立流式响应，reader 暂存于 window（跨脚本调用保持）；
    // 返回 { error } 或 { total }（content-length 可能缺失 → null）
    private const string PrepareScriptTemplate = @"(async () => {
        try {
            const response = await fetch(URL, { credentials: 'include' });
            if (!response.ok) return JSON.stringify({ error: 'HTTP ' + response.status });
            const total = Number(response.headers.get('content-length')) || null;
            const reader = response.body.getReader();
            window.__mediaReader = reader;
            return JSON.stringify({ total });
        } catch (e) {
            return JSON.stringify({ error: String(e) });
        }
    })()";

    // 第二步：从 reader 读取至多 CHUNK 字节（read() 可能返回小块，循环凑满）；
    // 返回 { error } / { done: true }（流结束）/ { chunk: base64 }（流式分块）
    private const string ChunkScriptTemplate = @"(async () => {
        try {
            const reader = window.__mediaReader;
            if (!reader) return JSON.stringify({ error: 'reader missing' });
            const CHUNK = CHUNK;
            const parts = [];
            let total = 0;
            while (total < CHUNK) {
                const { done, value } = await reader.read();
                if (done) { window.__mediaReader = null; break; }
                if (value.length > 0) { parts.push(value); total += value.length; }
            }
            if (parts.length === 0) return JSON.stringify({ done: true });
            const merged = new Uint8Array(total);
            let offset = 0;
            for (const part of parts) { merged.set(part, offset); offset += part.length; }
            let binary = '';
            const step = 0x8000;
            for (let i = 0; i < total; i += step) {
                binary += String.fromCharCode.apply(null, merged.subarray(i, i + step));
            }
            return JSON.stringify({ done: false, chunk: btoa(binary) });
        } catch (e) {
            return JSON.stringify({ error: String(e) });
        }
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
        // 第一步：页面 fetch 建立流式响应（不加载进内存）；
        // 注意：ExecuteScriptAsync 必须在 UI 线程调用，且循环的下一轮调用点
        // 位于本轮 continuation——任何 ConfigureAwait(false) 都会把调用点
        // 甩到线程池线程，触发 CoreWebView2 线程亲和检查崩溃
        var prepareScript = PrepareScriptTemplate.Replace("URL", JsonSerializer.Serialize(mediaUri.ToString()), StringComparison.Ordinal);
        var startRaw = await core.ExecuteScriptAsync(prepareScript).ConfigureAwait(true);
        long? total;
        try
        {
            using var startDoc = JsonDocument.Parse(startRaw);
            if (startDoc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return $"浏览器下载失败：响应无法解析（{TruncateForError(startRaw)}）。";
            }

            if (startDoc.RootElement.TryGetProperty("error", out var errorElement))
            {
                return $"浏览器下载失败：{errorElement.GetString()}";
            }

            if (startDoc.RootElement.TryGetProperty("total", out var totalElement) && totalElement.ValueKind == JsonValueKind.Number)
            {
                total = totalElement.GetInt64();
            }
            else
            {
                total = null;
            }
        }
        catch
        {
            return $"浏览器下载失败：响应无法解析（{TruncateForError(startRaw)}）。";
        }

        if (total is not null && total <= 0)
        {
            return "浏览器下载失败：媒体内容为空。";
        }

        if (total is not null && maxBytes is not null && total > maxBytes)
        {
            return "媒体内容超过大小限制。";
        }

        // 第二步：流式逐块读取 base64 并写入 .part
        var chunkScript = ChunkScriptTemplate.Replace("CHUNK", ChunkBytes.ToString(), StringComparison.Ordinal);
        await using (var stream = new FileStream(
            partPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
        {
            var position = 0L;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var raw = await core.ExecuteScriptAsync(chunkScript).ConfigureAwait(true);
                // 分块响应解析（纯函数，Null 安全）；Empty 状态带原始返回摘要，
                // 便于定位脚本行为（ExecuteScriptAsync 返回 null = 脚本执行失败）
                var response = ParseChunkResponse(raw);
                switch (response.Status)
                {
                    case ChunkParseStatus.Error:
                        return $"浏览器下载失败：{response.Error}";

                    case ChunkParseStatus.Done:
                        // 第一轮即结束：流未产出任何数据
                        if (position == 0)
                        {
                            return "浏览器下载失败：媒体内容为空（流未产出数据）。";
                        }
                        break;

                    case ChunkParseStatus.Empty:
                        return $"浏览器下载中断：脚本返回异常数据（{TruncateForError(raw)}）。";
                }

                if (response.Done)
                {
                    break;
                }

                if (string.IsNullOrEmpty(response.Chunk))
                {
                    return "浏览器下载中断。";
                }

                byte[] data;
                try
                {
                    data = Convert.FromBase64String(response.Chunk);
                }
                catch (FormatException)
                {
                    return "浏览器下载中断：分块数据损坏。";
                }

                if (data.Length == 0)
                {
                    return "浏览器下载中断：无分块数据。";
                }

                position += data.Length;
                if (maxBytes is not null && position > maxBytes)
                {
                    return "媒体内容超过大小限制。";
                }

                // 写入 continuation 必须回 UI 线程：下一轮 ExecuteScriptAsync 的调用点在此
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(true);
                progress?.Invoke(position, total);
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(true);
        }

        return null;
    }

    // 分块响应解析状态：Chunk 有效分块 / Done 流结束 / Error 脚本报错 / Empty 无有效数据
    internal enum ChunkParseStatus
    {
        Chunk,
        Done,
        Error,
        Empty,
    }

    // 分块响应解析（纯函数，可离线测试）：
    // 输入为 ExecuteScriptAsync 的 JSON 编码结果；脚本异常/JSON null/字段缺失
    // 一律不抛——error/done/chunk 按缺失语义返回，由调用方决策。
    // 注意：.NET 10 的 TryGetProperty 对非 Object 根元素会抛
    // InvalidOperationException（而非返回 false），必须先判 ValueKind
    internal static (ChunkParseStatus Status, string? Error, bool Done, string? Chunk) ParseChunkResponse(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (ChunkParseStatus.Empty, null, false, null);
            }

            var error = root.TryGetProperty("error", out var errorElement)
                && errorElement.ValueKind == JsonValueKind.String
                    ? errorElement.GetString()
                    : null;
            if (error is not null)
            {
                return (ChunkParseStatus.Error, error, false, null);
            }

            // ValueKind 直接比较而非 GetBoolean()：JSON null/非布尔不抛异常
            var done = root.TryGetProperty("done", out var doneElement)
                && doneElement.ValueKind == JsonValueKind.True;
            var chunk = root.TryGetProperty("chunk", out var chunkElement)
                && chunkElement.ValueKind == JsonValueKind.String
                    ? chunkElement.GetString()
                    : null;
            if (done)
            {
                return (ChunkParseStatus.Done, null, true, null);
            }

            return chunk is null
                ? (ChunkParseStatus.Empty, null, false, null)
                : (ChunkParseStatus.Chunk, null, false, chunk);
        }
        catch (JsonException)
        {
            return (ChunkParseStatus.Empty, null, false, null);
        }
        catch (InvalidOperationException)
        {
            // 非对象根元素（JSON null 等）的防御兜底
            return (ChunkParseStatus.Empty, null, false, null);
        }
    }

    // 错误消息摘要：截断脚本原始返回，避免海量 base64 刷屏
    private static string TruncateForError(string raw)
        => string.IsNullOrEmpty(raw)
            ? "(空)"
            : raw.Length <= 200 ? raw : raw[..200] + "…";
}

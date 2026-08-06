namespace Transfor;

// 媒体内容校验：HTTP 响应合理性（状态码/Content-Type/声明长度）与文件魔数（JPEG/PNG/GIF/WebP/MP4/WebM）
internal static class MediaContentValidator
{
    public const long DefaultMaxFileBytes = 4L * 1024 * 1024 * 1024;

    // 校验响应是否看起来是可下载媒体：2xx、Content-Type 匹配预期类型、声明长度未超限
    public static bool IsPlausibleResponse(
        HttpResponseMessage response,
        MediaKind expectedKind,
        long maxBytes,
        out string? error)
    {
        error = null;
        if (!response.IsSuccessStatusCode)
        {
            error = $"HTTP 状态码异常：{(int)response.StatusCode}";
            return false;
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!MatchesExpectedKind(contentType, expectedKind))
        {
            error = $"内容类型不符合预期：{contentType ?? "(空)"}";
            return false;
        }

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maxBytes)
        {
            error = "文件大小超过限制。";
            return false;
        }

        return true;
    }

    public static bool MatchesExpectedKind(string? contentType, MediaKind kind)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            // 未知 Content-Type 允许（由魔数校验兜底）
            return true;
        }

        var lower = contentType.ToLowerInvariant();
        return kind == MediaKind.Image
            ? lower.StartsWith("image/", StringComparison.Ordinal)
            : lower.StartsWith("video/", StringComparison.Ordinal);
    }

    // 魔数校验：可 Seek 流保存并恢复位置；不可 Seek 流只读取前缀，不要求恢复位置
    public static async Task<bool> HasValidMagicNumberAsync(
        Stream stream,
        MediaKind expectedKind,
        CancellationToken cancellationToken)
    {
        var header = new byte[12];
        var original = stream.CanSeek ? stream.Position : (long?)null;
        int read;
        try
        {
            read = await ReadUpToAsync(stream, header, header.Length, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (original is not null)
            {
                stream.Position = original.Value;
            }
        }

        return HasValidMagic(header.AsSpan(0, read), expectedKind);
    }

    // 按魔数识别具体格式并返回标准扩展名（.jpg/.png/.gif/.webp/.mp4/.webm）；
    // 无法识别返回 null；可 Seek 流恢复原位置
    public static async Task<string?> DetectExtensionAsync(
        Stream stream,
        MediaKind expectedKind,
        CancellationToken cancellationToken)
    {
        var header = new byte[12];
        var original = stream.CanSeek ? stream.Position : (long?)null;
        int read;
        try
        {
            read = await ReadUpToAsync(stream, header, header.Length, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (original is not null)
            {
                stream.Position = original.Value;
            }
        }

        return DetectExtension(header.AsSpan(0, read), expectedKind);
    }

    private static string? DetectExtension(ReadOnlySpan<byte> header, MediaKind kind)
    {
        if (kind == MediaKind.Image)
        {
            if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return ".jpg";
            if (header.Length >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
                && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A) return ".png";
            if (header.Length >= 4 && header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38) return ".gif";
            if (header.Length >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
                && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50) return ".webp";
            return null;
        }

        if (kind == MediaKind.Video)
        {
            if (header.Length >= 8 && header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70) return ".mp4";
            if (header.Length >= 4 && header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3) return ".webm";
            return null;
        }

        return null;
    }

    private static async Task<int> ReadUpToAsync(Stream stream, byte[] buffer, int max, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < max)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, max - total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }

    private static bool HasValidMagic(ReadOnlySpan<byte> header, MediaKind kind)
    {
        if (kind == MediaKind.Image)
        {
            // JPEG: FF D8 FF
            if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return true;
            // PNG: 89 50 4E 47 0D 0A 1A 0A
            if (header.Length >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
                && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A) return true;
            // GIF: 47 49 46 38
            if (header.Length >= 4 && header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38) return true;
            // WebP: RIFF .... WEBP
            if (header.Length >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
                && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50) return true;
        }
        else if (kind == MediaKind.Video)
        {
            // MP4: 偏移 4 处 ftyp
            if (header.Length >= 8 && header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70) return true;
            // WebM/Matroska: 1A 45 DF A3
            if (header.Length >= 4 && header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3) return true;
        }

        return false;
    }
}

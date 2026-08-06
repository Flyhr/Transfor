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
        => Detect(header, kind);

    // 统一魔数识别：返回标准扩展名（.jpg/.png/.gif/.webp/.heic/.avif/.bmp/.tiff/.mp4/.webm），
    // 无法识别返回 null；ftyp 开头的 ISO BMFF 文件按品牌区分：
    // heic/heix/hevc/mif1/msf1/avif/avis 为图片（实况/HEIF），avc1/isom/mp42 等为视频
    private static string? Detect(ReadOnlySpan<byte> header, MediaKind kind)
    {
        if (kind == MediaKind.Image)
        {
            if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return ".jpg";
            if (header.Length >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
                && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A) return ".png";
            if (header.Length >= 4 && header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38) return ".gif";
            if (header.Length >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
                && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50) return ".webp";
            // BMP: BM
            if (header.Length >= 2 && header[0] == 0x42 && header[1] == 0x4D) return ".bmp";
            // TIFF: II*\0 或 MM\0*
            if (header.Length >= 4
                && ((header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00)
                    || (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 && header[3] == 0x2A))) return ".tiff";
            // HEIC/HEIF/AVIF（ISO BMFF 图片品牌）
            var imageBrand = ReadBrand(header);
            if (imageBrand is not null && HeifImageBrands.Contains(imageBrand))
            {
                return imageBrand is "avif" or "avis" ? ".avif" : ".heic";
            }
            return null;
        }

        if (kind == MediaKind.Video)
        {
            var videoBrand = ReadBrand(header);
            if (videoBrand is not null && Mp4VideoBrands.Contains(videoBrand)) return ".mp4";
            if (header.Length >= 4 && header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3) return ".webm";
            return null;
        }

        return null;
    }

    // ISO BMFF 图片品牌（实况/HEIF/AVIF）
    private static readonly HashSet<string> HeifImageBrands = new(StringComparer.OrdinalIgnoreCase)
    {
        "heic", "heix", "hevc", "hevx", "mif1", "msf1", "avif", "avis",
    };

    // ISO BMFF 视频品牌（MP4 及其变体）
    private static readonly HashSet<string> Mp4VideoBrands = new(StringComparer.OrdinalIgnoreCase)
    {
        "avc1", "avc3", "mp42", "mp41", "isom", "iso2", "dash", "mp21", "M4V",
    };

    // 读取 ftyp 盒的 major brand（偏移 8-11）；非 ftyp 开头返回 null
    private static string? ReadBrand(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12
            || header[4] != 0x66 || header[5] != 0x74 || header[6] != 0x79 || header[7] != 0x70)
        {
            return null;
        }

        return System.Text.Encoding.ASCII.GetString(header.Slice(8, 4));
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
        => Detect(header, kind) is not null;
}

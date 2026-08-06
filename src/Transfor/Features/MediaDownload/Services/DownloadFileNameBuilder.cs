using System.Text;

namespace Transfor;

// 下载文件名构建：清理非法字符、解析扩展名、生成唯一路径；
// BuildUniquePath 只生成候选名，最终保存以独占创建/原子移动预留目标（由下载器执行）
internal static class DownloadFileNameBuilder
{
    // 文件名主体最大长度
    private const int MaxNameLength = 80;

    public static string SanitizeFileName(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            builder.Append(invalid.Contains(c) ? '_' : c);
        }

        var name = builder.ToString().TrimEnd('.', ' ');
        if (name.Length > MaxNameLength)
        {
            name = name[..MaxNameLength];
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "download";
        }

        return name;
    }

    // 移除标题中的 # 话题标签（如 "标题 # 话题1 # 话题2" → "标题"）；
    // 支持 # 与话题间有无空格；纯文本清理，不涉及 HTML 解析
    public static string StripHashtags(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return raw;
        }

        var cleaned = System.Text.RegularExpressions.Regex.Replace(raw, @"#\s*[^#\s]+", " ");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
        return cleaned;
    }

    // 根据 Content-Type 与媒体类型解析扩展名；
    // Content-Type 缺失或为泛化类型（如抖音 RENDER_DATA 图片的裸 "image"）时，
    // 按 URL 路径扩展名推断（抖音 CDN 图片 URL 带格式后缀，如 q80.jpeg）；
    // 仍无法确定时按类型返回通用后缀
    public static string ResolveExtension(string? contentType, MediaKind kind, string? urlPath = null)
    {
        var inferredFromUrl = false;
        if (contentType is not null)
        {
            var lower = contentType.ToLowerInvariant();
            switch (lower)
            {
                case "image/jpeg": return ".jpg";
                case "image/png": return ".png";
                case "image/gif": return ".gif";
                case "image/webp": return ".webp";
                case "video/mp4": return ".mp4";
                case "video/webm": return ".webm";
                case "video/quicktime": return ".mov";
                default:
                    // 未知子类型与裸类型（"image"/"video"）：继续尝试 URL 推断
                    if (lower.StartsWith("image/", StringComparison.Ordinal)
                        || lower.StartsWith("video/", StringComparison.Ordinal)
                        || lower is "image" or "video")
                    {
                        inferredFromUrl = true;
                    }
                    break;
            }
        }
        else
        {
            inferredFromUrl = true;
        }

        if (inferredFromUrl && !string.IsNullOrEmpty(urlPath))
        {
            // 剥离查询串/片段后再取扩展名（防御：调用方可能传完整 URL）
            var queryIndex = urlPath.IndexOfAny(new[] { '?', '#' });
            var pathOnly = queryIndex >= 0 ? urlPath[..queryIndex] : urlPath;
            var extension = Path.GetExtension(pathOnly)?.ToLowerInvariant();
            switch (extension)
            {
                case ".jpg":
                case ".jpeg":
                    return ".jpg";
                case ".png": return ".png";
                case ".gif": return ".gif";
                case ".webp": return ".webp";
                case ".bmp": return ".bmp";
                case ".avif": return ".avif";
                case ".mp4": return ".mp4";
                case ".webm": return ".webm";
                case ".mov": return ".mov";
            }
        }

        return kind == MediaKind.Image ? ".img" : ".bin";
    }

    // 生成目标目录内不冲突的完整路径；文件名包含路径分隔符或目录不是绝对路径时拒绝
    public static string BuildUniquePath(string directory, string fileName)
    {
        if (!Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException("目标目录必须是绝对路径。", nameof(directory));
        }

        if (fileName.Contains(Path.DirectorySeparatorChar) || fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("文件名不能包含路径分隔符。", nameof(fileName));
        }

        var candidate = Path.Combine(directory, fileName);
        var index = 1;
        while (File.Exists(candidate))
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            candidate = Path.Combine(directory, $"{name}({index}){ext}");
            index++;
        }

        return candidate;
    }

    // 校验候选路径确实位于目标目录内（拒绝含 .. 的父目录逃逸段）
    public static bool IsWithinDirectory(string directory, string path)
    {
        if (!Path.IsPathFullyQualified(directory) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        if (ContainsParentTraversal(directory) || ContainsParentTraversal(path))
        {
            return false;
        }

        var dir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        return full.Equals(dir, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsParentTraversal(string path)
    {
        foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment == "..")
            {
                return true;
            }
        }
        return false;
    }
}

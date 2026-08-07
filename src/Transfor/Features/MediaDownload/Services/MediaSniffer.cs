namespace Transfor;

// 媒体嗅探器（Phase 4B）：三层过滤识别网络捕获中的作品媒体——
// 1. 基础层：GET + 2xx + Content-Type 命中识别集
// 2. 关联层：URL 必须命中结构化 JSON 提取的作品媒体白名单（核心防误抓）
// 3. 噪音层：头像/Logo/表情/相关推荐等 URL 关键词丢弃
// 接口 JSON（详情接口）不产出媒体候选，仅用于识别接口 URL
internal sealed class MediaSniffer : IMediaSniffer
{
    // 噪音关键词（与 DouyinMediaNormalizer 的候选过滤保持一致）：
    // 头像/Logo/表情包/评论/相关推荐等非作品媒体
    private static readonly string[] NoiseKeywords =
    {
        "avatar", "icon", "logo", "recommend", "related", "suggest",
        "emoji", "emoticon", "sticker", "comment", "reply", "face",
    };

    public IReadOnlyList<BrowserCapturedCandidate> Sniff(
        IReadOnlyList<NetworkResourceRecord> records,
        string? structuredJson)
    {
        // 严格模式：无结构化数据（或提取不到 URL）→ 空白名单 → 零产出
        var whitelist = MediaUrlExtractor.ExtractUrls(structuredJson);
        if (whitelist.Count == 0)
        {
            return Array.Empty<BrowserCapturedCandidate>();
        }

        var results = new List<BrowserCapturedCandidate>();
        var orderIndex = 0;
        foreach (var record in records)
        {
            // 基础层：GET + 2xx + 识别 Content-Type
            if (!record.IsSuccessfulGet)
            {
                continue;
            }

            if (!TryClassify(record.ContentType, out var kind))
            {
                continue;
            }

            // 接口 JSON 不产出媒体候选（仅用于识别详情接口 URL）
            if (DouyinDetailEndpointMatcher.IsDetailEndpoint(record.Uri.ToString(), resourceType: null))
            {
                continue;
            }

            // 关联层：命中作品媒体白名单
            if (!whitelist.Contains(MediaUrlExtractor.NormalizeUrl(record.Uri)))
            {
                continue;
            }

            // 噪音层：头像/Logo/表情/相关推荐等丢弃
            if (IsNoise(record.Uri))
            {
                continue;
            }

            results.Add(new BrowserCapturedCandidate(
                record.Uri,
                kind,
                orderIndex++,
                null,
                null,
                record.ContentType,
                null,
                BrowserCandidateSource.Network));
        }

        return results;
    }

    // Content-Type → 媒体类型（识别集：图片 jpeg/png/webp/gif，视频 mp4/webm）
    internal static bool TryClassify(string? contentType, out MediaKind kind)
    {
        switch (contentType)
        {
            case "image/jpeg":
            case "image/png":
            case "image/webp":
            case "image/gif":
                kind = MediaKind.Image;
                return true;

            case "video/mp4":
            case "video/webm":
                kind = MediaKind.Video;
                return true;

            default:
                kind = MediaKind.Image; // 调用方只在 TryClassify 返回 true 时使用 kind
                return false;
        }
    }

    internal static bool IsNoise(Uri uri)
    {
        var lower = uri.ToString().ToLowerInvariant();
        return NoiseKeywords.Any(keyword => lower.Contains(keyword, StringComparison.Ordinal));
    }
}

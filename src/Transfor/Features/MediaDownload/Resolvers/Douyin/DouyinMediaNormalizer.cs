namespace Transfor;

// 抖音候选归一化：把解析出的候选转换为统一模型；
// 保持多图原始顺序、同一媒体多 URL 归入同一资产、去除重复 URL、
// 过滤头像/Logo/相关推荐等非作品媒体、不按文件扩展名单独判断媒体类型
internal static class DouyinMediaNormalizer
{
    // URL 中包含这些关键字的候选视为非作品媒体（头像/Logo/相关推荐等）
    private static readonly string[] FilterKeywords =
    {
        "avatar", "icon", "logo", "recommend", "related", "suggest",
    };

    public static ResolvedMediaPost Normalize(Uri sourceUri, DouyinPageData data)
    {
        var assets = new List<MediaAsset>(data.Assets.Count);
        foreach (var candidate in data.Assets)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var variants = new List<MediaVariant>();
            foreach (var variantCandidate in candidate.Variants)
            {
                if (IsNonWorkMedia(variantCandidate.Url))
                {
                    continue;
                }

                if (!seen.Add(variantCandidate.Url))
                {
                    // 完全相同的候选 URL 去重
                    continue;
                }

                if (!Uri.TryCreate(variantCandidate.Url, UriKind.Absolute, out var uri)
                    || uri.Scheme is not ("http" or "https"))
                {
                    continue;
                }

                variants.Add(new MediaVariant(
                    uri,
                    variantCandidate.Width,
                    variantCandidate.Height,
                    variantCandidate.FramesPerSecond,
                    variantCandidate.Bitrate,
                    variantCandidate.ContentLength,
                    variantCandidate.ContentType,
                    variantCandidate.Codec,
                    variantCandidate.Source,
                    new MediaRequestContext(sourceUri, null),
                    variantCandidate.IsSegmented));
            }

            if (variants.Count > 0)
            {
                assets.Add(new MediaAsset(candidate.OrderIndex, candidate.Kind, variants));
            }
        }

        return new ResolvedMediaPost(
            MediaProviderId.Douyin,
            sourceUri,
            data.PostId,
            data.Title,
            data.AuthorName,
            assets);
    }

    private static bool IsNonWorkMedia(string url)
    {
        var lower = url.ToLowerInvariant();
        return FilterKeywords.Any(keyword => lower.Contains(keyword, StringComparison.Ordinal));
    }
}

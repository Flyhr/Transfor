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

    // 浏览器捕获候选 → 页面数据兜底：结构化数据缺失时按候选构造资产；
    // 图片按 DOM 顺序保持多图次序，视频合并为单资产多变体；过滤非作品媒体
    public static DouyinPageData NormalizeCandidatesToPageData(IReadOnlyList<BrowserCapturedCandidate> candidates)
    {
        var imageCandidates = new List<(int Order, DouyinVariantCandidate Variant)>();
        var videoVariants = new List<DouyinVariantCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (candidate.Uri.Scheme is not ("http" or "https"))
            {
                continue;
            }

            if (IsNonWorkMedia(candidate.Uri.ToString()))
            {
                continue;
            }

            if (!seen.Add(candidate.Uri.ToString()))
            {
                continue;
            }

            var kind = candidate.Kind ?? InferKind(candidate.ContentType);
            var variant = new DouyinVariantCandidate(
                candidate.Uri.ToString(),
                candidate.ContentType,
                candidate.Width,
                candidate.Height,
                null,
                null,
                candidate.ContentLength,
                null,
                MapSource(candidate.Source));
            if (kind == MediaKind.Video)
            {
                videoVariants.Add(variant);
            }
            else if (kind == MediaKind.Image)
            {
                imageCandidates.Add((candidate.OrderIndex ?? imageCandidates.Count, variant));
            }
        }

        var assets = new List<DouyinAssetCandidate>();
        var imageIndex = 0;
        foreach (var image in imageCandidates.OrderBy(item => item.Order))
        {
            assets.Add(new DouyinAssetCandidate(imageIndex++, MediaKind.Image, new[] { image.Variant }));
        }
        if (videoVariants.Count > 0)
        {
            assets.Add(new DouyinAssetCandidate(0, MediaKind.Video, videoVariants));
        }

        return new DouyinPageData(null, null, null, assets, assets.Count == 0, false, null);
    }

    // 未标注类型的候选按 Content-Type 推断
    private static MediaKind? InferKind(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var lower = contentType.ToLowerInvariant();
        if (lower.StartsWith("image/", StringComparison.Ordinal))
        {
            return MediaKind.Image;
        }
        if (lower.StartsWith("video/", StringComparison.Ordinal))
        {
            return MediaKind.Video;
        }
        return null;
    }

    private static MediaVariantSource MapSource(BrowserCandidateSource source)
        => source == BrowserCandidateSource.Network ? MediaVariantSource.NetworkCapture : MediaVariantSource.Dom;

    private static bool IsNonWorkMedia(string url)
    {
        var lower = url.ToLowerInvariant();
        return FilterKeywords.Any(keyword => lower.Contains(keyword, StringComparison.Ordinal));
    }
}

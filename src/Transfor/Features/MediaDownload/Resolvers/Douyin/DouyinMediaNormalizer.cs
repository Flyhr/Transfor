namespace Transfor;

// 抖音候选归一化：把解析出的候选转换为统一模型；
// 保持多图原始顺序、同一媒体多 URL 归入同一资产、去除重复 URL、
// 过滤头像/Logo/相关推荐等非作品媒体、不按文件扩展名单独判断媒体类型
internal static class DouyinMediaNormalizer
{
    // URL 中包含这些关键字的候选视为非作品媒体（头像/Logo/表情包/评论/相关推荐等）
    private static readonly string[] FilterKeywords =
    {
        "avatar", "icon", "logo", "recommend", "related", "suggest",
        "emoji", "emoticon", "sticker", "comment", "reply", "face",
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
                assets.Add(new MediaAsset(
                    candidate.OrderIndex,
                    candidate.Kind,
                    variants,
                    candidate.SourceIndex,
                    candidate.Role,
                    candidate.PairId));
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
    // 先判断作品类型：视频 → 只取主视频；图片/实况 → 取全部图片（忽略封面/预览视频）；
    // videoPreferred 由页面 URL 形态给出（/video/ 视频、/note/ 图文/实况），
    // 未知时用启发式：有效图片候选 ≥ 2 且含视频候选 → 图片优先（实况/图文页多图+预览视频），否则视频优先
    public static DouyinPageData NormalizeCandidatesToPageData(
        IReadOnlyList<BrowserCapturedCandidate> candidates,
        bool? videoPreferred = null)
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
            else if (kind == MediaKind.Image && !IsLikelySmallImage(candidate))
            {
                // 头像/表情包等小图（宽或高 < 200px）不作为作品图片
                imageCandidates.Add((candidate.OrderIndex ?? imageCandidates.Count, variant));
            }
        }

        // 作品类型判断：显式偏好 > 启发式
        var imageFirst = videoPreferred == false
            || (videoPreferred is null && videoVariants.Count > 0 && imageCandidates.Count >= 2);

        var assets = new List<DouyinAssetCandidate>();
        if (!imageFirst && videoVariants.Count > 0)
        {
            // 视频作品：只取主视频，页面图片（封面/头像/表情包/推荐）一律不收
            assets.Add(new DouyinAssetCandidate(0, MediaKind.Video, videoVariants));
        }
        else
        {
            // 图片/实况作品：全部图片按 DOM 顺序，忽略封面/预览视频
            var imageIndex = 0;
            foreach (var image in imageCandidates.OrderBy(item => item.Order))
            {
                assets.Add(new DouyinAssetCandidate(imageIndex++, MediaKind.Image, new[] { image.Variant }));
            }
        }

        return new DouyinPageData(null, null, null, assets, assets.Count == 0, false, null);
    }

    // 头像/表情包等小尺寸装饰图：宽高均已知且任一维度小于阈值时排除；
    // 尺寸未知（null）时保留（交由 URL 特征过滤）
    private static bool IsLikelySmallImage(BrowserCapturedCandidate candidate)
    {
        const int MinImageDimension = 200;
        return candidate.Width.HasValue
            && candidate.Height.HasValue
            && (candidate.Width.Value < MinImageDimension || candidate.Height.Value < MinImageDimension);
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

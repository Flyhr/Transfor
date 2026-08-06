namespace Transfor;

// 媒体质量选择器：从资产的多个变体中选出当前可下载的最高质量版本；
// 仅有分段候选时返回 UnsupportedSegmented，不伪装成可下载单文件
internal static class MediaQualitySelector
{
    // 至少 720p 的判定阈值：1280×720
    private const long BalancedMinimumArea = 1280L * 720;

    // 来源可信度排序（数值越小越可信）
    private static readonly int[] SourceRank =
    {
        (int)MediaVariantSource.StructuredData,
        (int)MediaVariantSource.InlineState,
        (int)MediaVariantSource.JsonLd,
        (int)MediaVariantSource.Dom,
        (int)MediaVariantSource.NetworkCapture,
        (int)MediaVariantSource.Thumbnail,
    };

    public static MediaSelectionResult SelectBest(
        MediaAsset asset,
        MediaQualityPreference preference)
    {
        if (asset.Variants.Count == 0)
        {
            return new(MediaSelectionStatus.NoUsableVariant, null, "没有可用的媒体变体。");
        }

        // 第一版只选择可直接下载的单文件（非分段）
        var pool = asset.Variants.Where(v => !v.IsSegmented).ToList();
        if (pool.Count == 0)
        {
            return new(MediaSelectionStatus.UnsupportedSegmented, null, "已发现更高质量的分段媒体流，但当前版本暂不支持合并。");
        }

        // 图片与视频：存在非缩略图候选时，完全排除缩略图池
        // （视频封面是 JPEG 图片，绝不能作为视频变体参与下载）
        if (pool.Any(v => v.Source != MediaVariantSource.Thumbnail))
        {
            pool = pool.Where(v => v.Source != MediaVariantSource.Thumbnail).ToList();
        }

        var ordered = pool.OrderBy(v => v, new VariantComparer(asset.Kind)).ToList();
        MediaVariant? selected = preference switch
        {
            MediaQualityPreference.Balanced => PickBalanced(ordered),
            _ => ordered[0],
        };

        if (selected is null)
        {
            return new(MediaSelectionStatus.NoUsableVariant, null, "没有可用的媒体变体。");
        }

        return new(MediaSelectionStatus.Selected, selected, null);
    }

    // Balanced：优先至少 720p 的最高可访问变体；不存在时回退到排序后的第一个
    private static MediaVariant? PickBalanced(List<MediaVariant> ordered)
    {
        return ordered.FirstOrDefault(v => (v.Width ?? 0) * (long)(v.Height ?? 0) >= BalancedMinimumArea) ?? ordered[0];
    }

    // 排序比较器：来源可信度 → 像素面积 → 帧率/码率（视频）或宽高（图片）→ 内容长度 → URI 字典序（平局）
    private sealed class VariantComparer : IComparer<MediaVariant>
    {
        private readonly MediaKind kind;

        public VariantComparer(MediaKind kind) => this.kind = kind;

        public int Compare(MediaVariant? x, MediaVariant? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var cmp = SourceRank[(int)x.Source].CompareTo(SourceRank[(int)y.Source]);
            if (cmp != 0) return -cmp;

            var areaX = (x.Width ?? 0) * (long)(x.Height ?? 0);
            var areaY = (y.Width ?? 0) * (long)(y.Height ?? 0);
            cmp = areaX.CompareTo(areaY);
            if (cmp != 0) return -cmp;

            if (kind == MediaKind.Video)
            {
                cmp = (x.FramesPerSecond ?? 0).CompareTo(y.FramesPerSecond ?? 0);
                if (cmp != 0) return -cmp;

                cmp = (x.Bitrate ?? 0).CompareTo(y.Bitrate ?? 0);
                if (cmp != 0) return -cmp;

                cmp = CodecRank(x.Codec).CompareTo(CodecRank(y.Codec));
                if (cmp != 0) return -cmp;
            }
            else
            {
                cmp = (x.Width ?? 0).CompareTo(y.Width ?? 0);
                if (cmp != 0) return -cmp;

                cmp = (x.Height ?? 0).CompareTo(y.Height ?? 0);
                if (cmp != 0) return -cmp;
            }

            cmp = (x.ContentLength ?? 0).CompareTo(y.ContentLength ?? 0);
            if (cmp != 0) return -cmp;

            return string.CompareOrdinal(x.Uri.ToString(), y.Uri.ToString());
        }

        // 编码兼容性：H.264/AVC、H.265/HEVC 优先，未知视为中性，其余靠后
        private static int CodecRank(string? codec)
        {
            if (codec is null) return 1;
            var lower = codec.ToLowerInvariant();
            if (lower.Contains("h264") || lower.Contains("avc") || lower.Contains("h265") || lower.Contains("hevc")) return 0;
            return 2;
        }
    }
}

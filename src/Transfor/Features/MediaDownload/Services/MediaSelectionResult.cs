namespace Transfor;

// 质量选择结果状态
internal enum MediaSelectionStatus
{
    // 已选出可下载变体
    Selected,
    // 仅有分段媒体流（当前版本不支持合并）
    UnsupportedSegmented,
    // 没有可用变体
    NoUsableVariant,
}

internal sealed record MediaSelectionResult(
    MediaSelectionStatus Status,
    MediaVariant? Variant,
    string? Message);

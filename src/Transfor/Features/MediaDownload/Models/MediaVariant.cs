namespace Transfor;

// 媒体变体：同一张图片或视频的不同质量版本
internal sealed record MediaVariant(
    Uri Uri,
    int? Width,
    int? Height,
    int? FramesPerSecond,
    long? Bitrate,
    long? ContentLength,
    string? ContentType,
    string? Codec,
    MediaVariantSource Source,
    MediaRequestContext RequestContext,
    bool IsSegmented = false);

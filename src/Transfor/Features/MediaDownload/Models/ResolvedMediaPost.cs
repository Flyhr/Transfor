namespace Transfor;

// 解析后的媒体作品：含标题、作者与按原始顺序排列的资产列表
internal sealed record ResolvedMediaPost(
    MediaProviderId Provider,
    Uri SourceUri,
    string? PostId,
    string? Title,
    string? AuthorName,
    IReadOnlyList<MediaAsset> Assets);

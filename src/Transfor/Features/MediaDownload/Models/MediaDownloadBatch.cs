namespace Transfor;

// 下载批次：承载来源链接、作品与任务集合；任务本身不再重复保存这些字段
internal sealed record MediaDownloadBatch(
    Guid Id,
    string SourceShareLink,
    ResolvedMediaPost Post,
    IReadOnlyList<MediaDownloadTask> Tasks);

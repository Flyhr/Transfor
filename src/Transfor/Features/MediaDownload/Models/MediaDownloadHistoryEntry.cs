namespace Transfor;

// 一次下载批次的历史记录：只保存最终路径与计数，不保存 CDN 临时 URL、Cookie 或授权信息
internal sealed record MediaDownloadHistoryEntry(
    MediaProviderId Provider,
    string SourceShareLink,
    string? Title,
    string? SavedDirectory,
    IReadOnlyList<string> SavedFiles,
    int SuccessCount,
    int FailureCount,
    int CancelledCount,
    DateTimeOffset DownloadedAtUtc);

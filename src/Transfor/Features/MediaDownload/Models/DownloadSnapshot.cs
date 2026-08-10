namespace Transfor;

// 下载任务阶段（快照用）：等待中 / 下载中 / 已落定
internal enum DownloadPhase
{
    Pending,
    Downloading,
    Completed,
}

// 下载任务快照（Phase 6 M3 数据源）：活动与排队批次的任务状态、进度与终态；
// 由 MediaDownloadCoordinator.GetSnapshot 生成；批次落定后随运行时状态一并清理
internal sealed record DownloadSnapshot(
    Guid BatchId,
    Guid TaskId,
    DownloadPhase Phase,
    MediaDownloadStatus? Status,
    int AssetIndex,
    MediaKind Kind,
    string TargetPath,
    long BytesDownloaded,
    long? TotalBytes,
    double? Percent,
    string? Error,
    string? SavedPath);

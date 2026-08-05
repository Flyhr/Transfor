namespace Transfor;

// 单个下载任务完成事件参数：包含批次 ID 与最终结果（含 SavedPath）
internal sealed record MediaDownloadTaskCompleted(
    Guid BatchId,
    Guid TaskId,
    MediaDownloadResult Result);

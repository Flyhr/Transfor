namespace Transfor;

// 单次下载的结果
internal sealed record MediaDownloadResult(
    Guid TaskId,
    MediaDownloadStatus Status,
    string? Error,
    string? SavedPath)
{
    public static MediaDownloadResult Success(Guid taskId, string savedPath) =>
        new(taskId, MediaDownloadStatus.Succeeded, null, savedPath);

    public static MediaDownloadResult Failed(Guid taskId, string error) =>
        new(taskId, MediaDownloadStatus.Failed, error, null);

    public static MediaDownloadResult Cancelled(Guid taskId) =>
        new(taskId, MediaDownloadStatus.Cancelled, null, null);
}

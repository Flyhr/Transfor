namespace Transfor;

// 下载进度报告
internal sealed record MediaDownloadProgress(
    Guid TaskId,
    long BytesDownloaded,
    long? TotalBytes,
    double? Percent)
{
    // 未知总大小时 Percent 为 null，避免除零
    public static MediaDownloadProgress Create(
        Guid taskId,
        long bytesDownloaded,
        long? totalBytes)
    {
        double? percent = totalBytes is > 0
            ? Math.Min(100d, bytesDownloaded * 100d / totalBytes.Value)
            : null;

        return new MediaDownloadProgress(
            taskId,
            bytesDownloaded,
            totalBytes,
            percent);
    }
}

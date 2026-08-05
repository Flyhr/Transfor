namespace Transfor;

// 媒体下载服务契约：只负责把单个变体下载到目标文件，不了解任何平台页面结构
internal interface IMediaDownloadService
{
    Task<MediaDownloadResult> DownloadAsync(
        MediaDownloadTask task,
        CancellationToken cancellationToken,
        IProgress<MediaDownloadProgress>? progress = null);
}

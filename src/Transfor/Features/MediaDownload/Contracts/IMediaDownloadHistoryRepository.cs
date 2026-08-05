namespace Transfor;

// 媒体下载历史仓库契约
internal interface IMediaDownloadHistoryRepository
{
    IReadOnlyList<MediaDownloadHistoryEntry> GetHistory();

    void Add(MediaDownloadHistoryEntry entry);
}

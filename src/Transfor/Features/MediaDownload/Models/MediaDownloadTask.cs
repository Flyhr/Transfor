namespace Transfor;

// 单个媒体下载任务：来源链接与作品信息由 MediaDownloadBatch 承载，此处不重复保存
internal sealed record MediaDownloadTask(
    Guid Id,
    MediaAsset Asset,
    MediaVariant SelectedVariant,
    string TargetPath);

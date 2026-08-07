namespace Transfor;

// 更新检查契约：只负责「检查是否有更新」，不负责下载与安装（Phase 2 接入 Velopack）
internal interface IUpdateService
{
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken);
}

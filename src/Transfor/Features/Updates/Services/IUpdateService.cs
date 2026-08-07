namespace Transfor;

// 更新检查契约：只负责「检查是否有更新」，不负责下载与安装（下载安装由 IUpdateInstaller 负责）
internal interface IUpdateService
{
    Task<UpdateCheckResult> CheckForUpdatesAsync(UpdateChannel channel, CancellationToken cancellationToken);
}

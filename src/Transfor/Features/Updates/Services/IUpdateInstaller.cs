namespace Transfor;

// 更新下载进度：百分比 + 已下载/总量（字节）
internal sealed record UpdateDownloadProgress(int Percent, long BytesReceived, long BytesTotal);

// 更新安装器契约（Task 2.6 更新源抽象的一部分）：
// 下载阶段与决策（UpdateService）分离；实现可替换为 OSS/COS/自建服务器等更新源
internal interface IUpdateInstaller : IDisposable
{
    // 检查并下载最新更新包到本地暂存；失败抛异常，取消抛 OperationCanceledException；
    // 返回下载到的目标版本
    Task<UpdateVersion> DownloadAsync(IProgress<UpdateDownloadProgress> progress, CancellationToken cancellationToken);

    // 应用已暂存的更新并立即重启为新版本（进程随之退出）
    void ApplyAndRestart();
}

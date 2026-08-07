namespace Transfor;

// 更新策略源抽象：业务不依赖具体发布源；
// 后续可扩展 GitHubUpdateSource / HttpUpdateSource / OssUpdateSource（Phase 2 Task 2.6）
internal interface IUpdatePolicySource
{
    // 按通道取策略：stable 与 beta 各自独立的策略文件（URL 由实现决定）；
    // 返回 null 表示没有可用策略；网络/解析异常直接抛出，由调用方转为 CheckFailed
    Task<UpdatePolicy?> FetchAsync(UpdateChannel channel, CancellationToken cancellationToken);
}

namespace Transfor;

// 更新服务：读取远程策略 → 版本判断 → 返回检查结果；
// 任何失败都返回 CheckFailed（网络错误绝不升级为 RequiredUpdate，保证应用可运行）；
// 通道随调用传入（设置中可切换，无需重启生效）
internal sealed class UpdateService : IUpdateService
{
    private readonly IUpdatePolicySource source;
    private readonly string currentVersion;

    public UpdateService(IUpdatePolicySource source, string currentVersion)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.currentVersion = currentVersion;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(UpdateChannel channel, CancellationToken cancellationToken)
    {
        if (!UpdateVersion.TryParse(currentVersion, out var current))
        {
            return new UpdateCheckResult(UpdateStatus.CheckFailed, null, null, null, null, "当前应用版本号非法。");
        }

        UpdatePolicy policy;
        try
        {
            // 通道由策略源决定文件（stable/beta 各自独立策略），策略内的 channel 字段仅作说明
            policy = await source.FetchAsync(channel, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("未获取到更新策略。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 保留完整异常链（HttpRequestException → IOException → SocketException），便于诊断
            var error = ErrorChainFormatter.Format(ex);
            AppLog.Update.Warn($"更新检查失败：{error}");
            return new UpdateCheckResult(UpdateStatus.CheckFailed, current, null, null, null, error);
        }

        if (!policy.Enabled)
        {
            AppLog.Update.Info("更新已被远程禁用");
            return new UpdateCheckResult(UpdateStatus.Disabled, current, null, null, policy);
        }

        if (!UpdateVersion.TryParse(policy.LatestVersion, out var latest))
        {
            return new UpdateCheckResult(UpdateStatus.CheckFailed, current, null, null, policy, "远程最新版本号非法。");
        }

        UpdateVersion? minimum = null;
        if (!string.IsNullOrWhiteSpace(policy.MinimumVersion))
        {
            if (!UpdateVersion.TryParse(policy.MinimumVersion, out minimum))
            {
                return new UpdateCheckResult(UpdateStatus.CheckFailed, current, latest, null, policy, "远程最低版本号非法。");
            }
        }

        // 版本判断（计划 Task 1.4）：
        // 当前 >= 最新 → UpToDate；最低 <= 当前 < 最新 → OptionalUpdate；当前 < 最低 → RequiredUpdate
        if (current.CompareTo(latest) >= 0)
        {
            return new UpdateCheckResult(UpdateStatus.UpToDate, current, latest, minimum, policy);
        }

        if (minimum is not null && current.CompareTo(minimum) < 0)
        {
            return new UpdateCheckResult(UpdateStatus.RequiredUpdate, current, latest, minimum, policy);
        }

        return new UpdateCheckResult(UpdateStatus.OptionalUpdate, current, latest, minimum, policy);
    }
}

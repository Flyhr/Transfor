using Velopack;
using Velopack.Sources;

namespace Transfor;

// Velopack 更新安装器：GitHub Releases 作为第一阶段更新源；
// 负责检查（本地已暂存判定）、下载（含增量包回退）、应用并重启；
// 应用自身不覆盖运行中的 EXE，安装生命周期全部交给 Velopack
internal sealed class VelopackUpdateInstaller : IUpdateInstaller, IDisposable
{
    public const string RepositoryUrl = "https://github.com/Flyhr/Transfor";

    private readonly UpdateChannel channel;
    private UpdateManager? manager;

    public VelopackUpdateInstaller(UpdateChannel channel)
    {
        this.channel = channel;
    }

    public async Task<UpdateVersion> DownloadAsync(IProgress<UpdateDownloadProgress> progress, CancellationToken cancellationToken)
    {
        var updateManager = GetManager();
        var info = await updateManager.CheckForUpdatesAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("更新源暂无可用更新。");

        var target = info.TargetFullRelease;
        var totalBytes = target.Size;
        await updateManager.DownloadUpdatesAsync(info, percent =>
        {
            if (progress is not null)
            {
                progress.Report(new UpdateDownloadProgress(percent, totalBytes * percent / 100, totalBytes));
            }
        }, cancellationToken).ConfigureAwait(false);

        if (!UpdateVersion.TryParse(target.Version.ToString(), out var version))
        {
            throw new InvalidOperationException($"更新包版本号非法：{target.Version}");
        }

        return version!;
    }

    // 应用暂存更新并重启；toApply 传 null 时由 Velopack 自动选择最新已下载版本
    public void ApplyAndRestart()
    {
        GetManager().ApplyUpdatesAndRestart(toApply: null, restartArgs: null);
    }

    // 同一实例在 下载→应用 之间复用管理器（暂存状态在管理器内），避免重新定位
    private UpdateManager GetManager()
    {
        manager ??= CreateManager();
        return manager;
    }

    private UpdateManager CreateManager()
    {
        // Beta 通道读取 GitHub 预发布（prerelease）；Stable 只读正式发布
        var source = new GithubSource(RepositoryUrl, accessToken: null, prerelease: channel == UpdateChannel.Beta);
        var options = new UpdateOptions
        {
            ExplicitChannel = channel == UpdateChannel.Beta ? "beta" : "stable",
        };
        return new UpdateManager(source, options);
    }

    public void Dispose()
    {
        // UpdateManager 无托管资源需要释放，仅清除引用避免长期持有
        manager = null;
    }
}

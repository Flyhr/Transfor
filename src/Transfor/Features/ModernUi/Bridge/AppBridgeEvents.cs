namespace Transfor;

// App Bridge 事件推送（Phase 6 M1）：把下载协调器的后台线程事件
// marshal 到 UI 线程并推送为 {event, data} JSON 消息（PostWebMessageAsJson 必须 UI 线程）；
// 随 AppShellForm 生命周期挂接与摘除（AppShell 未打开时不挂接）
internal sealed class AppBridgeEvents : IDisposable
{
    private readonly MediaDownloadCoordinator coordinator;
    private readonly Control uiAnchor;
    private readonly Action<string> post;
    private bool disposed;

    public AppBridgeEvents(
        MediaDownloadCoordinator coordinator,
        Control uiAnchor,
        Action<string> post)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.uiAnchor = uiAnchor ?? throw new ArgumentNullException(nameof(uiAnchor));
        this.post = post ?? throw new ArgumentNullException(nameof(post));
        coordinator.TaskProgressChanged += OnTaskProgressChanged;
        coordinator.TaskCompleted += OnTaskCompleted;
        coordinator.BatchCompleted += OnBatchCompleted;
    }

    // 下载进度推送（含未知总大小时 percent 为 null）
    private void OnTaskProgressChanged(object? sender, MediaDownloadProgress progress)
        => Post(() => post(AppBridgeProtocol.CreateEvent("downloadProgress", CreateProgressPayload(progress))));

    // 单任务完成推送（succeeded/failed/cancelled）
    private void OnTaskCompleted(object? sender, MediaDownloadTaskCompleted completed)
        => Post(() => post(AppBridgeProtocol.CreateEvent("taskCompleted", CreateCompletedPayload(completed))));

    // 批次全部落定推送（含历史已写入）
    private void OnBatchCompleted(object? sender, Guid batchId)
        => Post(() => post(AppBridgeProtocol.CreateEvent("batchCompleted", new { batchId })));

    // 后台线程事件 → UI 线程执行（回调内访问 CoreWebView2 成员）
    private void Post(Action action)
    {
        if (uiAnchor.IsDisposed)
        {
            return;
        }

        if (uiAnchor.InvokeRequired)
        {
            uiAnchor.BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    // 进度载荷（纯函数，可离线测试）
    internal static object CreateProgressPayload(MediaDownloadProgress progress) => new
    {
        taskId = progress.TaskId,
        bytesDownloaded = progress.BytesDownloaded,
        totalBytes = progress.TotalBytes,
        percent = progress.Percent,
    };

    // 完成载荷（纯函数，可离线测试）
    internal static object CreateCompletedPayload(MediaDownloadTaskCompleted completed) => new
    {
        batchId = completed.BatchId,
        taskId = completed.TaskId,
        status = completed.Result.Status.ToString().ToLowerInvariant(),
        savedPath = completed.Result.SavedPath,
        error = completed.Result.Error,
    };

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        coordinator.TaskProgressChanged -= OnTaskProgressChanged;
        coordinator.TaskCompleted -= OnTaskCompleted;
        coordinator.BatchCompleted -= OnBatchCompleted;
    }
}

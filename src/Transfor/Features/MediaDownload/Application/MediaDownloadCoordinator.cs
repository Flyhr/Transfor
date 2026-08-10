namespace Transfor;

// 下载队列协调器：批次串行化（同一时间仅一个活动批次，后续批次排队），
// 每任务独立 CancellationTokenSource（链接批次取消源），进度/完成事件线程安全；
// 批次全部落定后向 MediaStateStore 写一条历史（成功/失败/取消计数）。
// CancelAllAsync 取消当前活动任务与所有排队批次，并等待全部落定
internal sealed class MediaDownloadCoordinator : IDisposable
{
    private readonly IMediaDownloadService downloadService;
    private readonly MediaStateStore stateStore;
    private readonly object sync = new();
    private readonly Queue<PendingBatch> pendingBatches = new();
    private readonly List<TaskCompletionSource<bool>> batchCompletions = new();
    private readonly Dictionary<Guid, CancellationTokenSource> taskCancellations = new();
    private readonly Dictionary<Guid, TaskRuntime> taskRuntimes = new();
    private MediaDownloadBatch? activeBatch;
    private bool disposed;

    public MediaDownloadCoordinator(
        IMediaDownloadService downloadService,
        MediaStateStore stateStore)
    {
        this.downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    public bool HasActiveTasks
    {
        get
        {
            lock (sync)
            {
                return activeBatch is not null || pendingBatches.Count > 0 || taskCancellations.Count > 0;
            }
        }
    }

    // 事件可能在后台线程触发；UI 必须通过 BeginInvoke 更新控件
    public event EventHandler<MediaDownloadProgress>? TaskProgressChanged;
    public event EventHandler<MediaDownloadTaskCompleted>? TaskCompleted;

    // 批次全部落定后触发（参数为批次 ID）；用于资源收尾（如关闭浏览器会话）
    public event EventHandler<Guid>? BatchCompleted;

    // 入队一个批次；批次串行执行，返回批次全部落定的完成信号
    public Task EnqueueBatchAsync(
        MediaDownloadBatch batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Tasks.Count == 0)
        {
            throw new ArgumentException("批次不能没有任务。", nameof(batch));
        }

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (disposed)
            {
                throw new InvalidOperationException("协调器已经释放。");
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var batchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pendingBatches.Enqueue(new PendingBatch(batch, batchCts, completion));
            batchCompletions.Add(completion);
            // 注册任务运行时状态（快照数据源；等待中 → 下载中 → 已落定）
            for (var i = 0; i < batch.Tasks.Count; i++)
            {
                var task = batch.Tasks[i];
                taskRuntimes[task.Id] = new TaskRuntime
                {
                    BatchId = batch.Id,
                    Task = task,
                    SourceShareLink = batch.SourceShareLink,
                    AssetIndex = i,
                };
            }

            ProcessNextIfIdle();
            return completion.Task;
        }
    }

    // 取消单个任务：活动任务取消其取消源（落定为 Cancelled 并触发完成事件）；
    // 排队批次中的任务取消整个排队批次（任务未开始：直接出队落定，不写历史）
    public void CancelTask(Guid taskId)
    {
        lock (sync)
        {
            if (taskCancellations.TryGetValue(taskId, out var cts))
            {
                cts.Cancel();
                return;
            }

            var pending = pendingBatches.FirstOrDefault(batch => batch.Batch.Tasks.Any(task => task.Id == taskId));
            if (pending is not null)
            {
                // 出队并完成批次信号（排队批次未启动：无任务事件/历史）
                var remaining = pendingBatches.Where(batch => !ReferenceEquals(batch, pending)).ToArray();
                pendingBatches.Clear();
                foreach (var batch in remaining)
                {
                    pendingBatches.Enqueue(batch);
                }

                batchCompletions.Remove(pending.Completion);
                foreach (var task in pending.Batch.Tasks)
                {
                    taskRuntimes.Remove(task.Id);
                }

                pending.BatchCts.Cancel();
                pending.BatchCts.Dispose();
                pending.Completion.TrySetResult(true);
            }
        }
    }

    // 当前全部任务快照（活动 + 排队批次；含进度与终态；批次落定后清理）
    public IReadOnlyList<DownloadSnapshot> GetSnapshot()
    {
        lock (sync)
        {
            return taskRuntimes.Values
                .OrderBy(runtime => runtime.BatchId)
                .Select(runtime =>
                {
                    var task = runtime.Task;
                    var result = runtime.Result;
                    return new DownloadSnapshot(
                        runtime.BatchId,
                        task.Id,
                        runtime.Phase,
                        result?.Status,
                        runtime.AssetIndex,
                        task.Asset.Kind,
                        task.TargetPath,
                        runtime.BytesDownloaded,
                        runtime.TotalBytes,
                        runtime.TotalBytes is > 0
                            ? Math.Min(100d, runtime.BytesDownloaded * 100d / runtime.TotalBytes.Value)
                            : null,
                        result?.Error,
                        result?.SavedPath);
                })
                .ToArray();
        }
    }

    // 取消全部活动任务与排队批次，并等待全部落定
    public async Task CancelAllAsync(CancellationToken cancellationToken = default)
    {
        List<CancellationTokenSource> sources;
        List<CancellationTokenSource> batchSources;
        Task[] completions;
        lock (sync)
        {
            sources = taskCancellations.Values.ToList();
            batchSources = pendingBatches.Select(b => b.BatchCts).ToList();
            completions = batchCompletions.Select(c => c.Task).ToArray();
        }

        foreach (var source in sources)
        {
            source.Cancel();
        }
        foreach (var source in batchSources)
        {
            // 排队批次可能在等待期间启动，先取消其批次取消源，保证启动即被取消
            source.Cancel();
        }

        foreach (var task in completions)
        {
            try
            {
                await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 批次因取消而结束属于预期
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;

            // 释放时取消所有活动与排队批次
            foreach (var source in taskCancellations.Values)
            {
                source.Cancel();
            }
            foreach (var pending in pendingBatches)
            {
                pending.BatchCts.Cancel();
            }
        }
    }

    // 处理下一个排队批次（在锁内调用）；批次串行化
    private void ProcessNextIfIdle()
    {
        if (activeBatch is not null)
        {
            return;
        }

        if (pendingBatches.Count == 0)
        {
            return;
        }

        var pending = pendingBatches.Dequeue();
        activeBatch = pending.Batch;
        _ = RunBatchAsync(pending);
    }

    private async Task RunBatchAsync(PendingBatch pending)
    {
        var batch = pending.Batch;
        var batchToken = pending.BatchCts.Token;
        var semaphore = new SemaphoreSlim(Math.Max(1, stateStore.Settings.MaxConcurrentDownloads));
        var taskSources = new Dictionary<Guid, CancellationTokenSource>();
        var results = new List<MediaDownloadResult>(batch.Tasks.Count);

        lock (sync)
        {
            foreach (var task in batch.Tasks)
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(batchToken);
                taskCancellations[task.Id] = cts;
                taskSources[task.Id] = cts;
            }
        }

        try
        {
            var tasks = batch.Tasks.Select(task => RunTaskAsync(batch, task, semaphore, taskSources[task.Id], batchToken)).ToList();
            var settled = await Task.WhenAll(tasks).ConfigureAwait(false);
            results.AddRange(settled);

            // 批次落定：写一条下载历史（成功/失败/取消计数），历史写入由 MediaStateStore 串行化
            var entry = new MediaDownloadHistoryEntry(
                batch.Post.Provider,
                batch.SourceShareLink,
                batch.Post.Title,
                Path.GetDirectoryName(batch.Tasks[0].TargetPath),
                results.Where(r => r.Status == MediaDownloadStatus.Succeeded).Select(r => r.SavedPath!).ToArray(),
                results.Count(r => r.Status == MediaDownloadStatus.Succeeded),
                results.Count(r => r.Status == MediaDownloadStatus.Failed),
                results.Count(r => r.Status == MediaDownloadStatus.Cancelled),
                DateTimeOffset.UtcNow);
            stateStore.Add(entry);
            BatchCompleted?.Invoke(this, batch.Id);
        }
        finally
        {
            lock (sync)
            {
                foreach (var pair in taskSources)
                {
                    taskCancellations.Remove(pair.Key);
                    pair.Value.Dispose();
                }

                // 批次落定：清理该批次的任务运行时状态（快照不再包含）
                foreach (var task in batch.Tasks)
                {
                    taskRuntimes.Remove(task.Id);
                }

                batchCompletions.Remove(pending.Completion);
                activeBatch = null;
                pending.Completion.TrySetResult(true);
                pending.BatchCts.Dispose();
                ProcessNextIfIdle();
            }
        }
    }

    private async Task<MediaDownloadResult> RunTaskAsync(
        MediaDownloadBatch batch,
        MediaDownloadTask task,
        SemaphoreSlim semaphore,
        CancellationTokenSource taskCts,
        CancellationToken batchToken)
    {
        SetRuntimePhase(task.Id, DownloadPhase.Downloading);
        MediaDownloadResult result;
        try
        {
            try
            {
                await semaphore.WaitAsync(batchToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (batchToken.IsCancellationRequested)
            {
                result = MediaDownloadResult.Cancelled(task.Id);
                return NotifyCompleted(batch, task, result);
            }

            try
            {
                result = await downloadService.DownloadAsync(
                    task,
                    taskCts.Token,
                    new Progress<MediaDownloadProgress>(p => OnTaskProgress(task.Id, p))).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (taskCts.IsCancellationRequested || batchToken.IsCancellationRequested)
            {
                result = MediaDownloadResult.Cancelled(task.Id);
            }
        }
        finally
        {
            semaphore.Release();
        }

        // 所有路径恰好一次完成事件（含取消：前端依赖 taskCompleted 更新任务状态）
        return NotifyCompleted(batch, task, result);
    }

    // 任务进度（后台线程）：更新运行时快照 + 触发进度事件
    private void OnTaskProgress(Guid taskId, MediaDownloadProgress progress)
    {
        lock (sync)
        {
            if (taskRuntimes.TryGetValue(taskId, out var runtime))
            {
                runtime.BytesDownloaded = progress.BytesDownloaded;
                runtime.TotalBytes = progress.TotalBytes;
            }
        }

        TaskProgressChanged?.Invoke(this, progress);
    }

    private void SetRuntimePhase(Guid taskId, DownloadPhase phase)
    {
        lock (sync)
        {
            if (taskRuntimes.TryGetValue(taskId, out var runtime))
            {
                runtime.Phase = phase;
            }
        }
    }

    // 触发任务完成事件并返回结果（统一出口，所有路径恰好一次）；
    // 同时把终态写入运行时快照（批次落定前保留，供快照/重试读取）
    private MediaDownloadResult NotifyCompleted(
        MediaDownloadBatch batch,
        MediaDownloadTask task,
        MediaDownloadResult result)
    {
        lock (sync)
        {
            if (taskRuntimes.TryGetValue(task.Id, out var runtime))
            {
                runtime.Phase = DownloadPhase.Completed;
                runtime.Result = result;
            }
        }

        TaskCompleted?.Invoke(this, new MediaDownloadTaskCompleted(batch.Id, task.Id, result));
        return result;
    }

    // 排队批次条目：批次 + 可取消的批次取消源 + 完成信号
    private sealed record PendingBatch(
        MediaDownloadBatch Batch,
        CancellationTokenSource BatchCts,
        TaskCompletionSource<bool> Completion);

    // 任务运行时状态（快照/重试数据源）：阶段、进度与终态；
    // SourceShareLink 与 AssetIndex 供进程内重试（重新解析后按原资产构造新任务）
    private sealed class TaskRuntime
    {
        public required Guid BatchId { get; init; }
        public required MediaDownloadTask Task { get; init; }
        public required string SourceShareLink { get; init; }
        public required int AssetIndex { get; init; }
        public DownloadPhase Phase;
        public long BytesDownloaded;
        public long? TotalBytes;
        public MediaDownloadResult? Result;
    }
}

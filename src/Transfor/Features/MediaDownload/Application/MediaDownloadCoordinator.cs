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
            ProcessNextIfIdle();
            return completion.Task;
        }
    }

    public void CancelTask(Guid taskId)
    {
        lock (sync)
        {
            if (taskCancellations.TryGetValue(taskId, out var cts))
            {
                cts.Cancel();
            }
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
        try
        {
            await semaphore.WaitAsync(batchToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (batchToken.IsCancellationRequested)
        {
            return MediaDownloadResult.Cancelled(task.Id);
        }

        try
        {
            var result = await downloadService.DownloadAsync(
                task,
                taskCts.Token,
                new Progress<MediaDownloadProgress>(p => TaskProgressChanged?.Invoke(this, p))).ConfigureAwait(false);

            TaskCompleted?.Invoke(this, new MediaDownloadTaskCompleted(batch.Id, task.Id, result));
            return result;
        }
        catch (OperationCanceledException) when (taskCts.IsCancellationRequested || batchToken.IsCancellationRequested)
        {
            return MediaDownloadResult.Cancelled(task.Id);
        }
        finally
        {
            semaphore.Release();
        }
    }

    // 排队批次条目：批次 + 可取消的批次取消源 + 完成信号
    private sealed record PendingBatch(
        MediaDownloadBatch Batch,
        CancellationTokenSource BatchCts,
        TaskCompletionSource<bool> Completion);
}

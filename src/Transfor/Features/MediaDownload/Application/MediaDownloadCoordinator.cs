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
    // 终态任务保留（真实任务队列：批次落定后行仍可见）；入队序完成序，超上限清理最旧
    private readonly Queue<Guid> completedOrder = new();
    private const int MaxRetainedCompletedTasks = 100;
    private long nextBatchSequence;
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
            // 注册任务运行时状态（快照数据源；等待中 → 下载中 → 已落定）；
            // AssetIndex 用真实媒体索引（实况图 still/motion 配对、前端缩略图匹配依赖它），
            // BatchSequence/TaskIndex 保证快照顺序稳定（批次入队序 + 批内创建序）
            var batchSequence = ++nextBatchSequence;
            for (var i = 0; i < batch.Tasks.Count; i++)
            {
                var task = batch.Tasks[i];
                taskRuntimes[task.Id] = new TaskRuntime
                {
                    BatchId = batch.Id,
                    BatchSequence = batchSequence,
                    TaskIndex = i,
                    Task = task,
                    Post = batch.Post,
                    SourceShareLink = batch.SourceShareLink,
                    AssetIndex = task.Asset.Index,
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

    // 当前全部任务快照（活动 + 排队批次 + 已保留的终态任务）；
    // 顺序稳定：批次按入队顺序，批内按任务创建顺序（不按随机 BatchId）
    public IReadOnlyList<DownloadSnapshot> GetSnapshot()
    {
        lock (sync)
        {
            return taskRuntimes.Values
                .OrderBy(runtime => runtime.BatchSequence)
                .ThenBy(runtime => runtime.TaskIndex)
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

    // 清除保留的终态任务（解析新作品时清空旧队列数据，下载历史只在历史页查看）；
    // 仅清理 Completed，活动/排队任务不受影响；HasActiveTasks 语义不变
    public void ClearRetainedTasks()
    {
        lock (sync)
        {
            while (completedOrder.Count > 0)
            {
                var id = completedOrder.Dequeue();
                if (taskRuntimes.TryGetValue(id, out var runtime) && runtime.Phase == DownloadPhase.Completed)
                {
                    taskRuntimes.Remove(id);
                }
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

    // 构造进程内重试候选：复用原任务 Asset/Variant（CDN URL 可能仍有效）与作品上下文，
    // 按统一文件名规则生成新目标路径；终态任务（失败/取消）在批次落定后仍可重试，
    // 重试构造成功后旧终态行被替换（前端刷新后不残留重复失败行）
    public RetryCandidate? CreateRetryTask(Guid taskId, string downloadDirectory)
    {
        lock (sync)
        {
            if (!taskRuntimes.TryGetValue(taskId, out var runtime))
            {
                return null;
            }

            // 仅失败/取消可重试（成功任务与进行中任务不可）
            if (runtime.Result is null || runtime.Result.Status == MediaDownloadStatus.Succeeded)
            {
                return null;
            }

            var asset = runtime.Task.Asset;
            var variant = runtime.Task.SelectedVariant;
            var fileName = DownloadFileNameBuilder.BuildFileName(runtime.Post, asset, variant);
            var target = DownloadFileNameBuilder.BuildUniquePath(downloadDirectory, fileName);
            var retryTask = new MediaDownloadTask(Guid.NewGuid(), asset, variant, target);
            // 终态替换：移除旧任务运行时（含完成序），新任务入队后成为唯一对应行
            taskRuntimes.Remove(taskId);
            var orderIds = completedOrder.ToList();
            if (orderIds.Remove(taskId))
            {
                completedOrder.Clear();
                foreach (var id in orderIds)
                {
                    completedOrder.Enqueue(id);
                }
            }

            return new RetryCandidate(runtime.SourceShareLink, runtime.Post, retryTask);
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

            // 释放时取消所有活动与排队批次，并清理全部运行时资源
            foreach (var source in taskCancellations.Values)
            {
                source.Cancel();
            }
            foreach (var pending in pendingBatches)
            {
                pending.BatchCts.Cancel();
            }

            taskCancellations.Clear();
            taskRuntimes.Clear();
            completedOrder.Clear();
            pendingBatches.Clear();
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
            AppLog.Download.Info($"批次 {batch.Id:N} 落定：成功 {results.Count(r => r.Status == MediaDownloadStatus.Succeeded)} / 失败 {results.Count(r => r.Status == MediaDownloadStatus.Failed)} / 取消 {results.Count(r => r.Status == MediaDownloadStatus.Cancelled)}");
            BatchCompleted?.Invoke(this, batch.Id);
            // 下载后打开目录设置：批次有成功文件时打开对应目录一次（尽力而为）
            TryOpenFolderOnBatchComplete(results);
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

                // 批次落定：终态任务运行时保留（真实任务队列：getDownloads 仍返回该批次任务行，
                // 进程内重试可用）；超过上限的最旧终态任务在 NotifyCompleted 中清理
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

    // 下载后打开目录设置：批次有成功文件时打开对应目录一次（尽力而为，失败不影响结果）
    private void TryOpenFolderOnBatchComplete(List<MediaDownloadResult> results)
    {
        if (!ShouldOpenFolderAfterDownload(stateStore.Settings, results))
        {
            return;
        }

        var savedPath = results.FirstOrDefault(r => r.Status == MediaDownloadStatus.Succeeded)?.SavedPath;
        if (savedPath is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(savedPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch
        {
            // 打开目录失败不影响下载结果
        }
    }

    // 是否应在批次落定后打开下载目录（纯函数，可离线测试）：
    // 设置开启且有成功文件时触发
    internal static bool ShouldOpenFolderAfterDownload(MediaDownloadSettings settings, IReadOnlyList<MediaDownloadResult> results)
        => settings.OpenFolderAfterDownload
           && results.Any(r => r.Status == MediaDownloadStatus.Succeeded && r.SavedPath is not null);

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
    // 同时把终态写入运行时快照并进入保留队列（批次落定后仍可快照/重试）；
    // 保留超过 MaxRetainedCompletedTasks 时清理最旧终态任务（仅 Completed，不影响活动任务）
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

            completedOrder.Enqueue(task.Id);
            while (completedOrder.Count > MaxRetainedCompletedTasks)
            {
                var oldest = completedOrder.Dequeue();
                taskRuntimes.Remove(oldest);
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
    // SourceShareLink 与 AssetIndex 供进程内重试（重新解析后按原资产构造新任务）；
    // BatchSequence/TaskIndex 保证快照顺序稳定（入队序 + 批内创建序）
    private sealed class TaskRuntime
    {
        public required Guid BatchId { get; init; }
        public required long BatchSequence { get; init; }
        public required int TaskIndex { get; init; }
        public required MediaDownloadTask Task { get; init; }
        public required ResolvedMediaPost Post { get; init; }
        public required string SourceShareLink { get; init; }
        public required int AssetIndex { get; init; }
        public DownloadPhase Phase;
        public long BytesDownloaded;
        public long? TotalBytes;
        public MediaDownloadResult? Result;
    }
}

// 进程内重试候选：原作品上下文 + 新构造的下载任务（复用 Asset/Variant）
internal sealed record RetryCandidate(
    string SourceShareLink,
    ResolvedMediaPost Post,
    MediaDownloadTask Task);

using System.Diagnostics;

namespace Transfor;

// App Bridge（Phase 5B + Phase 6）：处理 Web UI 发来的 JSON 消息并调用应用服务；
// 方法分发为纯逻辑（可离线测试），WebView2 消息桥接由 AppShellForm 负责；
// 媒体解析/下载能力随 Phase 6 页面迁移逐批接入；
// 剪贴板读取要求 UI 线程（消息处理起始线程满足）
internal sealed class AppBridge
{
    private readonly TextStateStore stateStore;
    private readonly IUpdateService updateService;
    private readonly MediaResolveCoordinator resolveCoordinator;
    private readonly MediaDownloadCoordinator downloadCoordinator;
    private readonly MediaStateStore mediaStateStore;
    private readonly MediaPreviewService previewService;

    // 最近一次解析结果（downloadSelected/getPreview 引用；作品变化需重新解析）
    private ResolvedMediaPost? lastPost;
    private string? lastShareLink;

    // 解析会话令牌：新解析使旧结果失效（防并发解析覆盖）
    private long resolveVersion;

    public AppBridge(
        TextStateStore stateStore,
        IUpdateService updateService,
        MediaResolveCoordinator resolveCoordinator,
        MediaDownloadCoordinator downloadCoordinator,
        MediaStateStore mediaStateStore,
        MediaPreviewService previewService)
    {
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        this.updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        this.resolveCoordinator = resolveCoordinator ?? throw new ArgumentNullException(nameof(resolveCoordinator));
        this.downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));
        this.mediaStateStore = mediaStateStore ?? throw new ArgumentNullException(nameof(mediaStateStore));
        this.previewService = previewService ?? throw new ArgumentNullException(nameof(previewService));
    }

    // 处理一条请求 JSON，返回应回发的响应 JSON；非法消息返回 null；
    // checkUpdate 涉及网络，整体异步（不阻塞 UI 线程）
    public async Task<string?> HandleAsync(string requestJson)
    {
        var request = AppBridgeProtocol.ParseRequest(requestJson);
        if (request is null)
        {
            return null;
        }

        try
        {
            return request.Method switch
            {
                "getAppInfo" => AppBridgeProtocol.CreateSuccessResponse(request.Id, GetAppInfo()),
                "getSettings" => AppBridgeProtocol.CreateSuccessResponse(request.Id, GetSettings()),
                "saveSettings" => AppBridgeProtocol.CreateSuccessResponse(request.Id, SaveSettings(request)),
                "checkUpdate" => await CheckUpdateAsync(request).ConfigureAwait(false),
                "getClipboardText" => await GetClipboardTextAsync(request).ConfigureAwait(false),
                "convertText" => AppBridgeProtocol.CreateSuccessResponse(request.Id, ConvertText(request)),
                "copyTextWithHistory" => AppBridgeProtocol.CreateSuccessResponse(request.Id, CopyTextWithHistory(request)),
                "resolveMedia" => await ResolveMediaAsync(request).ConfigureAwait(false),
                "getPreview" => await GetPreviewAsync(request).ConfigureAwait(false),
                "downloadSelected" => AppBridgeProtocol.CreateSuccessResponse(request.Id, DownloadSelected(request)),
                "getDownloads" => AppBridgeProtocol.CreateSuccessResponse(request.Id, GetDownloads()),
                "cancelTask" => AppBridgeProtocol.CreateSuccessResponse(request.Id, CancelTask(request)),
                "cancelAllDownloads" => AppBridgeProtocol.CreateSuccessResponse(request.Id, CancelAllDownloads()),
                "retryTask" => AppBridgeProtocol.CreateSuccessResponse(request.Id, RetryTask(request)),
                "openFile" => AppBridgeProtocol.CreateSuccessResponse(request.Id, OpenExistingPath(request, folder: false)),
                "openFolder" => AppBridgeProtocol.CreateSuccessResponse(request.Id, OpenExistingPath(request, folder: true)),
                "getHistory" => AppBridgeProtocol.CreateSuccessResponse(request.Id, GetHistory()),
                "clearHistory" => AppBridgeProtocol.CreateSuccessResponse(request.Id, ClearHistory(request)),
                "deleteHistoryEntry" => AppBridgeProtocol.CreateSuccessResponse(request.Id, DeleteHistoryEntry(request)),
                _ => AppBridgeProtocol.CreateErrorResponse(request.Id, $"未知方法：{request.Method}"),
            };
        }
        catch (Exception ex)
        {
            return AppBridgeProtocol.CreateErrorResponse(request.Id, ex.Message);
        }
    }

    private object GetAppInfo() => new
    {
        version = AppVersion.Current,
        channel = stateStore.Settings.UpdateChannel.ToString().ToLowerInvariant(),
    };

    private object GetSettings() => new
    {
        updateChannel = stateStore.Settings.UpdateChannel.ToString().ToLowerInvariant(),
        quoteHistoryLimit = stateStore.Settings.QuoteHistoryLimit,
        spaceHistoryLimit = stateStore.Settings.SpaceHistoryLimit,
    };

    private object SaveSettings(BridgeRequest request)
    {
        // 统一解析并校验全部参数：任一非法 → 抛错（响应 error），不落盘；
        // 全部合法 → 一次性 UpdateSettings（原子写入，任一写失败整体回滚）
        var next = stateStore.Settings;

        var channelText = request.GetString("updateChannel");
        if (!string.IsNullOrWhiteSpace(channelText))
        {
            if (!Enum.TryParse<UpdateChannel>(channelText, true, out var channel) || !Enum.IsDefined(channel))
            {
                throw new ArgumentException($"非法更新通道：{channelText}");
            }

            next = next with { UpdateChannel = channel };
        }

        var quoteText = request.GetString("quoteHistoryLimit");
        if (!string.IsNullOrWhiteSpace(quoteText))
        {
            if (!int.TryParse(quoteText, out var quote) || quote is < 1 or > 500)
            {
                throw new ArgumentException($"非法引号转换历史上限：{quoteText}");
            }

            next = next with { QuoteHistoryLimit = quote };
        }

        var spaceText = request.GetString("spaceHistoryLimit");
        if (!string.IsNullOrWhiteSpace(spaceText))
        {
            if (!int.TryParse(spaceText, out var space) || space is < 1 or > 500)
            {
                throw new ArgumentException($"非法去除空格历史上限：{spaceText}");
            }

            next = next with { SpaceHistoryLimit = space };
        }

        stateStore.UpdateSettings(next);
        return GetSettings();
    }

    private async Task<string> CheckUpdateAsync(BridgeRequest request)
    {
        var result = await updateService.CheckForUpdatesAsync(stateStore.Settings.UpdateChannel, CancellationToken.None)
            .ConfigureAwait(false);
        var status = result.Status switch
        {
            UpdateStatus.UpToDate => "upToDate",
            UpdateStatus.OptionalUpdate => "optionalUpdate",
            UpdateStatus.RequiredUpdate => "requiredUpdate",
            UpdateStatus.CheckFailed => "checkFailed",
            UpdateStatus.Disabled => "disabled",
            _ => "unknown",
        };
        return AppBridgeProtocol.CreateSuccessResponse(request.Id, new { status, version = AppVersion.Current });
    }

    // 文本转换（纯转换，不记录历史）：tool=quote 引号转换 / space 去除空格
    private static object ConvertText(BridgeRequest request)
    {
        var tool = request.GetString("tool");
        var input = request.GetString("input") ?? string.Empty;
        var output = tool switch
        {
            "quote" => QuoteConverter.Convert(input),
            "space" => SpaceRemover.Remove(input),
            var other => throw new ArgumentException($"非法文本工具：{other ?? "(空)"}"),
        };
        return new { output };
    }

    // 复制转换结果并记录转换历史（与旧界面行为一致：复制时记历史）；
    // 先记历史（主数据），再写剪贴板（重试应对占用）
    private object CopyTextWithHistory(BridgeRequest request)
    {
        var tool = request.GetString("tool");
        var input = request.GetString("input") ?? string.Empty;
        var output = request.GetString("output") ?? string.Empty;
        var textTool = tool switch
        {
            "quote" => TextToolId.QuoteConversion,
            "space" => TextToolId.SpaceRemoval,
            var other => throw new ArgumentException($"非法文本工具：{other ?? "(空)"}"),
        };

        stateStore.Add(new HistoryEntry(textTool, input, output, DateTimeOffset.UtcNow));

        // 剪贴板写入（要求 UI 线程；消息处理起始线程满足）
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                System.Windows.Forms.Clipboard.SetText(output);
                break;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                System.Threading.Thread.Sleep(100);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"复制失败：{ex.Message}");
            }
        }

        return new { copied = true };
    }

    // 读取剪贴板文本：后台线程 Win32 直读（OLE 剪贴板被占用时可能挂起 UI 线程），
    // 总超时 5 秒；失败返回错误说明而非抛出（不阻塞 UI 线程、不引发调用超时）
    private static async Task<string> GetClipboardTextAsync(BridgeRequest request)
    {
        const int timeoutSeconds = 5;
        object result;
        try
        {
            var text = await Task.Run(() => ClipboardTextReader.ReadText(TimeSpan.FromSeconds(timeoutSeconds)))
                .ConfigureAwait(false);
            result = new { text = text ?? string.Empty, error = (string?)null };
        }
        catch (Exception ex)
        {
            result = new { text = string.Empty, error = $"读取剪贴板失败：{ex.Message}" };
        }

        return AppBridgeProtocol.CreateSuccessResponse(request.Id, result);
    }

    // 解析分享链接（Automatic）：支持分享文本（经 ShareLinkParser 提取链接）；
    // 解析开始时清空旧作品状态（失败/交互不保留旧作品，防止误下载）；
    // 会话令牌防止并发解析结果互相覆盖
    private async Task<string> ResolveMediaAsync(BridgeRequest request)
    {
        var link = request.GetString("link");
        var uri = ShareLinkParser.TryExtractFirstLink(link, out var linkError);
        if (uri is null)
        {
            throw new ArgumentException(linkError ?? "未在文本中找到链接。");
        }

        // 新解析使旧结果立即失效（失败/交互也不保留旧作品）
        var version = ++resolveVersion;
        lastPost = null;
        lastShareLink = null;

        var result = await resolveCoordinator.ResolveAsync(
            new MediaResolveRequest(uri, MediaResolveMode.Automatic, new MediaRequestContext(null, null)),
            CancellationToken.None).ConfigureAwait(false);

        // 过期结果：期间发生了更新的解析，丢弃本次结果
        if (version != resolveVersion)
        {
            return AppBridgeProtocol.CreateSuccessResponse(request.Id, new { status = "stale", message = "解析结果已过期，请重试。" });
        }

        switch (result.Status)
        {
            case MediaResolveStatus.Succeeded when result.Post is not null:
                lastPost = result.Post;
                lastShareLink = link;
                return AppBridgeProtocol.CreateSuccessResponse(request.Id, new
                {
                    status = "succeeded",
                    post = BuildPostPayload(result.Post),
                });

            case MediaResolveStatus.RequiresUserInteraction:
                return AppBridgeProtocol.CreateSuccessResponse(request.Id, new
                {
                    status = "requiresInteraction",
                    message = result.Message,
                });

            default:
                return AppBridgeProtocol.CreateSuccessResponse(request.Id, new
                {
                    status = "failed",
                    message = result.Message,
                });
        }
    }

    // 预览：下载首选变体 → base64 data URL（限制 4MB，避免大 JSON 传输）
    private async Task<string> GetPreviewAsync(BridgeRequest request)
    {
        if (lastPost is null)
        {
            throw new InvalidOperationException("请先解析作品。");
        }

        var index = request.Parameters.HasValue
            && request.Parameters.Value.TryGetProperty("assetIndex", out var indexElement)
            && indexElement.ValueKind == System.Text.Json.JsonValueKind.Number
                ? indexElement.GetInt32()
                : -1;
        if (index < 0 || index >= lastPost.Assets.Count)
        {
            throw new ArgumentException($"媒体序号非法：{index}");
        }

        var asset = lastPost.Assets[index];
        if (asset.Kind != MediaKind.Image)
        {
            // 预览链路（MediaPreviewService）只接受图片内容；视频卡片展示元数据
            throw new InvalidOperationException("仅图片支持预览。");
        }

        var selection = MediaQualitySelector.SelectBest(asset, mediaStateStore.Settings.QualityPreference);
        if (selection.Status != MediaSelectionStatus.Selected || selection.Variant is null)
        {
            throw new InvalidOperationException("该媒体没有可预览的版本。");
        }

        var path = await previewService.DownloadPreviewAsync(selection.Variant, CancellationToken.None).ConfigureAwait(false);
        // 限制原始文件 ≤ 4MB（base64 data URL 膨胀约 1.33x，最终 Bridge 响应约 ≤5.3MB）
        var info = new FileInfo(path);
        if (info.Length > 4 * 1024 * 1024)
        {
            throw new InvalidOperationException("预览文件过大。");
        }

        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        var mime = selection.Variant.ContentType ?? (asset.Kind == MediaKind.Image ? "image/jpeg" : "video/mp4");
        return AppBridgeProtocol.CreateSuccessResponse(request.Id, new
        {
            dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}",
        });
    }

    // 下载选中资产：引用最近解析结果，C# 侧复用文件名规则构造任务并入队；
    // 入队即返回（进度经事件推送），批次串行执行
    private object DownloadSelected(BridgeRequest request)
    {
        if (lastPost is null)
        {
            throw new InvalidOperationException("请先解析作品。");
        }

        var shareLink = request.GetString("shareLink");
        if (string.IsNullOrWhiteSpace(shareLink) || !string.Equals(shareLink, lastShareLink, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("作品已变化，请重新解析。");
        }

        var indexes = request.Parameters.HasValue
            && request.Parameters.Value.TryGetProperty("assets", out var assetsElement)
            && assetsElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? assetsElement.EnumerateArray().Select(e => e.GetInt32()).ToArray()
                : Array.Empty<int>();
        if (indexes.Length == 0)
        {
            throw new ArgumentException("未选择任何媒体。");
        }

        var directory = mediaStateStore.Settings.DownloadDirectory;
        var tasks = new List<MediaDownloadTask>(indexes.Length);
        foreach (var index in indexes)
        {
            if (index < 0 || index >= lastPost.Assets.Count)
            {
                throw new ArgumentException($"媒体序号非法：{index}");
            }

            var asset = lastPost.Assets[index];
            var selection = MediaQualitySelector.SelectBest(asset, mediaStateStore.Settings.QualityPreference);
            if (selection.Status != MediaSelectionStatus.Selected || selection.Variant is null)
            {
                throw new InvalidOperationException($"媒体 {index + 1} 没有可下载的版本。");
            }

            var fileName = DownloadFileNameBuilder.BuildFileName(lastPost, asset, selection.Variant);
            var target = DownloadFileNameBuilder.BuildUniquePath(directory, fileName);
            tasks.Add(new MediaDownloadTask(Guid.NewGuid(), asset, selection.Variant, target));
        }

        var batch = new MediaDownloadBatch(Guid.NewGuid(), shareLink, lastPost, tasks);
        // 入队后立即返回；批次进度/完成经 AppBridgeEvents 推送
        _ = downloadCoordinator.EnqueueBatchAsync(batch, CancellationToken.None);
        return new { batchId = batch.Id, accepted = tasks.Count };
    }

    // 作品载荷：资产列表 + 每资产首选变体摘要（JS 侧渲染卡片）
    private object BuildPostPayload(ResolvedMediaPost post)
    {
        var preference = mediaStateStore.Settings.QualityPreference;
        return new
        {
            postId = post.PostId,
            title = post.Title,
            authorName = post.AuthorName,
            assets = post.Assets.Select((asset, index) =>
            {
                var selection = MediaQualitySelector.SelectBest(asset, preference);
                var variant = selection.Status == MediaSelectionStatus.Selected ? selection.Variant : null;
                return new
                {
                    index,
                    kind = asset.Kind.ToString().ToLowerInvariant(),
                    role = asset.Role.ToString().ToLowerInvariant(),
                    pairId = asset.PairId,
                    width = variant?.Width,
                    height = variant?.Height,
                    contentLength = variant?.ContentLength,
                    status = selection.Status.ToString(),
                    message = selection.Message,
                };
            }).ToArray(),
        };
    }

    // 当前下载任务快照（活动 + 排队批次；批次落定后清理）
    private object GetDownloads() => downloadCoordinator.GetSnapshot()
        .Select(s => new
        {
            batchId = s.BatchId,
            taskId = s.TaskId,
            phase = s.Phase.ToString().ToLowerInvariant(),
            status = s.Status?.ToString().ToLowerInvariant(),
            assetIndex = s.AssetIndex,
            kind = s.Kind.ToString().ToLowerInvariant(),
            targetPath = s.TargetPath,
            bytesDownloaded = s.BytesDownloaded,
            totalBytes = s.TotalBytes,
            percent = s.Percent,
            error = s.Error,
            savedPath = s.SavedPath,
        })
        .ToArray();

    // 取消单个任务（活动任务取消；排队批次出队落定）
    private object CancelTask(BridgeRequest request)
    {
        downloadCoordinator.CancelTask(ParseGuid(request, "taskId"));
        return new { cancelled = true };
    }

    // 取消全部活动与排队任务（后台执行，立即返回）
    private object CancelAllDownloads()
    {
        _ = downloadCoordinator.CancelAllAsync();
        return new { cancelled = true };
    }

    // 进程内重试：复用原任务 Asset/Variant 构造新任务入队；仅活动批次内有效
    private object RetryTask(BridgeRequest request)
    {
        var taskId = ParseGuid(request, "taskId");
        var candidate = downloadCoordinator.CreateRetryTask(taskId, mediaStateStore.Settings.DownloadDirectory);
        if (candidate is null)
        {
            throw new InvalidOperationException("任务已结束，无法重试。请在历史中重新执行。");
        }

        var batch = new MediaDownloadBatch(Guid.NewGuid(), candidate.SourceShareLink, candidate.Post, new[] { candidate.Task });
        _ = downloadCoordinator.EnqueueBatchAsync(batch, CancellationToken.None);
        return new { accepted = 1, batchId = batch.Id, taskId = candidate.Task.Id };
    }

    // 打开文件（默认程序）/ 打开所在文件夹（explorer 定位选中）；
    // 仅限下载目录内且存在的文件（防路径逃逸与任意文件）
    private object OpenExistingPath(BridgeRequest request, bool folder)
    {
        var path = request.GetString("path");
        if (string.IsNullOrWhiteSpace(path)
            || !ShouldAllowOpen(mediaStateStore.Settings.DownloadDirectory, path))
        {
            throw new ArgumentException("路径不在下载目录内或不存在。");
        }

        try
        {
            if (folder)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"打开失败：{ex.Message}");
        }

        return new { opened = true };
    }

    // 打开路径校验（纯函数，可离线测试）：下载目录内（无 .. 逃逸）且文件存在
    internal static bool ShouldAllowOpen(string downloadDirectory, string path) =>
        !string.IsNullOrWhiteSpace(downloadDirectory)
        && !string.IsNullOrWhiteSpace(path)
        && DownloadFileNameBuilder.IsWithinDirectory(downloadDirectory, path)
        && File.Exists(path);

    // 历史（合并：文本转换 + 媒体下载，带类型标记）
    private object GetHistory() => new
    {
        text = new
        {
            quote = stateStore.GetHistory(TextToolId.QuoteConversion)
                .Select(h => new { input = h.OriginalInput, output = h.ConvertedOutput, time = h.CreatedAtUtc })
                .ToArray(),
            space = stateStore.GetHistory(TextToolId.SpaceRemoval)
                .Select(h => new { input = h.OriginalInput, output = h.ConvertedOutput, time = h.CreatedAtUtc })
                .ToArray(),
        },
        media = mediaStateStore.GetHistory()
            .Select(h => new
            {
                provider = h.Provider.ToString().ToLowerInvariant(),
                sourceShareLink = h.SourceShareLink,
                title = h.Title,
                savedDirectory = h.SavedDirectory,
                savedFiles = h.SavedFiles,
                successCount = h.SuccessCount,
                failureCount = h.FailureCount,
                cancelledCount = h.CancelledCount,
                time = h.DownloadedAtUtc,
            })
            .ToArray(),
    };

    // 清空历史：text（tool=quote/space）/ media（整组）
    private object ClearHistory(BridgeRequest request)
    {
        switch (request.GetString("type"))
        {
            case "text":
                stateStore.ClearHistory(ParseTextTool(request));
                break;

            case "media":
                mediaStateStore.ClearHistory();
                break;

            default:
                throw new ArgumentException($"非法历史类型：{request.GetString("type") ?? "(空)"}");
        }

        return new { cleared = true };
    }

    // 删除单条历史：text（tool + index）/ media（index）
    private object DeleteHistoryEntry(BridgeRequest request)
    {
        var index = request.Parameters.HasValue
            && request.Parameters.Value.TryGetProperty("index", out var indexElement)
            && indexElement.ValueKind == System.Text.Json.JsonValueKind.Number
                ? indexElement.GetInt32()
                : -1;

        switch (request.GetString("type"))
        {
            case "text":
                stateStore.RemoveHistory(ParseTextTool(request), index);
                break;

            case "media":
                mediaStateStore.RemoveAt(index);
                break;

            default:
                throw new ArgumentException($"非法历史类型：{request.GetString("type") ?? "(空)"}");
        }

        return new { deleted = true };
    }

    private static TextToolId ParseTextTool(BridgeRequest request) => request.GetString("tool") switch
    {
        "quote" => TextToolId.QuoteConversion,
        "space" => TextToolId.SpaceRemoval,
        var other => throw new ArgumentException($"非法文本历史类型：{other ?? "(空)"}"),
    };

    private static Guid ParseGuid(BridgeRequest request, string name)
    {
        var value = request.GetString(name);
        if (!Guid.TryParse(value, out var guid))
        {
            throw new ArgumentException($"非法参数：{name}");
        }

        return guid;
    }
}

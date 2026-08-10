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
                "getClipboardText" => AppBridgeProtocol.CreateSuccessResponse(request.Id, GetClipboardText()),
                "resolveMedia" => await ResolveMediaAsync(request).ConfigureAwait(false),
                "getPreview" => await GetPreviewAsync(request).ConfigureAwait(false),
                "downloadSelected" => AppBridgeProtocol.CreateSuccessResponse(request.Id, DownloadSelected(request)),
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

    // 读取剪贴板文本（要求 UI 线程；消息处理起始线程满足）
    private static object GetClipboardText() => new
    {
        text = System.Windows.Forms.Clipboard.ContainsText()
            ? System.Windows.Forms.Clipboard.GetText()
            : string.Empty,
    };

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
}

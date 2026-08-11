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

    // 浏览器导航（AppShell 初始化互联网控件后设置）；未初始化时浏览器方法报错
    public BrowserNavigationService? BrowserNavigation { get; set; }

    // 浏览器控件显隐回调（AppShell 提供）：HTML 页面切换浏览器页时调用
    public Action<bool>? SetBrowserVisible { get; set; }

    // 侧边栏高亮回调（AppShell 提供）：HTML 内部跳转（如「查看媒体」）时同步宿主高亮
    public Action<string>? SetActiveNav { get; set; }

    // 主题回调（AppShell 提供）：HTML 主题切换（含跟随系统）时同步宿主侧边栏配色
    public Action<bool>? SetTheme { get; set; }

    // 热键服务（应用注入）；未设置时快捷键编辑报错
    public GlobalHotKeyManager? HotKeyManager { get; set; }

    // 下载目录浏览回调（AppShell 提供 FolderBrowserDialog）
    public Func<string?>? BrowseDirectory { get; set; }

    // 浏览器数据清除回调（AppShell 提供；scope=cookies/cache/all）
    public Func<string, Task>? ClearBrowserData { get; set; }

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
                "browserNavigate" => AppBridgeProtocol.CreateSuccessResponse(request.Id, BrowserNavigate(request)),
                "browserBack" => AppBridgeProtocol.CreateSuccessResponse(request.Id, BrowserAction(() => BrowserNavigation?.Back())),
                "browserForward" => AppBridgeProtocol.CreateSuccessResponse(request.Id, BrowserAction(() => BrowserNavigation?.Forward())),
                "browserRefresh" => AppBridgeProtocol.CreateSuccessResponse(request.Id, BrowserAction(() => BrowserNavigation?.Refresh())),
                "browserStop" => AppBridgeProtocol.CreateSuccessResponse(request.Id, BrowserAction(() => BrowserNavigation?.Stop())),
                "browserGetState" => AppBridgeProtocol.CreateSuccessResponse(request.Id, BrowserGetState()),
                "setBrowserVisible" => AppBridgeProtocol.CreateSuccessResponse(request.Id, SetBrowserVisibleState(request)),
                "setActiveNav" => AppBridgeProtocol.CreateSuccessResponse(request.Id, SetActiveNavState(request)),
                "setTheme" => AppBridgeProtocol.CreateSuccessResponse(request.Id, SetThemeState(request)),
                "browseDirectory" => AppBridgeProtocol.CreateSuccessResponse(request.Id, BrowseDirectoryState()),
                "clearBrowserData" => await ClearBrowserDataAsync(request).ConfigureAwait(false),
                "getRecent" => AppBridgeProtocol.CreateSuccessResponse(request.Id, GetRecent()),
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
        hotKey = new
        {
            modifiers = stateStore.Settings.HistoryHotKey.Modifiers.ToString(),
            key = stateStore.Settings.HistoryHotKey.Key.ToString(),
        },
        media = new
        {
            downloadDirectory = mediaStateStore.Settings.DownloadDirectory,
            maxConcurrentDownloads = mediaStateStore.Settings.MaxConcurrentDownloads,
            defaultSelectAll = mediaStateStore.Settings.DefaultSelectAll,
            openFolderAfterDownload = mediaStateStore.Settings.OpenFolderAfterDownload,
            qualityPreference = mediaStateStore.Settings.QualityPreference.ToString().ToLowerInvariant(),
            networkMode = mediaStateStore.Settings.NetworkMode.ToString().ToLowerInvariant(),
            proxyAddress = mediaStateStore.Settings.ProxyAddress,
        },
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

        // 快捷键（需热键服务）：先注册新热键（系统立即生效），再随文本设置持久化；
        // 持久化失败回滚热键注册并抛出
        var keyText = request.GetString("hotKeyKey");
        var modifiersText = request.GetString("hotKeyModifiers");
        if (keyText is not null || modifiersText is not null)
        {
            var newHotKey = ParseHotKey(keyText ?? string.Empty, modifiersText ?? string.Empty);
            if (HotKeyManager is null)
            {
                throw new InvalidOperationException("热键服务未初始化。");
            }

            if (!HotKeyManager.TryReplace(newHotKey, out var hotKeyError))
            {
                throw new InvalidOperationException(hotKeyError);
            }

            var previousHotKey = stateStore.Settings.HistoryHotKey;
            try
            {
                next = next with { HistoryHotKey = newHotKey };
            }
            catch
            {
                HotKeyManager.TryReplace(previousHotKey, out _);
                throw;
            }
        }

        // 文本设置一次性持久化（含热键；写失败时内存回滚且热键已注册的新值保留——
        // 旧界面同语义：注册成功即生效，持久化失败提示）
        stateStore.UpdateSettings(next);

        // 媒体设置（下载目录/并发/默认全选/打开目录/质量/网络模式/代理）
        var mediaNext = mediaStateStore.Settings;
        var directoryText = request.GetString("downloadDirectory");
        if (!string.IsNullOrWhiteSpace(directoryText))
        {
            mediaNext = mediaNext with { DownloadDirectory = directoryText };
        }

        var concurrency = request.GetInt32("maxConcurrentDownloads");
        if (concurrency is not null)
        {
            mediaNext = mediaNext with { MaxConcurrentDownloads = concurrency.Value };
        }

        var defaultSelectAll = request.GetBool("defaultSelectAll");
        if (defaultSelectAll is not null)
        {
            mediaNext = mediaNext with { DefaultSelectAll = defaultSelectAll.Value };
        }

        var openFolder = request.GetBool("openFolderAfterDownload");
        if (openFolder is not null)
        {
            mediaNext = mediaNext with { OpenFolderAfterDownload = openFolder.Value };
        }

        var qualityText = request.GetString("qualityPreference");
        if (!string.IsNullOrWhiteSpace(qualityText))
        {
            mediaNext = mediaNext with { QualityPreference = ParseEnum<MediaQualityPreference>(qualityText, "质量偏好") };
        }

        var networkText = request.GetString("networkMode");
        if (!string.IsNullOrWhiteSpace(networkText))
        {
            mediaNext = mediaNext with { NetworkMode = ParseEnum<MediaNetworkMode>(networkText, "网络模式") };
        }

        var proxyText = request.GetString("proxyAddress");
        if (proxyText is not null)
        {
            mediaNext = mediaNext with { ProxyAddress = proxyText };
        }

        // 媒体设置校验（并发/枚举/CustomProxy 代理地址/目录）后一次性持久化
        mediaNext.Validate();
        mediaStateStore.UpdateSettings(mediaNext);

        return GetSettings();
    }

    // 解析快捷键（modifiers 逗号分隔如 "Control,Alt"）；纯解析不注册
    private static HotKeyBinding ParseHotKey(string keyText, string modifiersText)
    {
        if (!Enum.TryParse<System.Windows.Forms.Keys>(keyText, true, out var key)
            || key == System.Windows.Forms.Keys.None)
        {
            throw new ArgumentException($"非法快捷键主键：{keyText}");
        }

        var modifiers = System.Windows.Forms.Keys.None;
        foreach (var part in modifiersText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<System.Windows.Forms.Keys>(part, true, out var modifier)
                || modifier is not (System.Windows.Forms.Keys.Control or System.Windows.Forms.Keys.Alt or System.Windows.Forms.Keys.Shift or System.Windows.Forms.Keys.LWin))
            {
                throw new ArgumentException($"非法修饰键：{part}");
            }

            modifiers |= modifier;
        }

        return HotKeyBinding.Create(modifiers, key);
    }

    private static TEnum ParseEnum<TEnum>(string text, string displayName)
        where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(text, true, out var value) || !Enum.IsDefined(value))
        {
            throw new ArgumentException($"非法{displayName}：{text}");
        }

        return value;
    }

    // 下载目录浏览（宿主弹系统目录对话框）
    private object BrowseDirectoryState() => new
    {
        path = BrowseDirectory?.Invoke(),
    };

    // 浏览器数据清除（scope=cookies/cache/all，宿主经共享 Profile 执行）
    private async Task<string> ClearBrowserDataAsync(BridgeRequest request)
    {
        var scope = request.GetString("scope");
        if (scope is not ("cookies" or "cache" or "all"))
        {
            throw new ArgumentException($"非法清除范围：{scope ?? "(空)"}");
        }

        if (ClearBrowserData is null)
        {
            throw new InvalidOperationException("浏览器服务不可用。");
        }

        await ClearBrowserData(scope).ConfigureAwait(false);
        return AppBridgeProtocol.CreateSuccessResponse(request.Id, new { cleared = true });
    }

    // 最近记录（文本两工具 + 媒体各最近 5 条，首页卡片用）
    private object GetRecent() => new
    {
        text = new
        {
            quote = stateStore.GetHistory(TextToolId.QuoteConversion)
                .TakeLast(5)
                .Select(h => new { input = h.OriginalInput, output = h.ConvertedOutput, time = h.CreatedAtUtc })
                .ToArray(),
            space = stateStore.GetHistory(TextToolId.SpaceRemoval)
                .TakeLast(5)
                .Select(h => new { input = h.OriginalInput, output = h.ConvertedOutput, time = h.CreatedAtUtc })
                .ToArray(),
        },
        media = mediaStateStore.GetHistory()
            .TakeLast(5)
            .Select(h => new
            {
                title = h.Title,
                sourceShareLink = h.SourceShareLink,
                successCount = h.SuccessCount,
                time = h.DownloadedAtUtc,
            })
            .ToArray(),
    };

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
            catch (Exception) when (attempt < maxAttempts)
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

    // 浏览器导航（地址规范化在 BrowserNavigationService.Navigate 内执行并校验）
    private object BrowserNavigate(BridgeRequest request)
    {
        var navigation = BrowserNavigation ?? throw new InvalidOperationException("浏览器尚未初始化。");
        var address = request.GetString("address");
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("请输入网址。");
        }

        navigation.Navigate(address);
        return new { ok = true };
    }

    // 浏览器动作（后退/前进/刷新/停止）
    private object BrowserAction(Action action)
    {
        if (BrowserNavigation is null)
        {
            throw new InvalidOperationException("浏览器尚未初始化。");
        }

        action();
        return new { ok = true };
    }

    // 浏览器状态（当前地址/能否后退/前进）
    private object BrowserGetState()
    {
        var navigation = BrowserNavigation;
        return new
        {
            url = navigation?.CurrentUrl,
            canGoBack = navigation?.CanGoBack ?? false,
            canGoForward = navigation?.CanGoForward ?? false,
        };
    }

    // 浏览器控件显隐（HTML 页面切换浏览器页时调用）；visible 缺失/非布尔 → 拒绝
    private object SetBrowserVisibleState(BridgeRequest request)
    {
        var visible = request.GetBool("visible")
            ?? throw new ArgumentException("参数 visible 必须为布尔值。");
        SetBrowserVisible?.Invoke(visible);
        return new { ok = true };
    }

    // 侧边栏高亮同步（HTML 内部跳转时通知宿主）
    private object SetActiveNavState(BridgeRequest request)
    {
        var page = request.GetString("page");
        if (string.IsNullOrWhiteSpace(page))
        {
            throw new ArgumentException("参数 page 不能为空。");
        }

        SetActiveNav?.Invoke(page);
        return new { ok = true };
    }

    // 主题同步（HTML 主题切换/跟随系统变化时通知宿主侧边栏配色）
    private object SetThemeState(BridgeRequest request)
    {
        var dark = request.GetBool("dark")
            ?? throw new ArgumentException("参数 dark 必须为布尔值。");
        SetTheme?.Invoke(dark);
        return new { ok = true };
    }
}

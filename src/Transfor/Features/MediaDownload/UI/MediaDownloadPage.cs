using System.Runtime.InteropServices;

namespace Transfor;

// 媒体页面状态机
internal enum MediaPageState
{
    Idle,
    Resolving,
    WaitingForUser,
    Resolved,
    Downloading,
    Completed,
    Failed,
}

// 媒体下载页面：输入分享文本 → 解析 → 选择资产 → 队列下载；
// 不引用任何具体平台解析器或 Edge CDP 类型
internal sealed class MediaDownloadPage : UserControl, IFeaturePage
{
    private readonly MediaResolveCoordinator resolveCoordinator;
    private readonly MediaDownloadCoordinator downloadCoordinator;
    private readonly MediaStateStore stateStore;
    private readonly Func<Control, ValueTask> ensureBrowserInitializedAsync;
    private readonly MediaPreviewService previewService;
    private readonly TextBox inputBox;
    private readonly Button pasteButton;
    private readonly Button parseButton;
    private readonly Button browserButton;
    private readonly Button clearButton;
    private readonly Label infoLabel;
    private readonly TextBox directoryBox;
    private readonly Button browseButton;
    private readonly Button settingsButton;
    private readonly Button selectAllButton;
    private readonly Button unselectAllButton;
    private readonly Button downloadButton;
    private readonly Label errorLabel;
    private readonly MediaAssetGrid assetGrid;
    private readonly DownloadQueueGrid queueGrid;
    private readonly MediaPreviewControl previewControl;
    private MediaPageState currentState = MediaPageState.Idle;
    private ResolvedMediaPost? currentPost;
    private string currentShareLink = string.Empty;
    private bool directoryOverridden;

    public MediaDownloadPage(
        MediaResolveCoordinator resolveCoordinator,
        MediaDownloadCoordinator downloadCoordinator,
        MediaStateStore stateStore,
        Func<Control, ValueTask> ensureBrowserInitializedAsync,
        MediaPreviewService previewService)
    {
        this.resolveCoordinator = resolveCoordinator ?? throw new ArgumentNullException(nameof(resolveCoordinator));
        this.downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        this.ensureBrowserInitializedAsync = ensureBrowserInitializedAsync ?? throw new ArgumentNullException(nameof(ensureBrowserInitializedAsync));
        this.previewService = previewService ?? throw new ArgumentNullException(nameof(previewService));

        Dock = DockStyle.Fill;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 6 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        // 分享输入行
        inputBox = new TextBox { Dock = DockStyle.Fill, Multiline = false };
        pasteButton = new Button { AutoSize = true, Text = "从剪贴板粘贴" };
        parseButton = new Button { AutoSize = true, Text = "解析" };
        browserButton = new Button { AutoSize = true, Text = "打开真实 Edge 登录", Enabled = true };
        clearButton = new Button { AutoSize = true, Text = "清空" };
        pasteButton.Click += (_, _) => PasteFromClipboard();
        parseButton.Click += (_, _) => _ = ParseCoreAsync();
        browserButton.Click += (_, _) => _ = ParseWithBrowserAsync();
        var inputRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inputRow.Controls.Add(inputBox, 0, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        buttons.Controls.AddRange(new Control[] { pasteButton, parseButton, browserButton, clearButton });
        inputRow.Controls.Add(buttons, 1, 0);
        root.Controls.Add(inputRow, 0, 0);

        // 作品信息
        infoLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        root.Controls.Add(infoLabel, 0, 1);

        // 资产表 + 预览区
        assetGrid = new MediaAssetGrid { Dock = DockStyle.Fill };
        previewControl = new MediaPreviewControl { Dock = DockStyle.Fill };
        var assetRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        assetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        assetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        assetRow.Controls.Add(assetGrid, 0, 0);
        assetRow.Controls.Add(previewControl, 1, 0);
        assetGrid.PreviewRequested += AssetGrid_PreviewRequested;
        root.Controls.Add(assetRow, 0, 2);

        // 保存目录行 + 批量操作
        directoryBox = new TextBox { Dock = DockStyle.Fill, ReadOnly = false };
        browseButton = new Button { AutoSize = true, Text = "选择目录" };
        settingsButton = new Button { AutoSize = true, Text = "媒体设置" };
        selectAllButton = new Button { AutoSize = true, Text = "全选", Enabled = false };
        unselectAllButton = new Button { AutoSize = true, Text = "取消全选", Enabled = false };
        downloadButton = new Button { AutoSize = true, Text = "下载所选", Enabled = false };
        browseButton.Click += (_, _) => BrowseDirectory();
        settingsButton.Click += (_, _) => OpenSettings();
        selectAllButton.Click += (_, _) => assetGrid.SelectAll();
        unselectAllButton.Click += (_, _) => assetGrid.UnselectAll();
        downloadButton.Click += (_, _) => _ = DownloadSelectedAsync();
        var actionRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionRow.Controls.Add(directoryBox, 0, 0);
        var actionButtons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        actionButtons.Controls.AddRange(new Control[] { browseButton, settingsButton, selectAllButton, unselectAllButton, downloadButton });
        actionRow.Controls.Add(actionButtons, 1, 0);
        root.Controls.Add(actionRow, 0, 3);

        // 下载队列
        queueGrid = new DownloadQueueGrid { Dock = DockStyle.Fill };
        queueGrid.OperationRequested += QueueGrid_OperationRequested;
        root.Controls.Add(queueGrid, 0, 4);

        // 错误/提示
        errorLabel = new Label { Dock = DockStyle.Fill, ForeColor = Color.Firebrick, TextAlign = ContentAlignment.MiddleLeft };
        root.Controls.Add(errorLabel, 0, 5);

        Controls.Add(root);
        directoryBox.Text = stateStore.Settings.DownloadDirectory;

        downloadCoordinator.TaskProgressChanged += DownloadCoordinator_TaskProgressChanged;
        downloadCoordinator.TaskCompleted += DownloadCoordinator_TaskCompleted;
        clearButton.Click += (_, _) => { inputBox.Clear(); errorLabel.Text = string.Empty; };
    }

    public string Id => "media-download";
    public string DisplayName => "媒体下载";
    public Control View => this;
    public void OnActivated() => inputBox.Focus();

    // 测试入口：注入文本并执行自动解析
    internal async Task ResolveInputAsync(string text)
    {
        inputBox.Text = text;
        await ParseCoreAsync();
    }

    internal MediaPageState CurrentState => currentState;
    internal string InputText => inputBox.Text;
    internal string DownloadDirectoryText => directoryBox.Text;
    internal bool BrowserButtonEnabled => browserButton.Enabled;
    internal bool ParseButtonEnabled => parseButton.Enabled;
    internal bool DownloadButtonEnabled => downloadButton.Enabled;
    internal bool PasteButtonEnabled => pasteButton.Enabled;
    internal bool BrowseButtonEnabled => browseButton.Enabled;
    internal bool SelectAllButtonEnabled => selectAllButton.Enabled;
    internal bool UnselectAllButtonEnabled => unselectAllButton.Enabled;
    internal string ErrorText => errorLabel.Text;

    private void PasteFromClipboard()
    {
        try
        {
            inputBox.Text = Clipboard.GetText();
            errorLabel.Text = string.Empty;
        }
        catch (Exception ex) when (ex is ExternalException or ThreadStateException or InvalidOperationException)
        {
            errorLabel.Text = $"读取剪贴板失败：{ex.Message}";
        }
    }

    private async Task ParseCoreAsync()
    {
        try
        {
            var text = inputBox.Text;
            var uri = ShareLinkParser.TryExtractFirstLink(text, out var parseError);
            if (uri is null)
            {
                SetState(MediaPageState.Failed);
                errorLabel.Text = parseError ?? "未在文本中找到链接。";
                return;
            }

            SetState(MediaPageState.Resolving);
            var request = new MediaResolveRequest(uri, MediaResolveMode.Automatic, new MediaRequestContext(null, null));
            var result = await resolveCoordinator.ResolveAsync(request, CancellationToken.None);
            HandleResolveResult(result, uri.ToString());
        }
        catch (Exception ex)
        {
            // async void 入口的异常必须就地消化，否则会冒到 UI 线程弹崩溃对话框
            SetState(MediaPageState.Failed);
            errorLabel.Text = ErrorChainFormatter.Format(ex);
        }
    }

    private async Task ParseWithBrowserAsync()
    {
        try
        {
            var uri = ShareLinkParser.TryExtractFirstLink(inputBox.Text, out var parseError);
            if (uri is null)
            {
                SetState(MediaPageState.Failed);
                errorLabel.Text = parseError ?? "未在文本中找到链接。";
                return;
            }

            // 首次进入浏览器流程：在 UI 线程初始化浏览器会话；失败转为 WaitingForUser 提示
            try
            {
                await ensureBrowserInitializedAsync(this);
            }
            catch (Exception ex)
            {
                SetState(MediaPageState.WaitingForUser);
                errorLabel.Text = $"浏览器不可用：{ex.Message}";
                return;
            }

            SetState(MediaPageState.Resolving);
            var request = new MediaResolveRequest(uri, MediaResolveMode.BrowserInteractive, new MediaRequestContext(null, null));
            var result = await resolveCoordinator.ResolveAsync(request, CancellationToken.None);
            HandleResolveResult(result, uri.ToString());
        }
        catch (Exception ex)
        {
            SetState(MediaPageState.Failed);
            errorLabel.Text = ErrorChainFormatter.Format(ex);
        }
    }

    private void HandleResolveResult(MediaResolveResult result, string shareLink)
    {
        switch (result.Status)
        {
            case MediaResolveStatus.Succeeded:
                currentPost = result.Post!;
                currentShareLink = shareLink;
                FillAssets();
                SetState(MediaPageState.Resolved);
                errorLabel.Text = string.Empty;
                break;
            case MediaResolveStatus.RequiresUserInteraction:
                SetState(MediaPageState.WaitingForUser);
                errorLabel.Text = result.Message ?? "需要浏览器登录后继续解析。";
                break;
            case MediaResolveStatus.Unsupported:
                SetState(MediaPageState.Failed);
                errorLabel.Text = result.Message ?? "暂不支持该链接。";
                break;
            default:
                SetState(MediaPageState.Failed);
                errorLabel.Text = result.Message ?? "解析失败。";
                break;
        }
    }

    private void FillAssets()
    {
        var post = currentPost!;
        var selections = new List<MediaSelectionResult>(post.Assets.Count);
        var hasSegmented = false;
        foreach (var asset in post.Assets)
        {
            var selection = MediaQualitySelector.SelectBest(asset, stateStore.Settings.QualityPreference);
            selections.Add(selection);
            if (selection.Status == MediaSelectionStatus.UnsupportedSegmented)
            {
                hasSegmented = true;
            }
        }

        assetGrid.LoadPost(post, selections, stateStore.Settings.DefaultSelectAll);
        infoLabel.Text = $"平台：{(post.Provider == MediaProviderId.Douyin ? "抖音" : "直接链接")}    标题：{post.Title ?? "(无标题)"}    作者：{post.AuthorName ?? "-"}    共发现：{post.Assets.Count} 个媒体";
        errorLabel.Text = hasSegmented
            ? "已发现更高质量的分段媒体流，但当前版本暂不支持合并。"
            : string.Empty;
    }

    // 预览请求：图片下载预览，视频显示元数据
    private async Task ShowPreviewAsync(MediaAsset asset, MediaVariant variant)
    {
        if (asset.Kind == MediaKind.Video)
        {
            previewControl.ShowVideoInfo(variant);
            return;
        }

        try
        {
            var path = await previewService.DownloadPreviewAsync(variant, CancellationToken.None);
            previewControl.ShowImage(path);
        }
        catch (OperationCanceledException)
        {
            // 预览取消不提示
        }
        catch (Exception ex)
        {
            previewControl.ShowError($"预览失败：{ErrorChainFormatter.Format(ex)}");
        }
    }

    private void AssetGrid_PreviewRequested(object? sender, (MediaAsset Asset, MediaVariant Variant) e)
    {
        _ = ShowPreviewAsync(e.Asset, e.Variant);
    }

    // 媒体设置：保存后若用户未在页面级覆盖目录，则同步刷新默认目录
    private void OpenSettings()
    {
        using var form = new MediaSettingsForm(stateStore);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            if (!directoryOverridden)
            {
                directoryBox.Text = stateStore.Settings.DownloadDirectory;
            }
        }
    }

    private async Task DownloadSelectedAsync()
    {
        try
        {
            var post = currentPost;
            if (post is null)
            {
                return;
            }

            var selected = assetGrid.GetSelected();
            if (selected.Count == 0)
            {
                errorLabel.Text = "请先勾选要下载的媒体。";
                return;
            }

            var directory = directoryBox.Text;
            if (!Directory.Exists(directory))
            {
                errorLabel.Text = "保存目录不存在，请先选择。";
                return;
            }

            SetState(MediaPageState.Downloading);
            var tasks = new List<MediaDownloadTask>(selected.Count);
            foreach (var (asset, variant) in selected)
            {
                var fileName = BuildFileName(post, asset, variant);
                var target = DownloadFileNameBuilder.BuildUniquePath(directory, fileName);
                tasks.Add(new MediaDownloadTask(Guid.NewGuid(), asset, variant, target));
            }

            var batch = new MediaDownloadBatch(Guid.NewGuid(), currentShareLink, post, tasks);
            foreach (var task in tasks)
            {
                queueGrid.AddTask(task);
            }

            await downloadCoordinator.EnqueueBatchAsync(batch, CancellationToken.None);
            SetState(MediaPageState.Completed);
        }
        catch (Exception ex)
        {
            SetState(MediaPageState.Failed);
            errorLabel.Text = ErrorChainFormatter.Format(ex);
        }
    }

    private void QueueGrid_OperationRequested(object? sender, (Guid TaskId, bool Retry) e)
    {
        var post = currentPost;
        if (post is null)
        {
            return;
        }

        if (e.Retry)
        {
            _ = RetryTaskAsync(post, e.TaskId);
        }
        else
        {
            downloadCoordinator.CancelTask(e.TaskId);
        }
    }

    // 重试：重新解析作品获取新的媒体 URL（旧 URL 可能过期/失效），
    // 按资产序号匹配后以新批次入队
    private async Task RetryTaskAsync(ResolvedMediaPost post, Guid originalTaskId)
    {
        try
        {
            var original = queueGrid.FindTask(originalTaskId);
            if (original is null)
            {
                return;
            }

            // 重新解析：优先用当前分享链接，其次用原作品页 URI
            var sourceText = string.IsNullOrWhiteSpace(currentShareLink) ? post.SourceUri.ToString() : currentShareLink;
            var uri = ShareLinkParser.TryExtractFirstLink(sourceText, out _) ?? post.SourceUri;
            var result = await resolveCoordinator.ResolveAsync(
                new MediaResolveRequest(uri, MediaResolveMode.Automatic, new MediaRequestContext(null, null)),
                CancellationToken.None);
            if (result.Status != MediaResolveStatus.Succeeded || result.Post is null)
            {
                errorLabel.Text = $"重试解析失败：{result.Message ?? "解析未成功"}";
                return;
            }

            // 按资产序号匹配原任务对应的媒体
            var freshAsset = result.Post.Assets.FirstOrDefault(asset => asset.Index == original.Asset.Index);
            if (freshAsset is null)
            {
                errorLabel.Text = "重新解析后未找到对应媒体。";
                return;
            }

            var selection = MediaQualitySelector.SelectBest(freshAsset, stateStore.Settings.QualityPreference);
            if (selection.Status != MediaSelectionStatus.Selected || selection.Variant is null)
            {
                errorLabel.Text = selection.Message ?? "重新解析后无可用变体。";
                return;
            }

            var target = DownloadFileNameBuilder.BuildUniquePath(directoryBox.Text, Path.GetFileName(original.TargetPath));
            var retryTask = new MediaDownloadTask(Guid.NewGuid(), freshAsset, selection.Variant, target);
            var batch = new MediaDownloadBatch(Guid.NewGuid(), currentShareLink, result.Post, new MediaDownloadTask[] { retryTask });
            queueGrid.AddTask(retryTask);
            await downloadCoordinator.EnqueueBatchAsync(batch, CancellationToken.None);
        }
        catch (Exception ex)
        {
            errorLabel.Text = ErrorChainFormatter.Format(ex);
        }
    }

    internal static string BuildFileName(ResolvedMediaPost post, MediaAsset asset, MediaVariant variant)
    {
        var baseName = string.IsNullOrWhiteSpace(post.Title)
            ? "media"
            : DownloadFileNameBuilder.SanitizeFileName(DownloadFileNameBuilder.StripHashtags(post.Title));
        var ext = DownloadFileNameBuilder.ResolveExtension(variant.ContentType, asset.Kind, variant.Uri.AbsolutePath);
        var number = asset.SourceIndex + 1;

        return asset.Role switch
        {
            // 实况图配对：同一序号输出 _still 静态照片与 _motion 动态视频
            MediaAssetRole.LivePhotoStill => $"{baseName}_{number:D2}_still{ext}",
            MediaAssetRole.LivePhotoMotion => $"{baseName}_{number:D2}_motion{ext}",
            MediaAssetRole.AlbumPreview => $"{baseName}_album_preview{ext}",
            _ => asset.Index == 0 ? $"{baseName}{ext}" : $"{baseName}_{number:D2}{ext}",
        };
    }

    private void BrowseDirectory()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = directoryBox.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            directoryBox.Text = dialog.SelectedPath;
            directoryOverridden = true;
            errorLabel.Text = string.Empty;
        }
        // 用户取消时保留原目录
    }

    // 状态机：按状态启用/禁用会引起冲突的按钮
    private void SetState(MediaPageState state)
    {
        currentState = state;
        var basicEnabled = state is MediaPageState.Idle or MediaPageState.Resolved or MediaPageState.Completed or MediaPageState.Failed;
        parseButton.Enabled = basicEnabled;
        pasteButton.Enabled = basicEnabled;
        clearButton.Enabled = basicEnabled;
        browseButton.Enabled = basicEnabled;
        // 浏览器登录在空闲/失败/待交互等状态下始终可用（用户可在任意时刻强制走浏览器解析），
        // 仅解析与下载进行中禁用避免并发操作
        browserButton.Enabled = state is not (MediaPageState.Resolving or MediaPageState.Downloading);
        selectAllButton.Enabled = state == MediaPageState.Resolved;
        unselectAllButton.Enabled = state == MediaPageState.Resolved;
        downloadButton.Enabled = state == MediaPageState.Resolved;
    }

    // 进度事件可能在后台线程触发：经 BeginInvoke 切回 UI 线程更新控件
    private void DownloadCoordinator_TaskProgressChanged(object? sender, MediaDownloadProgress e)
    {
        BeginInvoke(() =>
        {
            queueGrid.UpdateProgress(e.TaskId, e.Percent);
        });
    }

    // 完成事件：更新队列状态；成功后用实际文件回填资产表「尺寸」（解析阶段缺失时）
    private void DownloadCoordinator_TaskCompleted(object? sender, MediaDownloadTaskCompleted e)
    {
        BeginInvoke(() =>
        {
            queueGrid.CompleteTask(e.TaskId, e.Result.Status, e.Result.Error);
            if (e.Result.Status != MediaDownloadStatus.Succeeded
                || e.Result.SavedPath is null
                || queueGrid.FindTask(e.TaskId) is not { } task)
            {
                return;
            }

            var (width, height) = task.Asset.Kind == MediaKind.Image
                ? TryReadImageSize(e.Result.SavedPath)
                : (null, null);
            assetGrid.UpdateFileInfo(task.SelectedVariant.Uri, width, height);
        });
    }

    // 轻量读取图片尺寸（仅解码头部）；失败（HEIC 等 GDI+ 不支持格式）返回空
    private static (int? Width, int? Height) TryReadImageSize(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var image = Image.FromStream(stream);
            return (image.Width, image.Height);
        }
        catch
        {
            return (null, null);
        }
    }
}

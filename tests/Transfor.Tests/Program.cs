using Transfor;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using System.Net;


// ===== 控制台式测试运行器 =====
// 无测试框架依赖：逐项断言核心功能，全部通过时输出 "All N tests passed."，
// 任一断言失败抛出异常并以非零码退出。

// 引号转换测试用例：名称、输入、期望输出
var quoteCases = new (string Name, string? Input, string Expected)[]
{
    ("null input returns empty string", null, string.Empty),
    ("empty input returns empty string", string.Empty, string.Empty),
    ("english double quotes become english single quotes", "\"hello\"", "'hello'"),
    ("chinese double quotes become paired chinese single quotes", "\u201C\u4F60\u597D\u201D", "\u2018\u4F60\u597D\u2019"),
    ("mixed chinese and english quotes are converted", "\u4ED6\u8BF4\uFF1A\u201Chello\u201D", "\u4ED6\u8BF4\uFF1A\u2018hello\u2019"),
    ("multiline content keeps line breaks", "\"a\"\r\n\u201Cb\u201D", "'a'\r\n\u2018b\u2019"),
    ("text without double quotes is unchanged", "text without quotes", "text without quotes"),
    ("existing single quotes are unchanged", "'hi' \u2018hello\u2019", "'hi' \u2018hello\u2019"),
};

// 去除空格测试用例：名称、输入、期望输出
var spaceCases = new (string Name, string? Input, string Expected)[]
{
    ("null input returns empty string", null, string.Empty),
    ("empty input returns empty string", string.Empty, string.Empty),
    ("half-width spaces are removed", "\u4F60 \u597D", "\u4F60\u597D"),
    ("full-width spaces are removed", "\u4F60\u3000\u597D", "\u4F60\u597D"),
    ("line breaks and tabs are preserved", "a b\r\nc\td", "ab\r\nc\td"),
    ("text without removable spaces is unchanged", "abc\r\nc\td", "abc\r\nc\td"),
};

// 执行两种转换器的用例
foreach (var testCase in quoteCases)
{
    var actual = QuoteConverter.Convert(testCase.Input);
    AssertEqual(testCase.Expected, actual, testCase.Name);
}

foreach (var testCase in spaceCases)
{
    var actual = SpaceRemover.Remove(testCase.Input);
    AssertEqual(testCase.Expected, actual, testCase.Name);
}

// 执行其余分组测试（迁移、路径、页面契约、历史存储、粘贴协调等）
TestPendingMigrationRecovery();
TestSplitStateMigration();
TestAppPaths();
TestTextToolsPageContract();
TestTextToolDefinition();
TestHotKeyBinding();
TestHistoryStore();
TestPasteCoordinator();
TestMutationsPersistWithoutExplicitSave();
TestUpdateSettingsRollsBackOnWriteFailure();
TestMediaModels();
TestMediaStateStore();
TestMediaStateStoreConcurrency();
TestShareLinkParser();
TestMediaResolverRegistry();
TestMediaResolveCoordinator();
TestSafeUriValidator();
TestSafeHttpRequestSender();
TestMediaQualitySelector();
TestMediaContentValidator();
TestDownloadFileNameBuilder();
TestMediaHashService();
TestBrowserCookieMatcher();
TestMediaDownloadService();
TestDownloadCoordinator();
TestDirectMediaResolver();
TestMediaDownloadPage();
TestMediaServicesLifecycle();
TestBrowserProxy();
TestDouyinPageParser();
TestDouyinMediaNormalizer();
TestDouyinMediaResolver();
TestDouyinTransportClassifier();
TestDouyinTransportPreference();
TestErrorChainFormatter();
TestCdpConnection();
TestEdgeProxyComparison();
TestMediaNetworkModeSettings();
TestMediaCache();
TestMediaSettingsForm();
TestMediaPreviewService();
TestMediaAssetGrid();
TestDouyinStructuredDataFallbacks();
TestDouyinDetailEndpointMatcher();
TestDouyinCandidateFallback();
TestMagicExtensionDetection();
TestBatchCompletedEvent();

Console.WriteLine($"All {TestCounter.Passed} tests passed.");
static void TestPendingMigrationRecovery()
{
    var directory = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    var paths = new AppPaths(directory);
    LegacyStateTestWriter.Write(paths.LegacyStateFile, TextToolId.SpaceRemoval);
    Directory.CreateDirectory(directory);
    File.WriteAllText(paths.PendingMigrationFile, "{\"schemaVersion\":1}");
    File.WriteAllText(paths.SettingsFile, "not json");
    StateMigrationService.EnsureMigrated(paths);
    AssertEqual(false, File.Exists(paths.PendingMigrationFile), "readable legacy resolves pending migration");
    AssertEqual(TextToolId.SpaceRemoval, TextStateStore.Load(paths).UiState.LastViewedTool, "pending migration restores legacy ui state");

    File.WriteAllText(paths.PendingMigrationFile, "{\"schemaVersion\":1}");
    StateMigrationService.EnsureMigrated(paths);
    AssertEqual(false, File.Exists(paths.PendingMigrationFile), "complete new state resolves pending migration");
}

// 场景：旧 state.json 拆分为三个新文件，并生成备份
static void TestSplitStateMigration()
{
    var directory = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    var paths = new AppPaths(directory);
    LegacyStateTestWriter.Write(paths.LegacyStateFile, TextToolId.SpaceRemoval);
    StateMigrationService.EnsureMigrated(paths);
    var state = TextStateStore.Load(paths);
    AssertEqual(TextToolId.SpaceRemoval, state.UiState.LastViewedTool, "migrated ui state");
    AssertEqual(true, File.Exists(paths.LegacyBackupFile), "legacy backup");
}

// 场景：AppPaths 的目录与文件名约定
static void TestAppPaths()
{
    var root = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    var paths = new AppPaths(root);
    AssertEqual(Path.GetFullPath(root), paths.ApplicationDirectory, "application directory");
    AssertEqual("state.json", Path.GetFileName(paths.LegacyStateFile), "legacy state file name");
}

// 场景：文本转换页面实现 IFeaturePage 契约（需 STA 线程创建控件）
static void TestTextToolsPageContract()
{
    var path = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"), "state.json");
    RunSta(() =>
    {
        using var page = new TextToolsPage(TextStateStore.Load(new AppPaths(Path.GetDirectoryName(path)!)));
        AssertEqual("text-tools", page.Id, "text page id");
        AssertEqual("文本转换", page.DisplayName, "text page display name");
        AssertEqual(true, page.View is UserControl, "text page view");
    });
}

// 在 STA 线程上执行 WinForms 相关断言（否则控件创建会抛异常）
static void RunSta(Action action)
{
    Exception? error = null;
    var thread = new Thread(() =>
    {
        try { action(); }
        catch (Exception exception) { error = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (error is not null) throw error;
}

// 场景：文本工具的静态定义（标识、名称、转换函数）
static void TestTextToolDefinition()
{
    var definition = new TextToolDefinition(TextToolId.QuoteConversion, "引号转换", QuoteConverter.Convert);

    AssertEqual(TextToolId.QuoteConversion, definition.Id, "text tool id");
    AssertEqual("引号转换", definition.DisplayName, "text tool display name");
    AssertEqual("'x'", definition.Convert("\"x\""), "text tool converter");
}

// 场景：快捷键绑定的默认值、合法组合与非法输入
static void TestHotKeyBinding()
{
    var defaultBinding = HotKeyBinding.Default;
    AssertEqual("Alt+Q", defaultBinding.DisplayText, "default hotkey display");
    AssertEqual(true, defaultBinding.Modifiers.HasFlag(Keys.Alt), "default hotkey modifier");
    AssertEqual(Keys.Q, defaultBinding.Key, "default hotkey key");

    // 四种修饰键 + F5 都是合法组合
    foreach (var modifiers in new[] { Keys.Control, Keys.Alt, Keys.Shift, Keys.LWin })
    {
        var binding = HotKeyBinding.Create(modifiers, Keys.F5);
        AssertEqual(Keys.F5, binding.Key, "valid hotkey key");
    }

    // 缺少修饰键 / 缺少主键 / 主键选成修饰键都是非法输入
    AssertThrows<ArgumentException>(() => HotKeyBinding.Create(Keys.None, Keys.Q), "hotkey requires modifier");
    AssertThrows<ArgumentException>(() => HotKeyBinding.Create(Keys.Alt, Keys.None), "hotkey requires key");
    AssertThrows<ArgumentException>(() => HotKeyBinding.Create(Keys.Alt, Keys.Control), "hotkey rejects modifier key");    AssertThrows<ArgumentException>(() => HotKeyBinding.Create(Keys.Alt, Keys.ControlKey), "hotkey rejects modifier key code");
}

// 场景：历史存储的默认值、增删裁剪、独立分类、持久化与损坏回退
static void TestHistoryStore()
{
    var root = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var path = Path.Combine(root, "state.json");

    try
    {
        // 首次加载：全部为默认值
        var store = TextStateStore.Load(new AppPaths(Path.GetDirectoryName(path)!));
        AssertEqual(HotKeyBinding.Default, store.Settings.HistoryHotKey, "default hotkey");
        AssertEqual(100, store.Settings.QuoteHistoryLimit, "default quote history limit");
        AssertEqual(100, store.Settings.SpaceHistoryLimit, "default space history limit");
        AssertEqual(TextToolId.QuoteConversion, store.UiState.LastViewedTool, "default last viewed tool");

        // 写入两条历史、更新设置并保存，然后重新加载验证持久化
        var original = "\"a\"\r\n b";
        var converted = "'a'\r\nb";
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        store.Add(new HistoryEntry(TextToolId.QuoteConversion, original, converted, createdAt));
        store.Add(new HistoryEntry(TextToolId.SpaceRemoval, "x y", "xy", createdAt.AddSeconds(1)));
        store.SetLastViewedTool(TextToolId.SpaceRemoval);
        store.UpdateSettings(store.Settings with { QuoteHistoryLimit = 1, SpaceHistoryLimit = 2 });
        store.Save();

        var reloaded = TextStateStore.Load(new AppPaths(Path.GetDirectoryName(path)!));
        var quote = reloaded.GetHistory(TextToolId.QuoteConversion);
        var space = reloaded.GetHistory(TextToolId.SpaceRemoval);
        AssertEqual(1, quote.Count, "quote history count");
        AssertEqual(1, space.Count, "space history count");
        AssertEqual(original, quote[0].OriginalInput, "history original input");
        AssertEqual(converted, quote[0].ConvertedOutput, "history converted output");
        AssertEqual(createdAt, quote[0].CreatedAtUtc, "history timestamp");
        AssertEqual(TextToolId.SpaceRemoval, reloaded.UiState.LastViewedTool, "last viewed persistence");

        // 超出上限时裁剪最旧记录；两种分类互不影响
        reloaded.UpdateSettings(reloaded.Settings with { QuoteHistoryLimit = 1, SpaceHistoryLimit = 1 });
        reloaded.Add(new HistoryEntry(TextToolId.QuoteConversion, "second", "second", createdAt.AddSeconds(2)));
        reloaded.Add(new HistoryEntry(TextToolId.QuoteConversion, "third", "third", createdAt.AddSeconds(3)));
        reloaded.Add(new HistoryEntry(TextToolId.SpaceRemoval, "second", "second", createdAt.AddSeconds(4)));
        AssertEqual("third", reloaded.GetHistory(TextToolId.QuoteConversion)[0].OriginalInput, "quote limit trims oldest");
        AssertEqual("second", reloaded.GetHistory(TextToolId.SpaceRemoval)[0].OriginalInput, "space history independent");

        // 清空只影响指定分类
        reloaded.ClearHistory(TextToolId.QuoteConversion);
        AssertEqual(0, reloaded.GetHistory(TextToolId.QuoteConversion).Count, "clear quote history");
        AssertEqual(1, reloaded.GetHistory(TextToolId.SpaceRemoval).Count, "clear keeps space history");

        // 历史上限越界被拒绝
        AssertThrows<ArgumentException>(() => reloaded.UpdateSettings(reloaded.Settings with { QuoteHistoryLimit = 0 }), "history limit lower bound");
        AssertThrows<ArgumentException>(() => reloaded.UpdateSettings(reloaded.Settings with { SpaceHistoryLimit = 501 }), "history limit upper bound");

        // 历史文件损坏：设置保留，历史回退为空
        File.WriteAllText(Path.Combine(root, "text-history.json"), "not json");
        var corrupt = TextStateStore.Load(new AppPaths(root));
        AssertEqual(reloaded.Settings, corrupt.Settings, "corrupt history keeps settings");
        AssertEqual(0, corrupt.GetHistory(TextToolId.QuoteConversion).Count, "corrupt history fallback");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

// 场景：粘贴协调器的执行顺序与各环节失败时的中止行为
static void TestPasteCoordinator()
{
    var entry = new HistoryEntry(TextToolId.QuoteConversion, "input", "result", DateTimeOffset.UtcNow);

    // 全链路成功：按 clipboard → restore → paste 的顺序执行
    var successCalls = new List<string>();
    var success = new PasteCoordinator(
        new FakeClipboard(successCalls, succeeds: true),
        new FakeWindowInput(successCalls, restoreSucceeds: true, pasteSucceeds: true));
    var successResult = success.TryPaste(entry, new nint(42));
    AssertEqual(true, successResult.Succeeded, "paste success");
    AssertEqual("clipboard,restore,paste", string.Join(",", successCalls), "paste operation order");

    // 剪贴板失败：立即中止，不再执行后续步骤
    var clipboardFailureCalls = new List<string>();
    var clipboardFailure = new PasteCoordinator(
        new FakeClipboard(clipboardFailureCalls, succeeds: false),
        new FakeWindowInput(clipboardFailureCalls, restoreSucceeds: true, pasteSucceeds: true));
    var clipboardFailureResult = clipboardFailure.TryPaste(entry, new nint(42));
    AssertEqual(false, clipboardFailureResult.Succeeded, "clipboard failure result");
    AssertEqual("clipboard", string.Join(",", clipboardFailureCalls), "clipboard failure stops paste");

    // 窗口恢复失败：执行到 restore 即中止
    var windowFailureCalls = new List<string>();
    var windowFailure = new PasteCoordinator(
        new FakeClipboard(windowFailureCalls, succeeds: true),
        new FakeWindowInput(windowFailureCalls, restoreSucceeds: false, pasteSucceeds: true));
    var windowFailureResult = windowFailure.TryPaste(entry, new nint(42));
    AssertEqual(false, windowFailureResult.Succeeded, "window restore failure result");
    AssertEqual("clipboard,restore", string.Join(",", windowFailureCalls), "window failure stops paste");

    // 模拟粘贴失败：三个环节都执行但最终失败
    var pasteFailureCalls = new List<string>();
    var pasteFailure = new PasteCoordinator(
        new FakeClipboard(pasteFailureCalls, succeeds: true),
        new FakeWindowInput(pasteFailureCalls, restoreSucceeds: true, pasteSucceeds: false));
    var pasteFailureResult = pasteFailure.TryPaste(entry, new nint(42));
    AssertEqual(false, pasteFailureResult.Succeeded, "send input failure result");
    AssertEqual("clipboard,restore,paste", string.Join(",", pasteFailureCalls), "send input failure order");

    // 目标窗口句柄无效：一个环节都不执行
    var noWindowCalls = new List<string>();
    var noWindow = new PasteCoordinator(
        new FakeClipboard(noWindowCalls, succeeds: true),
        new FakeWindowInput(noWindowCalls, restoreSucceeds: true, pasteSucceeds: true));
    var noWindowResult = noWindow.TryPaste(entry, nint.Zero);
    AssertEqual(false, noWindowResult.Succeeded, "missing target window result");
    AssertEqual(string.Empty, string.Join(",", noWindowCalls), "missing target window stops all operations");
}

// 场景：状态修改方法无需显式 Save 即持久化（UI 不再调用全量 Save）
static void TestMutationsPersistWithoutExplicitSave()
{
    var root = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var store = TextStateStore.Load(new AppPaths(root));
        store.Add(new HistoryEntry(TextToolId.QuoteConversion, "in", "out", DateTimeOffset.UtcNow));
        var reloaded = TextStateStore.Load(new AppPaths(root));
        AssertEqual(1, reloaded.GetHistory(TextToolId.QuoteConversion).Count, "add persists without Save()");

        reloaded.SetLastViewedTool(TextToolId.SpaceRemoval);
        var reloaded2 = TextStateStore.Load(new AppPaths(root));
        AssertEqual(TextToolId.SpaceRemoval, reloaded2.UiState.LastViewedTool, "set-last-viewed persists without Save()");

        reloaded2.UpdateSettings(reloaded2.Settings with { QuoteHistoryLimit = 7 });
        var reloaded3 = TextStateStore.Load(new AppPaths(root));
        AssertEqual(7, reloaded3.Settings.QuoteHistoryLimit, "update-settings persists without Save()");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

// 场景：UpdateSettings 写入失败时回滚内存设置与裁剪结果
static void TestUpdateSettingsRollsBackOnWriteFailure()
{
    var root = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        // 用同名目录占位 settings.json，使 SaveSettings 的 File.Move 必然失败
        Directory.CreateDirectory(Path.Combine(root, "settings.json"));

        var store = TextStateStore.Load(new AppPaths(root));
        for (var i = 0; i < 10; i++)
        {
            store.Add(new HistoryEntry(TextToolId.QuoteConversion, i.ToString(), i.ToString(), DateTimeOffset.UtcNow));
        }

        var oldSettings = store.Settings;
        var writeFailed = false;
        try
        {
            store.UpdateSettings(oldSettings with { QuoteHistoryLimit = 5 });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            writeFailed = true;
        }
        AssertEqual(true, writeFailed, "write failure propagates");
        AssertEqual(oldSettings.QuoteHistoryLimit, store.Settings.QuoteHistoryLimit, "settings rolled back on write failure");
        AssertEqual(10, store.GetHistory(TextToolId.QuoteConversion).Count, "history trim rolled back on write failure");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

// 场景：统一媒体模型与解析契约
static void TestMediaModels()
{
    // 枚举顺序
    AssertEqual(0, (int)MediaKind.Image, "image kind order");
    AssertEqual(1, (int)MediaKind.Video, "video kind order");
    AssertEqual(0, (int)MediaProviderId.Direct, "direct provider order");
    AssertEqual(1, (int)MediaProviderId.Douyin, "douyin provider order");

    // MediaResolveResult：成功必须包含非空 Post，且拒绝不可下载状态
    var post = BuildValidPostForTest();
    var success = MediaResolveResult.Success(post);
    AssertEqual(MediaResolveStatus.Succeeded, success.Status, "success status");
    AssertEqual(true, success.Post is not null, "success carries post");
    AssertThrows<ArgumentNullException>(() => MediaResolveResult.Success(null!), "success rejects null post");
    AssertThrows<InvalidDataException>(() => MediaResolveResult.Success(post with { Assets = Array.Empty<MediaAsset>() }), "success rejects empty assets");
    AssertThrows<InvalidDataException>(() => MediaResolveResult.Success(post with { Assets = new MediaAsset[] { post.Assets[0] with { Variants = Array.Empty<MediaVariant>() } } }), "success rejects empty variants");
    AssertThrows<InvalidDataException>(() => MediaResolveResult.Success(post with { Assets = new MediaAsset[] { post.Assets[0] with { Variants = new MediaVariant[] { post.Assets[0].Variants[0] with { Uri = new Uri("ftp://x/y.mp4") } } } } }), "success rejects non-http variant");

    // RequiresUserInteraction 的 Post 必须为空
    var pending = MediaResolveResult.RequiresUserInteraction("需要浏览器");
    AssertEqual(MediaResolveStatus.RequiresUserInteraction, pending.Status, "interaction status");
    AssertEqual(true, pending.Post is null, "interaction carries no post");
    AssertEqual(MediaResolveStatus.Unsupported, MediaResolveResult.Unsupported("x").Status, "unsupported status");
    AssertEqual(MediaResolveStatus.Failed, MediaResolveResult.Failure("x").Status, "failed status");

    // 可持久化记录 JSON 往返
    var entry = new MediaDownloadHistoryEntry(MediaProviderId.Douyin, "https://v.douyin.com/abc/", "标题", @"C:\dl", new[] { @"C:\dl\1.mp4" }, 3, 1, 0, DateTimeOffset.UtcNow);
    var entryJson = System.Text.Json.JsonSerializer.Serialize(entry);
    var entryBack = System.Text.Json.JsonSerializer.Deserialize<MediaDownloadHistoryEntry>(entryJson);
    AssertEqual("标题", entryBack!.Title, "history entry round trip title");
    AssertEqual(1, entryBack.SavedFiles.Count, "history entry round trip files");
    AssertEqual(true, entryJson.Contains("SavedFiles", StringComparison.Ordinal), "history serializes saved files");

    // 请求上下文保存会话 ID，且序列化后不含 Cookie 字段
    var context = new MediaRequestContext(new Uri("https://www.douyin.com/"), "session-1");
    AssertEqual("session-1", context.BrowserSessionId, "session id in request context");
    var variant = new MediaVariant(new Uri("https://cdn.example.com/v.mp4"), 1920, 1080, 30, 2000, 1000, "video/mp4", "h264", MediaVariantSource.StructuredData, context);
    var variantJson = System.Text.Json.JsonSerializer.Serialize(variant).ToLowerInvariant();
    AssertEqual(true, !variantJson.Contains("cookie"), "no cookie field in variant json");

    // 分段变体通过 IsSegmented 表达
    var segmented = variant with { IsSegmented = true };
    AssertEqual(true, segmented.IsSegmented, "segmented variant flag");

    // MediaDownloadSettings：默认值与 CreateDefault
    var root = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var existingDownloads = Path.Combine(root, "dl");
        Directory.CreateDirectory(existingDownloads);
        var withDownloads = MediaDownloadSettings.CreateDefault(root, existingDownloads);
        AssertEqual(Path.GetFullPath(existingDownloads), withDownloads.DownloadDirectory, "create default prefers existing downloads dir");

        var fallback = MediaDownloadSettings.CreateDefault(root, Path.Combine(root, "missing-dl"));
        AssertEqual(Path.GetFullPath(root), fallback.DownloadDirectory, "create default falls back when downloads dir missing");

        AssertEqual(3, fallback.MaxConcurrentDownloads, "default concurrency");
        AssertEqual(true, fallback.DefaultSelectAll, "default select all");
        AssertEqual(false, fallback.OpenFolderAfterDownload, "default open folder");
        AssertEqual(MediaQualityPreference.Highest, fallback.QualityPreference, "default quality");

        // 并发范围 1-8
        AssertThrows<ArgumentOutOfRangeException>(() => (fallback with { MaxConcurrentDownloads = 0 }).Validate(), "concurrency lower bound");
        AssertThrows<ArgumentOutOfRangeException>(() => (fallback with { MaxConcurrentDownloads = 9 }).Validate(), "concurrency upper bound");
        AssertThrows<ArgumentException>(() => (fallback with { DownloadDirectory = "" }).Validate(), "empty directory rejected");
        AssertThrows<ArgumentOutOfRangeException>(() => (fallback with { QualityPreference = (MediaQualityPreference)99 }).Validate(), "undefined quality rejected");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }

    // MediaDownloadProgress：未知总大小不计算百分比，避免除零
    AssertEqual(null, MediaDownloadProgress.Create(Guid.NewGuid(), 50, null).Percent, "percent null when total unknown");
    AssertEqual(50d, MediaDownloadProgress.Create(Guid.NewGuid(), 50, 100).Percent, "percent computed");
    AssertEqual(100d, MediaDownloadProgress.Create(Guid.NewGuid(), 200, 100).Percent, "percent capped at 100");

    // MediaDownloadResult 工厂
    AssertEqual(MediaDownloadStatus.Succeeded, MediaDownloadResult.Success(Guid.Empty, "x").Status, "result success factory");
    AssertEqual(MediaDownloadStatus.Failed, MediaDownloadResult.Failed(Guid.Empty, "e").Status, "result failed factory");
    AssertEqual(MediaDownloadStatus.Cancelled, MediaDownloadResult.Cancelled(Guid.Empty).Status, "result cancelled factory");
}

// 测试辅助：构造一个合法的作品
static ResolvedMediaPost BuildValidPostForTest()
{
    var context = new MediaRequestContext(new Uri("https://www.douyin.com/video/1"), null);
    var variant = new MediaVariant(new Uri("https://cdn.example.com/v.mp4"), 1920, 1080, 30, 2000, 1000, "video/mp4", "h264", MediaVariantSource.StructuredData, context);
    var asset = new MediaAsset(0, MediaKind.Video, new MediaVariant[] { variant });
    return new ResolvedMediaPost(MediaProviderId.Douyin, new Uri("https://www.douyin.com/video/1"), "1", "t", "a", new MediaAsset[] { asset });
}

// 场景：媒体独立持久化（设置与历史分开存储，串行写入，损坏回退）
static void TestMediaStateStore()
{
    var root = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var paths = new AppPaths(root);

        // 缺失文件 -> 默认设置与空历史
        var store = MediaStateStore.Load(paths);
        AssertEqual(3, store.Settings.MaxConcurrentDownloads, "defaults when files missing");
        AssertEqual(0, store.GetHistory().Count, "empty history when file missing");
        AssertEqual(true, Path.IsPathFullyQualified(store.Settings.DownloadDirectory), "default directory is absolute");

        // UpdateSettings / Add 无需额外 Save 即持久化
        var downloadDir = Path.Combine(root, "dl");
        Directory.CreateDirectory(downloadDir);
        store.UpdateSettings(store.Settings with { MaxConcurrentDownloads = 5, DownloadDirectory = downloadDir });
        store.Add(new MediaDownloadHistoryEntry(MediaProviderId.Douyin, "https://v.douyin.com/a/", "t", downloadDir, new[] { Path.Combine(downloadDir, "1.mp4") }, 2, 0, 0, DateTimeOffset.UtcNow));
        var reloaded = MediaStateStore.Load(paths);
        AssertEqual(5, reloaded.Settings.MaxConcurrentDownloads, "settings persisted without Save()");
        AssertEqual(1, reloaded.GetHistory().Count, "history persisted without Save()");

        // 原始 JSON 中不出现敏感字段名称
        var raw = File.ReadAllText(paths.MediaDownloadHistoryFile).ToLowerInvariant();
        AssertEqual(true, !raw.Contains("cookie") && !raw.Contains("token") && !raw.Contains("authorization"), "no credential fields serialized");

        // 损坏 JSON -> 回退默认值
        File.WriteAllText(paths.MediaSettingsFile, "not json");
        AssertEqual(3, MediaStateStore.Load(paths).Settings.MaxConcurrentDownloads, "corrupt settings fallback to defaults");

        // 未知 schemaVersion -> 不按版本 1 解析
        File.WriteAllText(paths.MediaSettingsFile, "{\"SchemaVersion\":2,\"Value\":{}}");
        AssertEqual(3, MediaStateStore.Load(paths).Settings.MaxConcurrentDownloads, "unknown schema version falls back");

        // 相对下载目录被拒绝
        AssertThrows<ArgumentException>(() => store.UpdateSettings(store.Settings with { DownloadDirectory = "relative" }), "relative directory rejected");

        // 历史超过 200 条后仅保留最新 200 条
        for (var i = 0; i < 205; i++)
        {
            store.Add(new MediaDownloadHistoryEntry(MediaProviderId.Direct, "u", $"t{i}", downloadDir, Array.Empty<string>(), 1, 0, 0, DateTimeOffset.UtcNow));
        }
        var capped = MediaStateStore.Load(paths).GetHistory();
        AssertEqual(200, capped.Count, "history capped at 200");
        AssertEqual("t204", capped[^1].Title, "latest history kept");
        AssertEqual("t5", capped[0].Title, "oldest history trimmed");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

// 场景：并发 Add 与 UpdateSettings 后 JSON 仍是完整文档，历史不丢失
static void TestMediaStateStoreConcurrency()
{
    var root = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var store = MediaStateStore.Load(new AppPaths(root));
        var tasks = new List<Task>();
        for (var i = 0; i < 40; i++)
        {
            var captured = i;
            tasks.Add(Task.Run(() => store.Add(new MediaDownloadHistoryEntry(MediaProviderId.Direct, "u", $"c{captured}", root, Array.Empty<string>(), 1, 0, 0, DateTimeOffset.UtcNow))));
            tasks.Add(Task.Run(() => store.UpdateSettings(store.Settings with { MaxConcurrentDownloads = 1 + captured % 8 })));
        }
        Task.WaitAll(tasks.ToArray());

        var reloaded = MediaStateStore.Load(new AppPaths(root));
        AssertEqual(40, reloaded.GetHistory().Count, "concurrent adds all persisted");
        AssertEqual(true, reloaded.Settings.MaxConcurrentDownloads is >= 1 and <= 8, "concurrent settings writes remain valid");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

// 场景：分享链接提取
static void TestShareLinkParser()
{
    var link = ShareLinkParser.TryExtractFirstLink("3.28 复制打开抖音，看看【某用户的作品】 https://v.douyin.com/abc123/", out _);
    AssertEqual("https://v.douyin.com/abc123/", link?.ToString(), "extract from share text");

    var clean = ShareLinkParser.TryExtractFirstLink("https://v.douyin.com/abc123/。", out _);
    AssertEqual("https://v.douyin.com/abc123/", clean?.ToString(), "trailing full-width period cleaned");

    var cleanAscii = ShareLinkParser.TryExtractFirstLink("https://v.douyin.com/abc123/.", out _);
    AssertEqual("https://v.douyin.com/abc123/", cleanAscii?.ToString(), "trailing ascii period cleaned");

    var first = ShareLinkParser.TryExtractFirstLink("https://v.douyin.com/one/ 然后 https://v.douyin.com/two/", out _);
    AssertEqual("https://v.douyin.com/one/", first?.ToString(), "only first valid link returned");

    // 第一个候选无效时继续寻找下一个候选
    var skipInvalid = ShareLinkParser.TryExtractFirstLink("看这里 https:// 然后 https://v.douyin.com/ok/", out _);
    AssertEqual("https://v.douyin.com/ok/", skipInvalid?.ToString(), "skips invalid candidate and finds next");

    AssertEqual(true, ShareLinkParser.TryExtractFirstLink("没有链接的文本", out var noLinkError) is null && noLinkError is not null, "no link returns error");
    AssertEqual(true, ShareLinkParser.TryExtractFirstLink("", out _) is null, "empty text returns null");
    AssertEqual(true, ShareLinkParser.TryExtractFirstLink("ftp://v.douyin.com/abc/", out _) is null, "non-http scheme rejected");
}

// 测试替身：可控的解析器（定义见文件末尾）
// 场景：解析注册中心
static void TestMediaResolverRegistry()
{
    var douyin = new FakeResolver(MediaProviderId.Douyin, uri => uri.Host.Equals("douyin.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".douyin.com", StringComparison.OrdinalIgnoreCase));
    var registry = new MediaResolverRegistry(new IMediaResolver[] { douyin });
    AssertEqual(true, registry.TryGetResolver(new Uri("https://v.douyin.com/a/"), out var matched) && matched?.Provider == MediaProviderId.Douyin, "first matching resolver wins");
    AssertEqual(false, registry.TryGetResolver(new Uri("https://other.com/x"), out _), "non-matching uri returns false");

    var empty = new MediaResolverRegistry(Array.Empty<IMediaResolver>());
    AssertEqual(false, empty.TryGetResolver(new Uri("https://v.douyin.com/a/"), out _), "empty registry returns false");

    // 重复 Provider 被拒绝
    AssertThrows<ArgumentException>(() => new MediaResolverRegistry(new IMediaResolver[] { new FakeResolver(MediaProviderId.Douyin), new FakeResolver(MediaProviderId.Douyin) }), "duplicate provider rejected");
}

// 场景：解析协调器
static void TestMediaResolveCoordinator()
{
    // 找到解析器：原样返回成功结果
    var ok = new MediaResolveCoordinator(new MediaResolverRegistry(new IMediaResolver[] { new FakeResolver(MediaProviderId.Douyin) }));
    var result = ok.ResolveAsync(new MediaResolveRequest(new Uri("https://v.douyin.com/a/"), MediaResolveMode.Automatic, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.Succeeded, result.Status, "resolves with resolver");

    // 无解析器：Unsupported 而非异常
    var unsupported = new MediaResolveCoordinator(new MediaResolverRegistry(Array.Empty<IMediaResolver>()));
    var noMatch = unsupported.ResolveAsync(new MediaResolveRequest(new Uri("https://v.douyin.com/a/"), MediaResolveMode.Automatic, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.Unsupported, noMatch.Status, "no resolver returns unsupported");

    // 需要浏览器：原样透传
    var pendingResolver = new FakeResolver(MediaProviderId.Douyin, resultFactory: (_, _) => MediaResolveResult.RequiresUserInteraction("需要浏览器"));
    var pending = new MediaResolveCoordinator(new MediaResolverRegistry(new IMediaResolver[] { pendingResolver }));
    var pendingResult = pending.ResolveAsync(new MediaResolveRequest(new Uri("https://v.douyin.com/a/"), MediaResolveMode.Automatic, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.RequiresUserInteraction, pendingResult.Status, "interaction result passed through");

    // 未预期异常：转换为 Failure
    var throwingResolver = new FakeResolver(MediaProviderId.Douyin, resultFactory: (_, _) => throw new InvalidOperationException("boom"));
    var failure = new MediaResolveCoordinator(new MediaResolverRegistry(new IMediaResolver[] { throwingResolver }));
    var failed = failure.ResolveAsync(new MediaResolveRequest(new Uri("https://v.douyin.com/a/"), MediaResolveMode.Automatic, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.Failed, failed.Status, "unexpected exception becomes failure");

    // 异常链被保留：TLS 外层错误消息应包含内层 IOException 类型与消息
    var chainResolver = new ThrowingResolver(new HttpRequestException(
        HttpRequestError.SecureConnectionError,
        "The SSL connection could not be established, see inner exception.",
        new IOException("Received an unexpected EOF or 0 bytes from the transport stream.")));
    var chainFailure = new MediaResolveCoordinator(new MediaResolverRegistry(new IMediaResolver[] { chainResolver }));
    var chainResult = chainFailure.ResolveAsync(new MediaResolveRequest(new Uri("https://v.douyin.com/a/"), MediaResolveMode.Automatic, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.Failed, chainResult.Status, "ssl exception becomes failure");
    AssertEqual(true, chainResult.Message?.Contains("IOException") == true, "inner exception type surfaced in message");
    AssertEqual(true, chainResult.Message?.Contains("Received an unexpected EOF") == true, "inner exception message surfaced");

    // 取消仍表现为取消，不转换为 Failed
    var cancellingResolver = new FakeResolver(MediaProviderId.Douyin, resultFactory: (_, token) => throw new OperationCanceledException(token));
    var cancel = new MediaResolveCoordinator(new MediaResolverRegistry(new IMediaResolver[] { cancellingResolver }));
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var cancelled = false;
    try
    {
        cancel.ResolveAsync(new MediaResolveRequest(new Uri("https://v.douyin.com/a/"), MediaResolveMode.Automatic, new MediaRequestContext(null, null)), cts.Token).GetAwaiter().GetResult();
    }
    catch (OperationCanceledException)
    {
        cancelled = true;
    }
    AssertEqual(true, cancelled, "cancellation stays cancellation");
}

// 场景：URI 安全校验（不查询真实 DNS）
static void TestSafeUriValidator()
{
    var publicDns = new FakeDnsResolver(_ => Task.FromResult(new IPAddress[] { IPAddress.Parse("8.8.8.8") }));
    var validator = new SafeUriValidator(publicDns);

    AssertEqual(false, validator.ValidateAsync(new Uri("http://127.0.0.1/x"), CancellationToken.None).GetAwaiter().GetResult().IsAllowed, "loopback rejected");
    AssertEqual(false, validator.ValidateAsync(new Uri("http://10.1.2.3/x"), CancellationToken.None).GetAwaiter().GetResult().IsAllowed, "private 10/8 rejected");
    AssertEqual(false, validator.ValidateAsync(new Uri("http://172.20.0.1/x"), CancellationToken.None).GetAwaiter().GetResult().IsAllowed, "private 172.16/12 rejected");
    AssertEqual(false, validator.ValidateAsync(new Uri("http://192.168.1.1/x"), CancellationToken.None).GetAwaiter().GetResult().IsAllowed, "private 192.168/16 rejected");
    AssertEqual(false, validator.ValidateAsync(new Uri("http://169.254.0.1/x"), CancellationToken.None).GetAwaiter().GetResult().IsAllowed, "link-local rejected");
    AssertEqual(false, validator.ValidateAsync(new Uri("http://localhost/x"), CancellationToken.None).GetAwaiter().GetResult().IsAllowed, "localhost rejected");
    AssertEqual(false, validator.ValidateAsync(new Uri("file:///C:/x"), CancellationToken.None).GetAwaiter().GetResult().IsAllowed, "file scheme rejected");
    AssertEqual(false, validator.ValidateAsync(new Uri("javascript:alert(1)"), CancellationToken.None).GetAwaiter().GetResult().IsAllowed, "javascript scheme rejected");
    AssertEqual(false, validator.ValidateAsync(new Uri("http://user:pass@8.8.8.8/x"), CancellationToken.None).GetAwaiter().GetResult().IsAllowed, "uri with userinfo rejected");
    AssertEqual(true, validator.ValidateAsync(new Uri("https://www.douyin.com/video/1"), CancellationToken.None).GetAwaiter().GetResult().IsAllowed, "public dns result allowed");

    // Fake DNS 返回私网地址：域名被拒绝
    var privateDns = new FakeDnsResolver(_ => Task.FromResult(new IPAddress[] { IPAddress.Parse("10.0.0.1") }));
    AssertEqual(false, new SafeUriValidator(privateDns).ValidateAsync(new Uri("https://cdn.example.com/x"), CancellationToken.None).GetAwaiter().GetResult().IsAllowed, "domain resolving to private ip rejected");

    // DNS 解析失败：拒绝
    var failingDns = new FakeDnsResolver(_ => throw new Exception("nxdomain"));
    AssertEqual(false, new SafeUriValidator(failingDns).ValidateAsync(new Uri("https://cdn.example.com/x"), CancellationToken.None).GetAwaiter().GetResult().IsAllowed, "dns failure rejected");
}

// 场景：安全请求发送器（相对重定向合并、每跳校验、重定向上限、敏感头与跨源 Referer 处理）
static void TestSafeHttpRequestSender()
{
    var validator = new SafeUriValidator(new FakeDnsResolver(_ => Task.FromResult(new IPAddress[] { IPAddress.Parse("8.8.8.8") })));

    // 相对重定向正确合并并到达最终地址
    var handler = new StubHttpHandler(
        _ => RedirectResponse("/b"),
        _ => new HttpResponseMessage(HttpStatusCode.OK));
    var sender = new SafeHttpRequestSender(new HttpClient(handler) , validator);
    using var response = sender.SendAsync(
        new Uri("https://v.douyin.com/a"),
        (uri, _) => Task.FromResult(new HttpRequestMessage(HttpMethod.Get, uri)),
        HttpCompletionOption.ResponseHeadersRead,
        CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(HttpStatusCode.OK, response.StatusCode, "follows redirect chain");
    AssertEqual("https://v.douyin.com/b", handler.Requests[1].RequestUri?.ToString(), "relative redirect merged");

    // 重定向到私网 IP：每跳校验生效
    var evilHandler = new StubHttpHandler(_ => RedirectResponse("http://10.0.0.1/x"));
    var evilSender = new SafeHttpRequestSender(new HttpClient(evilHandler), validator);
    var evilRejected = false;
    try
    {
        evilSender.SendAsync(
            new Uri("https://v.douyin.com/a"),
            (uri, _) => Task.FromResult(new HttpRequestMessage(HttpMethod.Get, uri)),
            HttpCompletionOption.ResponseHeadersRead,
            CancellationToken.None).GetAwaiter().GetResult();
    }
    catch (InvalidOperationException)
    {
        evilRejected = true;
    }
    AssertEqual(true, evilRejected, "redirect to private ip rejected");

    // 超过重定向上限（默认 5）
    var loopHandler = new StubHttpHandler(_ => RedirectResponse("/x"));
    var loopSender = new SafeHttpRequestSender(new HttpClient(loopHandler), validator);
    var loopRejected = false;
    try
    {
        loopSender.SendAsync(
            new Uri("https://v.douyin.com/a"),
            (uri, _) => Task.FromResult(new HttpRequestMessage(HttpMethod.Get, uri)),
            HttpCompletionOption.ResponseHeadersRead,
            CancellationToken.None).GetAwaiter().GetResult();
    }
    catch (InvalidOperationException)
    {
        loopRejected = true;
    }
    AssertEqual(true, loopRejected, "redirect limit exceeded");

    // 跨源 Referer 被清除；Authorization/Proxy-Authorization 被清除
    var refHandler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
    var refSender = new SafeHttpRequestSender(new HttpClient(refHandler), validator);
    using var refResponse = refSender.SendAsync(
        new Uri("https://v.douyin.com/a"),
        (uri, _) =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Referrer = new Uri("https://evil.com/steal");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");
            request.Headers.TryAddWithoutValidation("Proxy-Authorization", "proxy secret");
            return Task.FromResult(request);
        },
        HttpCompletionOption.ResponseHeadersRead,
        CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(true, !refHandler.Requests[0].Headers.Contains("Authorization"), "authorization stripped");
    AssertEqual(true, !refHandler.Requests[0].Headers.Contains("Proxy-Authorization"), "proxy authorization stripped");
    AssertEqual(true, refHandler.Requests[0].Headers.Referrer is null, "cross-origin referer stripped");

    // 同源 Referer 保留（host 完全一致）
    var sameHandler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
    var sameSender = new SafeHttpRequestSender(new HttpClient(sameHandler), validator);
    using var sameResponse = sameSender.SendAsync(
        new Uri("https://v.douyin.com/a"),
        (uri, _) =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Referrer = new Uri("https://v.douyin.com/video/1");
            return Task.FromResult(request);
        },
        HttpCompletionOption.ResponseHeadersRead,
        CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual("https://v.douyin.com/video/1", sameHandler.Requests[0].Headers.Referrer?.ToString(), "same-origin referer kept");

    static HttpResponseMessage RedirectResponse(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };
}

// 场景：质量选择器
static void TestMediaQualitySelector()
{
    static MediaVariant V(string url, int? w, int? h, long? len, MediaVariantSource source, int? fps = null, long? bitrate = null, string? codec = null, bool segmented = false) =>
        new(new Uri(url), w, h, fps, bitrate, len, null, codec, source, new MediaRequestContext(null, null), segmented);

    // 超大缩略图不会压过真实作品候选
    var small = V("https://x/1.jpg", 100, 100, 1000, MediaVariantSource.StructuredData);
    var large = V("https://x/2.jpg", 2000, 2000, 5000, MediaVariantSource.StructuredData);
    var thumb = V("https://x/thumb.jpg", 9000, 9000, 9999, MediaVariantSource.Thumbnail);
    var imageAsset = new MediaAsset(0, MediaKind.Image, new[] { small, thumb, large });
    var imageResult = MediaQualitySelector.SelectBest(imageAsset, MediaQualityPreference.Highest);
    AssertEqual(MediaSelectionStatus.Selected, imageResult.Status, "image selection status");
    AssertEqual("https://x/2.jpg", imageResult.Variant?.Uri.ToString(), "thumbnail never wins over real candidate");
    AssertEqual(true, !imageResult.Variant!.Uri.ToString().Contains("thumb"), "thumbnail excluded when real candidates exist");

    // 同分辨率视频按帧率/码率排序；分段候选不作为直接下载对象
    var v720 = V("https://x/a.mp4", 1280, 720, 1000, MediaVariantSource.StructuredData, 30, 1000, "h264");
    var v1080 = V("https://x/b.mp4", 1920, 1080, 2000, MediaVariantSource.StructuredData, 30, 2000, "h264");
    var v1080f60 = V("https://x/c.mp4", 1920, 1080, 3000, MediaVariantSource.StructuredData, 60, 2000, "h264");
    var videoAsset = new MediaAsset(0, MediaKind.Video, new[] { v720, v1080, v1080f60 });
    var videoResult = MediaQualitySelector.SelectBest(videoAsset, MediaQualityPreference.Highest);
    AssertEqual("https://x/c.mp4", videoResult.Variant?.Uri.ToString(), "higher fps wins on equal resolution");

    // 只有分段候选：返回 UnsupportedSegmented，不伪装可下载
    var segmentedOnly = new MediaAsset(0, MediaKind.Video, new[] { V("https://x/hls/master.m3u8", 1920, 1080, null, MediaVariantSource.StructuredData, segmented: true) });
    var segmentedResult = MediaQualitySelector.SelectBest(segmentedOnly, MediaQualityPreference.Highest);
    AssertEqual(MediaSelectionStatus.UnsupportedSegmented, segmentedResult.Status, "segmented-only reported");
    AssertEqual(true, segmentedResult.Variant is null, "no fake downloadable variant for segmented");

    // Balanced：优先至少 720p；没有 720p 时回退最高可用
    var v480 = V("https://x/d.mp4", 854, 480, 800, MediaVariantSource.StructuredData, 30, 800, "h264");
    var balancedWith720 = MediaQualitySelector.SelectBest(new MediaAsset(0, MediaKind.Video, new[] { v480, v1080 }), MediaQualityPreference.Balanced);
    AssertEqual("https://x/b.mp4", balancedWith720.Variant?.Uri.ToString(), "balanced prefers 720p");
    var balancedFallback = MediaQualitySelector.SelectBest(new MediaAsset(0, MediaKind.Video, new[] { v480 }), MediaQualityPreference.Balanced);
    AssertEqual("https://x/d.mp4", balancedFallback.Variant?.Uri.ToString(), "balanced falls back below 720p");

    // 空变体：NoUsableVariant
    var empty = MediaQualitySelector.SelectBest(new MediaAsset(0, MediaKind.Image, Array.Empty<MediaVariant>()), MediaQualityPreference.Highest);
    AssertEqual(MediaSelectionStatus.NoUsableVariant, empty.Status, "no variant reported");
}

// 场景：内容校验（魔数 + 响应合理性）
static void TestMediaContentValidator()
{
    // 魔数识别 + 可 Seek 流位置恢复
    var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 };
    using var jpegStream = new MemoryStream(jpeg);
    jpegStream.Position = 0;
    AssertEqual(true, MediaContentValidator.HasValidMagicNumberAsync(jpegStream, MediaKind.Image, CancellationToken.None).GetAwaiter().GetResult(), "jpeg magic ok");
    AssertEqual(0, jpegStream.Position, "seekable stream position restored");

    var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
    using var pngStream = new MemoryStream(png);
    AssertEqual(true, MediaContentValidator.HasValidMagicNumberAsync(pngStream, MediaKind.Image, CancellationToken.None).GetAwaiter().GetResult(), "png magic ok");

    var mp4 = new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0, 0, 0, 0 };
    using var mp4Stream = new MemoryStream(mp4);
    AssertEqual(true, MediaContentValidator.HasValidMagicNumberAsync(mp4Stream, MediaKind.Video, CancellationToken.None).GetAwaiter().GetResult(), "mp4 magic ok");

    // ZIP/HTML 伪装媒体被拒绝
    var zip = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0, 0, 0, 0, 0 };
    using var zipStream = new MemoryStream(zip);
    AssertEqual(false, MediaContentValidator.HasValidMagicNumberAsync(zipStream, MediaKind.Image, CancellationToken.None).GetAwaiter().GetResult(), "zip rejected as image");
    using var htmlStream = new MemoryStream("<html></html>"u8.ToArray());
    AssertEqual(false, MediaContentValidator.HasValidMagicNumberAsync(htmlStream, MediaKind.Video, CancellationToken.None).GetAwaiter().GetResult(), "html rejected as video");

    // 不可 Seek 流不抛异常
    var nonSeekable = new NonSeekableStream(jpeg);
    AssertEqual(true, MediaContentValidator.HasValidMagicNumberAsync(nonSeekable, MediaKind.Image, CancellationToken.None).GetAwaiter().GetResult(), "non-seekable stream ok");

    // 响应合理性：Content-Type、声明长度
    var okResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
    okResponse.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
    AssertEqual(true, MediaContentValidator.IsPlausibleResponse(okResponse, MediaKind.Image, 1024, out _), "plausible response ok");

    var badStatus = new HttpResponseMessage(HttpStatusCode.NotFound);
    AssertEqual(false, MediaContentValidator.IsPlausibleResponse(badStatus, MediaKind.Image, 1024, out _), "non-2xx rejected");

    var wrongType = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
    wrongType.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
    AssertEqual(false, MediaContentValidator.IsPlausibleResponse(wrongType, MediaKind.Video, 1024, out _), "text/html rejected for video");

    var oversized = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
    oversized.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
    oversized.Content.Headers.ContentLength = 2048;
    AssertEqual(false, MediaContentValidator.IsPlausibleResponse(oversized, MediaKind.Video, 1024, out _), "declared length over limit rejected");
}

// 场景：文件名构建
static void TestDownloadFileNameBuilder()
{
    AssertEqual("a_b_c.mp4", DownloadFileNameBuilder.SanitizeFileName("a:b\\c.mp4"), "invalid chars replaced");
    AssertEqual("a_b_c", DownloadFileNameBuilder.SanitizeFileName("a:b\\c."), "trailing dot trimmed");
    AssertEqual("download", DownloadFileNameBuilder.SanitizeFileName("...."), "empty name falls back");
    AssertEqual(".jpg", DownloadFileNameBuilder.ResolveExtension("image/jpeg", MediaKind.Image), "jpeg extension");
    AssertEqual(".webp", DownloadFileNameBuilder.ResolveExtension("image/webp", MediaKind.Image), "webp extension");
    AssertEqual(".mp4", DownloadFileNameBuilder.ResolveExtension("video/mp4", MediaKind.Video), "mp4 extension");
    AssertEqual(".img", DownloadFileNameBuilder.ResolveExtension(null, MediaKind.Image), "unknown image extension");
    AssertEqual(".bin", DownloadFileNameBuilder.ResolveExtension(null, MediaKind.Video), "unknown video extension");

    // 泛化/缺失 Content-Type：按 URL 路径扩展名推断（抖音图片 content_type 为裸 "image"）
    AssertEqual(".jpg", DownloadFileNameBuilder.ResolveExtension("image", MediaKind.Image, "/tos-cn-i-0813/abc~tplv-dy-vqe2-sr-v2:1440:2560:q80.jpeg?lk3s=1"), "bare image type inferred from jpeg url");
    AssertEqual(".png", DownloadFileNameBuilder.ResolveExtension(null, MediaKind.Image, "/path/logo.png"), "null type inferred from png url");
    AssertEqual(".webp", DownloadFileNameBuilder.ResolveExtension("image", MediaKind.Image, "/path/img~q80.webp"), "bare image type inferred from webp url");
    AssertEqual(".mp4", DownloadFileNameBuilder.ResolveExtension("video", MediaKind.Video, "/aweme/v1/play/video.mp4?video_id=1"), "bare video type inferred from mp4 url");
    AssertEqual(".img", DownloadFileNameBuilder.ResolveExtension("image", MediaKind.Image, "/path/no-extension"), "bare image without url extension falls back");
    AssertEqual(".jpg", DownloadFileNameBuilder.ResolveExtension("image/jpeg", MediaKind.Image, "/path/confusing.webp"), "explicit type wins over url");

    // 重名使用 (1)、(2)
    var dir = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var first = DownloadFileNameBuilder.BuildUniquePath(dir, "x.mp4");
        File.WriteAllText(first, "1");
        var second = DownloadFileNameBuilder.BuildUniquePath(dir, "x.mp4");
        AssertEqual("x(1).mp4", Path.GetFileName(second), "duplicate gets suffix");
        File.WriteAllText(second, "2");
        var third = DownloadFileNameBuilder.BuildUniquePath(dir, "x.mp4");
        AssertEqual("x(2).mp4", Path.GetFileName(third), "second duplicate gets next suffix");

        // 绝对路径要求与 .. 越界拒绝
        AssertThrows<ArgumentException>(() => DownloadFileNameBuilder.BuildUniquePath("relative", "x.mp4"), "relative directory rejected");
        AssertThrows<ArgumentException>(() => DownloadFileNameBuilder.BuildUniquePath(dir, "../x.mp4"), "path separator in name rejected");

        // 路径包含关系校验
        AssertEqual(true, DownloadFileNameBuilder.IsWithinDirectory(dir, Path.Combine(dir, "x.mp4")), "path within directory");
        AssertEqual(false, DownloadFileNameBuilder.IsWithinDirectory(dir, Path.Combine(Path.GetTempPath(), "evil", "x.mp4")), "path outside directory rejected");
    }
    finally
    {
        Directory.Delete(dir, recursive: true);
    }
}

// 场景：哈希流式计算
static void TestMediaHashService()
{
    var data = new byte[128 * 1024];
    new Random(42).NextBytes(data);
    using var stream = new MemoryStream(data);
    var hash = MediaHashService.ComputeSha256Async(stream, CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(64, hash.Length, "sha256 hex length");

    using var reference = new MemoryStream(data);
    var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(reference));
    AssertEqual(expected, hash, "sha256 matches reference");
}

// 场景：Cookie 匹配规则
static void TestBrowserCookieMatcher()
{
    var uri = new Uri("https://v.douyin.com/a/b");
    var httpUri = new Uri("http://v.douyin.com/a/b");

    // 域名匹配：完整相等与真实子域
    AssertEqual(true, BrowserCookieMatcher.ShouldSend(new BrowserCookie("v.douyin.com", "/", "a", "1", false), "s1", uri, true), "exact domain match");
    AssertEqual(true, BrowserCookieMatcher.ShouldSend(new BrowserCookie(".douyin.com", "/", "a", "1", false), "s1", uri, true), "leading dot domain parent matches subdomain");
    AssertEqual(false, BrowserCookieMatcher.ShouldSend(new BrowserCookie("evil.com", "/", "a", "1", false), "s1", uri, true), "foreign domain never sent");
    AssertEqual(false, BrowserCookieMatcher.ShouldSend(new BrowserCookie("douyin.com", "/", "a", "1", false), "s1", new Uri("https://cdn.example.com/x"), true), "douyin cookie not sent to cdn.example.com");
    AssertEqual(false, BrowserCookieMatcher.ShouldSend(new BrowserCookie("evildouyin.com", "/", "a", "1", false), "s1", uri, true), "suffix-lookalike domain rejected");

    // Secure 要求
    AssertEqual(false, BrowserCookieMatcher.ShouldSend(new BrowserCookie("v.douyin.com", "/", "a", "1", true), "s1", httpUri, false), "secure cookie blocked on http");
    AssertEqual(true, BrowserCookieMatcher.ShouldSend(new BrowserCookie("v.douyin.com", "/", "a", "1", true), "s1", uri, true), "secure cookie allowed on https");

    // Path 目录边界
    AssertEqual(true, BrowserCookieMatcher.ShouldSend(new BrowserCookie("v.douyin.com", "/a", "a", "1", false), "s1", uri, true), "path prefix match");
    AssertEqual(false, BrowserCookieMatcher.ShouldSend(new BrowserCookie("v.douyin.com", "/a", "a", "1", false), "s1", new Uri("https://v.douyin.com/ab"), true), "path boundary enforced (/a != /ab)");

    // BrowserSessionId 为空不发送
    AssertEqual(false, BrowserCookieMatcher.ShouldSend(new BrowserCookie("v.douyin.com", "/", "a", "1", false), null, uri, true), "empty session blocks cookies");
}

// 场景：安全流式下载服务
static void TestMediaDownloadService()
{
    var root = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var validator = new SafeUriValidator(new FakeDnsResolver(_ => Task.FromResult(new IPAddress[] { IPAddress.Parse("8.8.8.8") })));

        // 构造媒体内容（MP4 魔数）
        var mp4Body = new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x63, 0x31, 0x00, 0x00, 0x00, 0x20 };

        static MediaDownloadTask TaskFor(string target, MediaVariant variant, MediaAsset asset) =>
            new(Guid.NewGuid(), asset, variant, target);

        static MediaAsset Mp4Asset() =>
            new(0, MediaKind.Video, new[] { new MediaVariant(new Uri("https://cdn.example.com/v.mp4"), 1920, 1080, null, null, null, "video/mp4", "h264", MediaVariantSource.StructuredData, new MediaRequestContext(null, null)) });

        static MediaVariant VariantWith(Uri uri, MediaRequestContext context) =>
            new(uri, 1920, 1080, null, null, null, "video/mp4", "h264", MediaVariantSource.StructuredData, context);

        // 200 成功：文件存在、.part 删除、SavedPath 正确
        var okTarget = Path.Combine(root, "ok.mp4");
        var okHandler = new StubHttpHandler(_ => OkMediaResponse(mp4Body));
        var okService = new MediaDownloadService(new SafeHttpRequestSender(new HttpClient(okHandler), validator), null);
        var okResult = okService.DownloadAsync(TaskFor(okTarget, VariantWith(new Uri("https://cdn.example.com/v.mp4"), new MediaRequestContext(null, null)), Mp4Asset()), CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Succeeded, okResult.Status, "download success");
        AssertEqual(true, File.Exists(okTarget), "file exists");
        AssertEqual(false, File.Exists(okTarget + ".part." + okResult.TaskId.ToString("N")), "part removed after success");
        AssertEqual(okTarget, okResult.SavedPath, "saved path returned");

        // 404/429/500 失败
        foreach (var status in new[] { HttpStatusCode.NotFound, (HttpStatusCode)429, HttpStatusCode.InternalServerError })
        {
            var failTarget = Path.Combine(root, $"fail{(int)status}.mp4");
            var failHandler = new StubHttpHandler(_ => new HttpResponseMessage(status) { Content = new ByteArrayContent(Array.Empty<byte>()) });
            var failService = new MediaDownloadService(new SafeHttpRequestSender(new HttpClient(failHandler), validator), null);
            var failResult = failService.DownloadAsync(TaskFor(failTarget, VariantWith(new Uri("https://cdn.example.com/v.mp4"), new MediaRequestContext(null, null)), Mp4Asset()), CancellationToken.None).GetAwaiter().GetResult();
            AssertEqual(MediaDownloadStatus.Failed, failResult.Status, $"http {(int)status} fails");
            AssertEqual(false, File.Exists(failTarget), "no file on status failure");
        }

        // TLS 握手被拒：不进入浏览器兜底（未挂接浏览器会话）→ Failed 且保留异常链
        var tlsFailTarget = Path.Combine(root, "tlsfail.mp4");
        var tlsFailHandler = new ThrowingHttpHandler(new HttpRequestException(
            HttpRequestError.SecureConnectionError,
            "The SSL connection could not be established, see inner exception.",
            new IOException("Received an unexpected EOF or 0 bytes from the transport stream.")));
        var tlsFailService = new MediaDownloadService(new SafeHttpRequestSender(new HttpClient(tlsFailHandler), validator), null);
        var tlsFailResult = tlsFailService.DownloadAsync(TaskFor(tlsFailTarget, VariantWith(new Uri("https://cdn.example.com/v.mp4"), new MediaRequestContext(null, null)), Mp4Asset()), CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Failed, tlsFailResult.Status, "tls rejection without browser fails");
        AssertEqual(true, tlsFailResult.Error?.Contains("IOException") == true, "tls failure keeps inner chain");

        // TLS 握手被拒 + 浏览器会话可用：自动转入浏览器网络栈下载 → 成功
        var tlsBrowserTarget = Path.Combine(root, "tlsbrowser.mp4");
        var tlsBrowserHandler = new ThrowingHttpHandler(new HttpRequestException(
            HttpRequestError.SecureConnectionError,
            "The SSL connection could not be established, see inner exception.",
            new IOException("Received an unexpected EOF or 0 bytes from the transport stream.")));
        var browserDownloadSession = new BrowserDownloadSession();
        var tlsBrowserService = new MediaDownloadService(new SafeHttpRequestSender(new HttpClient(tlsBrowserHandler), validator), browserDownloadSession);
        var tlsBrowserTask = TaskFor(tlsBrowserTarget, VariantWith(new Uri("https://cdn.example.com/v.mp4"), new MediaRequestContext(null, null)), Mp4Asset());
        var tlsBrowserResult = tlsBrowserService.DownloadAsync(tlsBrowserTask, CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Succeeded, tlsBrowserResult.Status, "tls rejection falls back to browser download");
        AssertEqual(true, File.Exists(tlsBrowserTarget), "browser fallback file exists");
        AssertEqual(1, browserDownloadSession.DownloadRequests.Count, "browser download invoked once");
        AssertEqual(tlsBrowserTask.Id, browserDownloadSession.DownloadRequests[0], "browser download receives task id");

        // HTTP 状态失败（404）不触发浏览器兜底
        var statusBrowserTarget = Path.Combine(root, "statusbrowser.mp4");
        var statusBrowserHandler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent(Array.Empty<byte>()) });
        var statusBrowserSession = new BrowserDownloadSession();
        var statusBrowserService = new MediaDownloadService(new SafeHttpRequestSender(new HttpClient(statusBrowserHandler), validator), statusBrowserSession);
        var statusBrowserResult = statusBrowserService.DownloadAsync(TaskFor(statusBrowserTarget, VariantWith(new Uri("https://cdn.example.com/v.mp4"), new MediaRequestContext(null, null)), Mp4Asset()), CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Failed, statusBrowserResult.Status, "http status failure does not fall back");
        AssertEqual(0, statusBrowserSession.DownloadRequests.Count, "browser download not invoked on status failure");

        // 错误 Content-Type 失败
        var htmlTarget = Path.Combine(root, "html.mp4");
        var htmlHandler = new StubHttpHandler(_ => { var r = OkResponse(Array.Empty<byte>()); r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html"); return r; });
        var htmlService = new MediaDownloadService(new SafeHttpRequestSender(new HttpClient(htmlHandler), validator), null);
        var htmlResult = htmlService.DownloadAsync(TaskFor(htmlTarget, VariantWith(new Uri("https://cdn.example.com/v.mp4"), new MediaRequestContext(null, null)), Mp4Asset()), CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Failed, htmlResult.Status, "text/html rejected");

        // 声明长度超限失败
        var bigTarget = Path.Combine(root, "big.mp4");
        var bigHandler = new StubHttpHandler(_ => { var r = OkResponse(mp4Body); r.Content.Headers.ContentLength = 10 * 1024 * 1024; return r; });
        var bigService = new MediaDownloadService(new SafeHttpRequestSender(new HttpClient(bigHandler), validator), null, maxFileBytes: 1024);
        var bigResult = bigService.DownloadAsync(TaskFor(bigTarget, VariantWith(new Uri("https://cdn.example.com/v.mp4"), new MediaRequestContext(null, null)), Mp4Asset()), CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Failed, bigResult.Status, "declared length over limit fails");

        // chunked 实际读取超限：无 Content-Length 但流超限
        var chunkedTarget = Path.Combine(root, "chunked.mp4");
        var chunkedHandler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) });
        chunkedHandler.Requests.Clear();
        var chunkedService = new MediaDownloadService(new SafeHttpRequestSender(new HttpClient(chunkedHandler), validator), null, maxFileBytes: 1024);
        var chunkedResult = chunkedService.DownloadAsync(TaskFor(chunkedTarget, VariantWith(new Uri("https://cdn.example.com/v.mp4"), new MediaRequestContext(null, null)), Mp4Asset()), CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Failed, chunkedResult.Status, "actual read over limit fails");
        AssertEqual(false, File.Exists(chunkedTarget), "no file on actual over limit");

        // 流中途 IOException：.part 清理
        var abortTarget = Path.Combine(root, "abort.mp4");
        var abortHandler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new AbortedStreamContent() });
        var abortService = new MediaDownloadService(new SafeHttpRequestSender(new HttpClient(abortHandler), validator), null);
        var abortResult = abortService.DownloadAsync(TaskFor(abortTarget, VariantWith(new Uri("https://cdn.example.com/v.mp4"), new MediaRequestContext(null, null)), Mp4Asset()), CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Failed, abortResult.Status, "aborted stream fails");
        AssertEqual(false, File.Exists(abortTarget + ".part." + abortResult.TaskId.ToString("N")), "part cleaned after abort");

        // 取消：.part 清理、无目标文件
        var cancelTarget = Path.Combine(root, "cancel.mp4");
        using var cts = new CancellationTokenSource();
        var cancelHandler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new SlowStreamContent(cts.Token) });
        var cancelService = new MediaDownloadService(new SafeHttpRequestSender(new HttpClient(cancelHandler), validator), null);
        var cancelTask = cancelService.DownloadAsync(TaskFor(cancelTarget, VariantWith(new Uri("https://cdn.example.com/v.mp4"), new MediaRequestContext(null, null)), Mp4Asset()), cts.Token);
        cts.CancelAfter(200);
        var cancelResult = cancelTask.GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Cancelled, cancelResult.Status, "cancel returns cancelled");
        AssertEqual(false, File.Exists(cancelTarget), "no target file after cancel");
        AssertEqual(false, File.Exists(cancelTarget + ".part." + cancelResult.TaskId.ToString("N")), "part cleaned after cancel");

        // 会话变体（浏览器解析出的媒体）：直接走浏览器网络栈下载，不发起 HttpClient 请求
        var referer = new Uri("https://v.douyin.com/video/1");
        var directBrowserSession = new BrowserDownloadSession();
        var sessionTarget = Path.Combine(root, "session.mp4");
        var sessionHandler = new StubHttpHandler(_ => OkMediaResponse(mp4Body));
        var sessionService = new MediaDownloadService(new SafeHttpRequestSender(new HttpClient(sessionHandler), validator), directBrowserSession);
        var sessionVariant = VariantWith(new Uri("https://v.douyin.com/redirect"), new MediaRequestContext(referer, "session-1"));
        var sessionTask = TaskFor(sessionTarget, sessionVariant, Mp4Asset());
        var sessionResult = sessionService.DownloadAsync(sessionTask, CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Succeeded, sessionResult.Status, "session variant downloads via browser");
        AssertEqual(0, sessionHandler.Requests.Count, "no http request for session variant");
        AssertEqual(1, directBrowserSession.DownloadRequests.Count, "browser download invoked for session variant");
        AssertEqual(sessionTask.Id, directBrowserSession.DownloadRequests[0], "browser download receives task id");
        AssertEqual(true, File.Exists(sessionTarget), "session variant file saved");

        // 非会话变体：仍走 HttpClient，Referer 同源保留、跨源清除
        var cookieTarget = Path.Combine(root, "cookie.mp4");
        var cookieHandler = new StubHttpHandler(
            _ => RedirectResponseAbs("https://cdn.example.com/v.mp4"),
            _ => OkMediaResponse(mp4Body));
        var cookieService = new MediaDownloadService(new SafeHttpRequestSender(new HttpClient(cookieHandler), validator), null);
        var variant = VariantWith(new Uri("https://v.douyin.com/redirect"), new MediaRequestContext(referer, null));
        var cookieResult = cookieService.DownloadAsync(TaskFor(cookieTarget, variant, Mp4Asset()), CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Succeeded, cookieResult.Status, "non-session variant downloads via http");
        // 第一跳 Referer 保留（同源 douyin）
        AssertEqual(referer.ToString(), cookieHandler.Requests[0].Headers.Referrer?.ToString(), "referer sent on first hop");
        // 跨源跳到 cdn.example.com：Referer 被清除
        AssertEqual(true, cookieHandler.Requests[1].Headers.Referrer is null, "cross-origin referer cleared on redirect");

        // 缓存命中：直接从缓存复制，不发起任何网络请求
        var cacheUri = new Uri("https://cdn.example.com/cached.mp4");
        var cacheRoot = Path.Combine(root, "cache");
        var mediaCache = new MediaCache(cacheRoot);
        using (var ms = new MemoryStream(mp4Body))
        {
            mediaCache.SaveAsync(cacheUri, ms, CancellationToken.None).GetAwaiter().GetResult();
        }
        var cacheTarget = Path.Combine(root, "cached.mp4");
        var cacheHandler = new StubHttpHandler(_ => OkMediaResponse(mp4Body));
        var cacheService = new MediaDownloadService(new SafeHttpRequestSender(new HttpClient(cacheHandler), validator), null, mediaCache: mediaCache);
        var cacheResult = cacheService.DownloadAsync(TaskFor(cacheTarget, VariantWith(cacheUri, new MediaRequestContext(null, null)), Mp4Asset()), CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Succeeded, cacheResult.Status, "cache hit downloads from cache");
        AssertEqual(true, File.Exists(cacheTarget), "cached file saved");
        AssertEqual(0, cacheHandler.Requests.Count, "no http request when cache hit");

        // 缓存内容无效：清理缓存并回退到网络下载
        var badCacheUri = new Uri("https://cdn.example.com/badcached.mp4");
        using (var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }))
        {
            mediaCache.SaveAsync(badCacheUri, ms, CancellationToken.None).GetAwaiter().GetResult();
        }
        var badCacheTarget = Path.Combine(root, "badcached.mp4");
        var badCacheService = new MediaDownloadService(new SafeHttpRequestSender(new HttpClient(cacheHandler), validator), null, mediaCache: mediaCache);
        var badCacheResult = badCacheService.DownloadAsync(TaskFor(badCacheTarget, VariantWith(badCacheUri, new MediaRequestContext(null, null)), Mp4Asset()), CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Succeeded, badCacheResult.Status, "invalid cache falls back to network");
        AssertEqual(true, mediaCache.GetCachedPath(badCacheUri) is null, "invalid cache entry removed");

        // 相同目标内容：幂等成功
        var dupeTarget = Path.Combine(root, "dupe.mp4");
        File.WriteAllBytes(dupeTarget, mp4Body);
        var dupeHandler = new StubHttpHandler(_ => OkMediaResponse(mp4Body));
        var dupeService = new MediaDownloadService(new SafeHttpRequestSender(new HttpClient(dupeHandler), validator), null);
        var dupeResult = dupeService.DownloadAsync(TaskFor(dupeTarget, VariantWith(new Uri("https://cdn.example.com/v.mp4"), new MediaRequestContext(null, null)), Mp4Asset()), CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Succeeded, dupeResult.Status, "identical target idempotent success");
        AssertEqual(dupeTarget, dupeResult.SavedPath, "identical target keeps path");

        // 不同目标内容：生成唯一文件名，不覆盖
        var diffTarget = Path.Combine(root, "diff.mp4");
        File.WriteAllBytes(diffTarget, new byte[] { 1, 2, 3, 4 });
        var diffHandler = new StubHttpHandler(_ => OkMediaResponse(mp4Body));
        var diffService = new MediaDownloadService(new SafeHttpRequestSender(new HttpClient(diffHandler), validator), null);
        var diffResult = diffService.DownloadAsync(TaskFor(diffTarget, VariantWith(new Uri("https://cdn.example.com/v.mp4"), new MediaRequestContext(null, null)), Mp4Asset()), CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Succeeded, diffResult.Status, "different content still succeeds");
        AssertEqual(Path.Combine(root, "diff(1).mp4"), diffResult.SavedPath, "unique path generated without overwrite");
        AssertEqual(true, File.Exists(Path.Combine(root, "diff(1).mp4")), "unique file exists");
        AssertEqual(true, File.Exists(diffTarget), "original file not overwritten");

        // TargetPath 越出目录：拒绝
        var escapeTarget = Path.Combine(root, "..", "escape.mp4");
        var escapeService = new MediaDownloadService(new SafeHttpRequestSender(new HttpClient(new StubHttpHandler(_ => OkMediaResponse(mp4Body))), validator), null);
        var escapeResult = escapeService.DownloadAsync(TaskFor(escapeTarget, VariantWith(new Uri("https://cdn.example.com/v.mp4"), new MediaRequestContext(null, null)), Mp4Asset()), CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Failed, escapeResult.Status, "path escaping directory rejected");

        static HttpResponseMessage OkMediaResponse(byte[] body)
        {
            var r = OkResponse(body);
            r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
            return r;
        }

        static HttpResponseMessage OkResponse(byte[] body) =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

        static HttpResponseMessage RedirectResponseAbs(string location) =>
            new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.Absolute) } };
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

// 场景：下载队列协调器（串行批次 + 并发上限 + 取消 + 历史计数）
static void TestDownloadCoordinator()
{
    var root = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var state = MediaStateStore.Load(new AppPaths(root));

        // 4 个任务、并发上限 3：峰值并发等于 3
        var gated = new GateDownloadService();
        using var coordinator = new MediaDownloadCoordinator(gated, state);
        var post = BuildValidPostForTest();
        var batch = BuildBatchForTest(post, 4, root);
        var batchTask = coordinator.EnqueueBatchAsync(batch, CancellationToken.None);
        Task.Delay(300).GetAwaiter().GetResult();
        AssertEqual(3, gated.MaxConcurrent, "concurrency peak equals 3");
        coordinator.CancelAllAsync().GetAwaiter().GetResult();
        batchTask.GetAwaiter().GetResult();
        AssertEqual(false, coordinator.HasActiveTasks, "no active tasks after cancel all");

        // 单任务取消不影响其他任务（门控服务：取消的任务落定为 Cancelled，其余继续挂起）
        var gatedCancel = new GateDownloadService();
        using var coordinator2 = new MediaDownloadCoordinator(gatedCancel, state);
        var batch2 = BuildBatchForTest(post, 3, root);
        var batch2Task = coordinator2.EnqueueBatchAsync(batch2, CancellationToken.None);
        Task.Delay(200).GetAwaiter().GetResult();
        coordinator2.CancelTask(batch2.Tasks[0].Id);
        Task.Delay(100).GetAwaiter().GetResult();
        AssertEqual(1, gatedCancel.Completed.Count(r => r.Status == MediaDownloadStatus.Cancelled), "cancelled task settles as cancelled");
        AssertEqual(2, gatedCancel.ActiveCount, "other tasks unaffected by single cancel");
        coordinator2.CancelAllAsync().GetAwaiter().GetResult();
        batch2Task.GetAwaiter().GetResult();

        // 批次串行化：第二个批次排队，第一个批次结束后才开始（使用独立目录避免历史混入）
        var serialRoot = Path.Combine(root, "serial");
        Directory.CreateDirectory(serialRoot);
        var serialState = MediaStateStore.Load(new AppPaths(serialRoot));
        var gated2 = new GateDownloadService();
        using var coordinator3 = new MediaDownloadCoordinator(gated2, serialState);
        var batch3a = BuildBatchForTest(post, 1, serialRoot);
        var batch3b = BuildBatchForTest(post, 1, serialRoot);
        var taskA = coordinator3.EnqueueBatchAsync(batch3a, CancellationToken.None);
        var taskB = coordinator3.EnqueueBatchAsync(batch3b, CancellationToken.None);
        Task.Delay(200).GetAwaiter().GetResult();
        AssertEqual(1, gated2.ActiveCount, "only first batch active while second queued");
        coordinator3.CancelAllAsync().GetAwaiter().GetResult();
        taskA.GetAwaiter().GetResult();
        taskB.GetAwaiter().GetResult();
        AssertEqual(2, serialState.GetHistory().Count, "both serialized batches write history");

        // 全部失败：历史成功数为 0
        var failState = MediaStateStore.Load(new AppPaths(root));
        using var coordinator4 = new MediaDownloadCoordinator(new FakeFailDownloadService(), failState);
        var batch4 = BuildBatchForTest(post, 2, root);
        coordinator4.EnqueueBatchAsync(batch4, CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(0, failState.GetHistory().Last().SuccessCount, "all-failed batch records zero success");
        AssertEqual(2, failState.GetHistory().Last().FailureCount, "all-failed batch counts failures");

        // 设置并发数修改后下一个批次生效
        state.UpdateSettings(state.Settings with { MaxConcurrentDownloads = 1 });
        var gated3 = new GateDownloadService();
        using var coordinator5 = new MediaDownloadCoordinator(gated3, state);
        var batch5 = BuildBatchForTest(post, 3, root);
        var batch5Task = coordinator5.EnqueueBatchAsync(batch5, CancellationToken.None);
        Task.Delay(200).GetAwaiter().GetResult();
        AssertEqual(1, gated3.MaxConcurrent, "concurrency setting honored on next batch");
        coordinator5.CancelAllAsync().GetAwaiter().GetResult();
        batch5Task.GetAwaiter().GetResult();

        // 完成事件包含批次 ID 与最终 SavedPath
        var eventState = MediaStateStore.Load(new AppPaths(root));
        var succeed = new FakeSucceedDownloadService();
        using var coordinator6 = new MediaDownloadCoordinator(succeed, eventState);
        var eventBatch = BuildBatchForTest(post, 1, root);
        MediaDownloadTaskCompleted? completedEvent = null;
        coordinator6.TaskCompleted += (_, e) => completedEvent = e;
        coordinator6.EnqueueBatchAsync(eventBatch, CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(true, completedEvent is not null, "completed event fired");
        AssertEqual(eventBatch.Id, completedEvent!.BatchId, "completed event batch id");
        AssertEqual(true, completedEvent.Result.SavedPath is not null, "completed event saved path");

        // 空批次被拒绝
        using var coordinator7 = new MediaDownloadCoordinator(new FakeSucceedDownloadService(), state);
        var emptyBatch = new MediaDownloadBatch(Guid.NewGuid(), "u", post, Array.Empty<MediaDownloadTask>());
        AssertThrows<ArgumentException>(() => coordinator7.EnqueueBatchAsync(emptyBatch, CancellationToken.None), "empty batch rejected");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

// 测试辅助：构造下载批次
static MediaDownloadBatch BuildBatchForTest(ResolvedMediaPost post, int count, string directory)
{
    var tasks = new List<MediaDownloadTask>();
    for (var i = 0; i < count; i++)
    {
        var variant = post.Assets[0].Variants[0];
        var target = Path.Combine(directory, $"t{i}.mp4");
        tasks.Add(new MediaDownloadTask(Guid.NewGuid(), post.Assets[0], variant, target));
    }
    return new MediaDownloadBatch(Guid.NewGuid(), "https://v.douyin.com/a/", post, tasks);
}

// 场景：直接媒体解析器
static void TestDirectMediaResolver()
{
    var validator = new SafeUriValidator(new FakeDnsResolver(_ => Task.FromResult(new IPAddress[] { IPAddress.Parse("8.8.8.8") })));

    // 图片直接 URL -> 单图片资产
    var imageHandler = new StubHttpHandler(_ => { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) }; r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg"); return r; });
    var imageResolver = new DirectMediaResolver(new SafeHttpRequestSender(new HttpClient(imageHandler), validator));
    AssertEqual(true, imageResolver.CanResolve(new Uri("https://cdn.example.com/1.jpg")), "direct can resolve http url");
    AssertEqual(false, imageResolver.CanResolve(new Uri("https://v.douyin.com/abc/")), "douyin page domain not direct");
    AssertEqual(false, imageResolver.CanResolve(new Uri("https://www.douyin.com/video/1")), "www.douyin.com not direct");
    var imageResult = imageResolver.ResolveAsync(new MediaResolveRequest(new Uri("https://cdn.example.com/1.jpg"), MediaResolveMode.Automatic, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.Succeeded, imageResult.Status, "direct image succeeds");
    AssertEqual(1, imageResult.Post!.Assets.Count, "direct image single asset");
    AssertEqual(MediaKind.Image, imageResult.Post.Assets[0].Kind, "direct image kind");

    // 视频直接 URL -> 单视频资产
    var videoHandler = new StubHttpHandler(_ => { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) }; r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4"); return r; });
    var videoResolver = new DirectMediaResolver(new SafeHttpRequestSender(new HttpClient(videoHandler), validator));
    var videoResult = videoResolver.ResolveAsync(new MediaResolveRequest(new Uri("https://cdn.example.com/v.mp4"), MediaResolveMode.Automatic, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaKind.Video, videoResult.Post!.Assets[0].Kind, "direct video kind");

    // HTML -> Unsupported（业务结果，不抛异常）
    var htmlHandler = new StubHttpHandler(_ => { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) }; r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html"); return r; });
    var htmlResolver = new DirectMediaResolver(new SafeHttpRequestSender(new HttpClient(htmlHandler), validator));
    var htmlResult = htmlResolver.ResolveAsync(new MediaResolveRequest(new Uri("https://cdn.example.com/page"), MediaResolveMode.Automatic, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.Unsupported, htmlResult.Status, "direct html unsupported");

    // TLS 握手被拒（如抖音 CDN 域名）+ 已挂接浏览器：按扩展名乐观返回作品（下载阶段走浏览器栈）
    var tlsHandler = new ThrowingHttpHandler(new HttpRequestException(
        HttpRequestError.SecureConnectionError,
        "The SSL connection could not be established, see inner exception.",
        new IOException("Received an unexpected EOF or 0 bytes from the transport stream.")));
    var tlsProxy = new BrowserSessionAccessorProxy();
    tlsProxy.Attach(new FakeBrowserSession());
    var tlsResolver = new DirectMediaResolver(new SafeHttpRequestSender(new HttpClient(tlsHandler), validator), tlsProxy);
    var tlsImageResult = tlsResolver.ResolveAsync(new MediaResolveRequest(new Uri("https://p81-sign.douyinpic.com/x/y~tplv-s:1440:2560:q80.jpeg?lk3s=1"), MediaResolveMode.Automatic, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.Succeeded, tlsImageResult.Status, "tls-rejected direct image resolves via extension");
    AssertEqual(MediaKind.Image, tlsImageResult.Post!.Assets[0].Kind, "tls-rejected image kind from extension");
    var tlsVideoResult = tlsResolver.ResolveAsync(new MediaResolveRequest(new Uri("https://v.douyinvod.com/x/y.mp4?video_id=1"), MediaResolveMode.Automatic, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.Succeeded, tlsVideoResult.Status, "tls-rejected direct video resolves via extension");
    AssertEqual(MediaKind.Video, tlsVideoResult.Post!.Assets[0].Kind, "tls-rejected video kind from extension");

    // TLS 被拒 + 浏览器未挂接：维持原行为（异常交由协调器报告）
    var tlsNoBrowser = new DirectMediaResolver(new SafeHttpRequestSender(new HttpClient(new ThrowingHttpHandler(tlsHandler.Exception)), validator));
    var tlsNoBrowserThrown = false;
    try
    {
        tlsNoBrowser.ResolveAsync(new MediaResolveRequest(new Uri("https://p81-sign.douyinpic.com/x/y.jpeg"), MediaResolveMode.Automatic, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    }
    catch (HttpRequestException)
    {
        tlsNoBrowserThrown = true;
    }
    AssertEqual(true, tlsNoBrowserThrown, "tls rejection without browser still throws");

    // TLS 被拒 + 无扩展名 URL：保持保守，不猜测
    var tlsUnknown = new DirectMediaResolver(new SafeHttpRequestSender(new HttpClient(new ThrowingHttpHandler(tlsHandler.Exception)), validator), tlsProxy);
    var tlsUnknownThrown = false;
    try
    {
        tlsUnknown.ResolveAsync(new MediaResolveRequest(new Uri("https://p81-sign.douyinpic.com/tos-cn-i-0813/abc123"), MediaResolveMode.Automatic, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    }
    catch (HttpRequestException)
    {
        tlsUnknownThrown = true;
    }
    AssertEqual(true, tlsUnknownThrown, "tls rejection without recognizable extension throws");
}

// 场景：媒体下载页面 MVP（STA）
static void TestMediaDownloadPage()
{
    RunSta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = MediaStateStore.Load(new AppPaths(root));
            var download = new MediaDownloadCoordinator(new FakeSucceedDownloadService(), state);
            var resolve = new MediaResolveCoordinator(new MediaResolverRegistry(new IMediaResolver[] { new FakeResolver(MediaProviderId.Douyin, resultFactory: (_, _) => MediaResolveResult.RequiresUserInteraction("需要浏览器")) }));
            var validator = new SafeUriValidator(new FakeDnsResolver(_ => Task.FromResult(new IPAddress[] { IPAddress.Parse("8.8.8.8") })));
            using var preview = new MediaPreviewService(new SafeHttpRequestSender(new HttpClient(), validator), null);
            using var page = new MediaDownloadPage(resolve, download, state, _ => ValueTask.CompletedTask, preview);

            // 页面契约
            AssertEqual("media-download", page.Id, "media page id");
            AssertEqual("媒体下载", page.DisplayName, "media page display name");
            AssertEqual(true, page.View is UserControl, "media page view");

            // 保存目录初始值来自设置
            AssertEqual(state.Settings.DownloadDirectory, page.DownloadDirectoryText, "directory initial from settings");

            // 初始状态：解析与浏览器可用、下载禁用
            AssertEqual(MediaPageState.Idle, page.CurrentState, "initial state idle");
            AssertEqual(true, page.ParseButtonEnabled, "parse enabled initially");
            AssertEqual(true, page.BrowserButtonEnabled, "browser enabled initially");
            AssertEqual(false, page.DownloadButtonEnabled, "download disabled initially");

            // WaitingForUser：浏览器按钮启用，普通解析与下载禁用
            page.ResolveInputAsync("https://v.douyin.com/a/").GetAwaiter().GetResult();
            AssertEqual(MediaPageState.WaitingForUser, page.CurrentState, "interaction state");
            AssertEqual(true, page.BrowserButtonEnabled, "browser enabled in waiting state");
            AssertEqual(false, page.ParseButtonEnabled, "parse disabled in waiting state");
            AssertEqual(false, page.DownloadButtonEnabled, "download disabled in waiting state");

            download.Dispose();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    });
}

// 场景：媒体服务组合与生命周期（释放顺序、浏览器延迟初始化）
static void TestMediaServicesLifecycle()
{
    var root = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var state = MediaStateStore.Load(new AppPaths(root));
        var http = new HttpClient();
        var download = new MediaDownloadCoordinator(new FakeSucceedDownloadService(), state);
        var resolve = new MediaResolveCoordinator(new MediaResolverRegistry(Array.Empty<IMediaResolver>()));
        var proxy = new BrowserSessionAccessorProxy();
        var validator = new SafeUriValidator(new FakeDnsResolver(_ => Task.FromResult(new IPAddress[] { IPAddress.Parse("8.8.8.8") })));
        var services = new MediaServices
        {
            State = state,
            ResolveCoordinator = resolve,
            DownloadCoordinator = download,
            BrowserSessions = proxy,
            HttpClient = http,
            Preview = new MediaPreviewService(new SafeHttpRequestSender(new HttpClient(), validator), null),
        };

        // 未设置工厂时：浏览器能力不可用但 Direct 下载正常
        // （WinForms 控件必须在 STA 线程创建，故在 RunSta 中验证初始化失败）
        AssertEqual(false, proxy.IsAvailable, "browser unavailable before factory");
        RunSta(() =>
        {
            var browserError = false;
            try
            {
                using var probeControl = new UserControl();
                services.EnsureBrowserInitializedAsync(probeControl).GetAwaiter().GetResult();
            }
            catch (InvalidOperationException)
            {
                browserError = true;
            }
            AssertEqual(true, browserError, "browser init without factory throws recognizable error");
            AssertEqual(false, proxy.IsAvailable, "proxy stays unavailable after failed init");
        });

        // Attach 后代理委托调用
        proxy.Attach(new FakeBrowserSession(new BrowserCookie("douyin.com", "/", "a", "1", false)));
        AssertEqual(true, proxy.IsAvailable, "browser available after attach");
        var cookies = proxy.GetCookiesAsync("s1", new Uri("https://v.douyin.com/x"), CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(1, cookies.Count, "proxy delegates cookie call");

        // 未 Attach 的代理安全返回 unavailable / 空集合
        var emptyProxy = new BrowserSessionAccessorProxy();
        var emptyCapture = emptyProxy.CaptureAsync(new Uri("https://v.douyin.com/x"), false, CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(BrowserCaptureStatus.Unavailable, emptyCapture.Status, "unattached proxy capture unavailable");
        AssertEqual(0, emptyProxy.GetCookiesAsync("s1", new Uri("https://v.douyin.com/x"), CancellationToken.None).GetAwaiter().GetResult().Count, "unattached proxy cookies empty");

        // DisposeAsync 可重复调用且不抛异常；取消任务完成后才释放 HttpClient
        services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        services.Dispose();
        services.Dispose();
        AssertEqual(true, true, "dispose is idempotent");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

// 场景：浏览器会话代理
static void TestBrowserProxy()
{
    var proxy = new BrowserSessionAccessorProxy();
    AssertEqual(false, proxy.IsAvailable, "proxy unavailable initially");

    var inner = new FakeBrowserSession();
    proxy.Attach(inner);
    AssertEqual(true, proxy.IsAvailable, "proxy available after attach");

    var cookies = proxy.GetCookiesAsync("s1", new Uri("https://v.douyin.com/x"), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(0, cookies.Count, "proxy delegates to empty session");

    proxy.DisposeAsync().AsTask().GetAwaiter().GetResult();
    AssertEqual(false, proxy.IsAvailable, "proxy unavailable after dispose");
}

// 场景：抖音页面解析（本地 fixture，不访问网络）
static void TestDouyinPageParser()
{
    static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MediaDownload", name));

    var video = DouyinPageParser.Parse(ReadFixture("douyin-video-page.html"));
    AssertEqual("7123456789012345678", video.PostId, "video post id");
    AssertEqual("fixture 视频标题", video.Title, "video title");
    AssertEqual("fixture 作者", video.AuthorName, "video author");
    AssertEqual(true, !video.EmptyShell && !video.LoginRequired, "video page not shell or login");
    AssertEqual(1, video.Assets.Count, "video single asset");
    AssertEqual(MediaKind.Video, video.Assets[0].Kind, "video asset kind");
    AssertEqual(4, video.Assets[0].Variants.Count, "video variants (high/low/dl/cover)");

    var carousel = DouyinPageParser.Parse(ReadFixture("douyin-image-carousel-page.html"));
    AssertEqual(9, carousel.Assets.Count, "nine image assets");
    for (var i = 0; i < 9; i++)
    {
        AssertEqual(i, carousel.Assets[i].OrderIndex, $"image {i + 1} order");
    }
    AssertEqual(2, carousel.Assets[8].Variants.Count, "ninth image two variants");

    var shell = DouyinPageParser.Parse(ReadFixture("douyin-empty-shell.html"));
    AssertEqual(true, shell.EmptyShell, "empty shell detected");

    var login = DouyinPageParser.Parse(ReadFixture("douyin-login-required.html"));
    AssertEqual(true, login.LoginRequired, "login required detected");

    var removed = DouyinPageParser.Parse(ReadFixture("douyin-removed-page.html"));
    AssertEqual(true, removed.FailureReason is not null, "removed page failure reason");

    // 嵌套 RENDER_DATA（真实抖音结构 {"app":{"aweme":{"detail":{"aweme_detail":{...}}}}}）：
    // 递归查找 aweme_detail，解析出全部图片并保持顺序
    var nested = DouyinPageParser.ParseStructuredData(ReadFixture("douyin-nested-structured-data.json"));
    AssertEqual(false, nested.EmptyShell, "nested structure not empty shell");
    AssertEqual("嵌套结构图文作品", nested.Title, "nested title");
    AssertEqual("嵌套作者", nested.AuthorName, "nested author");
    AssertEqual(3, nested.Assets.Count, "nested three image assets");
    AssertEqual(MediaKind.Image, nested.Assets[0].Kind, "nested first kind image");
    for (var i = 0; i < 3; i++)
    {
        AssertEqual(i, nested.Assets[i].OrderIndex, $"nested asset {i + 1} order");
    }
    AssertEqual(2, nested.Assets[0].Variants.Count, "nested first image two variants");
}

// 场景：抖音候选归一化（顺序保持、去重、过滤非作品媒体）
static void TestDouyinMediaNormalizer()
{
    static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MediaDownload", name));

    var videoSource = new Uri("https://www.douyin.com/video/7123456789012345678");
    var video = DouyinMediaNormalizer.Normalize(videoSource, DouyinPageParser.Parse(ReadFixture("douyin-video-page.html")));
    AssertEqual(MediaProviderId.Douyin, video.Provider, "provider douyin");
    AssertEqual(1, video.Assets.Count, "single video asset after normalize");
    AssertEqual(MediaKind.Video, video.Assets[0].Kind, "video kind");
    // 头像/推荐被过滤，封面保留为缩略图变体
    AssertEqual(true, video.Assets[0].Variants.All(v => !v.Uri.ToString().Contains("avatar") && !v.Uri.ToString().Contains("recommend")), "avatar and recommend filtered");
    AssertEqual(true, video.Assets[0].Variants.Any(v => v.Source == MediaVariantSource.Thumbnail), "cover kept as thumbnail variant");
    AssertEqual(videoSource.ToString(), video.Assets[0].Variants[0].RequestContext.Referer?.ToString(), "referer set to page uri");

    var carouselSource = new Uri("https://www.douyin.com/note/7123456789012345000");
    var carousel = DouyinMediaNormalizer.Normalize(carouselSource, DouyinPageParser.Parse(ReadFixture("douyin-image-carousel-page.html")));
    AssertEqual(9, carousel.Assets.Count, "nine assets in order");
    for (var i = 0; i < 9; i++)
    {
        AssertEqual(i, carousel.Assets[i].Index, $"asset index {i}");
    }
    AssertEqual(true, carousel.Assets.All(a => a.Variants.All(v => !v.Uri.ToString().Contains("logo"))), "logo filtered");
    AssertEqual(2, carousel.Assets[8].Variants.Count, "same-image urls grouped as variants");
}

// 场景：抖音媒体解析器（Automatic 静态 + BrowserInteractive 浏览器）
static void TestDouyinMediaResolver()
{
    static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MediaDownload", name));

    var validator = new SafeUriValidator(new FakeDnsResolver(_ => Task.FromResult(new IPAddress[] { IPAddress.Parse("8.8.8.8") })));
    var proxy = new BrowserSessionAccessorProxy();
    var resolver = new DouyinMediaResolver(new DouyinHttpPageResolver(new SafeHttpRequestSender(new HttpClient(), validator)), proxy);

    // 域名匹配：边界正确
    AssertEqual(true, resolver.CanResolve(new Uri("https://v.douyin.com/abc/")), "douyin subdomain resolves");
    AssertEqual(true, resolver.CanResolve(new Uri("https://www.douyin.com/video/1")), "www.douyin resolves");
    AssertEqual(true, resolver.CanResolve(new Uri("https://iesdouyin.com/x")), "iesdouyin resolves");
    AssertEqual(false, resolver.CanResolve(new Uri("https://evildouyin.com/x")), "evildouyin not matched");
    AssertEqual(false, resolver.CanResolve(new Uri("https://other.com/x")), "other domain not matched");

    // BrowserInteractive：代理不可用 → RequiresUserInteraction
    var noProxy = new BrowserSessionAccessorProxy();
    var resolverNoProxy = new DouyinMediaResolver(new DouyinHttpPageResolver(new SafeHttpRequestSender(new HttpClient(), validator)), noProxy);
    var unavailable = resolverNoProxy.ResolveAsync(new MediaResolveRequest(new Uri("https://v.douyin.com/abc/"), MediaResolveMode.BrowserInteractive, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.RequiresUserInteraction, unavailable.Status, "browser mode without session requires interaction");

    // BrowserInteractive：捕获成功 → 成功并携带会话 ID（不携带 Cookie）
    var captureSession = new CapturingBrowserSession(ReadFixture("douyin-structured-data.json"));
    proxy.Attach(captureSession);
    var captured = resolver.ResolveAsync(new MediaResolveRequest(new Uri("https://www.douyin.com/video/7000000000000000000"), MediaResolveMode.BrowserInteractive, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.Succeeded, captured.Status, "browser capture succeeds");
    AssertEqual("fixture 浏览器捕获样本", captured.Post!.Title, "browser capture title");
    AssertEqual(1, captured.Post.Assets.Count, "browser capture asset count");
    AssertEqual("session-1", captured.Post.Assets[0].Variants[0].RequestContext.BrowserSessionId, "session id carried, not cookies");

    // 去死锁回归：IsAvailable=false（首次挂接尚未初始化）时 Capture 仍被调用，
    // 首次初始化发生在捕获内部，而非在解析器前置拦截
    var lazyProxy = new BrowserSessionAccessorProxy();
    lazyProxy.Attach(new LazyBrowserSession(ReadFixture("douyin-structured-data.json")));
    AssertEqual(false, lazyProxy.IsAvailable, "lazy session not yet ready");
    var lazyResolver = new DouyinMediaResolver(new DouyinHttpPageResolver(new SafeHttpRequestSender(new HttpClient(), validator)), lazyProxy);
    var lazyResult = lazyResolver.ResolveAsync(new MediaResolveRequest(new Uri("https://www.douyin.com/video/1"), MediaResolveMode.BrowserInteractive, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.Succeeded, lazyResult.Status, "capture called even when not yet initialized");

    // Automatic：空壳/登录 → 浏览器兜底
    // 浏览器不可用时：RequiresUserInteraction（提示使用浏览器）
    var shellHandler = new StubHttpHandler(_ => { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReadFixture("douyin-empty-shell.html"), System.Text.Encoding.UTF8, "text/html") }; return r; });
    var shellResolverNoBrowser = new DouyinMediaResolver(new DouyinHttpPageResolver(new SafeHttpRequestSender(new HttpClient(shellHandler), validator)), new BrowserSessionAccessorProxy());
    var shellResultNoBrowser = shellResolverNoBrowser.ResolveAsync(new MediaResolveRequest(new Uri("https://v.douyin.com/abc/"), MediaResolveMode.Automatic, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.RequiresUserInteraction, shellResultNoBrowser.Status, "empty shell without browser requires interaction");

    // 浏览器兜底成功时：空壳自动转入隐藏浏览器解析并成功
    var shellResolver = new DouyinMediaResolver(new DouyinHttpPageResolver(new SafeHttpRequestSender(new HttpClient(shellHandler), validator)), proxy);
    var shellResult = shellResolver.ResolveAsync(new MediaResolveRequest(new Uri("https://v.douyin.com/abc/"), MediaResolveMode.Automatic, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.Succeeded, shellResult.Status, "empty shell falls back to hidden browser");
    AssertEqual("fixture 浏览器捕获样本", shellResult.Post!.Title, "browser fallback title");

    // Automatic：TLS 握手被拒 → 自动转隐藏浏览器 → 成功并熔断
    var tlsHandler = new ThrowingHttpHandler(new HttpRequestException(
        HttpRequestError.SecureConnectionError,
        "The SSL connection could not be established, see inner exception.",
        new IOException("Received an unexpected EOF or 0 bytes from the transport stream.")));
    var tlsPreference = new DouyinTransportPreferenceState();
    var tlsProxy = new BrowserSessionAccessorProxy();
    tlsProxy.Attach(new CapturingBrowserSession(ReadFixture("douyin-structured-data.json")));
    var tlsResolver = new DouyinMediaResolver(new DouyinHttpPageResolver(new SafeHttpRequestSender(new HttpClient(tlsHandler), validator)), tlsProxy, tlsPreference);
    var tlsResult = tlsResolver.ResolveAsync(new MediaResolveRequest(new Uri("https://v.douyin.com/abc/"), MediaResolveMode.Automatic, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.Succeeded, tlsResult.Status, "tls rejection falls back to browser");
    AssertEqual(1, tlsResult.Post!.Assets.Count, "tls fallback asset count");
    AssertEqual(1, tlsHandler.Requests.Count, "http attempted once before fallback");
    AssertEqual(true, tlsPreference.ShouldUseBrowser, "breaker tripped after tls rejection");

    // 熔断后：不再尝试 HttpClient，直接浏览器解析
    var breakerResult = tlsResolver.ResolveAsync(new MediaResolveRequest(new Uri("https://v.douyin.com/abc/"), MediaResolveMode.Automatic, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.Succeeded, breakerResult.Status, "breaker resolve succeeds via browser");
    AssertEqual(1, tlsHandler.Requests.Count, "http not attempted after breaker");

    // Automatic：TLS 被拒但浏览器不可用 → RequiresUserInteraction（不吞错误）
    var tlsNoBrowser = new DouyinMediaResolver(new DouyinHttpPageResolver(new SafeHttpRequestSender(new HttpClient(new ThrowingHttpHandler(tlsHandler.Exception)), validator)), new BrowserSessionAccessorProxy());
    var tlsNoBrowserResult = tlsNoBrowser.ResolveAsync(new MediaResolveRequest(new Uri("https://v.douyin.com/abc/"), MediaResolveMode.Automatic, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.RequiresUserInteraction, tlsNoBrowserResult.Status, "tls fallback without browser requires interaction");

    // Automatic：删除作品 → Failed
    var removedHandler = new StubHttpHandler(_ => { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReadFixture("douyin-removed-page.html"), System.Text.Encoding.UTF8, "text/html") }; return r; });
    var removedResolver = new DouyinMediaResolver(new DouyinHttpPageResolver(new SafeHttpRequestSender(new HttpClient(removedHandler), validator)), proxy);
    var removedResult = removedResolver.ResolveAsync(new MediaResolveRequest(new Uri("https://v.douyin.com/abc/"), MediaResolveMode.Automatic, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.Failed, removedResult.Status, "removed work fails");

    // Automatic：静态页面解析成功（fixture 页）
    var pageHandler = new StubHttpHandler(_ => { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReadFixture("douyin-video-page.html"), System.Text.Encoding.UTF8, "text/html") }; return r; });
    var pageResolver = new DouyinMediaResolver(new DouyinHttpPageResolver(new SafeHttpRequestSender(new HttpClient(pageHandler), validator)), proxy);
    var pageResult = pageResolver.ResolveAsync(new MediaResolveRequest(new Uri("https://v.douyin.com/abc/"), MediaResolveMode.Automatic, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.Succeeded, pageResult.Status, "static page resolves");
    AssertEqual(1, pageResult.Post!.Assets.Count, "static page single video asset");
}

// 场景：传输失败分类器（结构化分类，不依赖异常消息字符串）
static void TestDouyinTransportClassifier()
{
    static DouyinTransportFailureKind Classify(Exception ex) => DouyinTransportClassifier.Classify(ex);

    // 安全策略拒绝：不进入浏览器兜底
    var policy = new UriValidationException(UriValidationKind.BlockedByPolicy, "URI 校验失败：目标地址属于禁止访问的网络范围。");
    AssertEqual(DouyinTransportFailureKind.SecurityPolicyRejected, Classify(policy), "policy rejection classified");
    AssertEqual(false, DouyinTransportClassifier.ShouldUseBrowserFallback(DouyinTransportFailureKind.SecurityPolicyRejected), "policy rejection not fallback");

    // 带类别的 DNS 失败：可兜底
    var dns = new UriValidationException(UriValidationKind.DnsFailed, "URI 校验失败：域名解析失败。");
    AssertEqual(DouyinTransportFailureKind.DnsFailure, Classify(dns), "typed dns failure classified");
    AssertEqual(true, DouyinTransportClassifier.ShouldUseBrowserFallback(DouyinTransportFailureKind.DnsFailure), "dns failure fallback");

    // HttpRequestException 按 HttpRequestError 分类
    static HttpRequestException Hre(HttpRequestError error, string message = "m", Exception? inner = null)
        => new(error, message, inner);

    AssertEqual(DouyinTransportFailureKind.TlsHandshakeRejected, Classify(Hre(HttpRequestError.SecureConnectionError, "ssl", new IOException("EOF"))), "secure connection classified");
    AssertEqual(true, DouyinTransportClassifier.ShouldUseBrowserFallback(DouyinTransportFailureKind.TlsHandshakeRejected), "tls rejection fallback");
    AssertEqual(DouyinTransportFailureKind.DnsFailure, Classify(Hre(HttpRequestError.NameResolutionError)), "name resolution classified");
    AssertEqual(DouyinTransportFailureKind.ConnectionReset, Classify(Hre(HttpRequestError.ConnectionError)), "connection error classified");
    AssertEqual(DouyinTransportFailureKind.ResponseEnded, Classify(Hre(HttpRequestError.ResponseEnded)), "response ended classified");
    AssertEqual(DouyinTransportFailureKind.ConnectionReset, Classify(Hre(HttpRequestError.ProxyTunnelError)), "proxy tunnel classified");
    AssertEqual(DouyinTransportFailureKind.Unknown, Classify(Hre(HttpRequestError.HttpProtocolError)), "protocol error unknown");
    AssertEqual(false, DouyinTransportClassifier.ShouldUseBrowserFallback(DouyinTransportFailureKind.Unknown), "unknown not fallback");

    // 超时与流中断：可兜底
    AssertEqual(DouyinTransportFailureKind.Timeout, Classify(new TaskCanceledException("timeout")), "timeout classified");
    AssertEqual(true, DouyinTransportClassifier.ShouldUseBrowserFallback(DouyinTransportFailureKind.Timeout), "timeout fallback");
    AssertEqual(DouyinTransportFailureKind.ResponseEnded, Classify(new IOException("stream ended")), "io exception classified");

    // 其他异常：未知且不兜底
    AssertEqual(DouyinTransportFailureKind.Unknown, Classify(new InvalidOperationException("other")), "other exception unknown");
}

// 场景：会话级传输熔断（仅当前进程有效，重启复位）
static void TestDouyinTransportPreference()
{
    var state = new DouyinTransportPreferenceState();
    AssertEqual(false, state.ShouldUseBrowser, "initially automatic");

    state.RecordFailure(DouyinTransportFailureKind.SecurityPolicyRejected);
    AssertEqual(false, state.ShouldUseBrowser, "policy rejection does not trip breaker");

    state.RecordFailure(DouyinTransportFailureKind.TlsHandshakeRejected);
    AssertEqual(true, state.ShouldUseBrowser, "tls rejection trips breaker");

    var fresh = new DouyinTransportPreferenceState();
    fresh.RecordFailure(DouyinTransportFailureKind.DnsFailure);
    AssertEqual(true, fresh.ShouldUseBrowser, "dns failure trips breaker");

    var fresh2 = new DouyinTransportPreferenceState();
    fresh2.RecordFailure(DouyinTransportFailureKind.Unknown);
    AssertEqual(false, fresh2.ShouldUseBrowser, "unknown does not trip breaker");
}

// 场景：错误链格式化（保留内层异常，限制长度）
static void TestErrorChainFormatter()
{
    var chain = ErrorChainFormatter.Format(new HttpRequestException("外层消息", new IOException("内层消息")));
    AssertEqual(true, chain.Contains("外层消息") && chain.Contains("[IOException]") && chain.Contains("内层消息"), "chain includes inner type and message");

    var single = ErrorChainFormatter.Format(new InvalidOperationException("solo"));
    AssertEqual("solo", single, "single exception message only");

    var longText = ErrorChainFormatter.Format(new InvalidOperationException(new string('x', 1000)));
    AssertEqual(401, longText.Length, "long chain truncated to limit plus ellipsis");
}

// 场景：CDP 连接（命令匹配、错误传播、事件分发、超时）
static void TestCdpConnection()
{
    using var server = new FakeCdpServer();
    var url = server.Start();

    var connection = new CdpConnection(url);
    var events = new List<(string Method, string? SessionId, int Value)>();
    connection.EventReceived += (method, parameters, sessionId) =>
    {
        events.Add((method, sessionId, parameters?["value"]?.GetValue<int>() ?? -1));
    };

    connection.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
    try
    {
        // 命令响应按 id 匹配
        var echo = connection.CommandAsync("Test.echo", new { x = 1 }, null, CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(true, echo?["ok"]?.GetValue<bool>() == true, "command response matched by id");
        AssertEqual("Test.echo", echo?["echo"]?.GetValue<string>(), "command echo payload");

        // sessionId 透传
        var withSession = connection.CommandAsync("Test.echo", null, "session-9", CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual("session-9", withSession?["session"]?.GetValue<string>(), "session id passed through");

        // CDP 错误转换为异常
        var errorThrown = false;
        try
        {
            connection.CommandAsync("Test.error", null, null, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("boom"))
        {
            errorThrown = true;
        }
        AssertEqual(true, errorThrown, "cdp error becomes exception");

        // 事件按方法 + sessionId 分发
        connection.CommandAsync("Test.event", null, null, CancellationToken.None).GetAwaiter().GetResult();
        Thread.Sleep(300);
        AssertEqual(1, events.Count, "event delivered once");
        AssertEqual("Test.emitted", events[0].Method, "event method delivered");
        AssertEqual("s1", events[0].SessionId, "event session id delivered");
        AssertEqual(42, events[0].Value, "event parameters delivered");

        // 无响应的命令按超时失败
        var timeoutThrown = false;
        try
        {
            connection.CommandAsync("Test.silent", null, null, CancellationToken.None, timeoutSeconds: 1).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            timeoutThrown = true;
        }
        AssertEqual(true, timeoutThrown, "silent command times out");
    }
    finally
    {
        connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

// 场景：Edge 复用代理地址一致性比较（规范化忽略引号/顺序）
static void TestEdgeProxyComparison()
{
    AssertEqual(true, EdgeProcessManager.ProxyEquals(null, null), "both null equal");
    AssertEqual(false, EdgeProcessManager.ProxyEquals(null, "http://127.0.0.1:7897"), "null vs value differ");
    AssertEqual(false, EdgeProcessManager.ProxyEquals("http://127.0.0.1:7897", null), "value vs null differ");
    AssertEqual(true, EdgeProcessManager.ProxyEquals("http://127.0.0.1:7897", "http://127.0.0.1:7897"), "same proxy equal");
    AssertEqual(true, EdgeProcessManager.ProxyEquals("http://127.0.0.1:7897", "http://127.0.0.1:7897/"), "trailing slash ignored");
    AssertEqual(false, EdgeProcessManager.ProxyEquals("http://127.0.0.1:7897", "http://127.0.0.1:8080"), "different port differ");
    AssertEqual(false, EdgeProcessManager.ProxyEquals("http://127.0.0.1:7897", "https://127.0.0.1:7897"), "different scheme differ");
    AssertEqual(false, EdgeProcessManager.ProxyEquals("http://127.0.0.1:7897", "http://localhost:7897"), "different host differ");
    AssertEqual(true, EdgeProcessManager.ProxyEquals("socks5://127.0.0.1:1080", "socks5://127.0.0.1:1080"), "socks proxy equal");
    AssertEqual(false, EdgeProcessManager.ProxyEquals("not-a-proxy", "http://127.0.0.1:7897"), "invalid value treated as different");
}

// 场景：网络模式设置校验（CustomProxy 需要有效代理地址）
static void TestMediaNetworkModeSettings()
{
    var settings = new MediaDownloadSettings(@"C:\dl", 3, true, false, MediaQualityPreference.Highest);
    AssertEqual(MediaNetworkMode.Direct, settings.NetworkMode, "network mode default direct");
    AssertEqual(true, settings.ProxyAddress == string.Empty, "proxy address default empty");

    // CustomProxy 无地址 → 校验拒绝
    var noAddress = settings with { NetworkMode = MediaNetworkMode.CustomProxy, ProxyAddress = "" };
    var rejected = false;
    try { noAddress.Validate(); }
    catch (ArgumentException) { rejected = true; }
    AssertEqual(true, rejected, "custom proxy without address rejected");

    // CustomProxy 非法地址 → 校验拒绝
    var badAddress = settings with { NetworkMode = MediaNetworkMode.CustomProxy, ProxyAddress = "not-a-proxy" };
    var rejectedBad = false;
    try { badAddress.Validate(); }
    catch (ArgumentException) { rejectedBad = true; }
    AssertEqual(true, rejectedBad, "custom proxy with invalid address rejected");

    // CustomProxy 有效地址 → 校验通过
    var valid = settings with { NetworkMode = MediaNetworkMode.CustomProxy, ProxyAddress = "http://127.0.0.1:7897" };
    valid.Validate();
    AssertEqual(true, true, "custom proxy with valid address accepted");

    // Direct/System 无需地址
    var direct = settings with { NetworkMode = MediaNetworkMode.Direct, ProxyAddress = "" };
    direct.Validate();
    var system = settings with { NetworkMode = MediaNetworkMode.System, ProxyAddress = "" };
    system.Validate();
    AssertEqual(true, true, "direct and system modes validate without address");
}

// 场景：媒体本地缓存（哈希命名、命中/写入/失效）
static void TestMediaCache()
{
    var root = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var cache = new MediaCache(Path.Combine(root, "MediaCache"));
        var uri = new Uri("https://cdn.example.com/1.jpg?x=1");

        // 未命中
        AssertEqual(true, cache.GetCachedPath(uri) is null, "cache miss initially");

        // 写入后可命中，且同一 URL 哈希稳定
        using (var ms = new MemoryStream(new byte[] { 1, 2, 3, 4 }))
        {
            cache.SaveAsync(uri, ms, CancellationToken.None).GetAwaiter().GetResult();
        }
        var cached = cache.GetCachedPath(uri);
        AssertEqual(true, cached is not null && File.Exists(cached), "cache hit after save");

        var uri2 = new Uri("https://cdn.example.com/2.jpg?x=1");
        AssertEqual(true, cache.GetCachedPath(uri2) is null, "different url different hash");

        // 失效删除
        cache.Invalidate(uri);
        AssertEqual(true, cache.GetCachedPath(uri) is null, "invalidate removes entry");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

// 场景：媒体设置窗体（STA）
static void TestMediaSettingsForm()
{
    RunSta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = MediaStateStore.Load(new AppPaths(root));
            state.UpdateSettings(state.Settings with { MaxConcurrentDownloads = 5 });

            using var form = new MediaSettingsForm(state);
            // 从 UI 回读当前设置
            var composed = form.ComposeFromControls();
            AssertEqual(5, composed.MaxConcurrentDownloads, "settings form reads concurrency");
            AssertEqual(state.Settings.DownloadDirectory, composed.DownloadDirectory, "settings form reads directory");
            AssertEqual(MediaQualityPreference.Highest, composed.QualityPreference, "settings form reads quality");
            AssertEqual(MediaNetworkMode.Direct, composed.NetworkMode, "network mode direct by default");

            // 校验非法组合被拒绝
            var invalid = composed with { MaxConcurrentDownloads = 99 };
            var rejected = false;
            try
            {
                invalid.Validate();
            }
            catch (ArgumentOutOfRangeException)
            {
                rejected = true;
            }
            AssertEqual(true, rejected, "settings form rejects invalid concurrency");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    });
}

// 场景：媒体预览服务（安全链路、大小与魔数校验、取消清理、不写历史）
static void TestMediaPreviewService()
{
    var root = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var state = MediaStateStore.Load(new AppPaths(root));
        var validator = new SafeUriValidator(new FakeDnsResolver(_ => Task.FromResult(new IPAddress[] { IPAddress.Parse("8.8.8.8") })));

        // 预览下载成功后不写下载历史
                var pngBody = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0, 0, 0 };
        var okHandler = new StubHttpHandler(_ => { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pngBody) }; r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png"); return r; });
        using var preview = new MediaPreviewService(new SafeHttpRequestSender(new HttpClient(okHandler), validator), null);
        var variant = new MediaVariant(new Uri("https://cdn.example.com/p.png"), 100, 100, null, null, null, "image/png", null, MediaVariantSource.StructuredData, new MediaRequestContext(null, null));
        var path = preview.DownloadPreviewAsync(variant, CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(true, File.Exists(path), "preview file exists");
        AssertEqual(0, state.GetHistory().Count, "preview does not write download history");

        // 非图片 Content-Type 拒绝
        var htmlHandler = new StubHttpHandler(_ => { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) }; r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html"); return r; });
        using var htmlPreview = new MediaPreviewService(new SafeHttpRequestSender(new HttpClient(htmlHandler), validator), null);
        var htmlRejected = false;
        try
        {
            htmlPreview.DownloadPreviewAsync(variant, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (InvalidDataException)
        {
            htmlRejected = true;
        }
        AssertEqual(true, htmlRejected, "html preview rejected");

        // 声明大小超 50 MiB 拒绝
        var bigHandler = new StubHttpHandler(_ => { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pngBody) }; r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png"); r.Content.Headers.ContentLength = 60 * 1024 * 1024; return r; });
        using var bigPreview = new MediaPreviewService(new SafeHttpRequestSender(new HttpClient(bigHandler), validator), null);
        var bigRejected = false;
        try
        {
            bigPreview.DownloadPreviewAsync(variant, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (InvalidDataException)
        {
            bigRejected = true;
        }
        AssertEqual(true, bigRejected, "oversized preview rejected");

        // 伪装图片（ZIP 魔数）拒绝
        var zipBody = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var zipHandler = new StubHttpHandler(_ => { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBody) }; r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png"); return r; });
        using var zipPreview = new MediaPreviewService(new SafeHttpRequestSender(new HttpClient(zipHandler), validator), null);
        var zipRejected = false;
        try
        {
            zipPreview.DownloadPreviewAsync(variant, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (InvalidDataException)
        {
            zipRejected = true;
        }
        AssertEqual(true, zipRejected, "zip disguised as image rejected");

        // 取消清理临时文件
                using var cts = new CancellationTokenSource();
        var slowHandler = new StubHttpHandler(_ => { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new SlowStreamContent(cts.Token) }; r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png"); return r; });
        slowHandler.Requests.Clear();
        using var cancelPreview = new MediaPreviewService(new SafeHttpRequestSender(new HttpClient(slowHandler), validator), null);
        var cancelTask = cancelPreview.DownloadPreviewAsync(variant, cts.Token);
        cts.CancelAfter(200);
        var cancelled = false;
        try
        {
            cancelTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        AssertEqual(true, cancelled, "preview cancel propagates");
        cancelPreview.ClearSessionCache();
        AssertEqual(true, true, "preview session cache cleared");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

// 场景：资产表（分段不进入可下载选择）
static void TestMediaAssetGrid()
{
    RunSta(() =>
    {
        var grid = new MediaAssetGrid();
        var context = new MediaRequestContext(null, null);
        var normal = new MediaVariant(new Uri("https://x/1.jpg"), 100, 100, null, null, 1000, "image/jpeg", null, MediaVariantSource.StructuredData, context);
        var segmented = new MediaVariant(new Uri("https://x/hls.m3u8"), 1920, 1080, null, null, null, "application/vnd.apple.mpegurl", null, MediaVariantSource.StructuredData, context, true);
        var post = new ResolvedMediaPost(MediaProviderId.Douyin, new Uri("https://v.douyin.com/a/"), "1", "t", "a", new MediaAsset[]
        {
            new MediaAsset(0, MediaKind.Image, new MediaVariant[] { normal }),
            new MediaAsset(1, MediaKind.Video, new MediaVariant[] { segmented }),
        });
        var selections = new MediaSelectionResult[]
        {
            new(MediaSelectionStatus.Selected, normal, null),
            new(MediaSelectionStatus.UnsupportedSegmented, null, "不支持合并"),
        };

        // 全选后仅可下载行被选中（分段不进入下载任务）
        grid.LoadPost(post, selections, defaultSelectAll: true);
        grid.SelectAll();
        var selected = grid.GetSelected();
        AssertEqual(1, selected.Count, "segmented asset not selectable");
        AssertEqual(0, selected[0].Asset.Index, "normal asset selected");
    });
}

// 场景：结构化数据兜底（NEXT_DATA 内嵌、URL 编码、详情接口响应）
static void TestDouyinStructuredDataFallbacks()
{
    static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MediaDownload", name));

    // NEXT_DATA 结构：作品数据深嵌在 props.pageProps 中
    var nextJson = ReadFixture("douyin-next-data.json");
    var nextData = DouyinPageParser.ParseStructuredData(nextJson);
    AssertEqual(3, nextData.Assets.Count, "next data image count");
    AssertEqual("7200000000000000001", nextData.PostId, "next data post id");
    AssertEqual("fixture NEXT_DATA 图文", nextData.Title, "next data title");
    AssertEqual(2, nextData.Assets[2].Variants.Count, "next data third image two variants");

    // 详情接口响应：根级 aweme_detail
    var apiData = DouyinPageParser.ParseStructuredData(ReadFixture("douyin-detail-api.json"));
    AssertEqual(3, apiData.Assets.Count, "detail api image count");
    AssertEqual("fixture 详情接口图文", apiData.Title, "detail api title");

    // 实况作品：images + video 混合 → 只产出图片资产，忽略封面/预览视频
    var liveData = DouyinPageParser.ParseStructuredData(ReadFixture("douyin-live-photo.json"));
    AssertEqual(3, liveData.Assets.Count, "live photo: three image assets");
    AssertEqual(MediaKind.Image, liveData.Assets[0].Kind, "live photo: image kind");
    AssertEqual(true, liveData.Assets.All(a => a.Variants.All(v => v.Url.Contains("live-"))), "live photo: preview video excluded");

    // URL 编码的 JSON：解码后解析
    var encoded = Uri.EscapeDataString(ReadFixture("douyin-detail-api.json"));
    var decodedData = DouyinPageParser.ParseStructuredData(encoded);
    AssertEqual(3, decodedData.Assets.Count, "url-encoded json image count");

    // 无关 JSON（不含作品数据）：空壳
    var unrelated = DouyinPageParser.ParseStructuredData("{\"config\":{\"env\":\"prod\"}}");
    AssertEqual(0, unrelated.Assets.Count, "unrelated json yields no assets");
}

// 场景：详情接口 URL 匹配规则
static void TestDouyinDetailEndpointMatcher()
{
    AssertEqual(true, DouyinDetailEndpointMatcher.IsDetailEndpoint("https://www.douyin.com/aweme/v1/web/aweme/detail/?aweme_id=7200000000000000001", "XHR"), "detail api xhr matched");
    AssertEqual(true, DouyinDetailEndpointMatcher.IsDetailEndpoint("https://www.douyin.com/aweme/v1/web/aweme/detail/?aweme_id=7200000000000000001", "Fetch"), "detail api fetch matched");
    AssertEqual(true, DouyinDetailEndpointMatcher.IsDetailEndpoint("https://www.douyin.com/aweme/v1/web/aweme/detail/?aweme_id=1", null), "detail path matched without type");
    AssertEqual(false, DouyinDetailEndpointMatcher.IsDetailEndpoint("https://www.douyin.com/aweme/v1/web/aweme/detail/?aweme_id=1", "Document"), "document type rejected");
    AssertEqual(false, DouyinDetailEndpointMatcher.IsDetailEndpoint("https://www.douyin.com/aweme/v1/web/aweme/post/?aweme_id=1", "XHR"), "post endpoint rejected");
    AssertEqual(false, DouyinDetailEndpointMatcher.IsDetailEndpoint("https://www.douyin.com/aweme/v1/web/user/profile/?sec_user_id=1", "XHR"), "profile endpoint rejected");
    AssertEqual(false, DouyinDetailEndpointMatcher.IsDetailEndpoint("https://evil.com/aweme/detail/?aweme_id=1", "XHR"), "foreign host rejected");
    AssertEqual(false, DouyinDetailEndpointMatcher.IsDetailEndpoint("https://www.douyin.com/aweme/v1/web/aweme/detail/?aweme_id=1", "Image"), "image type rejected");
}

// 场景：浏览器候选兜底（视频优先、DOM 图片顺序、小图/特征过滤、Resolver 端到端）
static void TestDouyinCandidateFallback()
{
    // 视频优先：视频作品页的 img（封面/头像/表情包）一律不收
    var videoPageCandidates = new BrowserCapturedCandidate[]
    {
        new(new Uri("https://media.example/avatar/1.webp"), MediaKind.Image, 0, 50, 50, null, null, BrowserCandidateSource.Dom),
        new(new Uri("https://media.example/emoji/x.webp"), MediaKind.Image, 1, 100, 100, null, null, BrowserCandidateSource.Dom),
        new(new Uri("https://media.example/cover/1.webp"), MediaKind.Image, 2, 1080, 1920, null, null, BrowserCandidateSource.Dom),
        new(new Uri("https://media.example/video/main.mp4"), MediaKind.Video, 0, null, null, null, null, BrowserCandidateSource.Dom),
        new(new Uri("https://media.example/video/sub.mp4"), MediaKind.Video, 1, null, null, null, null, BrowserCandidateSource.Dom),
    };
    var videoPageData = DouyinMediaNormalizer.NormalizeCandidatesToPageData(videoPageCandidates);
    AssertEqual(1, videoPageData.Assets.Count, "video page: only video asset");
    AssertEqual(MediaKind.Video, videoPageData.Assets[0].Kind, "video page: video kind");
    AssertEqual(2, videoPageData.Assets[0].Variants.Count, "video page: variants merged");
    AssertEqual(true, videoPageData.Assets[0].Variants.All(v => !v.Url.Contains("avatar") && !v.Url.Contains("emoji") && !v.Url.Contains("cover")), "video page: page images excluded");

    // 图文页：DOM 图片顺序保持、去重、小图（头像/表情包）过滤
    var imagePageCandidates = new BrowserCapturedCandidate[]
    {
        new(new Uri("https://media.example/avatar/1.webp"), MediaKind.Image, 0, 50, 50, null, null, BrowserCandidateSource.Dom),
        new(new Uri("https://media.example/img/1.webp"), MediaKind.Image, 1, 1080, 1440, null, null, BrowserCandidateSource.Dom),
        new(new Uri("https://media.example/img/2.webp"), MediaKind.Image, 2, 1080, 1440, null, null, BrowserCandidateSource.Dom),
        new(new Uri("https://media.example/img/2.webp"), MediaKind.Image, 3, 1080, 1440, null, null, BrowserCandidateSource.Dom),
        new(new Uri("https://media.example/sticker/x.webp"), MediaKind.Image, 4, 120, 120, null, null, BrowserCandidateSource.Dom),
        new(new Uri("https://media.example/logo/x.png"), MediaKind.Image, 5, 200, 200, null, null, BrowserCandidateSource.Dom),
    };
    var imagePageData = DouyinMediaNormalizer.NormalizeCandidatesToPageData(imagePageCandidates);
    AssertEqual(2, imagePageData.Assets.Count, "image page: two work images kept");
    AssertEqual(MediaKind.Image, imagePageData.Assets[0].Kind, "image page: image kind");
    AssertEqual("https://media.example/img/1.webp", imagePageData.Assets[0].Variants[0].Url, "image order preserved");
    AssertEqual("https://media.example/img/2.webp", imagePageData.Assets[1].Variants[0].Url, "second image preserved and deduped");

    // 尺寸未知的图片候选保留（交由 URL 特征过滤）
    var unknownSizeCandidates = new BrowserCapturedCandidate[]
    {
        new(new Uri("https://media.example/img/1.webp"), MediaKind.Image, 0, null, null, null, null, BrowserCandidateSource.Dom),
    };
    var unknownSizeData = DouyinMediaNormalizer.NormalizeCandidatesToPageData(unknownSizeCandidates);
    AssertEqual(1, unknownSizeData.Assets.Count, "unknown-size image kept");

    // 显式类型偏好：/note/（图文/实况）即使含视频候选也只取图片
    var noteCandidates = new BrowserCapturedCandidate[]
    {
        new(new Uri("https://media.example/img/1.webp"), MediaKind.Image, 0, 1080, 1440, null, null, BrowserCandidateSource.Dom),
        new(new Uri("https://media.example/img/2.webp"), MediaKind.Image, 1, 1080, 1440, null, null, BrowserCandidateSource.Dom),
        new(new Uri("https://media.example/live-preview.mp4"), MediaKind.Video, 0, null, null, null, null, BrowserCandidateSource.Dom),
    };
    var noteData = DouyinMediaNormalizer.NormalizeCandidatesToPageData(noteCandidates, videoPreferred: false);
    AssertEqual(2, noteData.Assets.Count, "note page: images only");
    AssertEqual(true, noteData.Assets.All(a => a.Kind == MediaKind.Image), "note page: preview video excluded");

    // 无偏好启发式：多张有效图 + 视频候选 → 图片优先（实况/图文形态）
    var heuristicImageData = DouyinMediaNormalizer.NormalizeCandidatesToPageData(noteCandidates);
    AssertEqual(2, heuristicImageData.Assets.Count, "heuristic: multi-image + video prefers images");

    // 无偏好启发式：单张有效图 + 视频候选 → 视频优先（视频页封面形态）
    var heuristicVideoCandidates = new BrowserCapturedCandidate[]
    {
        new(new Uri("https://media.example/cover/1.webp"), MediaKind.Image, 0, 1080, 1920, null, null, BrowserCandidateSource.Dom),
        new(new Uri("https://media.example/video/main.mp4"), MediaKind.Video, 0, null, null, null, null, BrowserCandidateSource.Dom),
    };
    var heuristicVideoData = DouyinMediaNormalizer.NormalizeCandidatesToPageData(heuristicVideoCandidates);
    AssertEqual(1, heuristicVideoData.Assets.Count, "heuristic: single image + video prefers video");
    AssertEqual(MediaKind.Video, heuristicVideoData.Assets[0].Kind, "heuristic: video kind");

    // 显式类型偏好：/video/ 即使有多张图也只取视频
    var videoPrefData = DouyinMediaNormalizer.NormalizeCandidatesToPageData(noteCandidates, videoPreferred: true);
    AssertEqual(1, videoPrefData.Assets.Count, "video url: video only");
    AssertEqual(MediaKind.Video, videoPrefData.Assets[0].Kind, "video url: kind");

    // Resolver 端到端：结构化数据缺失但 DOM 候选存在 → 解析成功
    var validator = new SafeUriValidator(new FakeDnsResolver(_ => Task.FromResult(new IPAddress[] { IPAddress.Parse("8.8.8.8") })));
    var proxy = new BrowserSessionAccessorProxy();
    var resolver = new DouyinMediaResolver(new DouyinHttpPageResolver(new SafeHttpRequestSender(new HttpClient(), validator)), proxy);
    proxy.Attach(new CapturingBrowserSession(null, new BrowserCapturedCandidate[]
    {
        new(new Uri("https://media.example/img/1.webp"), MediaKind.Image, 0, 1080, 1440, null, null, BrowserCandidateSource.Dom),
        new(new Uri("https://media.example/img/2.webp"), MediaKind.Image, 1, 1080, 1440, null, null, BrowserCandidateSource.Dom),
    }));
    var result = resolver.ResolveAsync(new MediaResolveRequest(new Uri("https://www.douyin.com/note/7200000000000000003"), MediaResolveMode.BrowserInteractive, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.Succeeded, result.Status, "candidate fallback resolves");
    AssertEqual(2, result.Post!.Assets.Count, "candidate fallback two images");
    AssertEqual(MediaKind.Image, result.Post.Assets[0].Kind, "candidate fallback image kind");
    AssertEqual("session-1", result.Post.Assets[0].Variants[0].RequestContext.BrowserSessionId, "candidate fallback session id");

    // Resolver 端到端：/video/ URL + 视频候选 → 只取视频
    proxy.Attach(new CapturingBrowserSession(null, new BrowserCapturedCandidate[]
    {
        new(new Uri("https://media.example/cover/1.webp"), MediaKind.Image, 0, 1080, 1920, null, null, BrowserCandidateSource.Dom),
        new(new Uri("https://media.example/video/main.mp4"), MediaKind.Video, 0, null, null, null, null, BrowserCandidateSource.Dom),
    }));
    var videoResult = resolver.ResolveAsync(new MediaResolveRequest(new Uri("https://www.douyin.com/video/7200000000000000005"), MediaResolveMode.BrowserInteractive, new MediaRequestContext(null, null)), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(MediaResolveStatus.Succeeded, videoResult.Status, "video url fallback resolves");
    AssertEqual(1, videoResult.Post!.Assets.Count, "video url: single video asset");
    AssertEqual(MediaKind.Video, videoResult.Post.Assets[0].Kind, "video url: video kind");
}

// 场景：魔数识别扩展名与终化修正（.img/.bin → 真实格式）
static void TestMagicExtensionDetection()
{
    // 魔数 → 扩展名识别
    using (var jpeg = new MemoryStream(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 }))
    {
        AssertEqual(".jpg", MediaContentValidator.DetectExtensionAsync(jpeg, MediaKind.Image, CancellationToken.None).GetAwaiter().GetResult(), "jpeg magic to jpg");
    }
    using (var png = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }))
    {
        AssertEqual(".png", MediaContentValidator.DetectExtensionAsync(png, MediaKind.Image, CancellationToken.None).GetAwaiter().GetResult(), "png magic to png");
    }
    using (var webp = new MemoryStream(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }))
    {
        AssertEqual(".webp", MediaContentValidator.DetectExtensionAsync(webp, MediaKind.Image, CancellationToken.None).GetAwaiter().GetResult(), "webp magic to webp");
    }
    using (var mp4 = new MemoryStream(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0, 0, 0, 0 }))
    {
        AssertEqual(".mp4", MediaContentValidator.DetectExtensionAsync(mp4, MediaKind.Video, CancellationToken.None).GetAwaiter().GetResult(), "mp4 magic to mp4");
    }
    using (var webm = new MemoryStream(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0, 0, 0, 0, 0 }))
    {
        AssertEqual(".webm", MediaContentValidator.DetectExtensionAsync(webm, MediaKind.Video, CancellationToken.None).GetAwaiter().GetResult(), "webm magic to webm");
    }
    using (var zip = new MemoryStream(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0, 0, 0, 0, 0 }))
    {
        AssertEqual(null, MediaContentValidator.DetectExtensionAsync(zip, MediaKind.Image, CancellationToken.None).GetAwaiter().GetResult(), "unknown magic returns null");
    }

    // 终化修正：.img → .jpg、.bin → .mp4；已有具体扩展名不重复修正
    var root = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var jpegBody = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var part1 = Path.Combine(root, "p1.part");
        File.WriteAllBytes(part1, jpegBody);
        var (saved1, err1) = MediaFileFinalizer.TryFinalizeAsync(part1, Path.Combine(root, "photo.img"), MediaKind.Image, CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(true, err1 is null && saved1 is not null, "img finalize succeeds");
        AssertEqual("photo.jpg", Path.GetFileName(saved1), "img corrected to jpg by magic");

        var mp4Body = new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x63, 0x31, 0, 0, 0, 0 };
        var part2 = Path.Combine(root, "p2.part");
        File.WriteAllBytes(part2, mp4Body);
        var (saved2, err2) = MediaFileFinalizer.TryFinalizeAsync(part2, Path.Combine(root, "video.bin"), MediaKind.Video, CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(true, err2 is null && saved2 is not null, "bin finalize succeeds");
        AssertEqual("video.mp4", Path.GetFileName(saved2), "bin corrected to mp4 by magic");

        var part3 = Path.Combine(root, "p3.part");
        File.WriteAllBytes(part3, jpegBody);
        var (saved3, err3) = MediaFileFinalizer.TryFinalizeAsync(part3, Path.Combine(root, "explicit.jpg"), MediaKind.Image, CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(true, err3 is null && saved3 is not null, "explicit extension finalize succeeds");
        AssertEqual("explicit.jpg", Path.GetFileName(saved3), "explicit extension unchanged");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

// 场景：批次完成事件（资源收尾信号）
static void TestBatchCompletedEvent()
{
    var root = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var state = MediaStateStore.Load(new AppPaths(root));
        using var coordinator = new MediaDownloadCoordinator(new FakeSucceedDownloadService(), state);
        var post = BuildValidPostForTest();
        var batch = BuildBatchForTest(post, 2, root);

        var completed = new List<Guid>();
        coordinator.BatchCompleted += (_, batchId) => completed.Add(batchId);

        coordinator.EnqueueBatchAsync(batch, CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(1, completed.Count, "batch completed fired once");
        AssertEqual(batch.Id, completed[0], "batch completed carries batch id");

        // 第二个批次再次触发
        var batch2 = BuildBatchForTest(post, 1, root);
        coordinator.EnqueueBatchAsync(batch2, CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(2, completed.Count, "second batch completed fired");
        AssertEqual(batch2.Id, completed[1], "second batch id");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

// 通用断言：相等则通过，否则抛出带用例名的异常
static void AssertEqual<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected={expected}, actual={actual}");

    TestCounter.Passed++;
}

// 通用断言：期望 action 抛出指定类型的异常
static void AssertThrows<TException>(Action action, string name)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        TestCounter.Passed++;
        return;
    }

    throw new InvalidOperationException($"{name} failed. Expected {typeof(TException).Name}.");
}

// 断言计数器（顶层语句中静态本地函数无法捕获局部变量，故用静态类承载）
static class TestCounter
{
    public static int Passed;
}

// 测试辅助：按旧版 state.json 的格式写入测试数据
file static class LegacyStateTestWriter
{
    public static void Write(string path, TextToolId tool)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new { Settings = new { HotKeyModifiers = (int)Keys.Alt, HotKeyKey = (int)Keys.Q, QuoteHistoryLimit = 100, SpaceHistoryLimit = 100, LastViewedTool = tool.ToString() }, History = Array.Empty<HistoryEntry>() };
        File.WriteAllText(path, JsonSerializer.Serialize(data));
    }
}

// 测试替身：记录调用并按配置返回剪贴板写入结果
file sealed class FakeClipboard : IClipboardService
{
    private readonly List<string> calls;
    private readonly bool succeeds;

    public FakeClipboard(List<string> calls, bool succeeds)
    {
        this.calls = calls;
        this.succeeds = succeeds;
    }

    public bool TrySetText(string text, out string error)
    {
        calls.Add("clipboard");
        error = "clipboard failed";
        return succeeds;
    }
}

// 测试替身：记录调用并按配置返回窗口恢复/粘贴结果
file sealed class FakeWindowInput : IWindowInputService
{
    private readonly List<string> calls;
    private readonly bool restoreSucceeds;
    private readonly bool pasteSucceeds;

    public FakeWindowInput(List<string> calls, bool restoreSucceeds, bool pasteSucceeds)
    {
        this.calls = calls;
        this.restoreSucceeds = restoreSucceeds;
        this.pasteSucceeds = pasteSucceeds;
    }

    public bool TryRestoreWindow(nint handle, out string error)
    {
        calls.Add("restore");
        error = "restore failed";
        return restoreSucceeds;
    }

    public bool TrySendPaste(out string error)
    {
        calls.Add("paste");
        error = "paste failed";
        return pasteSucceeds;
    }
}

// 测试替身：可控的解析器
file sealed class FakeResolver : IMediaResolver
{
    private readonly Func<Uri, bool>? canResolve;
    private readonly Func<MediaResolveRequest, CancellationToken, MediaResolveResult>? resultFactory;

    public FakeResolver(MediaProviderId provider, Func<Uri, bool>? canResolve = null, Func<MediaResolveRequest, CancellationToken, MediaResolveResult>? resultFactory = null)
    {
        Provider = provider;
        this.canResolve = canResolve;
        this.resultFactory = resultFactory;
    }

    public MediaProviderId Provider { get; }
    public bool CanResolve(Uri sourceUri) => canResolve?.Invoke(sourceUri) ?? true;

    public Task<MediaResolveResult> ResolveAsync(MediaResolveRequest request, CancellationToken cancellationToken)
    {
        if (resultFactory is not null)
        {
            return Task.FromResult(resultFactory(request, cancellationToken));
        }

        var context = new MediaRequestContext(request.SourceUri, null);
        var variant = new MediaVariant(new Uri("https://cdn.example.com/v.mp4"), 1920, 1080, 30, 2000, 1000, "video/mp4", "h264", MediaVariantSource.StructuredData, context);
        var asset = new MediaAsset(0, MediaKind.Video, new MediaVariant[] { variant });
        var post = new ResolvedMediaPost(MediaProviderId.Douyin, request.SourceUri, "1", "t", "a", new MediaAsset[] { asset });
        return Task.FromResult(MediaResolveResult.Success(post));
    }
}

// 测试替身：按队列返回响应的 HTTP handler（最后一个响应工厂复用）
file sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage>[] responses;
    private int index;

    public StubHttpHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        this.responses = responses;
    }

    public List<HttpRequestMessage> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var factory = responses[Math.Min(index++, responses.Length - 1)];
        return Task.FromResult(factory(request));
    }
}

// 测试替身：发送时抛固定异常的 HTTP handler（模拟 TLS 握手被拒等传输失败）
file sealed class ThrowingHttpHandler : HttpMessageHandler
{
    public ThrowingHttpHandler(Exception exception)
    {
        Exception = exception;
    }

    public Exception Exception { get; }

    public List<HttpRequestMessage> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromException<HttpResponseMessage>(Exception);
    }
}

// 测试替身：始终抛异常的解析器
file sealed class ThrowingResolver : IMediaResolver
{
    private readonly Exception exception;

    public ThrowingResolver(Exception exception)
    {
        this.exception = exception;
    }

    public MediaProviderId Provider => MediaProviderId.Douyin;
    public bool CanResolve(Uri sourceUri) => true;

    public Task<MediaResolveResult> ResolveAsync(MediaResolveRequest request, CancellationToken cancellationToken)
        => Task.FromException<MediaResolveResult>(exception);
}

// 测试替身：可编程的 DNS 解析结果（禁止查询真实 DNS）
file sealed class FakeDnsResolver : IDnsResolver
{
    private readonly Func<string, Task<IPAddress[]>> resolver;

    public FakeDnsResolver(Func<string, Task<IPAddress[]>> resolver)
    {
        this.resolver = resolver;
    }

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => resolver(host);
}

// 测试替身：不可 Seek 的流
file sealed class NonSeekableStream : Stream
{
    private readonly byte[] data;
    private int position;

    public NonSeekableStream(byte[] data) => this.data = data;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var available = Math.Min(count, data.Length - position);
        Array.Copy(data, position, buffer, offset, available);
        position += available;
        return available;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

// 测试替身：可编程的浏览器会话（记录 Cookie 请求）
file sealed class FakeBrowserSession : IBrowserSessionAccessor
{
    private readonly IReadOnlyList<BrowserCookie> cookies;

    public FakeBrowserSession(params BrowserCookie[] cookies)
    {
        this.cookies = cookies;
    }

    public List<(string SessionId, Uri Uri)> CookieRequests { get; } = new();

    public bool IsAvailable => true;

    public Task<BrowserCaptureResult> CaptureAsync(Uri pageUri, bool interactive, CancellationToken cancellationToken)
        => throw new NotSupportedException("测试替身不支持捕获。");

    public Task<BrowserDownloadResult> DownloadAsync(
        Uri mediaUri,
        Guid taskId,
        string targetPath,
        MediaKind kind,
        CancellationToken cancellationToken,
        IProgress<MediaDownloadProgress>? progress = null,
        long? maxBytes = null)
        => throw new NotSupportedException("测试替身不支持下载。");

    public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(string browserSessionId, Uri requestUri, CancellationToken cancellationToken)
    {
        CookieRequests.Add((browserSessionId, requestUri));
        return Task.FromResult(cookies);
    }
    public Task PrefetchImagesAsync(IReadOnlyList<Uri> imageUris, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public ValueTask CloseBrowserAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

// 测试替身：读取时抛 IOException 的流内容
file sealed class AbortedStreamContent : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        => throw new IOException("模拟流中断");

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

// 测试替身：每次读取检查取消，取消时抛 OperationCanceledException 的流内容
file sealed class SlowStreamContent : HttpContent
{
    private readonly CancellationToken token;

    public SlowStreamContent(CancellationToken token)
    {
        this.token = token;
    }

    protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
    {
        var buffer = new byte[16];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await stream.WriteAsync(buffer, token);
            await Task.Delay(50, token);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

// 测试替身：可门控的下载服务（控制并发观测）
file sealed class GateDownloadService : IMediaDownloadService
{
    public List<MediaDownloadResult> Completed { get; } = new();
    private readonly object sync = new();
    private int active;
    private readonly List<TaskCompletionSource<bool>> gates = new();

    public int MaxConcurrent { get; private set; }
    public int ActiveCount
    {
        get { lock (sync) { return active; } }
    }

    public async Task<MediaDownloadResult> DownloadAsync(MediaDownloadTask task, CancellationToken cancellationToken, IProgress<MediaDownloadProgress>? progress = null)
    {
        TaskCompletionSource<bool> gate;
        lock (sync)
        {
            active++;
            MaxConcurrent = Math.Max(MaxConcurrent, active);
            gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            gates.Add(gate);
        }

        MediaDownloadResult? result = null;
        try
        {
            using var registration = cancellationToken.Register(() => gate.TrySetResult(false));
            await gate.Task.ConfigureAwait(false);
            result = cancellationToken.IsCancellationRequested
                ? MediaDownloadResult.Cancelled(task.Id)
                : MediaDownloadResult.Success(task.Id, task.TargetPath);
            return result;
        }
        finally
        {
            lock (sync)
            {
                active--;
                if (result is not null)
                {
                    Completed.Add(result);
                }
            }
        }
    }
}

// 测试替身：立即成功
file sealed class FakeSucceedDownloadService : IMediaDownloadService
{
    public Task<MediaDownloadResult> DownloadAsync(MediaDownloadTask task, CancellationToken cancellationToken, IProgress<MediaDownloadProgress>? progress = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(MediaDownloadResult.Cancelled(task.Id));
        }
        File.WriteAllBytes(task.TargetPath, new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70 });
        return Task.FromResult(MediaDownloadResult.Success(task.Id, task.TargetPath));
    }
}

// 测试替身：立即失败
file sealed class FakeFailDownloadService : IMediaDownloadService
{
    public Task<MediaDownloadResult> DownloadAsync(MediaDownloadTask task, CancellationToken cancellationToken, IProgress<MediaDownloadProgress>? progress = null)
        => Task.FromResult(MediaDownloadResult.Failed(task.Id, "模拟失败"));
}

// 测试替身：返回固定结构化数据的浏览器会话
file sealed class CapturingBrowserSession : IBrowserSessionAccessor
{
    private readonly string? structuredDataJson;
    private readonly IReadOnlyList<BrowserCapturedCandidate> candidates;

    public CapturingBrowserSession(string? structuredDataJson, IReadOnlyList<BrowserCapturedCandidate>? candidates = null)
    {
        this.structuredDataJson = structuredDataJson;
        this.candidates = candidates ?? Array.Empty<BrowserCapturedCandidate>();
    }

    public bool IsAvailable => true;

    public Task<BrowserCaptureResult> CaptureAsync(Uri pageUri, bool interactive, CancellationToken cancellationToken)
        => Task.FromResult(new BrowserCaptureResult(
            "session-1",
            structuredDataJson,
            null,
            candidates,
            BrowserCaptureStatus.Succeeded,
            null));

    public Task<BrowserDownloadResult> DownloadAsync(
        Uri mediaUri,
        Guid taskId,
        string targetPath,
        MediaKind kind,
        CancellationToken cancellationToken,
        IProgress<MediaDownloadProgress>? progress = null,
        long? maxBytes = null)
        => throw new NotSupportedException("测试替身不支持下载。");

    public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(string browserSessionId, Uri requestUri, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<BrowserCookie>>(Array.Empty<BrowserCookie>());
    public Task PrefetchImagesAsync(IReadOnlyList<Uri> imageUris, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public ValueTask CloseBrowserAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

// 测试替身：尚未初始化（IsAvailable=false）但捕获可用的浏览器会话；
// 用于回归验证解析器不再用 IsAvailable 前置拦截首次初始化
file sealed class LazyBrowserSession : IBrowserSessionAccessor
{
    private readonly string structuredDataJson;

    public LazyBrowserSession(string structuredDataJson)
    {
        this.structuredDataJson = structuredDataJson;
    }

    public bool IsAvailable => false;

    public Task<BrowserCaptureResult> CaptureAsync(Uri pageUri, bool interactive, CancellationToken cancellationToken)
        => Task.FromResult(new BrowserCaptureResult(
            "session-lazy",
            structuredDataJson,
            null,
            Array.Empty<BrowserCapturedCandidate>(),
            BrowserCaptureStatus.Succeeded,
            null));

    public Task<BrowserDownloadResult> DownloadAsync(
        Uri mediaUri,
        Guid taskId,
        string targetPath,
        MediaKind kind,
        CancellationToken cancellationToken,
        IProgress<MediaDownloadProgress>? progress = null,
        long? maxBytes = null)
        => throw new NotSupportedException("测试替身不支持下载。");

    public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(string browserSessionId, Uri requestUri, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<BrowserCookie>>(Array.Empty<BrowserCookie>());
    public Task PrefetchImagesAsync(IReadOnlyList<Uri> imageUris, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public ValueTask CloseBrowserAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

// 测试替身：浏览器下载成功的会话（写入一个合法媒体文件并返回）
file sealed class BrowserDownloadSession : IBrowserSessionAccessor
{
    public bool IsAvailable => true;

    public List<Guid> DownloadRequests { get; } = new();

    public Task<BrowserCaptureResult> CaptureAsync(Uri pageUri, bool interactive, CancellationToken cancellationToken)
        => Task.FromResult(new BrowserCaptureResult(
            null, null, null, Array.Empty<BrowserCapturedCandidate>(),
            BrowserCaptureStatus.Unavailable, "测试替身不支持捕获。"));

    public async Task<BrowserDownloadResult> DownloadAsync(
        Uri mediaUri,
        Guid taskId,
        string targetPath,
        MediaKind kind,
        CancellationToken cancellationToken,
        IProgress<MediaDownloadProgress>? progress = null,
        long? maxBytes = null)
    {
        DownloadRequests.Add(taskId);
        if (cancellationToken.IsCancellationRequested)
        {
            return BrowserDownloadResult.CancelledResult();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var partPath = targetPath + $".part.{taskId:N}";
        // mp4 魔数开头，保证终化校验通过
        await File.WriteAllBytesAsync(partPath, new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70 }, cancellationToken);
        var (savedPath, error) = await MediaFileFinalizer.TryFinalizeAsync(partPath, targetPath, kind, cancellationToken);
        return savedPath is not null
            ? BrowserDownloadResult.Succeeded(savedPath)
            : BrowserDownloadResult.Failed(error ?? "测试下载失败。");
    }

    public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(string browserSessionId, Uri requestUri, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<BrowserCookie>>(Array.Empty<BrowserCookie>());
    public Task PrefetchImagesAsync(IReadOnlyList<Uri> imageUris, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public ValueTask CloseBrowserAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
// 测试替身：进程内模拟 CDP 服务器的 HttpListener WebSocket 端点
file sealed class FakeCdpServer : IDisposable
{
    private readonly HttpListener listener = new();
    private Task? serverTask;
    private volatile bool running = true;

    public string Start()
    {
        var portListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        portListener.Start();
        var port = ((System.Net.IPEndPoint)portListener.LocalEndpoint).Port;
        portListener.Stop();

        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        serverTask = Task.Run(RunAsync);
        return $"ws://127.0.0.1:{port}/devtools";
    }

    private async Task RunAsync()
    {
        while (running)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            var wsContext = await context.AcceptWebSocketAsync(null);
            _ = Task.Run(() => ServeAsync(wsContext.WebSocket));
        }
    }

    private async Task ServeAsync(System.Net.WebSockets.WebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                System.Net.WebSockets.WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                // 客户端发起关闭：回 close 帧（否则客户端 CloseAsync 会一直等待）
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    await ws.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    return;
                }

                var node = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(ms.ToArray()))!;
                var id = node["id"]?.GetValue<int>() ?? 0;
                var method = node["method"]?.GetValue<string>() ?? string.Empty;
                var sessionId = node["sessionId"]?.GetValue<string>();

                switch (method)
                {
                    case "Test.error":
                        await SendAsync(ws, new JsonObject
                        {
                            ["id"] = id,
                            ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "boom" },
                        });
                        break;

                    case "Test.event":
                        await SendAsync(ws, new JsonObject
                        {
                            ["method"] = "Test.emitted",
                            ["params"] = new JsonObject { ["value"] = 42 },
                            ["sessionId"] = "s1",
                        });
                        await SendAsync(ws, new JsonObject { ["id"] = id, ["result"] = new JsonObject() });
                        break;

                    case "Test.silent":
                        // 不响应：客户端应超时
                        break;

                    default:
                        await SendAsync(ws, new JsonObject
                        {
                            ["id"] = id,
                            ["result"] = new JsonObject
                            {
                                ["ok"] = true,
                                ["echo"] = method,
                                ["session"] = sessionId,
                            },
                        });
                        break;
                }
            }
        }
        catch
        {
            // 客户端断开等场景忽略
        }
    }

    private static Task SendAsync(System.Net.WebSockets.WebSocket ws, JsonObject message)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(message.ToJsonString());
        return ws.SendAsync(bytes, System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public void Dispose()
    {
        running = false;
        try { listener.Stop(); } catch { }
        try { listener.Close(); } catch { }
        try { serverTask?.Wait(1000); } catch { }
    }
}

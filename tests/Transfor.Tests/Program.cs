using Transfor;
using System.Text.Json;
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

Console.WriteLine($"All {TestCounter.Passed} tests passed.");

// 场景：迁移中断后恢复 —— 旧状态可读时重试迁移；新版状态完整时清除迁移标记
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

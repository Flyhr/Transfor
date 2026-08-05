using Transfor;
using System.Text.Json;
using System.Windows.Forms;

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

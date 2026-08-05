using Transfor;
using System.Windows.Forms;

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

var spaceCases = new (string Name, string? Input, string Expected)[]
{
    ("null input returns empty string", null, string.Empty),
    ("empty input returns empty string", string.Empty, string.Empty),
    ("half-width spaces are removed", "\u4F60 \u597D", "\u4F60\u597D"),
    ("full-width spaces are removed", "\u4F60\u3000\u597D", "\u4F60\u597D"),
    ("line breaks and tabs are preserved", "a b\r\nc\td", "ab\r\nc\td"),
    ("text without removable spaces is unchanged", "abc\r\nc\td", "abc\r\nc\td"),
};

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

TestAppPaths();
TestTextToolsPageContract();
TestTextToolDefinition();
TestHotKeyBinding();
TestHistoryStore();
TestPasteCoordinator();

Console.WriteLine($"All {quoteCases.Length + spaceCases.Length + 20} tests passed.");

static void TestAppPaths()
{
    var root = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    var paths = new AppPaths(root);
    AssertEqual(Path.GetFullPath(root), paths.ApplicationDirectory, "application directory");
    AssertEqual("state.json", Path.GetFileName(paths.LegacyStateFile), "legacy state file name");
}
static void TestTextToolsPageContract()
{
    var path = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"), "state.json");
    RunSta(() =>
    {
        using var page = new TextToolsPage(LegacyHistoryStore.Load(path));
        AssertEqual("text-tools", page.Id, "text page id");
        AssertEqual("文本转换", page.DisplayName, "text page display name");
        AssertEqual(true, page.View is UserControl, "text page view");
    });
}

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
static void TestTextToolDefinition()
{
    var definition = new TextToolDefinition(TextToolId.QuoteConversion, "引号转换", QuoteConverter.Convert);

    AssertEqual(TextToolId.QuoteConversion, definition.Id, "text tool id");
    AssertEqual("引号转换", definition.DisplayName, "text tool display name");
    AssertEqual("'x'", definition.Convert("\"x\""), "text tool converter");
}
static void TestHotKeyBinding()
{
    var defaultBinding = HotKeyBinding.Default;
    AssertEqual("Alt+Q", defaultBinding.DisplayText, "default hotkey display");
    AssertEqual(true, defaultBinding.Modifiers.HasFlag(Keys.Alt), "default hotkey modifier");
    AssertEqual(Keys.Q, defaultBinding.Key, "default hotkey key");

    foreach (var modifiers in new[] { Keys.Control, Keys.Alt, Keys.Shift, Keys.LWin })
    {
        var binding = HotKeyBinding.Create(modifiers, Keys.F5);
        AssertEqual(Keys.F5, binding.Key, "valid hotkey key");
    }

    AssertThrows<ArgumentException>(() => HotKeyBinding.Create(Keys.None, Keys.Q), "hotkey requires modifier");
    AssertThrows<ArgumentException>(() => HotKeyBinding.Create(Keys.Alt, Keys.None), "hotkey requires key");
    AssertThrows<ArgumentException>(() => HotKeyBinding.Create(Keys.Alt, Keys.Control), "hotkey rejects modifier key");    AssertThrows<ArgumentException>(() => HotKeyBinding.Create(Keys.Alt, Keys.ControlKey), "hotkey rejects modifier key code");
}

static void TestHistoryStore()
{
    var root = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var path = Path.Combine(root, "state.json");

    try
    {
        var store = LegacyHistoryStore.Load(path);
        AssertEqual(HotKeyBinding.Default, store.Settings.HistoryHotKey, "default hotkey");
        AssertEqual(100, store.Settings.QuoteHistoryLimit, "default quote history limit");
        AssertEqual(100, store.Settings.SpaceHistoryLimit, "default space history limit");
        AssertEqual(TextToolId.QuoteConversion, store.Settings.LastViewedTool, "default last viewed tool");

        var original = "\"a\"\r\n b";
        var converted = "'a'\r\nb";
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        store.Add(new HistoryEntry(TextToolId.QuoteConversion, original, converted, createdAt));
        store.Add(new HistoryEntry(TextToolId.SpaceRemoval, "x y", "xy", createdAt.AddSeconds(1)));
        store.SetLastViewedTool(TextToolId.SpaceRemoval);
        store.UpdateSettings(store.Settings with { QuoteHistoryLimit = 1, SpaceHistoryLimit = 2 });
        store.Save();

        var reloaded = LegacyHistoryStore.Load(path);
        var quote = reloaded.GetHistory(TextToolId.QuoteConversion);
        var space = reloaded.GetHistory(TextToolId.SpaceRemoval);
        AssertEqual(1, quote.Count, "quote history count");
        AssertEqual(1, space.Count, "space history count");
        AssertEqual(original, quote[0].OriginalInput, "history original input");
        AssertEqual(converted, quote[0].ConvertedOutput, "history converted output");
        AssertEqual(createdAt, quote[0].CreatedAtUtc, "history timestamp");
        AssertEqual(TextToolId.SpaceRemoval, reloaded.Settings.LastViewedTool, "last viewed persistence");

        reloaded.UpdateSettings(reloaded.Settings with { QuoteHistoryLimit = 1, SpaceHistoryLimit = 1 });
        reloaded.Add(new HistoryEntry(TextToolId.QuoteConversion, "second", "second", createdAt.AddSeconds(2)));
        reloaded.Add(new HistoryEntry(TextToolId.QuoteConversion, "third", "third", createdAt.AddSeconds(3)));
        reloaded.Add(new HistoryEntry(TextToolId.SpaceRemoval, "second", "second", createdAt.AddSeconds(4)));
        AssertEqual("third", reloaded.GetHistory(TextToolId.QuoteConversion)[0].OriginalInput, "quote limit trims oldest");
        AssertEqual("second", reloaded.GetHistory(TextToolId.SpaceRemoval)[0].OriginalInput, "space history independent");

        reloaded.ClearHistory(TextToolId.QuoteConversion);
        AssertEqual(0, reloaded.GetHistory(TextToolId.QuoteConversion).Count, "clear quote history");
        AssertEqual(1, reloaded.GetHistory(TextToolId.SpaceRemoval).Count, "clear keeps space history");

        AssertThrows<ArgumentException>(() => reloaded.UpdateSettings(reloaded.Settings with { QuoteHistoryLimit = 0 }), "history limit lower bound");
        AssertThrows<ArgumentException>(() => reloaded.UpdateSettings(reloaded.Settings with { SpaceHistoryLimit = 501 }), "history limit upper bound");

        File.WriteAllText(path, "not json");
        var corrupt = LegacyHistoryStore.Load(path);
        AssertEqual(AppSettings.Default, corrupt.Settings, "corrupt state fallback");
        AssertEqual(0, corrupt.GetHistory(TextToolId.QuoteConversion).Count, "corrupt history fallback");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}


static void TestPasteCoordinator()
{
    var entry = new HistoryEntry(TextToolId.QuoteConversion, "input", "result", DateTimeOffset.UtcNow);

    var successCalls = new List<string>();
    var success = new PasteCoordinator(
        new FakeClipboard(successCalls, succeeds: true),
        new FakeWindowInput(successCalls, restoreSucceeds: true, pasteSucceeds: true));
    var successResult = success.TryPaste(entry, new nint(42));
    AssertEqual(true, successResult.Succeeded, "paste success");
    AssertEqual("clipboard,restore,paste", string.Join(",", successCalls), "paste operation order");

    var clipboardFailureCalls = new List<string>();
    var clipboardFailure = new PasteCoordinator(
        new FakeClipboard(clipboardFailureCalls, succeeds: false),
        new FakeWindowInput(clipboardFailureCalls, restoreSucceeds: true, pasteSucceeds: true));
    var clipboardFailureResult = clipboardFailure.TryPaste(entry, new nint(42));
    AssertEqual(false, clipboardFailureResult.Succeeded, "clipboard failure result");
    AssertEqual("clipboard", string.Join(",", clipboardFailureCalls), "clipboard failure stops paste");

    var windowFailureCalls = new List<string>();
    var windowFailure = new PasteCoordinator(
        new FakeClipboard(windowFailureCalls, succeeds: true),
        new FakeWindowInput(windowFailureCalls, restoreSucceeds: false, pasteSucceeds: true));
    var windowFailureResult = windowFailure.TryPaste(entry, new nint(42));
    AssertEqual(false, windowFailureResult.Succeeded, "window restore failure result");
    AssertEqual("clipboard,restore", string.Join(",", windowFailureCalls), "window failure stops paste");

    var pasteFailureCalls = new List<string>();
    var pasteFailure = new PasteCoordinator(
        new FakeClipboard(pasteFailureCalls, succeeds: true),
        new FakeWindowInput(pasteFailureCalls, restoreSucceeds: true, pasteSucceeds: false));
    var pasteFailureResult = pasteFailure.TryPaste(entry, new nint(42));
    AssertEqual(false, pasteFailureResult.Succeeded, "send input failure result");
    AssertEqual("clipboard,restore,paste", string.Join(",", pasteFailureCalls), "send input failure order");

    var noWindowCalls = new List<string>();
    var noWindow = new PasteCoordinator(
        new FakeClipboard(noWindowCalls, succeeds: true),
        new FakeWindowInput(noWindowCalls, restoreSucceeds: true, pasteSucceeds: true));
    var noWindowResult = noWindow.TryPaste(entry, nint.Zero);
    AssertEqual(false, noWindowResult.Succeeded, "missing target window result");
    AssertEqual(string.Empty, string.Join(",", noWindowCalls), "missing target window stops all operations");
}static void AssertEqual<T>(T expected, T actual, string name)
{
    if (EqualityComparer<T>.Default.Equals(expected, actual))
    {
        return;
    }

    throw new InvalidOperationException($"{name} failed. Expected: {expected}; Actual: {actual}");
}

static void AssertThrows<TException>(Action action, string name)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"{name} failed. Expected {typeof(TException).Name}.");
}


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

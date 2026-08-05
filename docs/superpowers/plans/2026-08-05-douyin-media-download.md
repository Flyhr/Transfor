# 抖音媒体下载功能实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use `subagent-driven-development`（推荐）或 `executing-plans` 按任务逐项实施。步骤用 `- [ ]` 复选框跟踪。

**Goal:** 在现有 .NET 10 WinForms 应用中新增独立的「媒体下载」功能页，支持粘贴抖音分享文本、解析单视频/多图作品、选择与流式下载当前会话可访问的最高质量媒体、队列进度与取消重试，并保持文本转换功能完全隔离。

**Architecture:** 保持单项目，按「通用导航 → 通用模型 → 通用下载器 → 媒体页面 → 抖音静态解析 → WebView2 兜底」顺序推进。`MainForm` 先泛化为页面外壳；媒体功能全部收在 `Features/MediaDownload`（契约/模型/应用/服务/解析器/UI）+ `Infrastructure/Networking` + `Platform/Windows/WebView`，不触碰 `TextToolId`、`HistoryEntry`、`ITextHistoryRepository`、`PasteCoordinator`、`TextStateStore`。持久化走独立的 `media-settings.json` 与 `download-history.json`。

**Tech Stack:** .NET 10、WinForms、`System.Text.Json`、`System.Net.Http`（自定义 `HttpMessageHandler` 测试）、无框架控制台测试项目、任务 13 引入 `Microsoft.Web.WebView2`。

## Global Constraints（摘自设计规格，逐条遵守）

- 保持单一 `src/Transfor` WinForms 项目与单一无依赖 `tests/Transfor.Tests` 控制台测试项目；不拆分新类库。
- 媒体功能不得混入 `TextToolId`、`HistoryEntry`、`ITextHistoryRepository`、`PasteCoordinator`、`TextStateStore`；媒体下载不参与全局快捷键历史面板、不执行剪贴板粘贴。
- 不绕过登录/验证码/访问控制；不破解或伪造平台私有签名；不使用硬编码 Cookie；不下载私密/无权访问内容；不实现水印移除。
- Cookie、Token、临时授权信息不写入普通 JSON、日志或仓库；WebView2 数据（Cookie/权限/缓存）只存于独立用户数据目录 `%LOCALAPPDATA%\Transfor\WebView2\Douyin`。
- 自动化测试禁止访问真实抖音服务；测试一律使用本地 fixture 样本与自定义 `HttpMessageHandler`。
- `MediaDownloadPage` 不得引用 `DouyinMediaResolver` 或任何解析器；`DouyinMediaResolver` 不负责文件下载；`MediaDownloadService` 不含抖音页面规则。
- 禁止用正则表达式作为完整 HTML 解析的唯一方案；禁止 `.Result`/`.Wait()`/`Task.Run(() => WebView2...)`；除 WinForms 事件处理器外不使用 `async void`。
- 所有网络操作支持 `CancellationToken`；下载写 `.part` 临时文件，成功后原子重命名，失败/取消删除 `.part`；单文件最大 4 GiB；`HttpCompletionOption.ResponseHeadersRead` 流式下载，不把整个文件载入内存。
- 状态存储对象的修改方法内部完成持久化；UI 调用修改方法后不再调用全量 `Save()`。
- 完成标准：`dotnet build Transfor.slnx` 与 `dotnet run --project tests/Transfor.Tests` 均成功；原 34 个测试继续通过；无真实网络依赖测试；无 Cookie/Token 进入仓库；WebView2 只在 UI STA 线程创建；退出时所有资源正确释放。
- 「最高质量」= 当前公开页面或当前用户浏览器会话能够访问到的最高质量、可直接下载的媒体版本；不承诺原始文件、无水印、可访问私密/删除/付费/受限作品。
- 第一版不合并 DASH 分离音视频、HLS 分片，不引入 FFmpeg；发现分段媒体流时显示「已发现更高质量的分段媒体流，但当前版本暂不支持合并」。

---

### Task 1: MainForm 泛化为通用外壳并修正重复保存

**Files:**
- Modify: `src/Transfor/Shell/MainForm.cs`
- Modify: `src/Transfor/App/TransforApplicationContext.cs`
- Modify: `src/Transfor/Features/TextTools/UI/TextToolsPage.cs`
- Modify: `src/Transfor/Features/History/UI/HistoryPanelForm.cs`
- Modify: `src/Transfor/Features/Settings/UI/SettingsForm.cs`
- Test: `tests/Transfor.Tests/Program.cs`

**Interfaces:**
- Consumes: 现有 `IFeaturePage`、`TextToolsPage(TextStateStore)`、`TextStateStore` 各修改方法。
- Produces: `internal MainForm(IReadOnlyList<IFeaturePage> pages)`；约定「状态存储修改方法即持久化，UI 不再调用全量 `Save()`」。

- [ ] **Step 1: 写失败测试（持久化语义）**

在 `tests/Transfor.Tests/Program.cs` 追加（并注册调用、把结尾计数基数 `+ 20` 改为 `+ 21`）：

```csharp
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
```

- [ ] **Step 2: 运行验证测试现状**

Run: `dotnet run --project tests/Transfor.Tests`
Expected: 该新测试通过（`Add`/`SetLastViewedTool`/`UpdateSettings` 内部已落盘），其价值在 Step 3 删除冗余 `Save()` 后守住语义。

- [ ] **Step 3: MainForm 遍历页面集合创建导航**

`MainForm.cs` 构造函数改为：

```csharp
internal MainForm(IReadOnlyList<IFeaturePage> pages)
{
    Text = "文本转换器"; StartPosition = FormStartPosition.CenterScreen; MinimumSize = new Size(900, 600); Size = new Size(980, 660); Font = new Font("Microsoft YaHei UI", 10F);
    var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 2 };
    root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
    foreach (var page in pages)
    {
        var button = new Button { AutoSize = true, Text = page.DisplayName };
        button.Click += (_, _) => ShowPage(page);
        navigation.Controls.Add(button);
    }
    contentPanel = new Panel { Dock = DockStyle.Fill };
    root.Controls.Add(navigation, 0, 0); root.Controls.Add(contentPanel, 0, 1);
    Controls.Add(root); FormClosing += MainForm_FormClosing;
    if (pages.Count == 0) throw new ArgumentException("主窗口至少需要一个功能页。", nameof(pages));
    ShowPage(pages[0]);
}
```

删除字段 `private readonly TextToolsPage textToolsPage;`。

`TransforApplicationContext` 中改为：

```csharp
var pages = new IFeaturePage[] { new TextToolsPage(services.State) };
mainForm = new MainForm(pages);
```

- [ ] **Step 4: 删除冗余的 `Save()` 调用**

`TextToolsPage.CopyButton_Click`：删除 `Add` 之后的 `try { historyStore.Save(); } catch (IOException ...)` 整块。

`HistoryPanelForm.SelectTool`：

```csharp
try
{
    historyStore.SetLastViewedTool(tool);
}
catch (IOException ex)
{
    errorLabel.Text = $"保存当前分类失败：{ex.Message}";
}
```

`SettingsForm.SaveButton_Click`：

```csharp
try
{
    historyStore.UpdateSettings(nextSettings);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    hotKeyManager.TryReplace(oldSettings.HistoryHotKey, out _);
    errorLabel.Text = $"设置保存失败：{ex.Message}";
    return;
}
```

`SettingsForm.ClearHistory`：

```csharp
try
{
    historyStore.ClearHistory(tool);
    errorLabel.Text = string.Empty;
}
catch (IOException ex)
{
    errorLabel.Text = $"历史清空后保存失败：{ex.Message}";
}
```

`TransforApplicationContext.RegisterSavedHotKey`：

```csharp
try
{
    historyStore.UpdateSettings(historyStore.Settings with { HistoryHotKey = HotKeyBinding.Default });
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    startupHotKeyError += $"默认快捷键状态保存失败：{ex.Message}";
}
```

- [ ] **Step 5: 运行全套测试与构建**

Run: `dotnet run --project tests/Transfor.Tests`
Expected: `All 35 tests passed.`（8 + 6 + 21）

Run: `dotnet build Transfor.slnx`
Expected: 0 警告 0 错误

- [ ] **Step 6: 提交**

```powershell
git add src/Transfor tests/Transfor.Tests/Program.cs
git commit -m "refactor: generalize MainForm shell and persist on mutation"
```

### Task 2: 媒体通用数据模型

**Files:**
- Create: `src/Transfor/Features/MediaDownload/Models/MediaProviderId.cs`
- Create: `src/Transfor/Features/MediaDownload/Models/MediaKind.cs`
- Create: `src/Transfor/Features/MediaDownload/Models/MediaRequestContext.cs`
- Create: `src/Transfor/Features/MediaDownload/Models/MediaVariantSource.cs`
- Create: `src/Transfor/Features/MediaDownload/Models/MediaVariant.cs`
- Create: `src/Transfor/Features/MediaDownload/Models/MediaAsset.cs`
- Create: `src/Transfor/Features/MediaDownload/Models/ResolvedMediaPost.cs`
- Create: `src/Transfor/Features/MediaDownload/Models/MediaResolveResult.cs`
- Create: `src/Transfor/Features/MediaDownload/Models/MediaQualityPreference.cs`
- Create: `src/Transfor/Features/MediaDownload/Models/MediaDownloadSettings.cs`
- Create: `src/Transfor/Features/MediaDownload/Models/MediaDownloadTask.cs`
- Create: `src/Transfor/Features/MediaDownload/Models/MediaDownloadProgress.cs`
- Create: `src/Transfor/Features/MediaDownload/Models/MediaDownloadStatus.cs`
- Create: `src/Transfor/Features/MediaDownload/Models/MediaDownloadResult.cs`
- Create: `src/Transfor/Features/MediaDownload/Models/MediaDownloadHistoryEntry.cs`
- Test: `tests/Transfor.Tests/Program.cs`

**Interfaces:**
- Consumes: 无。
- Produces（后续任务依赖的精确签名）：

```csharp
internal enum MediaProviderId { Douyin, Direct }
internal enum MediaKind { Image, Video }
internal enum MediaVariantSource { StructuredData, InlineState, JsonLd, Dom, NetworkCapture, Thumbnail }
internal enum MediaQualityPreference { Highest, Balanced }
internal enum MediaDownloadStatus { Succeeded, Failed, Cancelled }

internal sealed record MediaRequestContext(Uri? Referer, string? BrowserSessionId);
internal sealed record MediaVariant(
    Uri Uri,
    int? Width,
    int? Height,
    int? FramesPerSecond,
    long? Bitrate,
    long? ContentLength,
    string? ContentType,
    string? Codec,
    MediaVariantSource Source,
    MediaRequestContext RequestContext);
internal sealed record MediaAsset(int Index, MediaKind Kind, IReadOnlyList<MediaVariant> Variants);
internal sealed record ResolvedMediaPost(
    MediaProviderId Provider,
    Uri SourceUri,
    string? PostId,
    string? Title,
    string? AuthorName,
    IReadOnlyList<MediaAsset> Assets);
internal sealed record MediaResolveResult(bool Succeeded, ResolvedMediaPost? Post, bool RequiresUserInteraction, string? Error);
internal sealed record MediaDownloadSettings(
    string DownloadDirectory,
    int MaxConcurrentDownloads,
    bool DefaultSelectAll,
    bool OpenFolderAfterDownload,
    MediaQualityPreference QualityPreference)
{
    public const int MinimumConcurrency = 1;
    public const int MaximumConcurrency = 8;
    public static MediaDownloadSettings Default { get; }
    public void Validate();
}
internal sealed record MediaDownloadTask(
    Guid Id,
    string SourceShareLink,
    ResolvedMediaPost Post,
    MediaAsset Asset,
    MediaVariant SelectedVariant,
    string TargetPath);
internal sealed record MediaDownloadProgress(Guid TaskId, long BytesDownloaded, long? TotalBytes, double? Percent);
internal sealed record MediaDownloadResult(Guid TaskId, MediaDownloadStatus Status, string? Error, string? SavedPath);
internal sealed record MediaDownloadHistoryEntry(
    MediaProviderId Provider,
    string SourceShareLink,
    string? Title,
    string? SavedDirectory,
    int SuccessCount,
    int FailureCount,
    DateTimeOffset DownloadedAtUtc);
```

`MediaRequestContext` 仅保存「使用哪个临时会话」的 `BrowserSessionId` 字符串标识，**不直接保存 Cookie**。

- [ ] **Step 1: 写失败测试**

```csharp
static void TestMediaModels()
{
    AssertEqual(0, (int)MediaKind.Image, "image kind order");
    AssertEqual(1, (int)MediaKind.Video, "video kind order");
    AssertEqual(3, MediaDownloadSettings.Default.MaxConcurrentDownloads, "default concurrency");
    AssertEqual(MediaQualityPreference.Highest, MediaDownloadSettings.Default.QualityPreference, "default quality preference");
    var entry = new MediaDownloadHistoryEntry(MediaProviderId.Douyin, "https://v.douyin.com/abc/", "标题", @"C:\dl", 3, 1, DateTimeOffset.UtcNow);
    var json = System.Text.Json.JsonSerializer.Serialize(entry);
    var back = System.Text.Json.JsonSerializer.Deserialize<MediaDownloadHistoryEntry>(json);
    AssertEqual("标题", back!.Title, "history entry round trip");
    AssertEqual(3, back.SuccessCount, "history entry counts");
    AssertThrows<ArgumentOutOfRangeException>(() => MediaDownloadSettings.Default with { MaxConcurrentDownloads = 9 }.Validate(), "concurrency upper bound");
}
```

- [ ] **Step 2: 运行验证红**

Run: `dotnet run --project tests/Transfor.Tests`
Expected: 编译失败（类型缺失）。

- [ ] **Step 3: 按上方签名创建 15 个模型文件**

`MediaDownloadSettings.Default`：

```csharp
private static string ResolveDefaultDownloadDirectory() =>
    Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"))
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        : AppPaths.Default.ApplicationDirectory;

public static MediaDownloadSettings Default => new(ResolveDefaultDownloadDirectory(), 3, true, false, MediaQualityPreference.Highest);

public void Validate()
{
    if (MaxConcurrentDownloads is < MinimumConcurrency or > MaximumConcurrency)
        throw new ArgumentOutOfRangeException(nameof(MaxConcurrentDownloads), "并发数必须在 1 到 8 之间。");
    if (string.IsNullOrWhiteSpace(DownloadDirectory))
        throw new ArgumentException("下载目录不能为空。", nameof(DownloadDirectory));
}
```

其余为纯 record/enum。

- [ ] **Step 4: 运行验证绿；构建通过**

Run: `dotnet run --project tests/Transfor.Tests` → `All 36 tests passed.`（计数基数 `+ 22`）
Run: `dotnet build Transfor.slnx` → 0 警告 0 错误

- [ ] **Step 5: 提交**

```powershell
git add src/Transfor/Features/MediaDownload/Models tests/Transfor.Tests/Program.cs
git commit -m "feat: add media download core models"
```

### Task 3: 解析契约与 ShareLinkParser

**Files:**
- Create: `src/Transfor/Features/MediaDownload/Contracts/IMediaResolver.cs`
- Create: `src/Transfor/Features/MediaDownload/Contracts/IMediaDownloadService.cs`
- Create: `src/Transfor/Features/MediaDownload/Contracts/IBrowserCaptureService.cs`
- Create: `src/Transfor/Features/MediaDownload/Contracts/IMediaDownloadHistoryRepository.cs`
- Create: `src/Transfor/Features/MediaDownload/Contracts/BrowserCaptureResult.cs`
- Create: `src/Transfor/Features/MediaDownload/Services/ShareLinkParser.cs`
- Test: `tests/Transfor.Tests/Program.cs`

**Interfaces:**

```csharp
internal interface IMediaResolver
{
    MediaProviderId Provider { get; }
    bool CanResolve(Uri sourceUri);
    Task<ResolvedMediaPost> ResolveAsync(Uri sourceUri, MediaRequestContext requestContext, CancellationToken cancellationToken);
}

internal interface IMediaDownloadService
{
    Task<MediaDownloadResult> DownloadAsync(MediaDownloadTask task, CancellationToken cancellationToken, IProgress<MediaDownloadProgress>? progress = null);
}

internal interface IBrowserCaptureService
{
    Task<BrowserCaptureResult> CaptureAsync(Uri pageUri, bool interactive, CancellationToken cancellationToken);
}

internal interface IMediaDownloadHistoryRepository
{
    IReadOnlyList<MediaDownloadHistoryEntry> GetHistory();
    void Add(MediaDownloadHistoryEntry entry);
}

internal sealed record BrowserCapturedCandidate(Uri Uri, string? ContentType, long? ContentLength);
internal sealed record BrowserCaptureResult(
    string? StructuredDataJson,
    IReadOnlyList<BrowserCapturedCandidate> Candidates,
    bool RequiresUserInteraction,
    string? Error);

internal sealed class BrowserResolutionRequiredException : Exception
{
    public BrowserResolutionRequiredException(string message) : base(message) { }
}
```

`ShareLinkParser.TryExtractFirstLink(string? text, out string? error)`：扫描第一个 `https://`/`http://`，取至空白符，再 `TrimEnd` 中文与英文标点集合（`，。、；：！？（）【】《》“”‘’…` 与 `,.!?;:)]}>'"`），`Uri.TryCreate` 校验仅 http/https；无链接返回 `error = "未在文本中找到链接。"`；一次只解析第一个有效链接；不访问网络。

- [ ] **Step 1: 写失败测试**

```csharp
static void TestShareLinkParser()
{
    var link = ShareLinkParser.TryExtractFirstLink("3.28 复制打开抖音，看看【某用户的作品】 https://v.douyin.com/abc123/", out _);
    AssertEqual("https://v.douyin.com/abc123/", link?.ToString(), "extract from share text");
    var clean = ShareLinkParser.TryExtractFirstLink("https://v.douyin.com/abc123/。", out _);
    AssertEqual("https://v.douyin.com/abc123/", clean?.ToString(), "trailing full-width period cleaned");
    var cleanAscii = ShareLinkParser.TryExtractFirstLink("https://v.douyin.com/abc123/.", out _);
    AssertEqual("https://v.douyin.com/abc123/", cleanAscii?.ToString(), "trailing ascii period cleaned");
    var first = ShareLinkParser.TryExtractFirstLink("https://v.douyin.com/one/ 然后 https://v.douyin.com/two/", out _);
    AssertEqual("https://v.douyin.com/one/", first?.ToString(), "only first link");
    AssertEqual(true, ShareLinkParser.TryExtractFirstLink("没有链接的文本", out var noLinkError) is null && noLinkError is not null, "no link returns error");
    AssertEqual(true, ShareLinkParser.TryExtractFirstLink("ftp://v.douyin.com/abc/", out _) is null, "non-http scheme rejected");
}
```

- [ ] **Step 2: 运行验证红** → 编译失败。
- [ ] **Step 3: 实现**（接口 + `ShareLinkParser` 按上述行为，不访问网络）。
- [ ] **Step 4: 运行验证绿**（`All 37 tests passed.`，基数 `+ 23`）；构建通过。
- [ ] **Step 5: 提交**

```powershell
git add src/Transfor/Features/MediaDownload/Contracts src/Transfor/Features/MediaDownload/Services/ShareLinkParser.cs tests/Transfor.Tests/Program.cs
git commit -m "feat: add media contracts and share link parser"
```

### Task 4: Infrastructure/Networking 安全网络原语

**Files:**
- Create: `src/Transfor/Infrastructure/Networking/HttpClientProvider.cs`
- Create: `src/Transfor/Infrastructure/Networking/SafeUriValidator.cs`
- Create: `src/Transfor/Infrastructure/Networking/RedirectResolver.cs`
- Create: `src/Transfor/Infrastructure/Networking/HttpResponseMetadataReader.cs`
- Test: `tests/Transfor.Tests/Program.cs`

**Interfaces:**

```csharp
internal static class HttpClientProvider
{
    public static HttpClient Create();   // AllowAutoRedirect=false, UseCookies=false, 桌面 UA 常量, Timeout=100s
}

internal static class SafeUriValidator
{
    public static bool IsAllowed(Uri uri, out string? error);
}

internal sealed class RedirectResolver
{
    public const int MaxRedirects = 5;
    public RedirectResolver(HttpClient client);
    public Task<Uri> ResolveFinalUriAsync(Uri initialUri, CancellationToken cancellationToken);
}

internal static class HttpResponseMetadataReader
{
    public static (string? ContentType, long? ContentLength) Read(HttpResponseMessage response);
}
```

`SafeUriValidator` 规则：仅 http/https；host 为 IP 字面量时检查回环（127/8、`::1`）、私网（10/8、172.16/12、192.168/16）、链路本地（169.254/16、`fe80::/10`）、`0.0.0.0`；host 为域名时 `Dns.GetHostAddressesAsync` 解析后逐地址检查（解析失败视为不允许）；`localhost` 直接拒绝。`RedirectResolver` 逐步 GET（`HttpCompletionOption.ResponseHeadersRead`），遇 3xx 取 `Location`（相对路径用 `new Uri(baseUri, location)` 合并），每跳重新 `SafeUriValidator` 校验，超过 5 跳抛 `InvalidOperationException("重定向次数超过限制。")`；非 3xx 返回当前 URI。

- [ ] **Step 1: 写失败测试（含测试辅助 handler）**

在 `Program.cs` 追加文件级测试辅助：

```csharp
file sealed class RedirectChainHandler : HttpMessageHandler
{
    private readonly int[] statuses;
    private readonly string?[] locations;
    private int index;
    public RedirectChainHandler(int[] statuses, string?[] locations)
    {
        this.statuses = statuses;
        this.locations = locations;
    }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var i = Math.Min(index++, statuses.Length - 1);
        var response = new HttpResponseMessage((HttpStatusCode)statuses[i]);
        if (locations[i] is not null) response.Headers.Location = new Uri(locations[i]!, UriKind.Relative);
        response.Content = new ByteArrayContent(Array.Empty<byte>());
        return Task.FromResult(response);
    }
}
```

```csharp
static void TestSafeUriValidator()
{
    AssertEqual(false, SafeUriValidator.IsAllowed(new Uri("http://127.0.0.1/x"), out _), "loopback rejected");
    AssertEqual(false, SafeUriValidator.IsAllowed(new Uri("http://10.1.2.3/x"), out _), "private 10/8 rejected");
    AssertEqual(false, SafeUriValidator.IsAllowed(new Uri("http://172.20.0.1/x"), out _), "private 172.16/12 rejected");
    AssertEqual(false, SafeUriValidator.IsAllowed(new Uri("http://192.168.1.1/x"), out _), "private 192.168/16 rejected");
    AssertEqual(false, SafeUriValidator.IsAllowed(new Uri("http://169.254.0.1/x"), out _), "link-local rejected");
    AssertEqual(false, SafeUriValidator.IsAllowed(new Uri("http://localhost/x"), out _), "localhost rejected");
    AssertEqual(false, SafeUriValidator.IsAllowed(new Uri("file:///C:/x"), out _), "file scheme rejected");
    AssertEqual(false, SafeUriValidator.IsAllowed(new Uri("javascript:alert(1)"), out _), "javascript scheme rejected");
    AssertEqual(true, SafeUriValidator.IsAllowed(new Uri("https://www.douyin.com/video/1"), out _), "https public allowed");
}

static void TestRedirectResolver()
{
    var client = new HttpClient(new RedirectChainHandler(new[] { 302, 302, 200 }, new[] { "/b", "/c", null })) { AllowAutoRedirect = false };
    var final = new RedirectResolver(client).ResolveFinalUriAsync(new Uri("https://v.douyin.com/a"), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual("https://v.douyin.com/c", final.ToString(), "follows two redirects");

    var loop = new RedirectResolver(new HttpClient(new RedirectChainHandler(
        new[] { 302, 302, 302, 302, 302, 302 }, new[] { "/x", "/x", "/x", "/x", "/x", "/x" })) { AllowAutoRedirect = false });
    AssertThrows<InvalidOperationException>(() => loop.ResolveFinalUriAsync(new Uri("https://v.douyin.com/a"), CancellationToken.None).GetAwaiter().GetResult(), "redirect limit exceeded");
}
```

- [ ] **Step 2: 运行验证红** → 编译失败。
- [ ] **Step 3: 实现四个文件**（`HttpClientProvider.Create` 中 `DefaultRequestHeaders.UserAgent` 设桌面浏览器 UA；`RedirectResolver` 每跳都过 `SafeUriValidator`）。
- [ ] **Step 4: 运行验证绿**（`All 39 tests passed.`，基数 `+ 25`）；构建通过。
- [ ] **Step 5: 提交**

```powershell
git add src/Transfor/Infrastructure/Networking tests/Transfor.Tests/Program.cs
git commit -m "feat: add safe networking primitives"
```

### Task 5: 质量选择与内容校验

**Files:**
- Create: `src/Transfor/Features/MediaDownload/Services/MediaQualitySelector.cs`
- Create: `src/Transfor/Features/MediaDownload/Services/MediaContentValidator.cs`
- Test: `tests/Transfor.Tests/Program.cs`

**Interfaces:**

```csharp
internal static class MediaQualitySelector
{
    public static MediaVariant SelectImage(IReadOnlyList<MediaVariant> variants, MediaQualityPreference preference);
    public static MediaVariant SelectVideo(IReadOnlyList<MediaVariant> variants, MediaQualityPreference preference);
}

internal static class MediaContentValidator
{
    public const long DefaultMaxFileBytes = 4L * 1024 * 1024 * 1024;
    public static bool IsPlausibleMedia(HttpResponseMessage response, MediaKind kind, long maxBytes, out string? error);
    public static bool HasValidMagicNumber(Stream stream, MediaKind kind);
    public static bool MatchesExpectedKind(string? contentType, MediaKind kind);
}
```

排序规则（`Highest`）：图片 `Width×Height` 降序 → `Width` 降序 → `ContentLength` 降序 → 来源优先级（`StructuredData > InlineState > JsonLd > Dom > NetworkCapture > Thumbnail`，即非缩略图来源优先）；视频 `Width×Height` 降序 → `FramesPerSecond` 降序 → `Bitrate` 降序 → 编码兼容性（`Codec` 含 `h264`/`avc`/`hevc` 优先，null 视为中性）→ `ContentLength` 降序。`Balanced`：同分辨率时优先 `ContentLength` 更小者。

`IsPlausibleMedia`：状态码 2xx、`MatchesExpectedKind(response.Content.Headers.ContentType?.MediaType, kind)`、`ContentLength` 为 null 或 ≤ maxBytes。`HasValidMagicNumber` 只读流前 12 字节：JPEG `FF D8 FF`、PNG `89 50 4E 47 0D 0A 1A 0A`、GIF `47 49 46 38`、WebP `RIFF....WEBP`、MP4（偏移 4 处 `ftyp`）、WebM/Matroska `1A 45 DF A3`。

- [ ] **Step 1: 写失败测试**

```csharp
static MediaVariant V(Uri uri, int? w, int? h, long? len, MediaVariantSource source, int? fps = null, long? bitrate = null, string? codec = null) =>
    new(uri, w, h, fps, bitrate, len, null, codec, source, new MediaRequestContext(null, null));

static void TestMediaQualitySelector()
{
    var small = V(new Uri("https://x/1.jpg"), 100, 100, 1000, MediaVariantSource.StructuredData);
    var large = V(new Uri("https://x/2.jpg"), 2000, 2000, 5000, MediaVariantSource.StructuredData);
    AssertEqual("https://x/2.jpg", MediaQualitySelector.SelectImage(new[] { small, large }, MediaQualityPreference.Highest).Uri.ToString(), "largest area wins");

    var wide = V(new Uri("https://x/3.jpg"), 3000, 1000, 6000, MediaVariantSource.StructuredData);
    AssertEqual("https://x/3.jpg", MediaQualitySelector.SelectImage(new[] { small, wide }, MediaQualityPreference.Highest).Uri.ToString(), "larger width wins on equal area");

    var thumb = V(new Uri("https://x/thumb.jpg"), 9000, 9000, 9999, MediaVariantSource.Thumbnail);
    AssertEqual("https://x/2.jpg", MediaQualitySelector.SelectImage(new[] { small, thumb, large }, MediaQualityPreference.Highest).Uri.ToString(), "thumbnail never wins over real candidate");

    var v720 = V(new Uri("https://x/a.mp4"), 1280, 720, 1000, MediaVariantSource.StructuredData, 30, 1000, "h264");
    var v1080 = V(new Uri("https://x/b.mp4"), 1920, 1080, 2000, MediaVariantSource.StructuredData, 30, 2000, "h264");
    AssertEqual("https://x/b.mp4", MediaQualitySelector.SelectVideo(new[] { v720, v1080 }, MediaQualityPreference.Highest).Uri.ToString(), "higher resolution video wins");

    var v1080f60 = V(new Uri("https://x/c.mp4"), 1920, 1080, 3000, MediaVariantSource.StructuredData, 60, 2000, "h264");
    AssertEqual("https://x/c.mp4", MediaQualitySelector.SelectVideo(new[] { v1080, v1080f60 }, MediaQualityPreference.Highest).Uri.ToString(), "higher fps wins on equal resolution");

    AssertEqual("https://x/a.mp4", MediaQualitySelector.SelectVideo(new[] { v720 }, MediaQualityPreference.Highest).Uri.ToString(), "single variant selected");
}

static void TestMediaContentValidator()
{
    using var jpeg = new MemoryStream(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 });
    AssertEqual(true, MediaContentValidator.HasValidMagicNumber(jpeg, MediaKind.Image), "jpeg magic ok");
    using var png = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 });
    AssertEqual(true, MediaContentValidator.HasValidMagicNumber(png, MediaKind.Image), "png magic ok");
    using var fake = new MemoryStream(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0, 0, 0, 0, 0 });
    AssertEqual(false, MediaContentValidator.HasValidMagicNumber(fake, MediaKind.Image), "zip magic rejected as image");
    using var mp4 = new MemoryStream(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0, 0, 0, 0 });
    AssertEqual(true, MediaContentValidator.HasValidMagicNumber(mp4, MediaKind.Video), "mp4 magic ok");

    AssertEqual(false, MediaContentValidator.MatchesExpectedKind("text/html", MediaKind.Video), "text/html rejected for video");
    AssertEqual(true, MediaContentValidator.MatchesExpectedKind("image/jpeg", MediaKind.Image), "image/jpeg accepted for image");
    AssertEqual(true, MediaContentValidator.MatchesExpectedKind(null, MediaKind.Video), "null content type allowed");

    using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
    response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
    AssertEqual(true, MediaContentValidator.IsPlausibleMedia(response, MediaKind.Image, 1024, out _), "plausible media ok");
    var tooBig = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
    tooBig.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
    tooBig.Content.Headers.ContentLength = 2048;
    AssertEqual(false, MediaContentValidator.IsPlausibleMedia(tooBig, MediaKind.Image, 1024, out _), "oversized rejected");
}
```

- [ ] **Step 2: 运行验证红** → 编译失败。
- [ ] **Step 3: 实现两个文件**。
- [ ] **Step 4: 运行验证绿**（`All 41 tests passed.`，基数 `+ 27`）；构建通过。
- [ ] **Step 5: 提交**

```powershell
git add src/Transfor/Features/MediaDownload/Services/MediaQualitySelector.cs src/Transfor/Features/MediaDownload/Services/MediaContentValidator.cs tests/Transfor.Tests/Program.cs
git commit -m "feat: add media quality selector and content validator"
```

### Task 6: 流式下载服务与文件命名

**Files:**
- Create: `src/Transfor/Features/MediaDownload/Services/MediaDownloadService.cs`
- Create: `src/Transfor/Features/MediaDownload/Services/DownloadFileNameBuilder.cs`
- Create: `src/Transfor/Features/MediaDownload/Services/MediaHashService.cs`
- Test: `tests/Transfor.Tests/Program.cs`

**Interfaces:**

```csharp
internal sealed record BrowserCookie(string Domain, string Name, string Value);

internal interface ICookieSource
{
    Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(Uri uri);
}

internal static class DownloadFileNameBuilder
{
    public static string SanitizeFileName(string raw);
    public static string BuildUniquePath(string directory, string fileName);
}

internal static class MediaHashService
{
    public static string ComputeSha256(Stream stream);
}

internal sealed class MediaDownloadService : IMediaDownloadService
{
    public MediaDownloadService(HttpClient client, ICookieSource? cookieSource, long maxFileBytes = MediaContentValidator.DefaultMaxFileBytes);
}
```

行为：请求头带 `Referer`（`task.SelectedVariant.RequestContext.Referer`）；Cookie 仅取 `cookieSource.GetCookiesAsync(requestUri)` 中域名与请求 host 相等或为其父后缀者拼 `Cookie` 头（不发送给不匹配域名）；GET + `HttpCompletionOption.ResponseHeadersRead`；`IsPlausibleMedia` 校验（失败即返回 `Failed` 且不写文件）；流式写 `TargetPath + ".part"`（32 KB 缓冲）并报告进度；完成后对 `.part` 校验魔数 → 计算 SHA-256，若目标已存在且哈希相同则删除 `.part` 判定成功（幂等重试），否则 `File.Move(part, target, true)`；失败/取消删除 `.part`；全程不整文件载入内存。

- [ ] **Step 1: 写失败测试（含 handler 测试辅助）**

```csharp
file sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>> responses;
    public List<HttpRequestMessage> Requests { get; } = new();
    public StubHttpMessageHandler(params Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>[] responses)
    {
        this.responses = new Queue<Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>>(responses);
    }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var factory = responses.Dequeue();
        return Task.FromResult(factory(request, cancellationToken));
    }
}

static Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> OkBytes(byte[] body, string contentType = "video/mp4", long? declaredLength = null) =>
    (_, _) =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        if (declaredLength is not null) response.Content.Headers.ContentLength = declaredLength;
        return response;
    };
```

```csharp
static string MediaPartFile(string target) => target + ".part";

static void TestDownloadFileNameBuilder()
{
    AssertEqual("a_b_c.mp4", DownloadFileNameBuilder.SanitizeFileName("a:b\\c.mp4"), "invalid chars replaced");
    var dir = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var first = DownloadFileNameBuilder.BuildUniquePath(dir, "x.mp4");
        File.WriteAllText(first, "1");
        var second = DownloadFileNameBuilder.BuildUniquePath(dir, "x.mp4");
        AssertEqual("x(1).mp4", Path.GetFileName(second), "duplicate gets suffix");
    }
    finally { Directory.Delete(dir, recursive: true); }
}

static void TestMediaDownloadService()
{
    var dir = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        // 200 成功：文件存在、.part 已删除
        var target = Path.Combine(dir, "ok.mp4");
        var body = new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x63, 0x31, 0x00, 0x00, 0x00, 0x20 };
        var okHandler = new StubHttpMessageHandler(OkBytes(body, "video/mp4"));
        var okClient = new HttpClient(okHandler);
        var service = new MediaDownloadService(okClient, null);
        var task = new MediaDownloadTask(Guid.NewGuid(), "https://v.douyin.com/a/", CreatePostForTest(), CreateAssetForTest(MediaKind.Video), CreateVariantForTest(new Uri("https://cdn.example.com/v.mp4")), target);
        var result = service.DownloadAsync(task, CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Succeeded, result.Status, "download success");
        AssertEqual(true, File.Exists(target), "file exists");
        AssertEqual(false, File.Exists(MediaPartFile(target)), "part removed after success");

        // 404 / 429 / 500 -> Failed
        foreach (var status in new[] { HttpStatusCode.NotFound, (HttpStatusCode)429, HttpStatusCode.InternalServerError })
        {
            var t2 = Path.Combine(dir, $"fail{(int)status}.mp4");
            var h = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(status) { Content = new ByteArrayContent(Array.Empty<byte>()) });
            var r2 = new MediaDownloadService(new HttpClient(h), null).DownloadAsync(
                new MediaDownloadTask(Guid.NewGuid(), "u", CreatePostForTest(), CreateAssetForTest(MediaKind.Video), CreateVariantForTest(new Uri("https://cdn.example.com/v.mp4")), t2), CancellationToken.None).GetAwaiter().GetResult();
            AssertEqual(MediaDownloadStatus.Failed, r2.Status, $"http {(int)status} fails");
            AssertEqual(false, File.Exists(MediaPartFile(t2)), "no part on status failure");
        }

        // Content-Type 非媒体 -> Failed
        var t3 = Path.Combine(dir, "html.mp4");
        var h3 = new StubHttpMessageHandler(OkBytes(Array.Empty<byte>(), "text/html"));
        var r3 = new MediaDownloadService(new HttpClient(h3), null).DownloadAsync(
            new MediaDownloadTask(Guid.NewGuid(), "u", CreatePostForTest(), CreateAssetForTest(MediaKind.Video), CreateVariantForTest(new Uri("https://cdn.example.com/v.mp4")), t3), CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Failed, r3.Status, "text/html rejected");

        // Content-Length 超限 -> Failed
        var t4 = Path.Combine(dir, "big.mp4");
        var h4 = new StubHttpMessageHandler(OkBytes(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70 }, "video/mp4", declaredLength: 1024));
        var r4 = new MediaDownloadService(new HttpClient(h4), null, maxFileBytes: 512).DownloadAsync(
            new MediaDownloadTask(Guid.NewGuid(), "u", CreatePostForTest(), CreateAssetForTest(MediaKind.Video), CreateVariantForTest(new Uri("https://cdn.example.com/v.mp4")), t4), CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Failed, r4.Status, "oversized rejected");

        // 流中途断开 -> Failed 且 .part 删除
        var t5 = Path.Combine(dir, "aborted.mp4");
        var h5 = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new AbortedStreamContent() });
        var r5 = new MediaDownloadService(new HttpClient(h5), null).DownloadAsync(
            new MediaDownloadTask(Guid.NewGuid(), "u", CreatePostForTest(), CreateAssetForTest(MediaKind.Video), CreateVariantForTest(new Uri("https://cdn.example.com/v.mp4")), t5), CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Failed, r5.Status, "aborted stream fails");
        AssertEqual(false, File.Exists(MediaPartFile(t5)), "no part after abort");

        // 取消 -> Cancelled 且 .part 删除
        var t6 = Path.Combine(dir, "cancelled.mp4");
        using var cts = new CancellationTokenSource();
        var h6 = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new SlowStreamContent(cts.Token) });
        var r6 = new MediaDownloadService(new HttpClient(h6), null).DownloadAsync(
            new MediaDownloadTask(Guid.NewGuid(), "u", CreatePostForTest(), CreateAssetForTest(MediaKind.Video), CreateVariantForTest(new Uri("https://cdn.example.com/v.mp4")), t6), cts.Token).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Cancelled, r6.Status, "cancel returns cancelled");
        AssertEqual(false, File.Exists(MediaPartFile(t6)), "no part after cancel");

        // Referer 头与 Cookie 域名过滤
        var headers = new StubHttpMessageHandler(OkBytes(body, "video/mp4"));
        var cookies = new FakeCookieSource(new[]
        {
            new BrowserCookie("douyin.com", "sid", "abc"),
            new BrowserCookie("evil.com", "sid", "stolen"),
        });
        var refUri = new Uri("https://www.douyin.com/video/1");
        var variant = CreateVariantForTest(new Uri("https://cdn.example.com/v.mp4"), refUri);
        new MediaDownloadService(new HttpClient(headers), cookies).DownloadAsync(
            new MediaDownloadTask(Guid.NewGuid(), "u", CreatePostForTest(), CreateAssetForTest(MediaKind.Video), variant, Path.Combine(dir, "ref.mp4")), CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(refUri.ToString(), headers.Requests[0].Headers.Referrer?.ToString(), "referer sent");
        AssertEqual(true, headers.Requests[0].Headers.TryGetValues("Cookie", out var cookieValues) && string.Join(";", cookieValues).Contains("abc"), "matching-domain cookie sent");
        AssertEqual(true, !string.Join(";", cookieValues).Contains("stolen"), "mismatched-domain cookie not sent");

        // 目标已存在且哈希相同 -> 幂等成功不覆盖
        var t7 = Path.Combine(dir, "dupe.mp4");
        File.WriteAllBytes(t7, body);
        var h7 = new StubHttpMessageHandler(OkBytes(body, "video/mp4"));
        var r7 = new MediaDownloadService(new HttpClient(h7), null).DownloadAsync(
            new MediaDownloadTask(Guid.NewGuid(), "u", CreatePostForTest(), CreateAssetForTest(MediaKind.Video), CreateVariantForTest(new Uri("https://cdn.example.com/v.mp4")), t7), CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(MediaDownloadStatus.Succeeded, r7.Status, "identical existing file is idempotent success");
    }
    finally { Directory.Delete(dir, recursive: true); }
}
```

测试辅助（文件级）：`AbortedStreamContent`（`ReadAsync` 抛 `IOException`）、`SlowStreamContent`（每次读前检查取消，取消时抛 `OperationCanceledException`，写入少量字节）、`FakeCookieSource : ICookieSource`、`CreatePostForTest`/`CreateAssetForTest`/`CreateVariantForTest`（构造最小 `ResolvedMediaPost`/`MediaAsset`/`MediaVariant`）。

- [ ] **Step 2: 运行验证红** → 编译失败。
- [ ] **Step 3: 实现三个文件**（`BuildUniquePath`：目标存在时追加 `(1)`、`(2)`…；`SanitizeFileName`：`Path.GetInvalidFileNameChars()` 替换为 `_` 并 `TrimEnd('.', ' ')`）。
- [ ] **Step 4: 运行验证绿**（`All 43 tests passed.`，基数 `+ 29`）；构建通过。
- [ ] **Step 5: 提交**

```powershell
git add src/Transfor/Features/MediaDownload/Services/MediaDownloadService.cs src/Transfor/Features/MediaDownload/Services/DownloadFileNameBuilder.cs src/Transfor/Features/MediaDownload/Services/MediaHashService.cs tests/Transfor.Tests/Program.cs
git commit -m "feat: add streaming media download service"
```

### Task 7: 下载队列协调器

**Files:**
- Create: `src/Transfor/Features/MediaDownload/Application/MediaDownloadCoordinator.cs`
- Test: `tests/Transfor.Tests/Program.cs`

**Interfaces:**

```csharp
internal sealed class MediaDownloadCoordinator : IDisposable
{
    public MediaDownloadCoordinator(IMediaDownloadService downloadService, MediaStateStore stateStore, int concurrency = 3);
    public bool HasActiveTasks { get; }
    public event EventHandler<MediaDownloadProgress>? TaskProgressChanged;
    public event EventHandler<MediaDownloadTaskCompleted>? TaskCompleted;
    public Task EnqueueAsync(string sourceShareLink, ResolvedMediaPost post, IReadOnlyList<MediaDownloadTask> tasks, CancellationToken cancellationToken);
    public void CancelTask(Guid taskId);
    public void CancelAll();
    public void Dispose();
}

internal sealed record MediaDownloadTaskCompleted(Guid TaskId, MediaDownloadStatus Status, string? Error, string? SavedPath);
```

行为：`SemaphoreSlim(concurrency)` 限流；每任务独立 `CancellationTokenSource`（存 `ConcurrentDictionary<Guid, CancellationTokenSource>`）；构造时捕获 `SynchronizationContext.Current`，非 null 则进度/完成事件经其 `Post` 回调（页面在 UI 线程创建即得 UI 上下文），null 时直接回调（控制台测试）；批次全部落定后向 `MediaStateStore.Add` 写入一条 `MediaDownloadHistoryEntry`（成功数/失败数）；失败任务由 UI「重试」重新入队（协调器不做自动重试）；`CancelAll` 取消全部并等待落定；`Dispose` 幂等。

- [ ] **Step 1: 写失败测试**

```csharp
file sealed class GateDownloadService : IMediaDownloadService
{
    public readonly List<Guid> Active = new();
    public int MaxConcurrent;
    public TaskCompletionSource<bool>? Gate;
    public async Task<MediaDownloadResult> DownloadAsync(MediaDownloadTask task, CancellationToken cancellationToken, IProgress<MediaDownloadProgress>? progress = null)
    {
        lock (Active) { Active.Add(task.Id); MaxConcurrent = Math.Max(MaxConcurrent, Active.Count); }
        try { await Task.Delay(Timeout.Infinite, cancellationToken); return new MediaDownloadResult(task.Id, MediaDownloadStatus.Cancelled, null, null); }
        finally { lock (Active) { Active.Remove(task.Id); } }
    }
}
```

```csharp
static void TestDownloadCoordinator()
{
    var root = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var state = new MediaStateStore(new AppPaths(root));

        // 并发上限 3：4 个任务同时入队，峰值并发不得超过 3
        var gated = new GateDownloadService();
        using var coordinator = new MediaDownloadCoordinator(gated, state, concurrency: 3);
        var post = CreatePostForTest();
        var tasks = Enumerable.Range(0, 4).Select(i => CreateTaskForTest(post, i)).ToArray();
        var batch = coordinator.EnqueueAsync("https://v.douyin.com/a/", post, tasks, CancellationToken.None);
        Task.Delay(300).GetAwaiter().GetResult();
        AssertEqual(true, gated.MaxConcurrent <= 3, "concurrency limited to 3");
        coordinator.CancelAll();
        batch.GetAwaiter().GetResult();
        AssertEqual(false, coordinator.HasActiveTasks, "no active tasks after cancel all");

        // 单任务取消不影响其他
        var okService = new FakeDownloadServiceSucceed();
        using var coordinator2 = new MediaDownloadCoordinator(okService, state, concurrency: 3);
        var tasks2 = Enumerable.Range(0, 3).Select(i => CreateTaskForTest(post, i)).ToArray();
        var batch2 = coordinator2.EnqueueAsync("u", post, tasks2, CancellationToken.None);
        Task.Delay(200).GetAwaiter().GetResult();
        coordinator2.CancelTask(tasks2[0].Id);
        batch2.GetAwaiter().GetResult();
        AssertEqual(2, state.GetHistory().Last().SuccessCount, "history success count");
        AssertEqual(1, state.GetHistory().Last().FailureCount, "history failure count");

        // 失败不写成功历史：全部失败 -> SuccessCount=0
        var failService = new FakeDownloadServiceFail();
        using var coordinator3 = new MediaDownloadCoordinator(failService, state, concurrency: 3);
        var tasks3 = new[] { CreateTaskForTest(post, 0) };
        coordinator3.EnqueueAsync("u", post, tasks3, CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(0, state.GetHistory().Last().SuccessCount, "all-failed batch records zero success");
    }
    finally { Directory.Delete(root, recursive: true); }
}
```

测试辅助：`FakeDownloadServiceSucceed`（立即 `Succeeded`，TargetPath 写空文件）、`FakeDownloadServiceFail`（立即 `Failed("模拟失败")`）、`CreateTaskForTest`。

- [ ] **Step 2: 运行验证红** → 编译失败（`MediaStateStore` 尚不存在，本任务引用它——见 Task 8；**实施顺序说明**：协调器依赖 `MediaStateStore`，若先做本任务，需同时实现 Task 8 的最小可用版 `MediaStateStore`。本计划把 Task 7/8 视为同一批实施：先实现 Task 8 存储，再完成 Task 7 队列。**
- [ ] **Step 3: 实现 Task 8 的 `MediaStateStore` 最小版（Settings 占位 + History 列表，方法内部持久化）**——详见 Task 8 完整实现，此处先落地其 `Load/Add/GetHistory`。
- [ ] **Step 4: 实现 `MediaDownloadCoordinator`**。
- [ ] **Step 5: 运行验证绿**（`All 44 tests passed.`，基数 `+ 30`）；构建通过。
- [ ] **Step 6: 提交**

```powershell
git add src/Transfor/Features/MediaDownload/Application tests/Transfor.Tests/Program.cs
git commit -m "feat: add download queue coordinator"
```

### Task 8: 媒体持久化

**Files:**
- Create: `src/Transfor/Infrastructure/Persistence/JsonFileStore.cs`
- Create: `src/Transfor/Infrastructure/Persistence/MediaStateStore.cs`
- Modify: `src/Transfor/App/AppPaths.cs`
- Test: `tests/Transfor.Tests/Program.cs`

**Interfaces:**

```csharp
internal static class JsonFileStore
{
    public static void Write<T>(string path, T value);       // path.tmp.<guid> -> File.Move(overwrite)
    public static T? TryRead<T>(string path);                 // 解析失败/版本不符返回 default
}

internal sealed class MediaStateStore : IMediaDownloadHistoryRepository
{
    public MediaStateStore(AppPaths paths);
    public MediaDownloadSettings Settings { get; }
    public void UpdateSettings(MediaDownloadSettings settings);   // 校验 + 内部持久化
    public IReadOnlyList<MediaDownloadHistoryEntry> GetHistory();
    public void Add(MediaDownloadHistoryEntry entry);             // 内部持久化
}
```

`AppPaths` 追加：

```csharp
public string MediaSettingsFile => Path.Combine(ApplicationDirectory, "media-settings.json");
public string MediaDownloadHistoryFile => Path.Combine(ApplicationDirectory, "download-history.json");
public string WebView2Directory => Path.Combine(ApplicationDirectory, "WebView2", "Douyin");
```

新文件不存在直接用默认值，**不做迁移**；损坏文件回退默认值。`TextStateStore` 本任务不修改（其自有 Write 逻辑保留，后续统一为 `JsonFileStore` 属于可选清理，不在本计划范围）。

- [ ] **Step 1: 写失败测试**

```csharp
static void TestMediaStateStore()
{
    var root = Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var paths = new AppPaths(root);

        // 缺文件 -> 默认设置与空历史
        var store = new MediaStateStore(paths);
        AssertEqual(3, store.Settings.MaxConcurrentDownloads, "defaults when files missing");
        AssertEqual(0, store.GetHistory().Count, "empty history when file missing");

        // UpdateSettings/Add 内部持久化（不调 Save）
        store.UpdateSettings(store.Settings with { MaxConcurrentDownloads = 5 });
        store.Add(new MediaDownloadHistoryEntry(MediaProviderId.Douyin, "https://v.douyin.com/a/", "t", root, 2, 0, DateTimeOffset.UtcNow));
        var reloaded = new MediaStateStore(paths);
        AssertEqual(5, reloaded.Settings.MaxConcurrentDownloads, "settings persisted");
        AssertEqual(1, reloaded.GetHistory().Count, "history persisted");

        // 文件中不出现 cookie/token/authorization 字段
        var raw = File.ReadAllText(paths.MediaDownloadHistoryFile).ToLowerInvariant();
        AssertEqual(true, !raw.Contains("cookie") && !raw.Contains("token") && !raw.Contains("authorization"), "no credential fields serialized");

        // 损坏 JSON -> 回退默认
        File.WriteAllText(paths.MediaSettingsFile, "not json");
        AssertEqual(3, new MediaStateStore(paths).Settings.MaxConcurrentDownloads, "corrupt settings fallback to defaults");
    }
    finally { Directory.Delete(root, recursive: true); }
}
```

- [ ] **Step 2: 运行验证红** → 编译失败。
- [ ] **Step 3: 实现 `JsonFileStore` + `MediaStateStore` + `AppPaths` 扩展**（`JsonFileStore` 用 `Document<T>(1, value)` 外壳，枚举字符串序列化）。
- [ ] **Step 4: 运行验证绿**（`All 45 tests passed.`，基数 `+ 31`）；构建通过。
- [ ] **Step 5: 提交**

```powershell
git add src/Transfor/Infrastructure/Persistence/JsonFileStore.cs src/Transfor/Infrastructure/Persistence/MediaStateStore.cs src/Transfor/App/AppPaths.cs tests/Transfor.Tests/Program.cs
git commit -m "feat: add media settings and download history store"
```

### Task 9: 解析注册中心与协调器

**Files:**
- Create: `src/Transfor/Features/MediaDownload/Application/MediaResolverRegistry.cs`
- Create: `src/Transfor/Features/MediaDownload/Application/MediaResolveCoordinator.cs`
- Test: `tests/Transfor.Tests/Program.cs`

**Interfaces:**

```csharp
internal sealed class MediaResolverRegistry
{
    public MediaResolverRegistry(IReadOnlyList<IMediaResolver> resolvers);
    public IMediaResolver GetRequiredResolver(Uri sourceUri);
}

internal sealed class MediaResolveCoordinator
{
    public MediaResolveCoordinator(MediaResolverRegistry registry, IBrowserCaptureService? browserCapture = null);
    public Task<MediaResolveResult> ResolveAsync(Uri sourceUri, CancellationToken cancellationToken);
}
```

流程：`GetRequiredResolver`（首个 `CanResolve` 匹配，否则 `NotSupportedException("暂不支持该链接。")`）→ 调 `ResolveAsync`；捕获 `BrowserResolutionRequiredException` 且已提供 `browserCapture` 时走浏览器解析（捕获 → `DouyinPageParser.Parse(StructuredDataJson)` → `DouyinMediaNormalizer.Normalize`，任务 13 落地）；否则返回 `MediaResolveResult(false, null, RequiresUserInteraction: true, "需要浏览器解析，请点击「浏览器登录」。")`。取消沿 `ct` 传递。

- [ ] **Step 1: 写失败测试**

```csharp
file sealed class FakeResolver : IMediaResolver
{
    private readonly MediaProviderId provider;
    private readonly bool canResolve;
    private readonly bool needsBrowser;
    public FakeResolver(MediaProviderId provider, bool canResolve = true, bool needsBrowser = false)
    {
        this.provider = provider;
        this.canResolve = canResolve;
        this.needsBrowser = needsBrowser;
    }
    public MediaProviderId Provider => provider;
    public bool CanResolve(Uri sourceUri) => canResolve;
    public Task<ResolvedMediaPost> ResolveAsync(Uri sourceUri, MediaRequestContext requestContext, CancellationToken cancellationToken)
    {
        if (needsBrowser) throw new BrowserResolutionRequiredException("需要浏览器");
        return Task.FromResult(CreatePostForTest());
    }
}
```

```csharp
static void TestMediaResolverRegistry()
{
    var douyin = new FakeResolver(MediaProviderId.Douyin);
    var registry = new MediaResolverRegistry(new IMediaResolver[] { douyin });
    AssertEqual(MediaProviderId.Douyin, registry.GetRequiredResolver(new Uri("https://v.douyin.com/a/")).Provider, "first matching resolver wins");

    var empty = new MediaResolverRegistry(Array.Empty<IMediaResolver>());
    AssertThrows<NotSupportedException>(() => empty.GetRequiredResolver(new Uri("https://v.douyin.com/a/")), "no resolver throws");
}

static void TestMediaResolveCoordinator()
{
    var ok = new MediaResolveCoordinator(new MediaResolverRegistry(new IMediaResolver[] { new FakeResolver(MediaProviderId.Douyin) }));
    var result = ok.ResolveAsync(new Uri("https://v.douyin.com/a/"), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(true, result.Succeeded && result.Post is not null, "resolves with resolver");

    var needsBrowser = new MediaResolveCoordinator(new MediaResolverRegistry(new IMediaResolver[] { new FakeResolver(MediaProviderId.Douyin, needsBrowser: true) }));
    var pending = needsBrowser.ResolveAsync(new Uri("https://v.douyin.com/a/"), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(true, pending.RequiresUserInteraction && !pending.Succeeded, "browser requirement surfaces as user-interaction result");
}
```

- [ ] **Step 2: 运行验证红** → 编译失败。
- [ ] **Step 3: 实现两个文件**。
- [ ] **Step 4: 运行验证绿**（`All 47 tests passed.`，基数 `+ 33`）；构建通过。
- [ ] **Step 5: 提交**

```powershell
git add src/Transfor/Features/MediaDownload/Application tests/Transfor.Tests/Program.cs
git commit -m "feat: add media resolve coordinator"
```

### Task 10: 媒体下载页面 UI

**Files:**
- Create: `src/Transfor/Features/MediaDownload/UI/MediaDownloadPage.cs`
- Create: `src/Transfor/Features/MediaDownload/UI/MediaAssetListControl.cs`
- Create: `src/Transfor/Features/MediaDownload/UI/MediaPreviewControl.cs`
- Test: `tests/Transfor.Tests/Program.cs`

**Interfaces:**

```csharp
internal enum MediaPageState { Idle, Resolving, WaitingForUser, Resolved, Downloading, Completed, Failed }

internal sealed class MediaDownloadPage : UserControl, IFeaturePage
{
    public MediaDownloadPage(MediaResolveCoordinator resolveCoordinator, MediaDownloadCoordinator downloadCoordinator, MediaStateStore stateStore);
    public string Id => "media-download";
    public string DisplayName => "媒体下载";
    public Control View => this;
    public void OnActivated();
}
```

布局按设计规格 ASCII 图：输入区（分享链接文本框 + `从剪贴板粘贴`/`解析`/`浏览器登录`/`清空` 按钮）→ 作品信息区（平台/标题/作者/共发现 N 个媒体）→ `MediaAssetListControl`（`ListView` + `CheckBoxes`，列：选择/序号/类型/尺寸/大小/预览）→ 保存目录行（`TextBox` + `选择目录`/`全选`/`取消全选`/`下载所选`）→ 下载队列区（`ListView`：文件名/进度/状态/操作）。

状态机：`Resolving`/`WaitingForUser` 期间禁用 粘贴/解析/浏览器登录/清空/下载相关按钮；`Downloading` 期间禁用 解析/浏览器登录/全选/取消全选/下载所选；`Completed`/`Failed` 恢复基础可用态。解析流程：`ShareLinkParser.TryExtractFirstLink` → `MediaResolveCoordinator.ResolveAsync` → 成功则填充列表（默认按 `Settings.DefaultSelectAll` 勾选，多图保持顺序）；`RequiresUserInteraction` 则置 `WaitingForUser` 并提示点击「浏览器登录」。

下载：从 `MediaQualitySelector` 选取每个选中资产的最佳变体 → 构造 `MediaDownloadTask`（目标路径经 `DownloadFileNameBuilder`，扩展名取自 `ContentType`）→ `downloadCoordinator.EnqueueAsync`；队列行绑定 `TaskProgressChanged`（进度条/百分比列）与 `TaskCompleted`（状态列；`Failed` 行提供 `重试` 按钮重新入队）；批次结束后若 `SuccessCount > 0` 且 `OpenFolderAfterDownload` 则 `Process.Start("explorer.exe", dir)`。「下载所选」前校验保存目录存在，不存在则提示选择。

`MediaPreviewControl`：选中图片资产时经 `MediaDownloadService` 下载其最佳变体到 `%TEMP%\Transfor\PreviewCache\`（每次解析前清空缓存目录）并以 `PictureBox` 显示，失败显示错误文本；视频仅显示元数据（尺寸/帧率/码率）占位。预览使用独立 `HttpClient`，支持取消。

- [ ] **Step 1: 写失败测试（STA）**

```csharp
static void TestMediaDownloadPageContract()
{
    RunSta(() =>
    {
        var state = new MediaStateStore(new AppPaths(Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"))));
        var download = new MediaDownloadCoordinator(new FakeDownloadServiceSucceed(), state, 3);
        var resolve = new MediaResolveCoordinator(new MediaResolverRegistry(Array.Empty<IMediaResolver>()));
        using var page = new MediaDownloadPage(resolve, download, state);
        AssertEqual("media-download", page.Id, "media page id");
        AssertEqual("媒体下载", page.DisplayName, "media page display name");
        AssertEqual(true, page.View is UserControl, "media page view");
        download.Dispose();
    });
}
```

- [ ] **Step 2: 运行验证红** → 编译失败。
- [ ] **Step 3: 实现三个 UI 文件**（事件处理器允许 `async void`；网络调用一律带 `CancellationToken`；不引用任何解析器类型）。
- [ ] **Step 4: 运行验证绿**（`All 48 tests passed.`，基数 `+ 34`）；构建通过。
- [ ] **Step 5: 提交**

```powershell
git add src/Transfor/Features/MediaDownload/UI tests/Transfor.Tests/Program.cs
git commit -m "feat: add media download page"
```

### Task 11: 组合与生命周期

**Files:**
- Create: `src/Transfor/App/MediaServices.cs`
- Modify: `src/Transfor/App/AppBootstrapper.cs`
- Modify: `src/Transfor/App/AppServices.cs`
- Modify: `src/Transfor/App/TransforApplicationContext.cs`
- Test: `tests/Transfor.Tests/Program.cs`

**Interfaces / 行为：**

```csharp
internal sealed class MediaServices : IDisposable
{
    public required MediaStateStore State { get; init; }
    public required MediaResolveCoordinator ResolveCoordinator { get; init; }
    public required MediaDownloadCoordinator DownloadCoordinator { get; init; }
    public required HttpClient HttpClient { get; init; }
    public void Dispose()
    {
        HttpClient.Dispose();
        DownloadCoordinator.Dispose();
    }
}
```

`AppServices` 追加 `public required MediaServices Media { get; init; }`；`Dispose()` 改为 `HotKeys.Dispose(); Media.Dispose();`。

`AppBootstrapper.Create()`：`var http = HttpClientProvider.Create();` → `MediaStateStore.Load(paths)` → `MediaDownloadService(http, cookieSource: null, ...)`（任务 13 前无 Cookie 源）→ `MediaDownloadCoordinator(...)` → 注册中心（`DouyinMediaResolver` 与 `DirectMediaResolver` 在任务 12 创建后接入；本任务先接空列表）→ `MediaResolveCoordinator(registry, browserCapture: null)` → 组装 `MediaServices`。页面数组：

```csharp
var pages = new IFeaturePage[]
{
    new TextToolsPage(services.State),
    new MediaDownloadPage(services.Media.ResolveCoordinator, services.Media.DownloadCoordinator, services.Media.State),
};
```

WebView2 不在此创建（延迟到 UI 线程首次使用，任务 13）。

`TransforApplicationContext.ExitApplication()` 增加：

```csharp
if (services.Media.DownloadCoordinator.HasActiveTasks)
{
    var confirm = MessageBox.Show(mainForm, "仍有下载任务进行中，确定要退出并取消任务吗？", "退出确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
    if (confirm != DialogResult.Yes)
    {
        return;
    }
    services.Media.DownloadCoordinator.CancelAll();
}
```

托盘关闭主窗口不触发退出，下载继续。

- [ ] **Step 1: 写失败测试**

```csharp
static void TestMediaServicesLifecycle()
{
    var state = new MediaStateStore(new AppPaths(Path.Combine(Path.GetTempPath(), "TransforTests", Guid.NewGuid().ToString("N"))));
    var http = new HttpClient();
    var download = new MediaDownloadCoordinator(new FakeDownloadServiceSucceed(), state, 3);
    var services = new MediaServices
    {
        State = state,
        ResolveCoordinator = new MediaResolveCoordinator(new MediaResolverRegistry(Array.Empty<IMediaResolver>())),
        DownloadCoordinator = download,
        HttpClient = http,
    };
    services.Dispose();
    services.Dispose();
    AssertEqual(true, true, "media services disposes twice without throwing");
}
```

- [ ] **Step 2: 运行验证红** → 编译失败。
- [ ] **Step 3: 实现组合改动**（本任务结束时页面数组含媒体页，托盘/热键/历史面板行为不变）。
- [ ] **Step 4: 运行验证绿**（`All 49 tests passed.`，基数 `+ 35`）；构建通过。
- [ ] **Step 5: 提交**

```powershell
git add src/Transfor/App tests/Transfor.Tests/Program.cs
git commit -m "feat: compose media services and exit rules"
```

### Task 12: 抖音静态解析

**Files:**
- Create: `src/Transfor/Features/MediaDownload/Resolvers/Douyin/DouyinMediaResolver.cs`
- Create: `src/Transfor/Features/MediaDownload/Resolvers/Douyin/DouyinHttpPageResolver.cs`
- Create: `src/Transfor/Features/MediaDownload/Resolvers/Douyin/DouyinPageParser.cs`
- Create: `src/Transfor/Features/MediaDownload/Resolvers/Douyin/DouyinMediaNormalizer.cs`
- Create: `src/Transfor/Features/MediaDownload/Resolvers/DirectMediaResolver.cs`
- Create: `tests/Transfor.Tests/Fixtures/MediaDownload/` 五个样本
- Modify: `src/Transfor/App/AppBootstrapper.cs`（注册中心接入 resolver）
- Test: `tests/Transfor.Tests/Program.cs`

**Interfaces / 行为：**

```csharp
internal sealed record DouyinAssetCandidate(
    int OrderIndex,
    string Url,
    string? ContentType,
    int? Width,
    int? Height,
    long? ContentLength,
    MediaVariantSource Source);

internal sealed record DouyinPageData(
    string? PostId,
    string? Title,
    string? AuthorName,
    IReadOnlyList<DouyinAssetCandidate> ImageCandidates,
    IReadOnlyList<DouyinAssetCandidate> VideoCandidates,
    bool EmptyShell,
    bool LoginRequired);

internal static class DouyinPageParser
{
    public static DouyinPageData Parse(string html);
}

internal static class DouyinMediaNormalizer
{
    public static ResolvedMediaPost Normalize(Uri sourceUri, DouyinPageData data);
}
```

`DouyinPageParser` 解析优先级：`RENDER_DATA` 型结构化 JSON（`System.Text.Json` DOM 遍历）→ 内嵌状态 JSON → `application/ld+json` → DOM 中 `<img src>`/`<video src>` 字符串定位扫描（不用正则作为唯一方案）→ 封面/缩略图兜底。空壳判定：无任何候选；登录判定：HTML 含固定登录/验证码特征标记。`DouyinMediaNormalizer`：按 `OrderIndex` 归组为 `MediaAsset`（图片顺序即原作品顺序）、去除完全相同的候选 URL、过滤头像/Logo/推荐封面关键字（常量表：`avatar`、`icon`、`logo`、`recommend`、`cover` 仅保留作为兜底来源）、不按文件扩展名判断媒体类型、变体保留全部元数据与 `RequestContext(Referer: sourceUri, BrowserSessionId: null)`。

`DouyinHttpPageResolver`（`IMediaResolver`，`Provider=Douyin`，`CanResolve`：host 为 `douyin.com`/`www.douyin.com`/`v.douyin.com`/`iesdouyin.com` 或其子域）：`RedirectResolver` 解分享短链 → GET 最终页（UA 头）→ `DouyinPageParser.Parse` → `EmptyShell`/`LoginRequired` 抛 `BrowserResolutionRequiredException` → 否则 `Normalize` 返回。

`DirectMediaResolver`（兜底，`Provider=Direct`，`CanResolve` 恒 true）：GET 探测（`ResponseHeadersRead`，立即释放）Content-Type，`image/*`/`video/*` 则构造单资源 post，否则抛 `NotSupportedException("该链接不是可直接下载的图片或视频。")`。

- [ ] **Step 1: 创建脱敏 fixture 样本**（合成内容，非真实网页缓存；不含任何真实 Cookie/账号数据）

`tests/Transfor.Tests/Fixtures/MediaDownload/douyin-video-page.html`：

```html
<!DOCTYPE html>
<html><head><title>视频作品</title>
<script id="RENDER_DATA" type="application/json">{"aweme_detail":{"aweme_id":"7123456789012345678","desc":"测试视频标题","author":{"nickname":"测试作者"},"video":{"play_addr":{"uri":"v1","url_list":["https://p3-sign.douyinpic.com/video/high/abc.mp4","https://p3-sign.douyinpic.com/video/low/abc.mp4"],"width":1920,"height":1080,"bit_rate":[[2000000,null,null]],"fps":30},"cover":{"uri":"cover","url_list":["https://p3-sign.douyinpic.com/cover/thumb.webp"]},"download_addr":{"uri":"dl","url_list":["https://p3-sign.douyinpic.com/video/dl/abc.mp4"],"width":1280,"height":720}}}}</script>
<script>window._SSR_DATA = {"login":true,"nickname":"测试作者"}</script>
</head><body>
<img src="https://p3-sign.douyinpic.com/aweme-avatar/123.jpeg" alt="avatar">
<div class="recommend"><img src="https://p3-sign.douyinpic.com/recommend/x.webp"></div>
<video src="https://p3-sign.douyinpic.com/video/dom/abc.mp4"></video>
</body></html>
```

`tests/Transfor.Tests/Fixtures/MediaDownload/douyin-image-carousel-page.html`：

```html
<!DOCTYPE html>
<html><head><title>图文作品</title>
<script id="RENDER_DATA" type="application/json">{"aweme_detail":{"aweme_id":"7123456789012345000","desc":"九图测试","author":{"nickname":"作者B"},"images":[{"url_list":["https://p3-sign.douyinpic.com/img/1.webp"],"width":1440,"height":1920},{"url_list":["https://p3-sign.douyinpic.com/img/2.webp"],"width":1440,"height":1920},{"url_list":["https://p3-sign.douyinpic.com/img/3.webp"],"width":1080,"height":1440},{"url_list":["https://p3-sign.douyinpic.com/img/4.webp"],"width":1080,"height":1440},{"url_list":["https://p3-sign.douyinpic.com/img/5.webp"],"width":1080,"height":1440},{"url_list":["https://p3-sign.douyinpic.com/img/6.webp"],"width":1080,"height":1440},{"url_list":["https://p3-sign.douyinpic.com/img/7.webp"],"width":1080,"height":1440},{"url_list":["https://p3-sign.douyinpic.com/img/8.webp"],"width":1080,"height":1440},{"url_list":["https://p3-sign.douyinpic.com/img/9.webp","https://p3-sign.douyinpic.com/img/9-2.webp"],"width":1080,"height":1440}]}}</script>
</head><body><img src="https://p3-sign.douyinpic.com/logo/icon.png"></body></html>
```

`tests/Transfor.Tests/Fixtures/MediaDownload/douyin-empty-shell.html`：

```html
<!DOCTYPE html>
<html><head><title>作品</title></head><body><div id="root"></div></body></html>
```

`tests/Transfor.Tests/Fixtures/MediaDownload/douyin-login-required.html`：

```html
<!DOCTYPE html>
<html><head><title>登录验证</title></head><body>
<div class="captcha-verify" data-verify-type="slide"></div>
<div>请先登录后再查看该内容</div>
</body></html>
```

`tests/Transfor.Tests/Fixtures/MediaDownload/douyin-structured-data.json`：

```json
{"aweme_detail":{"aweme_id":"7000000000000000000","desc":"浏览器捕获样本","author":{"nickname":"样本作者"},"video":{"play_addr":{"uri":"v","url_list":["https://cdn.example.com/video/1.mp4"],"width":1280,"height":720}}}}
```

- [ ] **Step 2: 写失败测试**

```csharp
static string ReadFixture(string name) =>
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MediaDownload", name));
```

（在 `Transfor.Tests.csproj` 中把 `Fixtures\**` 设为 `CopyToOutputDirectory`。）

```csharp
static void TestDouyinPageParser()
{
    var video = DouyinPageParser.Parse(ReadFixture("douyin-video-page.html"));
    AssertEqual("7123456789012345678", video.PostId, "video post id");
    AssertEqual("测试视频标题", video.Title, "video title");
    AssertEqual("测试作者", video.AuthorName, "video author");
    AssertEqual(true, !video.EmptyShell && !video.LoginRequired, "video page not shell or login");
    AssertEqual(1, video.VideoCandidates.Count, "video candidates grouped");
    AssertEqual(true, video.VideoCandidates[0].Url.Contains("high"), "highest variant present");

    var carousel = DouyinPageParser.Parse(ReadFixture("douyin-image-carousel-page.html"));
    AssertEqual(9, carousel.ImageCandidates.Count, "nine images found");
    for (var i = 0; i < 9; i++) AssertEqual(i + 1, carousel.ImageCandidates[i].OrderIndex, $"image {i + 1} order preserved");

    var shell = DouyinPageParser.Parse(ReadFixture("douyin-empty-shell.html"));
    AssertEqual(true, shell.EmptyShell, "empty shell detected");

    var login = DouyinPageParser.Parse(ReadFixture("douyin-login-required.html"));
    AssertEqual(true, login.LoginRequired, "login required detected");
}

static void TestDouyinMediaNormalizer()
{
    var video = DouyinMediaNormalizer.Normalize(
        new Uri("https://www.douyin.com/video/7123456789012345678"),
        DouyinPageParser.Parse(ReadFixture("douyin-video-page.html")));
    AssertEqual(MediaProviderId.Douyin, video.Provider, "provider douyin");
    AssertEqual(1, video.Assets.Count, "single video asset");
    AssertEqual(MediaKind.Video, video.Assets[0].Kind, "asset kind video");
    AssertEqual(true, video.Assets[0].Variants.Count >= 2, "multiple variants kept");
    AssertEqual(true, !video.Assets[0].Variants.Any(v => v.Uri.ToString().Contains("avatar") || v.Uri.ToString().Contains("recommend")), "avatar and recommend filtered");
    AssertEqual(true, video.Assets[0].Variants.Any(v => v.Uri.ToString().Contains("dl")), "download addr kept as variant");

    var carousel = DouyinMediaNormalizer.Normalize(
        new Uri("https://www.douyin.com/note/7123456789012345000"),
        DouyinPageParser.Parse(ReadFixture("douyin-image-carousel-page.html")));
    AssertEqual(9, carousel.Assets.Count, "nine image assets in order");
    for (var i = 0; i < 9; i++) AssertEqual(i + 1, carousel.Assets[i].Index, $"asset index {i + 1}");
    AssertEqual(true, !carousel.Assets.Any(a => a.Variants.Any(v => v.Uri.ToString().Contains("logo"))), "logo filtered");
    AssertEqual(2, carousel.Assets[8].Variants.Count, "duplicate url variants grouped under same asset");
}

static void TestDirectMediaResolver()
{
    var handler = new StubHttpMessageHandler(
        (_, _) => { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) }; r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg"); return r; });
    var resolver = new DirectMediaResolver(new HttpClient(handler));
    var post = resolver.ResolveAsync(new Uri("https://cdn.example.com/1.jpg"), new MediaRequestContext(null, null), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(1, post.Assets.Count, "direct image becomes single asset");
    AssertEqual(MediaKind.Image, post.Assets[0].Kind, "direct image kind");

    var htmlHandler = new StubHttpMessageHandler(
        (_, _) => { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) }; r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html"); return r; });
    AssertThrows<NotSupportedException>(() => new DirectMediaResolver(new HttpClient(htmlHandler)).ResolveAsync(new Uri("https://cdn.example.com/page"), new MediaRequestContext(null, null), CancellationToken.None).GetAwaiter().GetResult(), "html direct link rejected");
}
```

- [ ] **Step 3: 运行验证红** → 编译失败（fixture 已建、类型缺失）。
- [ ] **Step 4: 实现解析器与归一化器**；`AppBootstrapper` 注册中心接入 `DouyinMediaResolver`（含 `DouyinHttpPageResolver` 与内部 `HttpClient`）与 `DirectMediaResolver`。
- [ ] **Step 5: 运行验证绿**（`All 52 tests passed.`，基数 `+ 38`）；构建通过；`rg -n "Cookie|Token|password" tests/Transfor.Tests/Fixtures` 无命中。
- [ ] **Step 6: 提交**

```powershell
git add src/Transfor/Features/MediaDownload/Resolvers tests/Transfor.Tests/Fixtures tests/Transfor.Tests/Transfor.Tests.csproj tests/Transfor.Tests/Program.cs src/Transfor/App/AppBootstrapper.cs
git commit -m "feat: add douyin static page resolution"
```

### Task 13: WebView2 浏览器兜底

**Files:**
- Modify: `src/Transfor/Transfor.csproj`（`dotnet add src/Transfor package Microsoft.Web.WebView2`，锁定实现时的最新稳定版）
- Create: `src/Transfor/Platform/Windows/WebView/WebView2EnvironmentProvider.cs`
- Create: `src/Transfor/Platform/Windows/WebView/WebView2BrowserSession.cs`
- Create: `src/Transfor/Platform/Windows/WebView/DouyinBrowserForm.cs`
- Create: `src/Transfor/Features/MediaDownload/Resolvers/Douyin/DouyinBrowserPageResolver.cs`
- Modify: `src/Transfor/App/AppBootstrapper.cs`、`src/Transfor/App/TransforApplicationContext.cs`
- Test: `tests/Transfor.Tests/Program.cs`

**行为：**
- `WebView2EnvironmentProvider`：`CoreWebView2Environment.CreateAsync(null, paths.WebView2Directory)`（独立用户数据目录；Cookie/权限/缓存只存此处，不写入 Transfor 普通 JSON）。
- `WebView2BrowserSession : IBrowserCaptureService, ICookieSource, IDisposable`：`CaptureAsync(uri, interactive, ct)` → 导航 → 等待加载完成（登录/验证码时 `interactive=true` 显示 `DouyinBrowserForm` 等待用户自行操作，不自动识别或绕过验证码）→ 订阅 `WebResourceResponseReceived` 只收集 `image/*`/`video/*` 响应为 `BrowserCapturedCandidate` → `ExecuteScriptAsync` 取 `RENDER_DATA` 型结构化状态与轮播图顺序 → 返回 `BrowserCaptureResult`；`GetCookiesAsync(uri)` 经 `CookieManager.GetCookiesAsync` 且过滤域名。未检测到 WebView2 Runtime 时返回 `BrowserCaptureResult(Error: "未检测到 WebView2 Runtime。")`。
- `DouyinBrowserPageResolver`：`Provider=Douyin`，`CanResolve` 同抖音域；`CaptureAsync(StructuredDataJson)` → `DouyinPageParser.Parse` → 网络候选与 DOM 顺序交叉验证 → `DouyinMediaNormalizer.Normalize`。
- 创建时机：`TransforApplicationContext` 持有 `BrowserSessionHolder`（记录是否已创建），仅在 UI 线程（STA）首次「浏览器登录」或解析兜底时创建；不使用 `Task.Run(() => WebView2...)`。
- `MediaResolveCoordinator` 的浏览器兜底路径在任务 9 已预留参数，此处接入真实 `WebView2BrowserSession`；页面「浏览器登录」按钮调用 `ResolveWithBrowserAsync`（协调器新增方法，交互式打开浏览器）。

- [ ] **Step 1: 写失败测试（不依赖 WebView2 运行时）**

```csharp
file sealed class FakeBrowserCapture : IBrowserCaptureService
{
    public Task<BrowserCaptureResult> CaptureAsync(Uri pageUri, bool interactive, CancellationToken cancellationToken) =>
        Task.FromResult(new BrowserCaptureResult(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MediaDownload", "douyin-structured-data.json")),
            Array.Empty<BrowserCapturedCandidate>(), false, null));
}
```

```csharp
static void TestDouyinBrowserPageResolver()
{
    var resolver = new DouyinBrowserPageResolver(new FakeBrowserCapture());
    var post = resolver.ResolveAsync(new Uri("https://www.douyin.com/video/7000000000000000000"), new MediaRequestContext(null, "browser-session-1"), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual("浏览器捕获样本", post.Title, "browser capture title");
    AssertEqual(1, post.Assets.Count, "browser capture asset count");
    AssertEqual(MediaKind.Video, post.Assets[0].Kind, "browser capture kind");
    AssertEqual("browser-session-1", post.Assets[0].Variants[0].RequestContext.BrowserSessionId, "session id carried, not cookies");
}
```

- [ ] **Step 2: 运行验证红** → 编译失败。
- [ ] **Step 3: 添加包**：`dotnet add src/Transfor package Microsoft.Web.WebView2`
- [ ] **Step 4: 实现 WebView 三文件、浏览器解析器、协调器接入与页面「浏览器登录」按钮**。
- [ ] **Step 5: 运行验证绿**（`All 53 tests passed.`，基数 `+ 39`；全部单测在无 WebView2 Runtime 环境下仍通过）；构建通过。
- [ ] **Step 6: 提交**

```powershell
git add src/Transfor/Platform/Windows/WebView src/Transfor/Features/MediaDownload/Resolvers/Douyin/DouyinBrowserPageResolver.cs src/Transfor/Transfor.csproj tests/Transfor.Tests/Program.cs
git commit -m "feat: add webview2 browser fallback for douyin"
```

### Task 14: 收尾验证与文档

**Files:**
- Modify: `README.md`（功能列表加「媒体下载」；目录树补 `Features/MediaDownload`、`Infrastructure/Networking`、`Platform/Windows/WebView`；状态文件列表加 `media-settings.json` 与 `download-history.json`；使用说明加媒体页操作步骤与「最高质量」定义）
- 验证：全量构建、测试、静态检查

- [ ] **Step 1: 全量回归**

Run: `dotnet build Transfor.slnx` → Expected: 0 错误 0 警告
Run: `dotnet run --project tests/Transfor.Tests` → Expected: 34 个原有测试 + 全部新增测试通过

- [ ] **Step 2: 静态检查**

```powershell
rg -n "\.Result|\.Wait\(" src/Transfor   # 应无命中
rg -n "async void" src/Transfor          # 仅 UI 事件处理器允许
rg -n "DouyinMediaResolver" src/Transfor/Features/MediaDownload/UI   # 应无命中
rg -n "Cookie|Authorization|Token" tests/Transfor.Tests/Fixtures    # 应无命中
git diff --check
```

- [ ] **Step 3: 手动验收**（按以下清单逐项验证）

1. 原有引号转换、去空格正常；`Alt+Q` 历史面板与粘贴正常。
2. 托盘生命周期正常；文本历史不受媒体功能影响。
3. 主窗口可在「文本转换」与「媒体下载」间切换。
4. 分享文本可提取短链接；解析单视频与多图作品。
5. 多图数量与页面一致、顺序一致；头像/相关推荐不误收为作品媒体。
6. 视频可流式下载；解析与下载期间 UI 不冻结。
7. 取消下载后无 `.part` 残留；失败有明确提示且可重试。
8. 登录或验证码出现时显示浏览器窗口、用户自行操作，不伪装成功。
9. 删除、私密或失效作品显示明确错误。
10. 托盘下关闭主窗口 → 下载继续；真正退出且有任务 → 提示确认后取消退出。
11. 媒体设置与下载历史正确持久化于 `media-settings.json` / `download-history.json`；WebView2 数据仅存在于 `%LOCALAPPDATA%\Transfor\WebView2\Douyin`。

- [ ] **Step 4: 提交**

```powershell
git add README.md
git commit -m "docs: document media download feature and verification"
```

## 最终验证

- [ ] `git log --oneline -14` 按顺序出现 14 个交付提交。
- [ ] `dotnet build Transfor.slnx` 与 `dotnet run --project tests/Transfor.Tests` 均成功；`git status --short` 干净。
- [ ] 按 Task 14 Step 3 清单人工验收完成。

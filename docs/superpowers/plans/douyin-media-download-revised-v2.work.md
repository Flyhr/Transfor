# 抖音媒体下载功能实施计划（修订版 v2）

> **For agentic workers:** REQUIRED SUB-SKILL: Use `subagent-driven-development`（推荐）或 `executing-plans` 按任务逐项实施。每个任务必须使用 `- [ ]` 复选框跟踪；每个提交完成后都要运行完整测试与解决方案构建。禁止跨任务提前引用尚未创建的类型。

**Goal:** 在现有 `.NET 10 + WinForms` 应用中新增独立的「媒体下载」功能页，支持粘贴抖音分享文本、解析单视频/多图作品、选择并流式下载当前会话可访问的最高质量媒体、队列进度、取消与人工重试，同时保持文本转换功能完全隔离。

**Architecture:** 保持单项目，按「应用外壳 → 统一媒体契约 → 独立持久化 → 安全网络 → 通用下载器 → 队列与页面 → 抖音静态解析 → WebView2 兜底」推进。`MediaResolveCoordinator` 只负责选择解析器和统一错误边界；抖音的静态解析与浏览器兜底均封装在 `DouyinMediaResolver` 内部。媒体状态使用独立的 `media-settings.json` 与 `download-history.json`，不进入 `TextStateStore`。

**Tech Stack:** `.NET 10`、WinForms、`System.Text.Json`、`System.Net.Http`、自定义 `HttpMessageHandler`、无测试框架控制台测试项目；WebView2 阶段固定使用 `Microsoft.Web.WebView2 1.0.4078.44`，升级必须单独提交并重新执行回归测试。

---

## 一、修订版关键决策

1. **统一解析返回契约**：`IMediaResolver.ResolveAsync` 与 `MediaResolveCoordinator.ResolveAsync` 均返回 `MediaResolveResult`；统一使用 `MediaResolveStatus.RequiresUserInteraction` 和 `MediaResolveResult.RequiresUserInteraction(...)` 表示需要浏览器，不通过异常表达。
2. **平台隔离**：`MediaResolveCoordinator` 不引用 `DouyinPageParser`、`DouyinMediaNormalizer`、WebView2 或任何抖音类型。
3. **可恢复业务状态**：解析结果使用 `MediaResolveStatus` 枚举，禁止 `Succeeded=true/Post=null` 之类的非法布尔组合。
4. **安全网络异步化**：DNS 校验使用可注入的 `IDnsResolver`；测试禁止真实 DNS 和真实抖音访问。
5. **所有 URL 都重新校验**：分享链接、每次重定向、静态页面地址、媒体候选地址和下载重定向均通过安全校验。
6. **媒体候选采用“资产—变体”两层模型**：一张图片或一个视频是一个 `MediaAsset`，不同质量 URL 是多个 `MediaVariant`。
7. **浏览器会话延迟注入**：下载器和抖音解析器依赖 `IBrowserSessionAccessor`，不直接持有 WebView2 控件；仅在用户首次选择浏览器解析时，在 UI STA 线程初始化 WebView2。
8. **取消为异步操作**：队列提供 `CancelAllAsync`，应用退出时先等待任务落定，再在主窗体仍存活的 UI 线程消息循环中释放 WebView2，最后释放 `HttpClient`。
9. **不静默覆盖文件**：目标文件内容不同则生成唯一文件名；内容相同才判定幂等成功。
10. **UI 使用 `DataGridView`**：资源列表和下载队列需要复选框、按钮、状态和进度，不使用不适合嵌入交互控件的 `ListView`。
11. **测试计数自动累计**：删除手工维护的 `+20/+21/...` 测试数量常量。
12. **第一版不处理 DASH/HLS 合并**：发现分段媒体时给出明确提示，不引入 FFmpeg。

## 执行规格粒度

本计划采用“**任务规格 + 高风险代码锚点**”的混合形式：

- 普通模型、目录和简单 UI 任务保持清单式，避免计划膨胀为整份实现源码。
- Task 1、Task 7、Task 11、Task 12 等高风险任务必须包含可编译的关键代码骨架、fixture 最小示例和明确验证命令。
- AI 实施时不得把代码锚点当成伪代码；若实际仓库签名不同，必须先更新计划中的接口，再修改实现。
- 每个 Task 开始前先运行基线测试；结束后运行下述三连验证，不允许跨 Task 留下编译失败状态。

---

## 二、全局约束

- 保持一个 `src/Transfor` WinForms 项目和一个 `tests/Transfor.Tests` 控制台测试项目；不拆分新类库。
- 媒体功能不得进入 `TextToolId`、`HistoryEntry`、`ITextHistoryRepository`、`PasteCoordinator` 或 `TextStateStore`。
- 媒体下载不参与 `Alt+Q` 文本历史面板，不执行剪贴板自动粘贴。
- 不绕过登录、验证码、访问控制；不破解或伪造平台私有签名；不实现水印移除。
- 不使用硬编码 Cookie；Cookie、Token、Authorization 和临时授权信息不得写入普通 JSON、日志、测试样本或仓库。
- WebView2 数据只保存于 `%LOCALAPPDATA%\Transfor\WebView2\Douyin`。
- 自动化测试禁止访问真实网络；DNS、HTTP、浏览器捕获全部使用 Fake/Stub。
- `MediaDownloadPage` 不引用任何具体平台解析器。
- `DouyinMediaResolver` 不负责文件保存；`MediaDownloadService` 不包含抖音页面规则。
- 禁止 `.Result`、`.Wait()` 和 `Task.Run(() => WebView2...)`；仅 WinForms 事件处理器和应用退出事件允许 `async void`。
- 所有网络和长时间文件操作必须接收 `CancellationToken`。
- `MediaStateStore` 的设置与历史写入必须串行化；并发批次不得互相覆盖 JSON。
- 下载使用 `HttpCompletionOption.ResponseHeadersRead`，持续流式计数，单文件默认上限 `4 GiB`。
- 临时文件格式为 `<目标文件>.part.<TaskId>`；失败或取消必须清除。
- “最高质量”指当前公开页面或当前浏览器会话可访问的、可直接下载的最高质量变体；不承诺作者原始文件、无水印或受限内容。
- `Balanced` 必须有确定排序规则；仅有 DASH/HLS 分段候选时，选择器返回明确的“分段媒体不支持”结果，而不是返回可下载的 `MediaVariant`。
- 每个任务结束必须满足：

```powershell
dotnet run --project tests/Transfor.Tests
dotnet build Transfor.slnx
git diff --check
```

---

## 三、目标目录

```text
src/Transfor/
├─ App/
│  ├─ AppBootstrapper.cs
│  ├─ AppPaths.cs
│  ├─ AppServices.cs
│  ├─ MediaServices.cs
│  └─ TransforApplicationContext.cs
├─ Shell/
│  ├─ IFeaturePage.cs
│  └─ MainForm.cs
├─ Features/
│  ├─ TextTools/
│  ├─ History/
│  ├─ Settings/
│  └─ MediaDownload/
│     ├─ Contracts/
│     ├─ Models/
│     ├─ Application/
│     ├─ Services/
│     ├─ Resolvers/
│     │  ├─ DirectMediaResolver.cs
│     │  └─ Douyin/
│     └─ UI/
├─ Infrastructure/
│  ├─ Networking/
│  └─ Persistence/
└─ Platform/Windows/
   └─ WebView/

tests/Transfor.Tests/
├─ Program.cs
└─ Fixtures/MediaDownload/
```

---

# Task 1：泛化 MainForm、清理重复保存并改造测试计数

**Files**

- Modify: `src/Transfor/Shell/MainForm.cs`
- Modify: `src/Transfor/App/TransforApplicationContext.cs`
- Modify: `src/Transfor/Features/TextTools/UI/TextToolsPage.cs`
- Modify: `src/Transfor/Features/History/UI/HistoryPanelForm.cs`
- Modify: `src/Transfor/Features/Settings/UI/SettingsForm.cs`
- Modify: `tests/Transfor.Tests/Program.cs`

## 接口与行为

```csharp
internal MainForm(IReadOnlyList<IFeaturePage> pages)
```

- 构造函数首先校验 `pages` 非空，再创建导航和内容区。
- 根据页面集合动态创建导航按钮，不再直接构造 `TextToolsPage`。
- 页面切换调用 `page.OnActivated()`。
- 状态修改方法内部已落盘，UI 不再额外调用 `Save()`。
- 当前 `TextStateStore.UpdateSettings` 的跨文件原子性不是媒体功能的一部分；本任务不得扩大为新的状态迁移重构，但必须保证异常仍向 UI 返回，且内存状态不被无提示破坏。
- `Add`、`SetLastViewedTool`、`UpdateSettings` 和 `ClearHistory` 的异常必须在调用它们的 UI 事件内捕获并显示；不能先执行变更、再在另一个 `Save()` 调用中捕获。
- `TextStateStore.UpdateSettings` 在任一写入失败时恢复原内存设置和历史裁剪结果，并向调用方抛出原异常；测试覆盖“写入失败后内存设置仍为旧值”。

## 测试计数改造

删除：

```csharp
Console.WriteLine($"All {quoteCases.Length + spaceCases.Length + 20} tests passed.");
```

增加统一计数：

```csharp
static int passed;

static void AssertEqual<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected={expected}, actual={actual}");

    passed++;
}
```

`AssertThrows` 等断言成功后也必须递增 `passed`。最后仅输出：

```csharp
Console.WriteLine($"All {passed} tests passed.");
```

## Steps

- [ ] 增加“修改方法无需显式 Save 仍持久化”的回归测试。
- [ ] 将 `MainForm` 改为接收页面集合。
- [ ] `TransforApplicationContext` 暂时传入仅含 `TextToolsPage` 的集合。
- [ ] 删除 `TextToolsPage.Add()` 后的全量 `Save()`。
- [ ] 删除 `HistoryPanelForm.SetLastViewedTool()` 后的全量 `Save()`。
- [ ] 删除 `SettingsForm.UpdateSettings/ClearHistory()` 后的全量 `Save()`。
- [ ] 删除快捷键回退流程中的重复 `Save()`。
- [ ] 改造自动测试计数器。
- [ ] 运行全部测试和构建。

## Commit

```powershell
git add src/Transfor tests/Transfor.Tests/Program.cs
git commit -m "重构：泛化功能外壳并在变更时持久化"
```

---

# Task 2：建立统一媒体模型与解析契约

**Files**

- Create: `src/Transfor/Features/MediaDownload/Models/*.cs`
- Create: `src/Transfor/Features/MediaDownload/Contracts/*.cs`
- Test: `tests/Transfor.Tests/Program.cs`

## 枚举

```csharp
internal enum MediaProviderId { Direct, Douyin }
internal enum MediaKind { Image, Video }
internal enum MediaVariantSource
{
    StructuredData,
    InlineState,
    JsonLd,
    Dom,
    NetworkCapture,
    Thumbnail
}
internal enum MediaQualityPreference { Highest, Balanced }
internal enum MediaResolveStatus
{
    Succeeded,
    RequiresUserInteraction,
    Unsupported,
    Failed
}
internal enum MediaResolveMode
{
    Automatic,
    BrowserInteractive
}
internal enum MediaDownloadStatus
{
    Succeeded,
    Failed,
    Cancelled
}
```

## 核心模型

```csharp
internal sealed record MediaRequestContext(
    Uri? Referer,
    string? BrowserSessionId);

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
    MediaRequestContext RequestContext,
    bool IsSegmented = false);

internal sealed record MediaAsset(
    int Index,
    MediaKind Kind,
    IReadOnlyList<MediaVariant> Variants);

internal sealed record ResolvedMediaPost(
    MediaProviderId Provider,
    Uri SourceUri,
    string? PostId,
    string? Title,
    string? AuthorName,
    IReadOnlyList<MediaAsset> Assets);

internal sealed record MediaResolveRequest(
    Uri SourceUri,
    MediaResolveMode Mode,
    MediaRequestContext RequestContext);
```

## 统一解析结果

不得继续使用：

```csharp
bool Succeeded
bool RequiresUserInteraction
```

使用单一状态枚举和受控工厂：

```csharp
internal sealed record MediaResolveResult
{
    private MediaResolveResult(
        MediaResolveStatus status,
        ResolvedMediaPost? post,
        string? message)
    {
        Status = status;
        Post = post;
        Message = message;
    }

    public MediaResolveStatus Status { get; }
    public ResolvedMediaPost? Post { get; }
    public string? Message { get; }

    public static MediaResolveResult Success(ResolvedMediaPost post) =>
        new(MediaResolveStatus.Succeeded,
            post ?? throw new ArgumentNullException(nameof(post)),
            null);

    public static MediaResolveResult RequiresUserInteraction(string message) =>
        new(MediaResolveStatus.RequiresUserInteraction, null, message);

    public static MediaResolveResult Unsupported(string message) =>
        new(MediaResolveStatus.Unsupported, null, message);

    public static MediaResolveResult Failure(string message) =>
`MediaResolveResult` 是运行时结果，不参与 JSON 持久化；“模型 JSON 往返”测试只覆盖明确标记为可持久化的记录。`Success` 还必须拒绝空资产列表、空变体列表和非 HTTP/HTTPS 变体 URI，避免出现“成功但无法下载”的状态。
实现时使用以下校验骨架，不把校验留给 UI：

```csharp
private static void ValidatePost(ResolvedMediaPost post)
{
    if (post.Assets.Count == 0)
        throw new InvalidDataException("作品没有可下载资产。");

    foreach (var asset in post.Assets)
    {
        if (asset.Variants.Count == 0)
            throw new InvalidDataException("资产没有可下载变体。");

        foreach (var variant in asset.Variants)
        {
            if (variant.Uri.Scheme is not ("http" or "https"))
                throw new InvalidDataException("媒体 URI 协议不安全。");
        }
    }
}
```
        new(MediaResolveStatus.Failed, null, message);
}
```

## 统一解析契约

```csharp
internal interface IMediaResolver
{
    MediaProviderId Provider { get; }
    bool CanResolve(Uri sourceUri);

    Task<MediaResolveResult> ResolveAsync(
        MediaResolveRequest request,
        CancellationToken cancellationToken);
}
```

`IMediaResolver` 和 `MediaResolveCoordinator` 必须返回同一 `MediaResolveResult`。正常的“需要浏览器”不得抛异常。

## 下载与浏览器契约

```csharp
internal interface IMediaDownloadService
{
    Task<MediaDownloadResult> DownloadAsync(
        MediaDownloadTask task,
        CancellationToken cancellationToken,
        IProgress<MediaDownloadProgress>? progress = null);
}

internal interface IMediaDownloadHistoryRepository
{
    IReadOnlyList<MediaDownloadHistoryEntry> GetHistory();
    void Add(MediaDownloadHistoryEntry entry);
}

internal interface IBrowserSessionAccessor : IAsyncDisposable
{
    bool IsAvailable { get; }

    Task<BrowserCaptureResult> CaptureAsync(
        Uri pageUri,
        bool interactive,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
        string browserSessionId,
        Uri requestUri,
        CancellationToken cancellationToken);
}
```

## 浏览器捕获模型

```csharp
internal enum BrowserCandidateSource { Dom, Network }
internal enum BrowserCaptureStatus
{
    Succeeded,
    RequiresUserInteraction,
    Unavailable,
    Failed
}

internal sealed record BrowserCapturedCandidate(
    Uri Uri,
    MediaKind? Kind,
    int? OrderIndex,
    int? Width,
    int? Height,
    string? ContentType,
    long? ContentLength,
    BrowserCandidateSource Source);

internal sealed record BrowserCaptureResult(
    string? BrowserSessionId,
    string? StructuredDataJson,
    string? DomSnapshotJson,
    IReadOnlyList<BrowserCapturedCandidate> Candidates,
    BrowserCaptureStatus Status,
    string? Error);

internal sealed record BrowserCookie(
    string Domain,
    string Path,
    string Name,
    string Value,
    bool Secure);
```

## 下载模型

```csharp
internal sealed record MediaDownloadSettings(
    string DownloadDirectory,
    int MaxConcurrentDownloads,
    bool DefaultSelectAll,
    bool OpenFolderAfterDownload,
    MediaQualityPreference QualityPreference)
{
    public const int MinimumConcurrency = 1;
    public const int MaximumConcurrency = 8;
    public const int DefaultConcurrency = 3;
    public const bool DefaultSelectAllValue = true;
    public const bool DefaultOpenFolderAfterDownload = false;
    public const MediaQualityPreference DefaultQualityPreference =
        MediaQualityPreference.Highest;

    public static MediaDownloadSettings CreateDefault(
        string fallbackDirectory,
        string? downloadsDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackDirectory);

        downloadsDirectory ??= Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile),
            "Downloads");

        string directory = Directory.Exists(downloadsDirectory)
            ? Path.GetFullPath(downloadsDirectory)
            : Path.GetFullPath(fallbackDirectory);

        return new MediaDownloadSettings(
            directory,
            DefaultConcurrency,
            DefaultSelectAllValue,
            DefaultOpenFolderAfterDownload,
            DefaultQualityPreference);
    }

    public void Validate()
    {
        if (MaxConcurrentDownloads is < MinimumConcurrency
            or > MaximumConcurrency)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentDownloads),
                "并发数必须在 1 到 8 之间。");
        }

        if (string.IsNullOrWhiteSpace(DownloadDirectory))
        {
            throw new ArgumentException(
                "下载目录不能为空。",
                nameof(DownloadDirectory));
        }

        if (!Enum.IsDefined(QualityPreference))
        {
            throw new ArgumentOutOfRangeException(
                nameof(QualityPreference));
        }
    }
}

internal sealed record MediaDownloadTask(
    Guid Id,
    MediaAsset Asset,
    MediaVariant SelectedVariant,
    string TargetPath);

internal sealed record MediaDownloadProgress(
    Guid TaskId,
    long BytesDownloaded,
    long? TotalBytes,
    double? Percent)
{
    public static MediaDownloadProgress Create(
        Guid taskId,
        long bytesDownloaded,
        long? totalBytes)
    {
        double? percent = totalBytes is > 0
            ? Math.Min(100d,
                bytesDownloaded * 100d / totalBytes.Value)
            : null;

        return new MediaDownloadProgress(
            taskId,
            bytesDownloaded,
            totalBytes,
            percent);
    }
}

internal sealed record MediaDownloadResult(
    Guid TaskId,
    MediaDownloadStatus Status,
    string? Error,
    string? SavedPath)
{
    public static MediaDownloadResult Success(
        Guid taskId,
        string savedPath) =>
        new(taskId, MediaDownloadStatus.Succeeded, null, savedPath);

    public static MediaDownloadResult Failed(
        Guid taskId,
        string error) =>
        new(taskId, MediaDownloadStatus.Failed, error, null);

    public static MediaDownloadResult Cancelled(Guid taskId) =>
        new(taskId, MediaDownloadStatus.Cancelled, null, null);
}

internal sealed record MediaDownloadHistoryEntry(
    MediaProviderId Provider,
    string SourceShareLink,
    string? Title,
    string? SavedDirectory,
    IReadOnlyList<string> SavedFiles,
    int SuccessCount,
    int FailureCount,
    int CancelledCount,
    DateTimeOffset DownloadedAtUtc);
```

## Tests

- [ ] `MediaResolveResult.Success` 必须包含非空 `Post`。
- [ ] `MediaResolveResult.RequiresUserInteraction` 的 `Post` 必须为空；统一解析状态命名，不再出现 `RequiresInteraction`。
- [ ] 可持久化记录 JSON 往返正常；`MediaResolveResult`、浏览器捕获和 Cookie 模型不参与持久化测试。
- [ ] `CreateDefault` 优先使用用户 `Downloads` 目录；测试通过 `downloadsDirectory` 参数注入存在/不存在的临时目录，验证缺失时回退 `AppPaths.ApplicationDirectory`，不得依赖测试机器真实目录状态。
- [ ] 默认值固定为：并发 3、默认全选 `true`、下载后打开目录 `false`、质量策略 `Highest`；并发范围仅允许 1–8。
- [ ] `BrowserSessionId` 可保存于请求上下文，但模型中不存在 Cookie 字段。
- [ ] 分段变体可通过 `IsSegmented=true` 表达。

## Commit

```powershell
git add src/Transfor/Features/MediaDownload tests/Transfor.Tests/Program.cs
git commit -m "功能：新增统一媒体模型与契约"
```

---

# Task 3：媒体独立持久化

**Files**

- Create: `src/Transfor/Infrastructure/Persistence/JsonFileStore.cs`
- Create: `src/Transfor/Infrastructure/Persistence/MediaStateStore.cs`
- Modify: `src/Transfor/App/AppPaths.cs`
- Test: `tests/Transfor.Tests/Program.cs`

## Paths

```csharp
public string MediaSettingsFile =>
    Path.Combine(ApplicationDirectory, "media-settings.json");

public string MediaDownloadHistoryFile =>
    Path.Combine(ApplicationDirectory, "download-history.json");

public string WebView2Directory =>
    Path.Combine(ApplicationDirectory, "WebView2", "Douyin");
```

## Interfaces

```csharp
internal static class JsonFileStore
{
    public static void Write<T>(string path, T value);
    public static T? TryRead<T>(string path);
}

internal sealed class MediaStateStore : IMediaDownloadHistoryRepository
{
    public static MediaStateStore Load(AppPaths paths);

    public MediaDownloadSettings Settings { get; }
    public void UpdateSettings(MediaDownloadSettings settings);
    public IReadOnlyList<MediaDownloadHistoryEntry> GetHistory();
    public void Add(MediaDownloadHistoryEntry entry);
}
```

统一使用 `Load`，不要同时存在公开构造函数和 `Load` 两套入口。

## Persistence rules

- 新文件不存在时调用 `MediaDownloadSettings.CreateDefault(paths.ApplicationDirectory)`，不迁移旧文本状态。
- JSON 使用 `Document<T>(SchemaVersion: 1, Value)` 外壳。
- 写入使用同目录随机临时文件，再替换正式文件。
- 损坏或未知版本文件回退默认值，但不得覆盖损坏原文件。
- `MediaDownloadSettings.Validate` 必须要求下载目录为绝对路径；加载时使用规范化后的完整路径。
- `MediaStateStore` 以私有锁或单写入队列串行化 `UpdateSettings`、`Add` 和 `GetHistory` 的快照；并发批次测试必须证明没有历史丢失。
- 下载历史默认保留最近 200 个批次；超出后裁剪最旧记录。
- 下载历史不得包含 CDN 临时 URL、Cookie、Token、Authorization 或页面 HTML。

## Tests

- [ ] 缺失文件返回默认设置和空历史。
- [ ] `UpdateSettings`、`Add` 无需额外 `Save` 即持久化。
- [ ] 损坏 JSON 回退默认值。
- [ ] 未知 `schemaVersion` 不按版本 1 解析。
- [ ] 原始 JSON 中不出现敏感字段名称。
- [ ] 历史超过 200 条后仅保留最新 200 条。
- [ ] 并发调用 `Add` 与 `UpdateSettings` 后 JSON 仍是完整文档，历史条目没有互相覆盖。
- [ ] 相对下载目录和越界目标路径被拒绝。

## Commit

```powershell
git add src/Transfor/Infrastructure/Persistence src/Transfor/App/AppPaths.cs tests/Transfor.Tests/Program.cs
git commit -m "功能：新增独立媒体状态持久化"
```

---

# Task 4：ShareLinkParser、解析注册中心与统一协调器

**Files**

- Create: `src/Transfor/Features/MediaDownload/Services/ShareLinkParser.cs`
- Create: `src/Transfor/Features/MediaDownload/Application/MediaResolverRegistry.cs`
- Create: `src/Transfor/Features/MediaDownload/Application/MediaResolveCoordinator.cs`
- Test: `tests/Transfor.Tests/Program.cs`

## ShareLinkParser

```csharp
internal static class ShareLinkParser
{
    public static Uri? TryExtractFirstLink(
        string? text,
        out string? error);
}
```

规则：

- 从每个 `http://` 或 `https://` 起点依次扫描候选片段；候选 URI 无效时继续寻找下一个候选。
- 取至空白符。
- 清理结尾中文/英文标点。
- 仅接受绝对 HTTP/HTTPS URI。
- 一次只返回第一个有效链接；若输入包含多个链接，后续链接不得影响返回结果。
- 不访问网络。

## Registry

```csharp
internal sealed class MediaResolverRegistry
{
    public MediaResolverRegistry(IReadOnlyList<IMediaResolver> resolvers);

    public bool TryGetResolver(
        Uri sourceUri,
        out IMediaResolver? resolver);
}
注册中心必须拒绝重复 `Provider`，并按“专用解析器优先、Direct 作为最后兜底”的确定顺序匹配。`CanResolve` 只允许做无网络的 URI 判断；注册顺序必须在 `AppBootstrapper` 中显式测试，避免 Direct resolver 抢走抖音链接。
```

## Coordinator

```csharp
internal sealed class MediaResolveCoordinator
{
    public MediaResolveCoordinator(MediaResolverRegistry registry);

    public async Task<MediaResolveResult> ResolveAsync(
        MediaResolveRequest request,
        CancellationToken cancellationToken);
}
```

行为：

1. 无匹配解析器：返回 `MediaResolveResult.Unsupported("暂不支持该链接。")`。
2. 找到解析器：原样返回解析器结果。
3. `OperationCanceledException` 且调用方取消：继续抛出。
4. 其他未预期异常：转换为 `MediaResolveResult.Failure(...)`。
5. 禁止引用抖音解析器或浏览器实现。

## Tests

- [ ] 分享文本提取短链接。
- [ ] 中文句号和 ASCII 标点清理。
- [ ] 无链接返回明确错误。
- [ ] 注册中心首个匹配解析器生效。
- [ ] 无解析器返回 `Unsupported`，不抛业务异常。
- [ ] Fake Resolver 返回 `MediaResolveStatus.RequiresUserInteraction` 时协调器原样返回。
- [ ] 取消仍表现为取消，不转换成 `Failed`。

## Commit

```powershell
git add src/Transfor/Features/MediaDownload/Application src/Transfor/Features/MediaDownload/Services/ShareLinkParser.cs tests/Transfor.Tests/Program.cs
git commit -m "功能：新增媒体解析协调与链接提取"
```

---

# Task 5：异步安全网络原语

**Files**

- Create: `src/Transfor/Infrastructure/Networking/IDnsResolver.cs`
- Create: `src/Transfor/Infrastructure/Networking/SystemDnsResolver.cs`
- Create: `src/Transfor/Infrastructure/Networking/SafeUriValidator.cs`
- Create: `src/Transfor/Infrastructure/Networking/SafeHttpRequestSender.cs`
- Create: `src/Transfor/Infrastructure/Networking/HttpClientProvider.cs`
- Create: `src/Transfor/Infrastructure/Networking/HttpResponseMetadataReader.cs`
- Test: `tests/Transfor.Tests/Program.cs`

## DNS contract

```csharp
internal interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(
        string host,
        CancellationToken cancellationToken);
}
```

测试使用 `FakeDnsResolver`，不得查询真实 DNS。

## URI validator

```csharp
internal sealed record UriValidationResult(
    bool IsAllowed,
    string? Error);

internal sealed class SafeUriValidator
{
    public SafeUriValidator(IDnsResolver dnsResolver);

    public Task<UriValidationResult> ValidateAsync(
        Uri uri,
        CancellationToken cancellationToken);
}
```

拒绝：

- 非 HTTP/HTTPS；
- `localhost`；
- 回环地址；
- RFC1918 私网；
- 链路本地；
- `0.0.0.0`；
- IPv6 loopback/link-local/unique-local；
- DNS 解析失败；
- 域名解析结果中任一地址属于禁止范围。

## HttpClient provider

```csharp
internal static class HttpClientProvider
{
    public static HttpClient Create();
}
```

必须使用：

```csharp
new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseCookies = false,
    AutomaticDecompression =
        DecompressionMethods.GZip |
        DecompressionMethods.Deflate |
        DecompressionMethods.Brotli
};
```

不要给 `HttpClient` 设置不存在的 `AllowAutoRedirect` 属性。

## 安全请求发送器

```csharp
internal sealed class SafeHttpRequestSender
{
    public const int DefaultMaxRedirects = 5;

    public SafeHttpRequestSender(
        HttpClient client,
        SafeUriValidator validator);

    public Task<HttpResponseMessage> SendAsync(
        Uri initialUri,
        Func<Uri, CancellationToken, Task<HttpRequestMessage>> requestFactory,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken,
        int maxRedirects = DefaultMaxRedirects);
}
```

行为：

- 初始 URI 和每个重定向 URI 都重新校验。
- 每一跳都重新调用 `requestFactory`，Cookie 仅针对当前目标域重新获取。
- 中间响应必须及时释放。
- 301/302/303 对非 GET 请求按明确策略转 GET；本项目解析/下载均使用 GET。
- 307/308 保持方法。
- 超过 5 跳返回明确错误。
- 不自动将 Cookie、Authorization 等敏感头转发到新域名。
`requestFactory` 只能设置明确允许的请求头（当前为 `Referer` 和当前 URI 匹配的 Cookie）；`SafeHttpRequestSender` 必须拒绝或清除 `Authorization`、代理认证和其他敏感头。跨源重定向时默认删除 `Referer`，不得把原始分享页地址泄露到不相关域名。

## Tests

- [ ] IP 字面量私网/回环/链路本地被拒绝。
- [ ] Fake DNS 返回私网 IP 时域名被拒绝。
- [ ] Fake DNS 返回公网 IP 时允许。
- [ ] 相对重定向正确合并。
- [ ] 每次重定向都执行校验。
- [ ] 超过重定向上限失败。
- [ ] 含用户信息的 URI、跨源 Referer 泄露和敏感请求头转发均被拒绝或清除。
- [ ] 重定向到已知平台页之外的页面不会被 Douyin resolver 当作作品页解析。
- [ ] 测试代码不使用真实 `douyin.com` DNS。

> 注：该方案是桌面客户端的最佳努力防护，不宣称构成服务端级 SSRF 安全边界。

## Commit

```powershell
git add src/Transfor/Infrastructure/Networking tests/Transfor.Tests/Program.cs
git commit -m "功能：新增异步安全网络原语"
```

---

# Task 6：质量选择、内容校验、文件命名与哈希

**Files**

- Create: `src/Transfor/Features/MediaDownload/Services/MediaQualitySelector.cs`
- Create: `src/Transfor/Features/MediaDownload/Services/MediaContentValidator.cs`
- Create: `src/Transfor/Features/MediaDownload/Services/DownloadFileNameBuilder.cs`
- Create: `src/Transfor/Features/MediaDownload/Services/MediaHashService.cs`
- Test: `tests/Transfor.Tests/Program.cs`

## 质量选择

```csharp
internal enum MediaSelectionStatus
{
    Selected,
    UnsupportedSegmented,
    NoUsableVariant
}

internal sealed record MediaSelectionResult(
    MediaSelectionStatus Status,
    MediaVariant? Variant,
    string? Message);

internal static class MediaQualitySelector
{
    public static MediaSelectionResult SelectBest(
        MediaAsset asset,
        MediaQualityPreference preference);
}
```

图片规则：

1. 如果存在非 `Thumbnail` 候选，完全排除缩略图池；只有全部为缩略图时才允许缩略图参与。
2. 来源可信度。
3. 像素面积。
4. 宽度和高度。
5. 内容长度。

`Highest` 按上述指标降序选择；`Balanced` 明确定义为“在非缩略图、非分段候选中优先选择至少 720p 的最高可访问变体，再按像素面积、码率/内容长度排序；不存在 720p 时回退到最高可访问变体”。两种策略都必须使用稳定的 URI 字典序作为最终平局打破规则。

视频规则：

1. 第一版优先 `IsSegmented=false` 的可直接下载文件。
2. 分辨率。
3. 帧率。
4. 码率。
5. 编码兼容性。
6. 内容长度。

若只有分段候选，返回 `MediaSelectionStatus.UnsupportedSegmented` 和明确消息供 UI 显示“不支持合并”；不得伪装成可下载单文件。

## 内容校验

```csharp
internal static class MediaContentValidator
{
    public const long DefaultMaxFileBytes = 4L * 1024 * 1024 * 1024;

    public static bool IsPlausibleResponse(
        HttpResponseMessage response,
        MediaKind expectedKind,
        long maxBytes,
        out string? error);

    public static Task<bool> HasValidMagicNumberAsync(
        Stream stream,
        MediaKind expectedKind,
        CancellationToken cancellationToken);
}
```

支持至少：JPEG、PNG、GIF、WebP、MP4、WebM/Matroska。读取魔数前保存当前位置，完成后恢复；不假设流当前位置为 0。
对于不可 Seek 的流，先复制不超过所需魔数长度的前缀到固定小缓冲区，不要求恢复位置；对于可 Seek 的流，必须在 `finally` 中恢复原位置。测试同时覆盖两种流。

## 文件命名

```csharp
internal static class DownloadFileNameBuilder
{
    public static string SanitizeFileName(string raw);
    public static string ResolveExtension(string? contentType, MediaKind kind);
    public static string BuildUniquePath(string directory, string fileName);
}
```

- 清理非法字符、尾部空格和句点。
- 限制文件名主体长度。
- 保留扩展名。
- 重名使用 `(1)`、`(2)`。
`BuildUniquePath` 只能生成候选名，不能单独保证并发安全。最终保存必须以独占创建/原子移动方式预留目标；发生竞态时重新生成 `(1)`、`(2)`，不得覆盖其他任务文件。下载器还必须验证最终目标路径位于用户选择的目录内。

## 哈希

```csharp
internal static class MediaHashService
{
    public static Task<string> ComputeSha256Async(
        Stream stream,
        CancellationToken cancellationToken);
}
```

不得将 4 GiB 文件读入内存。

## Tests

- [ ] 超大缩略图不会压过真实作品候选。
- [ ] 同分辨率视频按帧率/码率排序。
- [ ] 分段候选被识别但不作为第一版直接下载对象。
- [ ] 魔数正确识别并恢复流位置。
- [ ] ZIP/HTML 伪装媒体被拒绝。
- [ ] 非法文件名和重复文件名处理正确。
- [ ] 哈希流式计算。
- [ ] `Balanced` 的 720p 回退规则和最终平局规则确定。
- [ ] 不可 Seek 流的魔数校验不抛异常。
- [ ] 两个并发任务抢占同一文件名时均得到不同的最终路径。
- [ ] 绝对路径和 `..` 越界目标路径被拒绝。

## Commit

```powershell
git add src/Transfor/Features/MediaDownload/Services tests/Transfor.Tests/Program.cs
git commit -m "功能：新增媒体选择校验与文件工具"
```

---

# Task 7：安全流式下载服务

**Files**

- Create: `src/Transfor/Features/MediaDownload/Services/MediaDownloadService.cs`
- Create: `src/Transfor/Features/MediaDownload/Services/BrowserCookieMatcher.cs`
- Test: `tests/Transfor.Tests/Program.cs`

## Constructor

```csharp
internal sealed class MediaDownloadService : IMediaDownloadService
{
    public MediaDownloadService(
        SafeHttpRequestSender requestSender,
        IBrowserSessionAccessor? browserSessions,
        long maxFileBytes = MediaContentValidator.DefaultMaxFileBytes);
}
```

## Cookie rules

Cookie 只有在以下条件全部满足时才发送：

- `BrowserSessionId` 非空；
- Cookie domain 与请求 host 完全相等，或请求 host 是该 domain 的真实子域；
- `Secure=true` 时请求必须为 HTTPS；
- Cookie path 匹配；
- Domain 比较先移除可选前导点，并要求 `host == domain || host.EndsWith("." + domain, OrdinalIgnoreCase)`；Path 按 RFC 6265 的目录边界匹配，`/foo` 不匹配 `/foobar`。
- 不得依据 Referer 域名决定 Cookie。

示例：

- `.example.com` 可发送到 `cdn.example.com`；
- `douyin.com` 不得发送到 `cdn.example.com`；
- `evil.com` 永远不得发送到 `douyin.com`。

## 下载流程

1. 为当前跳转 URI 构建新请求。
2. 添加 `Referer`。
3. 根据当前 URI 从浏览器会话获取匹配 Cookie。
4. 通过 `SafeHttpRequestSender` 发送，所有重定向重新校验并重新获取 Cookie。
5. 检查 HTTP 状态、Content-Type 和声明长度。
6. 写入：

```text
<TargetPath>.part.<TaskId:N>
```

7. 每次读取后累计字节，超过上限立即取消并清理临时文件，即使没有 `Content-Length`。
8. 下载完成后重新打开临时文件，校验魔数和哈希。
9. 目标不存在：移动到目标。
10. 目标存在且哈希相同：删除临时文件，返回幂等成功。
11. 目标存在但内容不同：调用 `BuildUniquePath`，不得静默覆盖。
12. 失败和取消清理临时文件。
13. `SavedPath` 返回最终实际保存路径。
- `TargetPath` 必须是用户选择目录下的规范化路径；下载器创建目录前先验证路径包含关系。
- 完成字节写入后先检查取消，再进入不可取消的最终校验/原子移动区间；一旦最终移动开始，结果只能是成功或明确失败，不能返回“已取消但文件已保存”。
- `SafeHttpRequestSender` 负责释放每一跳由 `requestFactory` 创建的 `HttpRequestMessage`，避免重定向时请求对象泄漏。

## 关键实现锚点

下面骨架用于锁定安全链路、流式上限和临时文件生命周期；实现不得退化为直接 `HttpClient.GetAsync`：

```csharp
public async Task<MediaDownloadResult> DownloadAsync(
    MediaDownloadTask task,
    CancellationToken cancellationToken,
    IProgress<MediaDownloadProgress>? progress = null)
{
    string partPath = $"{task.TargetPath}.part.{task.Id:N}";

    try
    {
        using HttpResponseMessage response =
            await requestSender.SendAsync(
                task.SelectedVariant.Uri,
                (currentUri, token) => BuildRequestAsync(
                    currentUri,
                    task.SelectedVariant.RequestContext,
                    token),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

        if (!MediaContentValidator.IsPlausibleResponse(
                response,
                task.Asset.Kind,
                maxFileBytes,
                out string? validationError))
        {
            return MediaDownloadResult.Failed(
                task.Id,
                validationError ?? "媒体响应无效。");
        }

        await using Stream source =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream destination = new(
            partPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 32 * 1024,
            useAsync: true);

        byte[] buffer = new byte[32 * 1024];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(
                buffer.AsMemory(),
                cancellationToken);
            if (read == 0) break;

            total += read;
            if (total > maxFileBytes)
                throw new InvalidDataException("文件大小超过限制。");

            await destination.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
            progress?.Report(MediaDownloadProgress.Create(
                task.Id, total, response.Content.Headers.ContentLength));
        }

        await destination.FlushAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return await FinalizeDownloadAsync(
            task, partPath, CancellationToken.None);
    }
    catch (OperationCanceledException)
        when (cancellationToken.IsCancellationRequested)
    {
        TryDelete(partPath);
        return MediaDownloadResult.Cancelled(task.Id);
    }
    catch (Exception ex)
    {
        TryDelete(partPath);
        return MediaDownloadResult.Failed(task.Id, ex.Message);
    }
}
```

`BuildRequestAsync` 只为当前跳转 URI 创建新的 GET 请求、添加 Referer，并按 `BrowserSessionId + currentUri` 异步获取 Cookie；URL 校验和每跳重定向校验由 `SafeHttpRequestSender.SendAsync` 统一执行。`FinalizeDownloadAsync` 必须重新打开临时文件完成魔数、哈希、幂等和唯一命名处理。

## Tests

使用 Stub Handler/Fake Browser Session，覆盖：

- [ ] 200 成功。
- [ ] 404、429、500 失败。
- [ ] 错误 Content-Type 失败。
- [ ] 声明长度超限失败。
- [ ] chunked 实际读取超限失败。
- [ ] 流中途抛 `IOException` 后 `.part` 清理。
- [ ] 取消测试必须使用 `CancelAfter` 或可控 TCS，禁止永久等待。
- [ ] Referer 正确。
- [ ] Cookie 仅发送到匹配域。
- [ ] 每次重定向重新获取 Cookie。
- [ ] 相同目标内容幂等成功。
- [ ] 不同目标内容生成唯一文件名。
- [ ] 魔数校验前正确重置/重新打开流。
- [ ] 取消发生在最终移动前会清理 `.part` 并返回 `Cancelled`。
- [ ] 取消发生在最终移动开始后不会返回“已取消但文件已保存”。
- [ ] `TargetPath` 越出用户选择目录时拒绝。
- [ ] Cookie Path 的目录边界、前导点 Domain 和跨源 Referer 均有测试。

## 本任务验证

```powershell
dotnet run --project tests/Transfor.Tests
dotnet build Transfor.slnx
git diff --check
```

## Commit

```powershell
git add src/Transfor/Features/MediaDownload/Services tests/Transfor.Tests/Program.cs
git commit -m "功能：新增安全流式媒体下载服务"
```

---

# Task 8：下载队列协调器

**Files**

- Create: `src/Transfor/Features/MediaDownload/Application/MediaDownloadCoordinator.cs`
- Create: `src/Transfor/Features/MediaDownload/Models/MediaDownloadTaskCompleted.cs`
- Test: `tests/Transfor.Tests/Program.cs`

## Interface

```csharp
internal sealed record MediaDownloadBatch(
    Guid Id,
    string SourceShareLink,
    ResolvedMediaPost Post,
    IReadOnlyList<MediaDownloadTask> Tasks);

```
`MediaDownloadTaskCompleted.cs` 必须定义为：

```csharp
internal sealed record MediaDownloadTaskCompleted(
    Guid BatchId,
    Guid TaskId,
    MediaDownloadResult Result);
```

```csharp
internal sealed class MediaDownloadCoordinator : IDisposable
{
    public MediaDownloadCoordinator(
        IMediaDownloadService downloadService,
        MediaStateStore stateStore);

    public bool HasActiveTasks { get; }

    public event EventHandler<MediaDownloadProgress>? TaskProgressChanged;
    public event EventHandler<MediaDownloadTaskCompleted>? TaskCompleted;

    public Task EnqueueBatchAsync(
        MediaDownloadBatch batch,
        CancellationToken cancellationToken);

    public void CancelTask(Guid taskId);

    public Task CancelAllAsync(
        CancellationToken cancellationToken = default);

    public void Dispose();
}
```

## Behavior

- 批次串行化：协调器内部维护批次队列，同一时间仅一个活动批次，后续批次排队执行，严格保证全局并发不超过设置上限。
- 每个批次开始时读取 `stateStore.Settings.MaxConcurrentDownloads` 作为该批次 `SemaphoreSlim` 容量，设置修改对下一个批次生效。
- 每个任务使用独立且与批次 Token 链接的 `CancellationTokenSource`。
- `MediaDownloadBatch` 是唯一的来源链接、作品和任务集合；不要同时把这些字段复制到 `MediaDownloadTask`。
- `MediaDownloadTaskCompleted` 必须包含 `TaskId`、`MediaDownloadResult` 和批次 ID，事件顺序为“任务结果落定后再移除活动任务”。
- 历史写入通过 `MediaStateStore` 的串行写入入口完成；多个批次同时结束时每一条历史都必须保留。
- 任务字典使用线程安全结构。
- 事件可能在后台线程触发；UI 必须通过 `BeginInvoke` 更新控件，不依赖构造时捕获 `SynchronizationContext`。
- 所有任务落定后写一条下载历史，分别统计成功、失败和取消。
- 协调器不自动重试；UI 重试时创建新的任务 ID 并重新入队。
- `CancelAllAsync` 必须等待全部活动任务结束。
- `Dispose` 幂等；退出流程必须先 `CancelAllAsync` 再 `Dispose`。

## Tests

不得使用固定 `Task.Delay(300)` 推测并发状态。使用可控的 `TaskCompletionSource` 和带超时的等待条件。

- [ ] 4 个任务、并发上限 3 时确认峰值**等于** 3。
- [ ] 单任务取消不影响其他任务。
- [ ] `CancelAllAsync` 返回后 `HasActiveTasks=false`。
- [ ] 成功/失败/取消计数正确。
- [ ] 全部失败时历史成功数为 0。
- [ ] 设置并发数修改后下一个批次生效。
- [ ] 两个批次排队依次结束时历史各写一条且互不覆盖。
- [ ] `MediaDownloadBatch` 中的来源链接/作品与任务集合不会出现重复参数不一致。
- [ ] 任务完成事件包含批次 ID 和最终 `SavedPath`。

## Commit

```powershell
git add src/Transfor/Features/MediaDownload/Application src/Transfor/Features/MediaDownload/Models tests/Transfor.Tests/Program.cs
git commit -m "功能：新增可取消媒体下载队列"
```

---

# Task 9：DirectMediaResolver 与媒体页面 MVP

**Files**

- Create: `src/Transfor/Features/MediaDownload/Resolvers/DirectMediaResolver.cs`
- Create: `src/Transfor/Features/MediaDownload/UI/MediaDownloadPage.cs`
- Create: `src/Transfor/Features/MediaDownload/UI/MediaAssetGrid.cs`
- Create: `src/Transfor/Features/MediaDownload/UI/DownloadQueueGrid.cs`
- Test: `tests/Transfor.Tests/Program.cs`

## Direct resolver

```csharp
internal sealed class DirectMediaResolver : IMediaResolver
{
    public MediaProviderId Provider => MediaProviderId.Direct;

    public Task<MediaResolveResult> ResolveAsync(
        MediaResolveRequest request,
        CancellationToken cancellationToken);
}
```

- 使用 `SafeHttpRequestSender` 获取响应头。
- `image/*` 转为单图片资产。
- `video/*` 转为单视频资产。
- HTML 或未知内容返回 `Unsupported`，不抛业务异常。
- `CanResolve` 只做无网络 URI 形态判断；已知 `douyin.com`/`iesdouyin.com` 页面域名必须返回 `false`，Direct resolver 作为最后兜底注册在专用 resolver 之后。真正安全校验在发送器中执行。
- 只有 `MediaSelectionStatus.Selected` 且 `Variant` 非空时才创建 `MediaDownloadTask`；`UnsupportedSegmented` 和 `NoUsableVariant` 只更新 UI 状态。

## 页面状态

```csharp
internal enum MediaPageState
{
    Idle,
    Resolving,
    WaitingForUser,
    Resolved,
    Downloading,
    Completed,
    Failed
}
```

```csharp
internal sealed class MediaDownloadPage : UserControl, IFeaturePage
{
    public MediaDownloadPage(
        MediaResolveCoordinator resolveCoordinator,
        MediaDownloadCoordinator downloadCoordinator,
        MediaStateStore stateStore);
    public string Id => "media-download";
    public string DisplayName => "媒体下载";
    public Control View => this;
    public void OnActivated();
}
```

Task 10 将构造函数扩展为：

```csharp
public MediaDownloadPage(
    MediaResolveCoordinator resolveCoordinator,
    MediaDownloadCoordinator downloadCoordinator,
    MediaStateStore stateStore,
    Func<Control, ValueTask> ensureBrowserInitializedAsync);
```

## UI requirements

页面必须完整包含以下区域：

- 分享输入行：分享文本/链接输入框，以及「从剪贴板粘贴」「解析」「浏览器登录」「清空」按钮。
- 作品信息区：平台、标题、作者、媒体数量。
- 资产表：使用 `DataGridView`，包含勾选、序号、类型、尺寸、预计大小、质量状态和预览按钮占位。
- 保存目录行：可编辑或只读目录框，以及「选择目录」按钮；初始值取 `stateStore.Settings.DownloadDirectory`。页面级选择仅影响当前及后续批次，不自动改写默认设置；修改默认目录由 Task 13 的媒体设置入口负责。
- 批量操作行：「全选」「取消全选」「下载所选」。
- 下载表：使用 `DataGridView`，包含文件名、进度、状态、取消/重试按钮。

「从剪贴板粘贴」只读取文本，不自动开始解析；剪贴板读取失败时在页面显示错误，不使程序退出。选择目录使用 `FolderBrowserDialog`，用户取消时保留原目录。
- `Resolving`：禁用解析、浏览器、下载。
- `WaitingForUser`：**启用浏览器登录按钮**，禁用普通解析和下载。
- `Downloading`：禁用再次解析和批量选择，但允许取消任务。
- UI 事件处理器允许 `async void`，内部调用必须带 Token。
- 页面不引用 `DirectMediaResolver`、`DouyinMediaResolver` 或 WebView2 类型。
- 此任务结束时，直接图片/视频 URL 已可解析和下载；抖音链接显示“暂未接入解析器”或 `Unsupported`。
- Task 9 的自动测试可以直接调用解析器和队列；在 Task 10 完成组合根之前，媒体页不要求从真实应用入口手动访问。

## Tests

- [ ] Direct 图片返回单资产。
- [ ] Direct 视频返回单资产。
- [ ] Direct HTML 返回 `Unsupported`。
- [ ] 页面实现 `IFeaturePage`。
- [ ] `WaitingForUser` 状态允许浏览器按钮。
- [ ] 资产表和队列表使用 `DataGridView`。
- [ ] 页面包含「从剪贴板粘贴」按钮和保存目录选择行。
- [ ] 保存目录初始值来自 `MediaDownloadSettings.DownloadDirectory`。

## Commit

```powershell
git add src/Transfor/Features/MediaDownload/Resolvers src/Transfor/Features/MediaDownload/UI tests/Transfor.Tests/Program.cs
git commit -m "功能：新增直接媒体解析与下载页初版"
```

---

# Task 10：服务组合与异步退出生命周期

**Files**

- Create: `src/Transfor/App/MediaServices.cs`
- Create: `src/Transfor/Features/MediaDownload/Application/BrowserSessionAccessorProxy.cs`
- Modify: `src/Transfor/App/AppBootstrapper.cs`
- Modify: `src/Transfor/App/AppServices.cs`
- Modify: `src/Transfor/App/TransforApplicationContext.cs`
- Test: `tests/Transfor.Tests/Program.cs`

## Browser proxy

```csharp
internal sealed class BrowserSessionAccessorProxy : IBrowserSessionAccessor
{
    public bool IsAvailable { get; }
    public void Attach(IBrowserSessionAccessor accessor);
    // 未 Attach 时 Capture 返回明确 unavailable 结果，GetCookies 返回空集合。
}
```

下载服务和未来的抖音解析器始终依赖 Proxy，避免 Task 12 引入 WebView2 后重新构造整个依赖图。

## MediaServices

```csharp
internal sealed class MediaServices : IDisposable, IAsyncDisposable
{
    public required MediaStateStore State { get; init; }
    public required MediaResolveCoordinator ResolveCoordinator { get; init; }
    public required MediaDownloadCoordinator DownloadCoordinator { get; init; }
    public required BrowserSessionAccessorProxy BrowserSessions { get; init; }
    public required HttpClient HttpClient { get; init; }

    public Func<Control, IBrowserSessionAccessor>? BrowserSessionFactory { get; set; }

    public ValueTask EnsureBrowserInitializedAsync(Control uiOwner);
    public ValueTask DisposeAsync();
    public void Dispose();
}
```

- `EnsureBrowserInitializedAsync` 只在首次进入 `BrowserInteractive` 流程时调用一次，并把工厂创建的实现 Attach 到 Proxy；初始化失败时保留 Proxy 的 unavailable 状态并抛出可识别的浏览器不可用异常，由页面转换为 `RequiresUserInteraction`。
- Task 12 之前工厂为空，浏览器能力不可用但 Direct 下载正常。
- `DisposeAsync`：先 `CancelAllAsync`，再释放协调器、浏览器会话，最后释放 `HttpClient`。

## Application lifecycle

`TransforApplicationContext` 创建页面集合：

```csharp
var pages = new IFeaturePage[]
{
    new TextToolsPage(services.State),
    new MediaDownloadPage(
        services.Media.ResolveCoordinator,
        services.Media.DownloadCoordinator,
        services.Media.State,
        services.Media.EnsureBrowserInitializedAsync),
};
```

退出方法改为 `async void`：

1. 有活动任务时询问。
2. 用户取消退出则直接返回。
3. `await CancelAllAsync()`。
4. 在主窗体和 UI 消息循环仍存活时 `await services.DisposeAsync()`，确保 WebView2 在创建它的 STA 线程释放。
5. 关闭历史面板和主窗体。
6. 在 `finally` 中隐藏/释放托盘并结束消息循环；释放异常必须转换为可见错误，不得遗留半退出状态。

关闭主窗口到托盘不取消下载。

## Tests

- [ ] Proxy 未 Attach 时安全返回 unavailable。
- [ ] Proxy Attach 后委托调用。
- [ ] `DisposeAsync` 可重复调用且不抛异常。
- [ ] 有任务时 `CancelAllAsync` 完成后才释放 HttpClient。
- [ ] 浏览器未被使用时不会创建 WebView2。
- [ ] WebView2 已初始化时，退出顺序为取消任务 → 释放浏览器 → 释放 HttpClient → 关闭主窗体。
- [ ] 浏览器运行时缺失时 `EnsureBrowserInitializedAsync` 抛出可识别异常、Proxy 仍为 unavailable，应用仍可使用 Direct 下载。
- [ ] MainForm 同时显示“文本转换”和“媒体下载”导航。

## Commit

```powershell
git add src/Transfor/App src/Transfor/Features/MediaDownload/Application tests/Transfor.Tests/Program.cs
git commit -m "功能：组装媒体服务并完善异步生命周期"
```

---

# Task 11：抖音静态页面解析

**Files**

- Create: `src/Transfor/Features/MediaDownload/Resolvers/Douyin/DouyinCandidateModels.cs`
- Create: `src/Transfor/Features/MediaDownload/Resolvers/Douyin/DouyinPageParser.cs`
- Create: `src/Transfor/Features/MediaDownload/Resolvers/Douyin/DouyinMediaNormalizer.cs`
- Create: `src/Transfor/Features/MediaDownload/Resolvers/Douyin/DouyinHttpPageResolver.cs`
- Create: `src/Transfor/Features/MediaDownload/Resolvers/Douyin/DouyinMediaResolver.cs`
- Modify: `src/Transfor/App/AppBootstrapper.cs`
- Create: `tests/Transfor.Tests/Fixtures/MediaDownload/*`
- Modify: `tests/Transfor.Tests/Transfor.Tests.csproj`
- Test: `tests/Transfor.Tests/Program.cs`

## 候选模型

不得使用“一候选一个 URL”并同时声称候选已分组。使用两层结构：

```csharp
internal sealed record DouyinVariantCandidate(
    string Url,
    string? ContentType,
    int? Width,
    int? Height,
    int? FramesPerSecond,
    long? Bitrate,
    long? ContentLength,
    string? Codec,
    MediaVariantSource Source,
    bool IsSegmented = false);

internal sealed record DouyinAssetCandidate(
    int OrderIndex,
    MediaKind Kind,
    IReadOnlyList<DouyinVariantCandidate> Variants);

internal sealed record DouyinPageData(
    string? PostId,
    string? Title,
    string? AuthorName,
    IReadOnlyList<DouyinAssetCandidate> Assets,
    bool EmptyShell,
    bool LoginRequired,
    string? FailureReason);
```

九张图片必须表现为 9 个 `DouyinAssetCandidate`；第九张的多个 URL 进入该资产的 `Variants`。

## Parser scope

- 仅解析已知的结构化 JSON 容器，例如 `RENDER_DATA`、内嵌状态 JSON、`application/ld+json`。
- 使用字符串扫描定位脚本块，再使用 `System.Text.Json` 解析 JSON。
- 不尝试使用正则实现完整 HTML DOM。
- 找不到结构化作品数据时设置 `EmptyShell=true`，交由浏览器模式处理。
- 页面响应正文必须有独立上限（例如 32 MiB），超过上限立即停止读取并返回 `Failed`；不得把整页无限读入内存。
- HTTP 最终页面 URI 必须仍属于允许的抖音页面域名；CDN 媒体域名只允许作为候选下载 URI，不得作为作品页。
- 解析后按资产顺序去重 URL，并拒绝空变体、非 HTTP/HTTPS URI 和跨页面伪造的头像/Logo 候选。
- 检测登录提示时设置 `LoginRequired=true`。
- 删除、私密、失效作品返回明确 `FailureReason`。

## Douyin resolver contract

```csharp
internal sealed class DouyinMediaResolver : IMediaResolver
{
    public MediaProviderId Provider => MediaProviderId.Douyin;

    public Task<MediaResolveResult> ResolveAsync(
        MediaResolveRequest request,
        CancellationToken cancellationToken);
}
```

`Automatic` 模式：

1. 安全解析短链接和最终作品页。
2. 静态页面解析成功：返回 `Success`。
3. 空壳或需要登录：返回 `RequiresUserInteraction`。
4. 删除/私密/格式错误：返回 `Failed`。

`BrowserInteractive` 模式：

- 如果 Proxy 不可用：返回 `RequiresUserInteraction("当前环境尚未启用浏览器解析。")`。
- Task 12 后调用浏览器捕获并在解析器内部完成归一化。

`MediaResolveCoordinator` 不做任何抖音分支。

## Domain matching

`CanResolve` 必须进行边界正确的后缀判断：

- `douyin.com` 或 `*.douyin.com`
- `iesdouyin.com` 或 `*.iesdouyin.com`

不得把 `evildouyin.com` 当作合法子域。

## Fixtures

最小化、脱敏地提交：

```text
douyin-video-page.html
douyin-image-carousel-page.html
douyin-empty-shell.html
douyin-login-required.html
douyin-removed-page.html
douyin-structured-data.json
```

禁止包含真实 Cookie、Token、账号标识和完整网页缓存。

fixture 必须是手工最小化样本，而不是从真实页面整页保存。多图样本至少包含“资产—变体”关系，例如：

```json
{
  "aweme_id": "fixture-image-post",
  "desc": "fixture title",
  "author": { "nickname": "fixture author" },
  "images": [
    {
      "width": 1080,
      "height": 1440,
      "url_list": [
        "https://media.example/image-01-a.webp",
        "https://media.example/image-01-b.webp"
      ]
    },
    {
      "width": 1440,
      "height": 1920,
      "url_list": [
        "https://media.example/image-02.webp"
      ]
    }
  ]
}
```

对应断言必须验证：2 个图片资产；第 1 个资产包含 2 个变体；顺序为 0、1。所有 fixture URL 使用保留测试域名，不请求网络。
`Transfor.Tests.csproj` 必须将 `Fixtures/MediaDownload/**/*` 标记为 `None` 并设置 `CopyToOutputDirectory=PreserveNewest`，测试只从 `AppContext.BaseDirectory` 读取 fixture。

## Tests

- [ ] 单视频作品解析为 1 个资产、多个变体。
- [ ] 多图作品保持数量和顺序。
- [ ] 同一图片的多个 URL 归入同一资产。
- [ ] 头像、Logo、相关推荐不进入作品资产。
- [ ] 空壳返回 `MediaResolveStatus.RequiresUserInteraction`。
- [ ] 登录页返回 `MediaResolveStatus.RequiresUserInteraction`。
- [ ] 删除作品返回 `Failed`。
- [ ] Automatic/BrowserInteractive 均返回统一 `MediaResolveResult`。
- [ ] `MediaResolveCoordinator` 中无抖音类型引用。

## 本任务验证

```powershell
dotnet run --project tests/Transfor.Tests
dotnet build Transfor.slnx
git diff --check
```

## Commit

```powershell
git add src/Transfor/Features/MediaDownload/Resolvers/Douyin src/Transfor/App/AppBootstrapper.cs tests/Transfor.Tests
git commit -m "功能：新增抖音静态媒体解析"
```

---

# Task 12：WebView2 浏览器会话与抖音兜底

**Files**

- Modify: `src/Transfor/Transfor.csproj`
- Create: `src/Transfor/Platform/Windows/WebView/WebView2EnvironmentProvider.cs`
- Create: `src/Transfor/Platform/Windows/WebView/WebView2BrowserSessionAccessor.cs`
- Create: `src/Transfor/Platform/Windows/WebView/DouyinBrowserForm.cs`
- Create: `src/Transfor/Features/MediaDownload/Resolvers/Douyin/DouyinBrowserCaptureAdapter.cs`
- Create: `src/Transfor/Platform/Windows/WebView/IUiDispatcher.cs`
`IUiDispatcher` 的外部接口固定为：

```csharp
internal interface IUiDispatcher
{
    Task<T> InvokeAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken);
}
```
- Modify: `src/Transfor/App/AppBootstrapper.cs`
- Modify: `src/Transfor/App/MediaServices.cs`
- Test: `tests/Transfor.Tests/Program.cs`

## Package pin

```xml
<PackageReference Include="Microsoft.Web.WebView2" Version="1.0.4078.44" />
```

不得使用浮动版本或“实现时最新稳定版”。

## UI-thread rule

- WebView2 控件、`CoreWebView2`、CookieManager 和相关事件只能在创建它们的 STA UI 线程访问。
- `WebView2BrowserSessionAccessor` 接收一个 WinForms `Control uiOwner`。
- 后台下载线程调用 Cookie API 时，Accessor 内部使用 `uiOwner.BeginInvoke`/封装的异步 UI 调度切回 UI 线程。
- `BeginInvoke` 本身是异步投递；风险来自调用方同步等待一个依赖 UI 回调完成的 Task。所有 Cookie/Capture 调用必须全链路 `await`，UI 线程和后台线程均禁止 `.Result`/`.Wait()`。
- UI 调度器必须检查 `uiOwner.IsDisposed/Disposing/IsHandleCreated`，支持 `CancellationToken`，并处理窗口关闭后 `BeginInvoke` 抛出的异常，返回明确的“浏览器会话不可用”结果，不能无限等待。
- 模态窗口不得通过同步等待阻塞创建 WebView2 的 UI 线程；登录窗体应保持消息循环并通过异步完成信号返回结果。
- 禁止 `Task.Run` 包裹 WebView2。

## UI 调度代码锚点

```csharp
private Task<T> InvokeOnUiAsync<T>(
    Func<CancellationToken, Task<T>> action,
    CancellationToken cancellationToken)
{
    if (uiOwner.IsDisposed || uiOwner.Disposing ||
        !uiOwner.IsHandleCreated)
    {
        throw new InvalidOperationException(
            "浏览器窗口已经关闭。");
    }

    var completion = new TaskCompletionSource<T>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    CancellationTokenRegistration registration =
        cancellationToken.Register(() =>
            completion.TrySetCanceled(cancellationToken));

    try
    {
        uiOwner.BeginInvoke(async () =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                completion.TrySetResult(
                    await action(cancellationToken));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                registration.Dispose();
            }
        });
    }
    catch
    {
        registration.Dispose();
        throw;
    }

    return completion.Task;
}
```

调用方必须 `await InvokeOnUiAsync(...)`，不得在 UI 线程同步等待返回 Task。
将 `Control.BeginInvoke` 封装在可注入的 `IUiDispatcher` 中，测试使用 Fake dispatcher，不创建真实 WebView2。取消时必须同时取消等待中的 completion 和 action 使用的 Token；若控件在投递后销毁，`HandleDestroyed`/取消回调必须让 Task 在有限时间内结束，不能无限挂起。

## Environment

```csharp
CoreWebView2Environment.CreateAsync(
    browserExecutableFolder: null,
    userDataFolder: paths.WebView2Directory);
```

## Capture behavior

- 为每次捕获创建或复用受控浏览器会话 ID。
- 监听 `WebResourceResponseReceived`，仅记录图片/视频候选元数据。
- 使用 `ExecuteScriptAsync` 获取结构化状态和 DOM 轮播顺序。
- `BrowserCaptureResult` 返回：会话 ID、结构化 JSON、DOM 快照、网络候选和交互状态。
- 登录/验证码时显示 `DouyinBrowserForm`，用户自行完成；不自动识别或绕过。
- 未安装 WebView2 Runtime 时返回明确错误，不使程序崩溃。
`CoreWebView2Environment.CreateAsync`、控件初始化和 CookieManager 调用必须捕获 Runtime 缺失、COM 初始化失败和控件已销毁异常，统一转换为 unavailable 结果；不得让 `AppBootstrapper` 或退出流程崩溃。

## Cookie behavior

```csharp
GetCookiesAsync(browserSessionId, requestUri, cancellationToken)
```

- 仅返回目标 URI 匹配的 Cookie。
- Cookie 不记录日志、不持久化到普通 JSON。
- 下载器仍通过 `BrowserCookieMatcher` 做防御性二次过滤。

## Integration

`AppBootstrapper` 设置：

```csharp
services.Media.BrowserSessionFactory = owner =>
    new WebView2BrowserSessionAccessor(owner, paths);
```

浏览器工厂只注入到 `MediaServices`。`MediaDownloadPage` 在用户点击浏览器解析时先 `await EnsureBrowserInitializedAsync(this)`，成功后再通过 Proxy 捕获；应用启动阶段不得创建 WebView2。

`DouyinMediaResolver` 的 `BrowserInteractive` 分支：

1. 调 Proxy `CaptureAsync`。
2. 把结构化 JSON、DOM 顺序和网络候选转换为 `DouyinPageData`。
3. 调 `DouyinMediaNormalizer`。
4. 所有变体携带 `BrowserSessionId`，但不携带 Cookie。

## Tests

自动测试不依赖真实 WebView2 Runtime：

- [ ] Fake Browser Capture 能解析为统一 `MediaResolveResult.Success`。
- [ ] BrowserSessionId 进入媒体请求上下文。
- [ ] Cookie 获取通过 UI 调度抽象调用。
- [ ] Fake `IUiDispatcher` 覆盖取消、控件销毁和投递失败，所有等待均在超时内结束。
- [ ] WebView2 Runtime 缺失、COM 初始化失败和 UI owner 已释放时均返回 unavailable。
- [ ] Runtime 不可用返回明确失败。
- [ ] Fake 捕获的 DOM 顺序与网络候选可交叉归组。
- [ ] 测试样本无敏感字段。

## Commit

```powershell
git add src/Transfor/Platform/Windows/WebView src/Transfor/Features/MediaDownload/Resolvers/Douyin src/Transfor/App src/Transfor/Transfor.csproj tests/Transfor.Tests/Program.cs
git commit -m "功能：新增 WebView2 抖音浏览器兜底"
```

---

# Task 13：媒体设置、预览、重试与交互加固

**Files**

- Create: `src/Transfor/Features/MediaDownload/UI/MediaSettingsForm.cs`
- Create: `src/Transfor/Features/MediaDownload/Services/MediaPreviewService.cs`
- Create: `src/Transfor/Features/MediaDownload/UI/MediaPreviewControl.cs`
- Modify: `src/Transfor/Features/MediaDownload/UI/MediaDownloadPage.cs`
- Modify: `src/Transfor/Features/MediaDownload/UI/MediaAssetGrid.cs`
- Modify: `src/Transfor/Features/MediaDownload/UI/DownloadQueueGrid.cs`
- Test: `tests/Transfor.Tests/Program.cs`

## Settings

提供实际用户入口，不能只创建 `media-settings.json` 而无 UI：

- 默认下载目录；
- 最大并发 1–8；
- 默认全选；
- 下载后打开目录；
- `Highest/Balanced`。

并发设置对下一个新批次生效。媒体设置窗体修改默认下载目录后，`MediaDownloadPage` 在没有用户临时覆盖当前目录时同步刷新；用户已在页面级选择目录时，不强制覆盖当前批次目录。

## Preview

- 图片预览使用独立 `MediaPreviewService`，最大预览文件 50 MiB。
- `MediaPreviewService` 必须依赖与正式下载相同的 `SafeHttpRequestSender` 和 `IBrowserSessionAccessor`，预览 URL、每次重定向、Cookie 匹配均走同一安全链路；不得直接调用裸 `HttpClient`。
- 预览使用 `ResponseHeadersRead` 流式写入 `%TEMP%\Transfor\PreviewCache\<SessionId>`，每次预览使用随机会话子目录，持续计数并支持取消；失败或取消清理临时文件，应用启动和服务释放时清理过期缓存。
- 不写下载历史，不进入正式队列。
- `Image.FromStream` 后必须复制为独立 Bitmap，避免底层流关闭导致显示失败。
- 预览必须同时检查 Content-Type、50 MiB 声明/实际大小和图片魔数；不得把 HTML、ZIP 或伪装响应交给 `Image.FromStream`。
- 视频第一版显示元数据，不自动播放。

## Retry

- 失败行提供 `DataGridViewButtonColumn` 重试。
- 重试生成新 TaskId。
- 每次重试生成新的 `MediaDownloadBatch` 和 TaskId，调用 `EnqueueBatchAsync(batch, token)`；批次落定后写入一条新的下载历史，不得回写或修改原批次历史。
- 不覆盖原失败状态，原失败行保留；新任务新增独立队列行。
- 浏览器会话失效时提示重新浏览器解析。

## UI state corrections

- `Resolving`：浏览器按钮禁用。
- `WaitingForUser`：浏览器按钮启用。
- `Downloading`：解析和批量选择禁用，取消按钮启用。
- `Completed/Failed`：恢复基础操作。
- UI 更新通过 `BeginInvoke`，避免跨线程操作控件。

## Segmented media

当 `MediaQualitySelector` 返回 `MediaSelectionStatus.UnsupportedSegmented` 时显示：

> 已发现更高质量的分段媒体流，但当前版本暂不支持合并。

不得创建无法完成的下载任务。

## Tests

- [ ] 媒体设置持久化并从 UI 读取。
- [ ] `WaitingForUser` 浏览器按钮可用。
- [ ] 分段媒体不进入下载任务。
- [ ] 重试使用新 TaskId，并作为新批次写入独立历史条目。
- [ ] 预览不写下载历史。
- [ ] 预览请求使用 `SafeHttpRequestSender`，私网地址与不安全重定向被拒绝。
- [ ] 预览取消清理临时文件。
- [ ] 页面级目录选择存在，且不会在用户取消 `FolderBrowserDialog` 时清空原值。
- [ ] 预览使用独立会话缓存目录，过期缓存可清理且并发预览不会互相删除。
- [ ] 预览 Content-Type、魔数、声明大小和 chunked 实际大小均校验。
- [ ] 用户选择目录之外的目标路径不会进入下载任务。

## Commit

```powershell
git add src/Transfor/Features/MediaDownload tests/Transfor.Tests/Program.cs
git commit -m "功能：加固媒体下载交互体验"
```

---

# Task 14：文档、静态检查与完整验收

**Files**

- Modify: `README.md`
- Verify: 全量代码、测试、状态文件和手动流程

## README

补充：

- 媒体下载功能；
- 目录结构；
- 状态文件列表；
- WebView2 Runtime 要求；
- “最高质量”的准确含义；
- 第一版不支持 DASH/HLS 合并；
- 合法使用和访问限制说明；
- 数据与 Cookie 存储位置。

同时修正 README 开头仍称所有状态保存在单一 `state.json` 的过时描述。

## Static checks

```powershell
rg -n "\.Result|\.Wait\(" src/Transfor
rg -n "Task\.Run\(.*WebView" src/Transfor
rg -n "async void" src/Transfor
rg -n "DouyinMediaResolver|DouyinPageParser|WebView2" src/Transfor/Features/MediaDownload/UI
rg -n "DouyinPageParser|DouyinMediaNormalizer" src/Transfor/Features/MediaDownload/Application
rg -n "Cookie|Authorization|Token|password" tests/Transfor.Tests/Fixtures
rg -n "RequiresInteraction|IsPlausibleMedia|InitializeBrowser" src/Transfor tests/Transfor.Tests
rg -n "MediaDownloadBatch|SavedFiles|IUiDispatcher" src/Transfor tests/Transfor.Tests
git diff --check
```

预期：

- `.Result/.Wait` 无命中；
- `Task.Run(WebView2)` 无命中；
- `async void` 仅 UI/退出事件；
- UI 不引用具体解析器；
- Application 协调器不引用抖音类型；
- fixtures 无敏感字段。
- 不存在旧状态命名 `RequiresInteraction`、旧方法名 `IsPlausibleMedia` 或启动阶段 `InitializeBrowser` 调用。
- 队列、历史和 WebView2 调度使用已定义的 `MediaDownloadBatch`、`SavedFiles`、`IUiDispatcher` 契约。

## Automated verification

```powershell
dotnet build Transfor.slnx
dotnet run --project tests/Transfor.Tests
```

要求：

- 0 error；
- 0 warning；
- 原有测试全部继续通过；
- 新测试全部通过；
- 测试数量由计数器自动输出；
- 无真实网络依赖。

## Manual acceptance

- [ ] 引号转换、去空格不回归。
- [ ] `Alt+Q` 历史面板与自动粘贴正常。
- [ ] 托盘生命周期正常。
- [ ] 主窗口可切换“文本转换/媒体下载”。
- [ ] 直接图片和视频 URL 可下载。
- [ ] 抖音分享文本能提取链接。
- [ ] 单视频解析成功。
- [ ] 多图数量和顺序正确。
- [ ] 头像、Logo 和相关推荐不被误识别。
- [ ] 解析和下载期间 UI 不冻结。
- [ ] 取消后没有 `.part.*` 残留。
- [ ] 目标重名不静默覆盖。
- [ ] 登录/验证码由用户在浏览器窗口中完成。
- [ ] 私密、删除、受限作品显示明确错误。
- [ ] 关闭主窗体到托盘时下载继续。
- [ ] 真正退出时先提示并等待取消完成。
- [ ] Cookie 仅存在 WebView2 UDF，不进入 JSON。
- [ ] `media-settings.json` 和 `download-history.json` 独立于文本状态。
- [ ] 分段媒体显示“不支持合并”，不创建错误任务。
- [ ] WebView2 仅在首次浏览器解析时初始化，Runtime 缺失不影响直接下载。
- [ ] 预览缓存按会话隔离并可清理。
- [ ] 并发下载不会覆盖目标文件，所有最终路径都位于用户选择目录内。

## Commit

```powershell
git add README.md
git commit -m "文档：补充并验收抖音媒体下载"
```

---

# 最终交付门禁

- [ ] 14 个提交均可独立构建，任一提交不引用后续任务类型。
- [ ] `IMediaResolver` 与 `MediaResolveCoordinator` 均返回 `MediaResolveResult`。
- [ ] “需要浏览器”不通过异常表示。
- [ ] `MediaResolveCoordinator` 无抖音/WebView2 依赖。
- [ ] 分享链接、重定向、媒体 URL 和下载重定向均经过安全校验。
- [ ] 自动测试不查询真实 DNS、不访问真实网络。
- [ ] Cookie 域名、Path、Secure 匹配正确。
- [ ] 下载同时检查声明长度和实际读取长度。
- [ ] `.part.<TaskId>` 在失败/取消后清理。
- [ ] `CancelAllAsync` 等待任务结束后才释放资源。
- [ ] 多图采用资产—变体模型，数量和变体数不冲突。
- [ ] WebView2 固定版本并仅在 STA UI 线程访问。
- [ ] 文本模块和媒体模块保持隔离。
- [ ] `git status --short` 干净。

## 计划自检

- [ ] 每个任务的文件清单、接口、测试和提交命令均不引用后续任务才创建的类型。
- [ ] 没有 `RequiresInteraction`/`IsPlausibleMedia` 等未定义名称。
- [ ] 所有长时间网络/文件/WebView2 操作都有取消和资源释放规则。
- [ ] 所有测试均使用 Fake/Stub，不访问真实 DNS、HTTP、抖音或 WebView2 Runtime。
- [ ] 解析结果状态、选择器结果、下载批次和 UI dispatcher 的名称在所有任务中一致。
- [ ] `MediaDownloadHistoryEntry.SavedFiles` 记录实际最终路径，不记录 CDN、Cookie 或授权信息。
- [ ] 退出顺序在主窗体销毁前释放 WebView2，且释放过程幂等。
- [ ] 所有提交信息使用中文，且本计划不要求推送。


# Transfor

Windows 桌面工具。基于 .NET 10 + WinForms 构建：内置现代化界面（Web UI：文本转换、媒体下载、浏览器、历史、设置）；媒体下载支持解析抖音分享链接并下载图片/视频；内置 WebView2 浏览器（独立 Profile 持久化登录态，兼作解析/下载的浏览器兜底）；应用常驻系统托盘。

## 功能

### 文本转换

- **引号转换**：英文双引号 `"` → `'`，中文双引号 `“ ”` → `‘ ’`
- **去除空格**：移除半角空格 ` ` 与全角空格 `　`，保留换行与制表符
- **历史记录**：两种功能独立记录转换历史，自动裁剪，上限可在 1–500 之间配置（默认 100）
- **全局快捷键**：默认 `Alt+Q` 呼出 `HistoryPanelForm` 历史面板（可在设置中修改），选中历史项后由 `PasteCoordinator` 自动粘贴到呼出前的目标窗口
- **系统托盘**：关闭主界面时隐藏到托盘，双击托盘图标重新打开；托盘菜单提供「新界面 / 检查更新 / 退出」
- **状态持久化**：设置、界面状态与文本历史分别保存到 `settings.json`、`ui-state.json` 与 `text-history.json`，文件损坏时自动回退到默认值

### 媒体下载

- 粘贴抖音分享文本/链接，解析单视频与多图作品（保持原始顺序），过滤头像、Logo 与相关推荐；实况图配对（静态照片 + 动态视频）在资产表显示 **LIVE** 标记
- 每个媒体按「资产—变体」模型管理，自动选择当前可访问的最高质量直接下载版本；`Balanced` 策略优先至少 720p
- 支持直接图片/视频 URL（`DirectMediaResolver` 兜底）
- 抖音边缘会对非浏览器 TLS 客户端拒绝握手：应用自动改用真实浏览器网络栈（WebView2 隐藏宿主，页面 fetch 携带浏览器 Cookie/指纹）解析页面、预览与下载媒体，无需用户干预
- 解析过程捕获页面真实网络请求（Network Capture）：结构化数据缺失时，命中「作品媒体白名单」的网络请求兜底产出媒体候选（严格模式，广告/头像/预加载绝不误抓）；CDP 读取登录态详情接口响应体作为最可靠的作品数据源，并按真实接口 URL 直取兜底
- 需要登录或验证码时，在「浏览器」页完成抖音登录（与解析/下载共享同一 Profile 登录态；登录一次持续复用，后续解析/下载自动携带）
- 流式下载（`.part` 临时文件 + 原子落盘）、队列进度、单个/全部取消、失败重试；重名文件自动编号不覆盖
- 图片预览走与正式下载相同的安全链路；媒体设置可配置默认下载目录、并发数（1–8）、默认全选、下载后打开目录与质量策略
- 最小化到托盘时下载继续；真正退出且有任务时会先确认再取消任务

### 内置浏览器（WebView2）

- 新界面「浏览器」页：地址栏 + 后退/前进/刷新/停止，支持访问任意网站（含 douyin.com 登录）
- 独立 Profile（`%LOCALAPPDATA%\Transfor\Browser\UserData`）：Cookie、LocalStorage、缓存与登录状态持久化，重启应用后保持登录
- 媒体解析/下载的浏览器兜底（隐藏宿主）与「浏览器」页**共享同一 Profile**：在浏览器页登录抖音一次，解析与下载自动携带登录态
- 设置中可清除浏览器数据：Cookie / 缓存 / 全部浏览器数据（登录态一并重置）
- WebView2 Runtime 缺失时主界面不可用：应用保留托盘并提示安装，仍可「检查更新」或「退出」；不再提供旧 WinForms 界面回退
- 依赖系统 Edge WebView2 Runtime（Windows 11 内置；Windows 10 随 Edge 浏览器更新）

### 新界面（当前主界面）

- 启动即进入唯一的新界面宿主：AppShellForm + WebView2 + 本地 HTML/CSS/JS（嵌入程序集，三文件）；WebView2 Runtime 缺失时仅保留托盘恢复操作
- 左侧边栏导航（工作台/媒体下载/浏览器/历史/设置）+ 内容区；Design System 基础组件（按钮/输入/卡片/对话框/Toast/进度条/侧边栏）；主题：跟随系统/浅色/深色
- **媒体下载页已迁移**：粘贴/输入分享链接（支持完整分享文本）→ 解析 → 媒体卡片（图片/视频/实况 LIVE、分辨率/大小、点击预览、勾选/全选）→ 下载选中；下载进度/完成经 Bridge 事件实时推送；解析失败不保留旧作品
- **下载队列已并入媒体页**：解析结果下方连续展示可折叠队列（等待中/下载中/已完成/失败/已取消 + 进度条 + 速度 + 已下载/总量），支持取消、进程内重试、打开文件或目录；历史记录只在「历史」页展示
- **历史页已迁移**：文本转换（引号/空格）+ 媒体下载分组，搜索过滤、单条删除、整组清空、媒体记录「重新执行」
- **浏览器页已迁移**：宿主侧边栏（C#）+ 内容区嵌套互联网 WebView2（共享 `Browser\UserData` 登录态，与 AppWebView 严格隔离）——地址栏（容错：分享文本/尾部标点/多余字段均可识别）/后退/前进/刷新/停止；页面加载后自动检测媒体，「当前页面检测到 X 个可能的媒体 [查看媒体]」直达解析（尺寸与装饰资源过滤，避免 Logo/头像误报）；浏览器初始化失败不影响其他页面
- **设置页已迁移（完整可编辑）**：常规（更新通道/历史上限）、下载（目录浏览/并发/默认全选/打开目录/质量偏好）、网络（模式/代理地址）、快捷键（历史面板热键可改，占用报错）、浏览器数据（清除 Cookie/缓存/全部）、更新（检查）
- **工作台已迁移**：文本工具（引号/空格 Tab 切换）以输入与实时结果双栏呈现，复制和状态集中在结果区
- App Bridge：JSON 消息协议（`getAppInfo`/`getSettings`/`saveSettings`/`checkUpdate`/`resolveMedia`/`downloadSelected`/`getPreview`/`getClipboardText`/`getDownloads`/`cancelTask`/`cancelAllDownloads`/`retryTask`/`openFile`/`openFolder`/`getHistory`/`clearHistory`/`deleteHistoryEntry`）+ 事件推送（`downloadProgress`/`taskCompleted`/`batchCompleted`）
- **安全隔离**：AppWebView 使用独立 Profile（`Browser\AppUi`，与互联网浏览器 `Browser\UserData` 严格分离）、禁止外部导航与新窗口，仅经 Bridge 协议访问应用服务
- **已完成清理**：旧 WinForms 页面与废弃 EdgeCdp 实现已删除；全局热键、HistoryPanelForm 历史浮窗及粘贴回原应用的服务仍保留

### 应用更新

- 版本号统一来源于项目配置（SemVer，当前 `0.15.0` 开发版），启动后与托盘菜单均可检查更新
- 远程 `update-policy.json` 策略：可选更新（稍后/立即）与强制更新（重新检查/立即/退出，不允许跳过）
- 更新下载基于 **Velopack**（GitHub Releases 为更新源）：下载进度/取消，「立即重启并更新」自动安装并重启；可选更新可「稍后重启」，下次启动自动应用
- 更新通道（设置中切换）：**Stable** 只接收正式版；**Beta** 额外接收预发布
- 网络错误、JSON 损坏等一律视为检查失败并静默放行，绝不阻断应用使用

> 「最高质量」指当前公开页面或当前浏览器会话可访问的、可直接下载的媒体版本；不承诺作者原始文件、无水印或可访问私密/删除/付费作品。第一版不支持 DASH/HLS 分段流合并，发现分段媒体时会明确提示。

## 项目结构

```text
src/Transfor/
├── App/                                     # 入口、组合根、应用生命周期与路径
│   ├── Program.cs                           # 入口：初始化 WinForms 并启动消息循环（全局异常兜底）
│   ├── AppBootstrapper.cs                   # 组合根：组装文本/媒体/更新/浏览器服务依赖图
│   ├── AppServices.cs                       # 应用服务容器，随应用释放
│   ├── MediaServices.cs                     # 媒体服务组合：协调器/浏览器代理/预览/HttpClient
│   ├── AppPaths.cs                          # 状态文件路径集合（%LOCALAPPDATA%\Transfor）
│   ├── AppVersion.cs                        # 当前版本（读取 csproj <Version>）
│   └── TransforApplicationContext.cs        # 应用上下文：新界面宿主/托盘/更新检查/退出流程
├── Features/
│   ├── TextTools/                           # 文本转换
│   │   ├── Models/                          # TextToolId / TextToolDefinition（共用，保留）
│   │   ├── Services/                        # QuoteConverter / SpaceRemover（核心，保留）
│   ├── History/                             # 历史记录
│   │   ├── Models/HistoryEntry.cs           # 历史条目（共用，保留）
│   │   ├── Services/                        # ITextHistoryRepository / PasteCoordinator（悬浮面板粘贴）
│   │   └── UI/                              # HistoryPanelForm（全局热键悬浮面板）
│   ├── Settings/                            # 设置
│   │   ├── Models/                          # AppSettings / HotKeyBinding / TextUiState（共用，保留）
│   ├── Updates/                             # 应用更新（Phase 1 检查 + Phase 2 Velopack）
│   │   ├── Models/                          # UpdateStatus / UpdateChannel / UpdatePolicy / UpdateCheckResult
│   │   ├── Services/                        # UpdateVersion(SemVer) / VersionComparer / UpdateService / IUpdatePolicySource / HttpUpdatePolicySource / IUpdateInstaller / VelopackUpdateInstaller
│   │   └── UI/                              # UpdateNoticeForm（可选/强制更新提示）/ UpdateDownloadForm（下载进度+重启）
│   ├── Browser/                             # WebView2 浏览器服务（新界面/解析兜底共用，保留）
│   │   ├── Contracts/IBrowserService.cs     # 浏览器门面
│   │   ├── Services/                        # BrowserService(共享环境+UI调度) / BrowserProfileService / BrowserNavigationService / BrowserCookieService
│   │   └── UI/                              # BrowserHostForm（解析/下载隐藏宿主）
│   ├── ModernUi/                            # 新界面（当前主界面）
│   │   ├── AppShellForm.cs                  # 宿主窗体：C# 侧边栏 + AppWebView + 互联网浏览器控件
│   │   ├── WebUiResources.cs                # 嵌入资源读取（index.html / styles.css / app.js）
│   │   ├── Bridge/                          # AppBridge（方法分发）/ AppBridgeEvents（事件推送）/ AppBridgeProtocol（JSON 协议）
│   │   └── webui/                           # 多文件 UI：index.html + styles.css + app.js（嵌入程序集，改后需重新构建）
│   └── MediaDownload/                       # 媒体下载（核心保留）
│       ├── Contracts/                       # IMediaResolver / IMediaDownloadService / IBrowserSessionAccessor / 浏览器捕获与网络记录模型
│       ├── Models/                          # MediaAsset / MediaVariant / ResolvedMediaPost / 下载批次/任务/设置/历史/快照等
│       ├── Application/                     # MediaResolveCoordinator / MediaDownloadCoordinator / MediaResolverRegistry / BrowserSessionAccessorProxy
│       ├── Services/                        # ShareLinkParser / MediaQualitySelector / MediaContentValidator / MediaDownloadService / MediaPreviewService / MediaFileFinalizer / DownloadFileNameBuilder / MediaHashService / MediaSniffer / MediaUrlExtractor / MediaCache / MediaSizeProbe / BrowserCookieMatcher 等
│       ├── Resolvers/
│       │   ├── DirectMediaResolver.cs       # 直接图片/视频 URL 兜底解析
│       │   └── Douyin/                      # 抖音静态解析（RENDER_DATA/JSON-LD/DOM）与浏览器兜底
├── Infrastructure/                          # 基础设施
│   ├── Networking/                          # 安全网络：DNS 抽象 / URI 校验 / 逐跳重定向 / HttpClient 工厂
│   │   ├── IDnsResolver.cs / SystemDnsResolver.cs
│   │   ├── SafeUriValidator.cs / UriValidationException.cs / UriValidationResult.cs
│   │   ├── SafeHttpRequestSender.cs         # 每跳校验 + 敏感头清除 + 重定向上限
│   │   ├── HttpClientProvider.cs            # 共享 HttpClient（禁用自动重定向与 Cookie 容器）
│   │   ├── HttpResponseMetadataReader.cs
│   │   └── ErrorChainFormatter.cs           # 异常链格式化（诊断/日志用）
│   ├── Persistence/                         # JSON 状态存储与旧状态迁移
│   │   ├── TextStateStore.cs / MediaStateStore.cs / JsonFileStore.cs / StateMigrationService.cs
│   └── Diagnostics/                         # 日志与诊断（Phase 7）
│       ├── AppLog.cs                        # 分类日志（五类，1MB 轮转，敏感数据脱敏）
│       ├── ErrorClassification.cs           # ErrorCategory / TransforError / ErrorClassifier
│       └── CrashDiagnostics.cs              # 崩溃现场记录（%TEMP%\Transfor\diagnostics\）
└── Platform/Windows/                        # Windows 平台适配层
    ├── Clipboard/
    │   ├── ClipboardTextReader.cs           # Win32 剪贴板读取（新界面用，后台线程安全）
    │   └── WindowsClipboardService.cs       # 剪贴板写入（热键历史面板粘贴用）
    ├── HotKeys/
    │   └── GlobalHotKeyManager.cs           # 全局热键（Alt+Q 历史浮窗）
    ├── Input/
    │   └── WindowsWindowInputService.cs     # 前台窗口恢复 + SendInput 模拟 Ctrl+V（历史浮窗粘贴用）
    ├── Native/
    │   └── WindowsNative.cs                 # user32.dll 互操作声明（热键与粘贴服务使用）
    ├── WebView2/                            # WebView2 媒体解析兜底（Phase 4A/4B/4C，当前生效）
    │   ├── WebView2BrowserSessionAccessor.cs # IBrowserSessionAccessor 实现（捕获/下载/取 Cookie）
    │   ├── BrowserCaptureSession.cs         # 页面数据提取（RENDER_DATA/NEXT_DATA/DOM/详情接口直取）
    │   ├── BrowserDownloadController.cs     # 浏览器网络栈下载（CDP 流式写入 .part）
    │   ├── NetworkCaptureService.cs         # 网络捕获（Phase 4B：URL/Method/Content-Type/Status 记录）
    │   └── CdpNetworkCaptureService.cs      # CDP 捕获（Phase 4C：详情接口响应体读取）

tests/
└── Transfor.Tests/                          # 控制台式测试运行器（无框架依赖，全部离线）
    ├── Program.cs                           # 969 断言：转换器/迁移/存储/网络/解析/下载/队列/UI/日志/错误分类
    ├── Fixtures/MediaDownload/              # 脱敏的抖音页面与结构化数据样本（不含真实凭据）
    └── Transfor.Tests.csproj                # 引用主项目，目标框架 net10.0-windows
```

应用状态位于 `%LOCALAPPDATA%\Transfor\`：

- 文本：`settings.json`、`ui-state.json`、`text-history.json`（首次启动新版时迁移旧 `state.json`，成功后备份为 `state.v1.backup.json`；迁移中断会优先使用仍可读取的旧状态恢复）
- 媒体：`media-settings.json`（默认下载目录/并发数/质量策略等）、`download-history.json`（批次下载记录）
- 浏览器数据（Cookie/权限/缓存/登录态）仅存于 `Browser\UserData\`（WebView2 Profile），不写入普通 JSON；下载历史不保存临时 CDN URL、Cookie 或授权信息

## 环境要求

- Windows 10/11
- .NET 10 SDK
- 浏览器兜底解析与下载需要 Microsoft Edge WebView2 Runtime（Windows 11 内置；Windows 10 随 Edge 更新自动安装）；登录抖音一次后持续复用登录态（在「浏览器」页完成）

## 构建与运行

```bash
# 构建
dotnet build Transfor.slnx

# 运行
dotnet run --project src/Transfor

# 运行测试（输出 "All N tests passed."）
dotnet run --project tests/Transfor.Tests
```

## 日志与诊断（Phase 7）

- 分类日志：`%TEMP%\Transfor\logs\`（application/update/browser/media-resolve/download，按天分文件 + 1MB 轮转保留近 5 个）
- 诊断文件：`%TEMP%\Transfor\diagnostics\`（解析现场 capture-*.json、崩溃 crash-*.txt）
- **敏感数据约定**：Cookie/Token/完整认证头禁止写入日志
- WebView2 Runtime 缺失：启动时检测并托盘气泡提示一次；主界面与浏览器兜底不可用，但仍可从托盘检查更新或退出
- 错误分类：Network/Parse/Browser/Download/Update/Permission/Unknown（关键路径标注，UI 显示用户可读信息，日志保存技术细节）

## 发布流程（Phase 2）

发布在 `Release` 分支完成，由 GitHub Actions 自动打包：

1. 在 `Release` 分支更新 `update-policy.json`（`latestVersion` / `minimumVersion` / 更新说明），推送该分支；
2. 打版本标签并推送（稳定版 `v0.15.0`；测试版 `v0.15.0-beta.1` 自动走 Beta 通道与 GitHub 预发布）：

   ```bash
   git tag v0.15.0 && git push origin v0.15.0
   ```

3. 工作流 `release.yml` 自动执行：发布（自包含 win-x64）→ `vpk` 打包（Setup.exe / nupkg / RELEASES）→ 创建 GitHub Release 并上传；CI 不执行断言测试——**发布前必须在本地跑全量测试**（见下方检查清单）；
4. 客户端从 GitHub Releases 拉取更新包完成升级（Velopack 负责下载/安装/重启，应用不覆盖运行中的 EXE；更新包完整性由 Velopack 校验）。

### 发布前检查清单

- [ ] `src/Transfor/Transfor.csproj` 的 `<Version>` 与 tag 版本一致
- [ ] `update-policy.json` 的 `latestVersion`/`minimumVersion`/说明已更新（两个通道）
- [ ] 全量测试通过（`dotnet run --project tests/Transfor.Tests`）
- [ ] `dotnet build Transfor.slnx` 0 警告 0 错误
- [ ] 浏览器相关改动真机验证（测试 fakes 无法覆盖真实 WebView2 行为）

## 使用说明

### 新界面

1. 启动进入新界面：左侧边栏「工作台 / 媒体 / 浏览器 / 历史 / 设置」五页，右上角可切换主题（跟随系统/浅色/深色）。
2. 关闭窗口不会退出程序，进程驻留系统托盘；托盘菜单提供「新界面 / 检查更新 / 退出」。

### 文本转换

3. 「工作台」页输入文本，自动实时转换（引号/空格 Tab 切换），点击「复制」写入剪贴板并记入历史。

### 媒体下载

4. 切到「媒体下载」页：粘贴抖音分享文本或直接图片/视频链接 → 「解析」；页面需要登录或验证码时切到「浏览器」页完成抖音登录，再返回重试。
5. 解析后勾选要下载的媒体（可全选），选择保存目录并点击「下载所选」；队列中可取消单个任务，失败行可重试；图片行可点击「预览」。
6. 「设置 → 下载」可修改默认下载目录、最大并发（1–8）、默认全选、下载后打开目录与质量策略，设置对下一个新批次生效。
7. 最小化到托盘时下载继续；真正退出且有任务时会先确认再取消任务。
8. 合法使用提示：仅下载你有权访问的公开内容，不绕过登录、验证码或访问控制。

### 内置浏览器

9. 切到「浏览器」页：输入网址（可省略协议，自动补全 https://）或粘贴完整链接，回车或点击「前往」。
10. 工具栏支持后退/前进/刷新/停止；页面加载后自动检测媒体，「当前页面检测到 X 个可能的媒体 [查看媒体]」直达解析；浏览器登录状态（如抖音）独立持久化，重启应用后保持。
11. 「设置 → 浏览器数据」可清除 Cookie、缓存或全部浏览器数据（清除后需重新登录）。

## Rider 调试提示

若 Rider 调试启动报 `Unable to detect target platform of file`：

- 升级到 Rider 2025.1+（.NET 10 支持），并在 `File > Invalidate Caches / Restart` 后重新构建；
- 或先 `dotnet build` 确认 `src/Transfor/bin/Debug/net10.0-windows/Transfor.exe` 已生成。

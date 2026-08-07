# Transfor

Windows 桌面工具。基于 .NET 10 + WinForms 构建：文本转换常驻系统托盘，通过全局快捷键随时呼出历史记录面板；媒体下载支持解析抖音分享链接并下载图片/视频；内置 WebView2 浏览器（独立 Profile 持久化登录态）。

## 功能

### 文本转换

- **引号转换**：英文双引号 `"` → `'`，中文双引号 `“ ”` → `‘ ’`
- **去除空格**：移除半角空格 ` ` 与全角空格 `　`，保留换行与制表符
- **历史记录**：两种功能独立记录转换历史，自动裁剪，上限可在 1–500 之间配置（默认 100）
- **全局快捷键**：默认 `Alt+Q` 呼出历史面板（可在设置中修改），选中历史项后自动粘贴到呼出前的目标窗口
- **系统托盘**：关闭主窗口时最小化到托盘，双击托盘图标重新打开
- **状态持久化**：设置、界面状态与文本历史分别保存到 `settings.json`、`ui-state.json` 与 `text-history.json`，文件损坏时自动回退到默认值

### 媒体下载

- 粘贴抖音分享文本/链接，解析单视频与多图作品（保持原始顺序），过滤头像、Logo 与相关推荐
- 每个媒体按「资产—变体」模型管理，自动选择当前可访问的最高质量直接下载版本；`Balanced` 策略优先至少 720p
- 支持直接图片/视频 URL（`DirectMediaResolver` 兜底）
- 抖音边缘会对非浏览器 TLS 客户端拒绝握手：应用自动改用真实浏览器网络栈（WebView2 隐藏宿主，页面 fetch 携带浏览器 Cookie/指纹）解析页面、预览与下载媒体，无需用户干预
- 解析过程捕获页面真实网络请求（Network Capture）：结构化数据缺失时，命中「作品媒体白名单」的网络请求兜底产出媒体候选（严格模式，广告/头像/预加载绝不误抓）；并按真实详情接口 URL 直取作品数据
- 需要登录或验证码时，在「浏览器」页完成抖音登录（与解析/下载共享同一 Profile 登录态；登录一次持续复用，后续解析/下载自动携带）
- 流式下载（`.part` 临时文件 + 原子落盘）、队列进度、单个/全部取消、失败重试；重名文件自动编号不覆盖
- 图片预览走与正式下载相同的安全链路；媒体设置可配置默认下载目录、并发数（1–8）、默认全选、下载后打开目录与质量策略
- 关闭主窗口到托盘时下载继续；真正退出且有任务时会先确认再取消任务

### 内置浏览器（WebView2）

- 主窗口「浏览器」页：地址栏 + 后退/前进/刷新/停止，支持访问任意网站（含 douyin.com 登录）
- 独立 Profile（`%LOCALAPPDATA%\Transfor\Browser\UserData`）：Cookie、LocalStorage、缓存与登录状态持久化，重启应用后保持登录
- 媒体解析/下载的浏览器兜底（隐藏宿主）与「浏览器」页**共享同一 Profile**：在浏览器页登录抖音一次，解析与下载自动携带登录态
- 设置中可清除浏览器数据：Cookie / 缓存 / 全部浏览器数据（登录态一并重置）
- 初始化失败（如 WebView2 Runtime 缺失）时页面显示明确提示，不影响应用其他功能
- 依赖系统 Edge WebView2 Runtime（Windows 11 内置；Windows 10 随 Edge 浏览器更新）

### 应用更新

- 版本号统一来源于项目配置（SemVer，当前 `0.9.0` 开发版），启动后与托盘菜单均可检查更新
- 远程 `update-policy.json` 策略：可选更新（稍后/立即）与强制更新（重新检查/立即/退出，不允许跳过）
- 更新下载基于 **Velopack**（GitHub Releases 为更新源）：下载进度/取消，「立即重启并更新」自动安装并重启；可选更新可「稍后重启」，下次启动自动应用
- 更新通道（设置中切换）：**Stable** 只接收正式版；**Beta** 额外接收预发布
- 网络错误、JSON 损坏等一律视为检查失败并静默放行，绝不阻断应用使用

> 「最高质量」指当前公开页面或当前浏览器会话可访问的、可直接下载的媒体版本；不承诺作者原始文件、无水印或可访问私密/删除/付费作品。第一版不支持 DASH/HLS 分段流合并，发现分段媒体时会明确提示。

## 项目结构

```text
src/Transfor/
├── App/                                     # 入口、组合根、应用生命周期与路径
│   ├── Program.cs                           # 入口：初始化 WinForms 并启动消息循环
│   ├── AppBootstrapper.cs                   # 组合根：组装文本与媒体服务依赖图
│   ├── AppServices.cs                       # 应用服务容器（文本 + 媒体 + 更新），随应用释放
│   ├── MediaServices.cs                     # 媒体服务组合：协调器/浏览器代理/预览/HttpClient
│   ├── AppPaths.cs                          # 状态文件路径集合（%LOCALAPPDATA%\Transfor）
│   ├── AppVersion.cs                        # 当前版本（读取 csproj <Version>）
│   └── TransforApplicationContext.cs        # 应用上下文：主窗口/历史面板/托盘/热键/更新检查/退出流程
├── Shell/                                   # 主窗口外壳与页面接口
│   ├── IFeaturePage.cs                      # 功能页契约（Id、名称、视图、激活回调）
│   └── MainForm.cs                          # 主窗口：由页面集合动态生成导航，关闭时隐藏到托盘
├── Features/
│   ├── TextTools/                           # 文本转换：页面、工具定义与转换器
│   │   ├── Models/
│   │   │   ├── TextToolId.cs                # 工具唯一标识（引号转换 / 去除空格）
│   │   │   └── TextToolDefinition.cs        # 工具静态定义（ID、名称、转换函数）
│   │   ├── Services/
│   │   │   ├── QuoteConverter.cs            # 引号转换：英文/中文双引号 → 单引号
│   │   │   └── SpaceRemover.cs              # 空格移除：半角/全角空格（保留换行制表符）
│   │   └── UI/
│   │       └── TextToolsPage.cs             # 转换页面：实时转换、复制结果并记入历史
│   ├── History/                             # 历史记录：模型、粘贴协调器与历史面板
│   │   ├── Models/
│   │   │   └── HistoryEntry.cs              # 历史条目（工具、原文、结果、时间）
│   │   ├── Services/
│   │   │   ├── ITextHistoryRepository.cs    # 历史仓库抽象（读取/追加/清空）
│   │   │   └── PasteCoordinator.cs          # 粘贴三步流程：剪贴板→恢复窗口→Ctrl+V
│   │   └── UI/
│   │       └── HistoryPanelForm.cs          # 全局快捷键呼出的历史面板（单击/回车粘贴）
│   ├── Settings/                            # 设置：模型与设置窗口
│   │   ├── Models/
│   │   │   ├── AppSettings.cs               # 文本设置（快捷键、历史上限 1–500，默认 100）
│   │   │   ├── HotKeyBinding.cs             # 快捷键绑定（校验、显示、Win32 注册格式）
│   │   │   └── TextUiState.cs               # 界面状态（最近查看的工具）
│   │   └── UI/
│   │       └── SettingsForm.cs              # 设置窗口（热键、上限、清空历史）
│   └── Updates/                             # 应用更新：检查（Phase 1）+ 自动下载安装（Phase 2）
│       ├── Models/                          # UpdateStatus / UpdateChannel / UpdatePolicy / UpdateCheckResult
│       ├── Services/                        # UpdateVersion(SemVer) / VersionComparer / UpdateService / IUpdatePolicySource / HttpUpdatePolicySource / IUpdateInstaller / VelopackUpdateInstaller
│       └── UI/                              # UpdateNoticeForm（可选/强制更新提示）/ UpdateDownloadForm（下载进度+重启）
│   └── Browser/                             # 内置浏览器（Phase 3/4A）：WebView2 独立 Profile
│       ├── Contracts/                       # IBrowserService（环境/Profile 生命周期门面）
│       ├── Services/                        # BrowserService（共享环境 + UI 线程调度）/ BrowserProfileService / BrowserNavigationService / BrowserCookieService
│       └── UI/                              # BrowserView（浏览器功能页）/ BrowserHostForm（解析+下载隐藏宿主）
│   └── MediaDownload/                       # 媒体下载：契约、模型、协调器、服务、解析器与 UI
│       ├── Contracts/                       # IMediaResolver / IMediaDownloadService / IBrowserSessionAccessor / 浏览器捕获模型
│       ├── Models/                          # MediaAsset / MediaVariant / ResolvedMediaPost / 下载批次/设置/历史等
│       ├── Application/                     # MediaResolveCoordinator / MediaDownloadCoordinator / 解析注册中心 / 浏览器会话代理
│       ├── Services/                        # ShareLinkParser / 质量选择 / 内容校验 / 流式下载 / 预览 / 文件名与哈希 / Cookie 匹配 / 网络嗅探（MediaSniffer/MediaUrlExtractor）
│       ├── Resolvers/
│       │   ├── DirectMediaResolver.cs       # 直接图片/视频 URL 兜底解析
│       │   └── Douyin/                      # 抖音静态解析（RENDER_DATA/JSON-LD/DOM）与浏览器兜底
│       └── UI/                              # MediaDownloadPage / 资产表 / 队列表 / 设置窗体 / 预览控件
├── Infrastructure/                          # 基础设施
│   ├── Networking/                          # 安全网络：DNS 抽象 / URI 校验 / 逐跳重定向 / HttpClient 工厂
│   │   ├── IDnsResolver.cs / SystemDnsResolver.cs
│   │   ├── SafeUriValidator.cs              # 拒绝私网/回环/链路本地等危险地址
│   │   ├── SafeHttpRequestSender.cs         # 每跳校验 + 敏感头清除 + 重定向上限
│   │   ├── HttpClientProvider.cs            # 共享 HttpClient（禁用自动重定向与 Cookie 容器）
│   │   └── HttpResponseMetadataReader.cs
│   └── Persistence/                         # JSON 状态存储与旧状态迁移
│       ├── TextStateStore.cs                # 文本状态存储：原子写入、损坏回退、写失败回滚
│       ├── MediaStateStore.cs               # 媒体设置与下载历史存储（独立文件、串行写入）
│       ├── JsonFileStore.cs                 # 通用原子 JSON 文件读写（schemaVersion 外壳）
│       └── StateMigrationService.cs         # 旧 state.json 拆分迁移与中断恢复
└── Platform/Windows/                        # Windows 平台适配层
    ├── Clipboard/
    │   └── WindowsClipboardService.cs       # 剪贴板写入（失败返回可读错误）
    ├── HotKeys/
    │   └── GlobalHotKeyManager.cs           # 全局热键注册/替换/释放（WM_HOTKEY 转发）
    ├── Input/
    │   └── WindowsWindowInputService.cs     # 前台窗口恢复 + SendInput 模拟 Ctrl+V
    ├── Native/
    │   └── WindowsNative.cs                 # user32.dll 互操作声明与输入结构
    ├── WebView2/                            # WebView2 媒体解析兜底（Phase 4A/4B，当前生效）
    │   ├── WebView2BrowserSessionAccessor.cs # IBrowserSessionAccessor 实现（捕获/下载/取 Cookie）
    │   ├── BrowserCaptureSession.cs         # 页面数据提取（RENDER_DATA/NEXT_DATA/DOM/详情接口直取）
    │   ├── BrowserDownloadController.cs     # 浏览器网络栈下载（页面 fetch + 分块流式写入 .part）
    │   └── NetworkCaptureService.cs         # 网络捕获（Phase 4B：URL/Method/Content-Type/Status 记录）
    └── EdgeCdp/                             # 旧 Edge CDP 实现（已由 WebView2 替代，保留未删除）

tests/
└── Transfor.Tests/                          # 控制台式测试运行器（无框架依赖，全部离线）
    ├── Program.cs                           # 400+ 断言：转换器/迁移/存储/热键/粘贴/网络/解析/下载/队列/UI/CDP
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

## 发布流程（Phase 2）

发布在 `Release` 分支完成，由 GitHub Actions 自动打包：

1. 在 `Release` 分支更新 `update-policy.json`（`latestVersion` / `minimumVersion` / 更新说明），推送该分支；
2. 打版本标签并推送（稳定版 `v0.9.0`；测试版 `v0.9.0-beta.1` 自动走 Beta 通道与 GitHub 预发布）：

   ```bash
   git tag v0.9.0 && git push origin v0.9.0
   ```

3. 工作流 `release.yml` 自动执行：测试 → 发布（自包含 win-x64）→ `vpk` 打包（Setup.exe / nupkg / RELEASES）→ 创建 GitHub Release 并上传；
4. 客户端从 GitHub Releases 拉取更新包完成升级（Velopack 负责下载/安装/重启，应用不覆盖运行中的 EXE）。

## 使用说明

### 文本转换

1. 首次启动显示主窗口，输入文本后自动实时转换，点击「复制结果」写入剪贴板并记入历史。
2. 关闭主窗口不会退出程序，进程驻留系统托盘。
3. 在任意程序中按下 `Alt+Q`，历史面板会出现在鼠标附近；单击或回车即可把选中结果粘贴回原窗口。
4. 托盘菜单提供「打开主窗口 / 设置 / 退出」。

### 媒体下载

5. 切换到「媒体下载」页：粘贴抖音分享文本或直接图片/视频链接 → 「解析」；页面需要登录或验证码时切换到「浏览器」页完成抖音登录，再返回重试。
6. 解析后勾选要下载的媒体（可全选），选择保存目录并点击「下载所选」；队列中可取消单个任务，失败行可重试；图片行可点击「预览」。
7. 「媒体设置」可修改默认下载目录、最大并发（1–8）、默认全选、下载后打开目录与质量策略，设置对下一个新批次生效。
8. 关闭主窗口到托盘时下载继续；真正退出且有任务时会先确认再取消任务。
9. 合法使用提示：仅下载你有权访问的公开内容，不绕过登录、验证码或访问控制。

### 内置浏览器

10. 切换到「浏览器」页：输入网址（可省略协议，自动补全 https://）或粘贴完整链接，回车或点击「前往」。
11. 工具栏支持后退/前进/刷新/停止；浏览器登录状态（如抖音）独立持久化，重启应用后保持。
12. 「设置 → 浏览器数据」可清除 Cookie、缓存或全部浏览器数据（清除后需重新登录）。

## Rider 调试提示

若 Rider 调试启动报 `Unable to detect target platform of file`：

- 升级到 Rider 2025.1+（.NET 10 支持），并在 `File > Invalidate Caches / Restart` 后重新构建；
- 或先 `dotnet build` 确认 `src/Transfor/bin/Debug/net10.0-windows/Transfor.exe` 已生成。

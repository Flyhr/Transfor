# Transfor 项目状态备忘（上下文压缩用）

> 用途：供 AI 与开发者快速恢复项目记忆。更新日期：2026-08-07。

## 〇、当前版本与路线

- **应用版本**：`0.12.0`（唯一来源：`src/Transfor/Transfor.csproj` 的 `<Version>`，SemVer）
- **浏览器技术路线**：WebView2 已落地（浏览器页 + 隐藏宿主）；媒体解析/下载兜底为 WebView2（`WebView2BrowserSessionAccessor`，与浏览器页共享 Profile 登录态）；旧 Edge CDP 实现保留未删除、不再被实例化
- **Phase 进度**：Phase 0（架构整理）✅ 标签 `architecture-baseline`；Phase 1（版本检查）✅；Phase 2（Velopack 自动更新）✅；Phase 3（WebView2 浏览器模块）✅；Phase 4A（浏览器兜底切 WebView2）✅；Phase 4B（Network Capture + MediaSniffer）✅ 完成于 dev；Phase 4C（CDP 读响应体）/ 4D（实况专项）未开始

## 一、项目概述

Windows 桌面工具（.NET 10 + WinForms，单项目 `Transfor.slnx`），两大功能：
1. **文本转换**：引号转换 / 去除空格，常驻系统托盘 + `Alt+Q` 历史面板自动粘贴
2. **抖音媒体下载**：粘贴分享链接 → 解析视频/图文/实况 → 选择媒体流式下载

## 二、框架结构

```
src/Transfor/
├─ App/            # AppBootstrapper(组合根) / AppServices / MediaServices / AppPaths / Program / TransforApplicationContext(托盘+退出)
├─ Shell/          # MainForm(页面外壳, 接收 IFeaturePage[]) / IFeaturePage
├─ Features/
│  ├─ TextTools/   # QuoteConverter / SpaceRemover / TextToolsPage
│  ├─ History/     # HistoryEntry / PasteCoordinator / HistoryPanelForm
│  ├─ Settings/    # AppSettings / HotKeyBinding / SettingsForm
│  ├─ Updates/     # 版本检查 + Velopack 安装：UpdateService / VersionComparer(UpdateVersion) / IUpdatePolicySource / IUpdateInstaller(VelopackUpdateInstaller, GithubSource) / UpdateNoticeForm + UpdateDownloadForm（进度/取消/重启）
│  ├─ Browser/     # Phase 3/4A WebView2：IBrowserService / BrowserService(共享环境+UI 线程调度) / BrowserProfileService(%LOCALAPPDATA%\Transfor\Browser\UserData) / BrowserNavigationService(地址规范化) / BrowserCookieService / BrowserView(功能页) / BrowserHostForm(解析+下载隐藏宿主)
│  └─ MediaDownload/
│     ├─ Contracts/   # IMediaResolver / IMediaDownloadService / IBrowserSessionAccessor / 浏览器捕获模型
│     ├─ Models/      # MediaAsset / MediaVariant / ResolvedMediaPost / MediaDownloadBatch/Task/Result/Settings/HistoryEntry / MediaAssetRole
│     ├─ Application/ # MediaResolveCoordinator / MediaDownloadCoordinator(串行批次+BatchCompleted事件) / MediaResolverRegistry / BrowserSessionAccessorProxy
│     ├─ Services/    # ShareLinkParser / MediaQualitySelector / MediaContentValidator(魔数+ISO BMFF品牌) / MediaDownloadService / MediaPreviewService / DownloadFileNameBuilder / MediaHashService / MediaFileFinalizer / BrowserCookieMatcher / MediaSniffer + MediaUrlExtractor(Phase 4B 白名单嗅探)
│     ├─ Resolvers/   # DirectMediaResolver + Douyin/(DouyinPageParser / DouyinMediaResolver / DouyinMediaNormalizer / DouyinHttpPageResolver / DouyinDetailEndpointMatcher / DouyinTransportClassifier / DouyinTransportPreferenceState / CaptureDiagnostics)
│     └─ UI/          # MediaDownloadPage / MediaAssetGrid / DownloadQueueGrid / MediaPreviewControl / MediaSettingsForm
├─ Infrastructure/
│  ├─ Networking/   # SafeHttpRequestSender(逐跳校验+敏感头清除) / SafeUriValidator / HttpClientProvider / IDnsResolver
│  └─ Persistence/  # TextStateStore / MediaStateStore / JsonFileStore / StateMigrationService
└─ Platform/Windows/
   ├─ Clipboard|HotKeys|Input|Native/
   ├─ WebView2/    # Phase 4A/4B 当前生效：WebView2BrowserSessionAccessor(IBrowserSessionAccessor) / BrowserCaptureSession(JS 提取) / BrowserDownloadController(fetch 分块下载) / NetworkCaptureService(网络记录)
   └─ EdgeCdp/     # 旧实现：已由 WebView2 替代，保留未删除（EdgeProcessManager / CdpConnection / EdgeCdpResourceDownloader 等）
tests/Transfor.Tests/   # 控制台测试运行器（无框架，Program.cs 全量断言，Fixtures/MediaDownload 脱敏样本）
```

## 三、媒体下载核心流程与设计决策
- **作品类型判断**：结构化数据存在 `images`（图文/实况）→ 图片；仅 `video` → 视频；**顶层 video 仅在无 images 时解析**（图集预览视频不产出）
- **实况图配对**：逐个解析 `images[i].url_list`（静态）+ `images[i].video`（play_addr_h264→play_addr→play_addr_265→download_addr + bit_rate[]）；`Role=LivePhotoStill/Motion`、`SourceIndex`、`PairId` 配对；**isLivePhoto 以存在可播放视频地址为最终依据**（live_photo_type/clip_type 仅辅助）
- **文件名配对**：`标题_01_still.jpg` + `标题_01_motion.mp4`（BuildFileName 按 Role）
- **浏览器兜底**：HttpClient 被 TLS 指纹拦截时自动切 Edge CDP 网络栈（`loadNetworkResource` + IO.read，带登录 Cookie）；Edge 独立 profile `%LOCALAPPDATA%\Transfor\Edge\Douyin`
- **解析兜底链**：详情接口 Network 捕获 → NEXT_DATA/INITIAL_STATE 提取 → 作品 ID 提取+浏览器网络栈直取详情接口 → DOM 候选（滚动懒加载 + data-src 回退）
- **质量选择**：`MediaQualitySelector` 按像素面积选最高清（含 video.bit_rate 高清档）；视频排除封面/Thumbnail
- **持久化**：媒体状态独立 `media-settings.json` / `download-history.json`；Cookie 只存 Edge profile
- **下载完成后关闭浏览器**：BatchCompleted 事件 → CloseBrowserAsync（可恢复关闭，Cookie 保留）

## 四、遇到的问题及解决方案（历史）

| 问题 | 解决方案 |
|---|---|
| HttpClient 被抖音 TLS 指纹拦截 | 真实 Edge + CDP 网络栈兜底（loadNetworkResource） |
| 视频解析不出（页面脚本无作品数据） | 详情接口 Network 捕获 + 作品 ID 提取直取接口 |
| 视频下载到 JPEG 封面 | Parser 不把 cover 当视频变体；选择器排除 Thumbnail；终化按魔数区分报错 |
| 实况下载到 MP3 音乐 | 音乐 URL（ies-music/.mp3）过滤；按 images[i].video 配对解析 |
| 实况只有 1 张图 | DOM 滚动触发懒加载 + data-src 回退 |
| 实况视频画质低 | 解析 video.bit_rate 高清档（1080p/2K/4K） |
| 实况图扩展名 .img 无法播放 | 魔数支持 HEIC/HEIF/AVIF（ISO BMFF 品牌区分图/视频） |
| 资产表尺寸/预计大小不显示 | 下载后按实际文件回填尺寸；「预计大小」列已删除 |
| 文件名带 # 话题标签 | StripHashtags 过滤 |
| 测试进程在 MTA 线程创建 UserControl 污染 | 浏览器初始化测试改 RunSta |
| FileStream.FlushAsync 环境挂起 | 预览小文件用同步 Flush |

## 五、当前状态

- **最新提交**：Phase 4B Network Capture + MediaSniffer（dev 分支，未推送）
- **测试**：670 断言全过（`dotnet run --project tests/Transfor.Tests`）；构建 0 警告 0 错误
- **版本**：0.12.0（csproj `<Version>`，唯一来源）
- **更新体系**：检查走 update-policy.json（GitHub raw `Release` 分支）；下载/安装/重启走 Velopack（GithubSource，Beta 通道读预发布）；`vpk pack -c stable|beta` 已实测通过
- **浏览器（Phase 3/4A/4B）**：WebView2 1.0.4129.50，独立 Profile `Browser\UserData`；浏览器页 + 隐藏宿主共用登录态；解析/下载兜底 WebView2BrowserSessionAccessor；**4B：导航全程网络捕获（NetworkCaptureService），结构化数据缺失时 MediaSniffer 白名单嗅探兜底 + 真实详情接口 URL 优先 fetch**
- **诊断目录**：`%TEMP%\Transfor\diagnostics\`（解析完成 capture-*.json + 下载失败 failed-media-*；临时诊断代码在 CaptureDiagnostics / MediaFileFinalizer.SaveFailureSample，定位后移除）
- **已知边界**：未登录（not_exist_login_cookie）时视频可能返回封面/低清；Android Motion Photo / Apple Live Photo 封装未做（二期）；DASH/HLS 分段流不支持；更新包未做代码签名；WebView2 媒体预取未实现（4A 范围外）；响应体读取未实现（4C 的 CDP 能力）；Edge CDP 保留未删除

## 六、开发注意事项（坑）

- 测试运行器 Program.cs：**file 类必须放文件末尾**（顶层语句之后）；测试计数用静态 TestCounter.Passed
- 勿用 PowerShell `-replace` 处理含 `\r\n` 字面量的文件（会误伤 C# 转义序列）——用 Edit 工具
- WinForms 控件必须在 STA 线程创建（测试用 RunSta）
- **WebView2**：`Microsoft.Web.WebView2` 汇总包无条件附带 WPF 程序集 → MSB3277 WindowsBase 冲突（良性，已用 `MSBuildWarningsAsMessages` 降级）；`CoreWebView2BrowsingDataKinds` 成员名是 `AllDomStorage`；控件必须 UI 线程 + `EnsureCoreWebView2Async` 异步初始化；**ExecuteScriptAsync 结果按 JSON 编码**（字符串带引号需反序列化，null 为 "null"）
- **隐藏宿主（Phase 4A/4B）**：解析/下载必须经 `BrowserService.RunOnUiAsync` 或 `BrowserHostForm.RunOnUiAsync` 调度到 UI 线程（带超时防死锁）；与浏览器页共享同一 `CoreWebView2Environment`（同 Profile 多控件官方支持）；下载走页面 fetch + 2MB 分块 base64 传回 C# 写 `.part`，总大小先入内存（大文件超 200MB 有内存压力，后续可改流式）
- **网络捕获（Phase 4B）**：`WebResourceResponseReceived` 拿不到 ResourceType 与响应体（读 body 是 4C CDP 能力），接口识别走 `DouyinDetailEndpointMatcher`（容忍 null 类型）；嗅探严格模式——结构化 JSON 提取作品媒体 URL 白名单（MediaUrlExtractor），未命中白名单的网络请求一律丢弃，广告/头像/预加载不误抓；噪音关键词与 DouyinMediaNormalizer 保持一致
- 媒体解析链：`WebView2BrowserSessionAccessor` 实现 `IBrowserSessionAccessor`，解析/下载/预览调用点零改动；Edge CDP 保留未实例化
- 更新流程：检查在 Task.Run 后台执行，提示/下载/重启编排经 `InvokeUiAsync` 回 UI 线程；`VelopackApp.Build().Run()` 必须在 Program.Main 最先调用（try/catch 包裹，失败不阻断启动）
- Velopack：`DownloadUpdatesAsync` 进度为 `Action<int>` 百分比；`Size` 非空 long；`ApplyUpdatesAndRestart(toApply: null, restartArgs: null)`；`vpk pack -c` 默认通道是 win，必须显式传 stable/beta
- 所有网络操作带 CancellationToken；禁止 .Result/.Wait()（生产代码）
- 修改状态存储方法内部已持久化，UI 不重复调 Save()
- 提交信息用中文，不推送

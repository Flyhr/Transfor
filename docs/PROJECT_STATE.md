# Transfor 项目状态备忘（上下文压缩用）

> 用途：供 AI 与开发者快速恢复项目记忆。更新日期：2026-08-07。

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
│  └─ MediaDownload/
│     ├─ Contracts/   # IMediaResolver / IMediaDownloadService / IBrowserSessionAccessor / 浏览器捕获模型
│     ├─ Models/      # MediaAsset / MediaVariant / ResolvedMediaPost / MediaDownloadBatch/Task/Result/Settings/HistoryEntry / MediaAssetRole
│     ├─ Application/ # MediaResolveCoordinator / MediaDownloadCoordinator(串行批次+BatchCompleted事件) / MediaResolverRegistry / BrowserSessionAccessorProxy
│     ├─ Services/    # ShareLinkParser / MediaQualitySelector / MediaContentValidator(魔数+ISO BMFF品牌) / MediaDownloadService / MediaPreviewService / DownloadFileNameBuilder / MediaHashService / MediaFileFinalizer / BrowserCookieMatcher
│     ├─ Resolvers/   # DirectMediaResolver + Douyin/(DouyinPageParser / DouyinMediaResolver / DouyinMediaNormalizer / DouyinHttpPageResolver / DouyinDetailEndpointMatcher / DouyinTransportClassifier / DouyinTransportPreferenceState / CaptureDiagnostics)
│     └─ UI/          # MediaDownloadPage / MediaAssetGrid / DownloadQueueGrid / MediaPreviewControl / MediaSettingsForm
├─ Infrastructure/
│  ├─ Networking/   # SafeHttpRequestSender(逐跳校验+敏感头清除) / SafeUriValidator / HttpClientProvider / IDnsResolver
│  └─ Persistence/  # TextStateStore / MediaStateStore / JsonFileStore / StateMigrationService
└─ Platform/Windows/
   ├─ Clipboard|HotKeys|Input|Native/
   └─ EdgeCdp/      # 浏览器兜底：EdgeProcessManager / EdgeCdpBrowserSessionAccessor / CdpConnection / CdpTargetSession / EdgeCdpResourceDownloader / MediaPagePrefetcher / EdgeExecutableLocator / MediaCache
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

- **最新提交**：`32433b9 修复：实况图按 images[i].video 逐个解析并配对输出静态照片与动态视频`
- **测试**：574 断言全过（`dotnet run --project tests/Transfor.Tests`）；构建 0 警告 0 错误
- **诊断目录**：`%TEMP%\Transfor\diagnostics\`（解析完成 capture-*.json + 下载失败 failed-media-*；临时诊断代码在 CaptureDiagnostics / MediaFileFinalizer.SaveFailureSample，定位后移除）
- **已知边界**：未登录（not_exist_login_cookie）时视频可能返回封面/低清；Android Motion Photo / Apple Live Photo 封装未做（二期）；DASH/HLS 分段流不支持

## 六、开发注意事项（坑）

- 测试运行器 Program.cs：**file 类必须放文件末尾**（顶层语句之后）；测试计数用静态 TestCounter.Passed
- 勿用 PowerShell `-replace` 处理含 `\r\n` 字面量的文件（会误伤 C# 转义序列）——用 Edit 工具
- WinForms 控件必须在 STA 线程创建（测试用 RunSta）
- WebView2 已废弃，当前浏览器方案为 Edge CDP（Platform/Windows/EdgeCdp）
- 所有网络操作带 CancellationToken；禁止 .Result/.Wait()（生产代码）
- 修改状态存储方法内部已持久化，UI 不重复调 Save()
- 提交信息用中文，不推送

# AGENTS.md — Transfor 项目开发注意事项

## 项目概况

- .NET 10 WinForms 桌面应用（`Transfor.slnx`，`src/Transfor`），含三页：文本工具 / 媒体下载（抖音）/ 内置浏览器（WebView2）
- 目标框架 `net10.0-windows`，自包含 win-x64 发布
- 提交信息一律中文；未推送的本地工作保持 dev 分支

## 构建与测试（重要）

- 构建：`dotnet build Transfor.slnx`（要求 0 警告 0 错误）
- 测试：**测试项目是无框架控制台运行器**（`tests/Transfor.Tests`），**必须用 `dotnet run --project tests/Transfor.Tests` 执行**（输出 `All N tests passed.`），`dotnet test` 不会运行任何断言
- CI（release.yml）已按此配置：restore → build --no-restore → `dotnet run --no-build`
- 测试文件 `Program.cs` 中 `file` 类必须位于文件末尾；新增测试函数需在 Main 中注册调用
- 新增纯函数时尽量补离线断言（项目风格：解析/格式化逻辑抽纯函数 + 测试）

## WebView2 / 浏览器开发（本会话踩坑记录，务必遵守）

### 线程亲和（最高优先级）

- **CoreWebView2 成员只能在创建它的 UI 线程访问**；所有浏览器操作经 `BrowserService.RunOnUiAsync`（锚点 = 主窗体 `UiAnchor`）调度
- **`ConfigureAwait(false)` 会破坏 WebView2 调用链**：ExecuteScriptAsync / CallDevToolsProtocolMethodAsync 的 await 必须 `ConfigureAwait(true)`，否则下一轮调用点落到线程池线程（`CoreWebview2 members can only be accessed from the UI thread` + COM cast 异常）
- `Control.InvokeRequired` 在句柄未创建时恒为 false：调度前先 `EnsureUiAnchorReady`（句柄创建 + Application.MessageLoop 检查）
- 隐藏宿主（BrowserHostForm）启动预初始化（`EnsureHostCoreAsync`，构造器 UI 线程同步调用），`host.Show()`（Opacity=0）保证句柄/消息循环稳定

### ExecuteScriptAsync 的 Promise 陷阱

- **本机 WebView2 内核（151）与 SDK 1.0.4129.50 组合下，ExecuteScriptAsync 对异步 Promise 结果返回空对象 `{}`**（不等待 Promise 完成）
- 因此**下载禁用页面 fetch 脚本**，改用 **CDP 直取**：`Page.getFrameTree` → `Network.loadNetworkResource`（options: disableCache+includeCredentials）→ `IO.read` 循环（64KB/块）→ `IO.close`（见 `BrowserDownloadController`）
- 解析用的同步脚本（`BrowserCaptureSession`）正常；任何新增异步 JS 一律改"同步触发 + 全局状态轮询"或 CDP
- 页面 fetch 媒体 CDN 会被 CORS 拦截（`Failed to fetch`），CDP 网络层不受限

### WebView2 事件回调

- `NetworkCaptureService.OnResponseReceived` 必须包 try-catch：**WebResourceResponseReceived 的 Response 对象仅在回调期间有效**，重定向/流响应会抛 `COMException 0x80070585`（无效索引）
- 所有 CoreWebView2 事件回调（CDP 事件、NavigationCompleted）都要异常保护，单条失败忽略，绝不中断事件链

### JSON 解析防御（.NET 10 行为变化）

- **.NET 10 的 `JsonElement.TryGetProperty` 对非 Object 根元素（JSON null）会抛 `InvalidOperationException`（"requires an element of type 'Object'"），而不是返回 false**
- 解析任何外部 JSON（脚本返回、CDP 响应、策略文件）必须先判 `ValueKind == JsonValueKind.Object`，catch 需同时捕获 `JsonException` 和 `InvalidOperationException`
- `GetBoolean()`/`GetString()` 对 JSON null 的行为不同：GetBoolean 抛、GetString 返回 null——用 ValueKind 直接比较更安全

## 更新体系

- 版本唯一来源：`src/Transfor/Transfor.csproj` 的 `<Version>`（AppVersion.Current 读 InformationalVersion）；**打 tag 发布前必须同步版本号**
- 策略文件（GitHub Release 分支根目录）：`update-policy.json`（Stable）/ `update-policy.beta.json`（Beta），**latestVersion 每次发版必须更新**，否则旧版收不到更新提示
- 发布：push dev → merge Release → `git tag v0.14.x` → push tag（Actions 触发：构建 + 回归测试 + vpk 打包）
- 安装包命名：Stable = `Transfor-stable-Setup.exe`，Beta = `Transfor-beta-Setup.exe`（Velopack 按通道命名，不是按版本）
- 更新网络与媒体网络分离：策略源用 `HttpClientProvider.CreateForUpdates()`（系统代理），不共享媒体 HttpClient
- `UpdateVersion` 严格三段 SemVer（2 段/4 段拒绝）；更新策略按通道取文件（FetchAsync(channel)）
- 强制更新不进入主业务界面：启动检查完成后再显示主窗体（`ShowAfterStartupCheckAsync`）
- WinForms 窗体必须 `Controls.Add(root)` 挂载布局（UpdateNoticeForm 曾因遗漏导致空白窗口——有 UI 测试防回归）

## 抖音媒体解析/下载

- 解析链路：HTTP 直解析（匿名优先）→ TLS 被拒熔断 → 浏览器 CDP 捕获兜底；`/note/` 图文笔记匿名是登录墙（平台策略，不是 bug）
- 实况图：`images[i].url_list`（still）+ `images[i].video`（motion）按 PairId 配对，文件名 `_still`/`_motion`；浏览器兜底也保留配对（`DouyinMediaNormalizer.NormalizeCandidatesToPageData`）
- 音乐 URL（ies-music/.mp3）过滤；封面/头像/表情包等非作品媒体按关键词过滤
- 诊断文件：`%TEMP%\Transfor\diagnostics\`（capture-*.json 解析现场、crash-*.txt 崩溃现场）；**浏览器捕获失败路径也会写诊断**
- 全局异常兜底：`Program.cs` 的 ThreadException/UnhandledException 记录诊断 + 友好提示

## UI 约定

- 解析/下载进行中仅禁用"解析/下载"按钮，粘贴/清空/浏览保持可用；下载完成后全部恢复（`SetState` + `IsBusy` 防并发）
- 无效链接提示明确文案（空输入/无链接区分）；解析结果落 errorLabel（窗口底部红字）
- UI 状态测试用 `RunSta`（STA 线程）+ 挂起 FakeResolver（asyncResultFactory）+ DoEvents 泵消息（UI 线程同步等待会死锁）

## 发布前检查清单

1. csproj `<Version>` 与 tag 版本一致
2. 策略文件 latestVersion 已更新（两个通道）
3. 全量测试通过（`dotnet run --project tests/Transfor.Tests`）
4. build 0 警告 0 错误
5. 浏览器相关改动真机验证（测试用 fakes 无法覆盖真实 WebView2 行为）

# Transfor 后续架构升级与功能实施计划

## 1. 总体目标

在不破坏 Transfor 当前已有功能的前提下，逐步完成以下升级：

1. 建立稳定、可扩展的应用版本体系。
2. 支持联网检查更新。
3. 支持强制更新与非强制更新。
4. 接入自动下载安装及重启更新能力。
5. 为 Stable / Beta 更新通道预留能力。
6. 引入 Microsoft Edge WebView2 作为内置浏览器。
7. 建立浏览器 Profile、Cookie、网络请求捕获能力。
8. 将 WebView2 作为媒体解析的辅助渠道，而不是取代现有解析器。
9. 建立 Direct / Browser / Network Capture 多级媒体解析架构。
10. 最终重构当前 WinForms UI，形成现代化桌面应用界面。
11. UI 与业务逻辑解耦，为未来继续增加抖音、B站、小红书等解析器做好准备。

---

# 2. 总体实施原则

整个升级过程禁止一次性完成。

建议按照以下顺序推进：

```text
Phase 0
基础架构整理
    ↓
Phase 1
版本与更新策略基础设施
    ↓
Phase 2
自动更新与发布流程
    ↓
Phase 3
WebView2 浏览器模块
    ↓
Phase 4
浏览器辅助媒体解析
    ↓
Phase 5
现代 UI 基础框架
    ↓
Phase 6
逐页面迁移 UI
    ↓
Phase 7
整体稳定性、安全与发布完善
```

基本原则：

- 一个阶段一个独立分支。
- 一个阶段可以拆成多个 PR。
- 每个阶段完成后必须可以正常运行。
- 不允许因为后续规划而提前删除现有实现。
- 优先使用接口和适配层进行渐进迁移。
- UI 不允许直接包含媒体解析、网络请求、更新等核心业务逻辑。
- WebView2 不允许直接取代 `IMediaResolver`。
- 大文件下载仍由 C# 下载服务负责。
- 浏览器负责页面执行、登录态、Cookie 和网络资源发现。
- 自动更新失败不能导致普通版本用户无法打开程序。
- 强制更新只有在成功取得远程更新策略后才能触发。

---

# Phase 0：现有架构整理与升级准备

## 目标

在增加新功能前整理 Transfor 当前项目，使后续更新、浏览器和 UI 模块不会继续堆积在 WinForms Form 中。

本阶段不改变现有用户功能。

---

## Task 0.1：整理项目模块边界

建议逐步形成：

```text
Transfor
│
├─ Application
│
├─ Media
│  ├─ Abstractions
│  ├─ Models
│  ├─ Resolvers
│  └─ Downloads
│
├─ Browser
│
├─ Updates
│
├─ History
│
├─ Settings
│
├─ Infrastructure
│
└─ UI
```

不要求马上建立多个独立 `.csproj`。

第一阶段可以仍然保持单项目，只整理目录和依赖方向。

---

## Task 0.2：确认媒体核心契约

保留并完善：

```text
IMediaResolver
MediaResolveCoordinator
MediaResolveResult
MediaItem
```

统一媒体类型。

例如：

```text
Image
Video
LivePhoto
Audio
Unknown
```

其中 LivePhoto 不应该只表示一张图片。

建议结构支持：

```text
LivePhoto
├─ StillImageUrl
└─ MotionVideoUrl
```

为后续抖音实况图解析做好基础。

---

## Task 0.3：建立统一下载服务

目标：

```text
IMediaDownloadService
MediaDownloadService
```

下载服务负责：

- 文件下载
- 并发控制
- 进度
- 重试
- 取消
- 文件名
- 下载目录
- 重复文件处理
- 下载历史

Resolver 只负责：

> 找到媒体。

Downloader 只负责：

> 下载媒体。

两者禁止混合。

---

## Task 0.4：清理 UI 与业务逻辑耦合

MainForm / UserControl 不应该直接：

```text
HttpClient.GetAsync()
解析抖音 JSON
写下载文件
读写 Cookie
检查 GitHub
```

全部逐步调用 Service。

---

## Phase 0 验收标准

必须满足：

- 当前已有功能全部可用。
- 抖音解析没有行为回退。
- 下载功能没有行为回退。
- 历史记录正常。
- 设置正常。
- 托盘功能正常。
- 全局快捷键正常。
- 项目可以正常 Build / Publish。
- UI 层不再承担主要网络和解析业务。

完成后打标签：

```text
architecture-baseline
```

---

# Phase 1：版本与更新策略基础设施

## 目标

建立更新系统的数据模型和判断逻辑。

本阶段：

> 只完成“检查是否有更新”。

暂时不要自动覆盖程序。

---

# Task 1.1：建立应用版本规范

采用 Semantic Versioning：

```text
MAJOR.MINOR.PATCH
```

例如：

```text
1.0.0
1.1.0
1.1.1
2.0.0
```

测试版允许：

```text
1.2.0-beta.1
1.2.0-beta.2
1.2.0-rc.1
```

禁止同时维护多套无关联版本来源。

应用版本应统一来源于项目配置。

---

# Task 1.2：建立 Updates 模块

建议：

```text
Updates
├─ IUpdateService.cs
├─ UpdateService.cs
├─ UpdatePolicy.cs
├─ UpdateCheckResult.cs
├─ UpdateStatus.cs
├─ UpdateChannel.cs
└─ VersionComparer.cs
```

---

## UpdateStatus

至少包含：

```text
UpToDate
OptionalUpdate
RequiredUpdate
CheckFailed
Disabled
```

---

# Task 1.3：定义远程更新策略

建议：

```json
{
  "enabled": true,

  "latestVersion": "1.5.0",
  "minimumVersion": "1.3.0",

  "channel": "stable",

  "releaseDate": "2026-08-07",

  "title": "Transfor 1.5.0",

  "message": "新增媒体解析功能并修复部分问题。",

  "changelog": [
    "新增功能 A",
    "优化功能 B",
    "修复问题 C"
  ],

  "downloadUrl": "",

  "sha256": ""
}
```

---

# Task 1.4：实现版本判断

逻辑必须为：

```text
当前版本 >= latestVersion

→ UpToDate
```

```text
minimumVersion <= 当前版本 < latestVersion

→ OptionalUpdate
```

```text
当前版本 < minimumVersion

→ RequiredUpdate
```

---

# Task 1.5：更新检查失败策略

必须特别处理：

```text
无网络
DNS 错误
GitHub 不可访问
HTTPS 错误
JSON 损坏
服务器超时
```

这些情况：

```text
→ CheckFailed
```

不能自动判定：

```text
→ RequiredUpdate
```

即：

> 网络错误绝对不能导致程序被锁死。

---

# Task 1.6：更新提示 UI

普通更新：

```text
发现新版本 1.5.0

更新内容：
...

[稍后更新]
[立即更新]
```

强制更新：

```text
当前版本已停止支持。

当前版本：1.2.0
最新版本：1.5.0

[重新检查]
[立即更新]
[退出]
```

强制更新时不进入主业务界面。

---

# Phase 1 验收

使用 Mock 数据测试：

```text
Current 1.5.0
Latest 1.5.0
→ UpToDate
```

```text
Current 1.4.0
Latest 1.5.0
Minimum 1.3.0
→ OptionalUpdate
```

```text
Current 1.2.0
Latest 1.5.0
Minimum 1.3.0
→ RequiredUpdate
```

```text
更新服务器无法连接
→ CheckFailed
→ 应用仍可运行
```

---

# Phase 2：自动更新与发布体系

## 目标

在 Phase 1 基础上实现真正：

```text
发现更新
→ 下载
→ 安装
→ 重启
```

建议采用：

```text
Velopack
```

不要自己写 EXE 覆盖更新程序。

---

# Task 2.1：接入 Velopack

负责：

- 安装
- 更新包
- 文件替换
- 应用关闭
- 新版本启动
- 更新生命周期

应用本身不要直接覆盖正在运行的 EXE。

---

# Task 2.2：GitHub Releases 作为第一阶段更新源

第一阶段架构：

```text
GitHub Repository

├─ update-policy.json
│
└─ Releases
   ├─ Transfor 1.0.0
   ├─ Transfor 1.1.0
   └─ Transfor 1.2.0
```

客户端：

```text
UpdateService
     ↓
读取 update-policy
     ↓
判断版本
     ↓
Velopack
     ↓
GitHub Releases
```

---

# Task 2.3：下载过程 UI

要求支持：

```text
正在下载 Transfor 1.5.0

████████░░░░ 68%

34.2 MB / 50 MB

[取消]
```

下载完成：

```text
更新已准备完成。

[立即重启并更新]
```

可选更新允许：

```text
稍后重启
```

强制更新则要求完成更新后再进入主程序。

---

# Task 2.4：增加 Stable / Beta 通道

Settings：

```text
更新通道

● Stable 稳定版
○ Beta 测试版
```

默认：

```text
Stable
```

Beta 用户允许收到：

```text
1.6.0-beta.1
```

普通用户只收到：

```text
1.6.0
```

---

# Task 2.5：GitHub Actions 自动发布

最终目标：

```text
git tag v1.6.0
        ↓
push
        ↓
GitHub Actions
        ↓
dotnet restore
        ↓
dotnet test
        ↓
dotnet publish
        ↓
Velopack pack
        ↓
GitHub Release
        ↓
上传安装包/更新包
```

正式发布不再人工复制文件。

---

# Task 2.6：更新源抽象

禁止让业务代码依赖 GitHub。

例如：

```text
IUpdateSource
```

以后可以存在：

```text
GitHubUpdateSource

HttpUpdateSource

OssUpdateSource
```

这样未来从 GitHub 迁移：

```text
阿里云 OSS
腾讯云 COS
自建服务器
```

不需要修改 UI 和主要更新逻辑。

---

# Phase 2 验收

完整模拟：

```text
安装 1.0.0
↓
发布 1.1.0
↓
启动 1.0.0
↓
检测到 1.1.0
↓
下载
↓
退出
↓
更新
↓
自动启动
↓
显示 1.1.0
```

同时验证：

- 安装失败不会破坏旧版本。
- 下载中断可以重新更新。
- 网络失败可以正常运行旧版本。
- 强制更新逻辑正确。
- 可选更新可以跳过。

---

# Phase 3：WebView2 内置浏览器

## 目标

给 Transfor 增加真正的 Edge Chromium 浏览器环境。

本阶段只建设 Browser 基础能力。

暂时不要修改主解析器。

---

# Task 3.1：接入 WebView2

增加：

```text
BrowserView
```

提供：

```text
地址栏

后退
前进
刷新
停止

页面显示
```

---

# Task 3.2：建立 BrowserService

建议：

```text
Browser
├─ IBrowserService.cs
├─ BrowserService.cs
├─ BrowserProfileService.cs
├─ BrowserCookieService.cs
├─ BrowserNavigationService.cs
└─ BrowserView.cs
```

禁止业务 Resolver 直接创建 WebView2。

---

# Task 3.3：建立独立 WebView2 Profile

例如：

```text
%LocalAppData%
└─ Transfor
   └─ Browser
      └─ UserData
```

作用：

```text
Cookie
LocalStorage
缓存
登录状态
```

用户登录一次抖音后，后续尽量保持登录状态。

---

# Task 3.4：Cookie 管理

BrowserCookieService 至少支持：

```text
GetCookiesAsync(uri)

ClearCookiesAsync()

ClearAllBrowserDataAsync()
```

后续允许：

```text
WebView2 Cookie
      ↓
转换
      ↓
HttpClient CookieContainer
```

但本阶段只建设接口。

---

# Task 3.5：浏览器隐私设置

Settings 增加：

```text
浏览器数据

[清除 Cookie]
[清除缓存]
[清除全部浏览器数据]
```

---

# Phase 3 验收

必须成功：

```text
Transfor
↓
打开 Browser
↓
访问 douyin.com
↓
正常执行 JS
↓
可以登录
↓
关闭 Transfor
↓
重新打开
↓
Profile 正常持久化
```

---

# Phase 4：WebView2 媒体解析辅助系统

这是一个较大的独立阶段。

建议再拆成 4A、4B、4C。

---

# Phase 4A：BrowserResolver

## 目标

允许现有 Direct Resolver 失败后调用浏览器。

解析策略：

```text
MediaResolveCoordinator

       ↓

DirectResolver
       ↓
成功？
├─ Yes → 返回
└─ No
     ↓
BrowserResolver
```

---

# Task 4A.1

建立：

```text
IBrowserMediaResolver
BrowserMediaResolver
```

职责：

```text
打开页面
等待页面加载
获得最终 URL
读取页面必要数据
执行有限 JS
返回 MediaResolveResult
```

---

# Task 4A.2

不能直接删除当前：

```text
DouyinResolver
```

现有解析仍然作为第一优先级。

原因：

```text
Direct Resolver
速度快
资源占用低
无需启动 Chromium
```

WebView2 作为 fallback。

---

# Phase 4B：Network Capture

## 目标

允许 Transfor 观察浏览器真正访问的资源。

建立：

```text
NetworkCaptureService
```

监听：

```text
WebResourceRequested

WebResourceResponseReceived
```

初步记录：

```text
URL
Method
Content-Type
Status
ResourceType
```

---

# Task 4B.1：建立 MediaSniffer

```text
IMediaSniffer
MediaSniffer
```

识别：

```text
image/jpeg
image/png
image/webp

video/mp4
video/webm

application/json
```

但：

> 不允许看到 mp4 就直接认为是作品视频。

必须关联：

```text
当前作品
请求来源
页面状态
接口 JSON
```

避免把广告、头像、预加载视频全部识别为用户媒体。

---

# Phase 4C：CDP Network Capture

如果普通 WebView2 网络事件不足，再增加：

```text
Chrome DevTools Protocol
```

监听：

```text
Network.requestWillBeSent

Network.responseReceived

Network.loadingFinished
```

此阶段作为高级能力。

不要在 4A 就立即引入 CDP。

---

# Phase 4D：实况图专项解析

重点解决：

```text
Live Photo / 实况图
```

数据模型：

```text
LivePhotoMediaItem

├─ StillImage
└─ MotionVideo
```

如果：

```text
StillImage != null
MotionVideo != null
```

则 UI 显示：

```text
LIVE
```

下载时可以：

```text
下载静态图

下载动态部分

下载完整实况资源
```

根据后续实际格式决定最终封装形式。

---

# Phase 4 最终解析链

目标：

```text
MediaResolveCoordinator
         │
         ▼
    DirectResolver
         │
        Fail
         ▼
    BrowserResolver
         │
        Fail
         ▼
 NetworkCaptureResolver
         │
        Fail
         ▼
    ResolveFailed
```

后续可以增加：

```text
DouyinDirectResolver

DouyinBrowserResolver

DouyinNetworkResolver
```

而不修改 UI。

---

# Phase 4 验收

至少覆盖：

```text
普通视频

单张图片

多张图片

实况图

多实况图

静态图 + 实况图混合
```

测试：

```text
原 Direct Resolver 成功
→ 不应启动浏览器
```

```text
Direct Resolver 失败
→ 自动 Browser fallback
```

```text
Browser 成功
→ 返回统一 MediaResolveResult
```

---

# Phase 5：现代 UI 重构基础框架

到这个阶段才开始真正重做 UI。

不要提前。

---

# UI 技术路线

推荐：

```text
WinForms Host
+
WebView2
+
HTML/CSS/JavaScript
```

业务仍然：

```text
C#
```

WebView2 负责：

```text
界面渲染
```

而不是：

```text
核心业务逻辑
```

---

# Phase 5A：建立新的 App Shell

目标：

```text
┌──────────────────────────────────┐
│ Transfor                         │
├────────┬─────────────────────────┤
│ 首页   │                         │
│        │                         │
│ 下载   │       Page Content      │
│        │                         │
│ 浏览器 │                         │
│        │                         │
│ 历史   │                         │
│        │                         │
│ 设置   │                         │
└────────┴─────────────────────────┘
```

只建立：

```text
导航
页面容器
主题
窗口框架
```

暂时不迁移复杂功能。

---

# Phase 5B：建立 UI Bridge

定义严格的：

```text
Web UI
   ↕
App Bridge
   ↕
C# Application Services
```

例如：

```text
resolveMedia

startDownload

cancelDownload

getHistory

getSettings

saveSettings

checkUpdate
```

---

# 重要安全规则

本地 App UI WebView 与浏览互联网 WebView：

> 必须分离。

例如：

```text
AppWebView
```

只负责加载 Transfor 自己的：

```text
HTML / CSS / JS
```

允许调用：

```text
C# Bridge
```

---

另一个：

```text
BrowserWebView
```

负责：

```text
douyin.com
bilibili.com
其他互联网网站
```

禁止直接调用：

```text
删除文件
启动程序
读取本地文件
修改设置
执行任意 C# API
```

两种 WebView 不允许混用权限。

---

# Phase 5C：建立 Design System

先制定再开发。

包括：

```text
Typography

Spacing

Border Radius

Shadow

Button

Input

Card

Dialog

Toast

Progress

Tabs

Sidebar

Context Menu
```

建议整体采用：

```text
Windows 11
+
Fluent Design
+
现代简洁工具类应用
```

而不是追求花哨网页效果。

---

# Phase 5D：主题

支持：

```text
跟随系统

浅色

深色
```

并确保：

```text
所有页面统一主题变量
```

禁止每个页面自行设置颜色。

---

# Phase 5 验收

首先只做：

```text
新的 Shell
+
空页面
+
主题
+
C# Bridge
```

不要一开始同时迁移媒体下载功能。

---

# Phase 6：逐页面迁移

禁止一次把整个 WinForms UI 替换。

建议顺序：

---

## Phase 6.1 首页

迁移：

```text
快捷操作
最近记录
软件版本
更新状态
```

---

## Phase 6.2 媒体解析页面

优先级最高。

最终效果：

```text
输入链接

[粘贴]
[解析]

↓
作品信息

↓
媒体卡片

□ 图片
□ LIVE
□ 视频

↓
全选
下载选中
```

媒体卡片支持：

```text
预览图
类型
分辨率
文件大小
下载状态
```

---

## Phase 6.3 下载管理

增加：

```text
等待中

下载中

已完成

失败
```

以及：

```text
速度

进度

已下载大小

取消

重试

打开文件

打开文件夹
```

---

## Phase 6.4 历史记录

统一：

```text
媒体历史

转换历史

下载历史
```

并提供：

```text
搜索
删除
清空
重新执行
```

---

## Phase 6.5 浏览器

迁移 BrowserView：

```text
后退
前进
刷新
地址
```

并可增加：

```text
当前页面检测到 X 个媒体

[查看媒体]
```

---

## Phase 6.6 设置

整理为：

```text
常规

下载

浏览器

更新

外观

快捷键

高级
```

---

# Phase 7：发布、安全与稳定性

完成主要功能以后再进行。

---

# Task 7.1：日志系统

建议至少有：

```text
Application Log

Update Log

Browser Log

Media Resolve Log

Download Log
```

敏感数据：

```text
Cookie
Token
完整认证 Header
```

禁止写入普通日志。

---

# Task 7.2：错误分类

统一错误模型，例如：

```text
NetworkError

ParseError

BrowserError

DownloadError

UpdateError

PermissionError
```

UI 显示用户可理解信息。

日志保存技术细节。

---

# Task 7.3：崩溃恢复

特别测试：

```text
下载过程中关闭程序

浏览器初始化失败

WebView2 Runtime 不存在

更新中断

配置文件损坏

历史文件损坏
```

程序不能因此无法启动。

---

# Task 7.4：WebView2 Runtime

启动时检查：

```text
WebView2 Runtime
```

如果不存在：

```text
提供明确提示
```

不要直接崩溃。

---

# Task 7.5：下载安全

最终媒体 URL 必须经过：

```text
SafeHttpRequestSender
```

或统一安全请求层。

至少限制：

```text
file://

localhost

127.0.0.1

内网地址

非 HTTP/HTTPS URI
```

避免媒体 URL 被利用访问本地服务。

---

# Task 7.6：更新包完整性

下载后：

```text
SHA-256
```

或依赖 Velopack 的签名/包验证机制。

不能下载完成直接执行未知内容。

---

# 3. 推荐 Git 分支拆分

建议后续：

```text
feature/update-foundation

feature/velopack-updater

feature/update-channels

feature/webview2-browser

feature/browser-profile

feature/browser-media-resolver

feature/network-media-capture

feature/live-photo-support

feature/new-ui-shell

feature/new-ui-media-page

feature/new-ui-downloads

feature/new-ui-history

feature/new-ui-settings

hardening/release-security
```

每个分支：

```text
开发
↓
Build
↓
Test
↓
人工验收
↓
合并 dev
```

稳定后：

```text
dev
↓
release
↓
main
```

---

# 4. AI 每次执行任务时必须遵守的规则

以后给 AI 一个 Phase 时，同时附加以下要求：

## 第一条

执行前先读取：

```text
README
docs
当前目录结构
相关 Service
相关 Tests
```

禁止根据旧版本假设代码结构。

---

## 第二条

先输出：

```text
现状分析

计划修改文件

新增文件

潜在风险
```

然后再修改代码。

---

## 第三条

禁止顺带重构无关代码。

例如执行：

```text
Phase 3 WebView2
```

禁止顺便：

```text
重写下载器
重做设置页面
修改更新系统
```

---

## 第四条

尽量保持：

```text
向后兼容
```

新实现成熟以前保留旧实现。

---

## 第五条

每个 Phase 完成必须：

```text
dotnet restore

dotnet build

dotnet test
```

如果项目存在发布流程还要：

```text
dotnet publish
```

---

## 第六条

AI 必须列出：

```text
修改内容

为什么修改

测试结果

尚未解决的问题

下一阶段建议
```

---

# 5. 实际执行优先级

按照目前 Transfor 的开发状态，建议：

## 第一优先级

```text
Phase 0
架构整理
```

尤其先保证：

```text
MediaResolver

MediaResolveCoordinator

DownloadService
```

边界稳定。

---

## 第二优先级

```text
Phase 1
版本检查
```

这部分改动小，可以较早加入。

---

## 第三优先级

```text
Phase 2
Velopack 自动更新
```

建议尽早完成。

因为等用户已经大量使用 ZIP / 单 EXE 绿色版以后再迁移安装体系会更加麻烦。

---

## 第四优先级

```text
Phase 3
WebView2
```

完成浏览器基础能力。

---

## 第五优先级

```text
Phase 4
Browser Resolver
+
Network Capture
+
Live Photo
```

重点提高抖音等复杂网页的解析稳定性。

---

## 第六优先级

```text
Phase 5
新 UI 框架
```

此时后端业务接口已经基本稳定，UI 重写风险明显降低。

---

## 第七优先级

```text
Phase 6
逐页面迁移
```

---

## 第八优先级

```text
Phase 7
发布与稳定性强化
```

---

# 6. 推荐版本里程碑

可以将上述开发映射为版本。

## Transfor 0.x

当前开发阶段：

```text
0.5
媒体解析基础

0.6
下载体系

0.7
Live Photo

0.8
WebView2 Browser

0.9
自动更新
```

---

## Transfor 1.0

目标：

```text
稳定媒体解析

批量下载

WebView2

自动更新

现代化 UI

安装程序

Stable Channel
```

这时再正式定义：

```text
Transfor 1.0.0
```

---

## Transfor 1.1

增加：

```text
Beta Channel

网络媒体捕获

高级浏览器解析
```

---

## Transfor 1.2+

逐渐增加：

```text
Bilibili

小红书

其他平台 Resolver

插件化媒体解析
```

---

# 7. 最终目标架构

```text
                         Transfor
                            │
              ┌─────────────┴──────────────┐
              │                            │
              ▼                            ▼
          AppWebView                  Application
        Modern Web UI                     │
              │               ┌────────────┼─────────────┐
              │               │            │             │
              ▼               ▼            ▼             ▼
          App Bridge        Media       Download       Update
                              │          Service       Service
                              │
                              ▼
                    MediaResolveCoordinator
                              │
                 ┌────────────┼──────────────┐
                 │            │              │
                 ▼            ▼              ▼
              Direct       Browser        Network
             Resolver      Resolver        Resolver
                               │              │
                               └──────┬───────┘
                                      ▼
                              BrowserService
                                      │
                            Browser WebView2
                                      │
                      ┌───────────────┼───────────────┐
                      ▼               ▼               ▼
                   Profile          Cookie          CDP
```

核心原则最终仍然保持：

```text
UI
不负责业务

Browser
不负责正式文件管理

Resolver
只负责发现媒体

DownloadService
负责下载

UpdateService
负责版本更新

Application Layer
负责协调业务
```

---

# 8. 当前不要做的事情

现阶段明确禁止：

### 不要直接全部重写成 Electron

没有必要。

现有 C#/.NET 能力应该保留。

---

### 不要把所有媒体解析都改成 WebView2

Direct Resolver 必须保留。

---

### 不要自己开发 EXE 覆盖式更新器

使用成熟更新框架。

---

### 不要同时做 WebView2 和整套 UI 重写

先完成 Browser 基础设施。

---

### 不要让互联网网页直接获得 C# 高权限 Bridge

AppWebView 和 BrowserWebView 必须安全隔离。

---

### 不要因为“未来可能需要服务器”现在就做后端

当前：

```text
GitHub Releases
+
静态 update-policy.json
```

足够。

未来需要：

```text
账号
授权
灰度
设备管理
远程 Feature Flag
```

时再考虑 ASP.NET Core 后端。

---

# 9. 建议 AI 实际执行顺序

正式开始后依次向 AI 下达：

```text
任务 1
执行 Phase 0：架构整理
```

验收后：

```text
任务 2
执行 Phase 1：版本检查基础设施
```

然后：

```text
任务 3
执行 Phase 2：Velopack 自动更新
```

然后：

```text
任务 4
执行 Phase 3：WebView2 Browser
```

然后：

```text
任务 5
执行 Phase 4A：BrowserResolver
```

然后：

```text
任务 6
执行 Phase 4B：Network Capture
```

然后：

```text
任务 7
执行 Phase 4D：Live Photo 完整支持
```

然后：

```text
任务 8
执行 Phase 5A～5D：现代 UI 基础框架
```

然后逐页面执行：

```text
任务 9
媒体页面

任务 10
下载页面

任务 11
历史页面

任务 12
浏览器页面

任务 13
设置页面
```

最终：

```text
任务 14
Phase 7：稳定性、安全、正式发布检查
```

---

# 10. 最重要的开发原则

整个升级不能理解成：

```text
重写 Transfor
```

而应该理解成：

```text
当前 Transfor
      ↓
业务服务化
      ↓
增加更新能力
      ↓
增加浏览器能力
      ↓
增加高级解析能力
      ↓
替换表现层
      ↓
形成新的 Transfor
```

这样既可以保留当前已经验证过的代码，又可以逐步得到：

```text
现代 UI
+
Edge WebView2
+
Direct / Browser / Network 多级解析
+
Live Photo
+
稳定下载器
+
远程更新
+
强制/可选更新
+
Stable/Beta
+
未来多平台扩展
```

并且每一步都可以单独测试、单独提交、单独回滚。
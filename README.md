# Transfor

Windows 桌面工具。基于 .NET 10 + WinForms 构建：文本转换常驻系统托盘，通过全局快捷键随时呼出历史记录面板；媒体下载支持解析抖音分享链接并下载图片/视频。

## 功能

- **引号转换**：英文双引号 `"` → `'`，中文双引号 `“ ”` → `‘ ’`
- **去除空格**：移除半角空格 ` ` 与全角空格 `　`，保留换行与制表符
- **历史记录**：两种功能独立记录转换历史，自动裁剪，上限可在 1–500 之间配置（默认 100）
- **全局快捷键**：默认 `Alt+Q` 呼出历史面板（可在设置中修改），选中历史项后自动粘贴到呼出前的目标窗口
- **系统托盘**：关闭主窗口时最小化到托盘，双击托盘图标重新打开
- **文本状态持久化**：设置、界面状态与文本历史分别保存到 `settings.json`、`ui-state.json` 与 `text-history.json`，文件损坏时自动回退到默认值
- **媒体下载**：粘贴抖音分享文本/链接，解析单视频与多图作品（保持原始顺序），选择单个或全部媒体并流式下载当前会话可访问的最高质量版本；支持队列进度、取消与失败重试
- **媒体持久化**：媒体设置与下载历史独立保存到 `media-settings.json` 与 `download-history.json`，不混入文本状态

> 「最高质量」指当前公开页面或当前浏览器会话可访问的、可直接下载的媒体版本；不承诺作者原始文件、无水印或可访问私密/删除/付费作品。第一版不支持 DASH/HLS 分段流合并，发现分段媒体时会明确提示。

## 项目结构

```text
src/Transfor/
├── App/                                     # 入口、组合根、应用生命周期与路径
│   ├── Program.cs                           # 入口：初始化 WinForms 并启动消息循环
│   ├── AppBootstrapper.cs                   # 组合根：组装状态存储、热键、粘贴协调器
│   ├── AppServices.cs                       # 服务容器：持有各单例服务并随应用释放
│   ├── AppPaths.cs                          # 状态文件路径集合（%LOCALAPPDATA%\Transfor）
│   └── TransforApplicationContext.cs        # 应用上下文：主窗口/历史面板/托盘/热键协调
├── Shell/                                   # 主窗口外壳与页面接口
│   ├── IFeaturePage.cs                      # 功能页契约（Id、名称、视图、激活回调）
│   └── MainForm.cs                          # 主窗口：导航栏 + 内容区，关闭时隐藏到托盘
├── Features/                                # 按功能组织的模块
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
│       ├── Models/
│       │   ├── AppSettings.cs               # 设置（快捷键、历史上限 1–500，默认 100）
│       │   ├── HotKeyBinding.cs             # 快捷键绑定（校验、显示、Win32 注册格式）
│       │   └── TextUiState.cs               # 界面状态（最近查看的工具）
│       └── UI/
│           └── SettingsForm.cs              # 设置窗口（热键、上限、清空历史）
│   └── MediaDownload/                       # 媒体下载：契约、模型、协调器、服务、解析器与 UI
│       ├── Contracts/                       # IMediaResolver / IMediaDownloadService / IBrowserSessionAccessor 等
│       ├── Models/                          # MediaAsset/MediaVariant/ResolvedMediaPost/下载设置与历史等
│       ├── Application/                     # MediaResolveCoordinator / MediaDownloadCoordinator / 解析注册中心
│       ├── Services/                        # ShareLinkParser / 质量选择 / 流式下载 / 预览 / 文件名与哈希
│       ├── Resolvers/                       # DirectMediaResolver + Douyin/（静态解析与浏览器兜底）
│       └── UI/                              # MediaDownloadPage / 资产表 / 队列表 / 设置窗体 / 预览控件
├── Infrastructure/                          # 基础设施
│   ├── Networking/                          # 安全网络：DNS 抽象 / URI 校验 / 逐跳重定向 / HttpClient 工厂
│   └── Persistence/                         # JSON 状态存储与旧状态迁移
│       ├── TextStateStore.cs                # 文本状态存储：原子写入、损坏自动回退默认值
│       ├── MediaStateStore.cs               # 媒体设置与下载历史存储（独立文件、串行写入）
│       ├── JsonFileStore.cs                 # 通用原子 JSON 文件读写
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
    └── WebView/                             # WebView2 浏览器会话（独立用户数据目录、UI 线程调度）

tests/
└── Transfor.Tests/                          # 控制台式测试运行器（无框架依赖）
    ├── Program.cs                           # 全部测试断言（转换器/迁移/存储/热键/粘贴/媒体解析与下载）
    ├── Fixtures/MediaDownload/              # 脱敏的抖音页面与结构化数据样本
    └── Transfor.Tests.csproj                # 引用主项目，目标框架 net10.0-windows
```

应用状态位于 `%LOCALAPPDATA%\Transfor\`：

- 文本：`settings.json`、`ui-state.json`、`text-history.json`（首次启动新版时迁移旧 `state.json`，成功后备份为 `state.v1.backup.json`；迁移中断会优先使用仍可读取的旧状态恢复）
- 媒体：`media-settings.json`（默认下载目录/并发数/质量策略等）、`download-history.json`（批次下载记录）
- WebView2 浏览器数据（Cookie/权限/缓存）仅存于 `WebView2\Douyin\`，不写入普通 JSON；下载历史不保存临时 CDN URL、Cookie 或授权信息
## 环境要求

- Windows 10/11
- .NET 10 SDK
- 浏览器兜底解析需要 WebView2 Runtime（Windows 10/11 通常自带；缺失时仅影响「浏览器登录」解析，直接下载不受影响）

## 构建与运行

```bash
# 构建
dotnet build Transfor.slnx

# 运行
dotnet run --project src/Transfor

# 运行测试（输出 "All N tests passed."）
dotnet run --project tests/Transfor.Tests
```

## 使用说明

1. 首次启动显示主窗口，输入文本后自动实时转换，点击「复制结果」写入剪贴板并记入历史。
2. 关闭主窗口不会退出程序，进程驻留系统托盘。
3. 在任意程序中按下 `Alt+Q`，历史面板会出现在鼠标附近；单击或回车即可把选中结果粘贴回原窗口。
4. 托盘菜单提供「打开主窗口 / 设置 / 退出」。
5. 切换到「媒体下载」页：粘贴抖音分享文本或直接图片/视频链接 → 「解析」；页面需要登录或验证码时点击「浏览器登录」在浏览器窗口中完成操作。
6. 解析后勾选要下载的媒体（可全选），选择保存目录并点击「下载所选」；队列中可取消单个任务，失败行可重试。
7. 关闭主窗口到托盘时下载继续；真正退出且有任务时会先确认再取消任务。
8. 合法使用提示：仅下载你有权访问的公开内容，不绕过登录、验证码或访问控制。

## Rider 调试提示

若 Rider 调试启动报 `Unable to detect target platform of file`：

- 升级到 Rider 2025.1+（.NET 10 支持），并在 `File > Invalidate Caches / Restart` 后重新构建；
- 或先 `dotnet build` 确认 `src/Transfor/bin/Debug/net10.0-windows/Transfor.exe` 已生成。

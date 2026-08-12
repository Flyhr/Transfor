# Transfor Focused Flow ModernUi 设计规格

## 已确认的视觉基准

以用户确认的“媒体 / 解析成功并下载中”设计图及其 Figma Dev Mode 标注为基准：白色工作区、176px 原生侧栏、青绿流程轨，以及底部内嵌下载队列。该图只作为视觉方向，不新增业务字段或下载状态。

## 目标与边界

- 仅改 `src/Transfor/Features/ModernUi` 的 Web UI 和原生宿主侧栏；旧 WinForms 预览 UI 不变。
- 固定浅色 Focused Flow：文字 `#0B1220`、次级文字 `#475569`、边框 `#E2E8F0`、深操作色 `#0F766E`、操作弱背景 `#F0F7FA`；不提供深色或主题切换入口。
- 实际字体使用 `"Segoe UI Variable", "Microsoft YaHei UI", sans-serif`；Figma/设计稿使用 Inter 不影响代码字体栈。
- 不改变 App Bridge 公开协议、下载/历史/设置数据格式、WebView2 Profile 与外部导航安全边界。

## 信息架构

原生侧栏仅保留：工作台、媒体、浏览器、历史、设置。工作台取代首页；删除下载管理路由、导航和独立页面。

- 工作台：两种文本工具，输入/结果约 1:1 双栏，小窗口单栏；复制和清空留在工具内；最近文本降权；没有跨页入口或最近媒体。
- 媒体：链接输入 → 解析结果 → 底部可折叠下载队列。队列消费现有下载快照与事件；保留取消、重试、打开文件、打开目录。历史只在历史页展示。
- 浏览器：64px 窄工具栏，保留返回/前进/刷新/地址/停止及媒体检测跳转；原生浏览器只覆盖内容区，绝不覆盖侧栏。
- 历史：文本/媒体分段标签与统一搜索；进入页面自动刷新；删除刷新按钮。
- 设置：分类左栏（常规、下载、网络、快捷键、浏览器数据、更新）+ 单一内容右栏；保存栏固定在底部；浏览器清理和更新检查留在对应分类。

## 组件与交互

- 所有 CSS token 使用 `--ff-*` 前缀；卡片圆角 12px、主控件高度 36px、主内容内边距 32px、流程区间距 24px。
- 侧栏固定 176px，含默认、悬停、焦点、活动与媒体活动徽标；图标使用宿主轻量矢量绘制，不增加业务导航；所有键盘焦点使用明显 `:focus-visible`。
- 流程轨为青绿/天空蓝连续细线；动效约 160ms，并在 `prefers-reduced-motion` 下关闭。
- 媒体成功态固定显示成功提示、全选、选择数量和批量下载；结果卡片四列，只有 960px 以下改两列、720px 以下改单列。
- 每张媒体卡片包含缩略图、覆盖式复选框、类型/尺寸/时长信息和独立下载按钮；独立下载不改变其余选择状态。
- 粘贴和解析输入使用安全 URL 提取，只把首个 `http(s)` URL 保留在输入框和解析请求中。
- 解析/下载时仅禁用相应解析或下载按钮，粘贴、清空、目录浏览和导航仍可用。
- 下载队列在媒体页同屏呈现横向任务行，消费现有快照与 `downloadProgress`、`taskCompleted`、`batchCompleted`、`cancelTask`、`retryTask`、`openFile`、`openFolder` 事件/命令。
- 960px 宽度时工作台及设置不可横向溢出，双栏改为单栏。

## 资源与宿主

Web UI 从内联单文件改为 `index.html`、`styles.css`、`app.js` 三个程序集嵌入资源；临时写入目录中使用相对资源引用加载。`AppShellForm` 侧栏固定 176px、隐藏底部版本号、浏览器工具栏预留 64px，并继续提供 `setBrowserVisible`、`setActiveNav`、`setTheme` 的安全 Bridge 行为（无 UI 主题入口）。

## 验收

离线断言覆盖三资源加载、无 downloads 路由/页面、媒体成功提示、`media-card`/`media-card-download` 结构、选择数量、连续流程轨、四列布局且 1100px 不降列、URL 提取函数、下载队列、工作台无跨页入口、历史结构、设置分类与固定保存栏，以及既有导航、主题 Bridge、取消/重试与下载事件。完成后运行 `dotnet run --project tests/Transfor.Tests -c Release`、`dotnet build Transfor.slnx -c Release` 和 self-contained win-x64 发布，均须无失败、无警告、无错误。

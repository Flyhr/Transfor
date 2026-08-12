# Transfor Focused Flow ModernUi Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `subagent-driven-development` (recommended) or `executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 ModernUi 重构为固定浅色的 Focused Flow，保留现有 Bridge 和业务能力。

**Architecture:** 将 web 页面分为语义 HTML、令牌化 CSS 和 Bridge 行为 JS；原生宿主只负责 92px 导航、64px 浏览器覆盖区和既有 WebView2 安全生命周期。下载任务继续由既有协调器和事件驱动，渲染位置从独立页面迁入媒体页。

**Tech Stack:** .NET 10、WinForms、WebView2、嵌入式 HTML/CSS/JavaScript、无框架控制台测试。

## Global Constraints

- 固定浅色，不增加主题切换入口或 Material 组件。
- 不改 App Bridge 公共方法或存储数据格式。
- WebView2 调用维持 UI 线程和 `ConfigureAwait(true)`。
- 测试只保留本地，提交信息使用中文，分支保持 `dev`，不推送。

---

### Task 1: 锁定资源与信息架构契约

**Files:**
- Modify: `tests/Transfor.Tests/Program.cs`
- Modify: `src/Transfor/Features/ModernUi/WebUiResources.cs`
- Modify: `src/Transfor/Transfor.csproj`

- [ ] 在 `TestWebUiResources` 增加三资源嵌入、无 `page-downloads`/downloads 导航、媒体队列、历史无刷新和设置结构断言。
- [ ] 运行 `dotnet run --project tests/Transfor.Tests`，确认新增断言因资源/结构尚未实现而失败。
- [ ] 给 `WebUiResources` 添加 CSS、JS 加载与包含检测；给 csproj 添加两个嵌入资源。
- [ ] 再运行测试，确认资源断言通过。

### Task 2: 拆分并实现 Focused Flow Web UI

**Files:**
- Modify: `src/Transfor/Features/ModernUi/webui/index.html`
- Create: `src/Transfor/Features/ModernUi/webui/styles.css`
- Create: `src/Transfor/Features/ModernUi/webui/app.js`

- [ ] 从现有页面移出 CSS 与 JS，保留所有相同的 Bridge 调用名、元素 ID 和下载事件处理。
- [ ] 以 `--ff-*` token 建立固定浅色布局、无障碍焦点和减弱动效。
- [ ] 删除 downloads 页面/导航及其历史卡；使 `refreshDownloads` 在媒体页的可折叠队列工作。
- [ ] 改造工作台、历史、设置和浏览器页面结构以满足已确认的信息架构。
- [ ] 运行全量离线测试，修复结构断言与行为回归。

### Task 3: 重绘原生宿主并保留生命周期

**Files:**
- Modify: `src/Transfor/Features/ModernUi/AppShellForm.cs`

- [ ] 将侧栏改为 92px、五项中文导航和 Focused Flow 固定浅色状态；移除主题按钮但保留 `SetTheme` 委托。
- [ ] 浏览器覆盖区改为 64px 工具栏保留高度，不改变 Profile、外部导航拦截、UI 线程或事件异常保护。
- [ ] 订阅下载状态以更新媒体徽标，并在释放时解除订阅（若协调器已暴露对应事件）。
- [ ] 运行测试与构建，修复编译/API 回归。

### Task 4: 文档、验证与本地提交

**Files:**
- Modify: `.gitignore`
- Modify: `docs/superpowers/specs/2026-08-11-transfor-focused-flow-ui-design.md`

- [ ] 添加 `/.superpowers/` 忽略项，不修改既有未跟踪目录内容。
- [ ] 对照规格逐项复检 DOM 文字、无水平溢出规则、Bridge 保留点和导航数。
- [ ] 运行 `dotnet run --project tests/Transfor.Tests`，预期 `All N tests passed.`。
- [ ] 运行 `dotnet build Transfor.slnx`，预期 0 警告 0 错误。
- [ ] 仅暂存允许提交的源码与文档，创建中文本地提交，不推送。

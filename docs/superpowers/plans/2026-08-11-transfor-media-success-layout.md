# Transfor 媒体成功态布局实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 ModernUi 媒体页成功态收敛到用户目标图：176px 正式侧栏、连续流程轨、四列媒体卡片、同屏下载队列，并保持现有 Bridge、下载数据格式、历史、设置和浏览器安全逻辑不变。

**Architecture:** 保留 C# AppShellForm 作为侧栏与 WebView2 宿主，只把导航按钮改为带轻量矢量图标的自绘控件并隐藏版本标签。Web UI 继续使用嵌入的 `index.html`、`styles.css`、`app.js`，媒体结果只使用现有 `post.assets`，选择数量由 DOM 复选框实时计算，单卡/批量下载都调用原有 `downloadSelected` Bridge。

**Tech Stack:** .NET 10 WinForms、WebView2、原生 HTML/CSS/JavaScript、无框架控制台测试运行器。

## Global Constraints

- 侧栏保留工作台、媒体、浏览器、历史、设置，不新增“下载管理”导航。
- 侧栏宽度固定 176px；不改变 WebView2 Profile、Bridge、下载服务、历史、设置和浏览器安全逻辑。
- CSS 令牌使用 `--ff-text: #0B1220`、`--ff-muted: #475569`、`--ff-border: #E2E8F0`、`--ff-primary: #0F766E`、`--ff-soft: #F0F7FA`、`--ff-control: 36px`、`--ff-radius: 12px`、`--ff-space: 32px` 的视觉值。
- 媒体卡片默认四列；仅 `max-width: 960px` 改为两列，`max-width: 720px` 改为单列。
- 测试必须用 `dotnet run --project tests/Transfor.Tests -c Release`，发布前测试项目仍不提交 GitHub。

---

### Task 1: 加入媒体页面契约的失败断言

**Files:**
- Modify: `D:\Code\Transfor\tests\Transfor.Tests\Program.cs:3071-3099`

**Interfaces:**
- Consumes: `WebUiResources.LoadIndexHtml()`, `LoadStylesCss()`, `LoadAppScript()`。
- Produces: 静态资源契约断言，覆盖成功提示、卡片下载按钮、选择数量、流程轨、四列 CSS、URL 提取、历史结构和下载事件/命令。

- [ ] **Step 1: Write the failing test**

在 `TestWebUiResources()` 中保留已有下载队列/历史断言，新增以下断言：

```csharp
AssertEqual(true, html.Contains("id=\"media-selection-count\"", StringComparison.Ordinal), "media selection count element");
AssertEqual(true, html.Contains("media-flow", StringComparison.Ordinal), "media flow wrapper");
AssertEqual(true, styles.Contains("grid-template-columns: repeat(4, minmax(0, 1fr));", StringComparison.Ordinal), "media grid has four columns");
AssertEqual(false, styles.Contains("@media (max-width: 1100px)", StringComparison.Ordinal), "media grid does not collapse at 1100px");
AssertEqual(true, styles.Contains("@media (max-width: 960px)", StringComparison.Ordinal), "media grid collapses at 960px");
AssertEqual(true, script.Contains("function extractShareUrl", StringComparison.Ordinal), "share URL extraction function");
AssertEqual(true, script.Contains("media-card-download", StringComparison.Ordinal), "single card download action");
AssertEqual(true, script.Contains("Bridge.invoke(\"downloadSelected\"", StringComparison.Ordinal), "downloadSelected bridge retained");
foreach (var token in new[] { "downloadProgress", "taskCompleted", "batchCompleted", "cancelTask", "retryTask", "openFile", "openFolder" })
    AssertEqual(true, script.Contains(token, StringComparison.Ordinal), $"download event or command retained: {token}");
AssertEqual(true, html.Contains("id=\"history-text-panel\"", StringComparison.Ordinal) && html.Contains("id=\"history-media-panel\"", StringComparison.Ordinal), "history panels preserved");
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project tests/Transfor.Tests -c Release`

Expected: FAIL on the new `media-selection-count`, four-column/1100px, URL extraction, or single-card download assertion before implementation.

- [ ] **Step 3: Commit**

Do not commit implementation yet; keep the red test output as the TDD checkpoint.

### Task 2: Implement the target media HTML structure

**Files:**
- Modify: `D:\Code\Transfor\src\Transfor\Features\ModernUi\webui\index.html`

**Interfaces:**
- Consumes: Existing media element IDs and queue IDs referenced by `app.js`.
- Produces: `media-flow`, `success-banner`, `media-selection-count`, `media-card-grid`, and queue container without a new navigation route.

- [ ] **Step 1: Replace the media page skeleton**

Use the exact visible copy `媒体` and `解析链接并下载媒体内容`; remove the `下载流程` eyebrow. Wrap steps 1–3 in `.media-flow`, add the step connector class to the final step, and keep `media-download-queue` in the same section. The success ribbon must contain `全选`, `已选择 N / N` via `id="media-selection-count"`, and `下载已选`.

- [ ] **Step 2: Preserve history and queue contracts**

Keep `history-text-panel`, `history-media-panel`, `downloads-list`, `downloads-empty`, `downloads-summary`, `queue-toggle`, `downloads-cancel-all`, and all existing settings/browser IDs unchanged.

### Task 3: Implement four-column media cards and selection behavior

**Files:**
- Modify: `D:\Code\Transfor\src\Transfor\Features\ModernUi\webui\app.js`

**Interfaces:**
- Consumes: Existing `post.assets`, `asset.index`, `asset.status`, `getPreview`, `downloadSelected`, and `defaultSelectAllSetting`.
- Produces: `extractShareUrl(text)`, `updateMediaSelectionCount()`, card-local `media-card-download`, and unchanged Bridge payload `{ shareLink, assets }`.

- [ ] **Step 1: Add URL extraction and normalize input**

Add:

```javascript
function extractShareUrl(text) {
  const match = String(text || "").match(/https?:\/\/[^\s]+/i);
  return match ? match[0].replace(/[，。、“”]+$/g, "") : "";
}
```

Use it in paste handling and before resolve; write the extracted value back to `media-link` and reject empty extraction with the existing clear error path.

- [ ] **Step 2: Render semantic cards**

Create `article.media-card` containing `.media-thumbnail`, absolute top-left checkbox, `.media-badge`, `.media-card-info`, title, metadata, and a `.btn.media-card-download` button. The button calls `Bridge.invoke("downloadSelected", { shareLink: currentShareLink, assets: [asset.index] })` and does not touch other checkboxes.

- [ ] **Step 3: Synchronize selection count**

Implement `updateMediaSelectionCount()` from enabled card checkboxes, call it after render/default selection, on each checkbox `change`, on select-all `change`, and after single-card download. Query `.media-card`, never `.card`, for batch indexes.

- [ ] **Step 4: Preserve queue event wiring**

Keep and restyle existing `downloadProgress`, `taskCompleted`, `batchCompleted`, `cancelTask`, `retryTask`, `openFile`, and `openFolder` paths; task state remains the same Bridge snapshot/event data.

### Task 4: Implement target CSS and flow/queue visual layout

**Files:**
- Modify: `D:\Code\Transfor\src\Transfor\Features\ModernUi\webui\styles.css`

**Interfaces:**
- Consumes: HTML classes from Task 2 and JS-generated media/queue classes from Task 3.
- Produces: 176px-compatible content, exact four-column desktop media grid, connected flow rail, compact queue rows.

- [ ] **Step 1: Set exact Focused Flow tokens**

Keep the existing token aliases for compatibility, but set the requested visual values and use `var(--ff-soft)` for control surfaces.

- [ ] **Step 2: Set media grid breakpoints**

Include:

```css
.media-card-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 12px;
}

@media (max-width: 960px) {
  .media-card-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}
```

Use a separate `@media (max-width: 720px)` single-column rule; remove the 1100px two-column rule.

- [ ] **Step 3: Connect flow steps**

Use `.media-flow::before` with the requested teal-to-sky gradient and stop its bottom extension for the last step using the final-step class.

- [ ] **Step 4: Render queue as horizontal task rows**

Style thumbnail, filename, status badge, progress, percent/speed/error, and action buttons in one row; show retry for failed/cancelled tasks and preserve open file/folder only for succeeded tasks.

### Task 5: Update AppShellForm navigation visuals

**Files:**
- Modify: `D:\Code\Transfor\src\Transfor\Features\ModernUi\AppShellForm.cs`

**Interfaces:**
- Consumes: Existing page keys and `NavigateTo`/`SetActiveNavButton` callbacks.
- Produces: 176px fixed sidebar with formal vector icons, same five page keys, hidden version label.

- [ ] **Step 1: Keep the five navigation items and 176px column**

Do not add a downloads page. Store icon kind separately from label and keep `SidebarWidth = 176`.

- [ ] **Step 2: Replace symbol text with a painted icon control**

Use a local `Button` subclass or paint callback to draw simple line icons for home, media, browser, history, and settings; keep text labels as `工作台`, `媒体`, `浏览器`, `历史`, `设置` and preserve click tags.

- [ ] **Step 3: Hide the version footer**

Remove the visible version label/footer row while leaving version sourcing untouched elsewhere.

### Task 6: Run verification and publish

**Files:**
- Verify: `D:\Code\Transfor\tests\Transfor.Tests\Program.cs`
- Verify: `D:\Code\Transfor\Transfor.slnx`
- Output: `D:\Code\Transfor\publish\Transfor\Transfor.exe`

- [ ] **Step 1: Run full offline tests**

Run: `dotnet run --project tests/Transfor.Tests -c Release`

Expected: `All N tests passed.`

- [ ] **Step 2: Build release**

Run: `dotnet build Transfor.slnx -c Release`

Expected: 0 warnings and 0 errors.

- [ ] **Step 3: Publish self-contained win-x64**

Run: `dotnet publish src/Transfor/Transfor.csproj -c Release -r win-x64 --self-contained true -o publish/Transfor`

Expected: `publish/Transfor/Transfor.exe` exists and has a current timestamp.

- [ ] **Step 4: Inspect diff and commit locally**

Run `git status --short`, inspect the final diff, ensure branch remains `dev`, then commit with a Chinese message such as `修复：对齐媒体页成功态与下载队列视觉` and do not push.

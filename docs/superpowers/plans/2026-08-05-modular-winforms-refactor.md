# Modular WinForms Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `subagent-driven-development` (recommended) or `executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve the current text converter while organizing it as one feature-partitioned WinForms project with recoverable, versioned text-state persistence.

**Architecture:** Keep one `.NET 10` WinForms project. Move application composition into `App`, navigation into `Shell`, text/history/settings into `Features`, JSON storage into `Infrastructure/Persistence`, and Win32 details into `Platform/Windows`. Persistence migration is a final, recoverable transaction from the legacy single state file to three versioned documents.

**Tech Stack:** .NET 10, WinForms, `System.Text.Json`, dependency-free console test project, Win32 P/Invoke.

## Global Constraints

- Keep a single `src/Transfor` WinForms project and a dependency-free `tests/Transfor.Tests` console project.
- Add no NuGet packages, media-download code, network code, WebView2, or `Features/MediaDownload` placeholders.
- Preserve conversion, tray, global-hotkey, history-panel paste, and text-history behavior.
- Use `TextToolDefinition` as the sole text-tool dispatch abstraction; do not add `ITextTransformer`.
- New JSON documents have `schemaVersion: 1`; normal writes are same-directory temporary-file replacements.
- If legacy `state.json` remains readable during an interrupted migration, it is authoritative; migration is the final refactor commit.
- All builds and test runs use an isolated `BaseOutputPath` while retaining the existing intermediate-output directory, because the user may be running the application from the default `bin` directory.

---

### Task 1: Restore the console test project

**Files:**
- Modify: `.gitignore`
- Add: `tests/Transfor.Tests/Transfor.Tests.csproj`
- Add: `tests/Transfor.Tests/Program.cs`

**Interfaces:**
- Consumes: public `QuoteConverter.Convert(string?)` and `SpaceRemover.Remove(string?)`, and internal types exposed to the test assembly by `AssemblyInfo.cs`.
- Produces: a tracked, runnable console test project referenced by `Transfor.slnx`.

- [ ] **Step 1: Make the tests trackable**

Remove only the `/tests/` rule from `.gitignore`; retain the `bin/`, `obj/`, `*.user`, and `/.idea/` rules.

```gitignore
bin/
obj/
*.user
/.idea/
```

- [ ] **Step 2: Confirm the existing test project is now visible to Git**

Run: `git status --short tests/Transfor.Tests .gitignore`

Expected: `Program.cs` and `Transfor.Tests.csproj` appear as untracked and `.gitignore` is modified; generated `bin/` and `obj/` files do not appear.

- [ ] **Step 3: Run the restored test suite before any production change**

Run:

```powershell
dotnet run --project tests/Transfor.Tests/Transfor.Tests.csproj --no-restore -p:BaseOutputPath=D:\tmp\Transfor-build\task1\
```

Expected: exit code `0` and `All ... tests passed.`

- [ ] **Step 4: Build the complete solution through the isolated output path**

Run:

```powershell
dotnet build Transfor.slnx --no-restore -p:BaseOutputPath=D:\tmp\Transfor-build\task1\
```

Expected: exit code `0`, with no dependency restore or locked-default-output error.

- [ ] **Step 5: Commit**

```powershell
git add .gitignore tests/Transfor.Tests
git commit -m "chore: restore console test project"
```

### Task 2: Partition existing models, transformations, history, and Windows code

**Files:**
- Create: `src/Transfor/Features/TextTools/Models/TextToolId.cs`
- Create: `src/Transfor/Features/TextTools/Models/TextToolDefinition.cs`
- Create: `src/Transfor/Features/TextTools/Services/QuoteConverter.cs`
- Create: `src/Transfor/Features/TextTools/Services/SpaceRemover.cs`
- Create: `src/Transfor/Features/History/Models/HistoryEntry.cs`
- Create: `src/Transfor/Features/History/Services/ITextHistoryRepository.cs`
- Create: `src/Transfor/Features/Settings/Models/AppSettings.cs`
- Create: `src/Transfor/Features/Settings/Models/HotKeyBinding.cs`
- Move: `HistoryStore.cs` to `Infrastructure/Persistence/LegacyHistoryStore.cs`
- Move: `PasteCoordinator.cs` to `Features/History/Services/PasteCoordinator.cs`
- Move: `GlobalHotKeyManager.cs` to `Platform/Windows/HotKeys/GlobalHotKeyManager.cs`
- Move: `WindowsNative.cs` to `Platform/Windows/Native/WindowsNative.cs`
- Delete: `src/Transfor/CoreModels.cs`
- Test: `tests/Transfor.Tests/Program.cs`

**Interfaces:**
- Consumes: current `ToolId`, converter behavior, legacy `HistoryStore`, and clipboard/input abstractions.
- Produces:

```csharp
internal enum TextToolId { QuoteConversion, SpaceRemoval }
internal sealed record TextToolDefinition(TextToolId Id, string DisplayName, Func<string?, string> Convert);
internal interface ITextHistoryRepository
{
    IReadOnlyList<HistoryEntry> GetHistory(TextToolId tool);
    void Add(HistoryEntry entry);
    void ClearHistory(TextToolId tool);
}
```

- [ ] **Step 1: Write failing tests for the new text-tool contracts**

Add a `TestTextToolDefinition` method that creates:

```csharp
var definition = new TextToolDefinition(TextToolId.QuoteConversion, "引号转换", QuoteConverter.Convert);
AssertEqual(TextToolId.QuoteConversion, definition.Id, "text tool id");
AssertEqual("'x'", definition.Convert("\"x\""), "text tool converter");
```

Update existing history tests to compile against `TextToolId` instead of `ToolId`.

- [ ] **Step 2: Run the test suite and verify the intended red failure**

Run the Task 1 isolated `dotnet run` command.

Expected: compilation failure because `TextToolId` and `TextToolDefinition` do not yet exist.

- [ ] **Step 3: Introduce the focused files and move code without changing behavior**

Create `TextToolId.cs`:

```csharp
namespace Transfor;

internal enum TextToolId
{
    QuoteConversion,
    SpaceRemoval,
}
```

Create `TextToolDefinition.cs`:

```csharp
namespace Transfor;

internal sealed record TextToolDefinition(
    TextToolId Id,
    string DisplayName,
    Func<string?, string> Convert);
```

Move the existing converter, history-entry, hotkey, paste, Win32, and legacy-store implementations into the specified folders. Rename only types needed to use `TextToolId`; do not change JSON fields or state-file path in this task. Make `LegacyHistoryStore` implement `ITextHistoryRepository` and retain its settings members for later tasks.

- [ ] **Step 4: Run the full suite and solution build**

Run both Task 1 verification commands.

Expected: exit code `0`; existing conversion, hotkey, history, and paste tests still pass.

- [ ] **Step 5: Commit**

```powershell
git add src/Transfor tests/Transfor.Tests/Program.cs
git commit -m "refactor: partition text and history models"
```

### Task 3: Extract the text-tools page and make MainForm a shell

**Files:**
- Create: `src/Transfor/Shell/IFeaturePage.cs`
- Create: `src/Transfor/Features/TextTools/UI/TextToolsPage.cs`
- Modify: `src/Transfor/Shell/MainForm.cs`
- Test: `tests/Transfor.Tests/Program.cs`

**Interfaces:**
- Consumes: `TextToolDefinition`, `ITextHistoryRepository`, and current clipboard behavior.
- Produces:

```csharp
internal interface IFeaturePage
{
    string Id { get; }
    string DisplayName { get; }
    Control View { get; }
    void OnActivated();
}
```

- [ ] **Step 1: Write a failing UI-contract test**

Add an STA test helper and assert the text page exposes the required shell contract:

```csharp
RunSta(() =>
{
    using var page = new TextToolsPage(historyStore);
    AssertEqual("text-tools", page.Id, "text page id");
    AssertEqual("文本转换", page.DisplayName, "text page display name");
    AssertEqual(true, page.View is UserControl, "text page view");
});
```

- [ ] **Step 2: Run the test suite and verify the intended red failure**

Run the Task 1 isolated test command.

Expected: compilation failure because `IFeaturePage` and `TextToolsPage` are absent.

- [ ] **Step 3: Extract UI behavior without semantic changes**

Create `IFeaturePage` with the interface above. Move the current tool buttons, input/output controls, `UpdateOutput`, and copy-to-history operation from `MainForm` to `TextToolsPage`. Set `Id` to `"text-tools"`, `DisplayName` to `"文本转换"`, `View` to `this`, and make `OnActivated` focus the input box.

Refactor `MainForm` to contain navigation plus a `Panel` content host. It creates the text page, shows its `View` in the host, and preserves the current title, minimum size, close-to-tray behavior, and visible text-tool interaction.

- [ ] **Step 4: Run the full suite and build**

Run both Task 1 verification commands.

Expected: exit code `0`; UI construction and all non-UI behavior continue to pass.

- [ ] **Step 5: Commit**

```powershell
git add src/Transfor/Shell src/Transfor/Features/TextTools/UI tests/Transfor.Tests/Program.cs
git commit -m "refactor: extract text tools feature page"
```

### Task 4: Move Windows services and create the composition root

**Files:**
- Create: `src/Transfor/App/AppPaths.cs`
- Create: `src/Transfor/App/AppServices.cs`
- Create: `src/Transfor/App/AppBootstrapper.cs`
- Move: `Program.cs` to `App/Program.cs`
- Move: `TransforApplicationContext.cs` to `App/TransforApplicationContext.cs`
- Move: `HistoryPanelForm.cs` to `Features/History/UI/HistoryPanelForm.cs`
- Move: `SettingsForm.cs` to `Features/Settings/UI/SettingsForm.cs`
- Move: `WindowsClipboardService` to `Platform/Windows/Clipboard/WindowsClipboardService.cs`
- Move: `WindowsWindowInputService` to `Platform/Windows/Input/WindowsWindowInputService.cs`
- Modify: `App/TransforApplicationContext.cs`, `App/Program.cs`
- Test: `tests/Transfor.Tests/Program.cs`

**Interfaces:**
- Consumes: legacy state store and the moved platform services.
- Produces:

```csharp
internal sealed class AppServices : IDisposable
{
    public required LegacyHistoryStore State { get; init; }
    public required GlobalHotKeyManager HotKeys { get; init; }
    public required PasteCoordinator PasteCoordinator { get; init; }
    public void Dispose() => HotKeys.Dispose();
}

internal static class AppBootstrapper
{
    public static AppServices Create();
}
```

- [ ] **Step 1: Write a failing composition test**

Add a test that creates `AppPaths` for a temporary directory and asserts its legacy state path ends with `state.json` and its application directory is absolute. Keep the test free of real hotkey registration.

- [ ] **Step 2: Run the test suite and verify red**

Run the Task 1 isolated test command.

Expected: compilation failure because `AppPaths` is absent.

- [ ] **Step 3: Implement composition without changing lifecycle behavior**

Implement `AppPaths` as an immutable path provider. Implement `AppBootstrapper.Create()` to load `LegacyHistoryStore`, create `GlobalHotKeyManager`, then construct `PasteCoordinator` from `WindowsClipboardService` and `WindowsWindowInputService`. Pass the returned `AppServices` to `TransforApplicationContext`; that context must retain its existing tray menu, startup-hotkey fallback, show/hide, and disposal logic while using injected services.

- [ ] **Step 4: Run the full suite and build**

Run both Task 1 verification commands.

Expected: exit code `0`; no reference remains to a source file in the project root except `.csproj` and `Properties`.

- [ ] **Step 5: Commit**

```powershell
git add src/Transfor/App src/Transfor/Features/History/UI src/Transfor/Features/Settings/UI src/Transfor/Platform tests/Transfor.Tests/Program.cs
git commit -m "refactor: compose Windows services at startup"
```

### Task 5: Split, version, and recover persisted text state

**Files:**
- Create: `src/Transfor/Infrastructure/Persistence/JsonFileStore.cs`
- Create: `src/Transfor/Infrastructure/Persistence/JsonSettingsStore.cs`
- Create: `src/Transfor/Infrastructure/Persistence/JsonTextHistoryStore.cs`
- Create: `src/Transfor/Infrastructure/Persistence/JsonUiStateStore.cs`
- Create: `src/Transfor/Infrastructure/Persistence/StateMigrationService.cs`
- Create: `src/Transfor/Features/Settings/Models/TextUiState.cs`
- Modify: `src/Transfor/App/AppPaths.cs`
- Modify: `src/Transfor/App/AppServices.cs`
- Modify: `src/Transfor/App/AppBootstrapper.cs`
- Modify: feature UI consumers to use separate settings/history/UI-state dependencies
- Delete: `src/Transfor/Infrastructure/Persistence/LegacyHistoryStore.cs`
- Test: `tests/Transfor.Tests/Program.cs`

**Interfaces:**
- Consumes: legacy `state.json` DTO compatibility, `AppSettings`, `TextUiState`, and `HistoryEntry`.
- Produces:

```csharp
internal sealed record TextUiState(TextToolId LastViewedTool)
{
    public static TextUiState Default { get; } = new(TextToolId.QuoteConversion);
}

internal interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

internal interface ITextHistoryRepository
{
    IReadOnlyList<HistoryEntry> GetHistory(TextToolId tool);
    void Add(HistoryEntry entry);
    void ClearHistory(TextToolId tool);
    void Trim(AppSettings settings);
    void Save();
}
```

- [ ] **Step 1: Write failing migration tests first**

Add tests using a unique temporary directory for all four required recovery cases:

```csharp
TestSuccessfulLegacyMigration();
TestInterruptedMigrationRebuildsFromReadableLegacy();
TestInterruptedMigrationKeepsCompleteValidNewStateWhenLegacyIsUnreadable();
TestNoRecoverableStateLeavesArtifactsAndUsesDefaults();
```

The second test must pre-create `migration.v1.pending.json`, a partial new `settings.json`, and a valid legacy `state.json`, then assert all three post-recovery documents equal the legacy state. The fourth test must assert that the corrupt legacy state and pending manifest still exist after startup.

- [ ] **Step 2: Run the migration tests and verify red**

Run:

```powershell
dotnet run --project tests/Transfor.Tests/Transfor.Tests.csproj --no-restore -p:BaseOutputPath=D:\tmp\Transfor-build\task5\
```

Expected: compilation failure because the split stores and `StateMigrationService` are absent.

- [ ] **Step 3: Implement versioned per-document stores**

Each file serializes as a root object with `schemaVersion` equal to `1`. `JsonFileStore` writes `target.tmp.<guid>` in the target directory, flushes the stream, then replaces the target. On load, a valid final file wins; a valid temporary file is promoted only if final is absent; invalid temporary files are retained.

`AppPaths` supplies `settings.json`, `ui-state.json`, `text-history.json`, legacy `state.json`, `state.v1.backup.json`, and `migration.v1.pending.json` under `%LOCALAPPDATA%\\Transfor`.

- [ ] **Step 4: Implement the migration transaction and recovery branch**

`StateMigrationService.EnsureMigrated()` must:

```csharp
if (HasPendingManifest(paths))
{
    RecoverPendingMigration(paths);
    return;
}

if (AllNewFilesExist(paths) || !File.Exists(paths.LegacyStateFile))
{
    return;
}

var legacy = LegacyStateReader.Read(paths.LegacyStateFile);
WriteAllStagedDocuments(legacy, paths);
WritePendingManifest(paths);
PromoteAllStagedDocuments(paths);
ValidateAllNewDocuments(paths);
File.Move(paths.LegacyStateFile, paths.LegacyBackupFile, overwrite: true);
DeleteMigrationArtifacts(paths);
```

`RecoverPendingMigration` must read the legacy state first. If valid, it recreates every new document from the legacy data and only then moves the legacy file to backup. If legacy is invalid but all new documents validate, it keeps those documents and removes only the pending/staged artifacts. Otherwise it leaves every artifact in place and returns defaults to the caller.

- [ ] **Step 5: Replace legacy-store consumers**

Change settings UI to save through `ISettingsStore`, history panel and text page to use `ITextHistoryRepository`, and history-panel selection to use `TextUiState`. `AppBootstrapper` runs migration before loading stores. Preserve trimming after settings save and the existing clipboard/paste behavior.

- [ ] **Step 6: Run migration tests, full suite, and build**

Run the Task 5 test command and the Task 1 solution-build command (with `task5` output paths).

Expected: exit code `0`; all existing tests plus the four recovery tests pass.

- [ ] **Step 7: Verify source layout and absence of deferred code**

Run:

```powershell
rg --files src/Transfor | Sort-Object
rg -n "ITextTransformer|MediaDownload|Douyin|WebView2|HttpClient" src tests
```

Expected: `ITextTransformer` and every deferred-media term have zero matches; feature, infrastructure, platform, shell, and app paths match this plan.

- [ ] **Step 8: Commit**

```powershell
git add src/Transfor tests/Transfor.Tests/Program.cs
git commit -m "refactor: split and migrate persisted text state"
```

## Final Verification

- [ ] Run `git log --oneline -5` and verify the five delivery commits appear in the declared order.
- [ ] Run the full isolated console test command once more with `final` output paths.
- [ ] Run `dotnet build Transfor.slnx --no-restore` once more with `final` output paths.
- [ ] Run `git status --short` and `git diff --check`; expect an empty worktree and no whitespace errors.
- [ ] Manually launch the app only after the user closes any currently running instance, then verify text conversion, copying records history, tray hide/show, hotkey history-panel display, and automatic paste.
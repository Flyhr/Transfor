# Modular WinForms Refactor Design

## Scope

Refactor the existing text-conversion application without adding media download features or third-party dependencies. Restore the existing console test project to source control, preserve the current WinForms behavior, and make the codebase ready for a future `MediaDownload` feature area.

## Constraints

- Keep one `.NET 10` WinForms application project and one dependency-free console test project.
- Do not add NuGet dependencies, WebView2, HTTP clients, or any media resolver/downloader in this phase.
- Preserve the current text tools, global-hotkey history panel, tray lifecycle, and existing `state.json` data.
- Do not reinterpret media downloads as text tools or text history.

## Selected Approach

Keep the application single-project and reorganize it by feature and platform boundary. This avoids the unnecessary project-reference overhead of splitting a small application into `Core`, `Infrastructure`, and WinForms libraries, while preventing the root source directory and `MainForm` from becoming a mixed feature bucket.

The three considered approaches were:

1. Keep the current flat layout and only add folders for future media code. This is low-effort but leaves `CoreModels`, `HistoryStore`, and `MainForm` coupled.
2. Split immediately into multiple class libraries. This offers stronger compile-time boundaries but is premature at the current size and creates extra project references.
3. Keep one application project, partition it by feature plus infrastructure and Windows platform code. This is the selected approach because it reduces coupling now and keeps future extraction possible.

## Target Structure for This Phase

```text
src/Transfor/
├─ App/
│  ├─ AppBootstrapper.cs
│  ├─ AppPaths.cs
│  ├─ AppServices.cs
│  ├─ Program.cs
│  └─ TransforApplicationContext.cs
├─ Shell/
│  ├─ IFeaturePage.cs
│  └─ MainForm.cs
├─ Features/
│  ├─ History/
│  │  ├─ Models/HistoryEntry.cs
│  │  ├─ Services/ITextHistoryRepository.cs
│  │  ├─ Services/PasteCoordinator.cs
│  │  └─ UI/HistoryPanelForm.cs
│  ├─ Settings/
│  │  ├─ Models/AppSettings.cs
│  │  ├─ Models/HotKeyBinding.cs
│  │  └─ UI/SettingsForm.cs
│  └─ TextTools/
│     ├─ Models/TextToolDefinition.cs
│     ├─ Models/TextToolId.cs
│     ├─ Services/QuoteConverter.cs
│     ├─ Services/SpaceRemover.cs
│     └─ UI/TextToolsPage.cs
├─ Infrastructure/Persistence/
│  ├─ JsonSettingsStore.cs
│  ├─ JsonTextHistoryStore.cs
│  ├─ StateMigrationService.cs
│  └─ StateStoreJson.cs
└─ Platform/Windows/
   ├─ Clipboard/WindowsClipboardService.cs
   ├─ HotKeys/GlobalHotKeyManager.cs
   ├─ Input/WindowsWindowInputService.cs
   └─ Native/WindowsNative.cs
```

Empty `MediaDownload` folders and interfaces are out of scope; they will be introduced alongside the first actual media feature.

## Composition and UI

`AppBootstrapper` will assemble `AppPaths`, persistence stores, Windows services, and `AppServices`. `TransforApplicationContext` will retain responsibility for tray behavior, global-hotkey handling, window visibility, and orderly disposal; it will receive `AppServices` instead of constructing its collaborators.

`MainForm` will become an application shell: it owns feature navigation and a content panel. The current text-conversion controls and behavior will move unchanged into `TextToolsPage : UserControl`, which implements `IFeaturePage`. The shell will initially expose the single text-tools page, so the visible UI and interactions remain unchanged while a future media page has a stable insertion point.

## Models and Text History

Delete the mixed `CoreModels.cs` after splitting its concepts. Text functionality will use `TextToolId` and `TextToolDefinition`; text history uses `HistoryEntry`; settings uses `AppSettings` and `HotKeyBinding`. The latter remains a Windows Forms `Keys`-based model for this phase because global hotkeys and the existing settings UI are Windows-specific.

`TextToolDefinition` is the sole text-tool dispatch abstraction: it owns a `TextToolId`, display name, and `Func<string?, string>` converter. `ITextTransformer` is deliberately not introduced because there are only two pure, static transformations and no independent consumer needs an implementation interface. This keeps the page's tool selection explicit and avoids an extra indirection.

`ITextHistoryRepository` defines the operations currently consumed by the UI: reading by text tool, adding, clearing, and trimming. `PasteCoordinator` remains text-history specific and retains the existing clipboard/restore/SendInput semantics.

## Persistence and Migration

Replace `HistoryStore` with separate JSON-backed stores and a migration service. The first startup after the refactor will check whether the new files are absent and legacy `%LOCALAPPDATA%\\Transfor\\state.json` exists. It will deserialize the supported legacy state, then write:

- `settings.json` for the hotkey and history limits;
- `ui-state.json` for the last viewed text tool;
- `text-history.json` for text history.

Each new file has a root `schemaVersion` of `1`. Normal writes affect exactly one document and use a same-directory temporary file followed by replacement. At startup, a valid final document wins over a leftover temporary file; if the final file is missing and the temporary file is valid, the temporary file is promoted. Invalid temporary files are retained for diagnosis and never replace readable state.

Migration is a recoverable multi-file transaction, not three unrelated writes:

1. Deserialize and validate the legacy state before creating or replacing any new final document.
2. Serialize all three versioned documents to uniquely named same-directory staged files and flush each staged file to disk.
3. Write a `migration.v1.pending.json` manifest that identifies the staged documents, then replace the three final documents. The manifest remains until all three final documents can be read back and validated.
4. Only after that read-back succeeds, rename `state.json` to `state.v1.backup.json` and remove the pending manifest and staged files.

On the next startup, a pending manifest means the transaction was interrupted. If the legacy state is readable, it is authoritative: the application recreates all three new documents from it, even if only a subset of new documents was already replaced. This prevents a partial new state from being mixed with old state. If the legacy state is no longer readable but all three final documents validate, those new documents are retained and the pending manifest is cleared. Only when neither a complete readable legacy state nor a complete readable set of new documents exists will the application run with defaults; it must preserve the legacy file, pending manifest, and staged files for recovery instead of deleting them.

This rule gives a deterministic answer to every partial-write state, while retaining the existing corruption fallback for data that has no recoverable valid representation.

## Delivery Order

The refactor will be delivered as five independently buildable and testable commits. No commit adds media-download behavior.

1. `chore: restore console test project` — remove the `/tests/` ignore rule, add the existing test source, and verify the suite with an isolated build-output directory.
2. `refactor: partition text and history models` — split `CoreModels.cs`, move text transformation and text-history types into their feature folders, and retain the legacy state store so persisted data behavior is unchanged.
3. `refactor: extract text tools feature page` — introduce `IFeaturePage`, make `MainForm` a shell, and move the current controls and copy/history behavior into `TextToolsPage`.
4. `refactor: compose Windows services at startup` — move Win32 integrations under `Platform/Windows`, add `AppServices` and `AppBootstrapper`, and keep the application-context lifecycle behavior unchanged.
5. `refactor: split and migrate persisted text state` — add versioned settings, UI-state, and text-history stores, implement the recovery protocol above, migrate legacy `state.json`, and add migration failure/restart tests. This is intentionally last so no earlier commit leaves a user with an incompatible persisted state.

## Tests and Verification

The ignored but present `tests/Transfor.Tests` console project will be restored to source control by removing `/tests/` from `.gitignore`. Before refactoring, its tests will be run with a separate build-output path because the user has an already-running application that locks the default executable.

New tests will be added before each persistence and migration change. The refactor must keep transformer, hotkey validation, text history trimming, and paste-coordinate test coverage. The final migration commit must additionally test successful migration, interrupted migration with a readable legacy source, interrupted migration with a complete valid new state, and no-recoverable-state fallback. Final verification will run the complete console test suite and a solution build using an isolated output directory, then inspect the diff and source tree.

## Deferred Work

Media download contracts, UI, settings, download history, HTTP security, and Douyin parsing are explicitly deferred. Once this refactor is stable, the next design/implementation cycle can add `Features/MediaDownload` without changing the text-history paste model.

# Engineering Handoff — Orbit File Manager (For Claude)

## 0. Claude pass — 2026-11-03 (Section 2 bug is FIXED)

**CancellationTokenSource crash.** The dialog the user saw came from `MainViewModel.Dispose()` (window close)
calling `Cancel()` on the `_previewCts` that `RefreshPreviewAsync(null)` had already disposed. The same leftover
also froze the preview pane after the first folder change: every later `RefreshPreviewAsync` faulted silently.
Fix: new `Utilities/CancellationHelper.cs` (`Replace` / `CancelAndClear` / `Release` / `SafeCancel`). Replacing
a source only cancels it; the operation that created it disposes it in its own `finally`. Applied to all 9 fields.
Superseded dialog runs no longer reset the UI of the newer run. `StructuredLogger` now writes `stackTrace`.

**UI fixes**
- Details view: Name column fills the free width (min 200, stops auto-fitting once the user drags it), tighter
  default widths for the other columns. At 1420 px nothing overflows anymore. Long names and dates show "…".
- Selection: the visible list now always shows the tab's selection after a view switch or reload (F5, view
  change, file watcher). Before, the status bar said "1 selected" while no row was highlighted, so Delete acted
  on an invisible item. Switching back to Details no longer leaves the rows scrolled to the far right.
- Breadcrumb keeps the current folder in view (it used to show the drive root and cut off the end).
- Toolbar: right group (view modes, theme, Settings) is laid out first so it is never clipped. Text labels hide
  automatically when the window is too narrow (dual-pane at the default width).
- Dark theme: implicit `ScrollViewer` + `GridView.GridViewScrollViewerStyleKey` styles (no white scrollbar corner),
  dark `ComboBox`/`ComboBoxItem` and `TabControl`/`TabItem` styles. Toolbar buttons expose their tooltip as the
  UI Automation name.
- Large Icons / Thumbnails tiles are top-aligned (icons line up). Thumbnails no longer upscale 32 px icons into
  blurry blobs. Large-icon size line no longer clipped.
- Folder Size Analyzer: `FolderAccentBrush` was never defined (folder icons invisible) and rows did not stretch
  (usage bars had zero width). Dialog lists now use `ModernFileListItemStyle`. Checksum dialog is tall enough
  for all four hashes. Batch Rename preview columns fit.
- `ThemeService` uses assembly-qualified pack URIs, so a theme swap also works when the app is hosted by a
  test harness.

Verified with `dotnet test` (47/47) and an in-process UI harness that hosts `App`, drives `MainViewModel` and
renders windows with `RenderTargetBitmap`. Mouse and keyboard injection does not work over the locked RDP session.

**Second pass (same day): address bar, orange redesign, logo**
- Address bar: clicking its empty area now enters path editing. `BreadcrumbScrollViewer` filled the bar, hit-tests
  its whole area and handled the click, so only the 2 px padding strip worked. The handler is now on
  `PreviewMouseLeftButtonDown` and the ScrollViewer is not focusable.
- Rectangle (marquee) selection: `Controls/MarqueeSelector.cs`, wired in `BrowserPaneControl` (`ArmMarquee` /
  `TrackMarquee` / `FinishMarquee`). Starts on empty space in every view, and in Details on any column except
  Name (the `NameCell` DockPanel still starts a file drag). Ctrl toggles, Shift adds, dragging past the edge
  auto-scrolls. Tile views compare item bounds; Details is virtualized and selects a row-index range.
- Dual-pane splitter: 8 px bar with a hairline and grip (`VerticalPaneSplitterStyle` /
  `HorizontalPaneSplitterStyle`) that turns orange on hover and drag; double-click splits evenly. The position is
  kept per orientation in `VerticalSplitRatio` / `HorizontalSplitRatio` and saved with the session.
- Compare Folders is a toggle (`IsComparingPanes`): the second press clears the highlights. While it is on,
  loading a folder in either pane re-compares; leaving dual pane ends it.
- Theme: "Graphite & Signal Orange". Accent `#FF7A1A` on warm graphite in dark mode, `#EA580C` on zinc greys
  in light mode. Accent buttons use dark ink (`AccentForegroundBrush` `#1A1108`). Danger is rose so it never
  reads as the accent.
- Signature: the selected row shows an orange rail on its leading edge (in `ModernFileListItemStyle`) instead
  of an outline box. The selected tab of the active pane glows orange (`ActiveTabGlowBrush` /
  `ActiveTabGlowBorderBrush`, fading into the toolbar colour). In dual-pane mode the other pane's selected tab
  stays plain, so the glow also marks the active pane; there is no full-width active-pane line any more.
- Toolbar: navigation, view modes and panel toggles sit in recessed segments (`ToolbarSegmentStyle`).
  Size/date columns use tabular numerals. Drive bars turn rose at 90%+ usage (`DriveEntryItem.IsNearlyFull`).
- Logo: a folder circled by a tilted orbit ring with a satellite. Shapes `Logo*` in `Resources/Icons.xaml`;
  toolbar mark in `MainWindow.xaml`; `Resources/OrbitFileManager.ico` (16–256 px, PNG entries) is the exe
  `ApplicationIcon` and the window icon. The .ico was rendered from the same geometries by the UI harness;
  regenerate it the same way if the shapes change.

**Third pass: Move to Recycle Bin speed**
- `FileOperationService.DeleteItemsAsync` called `Microsoft.VisualBasic` `DeleteFile`/`DeleteDirectory` once per
  item, about 31 ms of shell work each. Recycle Bin moves are now sent as one `SHFileOperation` per batch of 250
  (cancel and progress still work between batches). It now adds `FOF_WANTNUKEWARNING`, so an item that cannot go
  to the bin prompts instead of being destroyed silently. Junctions are still removed link-only, one by one.
- `MainViewModel.ExecuteDeleteAsync` no longer reloads every open tab and rebuilds the drive list and folder tree
  (which also collapsed the tree). It removes the deleted rows via `BrowserTabViewModel.RemoveEntries`, using
  `FileOperationResult.RemovedPaths`. Sidebar drive bars refresh on F5.
- Measured through the real Delete command on the dev PC: 64 items went from 2.0 s to 0.86 s; 304 items take 2.05 s
  (about 9–10 s with the old per-item calls).

## 1. Project Overview & Current State

**Orbit File Manager** is a native Windows 10/11 x64 desktop File Explorer alternative inspired functionally by XYplorer, built with **C#**, **WPF / XAML**, and **MVVM** (`CommunityToolkit.Mvvm` 8.3.2 + `Microsoft.Extensions.DependencyInjection` 8.0.1).

- **Target Framework**: Centralized in `Directory.Build.props` via `<OrbitTargetFramework>net8.0-windows</OrbitTargetFramework>` (matching the host machine's installed **.NET SDK 8.0.425**; switchable in 1 line to `net10.0-windows` if .NET 10 SDK is installed).
- **Automated Tests**: **47/47 xUnit tests passing** (`dotnet test OrbitFileManager.sln`). All tests run in isolated `%TEMP%` subdirectories and clean up in `Dispose()`.
- **Portable Storage**: `AppPathsService` stores `Data/`, `Logs/`, and `Cache/` beside `OrbitFileManager.exe` when writable, with automatic fallback to `%LOCALAPPDATA%\OrbitFileManager`.
- **Recent Fix Applied**: Fixed horizontal `ScrollBar` jumping to the right in `DetailsListView` during Dual-Pane folder navigation (`Controls/BrowserPaneControl.xaml.cs`: `DetailsListView_RequestBringIntoView` + `ResetScrollToLeftAndTop` wired to `BrowserTabViewModel.FolderNavigated`).

---

## 2. Priority #1 Bug to Fix Immediately

### Symptom (Reported by User with Screenshot + Log Entry)
Modal error dialog from `App.DispatcherUnhandledException`:
> **Orbit File Manager**
> `An unexpected error occurred:`
> `The CancellationTokenSource has been disposed.`

Logged in `Logs/orbit-20261103.jsonl`:
```json
{"timestamp":"2026-11-03T12:56:48.3979347+07:00","level":"ERROR","category":"App","message":"Unhandled UI exception.","exceptionType":"ObjectDisposedException","exceptionMessage":"The CancellationTokenSource has been disposed.","context":null}
```

### Exact Root Cause #1 (`MainViewModel.RefreshPreviewAsync`)
In `src/OrbitFileManager/ViewModels/MainViewModel.cs` (lines **1828–1859**):
```csharp
private async Task RefreshPreviewAsync(FileEntryItem? selected)
{
    _previewCts?.Cancel();
    _previewCts?.Dispose();
    // BUG: _previewCts is NOT set to null here!

    SelectedItemNote = selected is not null
        ? (_metadataService.GetMetadata(selected.FullPath)?.Note ?? string.Empty)
        : string.Empty;

    if (!ShowPreviewPanel || selected is null)
    {
        CurrentPreview = new FilePreviewData { Kind = PreviewKind.None };
        return; // <-- Returns early while _previewCts still points to the DISPOSED instance!
    }

    var cts = new CancellationTokenSource();
    _previewCts = cts;
    ...
```
**Reproduction sequence**:
1. User clicks any file -> `_previewCts` is allocated.
2. User changes folder or clicks empty space (`selected == null`) -> `RefreshPreviewAsync(null)` calls `_previewCts.Cancel()` and `_previewCts.Dispose()`, then **returns early without setting `_previewCts = null`**.
3. User clicks the next file -> `_ = RefreshPreviewAsync(selected)` calls `_previewCts?.Cancel()` on the **already-disposed** `CancellationTokenSource`, throwing `ObjectDisposedException` on an unawaited task (`NotifyActiveSelectionChanged`), which triggers `DispatcherUnhandledException`!

### Exact Root Cause #2 (Concurrent `cts.Token` Access Across All 9 Replaceable CTS Fields)
Across the ViewModels and Dialogs, the pattern:
```csharp
_xxxCts?.Cancel();
_xxxCts?.Dispose();
var cts = new CancellationTokenSource();
_xxxCts = cts;
```
disposes the previous `CancellationTokenSource` **immediately** while an in-flight `Task.Run(..., cts.Token)` may not yet have evaluated `cts.Token` (most notably `BrowserTabViewModel.ScheduleDebouncedRefresh` at lines **596–605**, where `_ = Task.Run(async () => await Task.Delay(350, cts.Token))` evaluates `cts.Token` on a ThreadPool thread *after* another `FileSystemWatcher` event may already have called `_watcherDebounceCts?.Dispose()`!). Accessing `.Token` or `.Cancel()` on a disposed `CancellationTokenSource` throws `ObjectDisposedException`.

### Recommended Fix Pattern (Apply Across All 9 Locations Below)
1. Capture `CancellationToken token = cts.Token;` **synchronously before** starting `Task.Run(...)` or `await` calls.
2. Use `Interlocked.Exchange(ref _xxxCts, newCts)` (or `Interlocked.Exchange(ref _xxxCts, null)` when clearing) and only call `SafeCancel(oldCts)` when replacing—**dispose `cts` inside the `finally` block of the async operation that owns it** (only after `await` completes, if `Interlocked.CompareExchange` confirms ownership, or via a small `CancelAndSwap` helper), and catch `ObjectDisposedException` alongside `OperationCanceledException`.
3. Audit all **9 `CancellationTokenSource` fields**:
   - `src/OrbitFileManager/ViewModels/MainViewModel.cs`:
     - `_previewCts` (lines 44, 1828–1859, 1911–1912)
     - `_thumbnailsCts` (lines 47, 875–887, 1917–1918)
     - `_searchCts` (lines 46, 812–815, 1471, 1915–1916)
     - `_operationCts` (lines 45, 1340–1343, 1429–1432, 1470, 1913–1914)
   - `src/OrbitFileManager/ViewModels/BrowserTabViewModel.cs`:
     - `_loadCts` (lines 23, 221–237, 286–289, 652–653)
     - `_watcherDebounceCts` (lines 25, 591–628)
   - `src/OrbitFileManager/Views/Dialogs/ChecksumDialog.xaml.cs`: `_cts` (lines 19, 66–69, 193–199)
   - `src/OrbitFileManager/Views/Dialogs/DuplicateFinderDialog.xaml.cs`: `_cts` (lines 20, 61–64, 215–221)
   - `src/OrbitFileManager/Views/Dialogs/FolderSizeDialog.xaml.cs`: `_cts` (lines 14, 70–73, 128–134)
4. Also enhance `src/OrbitFileManager/Services/Settings/StructuredLogger.cs` to serialize `ex.StackTrace` into the JSONL log entry so any future errors include exact line numbers.

---

## 3. Solution Map & Architecture

```text
OrbitFileManager/
├── Directory.Build.props                 # TargetFramework (net8.0-windows), Nullable, ImplicitUsings
├── OrbitFileManager.sln                  # Solution (App + xUnit Tests)
├── README.md                             # User docs, search grammar, shortcuts, manual test plan
├── HANDOFF.md                            # This engineering handoff document
├── src/OrbitFileManager/
│   ├── App.xaml / App.xaml.cs            # DI container registration & startup/shutdown lifecycle
│   ├── app.manifest                      # asInvoker, PerMonitorV2 DPI, longPathAware
│   ├── MainWindow.xaml(.cs)              # Top toolbar, Sidebar, Single/Dual-pane grid, Preview, Queue bar, Status bar
│   ├── Controls/
│   │   └── BrowserPaneControl.xaml(.cs)  # Reusable browser pane: Tab strip, Breadcrumb/Address, QuickFilter, 5 ListViews, D&D
│   ├── ViewModels/
│   │   ├── MainViewModel.cs              # Top-level coordinator, commands, Dual-Pane sync/swap/compare, Preview, Workspaces
│   │   ├── BrowserPaneViewModel.cs       # Per-pane tab collection, pin/lock/reorder/closed-tab stack
│   │   ├── BrowserTabViewModel.cs        # Per-tab directory loading, history, sorting, quick filter, FileSystemWatcher
│   │   └── FolderTreeNodeViewModel.cs    # Lazy-loaded sidebar folder tree nodes
│   ├── Services/
│   │   ├── FileSystem/
│   │   │   ├── FileBrowserService.cs     # FileSystemEnumerable fast directory scanner + drive listing
│   │   │   └── ShellIntegrationService.cs# Win32 SHGetFileInfo icons/type names + ShellExecuteEx Properties
│   │   ├── FileOperations/
│   │   │   ├── FileOperationService.cs   # Safe staged copy/move, Recycle Bin, symlink protection, Explorer clipboard
│   │   │   ├── FileOperationQueue.cs     # Background transfer worker channel, speed/ETA, Pause/Resume, Cancel
│   │   │   └── BatchRenameService.cs     # Two-phase atomic batch rename + collision check + Undo stack
│   │   ├── Search/
│   │   │   ├── SearchQueryParser.cs      # Boolean AST (AND/OR/NOT, wildcards, name/ext/regex/size/modified/created)
│   │   │   └── FileSearchService.cs      # Async streaming multi-root search + JSON presets
│   │   ├── Preview/
│   │   │   └── PreviewService.cs         # Non-locking Image, 64KB Text, PDF, IPropertyStore Audio/Video, Binary Hex
│   │   ├── Thumbnails/
│   │   │   └── ThumbnailService.cs       # IShellItemImageFactory shell thumbnails + bounded LRU cache
│   │   ├── Productivity/
│   │   │   ├── ChecksumService.cs        # Single-pass streaming MD5/SHA1/SHA256/SHA512 + hash verification
│   │   │   ├── DuplicateFinderService.cs # 3-pass duplicate scanner (Size -> 16KB edge hash -> Full SHA-256)
│   │   │   └── FolderSizeAnalyzerService.cs # Junction-safe recursive folder size & ratio analyzer
│   │   ├── Settings/
│   │   │   ├── AppPathsService.cs        # Portable Data/Logs/Cache path resolver
│   │   │   ├── SettingsService.cs        # settings.json persistence
│   │   │   ├── SessionService.cs         # sessions.json & workspaces.json
│   │   │   ├── FavoritesService.cs       # favorites.json + Quick Access defaults
│   │   │   ├── MetadataStoreService.cs   # item_metadata.json (color tags & item notes)
│   │   │   ├── ShortcutService.cs        # shortcuts.json customizable key bindings
│   │   │   └── StructuredLogger.cs       # Logs/orbit-YYYYMMDD.jsonl writer
│   │   ├── ThemeService.cs               # Dark/Light ResourceDictionary swap + DWM dark titlebar
│   │   └── WpfUserDialogService.cs       # Modal dialog service implementation
│   ├── Views/Dialogs/                    # Settings, BatchRename, AdvancedSearch, DuplicateFinder, FolderSize, Checksum, Conflict, Prompt, Confirm
│   ├── Themes/                           # DarkTheme.xaml, LightTheme.xaml, ControlStyles.xaml
│   ├── Resources/Icons.xaml              # Custom 24x24 vector geometry icons
│   ├── Converters/CommonConverters.cs    # Visibility, Enum, CutOpacity, HexToBrush, RatioToWidth converters
│   └── Utilities/                        # PathValidator.cs, FileSizeFormatter.cs
└── tests/OrbitFileManager.Tests/         # 47 xUnit unit & integration tests across Phases 1–6
```

---

## 4. Suggested Next Improvements & Polish Opportunities

After fixing the `CancellationTokenSource` lifecycle issue above, high-value UX/feature improvements include:

1. **Remember Parent Selection on `Up` / `Back` Navigation**:
   - When navigating *up* (`Alt+Up` / `Backspace`) from `C:\Users\<you>\.claude` to `C:\Users\<you>`, pass `selectPathAfterLoad: previousChildPath` so `.claude` is automatically highlighted (XYplorer behavior), while keeping horizontal scroll pinned at `0` (Left).
2. **Persist Custom Column Widths & Dual-Pane Splitter Ratio**:
   - Save user-resized `GridViewColumn` widths (`Name`, `Ext`, `Size`, `Type`, `Modified`, `Created`, `Attr`) in `AppSettings` and `LeftPane` / `RightPane` splitter star ratios in `AppSessionState`.
3. **Inline Rename (`F2`) in File List**:
   - Optional inline `TextBox` overlay inside the `Name` cell on `F2` (selecting filename stem without extension) as a faster alternative to `TextPromptDialog`.
4. **Native Windows Shell Context Menu Integration**:
   - Optional `"Windows Shell Menu..."` entry invoking `IContextMenu` (`SHBindToParent`) for third-party shell extensions (7-Zip, VS Code, Git).
5. **Tab Visual Indicator for Active Background Search**:
   - Small spinner or match counter badge on `IsSearchTab` pills while streaming search is running, plus a button to stop search directly on the tab bar.

---

## 5. Build, Test & Portable Publish Commands

```powershell
# Build solution
dotnet build OrbitFileManager.sln -c Release

# Run all 47 unit tests
dotnet test OrbitFileManager.sln

# Publish self-contained win-x64 portable folder
dotnet publish src/OrbitFileManager/OrbitFileManager.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false `
  -o publish/OrbitFileManager
```

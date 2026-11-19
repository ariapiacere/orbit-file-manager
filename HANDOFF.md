# Engineering Handoff — Orbit File Manager (For Claude)

## 2026-11-19 (later) — Rename / New Folder no longer jump the Details list sideways

User report: after Rename or New Folder the list often ends up scrolled to the far right. The 1.2.1 fix
(`ScrollIntoViewVertically`) was only ever tested from horizontal offset 0. Starting from a scrolled-right list
(as a user who scrolled to see the dates), v1.2.3 still jumped to 445/445 px or back to 0. The UI harness
`renamejump` mode measures this. It runs 9 cases (New Folder landing above/below, rename in place, rename to
top/bottom, focus returning to the list, End/Home) in single/dual pane, starting at 0, 60, 120 or 200 px. It
checks the offset at every rendered frame (`CompositionTarget.Rendering`), and that the selected row is
vertically in view.

Three causes, three fixes:
1. `BrowserTabViewModel.ApplyCollectionGrouping` cleared `GroupDescriptions` after every reload, even with grouping
   off. That rebuilds the whole list and produced the far-right jumps (445 px). It now does nothing unless
   grouping really changes.
2. A reload empties and refills the list. In one layout pass the extent collapses to ~10 px although all rows
   are there, so WPF clamps the offset to 0.
   - `BrowserPaneControl` remembers the offset when a reload starts (`IsLoading`) or a selection is pushed back.
     Until 1–3 s after it settles, any sideways move is put back (`KeepGuardedHorizontalOffset`).
   - The value is set straight on the row panel (`SetHorizontalOffset`), not through `ScrollToHorizontalOffset`,
     which is queued and let a frame through.
   - Mouse down, mouse wheel and Left/Right keys in the list end the guard. Opening another folder, switching
     tabs or switching view resets the remembered offset to 0, as before.
3. `Controls/DetailsRowsPanel` (the Details `ItemsPanel`, a `VirtualizingStackPanel`) re-implements
   `IScrollInfo.MakeVisible`: a bring-into-view request is narrowed to a 1 px strip at x = 1 in panel coordinates.
   The rows are arranged shifted by the offset, so that strip is always visible and only the vertical part
   remains. Do not "undo" the offset after `base.MakeVisible` instead: WPF retries the request and the UI thread
   hangs in a layout loop (seen in the harness). Viewport-relative x values land 2 px off.

Result: every rename / New Folder case ends at the starting offset in all setups; old code fails 7 cases from
200 px. Left over: End/Home still nudge 2 px (WPF keyboard navigation aligns the row edge through
`ScrollToHorizontalOffset`, not `MakeVisible`). In about 2 of 10 runs one rendered frame showed offset 0 before the
guard put it back. The harness now exits with code 99 after `HARNESS_TIMEOUT_S` (default 240 s), so a hung
layout cannot block a run.

## 2026-11-19 — "Fit columns to pane": Details without side scrolling

Dual-pane Details always scrolled sideways: the non-Name columns add up to 564 px and Name keeps at least 200 px,
so any pane narrower than about 780 px overflowed.
- **Switch:** a toolbar toggle next to the view modes (`FitColumnsToggle`, icon `IconFitColumns`) and the first
  item of the column-header right-click menu. It is `MainViewModel.FitDetailsColumns` /
  `ToggleFitDetailsColumnsCommand`, saved as `AppSettings.FitDetailsColumns` (default off).
- **Layout:** `Controls/DetailsColumnFitter.Fit(available, columns)` is pure and unit-tested. As the pane narrows:
  1. Preferred widths, and Name gets the rest.
  2. The other columns shrink evenly towards their minimums so Name keeps 200 px.
  3. Every other column at its minimum, and Name drops towards 120 px.
  4. Columns drop out in `DropOrder` (Attr, Created, Ext, Type, Size, Modified last).
- **Numbers:** `BrowserPaneControl.ColumnFit` holds preferred/min/drop order. Dates have a 108 px minimum
  ("2026-11-19 10:01" is 93 px plus 12 px padding), so a date is never cut off; Type and names end in "…".
- **In the pane:** fit mode sets the Details `ScrollViewer.HorizontalScrollBarVisibility` to Disabled.
  `FitAllColumns` runs on resize, viewport changes and column visibility changes. A dragged non-Name column keeps
  its width as its new preferred width (`_fitPreferredWidths`); Name always takes what is left. With the mode off,
  the old `FitNameColumn` behaviour is unchanged.
- **Tests:** `DetailsColumnFitterTests` (8 cases, including a 0–2000 px sweep). The UI harness `fitcolumns` mode
  checks five pane widths, 1284 to 319 px (single/dual, splitter 75/25 and 50/50, side panels on/off): no extent
  beyond the viewport, Name ≥ 120, dates whole, a dragged column, toggle on/off and the saved setting. Note that
  Farm01's screen is 1280 px wide and a window cannot grow beyond it, so vary pane widths with the splitter and
  panels, not `Win.Width`.

## 2026-11-17 (later) — Busy clipboard no longer crashes Ctrl+C; queue rows list their files

**Clipboard.** Ctrl+C showed "An unexpected error occurred: OpenClipboard Failed (0x800401D0 CLIPBRD_E_CANT_OPEN)".
- Cause: only one program can have the clipboard open. Clipboard history, RDP clipboard sync and clipboard managers
  open it right after each change. WPF's `SetDataObject(copy: true)` retries `OleSetClipboard` for 1 s and
  `OleFlushClipboard` for only about 0.1 s, then throws, and the exception reached `App.DispatcherUnhandledException`.
- `Utilities/ClipboardAccess`: every clipboard call goes through `TryRunAsync` / `TryGetAsync`:
  - Checks `IsFreeNow()` first and waits asynchronously, about 2.5 s in all, so WPF's own sleeping retry loop never
    freezes the window. Measured longest UI pause while waiting: 47 ms.
  - Gives up with a status-bar message (`BusyMessage`) instead of the dialog. Other errors still throw.
  - Callers: Copy, Cut, Paste (incl. the clear after a cut-paste), Copy Full Path / Filename / Relative Path, and
    the Checksum dialog.
  - `FileOperationService.SetClipboardFilesAsync` / `GetClipboardFilesAsync` replace the sync versions.
  - Commands are now `CopySelectedAsync`, `CutSelectedAsync`, `CopyFullPathAsync` and so on; the generated command
    names are unchanged.
  - `MainWindow` sets `e.Handled` before awaiting Ctrl+C/X/V and Ctrl+Shift+C.
- `IsFreeNow` must open the clipboard with a real window (a hidden `HWND_MESSAGE` `HwndSource`). Measured:
  opened with no window, `OpenClipboard(NULL)` can succeed while another process holds the clipboard the same way,
  and the following `CloseClipboard` ends that process's open.
- Tests: `ClipboardAndQueueTooltipTests` (retry, give-up after 2–3 s, no WPF call while held, other errors
  surface).
- The UI harness `clipboard` mode has a separate PowerShell process hold the clipboard:
  - 5 s hold: a message after 2.5 s, no dialog, UI responsive.
  - A plain WPF call fails with the user's exact error.
  - Ctrl+C with a 1.5 s hold and Ctrl+V with a 1 s hold both succeed once the clipboard frees.

**Queue row hover.** Hovering a job in the File Operation Queue shows `FileOperationJob.ItemsToolTip`:
- Copy/Move and the item count, the source folder ("several folders" when mixed) and the destination.
- Every item, with "→ new name" for a renamed copy (Keep both, same-folder copy), "(skipped)", "(failed)",
  "(waiting)" or "(not done)". At most 30 lines, then "…and N more".
- The queue records each outcome with `RecordItem(source, JobItemOutcome, target)` on the UI thread;
  `CreatedPaths` is now filled from it.

Known: `SingleFileUpdateTests.FolderScript_CopiesTheBuildAndKeepsUserData` failed twice (once in the full suite,
once in its class) and then passed 12 times in a row; the error message was not captured. It tests the 1.2.0
robocopy install script, which this change does not touch. If `release.ps1` stops on it, rerun.

## 2026-11-17 — New Folder no longer scrolls Details sideways; copied items stay selected

**Sideways jump.** After New Folder, a copy or any reload, the Details list could jump almost to its right end, so
the Name column disappeared.
- Cause: a reload replaces the item instances, so `BrowserPaneControl.SyncSelectionIntoActiveList` re-selects the row
  and calls `ScrollIntoView`. For a row that has no container yet (below the visible rows), WPF also scrolls
  horizontally to the row's far edge. Measured with a 1000 px window: 372 of 389 px after New Folder (the watcher
  reload right after it), 387 after a copy.
- Fix: `ScrollIntoViewVertically` keeps the Details horizontal offset. It restores it right away, at `Loaded` and at
  `ContextIdle`, because the shift can land in a later layout pass. A 2 px nudge is undone in the same pass.

**Copied items selected.** Ctrl+V, drag-drop and the copy/move-to-other-pane commands all go through
`FileOperationQueue`.
- `FileOperationJob.CreatedPaths` lists every top-level item a job wrote. With "Keep both" or a same-folder copy
  that is the renamed copy.
- `OnJobFinishedRefresh` now receives the job. `MainViewModel.RefreshAffectedTabsAsync(folder, paths)` makes every
  tab showing the destination select exactly those items; other tabs keep their selection. The direct
  `RunFileTransferAsync` path does the same with `FileOperationResult.CreatedOrUpdatedPaths`.
- Reloads keep the whole selection: `RefreshAsync(preserveSelection: true)` used to keep only `SelectedItem`, so a
  watcher reload cut a multi-selection down to one item. `NavigateAsync` / `ApplyFilterAndSort` now take a list of
  paths, and the first one that exists becomes `SelectedItem`. Sorting keeps the whole selection too.
- Tests: `SelectionAfterTransferTests` (created paths incl. Keep both, multi-selection kept across a reload, explicit
  paths with a missing and a differently-cased one). The in-process UI harness, New Folder through the real name
  dialog with 80 folders, copy, wait for the watcher reload, then F5, passes 11 checks; the old code fails 7.

## 2026-11-12 — 1.2.0: tabs survive crashes, single-file exe

**Crash-safe tabs.** Before, `sessions.json` was written only in `Window_Closing`, so a crash, a forced kill or a
Windows restart (WPF does not raise `Closing` when the session ends) lost every tab. Restore also dropped tabs whose
folder was missing and called `Directory.Exists` on the UI thread, which stalls for many seconds on an offline share.
- `SessionAutoSaver` (Services/Settings): `RequestSave()` debounces 750 ms; a 10 s timer catches unreported changes
  (selection, history). The state is built on the UI thread; serialising, comparing and writing run on the thread
  pool, and nothing is written when only `SavedAt` would change. Started at the end of `InitializeAsync`, so a
  half-restored session is never saved. `MainViewModel` triggers it from pane `Tabs` changes, `ActiveTab`, layout
  properties and per-tab properties (`SessionTabProperties`; tabs are re-synced on every collection change).
- `SessionService` writes `sessions.json.tmp` with `WriteThrough` + `Flush(true)`, then `File.Replace` (main → `.bak`),
  falling back to copy + move on volumes without ReplaceFile. Writes are numbered (`NextSequence()` on the UI thread)
  and serialized by a lock, so an older background write never lands after a newer one or after the final save.
  `LoadSessionWithRecovery()`: main → `.bak`; a damaged main (bad JSON, empty, NUL-filled, `null`) is renamed to
  `sessions.json.corrupt-<time>` and never rotated into the backup. `AppSessionState.Sanitize()` repairs null panes,
  blank tabs and bad indexes. The status bar says when tabs came from the backup or could not be restored.
- `App.OnSessionEnding` saves window + session (`MainWindow.SaveWindowAndSession`) on sign-out, restart or shutdown.
- `TabSessionState` gained `SelectedPath`, `BackHistory` and `ForwardHistory` (oldest first, 50 per direction).
  Older files load unchanged. `BrowserPaneViewModel.ToSessionState` now counts the active index among the saved tabs
  (search tabs are skipped; before, an active tab after a search tab restored the wrong tab).
- Restore (`BrowserPaneViewModel.RestoreSessionAsync(state, fallback, waitForLoads)`): every tab is created at once
  with `PrepareForRestore` (title, label, breadcrumbs, history; no disk I/O), then all folders load in parallel,
  active tab first. Startup passes `waitForLoads: false`. Until its first load finishes a tab reports its saved path
  and selection, so an autosave in the meantime loses nothing.
- Unavailable folders: `BrowserTabViewModel.IsUnavailable` (the tab's own folder failed to load). The tab keeps the
  path, shows a rose warning icon in its pill and a banner with **Retry** (= F5) and **Open nearest folder**
  (`FindNearestExistingFolderAsync`, off the UI thread; a locked tab opens it in a new tab). `NewTabAsync` no longer
  probes an unavailable tab's path, and `UpdateDriveSpaceSummary` reads `DriveInfo` off the UI thread.
- Tests: `SessionPersistenceTests` (20 cases: atomic save + backup, round trip, old format, five kinds of damaged
  file, missing main, both damaged, sanitising, write ordering, 40 parallel writers, autosave timing on a real
  Dispatcher, restore of missing/offline folders, nearest folder, history and active index).

**Single-file exe.** Releases now ship one self-contained `OrbitFileManager.exe` (155 MB; zip 63 MB, was 67 MB):
`-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded`, also set in a
`PublishSingleFile` property group in the csproj. Compression is off (measured: 69 MB exe but +120 ms start and
+100 MB RAM, zip unchanged). Warm start ~580 ms vs ~500 ms for the folder build.
- Checked for single-file breakage: no `Assembly.Location` use; `AppContext.BaseDirectory` is the exe folder, so
  `Data/Logs/Cache` stay beside the exe; WPF native DLLs are unpacked to `%TEMP%\.net\OrbitFileManager\<id>`.
- Old (1.1.x) updaters download the same zip name, extract it and robocopy `OrbitFileManager\OrbitFileManager.exe`
  over the old folder. Verified that the single exe runs there and loads nothing from the ~470 old files.
  `InstallLayoutService.CleanUpAsync` (started from `App.OnStartup`) then deletes them: single-file build only, only
  when `OrbitFileManager.dll` is beside the exe, only the names in `Services/Updates/LegacyFolderFiles.txt` (union of
  the 1.1.0–1.1.8 zips, embedded), never the exe, `Data`, `Logs`, `Cache` or anything else; emptied language folders
  go too. It also removes extraction folders of older versions unless a running Orbit still has them loaded (each is
  renamed first, which Windows refuses while a DLL inside is loaded).
- New updater (`UpdateService.BuildInstallScript`): a package holding only the exe is installed by copying it to
  `<exe>.new` and renaming it over `Environment.ProcessPath` (so a renamed exe works), retried 15 times, else
  "failed copy". Waiting uses the PID, not the image name. Folder packages still use robocopy.
- `tools/release.ps1` publishes single-file, refuses to continue unless the publish folder holds exactly the exe with
  the right version, and uploads the zip, `.sha256` and the bare `OrbitFileManager.exe`.
- Tests: `SingleFileUpdateTests` (10 cases; the install scripts really run under cmd, with a Thai/`%`/`&` folder
  name, a renamed exe, a lock that clears and one that does not, PID waiting, and the robocopy path).

**Verified on the released 1.2.0 (134/134 tests).** Farm01 has no machine-wide .NET and none of these runs set
`DOTNET_ROOT`, so the exe needs no installed runtime.
- Crash proof with the real exe (UI Automation; the RDP session is locked): seeded six tabs (pinned, locked, labels,
  views, sort, selection, history, a deleted folder, an offline `\\192.0.2.1\...` share). The window was up in 0.7 s
  (1.7 s on the very first start, which unpacks the native DLLs). Pressed + via UIA; the new tab was on disk about
  0.8 s later. After `taskkill /F` and a restart the re-saved state was identical. A `sessions.json` cut in half
  came back from `.bak`, with the status-bar message and `sessions.json.corrupt-*`.
- In-process harness renders: warning icons on unavailable tabs, banner with Retry / Open nearest folder. The
  offline share took 17.5 s to fail and the UI stayed responsive.
- Update 1.1.8 to 1.2.0 through 1.1.8's own updater (the v1.1.8 zip, SHA-256 checked against GitHub): 13 s from
  Install to restart. Afterwards only `OrbitFileManager.exe`, `Data`, `Logs` and `Cache` were left (466 files removed,
  0 failed), "Update installed ok 1.2.0" was logged, and the tabs were kept. UIA note: the status-bar badge's
  automation name is its tooltip, "Download and install the new version" (toolbar style); the dialog button is
  `InstallButton`.
- Single exe to single exe: this code built as 1.1.99 and renamed `Orbit File Manager (old).exe` updated itself to
  the released 1.2.0 under the same name ("1 file(s) copied / 1 file(s) moved", no `.new` left, tabs kept).
- Not tested: a real Windows restart or shutdown (`OnSessionEnding`), a mapped drive letter whose NAS is offline (the
  UNC case is covered), and a PC with AppLocker rules that block `%TEMP%` (the single-file host unpacks the WPF
  DLLs there; `DOTNET_BUNDLE_EXTRACT_BASE_DIR` can move that). Left alone: the sidebar's drive list still asks
  each drive `IsReady` on the UI thread at startup, so a dead mapped drive can still slow startup there.

## 2026-11-06 — Sorting, type-to-select, and mixed transfer replacement

- Natural name sorting puts `_` before digits and letters, retaining numeric ordering and directories first.
- All five browser lists use WPF `TextSearch` against `FileEntryItem.Name`: type a prefix to select and scroll,
  or repeat the first character to cycle matching items. Verified with routed text input, including `_` and Thai.
- Both transfer paths clear the destination's read-only attribute before a same-volume overwrite move, matching
  staged-copy behavior. Regression tests verify SHA-256 for a folder plus sibling MP4s in both selection orders,
  through the queue and direct service, including equal sizes/timestamps and read-only destinations.
- Thumbnail cache keys include size and modified time. Operation refreshes invalidate the cache even for
  replacements with identical metadata; a generation prevents in-flight loads from restoring an obsolete entry.
  Shell thumbnail and media property COM wrappers are released immediately so preview work does not retain them
  until GC. A real MP4 preview was checked for immediate exclusive file access.
- Follow-up: user confirmed the destination MP4 plays the old content after local-to-NAS copy with Apply to all.
  Reproduced this partial-success pattern by holding the destination MP4 open without delete sharing: the folder
  copies but the final staged rename fails. `FileTransferCommit` now retries replacement access/sharing failures
  for up to 3 seconds, supports cancellation/pause, and retains the old destination on failure. Both transfer paths
  use it. Queue rows show the failing file and error details, rather than only an error count.
- Validation: Release build and 81 xUnit cases pass, including transient locks, persistent locks, and cancellation.
  A separate WPF harness verifies text input in all five views. A real-MP4 harness verifies local-to-local and
  local-to-loopback-SMB/UNC copy, through both transfer paths, with preview, a temporary reader, Apply to all, and
  SHA-256 checks. The user's specific NAS destination was not available for verification.

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
- The + (new tab) button sits right after the last tab, browser style (`Controls/TabStripPanel.cs`). When the
  tabs overflow, the tab list scrolls (pixel scrolling, so the last tab ends flush) and + stays at the right edge.
- Tools > Check for Updates always answers with a window: the update offer, "You have the latest version", or an
  error. The check itself gives up after `UpdateService.CheckTimeout` (30 s); the HTTP clients keep their
  10-minute timeout for the package download.
- Auto-refresh (folder watcher in `BrowserTabViewModel`, "Folder watching" section):
  - A change that arrives while the list is loading queues one more reload (`_refreshAfterLoad`) instead of being
    dropped. Reloading the same folder keeps its watcher (`EnsureWatcher`), so there is no unwatched gap.
  - During a long copy the reload debounce (350 ms) is never pushed back more than `RefreshMaxDelayMs` (1.5 s),
    so files show up as they land.
  - `Changed` events update size and modified time in place, about once a second, without a reload.
    `FileEntryItem.SizeBytes` / `FormattedSize` / `ModifiedTime` are now settable and raise change notices.
  - A watcher `Error` (buffer overflow, dropped share) replaces the watcher off the UI thread, retrying every 5 s,
    then reloads. The buffer is 64 KB, the most a network share accepts.
  - `DirectoryLoadResult.FolderStampUtc` is the folder's last-write time read before listing. Right after a new
    watcher starts it is compared once. On network paths (UNC or mapped) it is compared every 5 s, because a
    share can stop sending change notices without an error, or never send them for changes made on the server.
  - The UI harness `autorefresh` mode covers all of this, including a loopback `\\localhost\C$` share. 18 checks;
    the old code fails 7.
  - Known slow spot, not fixed: a load adds rows to `DisplayedItems` one at a time on the UI thread. On the dev
    PC a local folder of 4,000 .txt files reloads in about 4 s and one of 15,000 in about 22 s, and the window is
    unresponsive meanwhile.
- Theme: "Graphite & Signal Orange". Accent `#FF7A1A` on warm graphite in dark mode, `#EA580C` on zinc greys
  in light mode. Accent buttons use dark ink (`AccentForegroundBrush` `#1A1108`). Danger is rose so it never
  reads as the accent.
- Accent presets (Settings > Appearance > Accent, saved as `AppSettings.Accent`):
  - Six presets: Signal Orange (default), Orbit Blue, Lagoon Teal, Nebula Violet, Aurora Green, Graphite Mono.
    They are defined in `Services/AccentPalettes.cs`.
  - Each preset has one base colour per mode. Hover, selection, tab glow and button ink are derived from it, and
    the ink is dark or white, whichever has more contrast.
  - In dark mode each preset also brings its own greys (cool slate for blue, plum for violet, and so on).
    Light mode keeps the zinc greys for every preset.
  - Orange adds nothing: its colours are DarkTheme.xaml / LightTheme.xaml as hand-tuned. Any other preset is an
    extra ResourceDictionary that `ThemeService.ApplyTheme(mode, accent)` inserts right after the base theme.
  - The dialog previews the mode and accent live; Cancel puts the saved ones back (`WpfUserDialogService`).
  - `AccentPaletteTests` check contrast for every preset: button text 4.5:1, accent on the list background 3:1.
  - The UI harness `accents` mode tests preview, Cancel and Save, and renders a contact sheet of all 12 looks.
  - The exe icon stays orange.
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

**Orbit File Manager** is a native Windows 10/11 x64 desktop File Explorer alternative built with **C#**, **WPF / XAML**, and **MVVM** (`CommunityToolkit.Mvvm` 8.3.2 + `Microsoft.Extensions.DependencyInjection` 8.0.1).

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
   - When navigating *up* (`Alt+Up` / `Backspace`) from `C:\Users\<you>\.claude` to `C:\Users\<you>`, pass `selectPathAfterLoad: previousChildPath` so `.claude` is automatically highlighted, while keeping horizontal scroll pinned at `0` (Left).
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

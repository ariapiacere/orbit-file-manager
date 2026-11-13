# Orbit File Manager

**Orbit File Manager** is a modern, native Windows 10/11 x64 desktop File Explorer alternative built with **C#**, **WPF / XAML**, and **MVVM** (`CommunityToolkit.Mvvm`), with an original Fluent-inspired Slate Dark & Daylight Light visual design.

---

## 1. Architecture & Portable Layout

- **UI & Presentation**: Native WPF (`PerMonitorV2` high-DPI aware, virtualized `ListView` / `GridView` / `WrapPanel` templates, runtime Dark/Light `ResourceDictionary` switching, Win32 DWM dark titlebar integration).
- **MVVM Layer**: `MainViewModel`, `BrowserPaneViewModel`, `BrowserTabViewModel`, and `FolderTreeNodeViewModel` powered by `CommunityToolkit.Mvvm`.
- **Services Layer**:
  - `FileBrowserService` & `ShellIntegrationService`: Low-allocation `FileSystemEnumerable`, `FileSystemWatcher` external change tracking, `SHGetFileInfo` icons/types, `ShellExecuteEx` native Properties dialog, and Explorer `CF_HDROP` + `Preferred DropEffect` clipboard interop.
  - `FileOperationService` & `FileOperationQueue`: Background channel worker queue with byte-level progress, transfer speed, ETA, Pause/Resume (`ManualResetEventSlim`), Cancel, conflict resolution (`Skip`, `Overwrite`, `Keep Both` + *Apply to All*), staged `.orbit_part_` copy safety, symlink/junction non-recursive deletion safeguards, and Windows Recycle Bin support.
  - `BatchRenameService`: Two-column live preview, prefix/suffix/base override, text/Regex replace (with regex timeout protection), capitalization, sequential numbering, character removal, collision & reserved Windows device name validation, atomic two-phase execution, and verified Undo (`Ctrl+Z`).
  - `SearchQueryParser` & `FileSearchService`: Boolean AST parser (`AND`, `OR`, `NOT`, parentheses) with streaming incremental results into dedicated search tabs, regex backtracking timeouts, and saved JSON presets.
  - `PreviewService` & `ThumbnailService`: `IShellItemImageFactory` shell thumbnails with bounded LRU caching, zoomable/checkerboard image preview, non-locking 64 KB text/code preview, PDF header/page visual preview, Windows Property System (`IPropertyStore`) audio/video metadata, and binary PE/hex signature inspection.
  - `ChecksumService`, `DuplicateFinderService`, `FolderSizeAnalyzerService`, `ShortcutService`: Single-pass streaming MD5/SHA-1/SHA-256/SHA-512 calculator, 3-stage byte-exact duplicate finder, junction-safe recursive disk usage analyzer, and customizable shortcuts.

### Portable Directory Structure

Since 1.2.0 Orbit is **one self-contained `OrbitFileManager.exe`** (about 155 MB). It runs on any Windows 10/11 x64 PC without installing .NET. Put it in a folder of its own: on first start it creates `Data`, `Logs` and `Cache` next to itself. (WPF's native libraries, about 8 MB, are unpacked once to `%TEMP%\.net\OrbitFileManager`.)

When `OrbitFileManager.exe` runs in a writable folder, all state stays self-contained alongside the executable (if launched from a read-only folder such as `Program Files`, it automatically falls back to `%LOCALAPPDATA%\OrbitFileManager`):

```text
OrbitFileManager/
├── OrbitFileManager.exe
├── Data/
│   ├── settings.json         # Theme, columns, toolbar customization, window bounds
│   ├── sessions.json         # Open tabs (folder, pin/lock, label, view, sort, selection, history), panes. Saved continuously
│   ├── sessions.json.bak     # The previous save; used automatically if sessions.json is damaged
│   ├── workspaces.json       # Named multi-pane workspace presets
│   ├── favorites.json        # User pinned favorite folders
│   ├── item_metadata.json    # Per-file/folder color tags & custom notes
│   ├── search_presets.json   # Saved advanced search queries
│   └── shortcuts.json        # Customizable keyboard bindings
├── Logs/
│   └── orbit-YYYYMMDD.jsonl  # Structured JSON Lines diagnostic logs
└── Cache/                    # Runtime cache directory
```

### Tabs survive crashes and restarts

Open tabs are saved about a second after any change (opening, closing, moving or pinning a tab, changing folder, view or sort) and every 10 seconds, not only when Orbit closes. A crash, *End task*, a forced kill or a Windows restart loses at most the last second. Each save is atomic: it is written to `sessions.json.tmp`, flushed to disk, then swapped in, and the file it replaces is kept as `sessions.json.bak`. A damaged `sessions.json` (for example after a power cut) is renamed to `sessions.json.corrupt-<time>` and the tabs come back from the backup.

On start every saved tab comes back, including tabs whose folder is gone or offline (a deleted folder, a NAS or mapped drive that is not connected yet). Those tabs show a warning icon and a banner with **Retry** and **Open nearest folder**. Folders load in the background, so a slow or offline network path never holds up startup.

---

## 2. Build, Test & Portable Publish Commands

> **SDK Note**: `<OrbitTargetFramework>` is centralized in `Directory.Build.props` (currently targeting `net8.0-windows` to match the installed .NET 8.0.425 SDK; change `<OrbitTargetFramework>` to `net10.0-windows` when building with the .NET 10 SDK).

Run from the repository root:

```powershell
# 1. Build the entire solution
dotnet build OrbitFileManager.sln -c Release

# 2. Run the automated xUnit tests (isolated in temporary directories)
dotnet test OrbitFileManager.sln

# 3. Launch in development mode
dotnet run --project src/OrbitFileManager/OrbitFileManager.csproj

# 4. Publish the single self-contained OrbitFileManager.exe (what releases ship)
dotnet publish src/OrbitFileManager/OrbitFileManager.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded `
  -o publish/OrbitFileManager
```

Compression (`-p:EnableCompressionInSingleFile=true`) is deliberately off: it shrinks the exe from 155 to 69 MB, but the release zip is 63 MB either way, and it adds about 120 ms to every start and about 100 MB of memory.

---

## 3. Search Query Grammar

Orbit's search engine supports wildcards, field filters, size/date comparisons, regular expressions (protected by a 250 ms per-item match timeout), parentheses, and boolean operators (`AND`, `OR`, `NOT` — space between terms defaults to `AND`):

| Syntax | Description | Example |
|---|---|---|
| `*.ext` / `*term*` | Wildcard filename match (`*` and `?`) | `*.png`, `*.blend`, `sc_*_v??.exr` |
| `name:<pattern>` | Filename substring or wildcard match | `name:render`, `name:"final pass"` |
| `ext:<ext>` | Exact extension match (with or without `.`) | `ext:exr`, `ext:png OR ext:jpg` |
| `regex:<pattern>` | .NET regular expression against filename | `regex:^sc_[0-9]{2}_` |
| `size:<op><value>` | File size (`>`, `>=`, `<`, `<=`, `=`, `min..max`; `B/KB/MB/GB/TB`) | `size:>100MB`, `size:1MB..50MB` |
| `modified:<op><age/date>` | Relative age (`h`, `d`, `w`, `m`, `y`) or ISO date (`YYYY-MM-DD`) | `modified:<7d`, `modified:>2026-01-01` |
| `created:<op><age/date>` | Creation timestamp filter | `created:<24h` |
| `AND` / `OR` / `NOT` | Boolean logic & `( ... )` grouping | `name:character NOT name:backup` |

---

## 4. Default Keyboard Shortcuts

Shortcuts marked *(Customizable)* can be rebound in **Settings → Keyboard Shortcuts** (`Data/shortcuts.json`):

| Shortcut | Action |
|---|---|
| `Ctrl+T` | New Tab *(Customizable)* |
| `Ctrl+W` / Middle-Click Tab | Close Active Tab *(Customizable)* |
| `Ctrl+Shift+T` | Reopen Closed Tab *(Customizable)* |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / Previous Tab |
| `Alt+Left` / `Alt+Right` / `Alt+Up` | Back / Forward / Parent Folder |
| `Ctrl+L` / `Alt+D` | Edit Address Bar |
| `Ctrl+F` | Focus Quick Filter *(Customizable)* |
| `Ctrl+Shift+F` | Open Advanced Search Dialog *(Customizable)* |
| `Ctrl+Shift+D` | Toggle Dual-Pane Mode *(Customizable)* |
| `F6` | Switch Active Pane *(Customizable)* |
| `F2` / `Ctrl+M` | Rename Item / Batch Rename Tool *(Customizable)* |
| `Ctrl+Z` | Undo Last Batch Rename |
| `Ctrl+C` / `Ctrl+X` / `Ctrl+V` | Copy / Cut / Paste (Windows Explorer compatible) |
| Type a name / repeat its first character | Jump to a matching file or folder / cycle matches (all view modes) |
| `Ctrl+Shift+C` | Copy Full Path(s) |
| `Delete` / `Shift+Delete` | Recycle Bin / Permanent Delete |
| `Ctrl+Shift+N` / `Ctrl+N` | New Folder / New Empty Text File |
| `Ctrl+H` / `Ctrl+P` / `Ctrl+B` | Toggle Hidden Files / Preview Panel / Sidebar *(Customizable)* |
| `Ctrl+Shift+U` | Duplicate File Finder *(Customizable)* |
| `Ctrl+Shift+Z` | Folder Size Analyzer *(Customizable)* |
| `Ctrl+Shift+K` | File Checksum Calculator *(Customizable)* |
| `F5` / `Alt+Enter` | Refresh Workspace / Native Windows Properties |

---

## 5. Manual Verification Checklist

Use a disposable test folder (never test destructive operations on production data):

1. **Large File Copy, Pause/Resume & Conflict Resolution**:
   - Open Dual-Pane mode (`Ctrl+Shift+D`), navigate Left and Right panes to two test folders, select a 1 GB+ file, and drag or copy (`Ctrl+C` / `Ctrl+V`) to the other pane.
   - Verify the expandable **Operation Queue** bar shows real-time MB/s speed, byte progress, and ETA. Click **Pause**, confirm disk transfer halts, then **Resume** or **Cancel** (verifying `.orbit_part_` temporary files are cleaned up on cancel).
   - Paste again into the same folder to trigger the **Conflict Resolution Dialog** and test **Skip**, **Keep Both**, and **Overwrite**.
   - For local-to-NAS replacement, select a folder plus sibling MP4s and choose **Overwrite** with **Apply to all**. Verify the destination videos play the new content. Temporary replacement locks retry for up to 3 seconds; persistent locks show the affected path and error in the queue while preserving the old file.
2. **Moving Across Drives & USB/Removable Storage**:
   - Move a folder from `C:\TempTest` to a secondary/USB drive `D:\TempTest` and verify source removal occurs only after complete destination verification.
3. **Network Shares & Disconnection Handling**:
   - Enter a UNC path (`\\server\share`) in the Address Bar (`Ctrl+L`). Verify inaccessible or disconnected shares show a non-crashing inline error banner while keeping all tabs responsive.
   - Open tabs on a NAS share, then end Orbit from Task Manager and restart it with the NAS unplugged: the tabs come back marked unavailable, startup is not delayed, and **Retry** opens them once the NAS is back.
4. **Permission & Locked File Errors**:
   - Navigate into a restricted OS folder (e.g. `C:\System Volume Information`) or attempt to rename a file held open exclusively by another process; confirm Orbit logs the warning and displays a clean error message without crashing.
5. **External File Modifications (`FileSystemWatcher`)**:
   - While viewing a folder in Orbit, create or rename a file in that folder using PowerShell or Notepad; confirm Orbit automatically refreshes the tab within ~400 ms while preserving selection.
6. **Unicode & Long Paths**:
   - Create folders and files containing Thai, Japanese, emoji, and accented characters (`โปรเจกต์_テスト_🚀.txt`) nested >260 characters deep (enabled via `longPathAware` in `app.manifest`), then preview, batch-rename, and calculate SHA-256 checksums.
7. **High-Density Folders (10,000–100,000 Items)**:
   - Open a large directory in **Details** view; verify UI virtualization keeps scrolling smooth, `Ctrl+F` Quick Filter narrows items instantaneously, and switching to **Thumbnails** loads previews asynchronously in the background without freezing navigation.
8. **Drag-and-Drop**:
   - Reorder tabs via drag-and-drop, drag folders onto the **Favorites** sidebar section to pin them, drag files between Left and Right panes (hold `Ctrl` to force Copy, `Shift` to force Move), and drag files between Orbit and Windows Explorer.

---

## 6. Updates, Source Code & Releases

### Updating Orbit

Orbit checks this repository's [GitHub Releases](https://github.com/ariapiacere/orbit-file-manager/releases) a few seconds after it starts. When a newer version exists, an **Update to x.y.z** button appears at the right of the status bar. You can also use **Tools → Check for Updates...**.

The update window shows the release notes, downloads the zip, checks its SHA-256, closes Orbit, replaces `OrbitFileManager.exe` (under whatever name it was started) and starts Orbit again. `Data`, `Logs` and `Cache` are never touched. The new exe is copied next to the old one first and swapped in by a rename, retried for about 15 seconds if something holds the old exe, so a failed update leaves the old version working. The automatic check can be turned off in **Settings → Updates**.

Installs from 1.1.x and earlier are a folder of about 470 files. Their updater copies the new exe over the old one; on its first start the new exe removes the old files. It deletes only the exact names that 1.0.x–1.1.x shipped, and only in a folder that still holds `OrbitFileManager.dll`.

Orbit can only update itself when it runs from a folder it can write to (the portable layout). A copy inside `Program Files` shows a link to the release page instead.

GitHub allows 60 API requests an hour per network without a token, shared by every PC behind the same public IP. Orbit therefore reads the latest version from the `releases/latest` page first and only calls the API when a newer version exists. If the API refuses, the update still installs from the release's download links; the update window then points to the release page for the notes.

### Getting the latest code

```powershell
git clone https://github.com/ariapiacere/orbit-file-manager.git
cd orbit-file-manager
git pull        # later: bring your copy up to date
```

### Releasing a new version

1. Bump `<Version>` in `Directory.Build.props` and commit.
2. Run `./tools/release.ps1 -Notes "What changed"` (needs the .NET 8 SDK and a logged-in GitHub CLI).

The script runs the tests, publishes the single-file exe (and stops if anything else lands next to it or its version is wrong), uploads `OrbitFileManager-win-x64-<version>.zip` (holding `OrbitFileManager\OrbitFileManager.exe`), its `.sha256` and the bare `OrbitFileManager.exe` as release `v<version>`, and pushes the tag. Every running copy offers the update on its next start. The newest exe is always at `https://github.com/ariapiacere/orbit-file-manager/releases/latest/download/OrbitFileManager.exe`.

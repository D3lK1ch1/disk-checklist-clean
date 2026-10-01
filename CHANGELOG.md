# Changelog

## [0.0.2] - 2026-10-02

First macOS build — an experimental, ad-hoc-signed `.dmg` for Apple Silicon, published as a
GitHub **pre-release** 

### Added
- **`scripts/package-mac.sh`** — packages `DiskCleanup.Avalonia` for macOS. `dotnet publish`
  (`-f net10.0 -r osx-arm64 --self-contained`) only produces a folder of binaries, unlike
  Tauri, so the script builds the rest itself: a `Disk Cleanup.app` bundle with an
  `Info.plist` whose `CFBundleExecutable` points at `DiskCleanup.Avalonia`, an ad-hoc
  signature (`codesign -s -`, then `codesign --verify`), and a `.dmg` with an Applications
  shortcut (`hdiutil create -format UDZO`). Ad-hoc signing matters because Apple Silicon
  refuses to run unsigned arm64 binaries at all — the same class of failure as
  AccountabilityApp issue #7. No hardened runtime: it blocks .NET's JIT without extra
  entitlements. `set -euo pipefail` stops the script on any failing step, so e.g. a renamed
  executable fails at `chmod +x` instead of shipping an app that can't launch.
- **`<Version>0.0.2</Version>`** in `DiskCleanup.Avalonia.csproj` — the script reads it for
  `Info.plist` and the DMG name (`DiskCleanup_0.0.2_aarch64.dmg`).
- **`.github/workflows/ci.yml`** — first CI for this repo. One job, `build-macos` on
  `macos-latest`, on every push/PR to `main`, mirroring AccountabilityApp's
  `tauri-build-macos`: runs the script, `hdiutil verify` + mount/unmount, writes
  `dmg.sha256` and the SHA-256 into the run summary, uploads both as the `macos-dmg`
  artifact (`if-no-files-found: error`).
- **README "macOS (Experimental)" section** — unsigned/Apple-Silicon-only warning, checksum
  one-liner against GitHub's per-asset digests, Open Anyway / `xattr` first-launch steps,
  and what differs on Mac (portable scanners only, `$TMPDIR` is permanent-delete SAFE,
  REVIEW items go to `~/.Trash` without Finder's Put Back, possible Full Disk Access).

### Known gaps
- **Not yet run on a real Mac.** CI proves it builds, signs, and produces a valid `.dmg` —
  not that it launches, scans, or that `MacTrashProvider` works. Avalonia sub-unit 9 stays
  open until a tester reports back.
- **No tests run in CI.** `DiskCleanup.Tests` targets `net10.0-windows` (it references
  `Core.Windows`), so it can't run on the macOS runner. Multi-targeting it is a separate unit.
- No Trash scanner on Mac — `MacTrashProvider` is only used to move REVIEW items into
  `~/.Trash`; nothing offers to empty it.
- Full Disk Access for `~/.Trash` is unconfirmed — documented as "if you see a permission
  error", not as a required step.
- Apple Silicon only, no notarization, no Intel/universal build.
- `DevPackageCaches`'s pip-cache path is still wrong on Mac (pre-existing, from 2026-08-31).

### Verification
- CI run 36886749673 on `main`: green in 59s. Log shows
  `Built: …/DiskCleanup_0.0.2_aarch64.dmg` and
  `hdiutil: verify: checksum of "publish/mac/DiskCleanup_0.0.2_aarch64.dmg" is VALID`.
  Artifact `macos-dmg` (48.9 MB) uploaded and downloaded successfully.
- On Windows before CI: `bash -n` syntax check of the script, `<Version>` parsing, and a
  real `osx-arm64` publish producing the `DiskCleanup.Avalonia` executable.

## Session 2026-09-29

### Added
- **`ActionKind.RunDocker` — Docker rows can now actually run, not just suggest.** New action
  in `ActionExecutor.cs` that executes `CommandSuggestion` for real, but only if it starts with
  exactly `docker`. The remaining words go through `ProcessStartInfo.ArgumentList` — no shell —
  so the docker binary is the only thing it can ever launch. Deliberately not a generic
  `RunCommand`: that would let any scanner bug become "run anything." stdout/stderr are read
  concurrently (reading one to the end first can deadlock if the other's buffer fills), with a
  2-minute timeout that kills the process tree. The result message carries docker's own output
  either way. Prune commands must include `-f`, since stdin isn't attached and docker's
  "Are you sure? [y/N]" prompt would otherwise abort them.
- **`DockerVolumes()` scanner (`Scanners.cs`) — one row per Docker volume.** Before this, the
  only volume visibility was `Docker()`'s single "Docker Volumes (reclaimable)" summary row, so
  there was no way to tell e.g. a live project's database from a dropped project's leftovers.
  Reads `docker system df -v --format "{{json .Volumes}}"` (format confirmed against real
  output first — every field, including `Links` and `Size`, is a JSON string). Volumes with
  `Links == "0"` become REVIEW rows with `RunDocker: docker volume rm <name>`; anything else
  (a count, or `N/A`) is treated as in use and listed INFO-only with no action — docker would
  refuse the removal anyway. Reason text names the compose project (from the
  `com.docker.compose.project` label) or flags the volume as anonymous, warns when the name looks
  like a database (`pgdata`/`mysql`/`db_data`/...), and states plainly that deletion is permanent
  (no Recycle Bin) and that on Windows the space is freed inside Docker's `.vhdx`, not on C:,
  until compacted. Anonymous volumes are labeled by the first 12 characters of their hash but
  removed by full name. JSON parsing is split into public `ParseDockerVolumes(string)` so it's
  testable without Docker. Wired into all four scan lists: WPF, Avalonia (Windows and
  Mac/Linux branches), and the console app.

### Tests
- `ActionExecutorTests.RunDocker_NonDockerCommand_IsRefused` — 6 cases (null, empty, bare
  `docker`, `powershell ...`, `dockerx ...`, `cmd /c docker ...`), all refused before any process
  starts, so they pass with or without Docker installed.
- New `DockerVolumesTests.cs`: 4 parser tests against a trimmed copy of real output (unused
  named/database volume, anonymous volume, in-use volume, empty array), plus
  `E2E_ScanThenRemove_DeletesRealVolume` — creates a throwaway `diskcleanup-e2e-<guid>` volume,
  asserts the scanner lists it as `RunDocker`, deletes it through `ActionExecutor.Execute`, and
  asserts `docker volume ls` no longer shows it. Only ever touches its own volume.

### Known gaps
- The E2E test returns early (passes without asserting) when Docker isn't running — xunit 2.9
  has no runtime skip. A green run on a machine without Docker proves nothing about it.
- `Docker()`'s existing "Docker Volumes (reclaimable)" summary row still suggests plain
  `docker volume prune`, which since Docker 23 only removes *anonymous* volumes — it can
  under-deliver against the reclaimable figure shown. Now largely redundant with the per-volume
  rows. The Images/Containers/Build cache rows remain `SuggestCommand`; converting them to
  `RunDocker` was considered and dropped as not needed.
- This partially reverses the [0.0.1] design decision "Docker ... cleanups are surfaced as
  suggested commands, not executed directly" — for volumes only.

### Verification
- `dotnet test` (filtered to `DockerVolumesTests` + `ActionExecutorTests`) — 25 passed, 0 failed,
  with Docker Desktop running (`docker info` exit 0), so the E2E test genuinely exercised a real
  volume; no leftover `diskcleanup-e2e-*` volume afterwards.
- `dotnet build` of `DiskCleanup.Avalonia`, `DiskCleanup.Wpf`, and `DiskCleanup` — all succeeded.

## Session 2026-09-11

### Fixed
- **WPF widget: log panel couldn't be resized, and maximizing the window didn't grow it.**
  `MainWindow.xaml`'s log row was a hardcoded `Height="120"` while only the DataGrid row was
  `*` — every extra pixel from maximizing went to the grid, never the log. Restructured into
  a nested `Grid` (DataGrid + `GridSplitter` + Details pane + action bar, all inside outer
  row 1) so a second `GridSplitter` between that block and the log negotiates space with the
  DataGrid's `*` row directly. This nesting was necessary because a `GridSplitter` only ever
  resizes its own immediate previous/next row — without it, dragging the log's splitter could
  only push against the action bar (fixed to its buttons' natural height), not reach through
  to the grid. Also dropped the Details pane's `ScrollViewer.MaxHeight="100"`, which capped it
  below whatever height its own splitter gave it.
- **Clean Selected could freeze the UI when a delete partially failed across many files.**
  `ActionExecutor.cs`'s `DeleteContents`/`DeleteFolder` joined every blocked file into one
  message with `string.Join("; ", failures)` — for a folder with hundreds of locked files
  (e.g. `SoftwareDistribution\Download` mid-Windows-Update), this produced one single
  multi-thousand-character line with no breaks. WPF's `LogBox` (`TextWrapping="Wrap"`) has to
  recompute word-wrap points across that entire run on every layout pass — once when the text
  is set, and again on every resize/maximize — which was very likely the real driver of the
  maximize/minimize freeze reported earlier this session, not row count (confirmed row count
  wasn't it: an empty, unscanned grid resized smoothly; a populated one didn't). Changed both
  join sites (plus `DeleteFolder`'s catch-block `detail` var) to join failures with
  `Environment.NewLine` instead, in shared `Core` code — fixes the console app's output too,
  not just WPF's.

### Known gaps
- The `Environment.NewLine` fix reduced but did not eliminate the freeze on a large
  partial-delete failure — a plain WPF `TextBox` with wrapped content still doesn't scale well
  once a log runs into the hundreds of lines. A real fix would mean not laying out the whole
  log as one `TextBox.Text` blob at all — e.g. a virtualized `ListBox`/`ItemsControl` bound to
  individual log lines, so only visible lines get measured. Deferred; capped for this session.

### Verification
- Live runs via `DiskCleanup.Wpf`: confirmed both `GridSplitter`s drag as expected (grid vs.
  details, and the whole top block vs. log); confirmed the maximize freeze correlates with
  DataGrid row count by comparing an empty grid (smooth) against a populated one (froze)
  *before* the `ActionExecutor` fix; re-ran Scan → Clean Selected against the same
  `SoftwareDistribution\Download` access-denied scenario after the fix — freeze is smaller but
  still present.

## Session 2026-08-31

### Added
- Avalonia port sub-unit 5: `ConfirmDialog` (hand-rolled modal window, no new NuGet package —
  Avalonia has no `MessageBox.Show` equivalent) + `MainWindow.axaml.cs`'s `CleanButton_Click`,
  mirroring WPF's confirm-then-clean flow exactly (message listing selected items → Yes/No →
  `ActionExecutor.Execute` off-thread → log + free-space before/after). Two adaptations from
  WPF's version, both because Avalonia's `DataGrid` behaves differently: no
  `ItemsGrid.CommitEdit()` call (the method doesn't exist on Avalonia's `DataGrid`, and isn't
  needed — `ItemCheckBox_Click` already writes `vm.IsSelected` synchronously, not through a
  deferred row-edit commit); cleaned items are removed from `_allItems` then `ApplyFilter()`
  regenerates `ItemsGrid.ItemsSource` fresh, rather than mutating a persistent `_visibleItems`
  collection like WPF does (matches sub-unit 4's existing reassignment pattern — Avalonia's
  grid didn't rebind on in-place `ObservableCollection` mutation).

### Fixed
- **Read-only files were misreported as locked instead of being deleted.**
  `ActionExecutor.cs`'s `DeleteContents`/`SafeDeleteTree`/`DeleteFile` called `File.Delete`
  directly, which throws "Access is denied" on any file flagged `FileAttributes.ReadOnly` —
  indistinguishable in the log from a real process lock, and unrecoverable by retrying since
  the attribute never changes on its own. Found via a real Clean Selected run: four files
  inside a git repo sitting in a scratch `Temp` folder failed every attempt (git writes packed
  objects read-only by design). New `DeleteFileClearReadOnly` helper clears the `ReadOnly`
  attribute before deleting, wired into all three call sites. 3 new tests
  (`DeleteFolder_ReadOnlyFile_IsClearedAndDeleted`, `DeleteContents_ReadOnlyFile_IsClearedAndDeleted`,
  `DeleteFile_ReadOnlyFile_IsClearedAndDeleted`).

### Known gaps
- Avalonia port sub-units 6-9 (runtime `PlatformSetup` OS branch, multi-target conversion to
  `net10.0-windows;net10.0`, docs pass, real Mac hardware verification) remain unstarted — see
  SPEC.md's "Avalonia port" section. No real Mac hardware available this session; sub-unit 7
  (multi-target) has to land before sub-unit 9 (hardware verification) is even attemptable,
  and access to an actual Mac to test on is the open blocker for 9 specifically.
- `DevPackageCaches`'s pip-cache path is still wrong on Mac (pre-existing, deferred, unrelated
  to this session's work).

### Verification
- `dotnet build` (solution-wide) — 0 warnings, 0 errors.
- `dotnet test` — 77 passed, 0 failed, 0 skipped (74 existing + 3 new ReadOnly tests).
- Live run via `DiskCleanup.Avalonia`: Clean Selected against real Recycle Bin/User Temp/
  Windows Temp/Windows Update cache entries — confirm dialog appeared listing selected items, Yes proceeded, resulting log matched the established `[OK]`/`[FAILED]` format exactly. A second live run after the ReadOnly fix confirmed the previously-failing git-object files (inside a scratch `LibreCrawl\.git\objects\pack\...` path) now clear instead of appearing under "Blocked by."

## Session 2026-08-28

### Fixed
- **`PersonalFolders()`/`DownloadsTopFolders()` (`Scanners.cs`) reported "Path not found" for
  any top-level entry that was a file, not a folder.** Both scanners enumerate
  `Directory.EnumerateFileSystemEntries` (files and folders mixed) but hardcoded every entry's
  action to `ActionKind.MoveFolderToRecycleBin`, whose existence guard checks
  `Directory.Exists` — always `false` for a file path. Found via a real WPF run: a REVIEW row
  for a file directly under OneDrive-redirected `Documents` failed to clean with "Path not
  found" even though the file was genuinely still there, untouched. Fixed by picking
  `MoveFileToRecycleBin` vs `MoveFolderToRecycleBin` per entry based on `Directory.Exists`
  instead of assuming folder. Affects both WPF and the in-progress Avalonia port equally,
  since both call the same Core scanners.
- **Avalonia app crashed on startup before any window appeared.** `MainWindow.axaml`'s
  `RiskFilterCombo` set `SelectedIndex="0"` directly in XAML, which fires `SelectionChanged`
  during `InitializeComponent()` — before `ItemsList` (declared later in the same file) is constructed. The handler's `ApplyFilter()` dereferenced `ItemsList` and threw
  `NullReferenceException`, so `dotnet run` returned to the prompt immediately with no visible window and no printed error (the exception was only visible running `dotnet run` directly, not backgrounded). Fixed by removing `SelectedIndex` from XAML and setting it in the `MainWindow` constructor after `InitializeComponent()`, once every named control exists.

### Known gaps
- Both fixes verified via `dotnet build` (0 warnings, 0 errors) and `dotnet test` (74 passed,
  0 failed) only as of this entry — live re-verification (relaunch `DiskCleanup.Avalonia` and
  confirm the window opens and the filter/details panel work; relaunch WPF and confirm the
  OneDrive `Documents` file now actually moves to Recycle Bin) not yet done this session.

### Verification
- `dotnet build` (solution-wide) — 0 warnings, 0 errors.
- `dotnet test` — 74 passed, 0 failed, 0 skipped.

## Session 2026-08-18 

### Added
- `RoamingAppData()` scanner (`Scanners.cs`) — flags top-level `%APPDATA%` folders whose
  name follows the reverse-domain identifier convention (`com.vendor.app`) that
  Tauri/Electron-style desktop apps use for per-user data. Matched via a curated TLD-prefix
  allowlist (`com`/`org`/`net`/`io`/`dev`/`app`/`co`/`me`/`xyz`, ≥3 dot-separated segments),
  not a bare "contains a dot" check, so vendor folders like `Microsoft` or versioned names
  like `Python 3.12` can never match. Always REVIEW + `MoveFolderToRecycleBin`, never SAFE —
  unlike `StalePackages`, there's no install cross-check for this ID format (bundle IDs don't
  map onto registry DisplayNames, and `Get-AppxPackage` only covers MSIX/Store apps), so the
  Reason text says so plainly instead of implying a confidence level the scanner doesn't have.
  Found by inspecting a real leftover: `com.d3lk1ch1.accountabilityapp` (88KB, containing an
  `accountability.db.unreadable.<n>` file — a database that failed to open/decrypt and was
  renamed aside, a strong dead-prototype signal).

### Fixed
- **WSL/network paths falsely reported as "Moved to Recycle Bin."** Confirmed empirically
  (not assumed): a throwaway file was written to `\\wsl.localhost\Ubuntu\home\...`, then
  `WindowsTrashProvider.MoveToTrash` was called against it directly, then the real Recycle Bin
  was searched via `Shell.Application`'s `Namespace(10)` for the item. `SHFileOperation` with
  `FOF_ALLOWUNDO` returned success and this tool reported "Moved to Recycle Bin" — but the file
  was never in the Recycle Bin. It was silently, permanently gone. The Windows Recycle Bin has
  no concept of a UNC/network location; `FOF_ALLOWUNDO` is silently ignored for such paths.
  This means every existing REVIEW-tier WSL row using `MoveFolderToRecycleBin`/
  `MoveFileToRecycleBin` (`.claude`/`.codex` cache dirs, session `.jsonl` transcripts, orphaned
  subagent folders) was already misreporting recoverability before this fix, for as long as
  those scanners have existed.
  - `WindowsTrashProvider.MoveToTrash` now detects UNC paths (`\\` prefix) up front and
    explicitly permanently-deletes via the existing reparse-point-safe `SafeDeleteTree`
    (bumped from `private` to `internal` in `ActionExecutor` so it can be reused instead of
    duplicated), returning an honest result: *"Permanently deleted - the Recycle Bin doesn't
    support network/WSL paths... cannot be undone."*
  - Every WSL-path `CheckItem` whose action requests the Recycle Bin now carries the same
    caveat in its `Reason` text (`Scanners.AppendUncRecycleBinNote`), so it's visible in the
    Details panel *before* the user clicks Clean, not only in the after-the-fact log message.
- **REVIEW-tier build dirs used permanent delete instead of Recycle Bin.** `ClassifyBuildDir`
  (`node_modules`/`target` discovery, both WSL and native) used `ActionKind.DeleteFolder` for
  *every* tier, including the two REVIEW branches (no project marker found; `node_modules`
  with no lockfile) — inconsistent with this project's own stated safety model ("REVIEW uses
  Recycle Bin where possible," Review.md). Both REVIEW branches now use
  `MoveFolderToRecycleBin`. On native paths (Downloads/Documents/Desktop) this is a genuine
  fix — an uncertain-marker hit is now actually recoverable. On WSL paths it's still
  effectively permanent per the OS limitation above, but is now honestly labeled instead of
  silently so. SAFE-tier build dirs (marker + lockfile both present) are unchanged — still
  permanent delete, consistent with how this codebase already treats other high-confidence
  regenerable caches (temp/cache contents).
- **WPF Details pane text wasn't copyable.** `DetailsText` (`MainWindow.xaml`) was a
  `TextBlock` — WPF `TextBlock` content genuinely cannot be mouse-selected at all, regardless
  of styling. Swapped for a read-only, borderless `TextBox`, which renders identically but
  supports drag-select and copy.

### Known gaps
- No automated test coverage added this session for `RoamingAppData` (it does take a
  `rootOverride` for testability, unlike Docker/Wsl — this is a real gap, not an
  can't-be-seamed precedent) or for `WindowsTrashProvider`'s new UNC-detection branch
  (its delegated deletion logic is exercised indirectly via `SafeDeleteTree`'s existing
  tests, but the UNC-path branch itself has zero direct coverage). Verified live/empirically
  instead — see Fixed above.

### Verification
- `dotnet build` — 0 warnings, 0 errors.
- `dotnet test` — 68 passed, 0 failed, 0 skipped.
- Live empirical test against a real WSL (Ubuntu) UNC path, described under Fixed above.

## [0.0.1] - 2026-08-07

Packaging fix — supersedes [0.0.0] as the first binary that actually runs when downloaded.
[0.0.0]'s GitHub release asset never worked on any machine other than the one it was built
on. Not the same release as the `[0.0.1] - 2026-06-15` entry further below — that predates
the version reset noted under [0.0.0]; kept both rather than rewriting history, same call
made when [0.0.0] superseded [0.0.4].

### Fixed
- Release exe didn't launch at all — no window, no error dialog, no Application or Defender
  event log entry, on a machine with the correct .NET runtime installed. Root cause:
  `DiskCleanup.Wpf.csproj` had no `SelfContained`/`RuntimeIdentifier`, so `dotnet publish`
  produced a framework-dependent build needing `DiskCleanup.Wpf.dll`, `DiskCleanup.Core.dll`,
  `.deps.json`, and `.runtimeconfig.json` alongside the exe — only the bare exe was ever
  attached to the [0.0.0] release. Republished self-contained + single-file
  (`dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
  -p:IncludeNativeLibrariesForSelfExtract=true`). `PublishSingleFile` alone still left five
  native WPF DLLs (`D3DCompiler_47_cor3.dll`, `PresentationNative_cor3.dll`,
  `wpfgfx_cor3.dll`, `PenImc_cor3.dll`, `vcruntime140_cor3.dll`) loose next to the exe,
  reproducing the identical failure in testing — `IncludeNativeLibrariesForSelfExtract` was
  needed to actually bundle them.
- README's "self-contained" claim for [0.0.0] was inaccurate given the actual
  framework-dependent build; corrected to describe the real single-file exe.

### Verification
- Copied the rebuilt exe alone into an isolated folder (no companion files) and launched it
  via `Start-Process`: confirmed a real window (title "Disk Cleanup", valid
  `MainWindowHandle`) opened — not just "process didn't crash." Ruled out SmartScreen
  (reproduced after clicking "Run anyway"), antivirus (Defender operational log showed only
  routine health checks, no detection/quarantine events), and a missing runtime
  (`Microsoft.WindowsDesktop.App 10.0.2` confirmed installed) before finding the actual
  cause.

## [0.0.0] - 2026-07-23

First public release — prebuilt, self-contained `.exe` published on GitHub Releases.
Version number reset to mark this as the first binary anyone outside this machine can
run; it supersedes [0.0.4] in scope — everything below was built and verified after that
entry was written, on top of everything [0.0.4] already covered.

### Added
- `NativeBuildDirs`/`FindNativeBuildDirs` — Windows-native `node_modules`/`target`
  scanning under Downloads/Documents/Desktop, reusing the same SAFE-only-with-a-project-
  marker logic as WSL's `FindBuildDirs`. Test coverage in `ScannersTests.cs`.
- `ITrashProvider` platform-abstraction seam — `MoveToTrash`/`EmptyTrash` now go through
  an interface (`WindowsTrashProvider` the only implementation today), replacing direct
  Win32 `DllImport` calls in `ActionExecutor`.
- Subagent folder cascade + orphan detection — `ScanClaudeFolder` matches a session's
  `.jsonl` against its sibling `subagents`/`tool-results` folder and deletes both in one
  action; a folder left behind by a prior partial cleanup gets its own SAFE row instead of
  being missed.
- `WindowsTempFolder()` — system-wide `C:\Windows\Temp` scanning/cleanup, distinct from
  user `%TEMP%`.
- `DevPackageCaches()` — NuGet (`~/.nuget/packages`) and pip (`%LocalAppData%\pip\Cache`)
  cache scanning.

### Known issues
- Docker scanner can show `0B` reclaimable rows as actionable.
- WSL build-folder discovery can still recurse through reparse points before size
  calculation/deletion guards apply.
- WPF checkbox behavior is manually verified only; automated tests cover DiskCleanup.Core,
  not WPF binding behavior.

### Verification
- Live full run, 2026-07-22: Recycle Bin + User Temp + Windows Temp + WSL/native AI-folder
  session files, 51.1GB → 60.5GB free (+9.4GB), zero crashes, every locked-file skip named
  by specific path and reason. NuGet/pip deletion itself not exercised this run (held off
  while `dotnet run` was active in the same session).
- Live run, 2026-07-15: `WindowsTempFolder` cleared 121/123 entries without elevation (2
  correctly skipped as genuinely locked); subagent-cascade fix found and cleanly deleted 19
  pre-existing orphaned session folders, zero failures.

## [0.0.4] - 2026-07-09

### Fixed
- Delete-side failure detail — DeleteContents/DeleteFolder/SafeDeleteTree now report which
  specific file or subfolder blocked a delete (locked, access denied) instead of a silent
  skip count.
- WSL node_modules/target scope — FindBuildDirs now classifies each hit SAFE only when a
  project marker (package.json, Cargo.toml/pom.xml/build.sbt) sits next to it, REVIEW
  otherwise. Known remote-dev-server dirs (.vscode-server and siblings) are pulled out as
  INFO-only rows instead of being walked into and offered as deletable.

### Known issues
- Docker scanner can show `0B` reclaimable rows as actionable.
- WSL build-folder discovery can still recurse through reparse points before size
  calculation/deletion guards apply — separate from the node_modules scope fix above.
- WPF checkbox behavior is manually verified only; automated tests cover DiskCleanup.Core,
  not WPF binding behavior.

### Verification
- `dotnet test` passed on 2026-07-09: 43 passed, 0 failed, 0 skipped.

## [0.0.3] - 2026-06-18.

### Known issues
- Docker scanner can show `0B` reclaimable rows as actionable.
- WSL `~/projects` build-folder discovery can still recurse through reparse points before size calculation/deletion guards apply.
- Scan-time errors are often swallowed, hiding Docker/WSL/registry/permission failures.
- WPF cleanup runs on the UI thread and can freeze during large deletes or shell operations.
- WPF checkbox behavior is manually verified only; automated tests cover `DiskCleanup.Core`, not WPF binding behavior.

### Verification
- `dotnet test` passed on 2026-06-18: 19 passed, 0 failed, 0 skipped.

## [0.0.2] - 2026-06-17

Safety hardening for the REVIEW-risk scanners, plus symlink/junction and WSL accounting fixes.

### Added
- `MoveFolderToRecycleBin` action — Downloads top-folders, stale AppData packages, and AI tool folder scanners now soft-delete via the Recycle Bin (`SHFileOperation` + `FOF_ALLOWUNDO`) instead of permanent deletion, so REVIEW-risk picks are recoverable.
- WSL compaction note — deleting a `\\wsl.localhost\...` path appends a reminder to the result message that freed space won't show on `C:` until the WSL virtual disk is compacted.

### Fixed
- Self-deletion guard — the Downloads top-folders scan no longer lists (or offers to delete) the folder this tool is running from.
- Symlink/junction safety, scan side — `GetDirectorySize` skips reparse points instead of following them, so sizes aren't inflated or misleading for linked directories (e.g. pnpm-style `node_modules` links, WSL mounts).
- Symlink/junction safety, delete side — `DeleteFolder` and `DeleteContents` now remove a reparse point as a link only, never recursing into its target.

### Tests
- Added coverage for `MoveFolderToRecycleBin` (success and missing-path cases).
- Added a junction-based reparse-point guard test (`DeleteFolder_DoesNotRecurseIntoJunction`), built with `mklink /J` so it exercises the real guard logic without requiring Developer Mode or elevation.

## [0.0.1] - 2026-06-15

Initial working version. Manual, interactive disk cleanup via CLI and a WPF widget.

### Added
- Scanner covering 9 categories: Recycle Bin, Windows Temp / SoftwareDistribution, VS Code VSIX cache, WSL caches and build dirs, Docker reclaimable space, top Downloads folders, stale AppData\Local\Packages, AI tool folders (.claude/.codex), and top installed apps by size (informational).
- Console app: numbered checklist, selection by number or `all-safe`, confirmation step before any action, free-space before/after report.
- `DiskCleanup.Core`: shared scanner, selection parser, and action executor used by both the console app and the widget.
- WPF widget: checklist grid with checkboxes, risk filter dropdown (All/SAFE/REVIEW/INFO), select-all-SAFE and clear-selection shortcuts, confirmation dialog before cleanup, free-space before/after log.
- xUnit test suite for selection parsing and action execution against throwaway temp directories.

### Design decisions
- No item is deleted without an explicit confirmation step, regardless of risk level (including `all-safe`).
- Installed apps (INFO) are view-only — no delete/uninstall action is wired up.
- Docker and other admin-adjacent cleanups are surfaced as suggested commands, not executed directly.

# Changelog

## [0.0.2] - 2026-08-18 (drafted, not yet tagged/released)

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

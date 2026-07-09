# Changelog

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

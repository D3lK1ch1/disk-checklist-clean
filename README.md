# disk-checklist-clean

A personal Windows disk-space cleanup tool. Scans common sources of reclaimable space, shows you a checklist, and only deletes what you explicitly tick and confirm. Nothing runs automatically, and nothing leaves your machine — there are no network calls anywhere in this codebase.

## Features

- Scanner + checklist printer (console)
- WPF widget: checklist grid, risk filter, details pane, Clean Selected
- Every scan/delete failure is surfaced by name and reason (no silent skip counts)
- Before/after free-space totals on every cleanup run

The scheduled/background check work is still pending — see [Roadmap](#roadmap).

## What it scans

- Recycle Bin
- User Temp (`%TEMP%`), system-wide `C:\Windows\Temp`, and Windows Update's `SoftwareDistribution\Download` cache
- VS Code `CachedExtensionVSIXs`
- Dev package caches: NuGet (`~/.nuget/packages`), pip (`%LocalAppData%\pip\Cache`)
- WSL: `~/.cache`, `~/.npm`, `~/.local/share/pnpm/store`, and any `node_modules`/`target` dirs anywhere under `$HOME` — classified SAFE (project marker present), REVIEW (marker missing), or INFO (inside a known remote-dev-server dir like `.vscode-server`, excluded rather than walked into)
- Native (non-WSL) `node_modules`/`target` build artifacts under Downloads, Documents, and Desktop, using the same project-marker check
- Docker reclaimable space (`docker system df`) and Docker Desktop's WSL2 `.vhdx` bloat (never auto-shrinks — flags it and suggests a `diskpart` compaction command to run yourself)
- Top-N largest folders in Downloads, Documents, Desktop, Pictures, Music, Videos
- `AppData\Local\Packages` folders untouched 6+ months, cross-checked against `Get-AppxPackage` to distinguish "app uninstalled" from "app still installed, folder just looks stale"
- `AppData\Roaming` folders named like a reverse-domain identifier (`com.vendor.app`, the convention Tauri/Electron-style desktop apps use) — always REVIEW, no install cross-check possible for this ID format
- `.claude` / `.codex` AI tool folders — known-safe cache subpaths, plus individual old session transcripts (age, message count, and a first-message excerpt so you can judge each one, not a size-only guess)
- Top installed apps by size (registry) — **informational only**

## Risk levels

Every scanned item is tagged:

- **SAFE** — fully regenerable caches/build artifacts (Recycle Bin, temp files, npm/NuGet/pip/VSIX caches, `node_modules`/`target` with a confirmed project marker). Still requires your confirmation before deletion — nothing is auto-deleted, even SAFE items, even via `all-safe`.
- **REVIEW** — needs your judgement (Downloads/Documents/Desktop folders, stale AppData packages, Docker prune, AI tool session files). You decide case by case. Folders in this category go to the Recycle Bin rather than permanent delete, so a wrong pick is recoverable.
- **INFO** — installed apps list, and items like Docker's `.vhdx` compaction that require a command you copy into an elevated terminal yourself. No delete action exists for this category from inside the app.

### Why installed apps are INFO-only, not deletable

Deleting a cache folder removes bytes nothing else depends on, and it's fully regenerable. Uninstalling an app means running an external uninstaller that can touch the registry, shared DLLs, services, and licensing — a much larger and less predictable blast radius, often needing admin rights. This tool deliberately keeps that out of scope: it shows you the list for awareness only.

## Safety

- **Self-deletion guard** — the Downloads scan never lists (or offers to delete) the folder this tool is running from, even if it ranks in the top N by size.
- **Recycle Bin, not permanent delete** — REVIEW-risk folders go through `SHFileOperation` with `FOF_ALLOWUNDO`, so they're recoverable. SAFE items (temp/cache contents) are deleted directly, since they're fully regenerable by design. **Exception:** the Windows Recycle Bin doesn't support network/WSL paths (`\\wsl.localhost\...`) at all — REVIEW items found there (WSL `.claude`/`.codex` cache, session files, build dirs) are permanently deleted instead, and the result message and Reason text say so explicitly rather than claiming a recoverability that isn't real.
- **Symlinks and junctions are never followed** — both when computing folder sizes and when deleting a folder, a symlink or junction found inside it is removed as a link only. Its target (e.g. a pnpm-style `node_modules` link, or a WSL mount) is left untouched.
- **WSL space accounting** — deleting files under a `\\wsl.localhost\...` path frees space inside that distro's virtual disk, not on `C:` directly. The result log notes this so the before/after free-space numbers aren't confusing.
- **`.claude`/`.codex` root folders are never offered whole** — only an allowlist of verified-safe cache subpaths and individual old session files. The root holds credentials, settings, and live memory files.
- **No admin auto-elevation** — anything that would need it (Docker vhdx compaction, MSI uninstalls) is printed as a command to copy into an elevated terminal, never run directly by the app.
- **No network calls** — every scan reads local disk/registry/CLI output only. Nothing is uploaded, logged externally, or phoned home.

## Project structure

- `DiskCleanup.Core/` — scanners, action executor, selection parsing (shared library)
- `DiskCleanup/` — console app (checklist + numeric selection)
- `DiskCleanup.Wpf/` — desktop widget (checklist grid, risk filter, details pane, Clean Selected)
- `DiskCleanup.Tests/` — xUnit tests for `Core`

## Running it

Requires the .NET 10 SDK on Windows.

**Widget (recommended):**
```powershell
cd DiskCleanup.Wpf
dotnet run
```
Click **Scan**, tick items, optionally filter by risk, then **Clean Selected**.
A confirmation dialog lists exactly what will be processed before anything happens.

**Console:**
```powershell
cd DiskCleanup
dotnet run
```

**Avalonia widget (cross-platform port, in progress):**
```powershell
cd DiskCleanup.Avalonia
dotnet run --framework net10.0-windows
```
This project multi-targets `net10.0-windows` and `net10.0` (the Mac/Linux build isn't
wired up yet so plain `dotnet run` fails with "Your project
targets multiple frameworks" and `--framework` on its own errors with "Required argument
missing" — it needs a value, not just the flag.

**Tests:**
```powershell
dotnet test
```

## Getting the exe without building it

Grab the latest `.exe` from [Releases](https://github.com/D3lK1ch1/disk-cleanup/releases)
— a single self-contained file (~130MB), no .NET runtime install and no other files needed
alongside it. It's unsigned, so Windows SmartScreen will show a "Windows protected your PC"
warning on first run — that's expected for an unsigned indie exe, not a sign anything's
wrong. Click **More info → Run anyway** to proceed.

## Roadmap

- `--check` mode: silent scan + Windows toast notification when free space drops below 45GB, triggered via Task Scheduler (setup command printed, not auto-configured)
- Filter by category/size in the widget (risk-level filter already exists)
- Cross-platform support (Linux, Mac) via an Avalonia UI rewrite of the widget, alongside the existing WPF version — blocked on splitting `DiskCleanup.Core` off its current Windows-only target framework first

## License

[MIT](LICENSE)

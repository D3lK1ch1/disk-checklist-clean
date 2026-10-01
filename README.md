# disk-checklist-clean

A personal disk-space cleanup tool for Windows, with an experimental macOS build. Scans common sources of reclaimable space, shows you a checklist, and only deletes what you explicitly tick and confirm. Nothing runs automatically, and nothing leaves your machine — there are no network calls anywhere in this codebase.

## Features

- Scanner + checklist printer (console)
- WPF widget: checklist grid, risk filter, details pane, Clean Selected
- Every scan/delete failure is surfaced by name and reason (no silent skip counts)
- Before/after free-space totals on every cleanup run

The scheduled/background check work is still pending.

## What it scans

- Recycle Bin
- User Temp (`%TEMP%`), system-wide `C:\Windows\Temp`, and Windows Update's `SoftwareDistribution\Download` cache (Some undeletable regardless and will be said so with a long log)
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

- `DiskCleanup.Core/` — cross-platform scanners, action executor, selection parsing (shared library)
- `DiskCleanup.Core.Windows/` — Windows-only scanners + `WindowsTrashProvider`
- `DiskCleanup.Core.Mac/` — `MacTrashProvider` (`~/.Trash`); no Mac-specific scanners yet
- `DiskCleanup/` — console app (checklist + numeric selection), Windows-only
- `DiskCleanup.Wpf/` — Windows desktop widget (checklist grid, risk filter, details pane, Clean Selected)
- `DiskCleanup.Avalonia/` — cross-platform GUI port; this is what ships as the macOS build (see [Roadmap](#roadmap))
- `DiskCleanup.Tests/` — xUnit tests for `Core`, `Core.Windows`, `Core.Mac`

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
This project multi-targets `net10.0-windows` and `net10.0`, so plain `dotnet run` fails
with "Your project targets multiple frameworks", and `--framework` on its own errors with
"Required argument missing" — it needs a value, not just the flag.

**macOS build (on a Mac):**
```bash
bash scripts/package-mac.sh
```
Produces `publish/mac/DiskCleanup_<version>_aarch64.dmg` (version comes from
`DiskCleanup.Avalonia.csproj`). CI runs the same script on every push to `main` — see
`.github/workflows/ci.yml`.

**Tests:**
```powershell
dotnet test
```

## Downloading a prebuilt build

### Windows

Grab the latest `.exe` from [Releases](https://github.com/D3lK1ch1/disk-checklist-clean/releases)
— a single self-contained file (~130MB), no .NET runtime install and no other files needed
alongside it. It's unsigned, so Windows SmartScreen will show a "Windows protected your PC"
warning on first run — that's expected for an unsigned indie exe, not a sign anything's
wrong. Click **More info → Run anyway** to proceed.

### macOS (Experimental)

> **The macOS build is unsigned and experimental.** This is a free, non-commercial project
> without an Apple Developer ID, so macOS cannot verify who made the app. It is built and
> checked in CI but has not yet been run on a physical Mac by the maintainer. Apple Silicon
> only (Intel Macs are not supported), macOS 12 or later. Use at your own discretion; see
> [LICENSE](LICENSE) for warranty terms.

Download `DiskCleanup_<version>_aarch64.dmg` from the
[Releases](https://github.com/D3lK1ch1/disk-checklist-clean/releases) page (macOS builds are
marked **Pre-release**).

**Check the download** (optional, recommended). This compares the file against the SHA-256
GitHub records for each release asset. Run it in the folder you downloaded to, with the
release's tag:
```bash
curl -s https://api.github.com/repos/D3lK1ch1/disk-checklist-clean/releases/tags/v0.0.2 | \
    jq -r '.assets[] | "\(.digest | ltrimstr("sha256:"))  \(.name)"' | \
    shasum -a 256 -c --ignore-missing
```
It should print `OK` next to the `.dmg`. `FAILED` means the file is corrupted or was changed
— don't open it.

**First launch** — macOS will block the app with *"Apple could not verify…"*. To allow it:

1. Open the `.dmg` and drag **Disk Cleanup** into your **Applications** folder.
2. Open the app once and dismiss the warning.
3. Go to **System Settings → Privacy & Security**, scroll down, and click **Open Anyway**
   next to the Disk Cleanup message.
4. Open the app again and confirm.

Or, from Terminal:
```bash
xattr -dr com.apple.quarantine "/Applications/Disk Cleanup.app"
```

**After updating to a new version:** repeat the **Open Anyway** step — every new download is
quarantined again.

**What's different on macOS:**
- Only the cross-platform scanners run: user temp (`$TMPDIR`), VS Code cache, dev package
  caches, `node_modules`/`target` build dirs, Docker, Downloads/personal folders, and
  `.claude`/`.codex` folders. There is no Trash scanner yet, and none of the Windows-only
  ones (Windows Update, AppData, installed apps, WSL).
- **User Temp is `$TMPDIR`**, where running Mac apps keep live files. It's SAFE-tier, so
  cleaning it deletes permanently. Quit other apps first if you tick it.
- REVIEW items move to `~/.Trash`, but Finder's **Put Back** won't work for them — drag
  them out of the Trash by hand to restore.
- If a REVIEW item fails to move to the Trash with a permission error, give Disk Cleanup
  **Full Disk Access** in **System Settings → Privacy & Security → Full Disk Access**.
  (Not yet confirmed whether this is needed.)

**Prefer not to bypass?** Building from source avoids the warning entirely, since locally
built apps aren't quarantined. Install the .NET 10 SDK, then run `bash scripts/package-mac.sh`
and open the `.app` in `publish/mac/`.

## Roadmap

- `--check` mode: silent scan + Windows toast notification when free space drops below 45GB, triggered via Task Scheduler (setup command printed, not auto-configured)
- Filter by category/size in the widget (risk-level filter already exists)
- Cross-platform support (Linux, Mac) via the Avalonia port in `DiskCleanup.Avalonia/`,
  alongside the existing WPF version. `DiskCleanup.Core` is already split off its
  Windows-only target framework, and the Avalonia project multi-targets
  `net10.0-windows`/`net10.0` with a runtime OS branch (`PlatformSetup.cs`) picking the
  right scanners/trash provider. The macOS build is packaged as an ad-hoc-signed `.dmg` in
  CI (see [macOS (Experimental)](#macos-experimental)). Still needed: confirmation from a
  real Mac that the app launches and `MacTrashProvider`/`~/.Trash` work as intended, a Trash
  scanner for Mac, Mac/Linux-specific scanners beyond the already-portable ones, and running
  the test suite on macOS (`DiskCleanup.Tests` is Windows-only today).

## License

[MIT](LICENSE)

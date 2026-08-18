using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DiskCleanup.Core;

public static class Scanners
{
    public static List<CheckItem> RecycleBin(List<string>? warnings = null)
    {
        var items = new List<CheckItem>();
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (sid == null) return items;

            long total = 0;
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                var recycleBinPath = System.IO.Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid);
                if (Directory.Exists(recycleBinPath))
                    total += GetDirectorySize(recycleBinPath);
            }
            items.Add(new CheckItem("Recycle Bin", total, "SAFE", Action: ActionKind.EmptyRecycleBin));
        }
        catch (Exception ex) { warnings?.Add($"Recycle Bin: could not read ({ex.Message})"); }
        return items;
    }

    // Portable: Path.GetTempPath() resolves to %TEMP% on Windows, /tmp (or
    // $TMPDIR) on Linux/Mac - no OS-specific logic needed here.
    public static List<CheckItem> TempFolders()
    {
        var items = new List<CheckItem>();

        var userTemp = System.IO.Path.GetTempPath();
        if (Directory.Exists(userTemp))
            items.Add(new CheckItem("User Temp folder", GetDirectorySize(userTemp), "SAFE", userTemp, Action: ActionKind.DeleteContents));

        return items;
    }

    // Windows-only: Windows Update's download cache. No Linux/Mac equivalent -
    // kept separate from TempFolders() so the portable half doesn't carry a
    // Windows-only concept.
    public static List<CheckItem> WindowsUpdateCache()
    {
        var items = new List<CheckItem>();

        var softwareDistribution = @"C:\Windows\SoftwareDistribution\Download";
        if (Directory.Exists(softwareDistribution))
            items.Add(new CheckItem("Windows Update cache (SoftwareDistribution\\Download)", GetDirectorySize(softwareDistribution), "SAFE", softwareDistribution, Action: ActionKind.DeleteContents));

        return items;
    }

    // Windows-only: C:\Windows\Temp is the system-wide temp folder (shared across
    // users/services), distinct from the per-user %TEMP% already covered by
    // TempFolders(). Files here may be locked by other users' processes or
    // services - delete-side failures surface via the existing warning collection,
    // same as WindowsUpdateCache().
    public static List<CheckItem> WindowsTempFolder(List<string>? warnings = null)
    {
        var items = new List<CheckItem>();

        var windowsTemp = @"C:\Windows\Temp";
        try
        {
            if (Directory.Exists(windowsTemp))
                items.Add(new CheckItem("Windows Temp (system-wide)", GetDirectorySize(windowsTemp), "SAFE", windowsTemp, Action: ActionKind.DeleteContents));
        }
        catch (Exception ex) { warnings?.Add($"Windows Temp (system-wide): could not read ({ex.Message})"); }

        return items;
    }

    public static List<CheckItem> VsCodeCache(List<string>? warnings = null)
    {
        var items = new List<CheckItem>();
        var tempRoot = System.IO.Path.GetTempPath();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(tempRoot, "*CachedExtensionVSIXs*"))
                items.Add(new CheckItem($"VS Code extension cache ({System.IO.Path.GetFileName(dir)})", GetDirectorySize(dir), "SAFE", dir, Action: ActionKind.DeleteFolder));
        }
        catch (Exception ex) { warnings?.Add($"VS Code extension cache: could not scan Temp folder ({ex.Message})"); }
        return items;
    }

    // Optional path overrides exist only so tests can point at a fake tree instead
    // of the real ~/.nuget/packages or pip cache - production callers pass neither.
    public static List<CheckItem> DevPackageCaches(List<string>? warnings = null, string? nugetPath = null, string? pipPath = null)
    {
        var items = new List<CheckItem>();

        var nuget = nugetPath ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        try
        {
            if (Directory.Exists(nuget))
                items.Add(new CheckItem("NuGet package cache", GetDirectorySize(nuget), "SAFE", nuget, Action: ActionKind.DeleteContents,
                    Reason: "Package download cache. Deleting it doesn't remove anything you've installed — the next build just re-downloads packages from NuGet instead of using this local cache, which is slower but not destructive."));
        }
        catch (Exception ex) { warnings?.Add($"NuGet package cache: could not read ({ex.Message})"); }

        var pip = pipPath ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pip", "Cache");
        try
        {
            if (Directory.Exists(pip))
                items.Add(new CheckItem("pip download cache", GetDirectorySize(pip), "SAFE", pip, Action: ActionKind.DeleteContents,
                    Reason: "Package download cache. Deleting it doesn't remove anything you've installed — the next pip install just re-downloads packages from PyPI instead of using this local cache, which is slower but not destructive."));
        }
        catch (Exception ex) { warnings?.Add($"pip download cache: could not read ({ex.Message})"); }

        return items;
    }

    public static List<CheckItem> Wsl(List<string>? warnings = null)
    {
        var items = new List<CheckItem>();
        try
        {
            foreach (var distro in GetWslDistros(warnings))
            {
                var basePath = $@"\\wsl.localhost\{distro}\home";
                if (!Directory.Exists(basePath)) continue;

                foreach (var userDir in Directory.EnumerateDirectories(basePath))
                {
                    var cache = System.IO.Path.Combine(userDir, ".cache");
                    if (Directory.Exists(cache))
                        items.Add(new CheckItem($"WSL ({distro}) ~/.cache", GetDirectorySize(cache), "SAFE", cache, Action: ActionKind.DeleteFolder,
                            Reason: "WSL app cache directory. Apps regenerate this automatically — deleting it doesn't break anything, it just means the next run may be slightly slower while caches are rebuilt."));

                    var npm = System.IO.Path.Combine(userDir, ".npm");
                    if (Directory.Exists(npm))
                        items.Add(new CheckItem($"WSL ({distro}) ~/.npm", GetDirectorySize(npm), "SAFE", npm, Action: ActionKind.DeleteFolder,
                            Reason: "npm's local package download cache. Deleting it doesn't remove or break any project — the next `npm install` anywhere just re-downloads packages from the registry instead of using this local cache, which is slower but not destructive."));

                    // pnpm-managed node_modules below are mostly symlinks into this
                    // store, so GetDirectorySize (which skips reparse points) reports
                    // them as near-zero - the real bytes live here instead, and
                    // nothing else in this scanner ever looks at this path.
                    var pnpmStore = System.IO.Path.Combine(userDir, ".local", "share", "pnpm", "store");
                    if (Directory.Exists(pnpmStore))
                        items.Add(new CheckItem($"WSL ({distro}) ~/.local/share/pnpm/store", GetDirectorySize(pnpmStore), "SAFE", pnpmStore,
                            Action: ActionKind.DeleteFolder,
                            Reason: "pnpm's shared package cache for this WSL distro, used by every pnpm project here. Deleting it doesn't remove or break any project - the next pnpm install anywhere just re-fetches/re-links packages from the registry instead of this local cache, which is slower but not destructive."));

                    var dirScan = FindBuildDirs(userDir);

                    foreach (var buildDir in dirScan.BuildDirs)
                    {
                        var rel = System.IO.Path.GetRelativePath(userDir, buildDir);
                        var dirName = System.IO.Path.GetFileName(buildDir);
                        var parentDir = System.IO.Path.GetDirectoryName(buildDir)!;
                        var label = $"WSL ({distro}) ~/{rel.Replace('\\', '/')}";
                        var size = GetDirectorySize(buildDir);

                        items.Add(ClassifyBuildDir(label, size, buildDir, parentDir, dirName, $"~/{rel.Replace('\\', '/')}"));
                    }

                    foreach (var excludedDir in dirScan.ExcludedAppDataDirs)
                    {
                        var rel = System.IO.Path.GetRelativePath(userDir, excludedDir).Replace('\\', '/');
                        items.Add(new CheckItem($"WSL ({distro}) ~/{rel}", GetDirectorySize(excludedDir), "INFO", excludedDir, Action: ActionKind.None,
                            Reason: "Bundled files for a remote dev-server (e.g. VS Code Remote-WSL). Deleting the *whole* folder is an official troubleshooting step - the editor reinstalls it automatically on reconnect. The risk is deleting only *part* of it (like one extension's node_modules) while the server may still be running, which is what this tool would otherwise offer piecemeal - so it's excluded here rather than partially cleaned. If you want a full reset, close all connected editor windows first, then delete this folder yourself."));
                    }

                    foreach (var claudeItem in ScanClaudeFolder(System.IO.Path.Combine(userDir, ".claude"), warnings))
                        items.Add(claudeItem with { Label = $"WSL ({distro}) {claudeItem.Label}" });
                }
            }
        }
        catch (Exception ex) { warnings?.Add($"WSL: scan failed ({ex.Message})"); }
        return items;
    }

    // Native-Windows counterpart to Wsl()'s build-dir discovery. Only walks
    // known user-content roots (Downloads/Documents/Desktop) - never the
    // whole C: drive - for the same reason DownloadsTopFolders doesn't.
    public static List<CheckItem> NativeBuildDirs(List<string>? warnings = null)
    {
        var items = new List<CheckItem>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            (System.IO.Path.Combine(userProfile, "Downloads"), "Downloads"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents"),
            (Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Desktop"),
        };

        foreach (var (root, rootLabel) in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var buildDir in FindNativeBuildDirs(root))
                {
                    var rel = System.IO.Path.GetRelativePath(root, buildDir);
                    var dirName = System.IO.Path.GetFileName(buildDir);
                    var parentDir = System.IO.Path.GetDirectoryName(buildDir)!;
                    var label = $"{rootLabel}\\{rel}";
                    var size = GetDirectorySize(buildDir);

                    items.Add(ClassifyBuildDir(label, size, buildDir, parentDir, dirName, label));
                }
            }
            catch (Exception ex) { warnings?.Add($"{rootLabel}: could not scan for build dirs ({ex.Message})"); }
        }
        return items;
    }

    public static List<CheckItem> Docker(List<string>? warnings = null)
    {
        var items = new List<CheckItem>();
        try
        {
            var psi = new ProcessStartInfo("docker", "system df")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc == null) return items;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            if (proc.ExitCode != 0)
            {
                warnings?.Add("Docker: 'docker system df' failed — is Docker Desktop running?");
                return items;
            }

            // Skip the header row, parse the rest.
            var lines = output.Split('\n').Skip(1);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                // Columns are whitespace-padded: TYPE TOTAL ACTIVE SIZE RECLAIMABLE
                var cols = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length < 2) continue;

                var type = cols[0];
                var reclaimable = string.Join(' ', cols.Skip(cols.Length - 2)).Trim();
                // Docker always renders zero as exactly "0B" (optionally
                // followed by "(0%)") - skip those, there's nothing to reclaim.
                if (reclaimable.StartsWith("0B", StringComparison.OrdinalIgnoreCase)) continue;

                var command = type switch
                {
                    "Images" => "docker image prune -a",
                    "Containers" => "docker container prune",
                    "Local" or "Build" => "docker builder prune",
                    "Volumes" => "docker volume prune",
                    _ => "docker system prune"
                };
                items.Add(new CheckItem($"Docker {type} (reclaimable)", 0, "REVIEW", SizeOverride: reclaimable, Action: ActionKind.SuggestCommand, CommandSuggestion: command));
            }
        }
        catch (Exception ex) { warnings?.Add($"Docker: could not query ({ex.Message})"); }
        return items;
    }

    // Docker()'s "reclaimable" rows only see Docker's own logical accounting
    // (docker system df). The physical .vhdx file backing Docker Desktop's
    // WSL2 disk grows but never auto-shrinks on Windows, even after pruning -
    // this scanner looks at the file directly instead.
    public static List<CheckItem> DockerVhdxBloat(long thresholdBytes = 20L * 1024 * 1024 * 1024, List<string>? warnings = null)
    {
        var items = new List<CheckItem>();
        var dockerWslDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Docker", "wsl");
        if (!Directory.Exists(dockerWslDir)) return items;

        try
        {
            // Recursive, not a hardcoded subfolder name - the exact layout
            // varies by Docker Desktop version (confirmed "main" on this
            // machine, other sources have guessed "disk"/"data").
            foreach (var path in Directory.EnumerateFiles(dockerWslDir, "*.vhdx", SearchOption.AllDirectories))
            {
                try
                {
                    var size = new FileInfo(path).Length;
                    if (size < thresholdBytes) continue;

                    items.Add(new CheckItem(
                        $"Docker WSL2 disk image ({System.IO.Path.GetFileName(path)})",
                        size, "REVIEW", path,
                        Action: ActionKind.SuggestCommand,
                        CommandSuggestion: $"wsl --shutdown, then run diskpart and enter: select vdisk file=\"{path}\" / compact vdisk / exit",
                        Reason: "Docker Desktop's virtual disk grows but never automatically shrinks on Windows, even after `docker system prune`. This size reflects space allocated over time, not necessarily currently-active data - compacting is safe and doesn't delete anything Docker is using."));
                }
                catch { }
            }
        }
        catch (Exception ex) { warnings?.Add($"Docker WSL2 disk scan: could not enumerate .vhdx files ({ex.Message})"); }
        return items;
    }

    // Outcome of an optional per-entry local-check (see SystemRootEntry.LocalCheck)
    // that cross-references whether the hardware/software a Tier A item depends on
    // is still on this machine - same three-outcome honesty as StalePackages's
    // lookupSucceeded pattern: a failed check must say so, never silently read as
    // "absent" (which would wrongly strengthen a safe-to-delete claim).
    public enum LocalCheckOutcome { Present, Absent, Unknown }

    // A single allowlist entry for SystemRootClutter - one exact name matched
    // directly under C:\ root, plus everything needed to classify it once found.
    // IsFile distinguishes DumpStack.log-style single files from folder leftovers
    // like HP eSupport - the two need different existence checks and size calls.
    // LocalCheck/CheckSubject are only set for entries whose safety genuinely
    // depends on this machine's hardware/software (Unit 2) - the SAFE/INFO entries
    // don't have that dependency, so they're left null and skip the check step.
    record SystemRootEntry(string Name, bool IsFile, string Risk, ActionKind Action, string Reason,
        string? CommandSuggestion = null, Func<LocalCheckOutcome>? LocalCheck = null, string? CheckSubject = null);

    // Curated exact-name allowlist, tiered by actual risk (not by "looks like
    // C:\ root clutter") - see SPEC.md's "Planned: system-root clutter" section
    // for the research behind each entry. intelFPGA is deliberately absent: it's
    // the live Quartus Prime installation (C:\intelFPGA_lite), not leftover
    // debris, same category as the runtime installers this scanner never touches.
    static readonly SystemRootEntry[] SystemRootAllowlist =
    {
        new("DumpStack.log", true, "SAFE", ActionKind.DeleteFile,
            "Bug-check stack-trace log written during crash-dump generation - not the crash dump itself (that lives in separate .dmp/minidump files). No hardware or software dependency; confirmed safe to delete."),
        new("DumpStack.log.tmp", true, "SAFE", ActionKind.DeleteFile,
            "Temporary working copy created during the same crash-dump logging process as DumpStack.log. Same reasoning - safe to delete."),

        new("HP eSupport", false, "REVIEW", ActionKind.MoveFolderToRecycleBin,
            "HP driver/diagnostic installer leftover. Community guidance on this one is genuinely mixed - keep it if you still use HP support software or might need to reinstall HP drivers without internet access later; otherwise it's safe to remove.",
            LocalCheck: CheckHpHardwarePresent, CheckSubject: "HP-manufactured hardware"),
        new("RyzenPPKG Driver", false, "REVIEW", ActionKind.MoveFolderToRecycleBin,
            "AMD Ryzen PPKG tuning-app installer leftover. Only relevant if this machine has an AMD Ryzen CPU - if it doesn't, this is safe to remove.",
            LocalCheck: CheckAmdCpuPresent, CheckSubject: "an AMD CPU"),
        new("WCH.CN", false, "REVIEW", ActionKind.MoveFolderToRecycleBin,
            "CH340/CH341 USB-to-serial driver installer leftover, commonly used by Arduino clones and ESP32-style dev boards. Only relevant if you use that kind of hardware - if not, safe to remove.",
            LocalCheck: CheckWchDriverRegistered, CheckSubject: "a CH340/CH341 driver"),

        new("inetpub", false, "INFO", ActionKind.None,
            "Possible active IIS web root. Deleting could take down a running website - verify no site is currently being served from here (check the IIS/W3SVC service) before touching it yourself. No delete action offered from inside this tool."),
        new("flexlm", false, "INFO", ActionKind.None,
            "Possible active license server directory. Deleting could break license validation for software depending on it, possibly on other machines - verify nothing is connected before touching it yourself. No delete action offered from inside this tool."),
        new("vfcompat.dll", true, "INFO", ActionKind.SuggestCommand,
            "Live Visual Studio Application Verifier/debugger component, misplaced at C:\\ root by a known, persistent Visual Studio installer bug - it belongs in C:\\Windows\\SysWOW64. Deleting it can break Application Verifier and corrupt Visual Studio's debugging pipeline. The correct fix is to move it, not delete it.",
            "Move-Item \"C:\\vfcompat.dll\" \"C:\\Windows\\SysWOW64\\vfcompat.dll\""),
        new("appverifUI.dll", true, "INFO", ActionKind.SuggestCommand,
            "Live Visual Studio Application Verifier/debugger component, misplaced at C:\\ root by the same installer bug as vfcompat.dll. Move it instead of deleting it.",
            "Move-Item \"C:\\appverifUI.dll\" \"C:\\Windows\\SysWOW64\\appverifUI.dll\""),
    };

    // rootOverride exists only so tests can point at a fake tree instead of the
    // real C:\ - production callers pass neither.
    public static List<CheckItem> SystemRootClutter(string? rootOverride = null, List<string>? warnings = null)
    {
        var items = new List<CheckItem>();
        var root = rootOverride ?? @"C:\";

        foreach (var entry in SystemRootAllowlist)
        {
            try
            {
                var path = System.IO.Path.Combine(root, entry.Name);
                var exists = entry.IsFile ? File.Exists(path) : Directory.Exists(path);
                if (!exists) continue;

                var size = entry.IsFile ? SafeFileSize(path) : GetDirectorySize(path);
                var reason = entry.LocalCheck != null && entry.CheckSubject != null
                    ? AppendLocalCheckNote(entry.Reason, entry.CheckSubject, RunLocalCheck(entry.LocalCheck))
                    : entry.Reason;

                items.Add(new CheckItem(entry.Name, size, entry.Risk, path,
                    Action: entry.Action, CommandSuggestion: entry.CommandSuggestion, Reason: reason));
            }
            catch (Exception ex) { warnings?.Add($"{entry.Name}: could not check ({ex.Message})"); }
        }

        return items;
    }

    // Wraps a LocalCheck delegate so a check's own failure can never propagate
    // as an "Absent" (falsely strengthens a delete recommendation) - only the
    // check itself decides Present/Absent, anything else collapses to Unknown.
    static LocalCheckOutcome RunLocalCheck(Func<LocalCheckOutcome> check)
    {
        try { return check(); }
        catch { return LocalCheckOutcome.Unknown; }
    }

    // Pure formatting, kept separate from the registry/process-based checks
    // above so the three-outcome wording can be unit tested without depending
    // on this machine's actual CPU vendor, BIOS manufacturer, or drivers.
    public static string AppendLocalCheckNote(string baseReason, string subject, LocalCheckOutcome outcome) => outcome switch
    {
        LocalCheckOutcome.Absent => $"{baseReason} Local check: {subject} not detected on this machine - likely safe to remove.",
        LocalCheckOutcome.Present => $"{baseReason} Local check: {subject} detected on this machine - may still be in use, verify before deleting.",
        _ => $"{baseReason} Local check: could not confirm {subject} - verify manually before deleting.",
    };

    // Registry read rather than a WMI/CIM query - same information
    // (CentralProcessor vendor string), no extra process spawn or WMI service
    // dependency, consistent with this scanner's other reads.
    static LocalCheckOutcome CheckAmdCpuPresent()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var vendor = key?.GetValue("VendorIdentifier") as string;
            if (string.IsNullOrEmpty(vendor)) return LocalCheckOutcome.Unknown;
            return vendor.Equals("AuthenticAMD", StringComparison.OrdinalIgnoreCase) ? LocalCheckOutcome.Present : LocalCheckOutcome.Absent;
        }
        catch { return LocalCheckOutcome.Unknown; }
    }

    // BIOS-reported system manufacturer - close enough to "is this HP hardware"
    // without needing a WMI query; OEM BIOS strings are typically "HP" or
    // "Hewlett-Packard".
    static LocalCheckOutcome CheckHpHardwarePresent()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var manufacturer = key?.GetValue("SystemManufacturer") as string;
            if (string.IsNullOrEmpty(manufacturer)) return LocalCheckOutcome.Unknown;
            return manufacturer.Contains("HP", StringComparison.OrdinalIgnoreCase) ||
                   manufacturer.Contains("Hewlett-Packard", StringComparison.OrdinalIgnoreCase)
                ? LocalCheckOutcome.Present : LocalCheckOutcome.Absent;
        }
        catch { return LocalCheckOutcome.Unknown; }
    }

    // No registry-only equivalent for "is a CH340/CH341 driver currently
    // registered" - shells out to PowerShell's Get-PnpDevice, same
    // ProcessStartInfo shell-out pattern GetInstalledPackageFamilyNames already
    // establishes below for "ask Windows for state" needs.
    static LocalCheckOutcome CheckWchDriverRegistered()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell",
                "-NoProfile -Command \"(Get-PnpDevice | Where-Object { $_.FriendlyName -like '*CH340*' -or $_.FriendlyName -like '*CH341*' }).Count\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc == null) return LocalCheckOutcome.Unknown;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000); // PowerShell cold-spawn - same budget as GetInstalledPackageFamilyNames
            if (proc.ExitCode != 0) return LocalCheckOutcome.Unknown;

            return int.TryParse(output.Trim(), out var count)
                ? (count > 0 ? LocalCheckOutcome.Present : LocalCheckOutcome.Absent)
                : LocalCheckOutcome.Unknown;
        }
        catch { return LocalCheckOutcome.Unknown; }
    }

    public static List<CheckItem> DownloadsTopFolders(int topN = 5, List<string>? warnings = null)
    {
        var items = new List<CheckItem>();
        var downloads = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(downloads)) return items;

        // Never offer to delete the disk-cleanup tool's own folder, even if it
        // ranks in the top N by size (e.g. after a few builds fill bin/obj).
        var selfRoot = GetSelfRootUnder(downloads);

        try
        {
            var entries = Directory.EnumerateFileSystemEntries(downloads)
                .Where(p => selfRoot == null || !string.Equals(p, selfRoot, StringComparison.OrdinalIgnoreCase))
                .Select(p => (Path: p, Size: Directory.Exists(p) ? GetDirectorySize(p) : SafeFileSize(p)))
                .OrderByDescending(x => x.Size)
                .Take(topN);

            foreach (var (path, size) in entries)
                items.Add(new CheckItem($"Downloads\\{System.IO.Path.GetFileName(path)}", size, "REVIEW", path, Action: ActionKind.MoveFolderToRecycleBin));
        }
        catch (Exception ex) { warnings?.Add($"Downloads: could not scan ({ex.Message})"); }
        return items;
    }

    /// <summary>
    /// Walks up from the running executable's location to find the ancestor
    /// directory that sits directly inside <paramref name="downloads"/>, if any.
    /// Returns null if this tool isn't running from somewhere under Downloads.
    /// </summary>
    static string? GetSelfRootUnder(string downloads)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir?.Parent != null)
        {
            if (string.Equals(dir.Parent.FullName.TrimEnd('\\'), downloads.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    // Shared-runtime/framework packages (e.g. the VCLibs redistributable many
    // Store apps depend on) are never flagged regardless of staleness - they
    // aren't a "your app's data", they're plumbing other apps rely on.
    static readonly string[] SharedRuntimePackagePrefixes =
    {
        "Microsoft.VCLibs",
        "Microsoft.NET.Native",
        "Microsoft.UI.Xaml",
        "Microsoft.WindowsAppRuntime",
        "Microsoft.Services.Store.Engagement",
    };

    public static bool IsSharedRuntimePackage(string folderName) =>
        SharedRuntimePackagePrefixes.Any(p => folderName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static List<CheckItem> StalePackages(int monthsThreshold = 6, List<string>? warnings = null)
    {
        var items = new List<CheckItem>();
        var packagesDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(packagesDir)) return items;

        var cutoff = DateTime.Now.AddMonths(-monthsThreshold);
        var installedNames = GetInstalledPackageFamilyNames();
        var lookupSucceeded = installedNames.Count > 0;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(packagesDir))
            {
                try
                {
                    var folderName = System.IO.Path.GetFileName(dir);
                    if (IsSharedRuntimePackage(folderName)) continue;

                    var lastWrite = Directory.GetLastWriteTime(dir);
                    if (lastWrite >= cutoff) continue;

                    var size = GetDirectorySize(dir);
                    if (size == 0) continue;

                    // installedNames is empty only when the lookup itself
                    // failed (PowerShell missing/errored) - treat that as
                    // "unknown", not "orphaned", so a lookup failure can't
                    // make every package falsely look safe to delete.
                    string reason;
                    if (!lookupSucceeded)
                        reason = $"Couldn't confirm whether this app is still installed (the installed-apps lookup failed). Folder hasn't been modified since {lastWrite:yyyy-MM-dd}. Treat with extra caution.";
                    else if (!installedNames.Contains(folderName))
                        reason = $"No package is currently registered under this exact ID (checked via Get-AppxPackage). If you still use this app, it may now run under a different/updated package ID - either way, this specific folder is leftover data. Safe to delete.";
                    else
                        reason = $"An app with this package ID is still installed. This folder holds that app's saved settings/data - deleting it may reset its settings or sign you out next time you open it. Folder itself hasn't been modified since {lastWrite:yyyy-MM-dd}.";

                    items.Add(new CheckItem(
                        $"AppData\\Local\\Packages\\{folderName}",
                        size, "REVIEW", dir, Action: ActionKind.MoveFolderToRecycleBin, Reason: reason));
                }
                catch { }
            }
        }
        catch (Exception ex) { warnings?.Add($"AppData\\Local\\Packages: could not scan ({ex.Message})"); }
        return items;
    }

    // Shells out to PowerShell rather than the native PackageManager WinRT API
    // - this project targets plain net10.0-windows (no Windows SDK contract
    // suffix), and Docker()/GetWslDistros() already establish the
    // ProcessStartInfo-shell-out pattern for "ask Windows for state" needs.
    // Returns an empty set on any failure - callers must treat that as
    // "couldn't determine install state", not "nothing is installed".
    static HashSet<string> GetInstalledPackageFamilyNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var psi = new ProcessStartInfo("powershell",
                "-NoProfile -Command \"Get-AppxPackage | Select-Object -ExpandProperty PackageFamilyName\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc == null) return names;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000); // PowerShell cold-spawn is slower than docker/wsl's 5000ms budget
            if (proc.ExitCode != 0) return names;

            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed)) names.Add(trimmed);
            }
        }
        catch { }
        return names;
    }

    // TLD-style first segments that mark a reverse-domain folder name
    // (com.vendor.app) - the identifier convention Tauri/Electron-style apps
    // use for their per-user data under AppData\Roaming. Curated rather than
    // "anything with dots" so vendor folders like "Microsoft" or versioned
    // names like "Python 3.12" can never match.
    static readonly string[] ReverseDomainTldPrefixes =
    {
        "com", "org", "net", "io", "dev", "app", "co", "me", "xyz",
    };

    public static bool IsReverseDomainName(string folderName)
    {
        var parts = folderName.Split('.');
        if (parts.Length < 3) return false;
        if (parts.Any(p => p.Length == 0)) return false;
        return ReverseDomainTldPrefixes.Contains(parts[0], StringComparer.OrdinalIgnoreCase);
    }

    // Tier 3 (name heuristic only): a reverse-domain folder name says which app
    // wrote it, not whether that app is dead - and unlike Local's caches,
    // Roaming holds real app state (settings, local databases). So every hit is
    // REVIEW + Recycle Bin, never SAFE and never a permanent delete. There's no
    // install cross-check here: bundle IDs like com.vendor.app don't map
    // reliably onto registry DisplayNames, and StalePackages's Get-AppxPackage
    // trick only covers MSIX/Store apps - the reason text says so instead of
    // pretending otherwise.
    //
    // rootOverride exists only so tests can point at a fake tree instead of the
    // real %APPDATA% - production callers pass neither.
    public static List<CheckItem> RoamingAppData(string? rootOverride = null, List<string>? warnings = null)
    {
        var items = new List<CheckItem>();
        var root = rootOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!Directory.Exists(root)) return items;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = System.IO.Path.GetFileName(dir);
                if (!IsReverseDomainName(name)) continue;

                var size = GetDirectorySize(dir);
                if (size == 0) continue;

                DateTime lastWrite;
                try { lastWrite = Directory.GetLastWriteTime(dir); }
                catch { continue; }

                items.Add(new CheckItem($"AppData\\Roaming\\{name}", size, "REVIEW", dir,
                    Action: ActionKind.MoveFolderToRecycleBin,
                    Reason: $"Reverse-domain folder name - the identifier pattern Tauri/Electron-style desktop apps use for per-user data. Last modified {lastWrite:yyyy-MM-dd}. May hold real app state (settings, local databases), not just regenerable cache - deleting resets that app if it's still installed. The name pattern is the only signal (no install check exists for this ID format), so confirm the app is dead/yours before removing. Recoverable from the Recycle Bin."));
            }
        }
        catch (Exception ex) { warnings?.Add($"AppData\\Roaming: could not scan ({ex.Message})"); }
        return items;
    }

    public static List<CheckItem> InstalledAppsBySize(int topN = 10, List<string>? warnings = null)
    {
        var apps = new List<(string Name, long SizeBytes)>();
        var keys = new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var (hive, path) in keys)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key == null) continue;

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var subKey = key.OpenSubKey(subKeyName);
                        var name = subKey?.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (subKey?.GetValue("EstimatedSize") is int sizeKb && sizeKb > 0)
                            apps.Add((name, (long)sizeKb * 1024));
                    }
                    catch { }
                }
            }
            catch (Exception ex) { warnings?.Add($"Installed apps: could not read {hive.Name}\\{path} ({ex.Message})"); }
        }

        return apps.OrderByDescending(a => a.SizeBytes)
            .Take(topN)
            .Select(a => new CheckItem($"Installed: {a.Name}", a.SizeBytes, "INFO"))
            .ToList();
    }

    public static List<CheckItem> PersonalFolders(int topN = 5, List<string>? warnings = null)
    {
        var items = new List<CheckItem>();
        var folders = new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents"),
            (Environment.GetFolderPath(Environment.SpecialFolder.Desktop),     "Desktop"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),  "Pictures"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),     "Music"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),    "Videos"),
        };

        foreach (var (folderPath, folderName) in folders)
        {
            if (!Directory.Exists(folderPath)) continue;
            try
            {
                var entries = Directory.EnumerateFileSystemEntries(folderPath)
                    .Select(p => (Path: p, Size: Directory.Exists(p) ? GetDirectorySize(p) : SafeFileSize(p)))
                    .OrderByDescending(x => x.Size)
                    .Take(topN);

                foreach (var (path, size) in entries)
                    items.Add(new CheckItem(
                        $"{folderName}\\{System.IO.Path.GetFileName(path)}",
                        size, "REVIEW", path,
                        Action: ActionKind.MoveFolderToRecycleBin,
                        Reason: $"One of your largest items in {folderName}. Moving to Recycle Bin is recoverable."));
            }
            catch (Exception ex) { warnings?.Add($"{folderName}: could not scan ({ex.Message})"); }
        }
        return items;
    }

    public static List<CheckItem> AiFolders(List<string>? warnings = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var items = new List<CheckItem>();
        items.AddRange(ScanClaudeFolder(System.IO.Path.Combine(home, ".claude"), warnings));
        items.AddRange(ScanCodexFolder(System.IO.Path.Combine(home, ".codex"), warnings));
        return items;
    }

    // No date filter - shows every session file regardless of age, since a
    // single one-and-done session can be just as "done" the day it's created
    // as it is months later. The only exclusion is the session currently in
    // use (cross-checked against sessions/*.json's live sessionId), so this
    // never offers to recycle-bin a transcript still being written to.
    //
    // Never offers the .claude root itself - it also holds .credentials.json,
    // settings.json, and CLAUDE.md/MEMORY.md (persistent cross-session memory),
    // so a single MoveFolderToRecycleBin on the root would take all of that with
    // it. Only these verified-safe, disposable subpaths are scanned. sessions/
    // and ide/ are deliberately excluded - they're live PID-keyed process state
    // for *running* instances, not history.
    public static List<CheckItem> ScanClaudeFolder(string root, List<string>? warnings = null)
    {
        var items = new List<CheckItem>();
        if (!Directory.Exists(root)) return items;

        var claudeCacheReasons = new Dictionary<string, string>
        {
            ["shell-snapshots"] = "Per-session shell-state snapshot scripts Claude Code generates automatically. Regenerated as needed - safe to delete, doesn't affect any other app.",
            ["paste-cache"] = "Clipboard paste cache Claude Code keeps temporarily. Regenerated as needed - safe to delete, doesn't affect any other app.",
            ["debug"] = "Debug logs from Claude Code. Safe to clear - doesn't affect any other app.",
            ["file-history"] = "Per-file undo/edit history for files you've edited in past Claude Code sessions. Deleting loses the ability to diff/undo those specific past edits, but doesn't affect any installed app or the files themselves.",
        };
        foreach (var (name, kind) in new[] { ("shell-snapshots", "cache"), ("paste-cache", "cache"), ("debug", "cache"), ("file-history", "edit history") })
        {
            var dir = System.IO.Path.Combine(root, name);
            if (!Directory.Exists(dir)) continue;
            var size = GetDirectorySize(dir);
            if (size > 0)
                items.Add(new CheckItem($".claude\\{name} ({kind})", size, "REVIEW", dir,
                    Action: ActionKind.MoveFolderToRecycleBin, Reason: AppendUncRecycleBinNote(claudeCacheReasons[name], dir)));
        }

        var projectsDir = System.IO.Path.Combine(root, "projects");
        if (Directory.Exists(projectsDir))
        {
            var activeSessionIds = GetActiveClaudeSessionIds(root);
            try
            {
                foreach (var projectDir in Directory.EnumerateDirectories(projectsDir).OrderBy(System.IO.Path.GetFileName))
                {
                    var projectName = System.IO.Path.GetFileName(projectDir);
                    List<string> jsonlFiles;
                    try { jsonlFiles = Directory.EnumerateFiles(projectDir, "*.jsonl").OrderBy(System.IO.Path.GetFileName).ToList(); }
                    catch { continue; }

                    // Session-named subdirectories (subagents/, tool-results/) that
                    // Claude Code creates next to a session's .jsonl - "memory/" is
                    // deliberately excluded, it's project-level persistent memory, not
                    // session-scoped. Matched to their sibling .jsonl below (cascade
                    // delete); whatever's left unmatched after that loop is an orphan
                    // whose conversation was already deleted.
                    var sessionDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        foreach (var dir in Directory.EnumerateDirectories(projectDir))
                        {
                            var dirName = System.IO.Path.GetFileName(dir);
                            if (string.Equals(dirName, "memory", StringComparison.OrdinalIgnoreCase)) continue;
                            sessionDirs[dirName] = dir;
                        }
                    }
                    catch { }

                    foreach (var jsonl in jsonlFiles)
                    {
                        // The transcript filename IS the sessionId (verified:
                        // projects/*/<sessionId>.jsonl). Skip it if a live
                        // sessions/*.json PID file claims that same ID - that
                        // session is still open, regardless of how old it is.
                        var sessionId = System.IO.Path.GetFileNameWithoutExtension(jsonl);
                        if (activeSessionIds.Contains(sessionId)) continue;

                        DateTime lastWrite;
                        try { lastWrite = File.GetLastWriteTime(jsonl); }
                        catch { continue; }

                        var size = SafeFileSize(jsonl);
                        if (size == 0) continue;

                        var (msgCount, excerpt) = AnalyzeClaudeSession(jsonl);
                        var reason = DescribeSessionFile("Claude Code", lastWrite, msgCount, excerpt);

                        // Cascade: a sibling <sessionId>/ folder (subagents/,
                        // tool-results/) is removed in the same action as its
                        // conversation - consumed here so the orphan pass below
                        // doesn't also list it.
                        string? secondaryPath = null;
                        if (sessionDirs.Remove(sessionId, out var sessionDir))
                        {
                            secondaryPath = sessionDir;
                            size += GetDirectorySize(sessionDir);
                            reason += " Includes this session's subagent/tool-result data.";
                        }

                        items.Add(new CheckItem(
                            $".claude\\projects\\{projectName}\\{System.IO.Path.GetFileName(jsonl)}",
                            size, "REVIEW", jsonl, Action: ActionKind.MoveFileToRecycleBin, Reason: AppendUncRecycleBinNote(reason, jsonl),
                            SecondaryPath: secondaryPath));
                    }

                    // Orphans: a session folder with no matching .jsonl left - the
                    // conversation itself was already deleted (by a prior cleanup,
                    // manually, etc.), so this is leftover subagent/tool-result data
                    // with no transcript left to reference. Still skips active
                    // sessions, same as the jsonl loop above.
                    foreach (var (sessionId, sessionDir) in sessionDirs.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        if (activeSessionIds.Contains(sessionId)) continue;

                        var size = GetDirectorySize(sessionDir);
                        if (size == 0) continue;

                        items.Add(new CheckItem(
                            $".claude\\projects\\{projectName}\\{sessionId} (orphaned subagent data)",
                            size, "SAFE", sessionDir, Action: ActionKind.MoveFolderToRecycleBin,
                            Reason: AppendUncRecycleBinNote(
                                "Subagent/tool-result data left behind after this session's conversation transcript was already deleted. No conversation exists to reference anymore - safe to remove.",
                                sessionDir)));
                    }
                }
            }
            catch (Exception ex) { warnings?.Add($".claude/projects: could not scan ({ex.Message})"); }
        }

        return items;
    }

    // Reads the live PID-keyed session files (e.g. sessions/12345.json) and
    // returns the set of sessionIds they claim - these are interactive Claude
    // Code processes that may currently be open, regardless of process status.
    static HashSet<string> GetActiveClaudeSessionIds(string claudeRoot)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sessionsDir = System.IO.Path.Combine(claudeRoot, "sessions");
        if (!Directory.Exists(sessionsDir)) return ids;

        try
        {
            foreach (var file in Directory.EnumerateFiles(sessionsDir, "*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (doc.RootElement.TryGetProperty("sessionId", out var idProp))
                    {
                        var id = idProp.GetString();
                        if (!string.IsNullOrEmpty(id)) ids.Add(id);
                    }
                }
                catch { }
            }
        }
        catch { }
        return ids;
    }

    // Codex has no equivalent live-PID-to-session mapping (unlike .claude's
    // sessions/*.json), so "currently active" can only be approximated here:
    // anything modified within this window is treated as possibly still being
    // written to and skipped. Less precise than the Claude check above - a
    // known asymmetry, not an oversight.
    const int CodexActiveGraceMinutes = 30;

    // No date filter beyond the active-session grace window above - shows
    // every session file regardless of age, same reasoning as ScanClaudeFolder.
    //
    // Never offers the .codex root itself - it also holds auth.json,
    // config.toml, and live SQLite runtime state. .sandbox/.sandbox-bin/
    // .sandbox-secrets are deliberately excluded - they hold live executables,
    // ACL state, and a sandbox_users.json, not disposable history. memories/,
    // rules/, skills/ are persistent/user-authored content, also excluded.
    public static List<CheckItem> ScanCodexFolder(string root, List<string>? warnings = null)
    {
        var items = new List<CheckItem>();
        if (!Directory.Exists(root)) return items;

        var codexCacheReasons = new Dictionary<string, string>
        {
            ["cache"] = "Codex CLI's own app-server/tool metadata cache. Regenerated as needed - safe to delete, doesn't affect any other app.",
            ["log"] = "Codex CLI's own log files. Safe to clear - doesn't affect any other app.",
            ["tmp"] = "Codex CLI's own temporary files. Regenerated as needed - safe to delete, doesn't affect any other app.",
            [".tmp"] = "Codex CLI's own temporary plugin-sync files. Regenerated as needed - safe to delete, doesn't affect any other app.",
        };
        foreach (var name in new[] { "cache", "log", "tmp", ".tmp" })
        {
            var dir = System.IO.Path.Combine(root, name);
            if (!Directory.Exists(dir)) continue;
            var size = GetDirectorySize(dir);
            if (size > 0)
                items.Add(new CheckItem($".codex\\{name} (cache)", size, "REVIEW", dir,
                    Action: ActionKind.MoveFolderToRecycleBin, Reason: AppendUncRecycleBinNote(codexCacheReasons[name], dir)));
        }

        var sessionsDir = System.IO.Path.Combine(root, "sessions");
        if (Directory.Exists(sessionsDir))
        {
            var activeGraceCutoff = DateTime.Now.AddMinutes(-CodexActiveGraceMinutes);
            try
            {
                // Nested by year/month/day, so this must recurse.
                foreach (var jsonl in Directory.EnumerateFiles(sessionsDir, "*.jsonl", SearchOption.AllDirectories).OrderBy(p => p))
                {
                    DateTime lastWrite;
                    try { lastWrite = File.GetLastWriteTime(jsonl); }
                    catch { continue; }
                    if (lastWrite > activeGraceCutoff) continue;

                    var size = SafeFileSize(jsonl);
                    if (size == 0) continue;

                    var (msgCount, excerpt) = AnalyzeCodexSession(jsonl);
                    var reason = DescribeSessionFile("Codex", lastWrite, msgCount, excerpt);
                    var rel = System.IO.Path.GetRelativePath(sessionsDir, jsonl).Replace('\\', '/');

                    items.Add(new CheckItem(
                        $".codex\\sessions\\{rel}",
                        size, "REVIEW", jsonl, Action: ActionKind.MoveFileToRecycleBin, Reason: AppendUncRecycleBinNote(reason, jsonl)));
                }
            }
            catch (Exception ex) { warnings?.Add($".codex/sessions: could not scan ({ex.Message})"); }
        }

        return items;
    }

    static string DescribeSessionFile(string toolName, DateTime lastWrite, int messageCount, string? excerpt)
    {
        var ageDays = (int)(DateTime.Now - lastWrite).TotalDays;
        var summary = excerpt != null
            ? $"{ageDays}d old, {messageCount} msgs - first message: \"{excerpt}\""
            : $"{ageDays}d old, {messageCount} msgs";
        return $"{summary}. A saved {toolName} conversation transcript - deleting it removes your ability to look back at this conversation, but doesn't affect {toolName}'s ability to run or any other app.";
    }

    // Counts user-turn messages and pulls the first one's text as a cursory
    // excerpt, so the user can judge relevance without opening the file.
    // Claude transcript lines look like:
    // {"type":"user","message":{"role":"user","content":"<text>"}}
    static (int MessageCount, string? Excerpt) AnalyzeClaudeSession(string path)
    {
        int count = 0;
        string? excerpt = null;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (!line.Contains("\"type\":\"user\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var el = doc.RootElement;
                    if (!el.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "user") continue;
                    if (!el.TryGetProperty("message", out var message)) continue;
                    count++;

                    if (excerpt == null && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    {
                        var text = content.GetString();
                        if (!string.IsNullOrWhiteSpace(text) && !text.TrimStart().StartsWith('<'))
                            excerpt = TruncateExcerpt(text);
                    }
                }
                catch { }
            }
        }
        catch { }
        return (count, excerpt);
    }

    // Codex rollout lines look like:
    // {"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<text>"}]}}
    // Many "role":"user" entries are tool-injected context (e.g.
    // <environment_context>...</environment_context>), not anything the human
    // typed - those are skipped so the excerpt is an actual human message.
    static (int MessageCount, string? Excerpt) AnalyzeCodexSession(string path)
    {
        int count = 0;
        string? excerpt = null;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (!line.Contains("\"role\":\"user\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (!doc.RootElement.TryGetProperty("payload", out var payload)) continue;
                    if (!payload.TryGetProperty("role", out var roleProp) || roleProp.GetString() != "user") continue;
                    count++;

                    if (excerpt != null) continue;
                    if (!payload.TryGetProperty("content", out var contentArr) || contentArr.ValueKind != JsonValueKind.Array) continue;

                    foreach (var block in contentArr.EnumerateArray())
                    {
                        if (!block.TryGetProperty("text", out var textProp)) continue;
                        var text = textProp.GetString();
                        if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith('<')) continue;
                        excerpt = TruncateExcerpt(text);
                        break;
                    }
                }
                catch { }
            }
        }
        catch { }
        return (count, excerpt);
    }

    static string TruncateExcerpt(string text)
    {
        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 100 ? oneLine : oneLine[..100] + "...";
    }

    // --- helpers ---

    static long GetDirectorySize(string path)
    {
        long size = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path))
            {
                try { size += new FileInfo(file).Length; }
                catch { }
            }
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                // Don't follow symlinks/junctions - they may point outside this
                // tree (e.g. pnpm-style node_modules links, WSL mounts) and would
                // give a misleading size and an unsafe target for later deletion.
                try
                {
                    if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;
                }
                catch { continue; }

                size += GetDirectorySize(dir);
            }
        }
        catch { }
        return size;
    }

    static long SafeFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    static List<string> GetWslDistros(List<string>? warnings = null)
    {
        var distros = new List<string>();
        try
        {
            var psi = new ProcessStartInfo("wsl", "-l -q")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.Unicode
            };
            using var proc = Process.Start(psi);
            if (proc == null) return distros;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            distros = output.Split('\n')
                .Select(l => l.Trim().Replace("\0", ""))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
        }
        catch (Exception ex) { warnings?.Add($"WSL: could not list distros ({ex.Message})"); }
        return distros;
    }

    // Known remote-dev-server directories under $HOME that ship their own
    // bundled node_modules/build output as an implementation detail, not user
    // project data. Deleting into these can corrupt the running dev-server
    // (see project memory: WSL cleanup corrupting VS Code Server).
    static readonly string[] WslAppServerDirNames =
    {
        ".vscode-server",
        ".vscode-server-insiders",
        ".cursor-server",
        ".windsurf-server",
    };

    // Already surfaced as their own dedicated CheckItems earlier in Wsl() -
    // walking into these here would just produce redundant/confusing rows.
    static readonly string[] WslAlreadyScannedDirNames = { ".cache", ".npm" };

    public static bool HasProjectMarker(string parentDir, string buildDirName) => buildDirName switch
    {
        "node_modules" => File.Exists(System.IO.Path.Combine(parentDir, "package.json")),
        "target" => new[] { "Cargo.toml", "pom.xml", "build.sbt" }.Any(f => File.Exists(System.IO.Path.Combine(parentDir, f))),
        _ => false,
    };

    static readonly string[] NodeLockfileNames = { "package-lock.json", "pnpm-lock.yaml", "yarn.lock" };

    // Whether re-running the package manager here would reproduce the same
    // dependency tree, not just "some" tree. Without a lockfile, npm/pnpm/yarn
    // resolve semver ranges fresh, which can silently install different
    // transitive versions than what's currently installed.
    public static bool HasLockfile(string parentDir) =>
        NodeLockfileNames.Any(f => File.Exists(System.IO.Path.Combine(parentDir, f)));

    // A workspace root's node_modules can contain symlinks to sibling
    // packages in the same repo, not just downloaded dependencies - worth
    // flagging in the reason text, though it doesn't change the SAFE/REVIEW
    // verdict itself.
    public static bool IsWorkspaceRoot(string parentDir)
    {
        var packageJson = System.IO.Path.Combine(parentDir, "package.json");
        if (File.Exists(packageJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
                if (doc.RootElement.TryGetProperty("workspaces", out _)) return true;
            }
            catch (JsonException) { /* malformed package.json - not a workspace signal either way */ }
        }
        return File.Exists(System.IO.Path.Combine(parentDir, "pnpm-workspace.yaml"));
    }

    const string WorkspaceRootNote =
        " This looks like a workspace root — sibling packages in this repo may link into this node_modules.";

    // Shared SAFE/REVIEW classification for a discovered node_modules/target
    // hit, used by both Wsl() and NativeBuildDirs(). pathForReason is the
    // path shown in the "couldn't confirm" REVIEW message, which differs
    // slightly between the two callers' label conventions.
    // Recycle Bin doesn't exist for \\wsl.localhost\... (or any UNC) path -
    // WindowsTrashProvider deletes those permanently and says so in its result
    // message, but that's only visible *after* the user clicks Clean. This
    // note puts the same fact in the Reason text (shown in the Details panel
    // before they commit) for any item whose Action requests the Recycle Bin
    // but whose path is actually a network/WSL location.
    static string AppendUncRecycleBinNote(string reason, string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal)
            ? reason + " Note: this is a WSL path — the Recycle Bin doesn't support it, so this action permanently deletes and cannot be undone."
            : reason;

    static CheckItem ClassifyBuildDir(string label, long size, string buildDir, string parentDir, string dirName, string pathForReason)
    {
        if (!HasProjectMarker(parentDir, dirName))
        {
            var markerNames = dirName == "node_modules" ? "package.json" : "Cargo.toml/pom.xml/build.sbt";
            var reviewReason = AppendUncRecycleBinNote(
                $"Found at {pathForReason} with no {markerNames} next to it — couldn't confirm this is one of your projects. Check the path before deleting.",
                buildDir);
            return new CheckItem(label, size, "REVIEW", buildDir, Action: ActionKind.MoveFolderToRecycleBin, Reason: reviewReason);
        }

        var workspaceNote = dirName == "node_modules" && IsWorkspaceRoot(parentDir) ? WorkspaceRootNote : "";

        if (dirName == "node_modules" && !HasLockfile(parentDir))
        {
            var lockfileReason = AppendUncRecycleBinNote(
                "package.json found but no lockfile (package-lock.json/pnpm-lock.yaml/yarn.lock) " +
                "next to it — reinstall may not reproduce the exact same dependency versions." + workspaceNote,
                buildDir);
            return new CheckItem(label, size, "REVIEW", buildDir, Action: ActionKind.MoveFolderToRecycleBin, Reason: lockfileReason);
        }

        var buildReason = (dirName == "node_modules"
            ? "npm/pnpm/yarn dependency folder for this project. Regenerated by running the package manager (e.g. `npm install`) in the project directory. Safe to delete — no project code lives here."
            : "Rust/Java/Scala build output for this project. Regenerated by running the build tool (e.g. `cargo build`) in the project directory. Safe to delete — no source code lives here.") + workspaceNote;
        return new CheckItem(label, size, "SAFE", buildDir, Action: ActionKind.DeleteFolder, Reason: buildReason);
    }

    public static WslBuildDirScan FindBuildDirs(string root)
    {
        var buildDirs = new List<string>();
        var excludedAppData = new List<string>();
        void Walk(string dir, int depth)
        {
            if (depth > 6) return;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    // Don't follow symlinks/junctions while discovering build
                    // dirs - matches GetDirectorySize's existing guard, so
                    // discovery can't traverse a link that size-calculation
                    // would skip.
                    try
                    {
                        if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                            continue;
                    }
                    catch { continue; }

                    var name = System.IO.Path.GetFileName(sub);

                    if (WslAppServerDirNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        excludedAppData.Add(sub);
                        continue; // app-owned machinery, don't recurse into it
                    }
                    if (WslAlreadyScannedDirNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                        continue; // already scanned as its own CheckItem

                    if (name == "node_modules" || name == "target")
                    {
                        buildDirs.Add(sub);
                        continue;
                    }
                    Walk(sub, depth + 1);
                }
            }
            catch { }
        }
        Walk(root, 0);
        return new WslBuildDirScan(buildDirs, excludedAppData);
    }

    public record WslBuildDirScan(List<string> BuildDirs, List<string> ExcludedAppDataDirs);

    // OS/vendor-owned trees that must never be descended into during a native
    // build-dir walk - an installed Electron/VS Code-family app can ship its
    // own bundled node_modules under these paths, which is the same failure
    // class FindBuildDirs guards against for WSL's .vscode-server (see
    // "Resolved" in SPEC.md). Under normal Downloads/Documents/Desktop roots
    // these wouldn't be encountered at all; this only matters if a symlink or
    // junction inside a scanned root points into one of them.
    static readonly string[] NativeExcludedRootDirNames =
    {
        "AppData", "Program Files", "Program Files (x86)", "Windows", "ProgramData",
    };

    public static List<string> FindNativeBuildDirs(string root)
    {
        var buildDirs = new List<string>();
        void Walk(string dir, int depth)
        {
            if (depth > 6) return;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    // Same reparse-point guard as FindBuildDirs - don't follow
                    // symlinks/junctions while discovering build dirs.
                    try
                    {
                        if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                            continue;
                    }
                    catch { continue; }

                    var name = System.IO.Path.GetFileName(sub);

                    if (NativeExcludedRootDirNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                        continue; // OS/vendor-owned, never descend into it

                    if (name == "node_modules" || name == "target")
                    {
                        buildDirs.Add(sub);
                        continue;
                    }
                    Walk(sub, depth + 1);
                }
            }
            catch { }
        }
        Walk(root, 0);
        return buildDirs;
    }
}

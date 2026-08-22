using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace DiskCleanup.Core;

// Windows-only scanners, split out of Scanners.cs (2026-08-21) so DiskCleanup.Core
// can retarget to portable net10.0. Everything here depends on a Windows-specific
// mechanism (registry, WSL, $Recycle.Bin, AppX) with no Linux/Mac equivalent — see
// SPEC.md's "Cross-platform MVP scope" per-scanner table for the reasoning behind
// each one's placement here instead of in the portable Scanners class.
public static class WindowsScanners
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
                    total += Scanners.GetDirectorySize(recycleBinPath);
            }
            items.Add(new CheckItem("Recycle Bin", total, "SAFE", Action: ActionKind.EmptyRecycleBin));
        }
        catch (Exception ex) { warnings?.Add($"Recycle Bin: could not read ({ex.Message})"); }
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
            items.Add(new CheckItem("Windows Update cache (SoftwareDistribution\\Download)", Scanners.GetDirectorySize(softwareDistribution), "SAFE", softwareDistribution, Action: ActionKind.DeleteContents));

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
                items.Add(new CheckItem("Windows Temp (system-wide)", Scanners.GetDirectorySize(windowsTemp), "SAFE", windowsTemp, Action: ActionKind.DeleteContents));
        }
        catch (Exception ex) { warnings?.Add($"Windows Temp (system-wide): could not read ({ex.Message})"); }

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
                        items.Add(new CheckItem($"WSL ({distro}) ~/.cache", Scanners.GetDirectorySize(cache), "SAFE", cache, Action: ActionKind.DeleteFolder,
                            Reason: "WSL app cache directory. Apps regenerate this automatically — deleting it doesn't break anything, it just means the next run may be slightly slower while caches are rebuilt."));

                    var npm = System.IO.Path.Combine(userDir, ".npm");
                    if (Directory.Exists(npm))
                        items.Add(new CheckItem($"WSL ({distro}) ~/.npm", Scanners.GetDirectorySize(npm), "SAFE", npm, Action: ActionKind.DeleteFolder,
                            Reason: "npm's local package download cache. Deleting it doesn't remove or break any project — the next `npm install` anywhere just re-downloads packages from the registry instead of using this local cache, which is slower but not destructive."));

                    // pnpm-managed node_modules below are mostly symlinks into this
                    // store, so GetDirectorySize (which skips reparse points) reports
                    // them as near-zero - the real bytes live here instead, and
                    // nothing else in this scanner ever looks at this path.
                    var pnpmStore = System.IO.Path.Combine(userDir, ".local", "share", "pnpm", "store");
                    if (Directory.Exists(pnpmStore))
                        items.Add(new CheckItem($"WSL ({distro}) ~/.local/share/pnpm/store", Scanners.GetDirectorySize(pnpmStore), "SAFE", pnpmStore,
                            Action: ActionKind.DeleteFolder,
                            Reason: "pnpm's shared package cache for this WSL distro, used by every pnpm project here. Deleting it doesn't remove or break any project - the next pnpm install anywhere just re-fetches/re-links packages from the registry instead of this local cache, which is slower but not destructive."));

                    var dirScan = FindBuildDirs(userDir);

                    foreach (var buildDir in dirScan.BuildDirs)
                    {
                        var rel = System.IO.Path.GetRelativePath(userDir, buildDir);
                        var dirName = System.IO.Path.GetFileName(buildDir);
                        var parentDir = System.IO.Path.GetDirectoryName(buildDir)!;
                        var label = $"WSL ({distro}) ~/{rel.Replace('\\', '/')}";
                        var size = Scanners.GetDirectorySize(buildDir);

                        items.Add(Scanners.ClassifyBuildDir(label, size, buildDir, parentDir, dirName, $"~/{rel.Replace('\\', '/')}"));
                    }

                    foreach (var excludedDir in dirScan.ExcludedAppDataDirs)
                    {
                        var rel = System.IO.Path.GetRelativePath(userDir, excludedDir).Replace('\\', '/');
                        items.Add(new CheckItem($"WSL ({distro}) ~/{rel}", Scanners.GetDirectorySize(excludedDir), "INFO", excludedDir, Action: ActionKind.None,
                            Reason: "Bundled files for a remote dev-server (e.g. VS Code Remote-WSL). Deleting the *whole* folder is an official troubleshooting step - the editor reinstalls it automatically on reconnect. The risk is deleting only *part* of it (like one extension's node_modules) while the server may still be running, which is what this tool would otherwise offer piecemeal - so it's excluded here rather than partially cleaned. If you want a full reset, close all connected editor windows first, then delete this folder yourself."));
                    }

                    foreach (var claudeItem in Scanners.ScanClaudeFolder(System.IO.Path.Combine(userDir, ".claude"), warnings))
                        items.Add(claudeItem with { Label = $"WSL ({distro}) {claudeItem.Label}" });
                }
            }
        }
        catch (Exception ex) { warnings?.Add($"WSL: scan failed ({ex.Message})"); }
        return items;
    }

    // Docker()'s "reclaimable" rows only see Docker's own logical accounting
    // (docker system df). The physical .vhdx file backing Docker Desktop's
    // WSL2 disk grows but never auto-shrinks on Windows, even after pruning -
    // this scanner looks at the file directly instead. WSL2-.vhdx-specific:
    // Mac's Docker Desktop grows a differently-shaped disk image under
    // ~/Library/Containers/... - a real analogue, but new scanner work, not a
    // port of this one.
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

                var size = entry.IsFile ? Scanners.SafeFileSize(path) : Scanners.GetDirectorySize(path);
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

                    var size = Scanners.GetDirectorySize(dir);
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

                var size = Scanners.GetDirectorySize(dir);
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
}

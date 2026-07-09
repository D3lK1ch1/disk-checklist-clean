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

    public static List<CheckItem> TempFolders()
    {
        var items = new List<CheckItem>();

        var userTemp = System.IO.Path.GetTempPath();
        if (Directory.Exists(userTemp))
            items.Add(new CheckItem("User Temp folder", GetDirectorySize(userTemp), "SAFE", userTemp, Action: ActionKind.DeleteContents));

        var softwareDistribution = @"C:\Windows\SoftwareDistribution\Download";
        if (Directory.Exists(softwareDistribution))
            items.Add(new CheckItem("Windows Update cache (SoftwareDistribution\\Download)", GetDirectorySize(softwareDistribution), "SAFE", softwareDistribution, Action: ActionKind.DeleteContents));

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

                        if (HasProjectMarker(parentDir, dirName))
                        {
                            var buildReason = dirName == "node_modules"
                                ? "npm/pnpm/yarn dependency folder for this project. Regenerated by running the package manager (e.g. `npm install`) in the project directory. Safe to delete — no project code lives here."
                                : "Rust/Java/Scala build output for this project. Regenerated by running the build tool (e.g. `cargo build`) in the project directory. Safe to delete — no source code lives here.";
                            items.Add(new CheckItem(label, size, "SAFE", buildDir, Action: ActionKind.DeleteFolder, Reason: buildReason));
                        }
                        else
                        {
                            var markerNames = dirName == "node_modules" ? "package.json" : "Cargo.toml/pom.xml/build.sbt";
                            var reviewReason = $"Found at ~/{rel.Replace('\\', '/')} with no {markerNames} next to it — couldn't confirm this is one of your projects. Check the path before deleting.";
                            items.Add(new CheckItem(label, size, "REVIEW", buildDir, Action: ActionKind.DeleteFolder, Reason: reviewReason));
                        }
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
                    Action: ActionKind.MoveFolderToRecycleBin, Reason: claudeCacheReasons[name]));
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

                    // Loose session files only - never recurses into the
                    // adjacent projects/*/memory/ subdirectory.
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

                        items.Add(new CheckItem(
                            $".claude\\projects\\{projectName}\\{System.IO.Path.GetFileName(jsonl)}",
                            size, "REVIEW", jsonl, Action: ActionKind.MoveFileToRecycleBin, Reason: reason));
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
                    Action: ActionKind.MoveFolderToRecycleBin, Reason: codexCacheReasons[name]));
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
                        size, "REVIEW", jsonl, Action: ActionKind.MoveFileToRecycleBin, Reason: reason));
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

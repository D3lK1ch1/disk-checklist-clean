using System.Diagnostics;
using System.Text.Json;

namespace DiskCleanup.Core;

public static class Scanners
{
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
                .Select(p => (Path: p, IsDir: Directory.Exists(p)))
                .Select(x => (x.Path, x.IsDir, Size: x.IsDir ? GetDirectorySize(x.Path) : SafeFileSize(x.Path)))
                .OrderByDescending(x => x.Size)
                .Take(topN);

            foreach (var (path, isDir, size) in entries)
                items.Add(new CheckItem($"Downloads\\{System.IO.Path.GetFileName(path)}", size, "REVIEW", path,
                    Action: isDir ? ActionKind.MoveFolderToRecycleBin : ActionKind.MoveFileToRecycleBin));
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
                    .Select(p => (Path: p, IsDir: Directory.Exists(p)))
                    .Select(x => (x.Path, x.IsDir, Size: x.IsDir ? GetDirectorySize(x.Path) : SafeFileSize(x.Path)))
                    .OrderByDescending(x => x.Size)
                    .Take(topN);

                foreach (var (path, isDir, size) in entries)
                    items.Add(new CheckItem(
                        $"{folderName}\\{System.IO.Path.GetFileName(path)}",
                        size, "REVIEW", path,
                        Action: isDir ? ActionKind.MoveFolderToRecycleBin : ActionKind.MoveFileToRecycleBin,
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

    internal static long GetDirectorySize(string path)
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

    internal static long SafeFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

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

    internal static CheckItem ClassifyBuildDir(string label, long size, string buildDir, string parentDir, string dirName, string pathForReason)
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

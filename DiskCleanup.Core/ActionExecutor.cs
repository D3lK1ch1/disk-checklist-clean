namespace DiskCleanup.Core;

public record ActionResult(CheckItem Item, bool Success, string Message);

public static class ActionExecutor
{
    // Platform seam (see ITrashProvider) - swap this to target Linux/Mac later
    // without touching any of the call sites below. No default: Core can't
    // reference a platform-specific implementation (WindowsTrashProvider lives
    // in DiskCleanup.Core.Windows), so each entry point (Program.cs, App.xaml.cs)
    // must set this once at startup before any trash action runs.
    public static ITrashProvider? TrashProvider { get; set; }

    static ITrashProvider RequireTrashProvider() => TrashProvider
        ?? throw new InvalidOperationException(
            "ActionExecutor.TrashProvider is not set. The platform entry point must assign it " +
            "(e.g. ActionExecutor.TrashProvider = new WindowsTrashProvider();) before executing " +
            "EmptyRecycleBin/MoveFolderToRecycleBin/MoveFileToRecycleBin actions.");

    public static ActionResult Execute(CheckItem item)
    {
        // A grouped item's Path is its project folder - running the normal delete
        // actions on it would delete the whole project. Only ExecuteAll handles these.
        if (item.GroupPaths != null)
            return new ActionResult(item, false, "Refused: grouped items can only run through ExecuteAll.");

        return item.Action switch
        {
            ActionKind.EmptyRecycleBin => EmptyRecycleBin(item),
            ActionKind.DeleteContents => DeleteContents(item),
            ActionKind.DeleteFolder => DeleteFolder(item),
            ActionKind.DeleteFile => DeleteFile(item),
            ActionKind.MoveFolderToRecycleBin => MoveFolderToRecycleBin(item),
            ActionKind.MoveFileToRecycleBin => MoveFileToRecycleBin(item),
            ActionKind.SuggestCommand => new ActionResult(item, true, $"Suggested command (run yourself): {item.CommandSuggestion}"),
            ActionKind.RunDocker => RunDocker(item),
            _ => new ActionResult(item, true, "No action taken (informational only)."),
        };
    }

    // Runs a whole selection, returning one result per item in the same order.
    // WSL folder deletes are pulled out and deleted from inside the distro via
    // WslBatchDelete, one wsl.exe call per project:
    //   - a grouped item (GroupPaths, built by the WSL scanner - one row per repo)
    //     sends all its folders as one batch;
    //   - a single WSL folder item joins the batch of its nearest .git ancestor,
    //     or gets a call of its own outside any repo.
    // Everything else goes through Execute unchanged.
    public static List<ActionResult> ExecuteAll(IReadOnlyList<CheckItem> items)
    {
        var results = new ActionResult?[items.Count];
        var batches = new Dictionary<(string Distro, string Group), List<(int Index, string UncPath, WslPath Wsl)>>();
        var gitCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        bool HasGit(string dir)
        {
            if (!gitCache.TryGetValue(dir, out var hasGit))
            {
                var git = Path.Combine(dir, ".git");
                gitCache[dir] = hasGit = Directory.Exists(git) || File.Exists(git); // .git is a file in worktrees
            }
            return hasGit;
        }

        void AddToBatch(int index, string distro, string group, string uncPath, WslPath wsl)
        {
            if (!batches.TryGetValue((distro, group), out var batch))
                batches[(distro, group)] = batch = new();
            batch.Add((index, uncPath, wsl));
        }

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];

            if (item.GroupPaths != null)
            {
                var targets = AsWslGroupDelete(item, out var refusal);
                if (targets == null)
                {
                    results[i] = new ActionResult(item, false, refusal!);
                    continue;
                }
                foreach (var (uncPath, wsl) in targets)
                    AddToBatch(i, wsl.Distro, item.Path!, uncPath, wsl);
                if (targets.Count == 0) // every folder already gone since the scan
                    results[i] = new ActionResult(item, true, "All folders were already gone.");
                continue;
            }

            var single = AsWslFolderDelete(item);
            if (single == null)
            {
                results[i] = Execute(item);
                continue;
            }
            AddToBatch(i, single.Distro, WslPaths.FindProjectRoot(item.Path!, HasGit) ?? item.Path!, item.Path!, single);
        }

        var outcomesByItem = new Dictionary<int, List<WslDeleteOutcome>>();
        var batchSizeByItem = new Dictionary<int, (int Size, string Group)>();
        foreach (var ((distro, group), batch) in batches)
        {
            var outcomes = WslBatchDelete.Run(distro, batch.Select(b => (b.UncPath, b.Wsl)).ToList());
            var groupLabel = WslPaths.TryParse(group)?.LinuxPath ?? group;
            for (int j = 0; j < batch.Count; j++)
            {
                var index = batch[j].Index;
                if (!outcomesByItem.TryGetValue(index, out var list))
                    outcomesByItem[index] = list = new();
                list.Add(outcomes[j]);
                batchSizeByItem[index] = (batch.Count, groupLabel);
            }
        }

        foreach (var (index, outcomes) in outcomesByItem)
            results[index] = WslResult(items[index], outcomes, batchSizeByItem[index].Size, batchSizeByItem[index].Group);

        return results.Select(r => r!).ToList();
    }

    static ActionResult WslResult(CheckItem item, List<WslDeleteOutcome> outcomes, int batchSize, string groupLabel)
    {
        var failed = outcomes.Where(o => o.Failure != null).ToList();
        var note = WslCompactionNote(item.Path);

        if (item.GroupPaths == null)
        {
            var batchNote = batchSize > 1 ? $" (batch of {batchSize} in {groupLabel})" : "";
            return failed.Count == 0
                ? new ActionResult(item, true, $"Permanently deleted inside WSL{batchNote} - cannot be undone." + note)
                : new ActionResult(item, false, $"Could not delete inside WSL. {failed[0].Failure}");
        }

        var deleted = outcomes.Count - failed.Count;
        if (failed.Count == 0)
            return new ActionResult(item, true,
                $"Permanently deleted all {outcomes.Count} folders inside WSL in one batch - cannot be undone." + note);

        // Any leftover keeps the row FAILED so it stays in the list - rescan to see what's left.
        return new ActionResult(item, false,
            $"Deleted {deleted} of {outcomes.Count} folders inside WSL; {failed.Count} still there:{Environment.NewLine}" +
            string.Join(Environment.NewLine, failed.Select(f => $"{f.UncPath}: {f.Failure}")));
    }

    // Non-null only for a folder delete that's safe to hand to `rm -rf` inside WSL.
    // Items with a SecondaryPath keep the old route - WslBatchDelete doesn't handle pairs.
    // Missing folders also keep the old route so they report "Path not found." as before.
    static WslPath? AsWslFolderDelete(CheckItem item)
    {
        if (item.Action is not (ActionKind.DeleteFolder or ActionKind.MoveFolderToRecycleBin)) return null;
        if (item.SecondaryPath != null) return null;
        var wsl = WslPaths.TryParse(item.Path);
        return wsl != null && Directory.Exists(item.Path) ? wsl : null;
    }

    // All-or-nothing safety check for a grouped item: every folder must pass TryParse
    // and be in the same distro as the project folder, or nothing in the group is
    // deleted. Folders already gone since the scan are skipped, not failed.
    static List<(string UncPath, WslPath Wsl)>? AsWslGroupDelete(CheckItem item, out string? refusal)
    {
        refusal = null;
        var project = WslPaths.TryParse(item.Path);
        if (item.Action is not (ActionKind.DeleteFolder or ActionKind.MoveFolderToRecycleBin) || project == null)
        {
            refusal = "Refused: grouped items are only supported for WSL folder deletes.";
            return null;
        }

        var targets = new List<(string, WslPath)>();
        foreach (var path in item.GroupPaths!)
        {
            var wsl = WslPaths.TryParse(path);
            if (wsl == null || wsl.Distro != project.Distro)
            {
                refusal = $"Refused: nothing deleted - \"{path}\" isn't a safe WSL path in {project.Distro}.";
                return null;
            }
            if (Directory.Exists(path)) targets.Add((path, wsl));
        }
        return targets;
    }

    // Prune commands can run for minutes on a large image cache; volume rm is
    // near-instant. One generous budget covers both.
    static readonly TimeSpan DockerTimeout = TimeSpan.FromMinutes(2);

    // Runs item.CommandSuggestion, which must start with "docker ". The rest is
    // split on whitespace and passed via ArgumentList (no shell), so nothing
    // other than the docker binary can ever be launched. Docker object names
    // can't contain spaces, so whitespace splitting is safe for them.
    // Callers must include -f on prune commands - stdin isn't attached, so
    // docker's "Are you sure? [y/N]" prompt would otherwise abort the prune.
    static ActionResult RunDocker(CheckItem item)
    {
        var parts = (item.CommandSuggestion ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "docker")
            return new ActionResult(item, false, $"Refused: RunDocker only runs 'docker ...' commands, got \"{item.CommandSuggestion}\".");

        var psi = new System.Diagnostics.ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in parts.Skip(1)) psi.ArgumentList.Add(arg);

        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
                return new ActionResult(item, false, "Could not start docker.");

            // Read both streams concurrently - reading one to the end first can
            // deadlock if the other's buffer fills up.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(DockerTimeout))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new ActionResult(item, false, $"Timed out after {DockerTimeout.TotalSeconds:0}s: {item.CommandSuggestion}");
            }

            var output = (stdout.Result + stderr.Result).Trim();
            return proc.ExitCode == 0
                ? new ActionResult(item, true, $"Ran: {item.CommandSuggestion}{Environment.NewLine}{output}")
                : new ActionResult(item, false, $"Failed (exit {proc.ExitCode}): {item.CommandSuggestion}{Environment.NewLine}{output}");
        }
        catch (Exception ex)
        {
            return new ActionResult(item, false, $"Could not run docker - is Docker Desktop installed and running? ({ex.Message})");
        }
    }

    static ActionResult EmptyRecycleBin(CheckItem item)
    {
        var result = RequireTrashProvider().EmptyTrash();
        return new ActionResult(item, result.Success, result.Message);
    }

    static ActionResult DeleteContents(CheckItem item)
    {
        if (item.Path == null || !Directory.Exists(item.Path))
            return new ActionResult(item, false, "Path not found.");

        int deleted = 0;
        var failures = new List<string>();
        foreach (var file in Directory.EnumerateFiles(item.Path))
        {
            try { DeleteFileClearReadOnly(file); deleted++; }
            catch (Exception ex) { failures.Add($"{file}: {ex.Message}"); }
        }
        foreach (var dir in Directory.EnumerateDirectories(item.Path))
        {
            var dirFailures = new List<string>();
            try { SafeDeleteTree(dir, dirFailures); deleted++; }
            catch (Exception ex)
            {
                failures.AddRange(dirFailures.Count > 0 ? dirFailures : new[] { $"{dir}: {ex.Message}" });
            }
        }

        if (failures.Count == 0)
            return new ActionResult(item, true, $"Cleared contents ({deleted} entries removed).");

        if (deleted == 0)
            return new ActionResult(item, false,
                $"Could not delete any entries. Blocked by:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}{Environment.NewLine}" +
                $"Run elevated: Remove-Item -Recurse -Force \"{item.Path}\\*\"");

        return new ActionResult(item, true,
            $"Cleared {deleted} entries, skipped {failures.Count}. Blocked by:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    static ActionResult DeleteFolder(CheckItem item)
    {
        if (item.Path == null || !Directory.Exists(item.Path))
            return new ActionResult(item, false, "Path not found.");

        var failures = new List<string>();
        try
        {
            SafeDeleteTree(item.Path, failures);
            return new ActionResult(item, true, "Folder deleted." + WslCompactionNote(item.Path));
        }
        catch (Exception ex)
        {
            var detail = failures.Count > 0 ? string.Join(Environment.NewLine, failures) : ex.Message;
            return new ActionResult(item, false,
                $"Could not delete folder. Blocked by:{Environment.NewLine}{detail}{Environment.NewLine}" +
                $"Run elevated: Remove-Item -Recurse -Force \"{item.Path}\"");
        }
    }

    // WSL deletions free space inside the distro's .vhdx, not on C: directly.
    internal static string WslCompactionNote(string? path) =>
        path != null && path.StartsWith(@"\\wsl.localhost\", StringComparison.OrdinalIgnoreCase)
            ? " Note: free space on C: won't change until the WSL disk image is compacted" +
              " (run: wsl --manage <distro> --set-sparse true)."
            : string.Empty;

    // Recursively deletes a directory tree. When a reparse point (symlink or
    // junction) is encountered as a child, only the link itself is removed —
    // the link's target is never touched.
    //
    // failures collects which specific file/subfolder blocked deletion (e.g.
    // locked, access denied, too-long path) instead of losing that detail to
    // Directory.Delete's generic "directory not empty" once this returns.
    //
    // internal (not private): WindowsTrashProvider reuses this for its UNC
    // permanent-delete fallback instead of duplicating the reparse-point guard.
    internal static void SafeDeleteTree(string path, List<string>? failures = null)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists) return;

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            info.Delete();
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path))
            try { DeleteFileClearReadOnly(file); }
            catch (Exception ex) { failures?.Add($"{file}: {ex.Message}"); }

        foreach (var sub in Directory.EnumerateDirectories(path))
        {
            var before = failures?.Count ?? 0;
            try { SafeDeleteTree(sub, failures); }
            catch (Exception ex)
            {
                // Only add a fallback entry if the recursive call didn't already
                // record a more specific reason for this subtree.
                if (failures != null && failures.Count == before)
                    failures.Add($"{sub}: {ex.Message}");
            }
        }

        Directory.Delete(path);
    }

    static ActionResult DeleteFile(CheckItem item)
    {
        if (item.Path == null || !File.Exists(item.Path))
            return new ActionResult(item, false, "File not found.");

        try
        {
            DeleteFileClearReadOnly(item.Path);
            return new ActionResult(item, true, "File deleted.");
        }
        catch (Exception ex)
        {
            return new ActionResult(item, false,
                $"Could not delete file. Blocked by: {ex.Message}. Run elevated: Remove-Item -Force \"{item.Path}\"");
        }
    }

    // Git (packed objects) and some installers write files read-only by design - not
    // locked, just flagged. Clearing the attribute first lets a genuine delete succeed
    // instead of failing with a generic "Access is denied" indistinguishable from a real
    // lock held by a running process.
    static void DeleteFileClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    static ActionResult MoveFolderToRecycleBin(CheckItem item)
    {
        if (item.Path == null || !Directory.Exists(item.Path))
            return new ActionResult(item, false, "Path not found.");
        return MoveToRecycleBin(item);
    }

    static ActionResult MoveFileToRecycleBin(CheckItem item)
    {
        if (item.Path == null || !File.Exists(item.Path))
            return new ActionResult(item, false, "File not found.");
        return MoveToRecycleBin(item);
    }

    // The only difference between the two callers above is which existence
    // check makes sense first; the actual move goes through TrashProvider.
    static ActionResult MoveToRecycleBin(CheckItem item)
    {
        var trashProvider = RequireTrashProvider();
        var result = trashProvider.MoveToTrash(item.Path!);
        var message = result.Success ? result.Message + WslCompactionNote(item.Path) : result.Message;

        if (item.SecondaryPath != null && Directory.Exists(item.SecondaryPath))
        {
            var secondary = trashProvider.MoveToTrash(item.SecondaryPath);
            message += secondary.Success
                ? " Paired folder moved too."
                : $" Could not move paired folder \"{item.SecondaryPath}\": {secondary.Message}";
        }

        return new ActionResult(item, result.Success, message);
    }
}

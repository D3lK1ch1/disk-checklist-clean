namespace DiskCleanup.Core;

public record ActionResult(CheckItem Item, bool Success, string Message);

public static class ActionExecutor
{
    // Platform seam (see ITrashProvider) - swap this to target Linux/Mac later
    // without touching any of the call sites below.
    public static ITrashProvider TrashProvider { get; set; } = new WindowsTrashProvider();

    public static ActionResult Execute(CheckItem item)
    {
        return item.Action switch
        {
            ActionKind.EmptyRecycleBin => EmptyRecycleBin(item),
            ActionKind.DeleteContents => DeleteContents(item),
            ActionKind.DeleteFolder => DeleteFolder(item),
            ActionKind.MoveFolderToRecycleBin => MoveFolderToRecycleBin(item),
            ActionKind.MoveFileToRecycleBin => MoveFileToRecycleBin(item),
            ActionKind.SuggestCommand => new ActionResult(item, true, $"Suggested command (run yourself): {item.CommandSuggestion}"),
            _ => new ActionResult(item, true, "No action taken (informational only)."),
        };
    }

    static ActionResult EmptyRecycleBin(CheckItem item)
    {
        var result = TrashProvider.EmptyTrash();
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
            try { File.Delete(file); deleted++; }
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
                $"Could not delete any entries. Blocked by: {string.Join("; ", failures)}. " +
                $"Run elevated: Remove-Item -Recurse -Force \"{item.Path}\\*\"");

        return new ActionResult(item, true,
            $"Cleared {deleted} entries, skipped {failures.Count}. Blocked by: {string.Join("; ", failures)}.");
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
            var detail = failures.Count > 0 ? string.Join("; ", failures) : ex.Message;
            return new ActionResult(item, false,
                $"Could not delete folder. Blocked by: {detail}. " +
                $"Run elevated: Remove-Item -Recurse -Force \"{item.Path}\"");
        }
    }

    // WSL deletions free space inside the distro's .vhdx, not on C: directly.
    static string WslCompactionNote(string? path) =>
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
    static void SafeDeleteTree(string path, List<string>? failures = null)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists) return;

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            info.Delete();
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path))
            try { File.Delete(file); }
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
        var result = TrashProvider.MoveToTrash(item.Path!);
        var message = result.Success ? result.Message + WslCompactionNote(item.Path) : result.Message;

        if (item.SecondaryPath != null && Directory.Exists(item.SecondaryPath))
        {
            var secondary = TrashProvider.MoveToTrash(item.SecondaryPath);
            message += secondary.Success
                ? " Paired folder moved to Recycle Bin too."
                : $" Could not move paired folder \"{item.SecondaryPath}\": {secondary.Message}";
        }

        return new ActionResult(item, result.Success, message);
    }
}

using System.Runtime.InteropServices;

namespace DiskCleanup.Core;

public record ActionResult(CheckItem Item, bool Success, string Message);

public static class ActionExecutor
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    const uint SHERB_NOCONFIRMATION = 0x00000001;
    const uint SHERB_NOPROGRESSUI = 0x00000002;
    const uint SHERB_NOSOUND = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    const uint FO_DELETE = 0x0003;
    const ushort FOF_ALLOWUNDO = 0x0040;
    const ushort FOF_NOCONFIRMATION = 0x0010;
    const ushort FOF_NOERRORUI = 0x0400;
    const ushort FOF_SILENT = 0x0004;

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
        try
        {
            var flags = SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND;
            var hr = SHEmptyRecycleBinW(IntPtr.Zero, null, flags);
            // S_OK (0) or S_FALSE (1, e.g. already empty) both count as success.
            if (hr == 0 || hr == 1)
                return new ActionResult(item, true, "Recycle Bin emptied.");
            return new ActionResult(item, false, $"Failed to empty Recycle Bin (HRESULT 0x{hr:X}).");
        }
        catch (Exception ex)
        {
            return new ActionResult(item, false, $"Failed to empty Recycle Bin: {ex.Message}");
        }
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

    // SHFileOperation/FO_DELETE works the same whether pFrom is a file or a
    // directory - the only difference between the two callers above is which
    // existence check makes sense first.
    static ActionResult MoveToRecycleBin(CheckItem item)
    {
        try
        {
            // pFrom must be double-null-terminated; the extra '\0' plus the
            // string's own terminator gives SHFileOperation what it needs.
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = item.Path + '\0',
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT),
            };
            var hr = SHFileOperation(ref op);
            if (hr == 0 && !op.fAnyOperationsAborted)
                return new ActionResult(item, true, "Moved to Recycle Bin." + WslCompactionNote(item.Path));
            if (op.fAnyOperationsAborted)
                return new ActionResult(item, false, "Operation was cancelled.");
            return new ActionResult(item, false, $"SHFileOperation failed (error 0x{hr:X}).");
        }
        catch (Exception ex)
        {
            return new ActionResult(item, false, $"Failed to move to Recycle Bin: {ex.Message}");
        }
    }
}

using System.Runtime.InteropServices;

namespace DiskCleanup.Core;

public class WindowsTrashProvider : ITrashProvider
{
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

    // SHFileOperation/FO_DELETE works the same whether pFrom is a file or a
    // directory, so this one method covers both callers in ActionExecutor.
    //
    // UNC paths (\\wsl.localhost\..., \\server\share\...) are checked first:
    // the Windows Recycle Bin has no concept of a network/WSL location.
    // Empirically verified (2026-08-18): calling SHFileOperation with
    // FOF_ALLOWUNDO against a \\wsl.localhost\ path returns success and would
    // report "Moved to Recycle Bin" here, but the file never appears in the
    // Recycle Bin - it's silently, permanently gone. Rather than let that
    // silent OS behavior report itself as recoverable, this path is deleted
    // explicitly and reported as what it actually is.
    public TrashResult MoveToTrash(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return PermanentlyDeleteUncPath(path);

        try
        {
            // pFrom must be double-null-terminated; the extra '\0' plus the
            // string's own terminator gives SHFileOperation what it needs.
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + '\0',
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT),
            };
            var hr = SHFileOperation(ref op);
            if (hr == 0 && !op.fAnyOperationsAborted)
                return new TrashResult(true, "Moved to Recycle Bin.");
            if (op.fAnyOperationsAborted)
                return new TrashResult(false, "Operation was cancelled.");
            return new TrashResult(false, $"SHFileOperation failed (error 0x{hr:X}).");
        }
        catch (Exception ex)
        {
            return new TrashResult(false, $"Failed to move to Recycle Bin: {ex.Message}");
        }
    }

    static TrashResult PermanentlyDeleteUncPath(string path)
    {
        var failures = new List<string>();
        try
        {
            if (Directory.Exists(path))
                ActionExecutor.SafeDeleteTree(path, failures);
            else if (File.Exists(path))
                File.Delete(path);
            else
                return new TrashResult(false, "Path not found.");

            if (failures.Count > 0)
                return new TrashResult(false, $"Partially deleted. Blocked by: {string.Join("; ", failures)}.");

            return new TrashResult(true,
                "Permanently deleted - the Recycle Bin doesn't support network/WSL paths, so this could not be sent there and cannot be undone.");
        }
        catch (Exception ex)
        {
            return new TrashResult(false, $"Failed to delete: {ex.Message}");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    const uint SHERB_NOCONFIRMATION = 0x00000001;
    const uint SHERB_NOPROGRESSUI = 0x00000002;
    const uint SHERB_NOSOUND = 0x00000004;

    public TrashResult EmptyTrash()
    {
        try
        {
            var flags = SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND;
            var hr = SHEmptyRecycleBinW(IntPtr.Zero, null, flags);
            // S_OK (0) or S_FALSE (1, e.g. already empty) both count as success.
            if (hr == 0 || hr == 1)
                return new TrashResult(true, "Recycle Bin emptied.");
            return new TrashResult(false, $"Failed to empty Recycle Bin (HRESULT 0x{hr:X}).");
        }
        catch (Exception ex)
        {
            return new TrashResult(false, $"Failed to empty Recycle Bin: {ex.Message}");
        }
    }
}

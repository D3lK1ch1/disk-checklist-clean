using System.Runtime.CompilerServices;

// WindowsTrashProvider (DiskCleanup.Core.Windows) reuses ActionExecutor.SafeDeleteTree
// for its UNC permanent-delete fallback instead of duplicating the reparse-point guard.
// MacTrashProvider (DiskCleanup.Core.Mac) reuses it the same way to empty ~/.Trash.
// Kept internal rather than public so it isn't part of Core's public API surface.
[assembly: InternalsVisibleTo("DiskCleanup.Core.Windows")]
[assembly: InternalsVisibleTo("DiskCleanup.Core.Mac")]

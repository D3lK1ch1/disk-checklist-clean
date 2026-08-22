namespace DiskCleanup.Core;

// macOS trash implementation of ITrashProvider. Moves items into ~/.Trash -
// the boot-volume trash convention macOS uses (each *other* mounted volume
// has its own .Trashes/<uid>, but this tool only ever touches boot-volume
// paths - Downloads, Documents, ~/Library equivalents - so ~/.Trash is
// always the right target for what this tool trashes).
//
// This is a plain filesystem move, not a call into Foundation's
// NSFileManager trashItem:resultingItemURL:error: - so Finder's "Put Back"
// (which relies on extended-attribute metadata that API writes) will not
// work for items moved this way. The item still lands in ~/.Trash, is
// visible there, and is recoverable by moving it back out manually - the
// same "recoverable, not identical-fidelity-to-native" bar
// WindowsTrashProvider accepts for its own UNC-path fallback. A true
// trashItem port would need Cocoa interop, out of scope for this seam.
//
// NOT YET VERIFIED ON A REAL MAC - built and unit-tested against a fake
// trash root on this Windows dev machine (see trashRootOverride below and
// MacTrashProviderTests). The actual ~/.Trash integration - whether Finder
// shows moved items correctly, whether macOS's own Trash icon/badge updates
// - can only be confirmed by running this on macOS.
public class MacTrashProvider : ITrashProvider
{
    readonly string _trashRoot;

    // trashRootOverride exists only so tests can point at a fake tree instead
    // of the real ~/.Trash - production callers pass neither.
    public MacTrashProvider(string? trashRootOverride = null)
    {
        _trashRoot = trashRootOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");
    }

    public TrashResult MoveToTrash(string path)
    {
        try
        {
            var isDirectory = Directory.Exists(path);
            var isFile = !isDirectory && File.Exists(path);
            if (!isDirectory && !isFile)
                return new TrashResult(false, "Path not found.");

            Directory.CreateDirectory(_trashRoot);
            var destination = UniqueDestination(Path.GetFileName(path));

            if (isDirectory)
                Directory.Move(path, destination);
            else
                File.Move(path, destination);

            return new TrashResult(true, "Moved to Trash.");
        }
        catch (Exception ex)
        {
            return new TrashResult(false, $"Failed to move to Trash: {ex.Message}");
        }
    }

    // Finder's own collision behavior: "name.ext" -> "name 2.ext" -> "name 3.ext"
    // rather than overwriting or erroring, since two different items with the
    // same name can land in Trash across separate cleanup runs.
    string UniqueDestination(string name)
    {
        var candidate = Path.Combine(_trashRoot, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
            return candidate;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            candidate = Path.Combine(_trashRoot, $"{stem} {i}{ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    public TrashResult EmptyTrash()
    {
        try
        {
            if (!Directory.Exists(_trashRoot))
                return new TrashResult(true, "Trash is already empty.");

            var failures = new List<string>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(_trashRoot))
            {
                try
                {
                    if (Directory.Exists(entry))
                        ActionExecutor.SafeDeleteTree(entry, failures);
                    else
                        File.Delete(entry);
                }
                catch (Exception ex) { failures.Add($"{entry}: {ex.Message}"); }
            }

            return failures.Count == 0
                ? new TrashResult(true, "Trash emptied.")
                : new TrashResult(false, $"Partially emptied. Blocked by: {string.Join("; ", failures)}.");
        }
        catch (Exception ex)
        {
            return new TrashResult(false, $"Failed to empty Trash: {ex.Message}");
        }
    }
}

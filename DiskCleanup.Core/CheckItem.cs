namespace DiskCleanup.Core;

public enum ActionKind
{
    None,
    EmptyRecycleBin,
    DeleteContents,
    DeleteFolder,
    DeleteFile,
    MoveFolderToRecycleBin,
    MoveFileToRecycleBin,
    SuggestCommand,
    // Runs CommandSuggestion for real, but only if it's a `docker ...` command -
    // deliberately not a generic "run any command" action.
    RunDocker,
}

public record CheckItem(
    string Label,
    long SizeBytes,
    string Risk,
    string? Path = null,
    string? SizeOverride = null,
    ActionKind Action = ActionKind.None,
    string? CommandSuggestion = null,
    string? Reason = null,
    // Paired folder that gets removed alongside Path in the same action, e.g. a
    // Claude Code session's <sessionId>/ subagents+tool-results dir next to its .jsonl.
    // Only MoveFileToRecycleBin honors this today - not a generic multi-path mechanism.
    string? SecondaryPath = null,
    // Folders this one row deletes together, e.g. every node_modules in one WSL repo.
    // When set, Path is the project folder for display only and is NEVER deleted itself:
    // Execute refuses grouped items, and ExecuteAll deletes exactly these paths.
    IReadOnlyList<string>? GroupPaths = null)
{
    public string FormattedSize => SizeOverride ?? Format(SizeBytes);

    public string ActionDescription => Action switch
    {
        ActionKind.None                   => "info only",
        ActionKind.EmptyRecycleBin        => "empty Recycle Bin",
        ActionKind.DeleteContents         => "delete contents",
        ActionKind.DeleteFolder           => "delete folder",
        ActionKind.DeleteFile             => "delete file",
        ActionKind.MoveFolderToRecycleBin => "→ Recycle Bin",
        ActionKind.MoveFileToRecycleBin   => "→ Recycle Bin",
        ActionKind.SuggestCommand         => "suggest command",
        ActionKind.RunDocker              => "run docker command",
        _                                 => "unknown",
    };

    static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#}{units[unit]}";
    }
}

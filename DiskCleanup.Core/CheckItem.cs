namespace DiskCleanup.Core;

public enum ActionKind
{
    None,
    EmptyRecycleBin,
    DeleteContents,
    DeleteFolder,
    MoveFolderToRecycleBin,
    MoveFileToRecycleBin,
    SuggestCommand,
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
    string? SecondaryPath = null)
{
    public string FormattedSize => SizeOverride ?? Format(SizeBytes);

    public string ActionDescription => Action switch
    {
        ActionKind.None                   => "info only",
        ActionKind.EmptyRecycleBin        => "empty Recycle Bin",
        ActionKind.DeleteContents         => "delete contents",
        ActionKind.DeleteFolder           => "delete folder",
        ActionKind.MoveFolderToRecycleBin => "→ Recycle Bin",
        ActionKind.MoveFileToRecycleBin   => "→ Recycle Bin",
        ActionKind.SuggestCommand         => "suggest command",
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

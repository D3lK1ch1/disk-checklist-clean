namespace DiskCleanup.Core;

public record TrashResult(bool Success, string Message);

// Platform seam for "move this path to the OS trash/recycle bin, recoverably."
// WindowsTrashProvider is the only implementation today; Linux (freedesktop.org
// trash spec) and Mac (~/.Trash) implementations are separate follow-up units.
public interface ITrashProvider
{
    TrashResult MoveToTrash(string path);
    TrashResult EmptyTrash();
}

using System.Diagnostics;
using DiskCleanup.Core;

namespace DiskCleanup.Tests;

// All tests operate on throwaway folders under the OS temp directory —
// never on real user paths like Downloads, AppData, or the Recycle Bin.
public class ActionExecutorTests
{
    // TrashProvider is no longer self-initializing (Core can't reference
    // WindowsTrashProvider once it moves to DiskCleanup.Core.Windows), so the
    // tests that exercise MoveFolderToRecycleBin/MoveFileToRecycleBin need it
    // set explicitly, same as a real entry point (Program.cs, App.xaml.cs) does.
    static ActionExecutorTests()
    {
        ActionExecutor.TrashProvider = new WindowsTrashProvider();
    }

    static string CreateTempDirWithContents()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "file.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(dir, "subdir"));
        File.WriteAllText(Path.Combine(dir, "subdir", "nested.txt"), "world");
        return dir;
    }

    [Fact]
    public void DeleteFolder_RemovesFolderAndContents()
    {
        var dir = CreateTempDirWithContents();
        var item = new CheckItem("test folder", 0, "REVIEW", dir, Action: ActionKind.DeleteFolder);

        var result = ActionExecutor.Execute(item);

        Assert.True(result.Success);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void DeleteFolder_MissingPath_ReturnsFailure()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_DoesNotExist_" + Guid.NewGuid());
        var item = new CheckItem("missing folder", 0, "REVIEW", dir, Action: ActionKind.DeleteFolder);

        var result = ActionExecutor.Execute(item);

        Assert.False(result.Success);
    }

    [Fact]
    public void DeleteContents_EmptiesFolderButKeepsIt()
    {
        var dir = CreateTempDirWithContents();
        var item = new CheckItem("test temp", 0, "SAFE", dir, Action: ActionKind.DeleteContents);

        var result = ActionExecutor.Execute(item);

        Assert.True(result.Success);
        Assert.True(Directory.Exists(dir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));

        Directory.Delete(dir);
    }

    [Fact]
    public void DeleteContents_MissingPath_ReturnsFailure()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_DoesNotExist_" + Guid.NewGuid());
        var item = new CheckItem("missing temp", 0, "SAFE", dir, Action: ActionKind.DeleteContents);

        var result = ActionExecutor.Execute(item);

        Assert.False(result.Success);
    }

    [Fact]
    public void SuggestCommand_DoesNotTouchFilesystemAndReturnsCommand()
    {
        var item = new CheckItem("Docker Images (reclaimable)", 0, "REVIEW",
            Action: ActionKind.SuggestCommand, CommandSuggestion: "docker image prune -a");

        var result = ActionExecutor.Execute(item);

        Assert.True(result.Success);
        Assert.Contains("docker image prune -a", result.Message);
    }

    [Fact]
    public void None_ReturnsSuccessWithNoAction()
    {
        var item = new CheckItem("Installed: Some App", 0, "INFO");

        var result = ActionExecutor.Execute(item);

        Assert.True(result.Success);
        Assert.Contains("informational", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MoveFolderToRecycleBin_SendsFolderToRecycleBinAndFolderDisappears()
    {
        var dir = CreateTempDirWithContents();
        var item = new CheckItem("test review folder", 0, "REVIEW", dir, Action: ActionKind.MoveFolderToRecycleBin);

        var result = ActionExecutor.Execute(item);

        Assert.True(result.Success, result.Message);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void MoveFolderToRecycleBin_MissingPath_ReturnsFailure()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_DoesNotExist_" + Guid.NewGuid());
        var item = new CheckItem("missing review folder", 0, "REVIEW", dir, Action: ActionKind.MoveFolderToRecycleBin);

        var result = ActionExecutor.Execute(item);

        Assert.False(result.Success);
    }

    [Fact]
    public void DeleteFolder_LockedFile_NamesTheBlockingFileInsteadOfGenericMessage()
    {
        var dir = CreateTempDirWithContents();
        var lockedPath = Path.Combine(dir, "subdir", "nested.txt");

        // Holding an exclusive FileStream open reliably makes File.Delete
        // throw "being used by another process" without needing elevation.
        using var lockHandle = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var item = new CheckItem("test folder", 0, "REVIEW", dir, Action: ActionKind.DeleteFolder);
        var result = ActionExecutor.Execute(item);

        Assert.False(result.Success);
        Assert.Contains(lockedPath, result.Message);
        Assert.True(Directory.Exists(dir), "Folder should survive when a file inside it couldn't be deleted.");

        lockHandle.Dispose();
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void DeleteContents_LockedFile_NamesTheBlockingFileAndKeepsRestCleared()
    {
        var dir = CreateTempDirWithContents();
        var lockedPath = Path.Combine(dir, "file.txt");

        using var lockHandle = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var item = new CheckItem("test temp", 0, "SAFE", dir, Action: ActionKind.DeleteContents);
        var result = ActionExecutor.Execute(item);

        // subdir/nested.txt has no lock, so it's cleared even though file.txt is blocked.
        Assert.True(result.Success);
        Assert.Contains(lockedPath, result.Message);
        Assert.True(File.Exists(lockedPath));
        Assert.False(Directory.Exists(Path.Combine(dir, "subdir")));

        lockHandle.Dispose();
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void DeleteFolder_DoesNotRecurseIntoJunction()
    {
        // Target is a separate directory that should survive the deletion.
        // Junctions (unlike symlinks) need no special privilege to create or
        // delete, so this runs the real guard logic on every machine.
        var target = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_LinkTarget_" + Guid.NewGuid());
        var container = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_LinkContainer_" + Guid.NewGuid());
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "important");
        Directory.CreateDirectory(container);

        var junction = Path.Combine(container, "link");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit(5000);

        Assert.True(Directory.Exists(junction), $"mklink /J failed to create the junction: {proc.StandardError.ReadToEnd()}");

        var item = new CheckItem("test container", 0, "REVIEW", container, Action: ActionKind.DeleteFolder);
        var result = ActionExecutor.Execute(item);

        Assert.True(result.Success, result.Message);
        Assert.False(Directory.Exists(container));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")), "Junction target must not be deleted.");

        Directory.Delete(target, recursive: true);
    }
}

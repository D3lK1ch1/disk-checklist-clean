using DiskCleanup.Core;

namespace DiskCleanup.Tests;

// Unlike WindowsTrashProvider (real Win32 shell32.dll calls against the
// actual Recycle Bin - only verifiable live), MacTrashProvider is a plain
// filesystem move against an overridable trash root, so its logic can be
// unit tested here even though nothing runs on real macOS yet.
public class MacTrashProviderTests
{
    static string CreateTempRoot(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"DiskCleanupTests_{prefix}_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void MoveToTrash_File_MovesFileIntoTrashRoot()
    {
        var workDir = CreateTempRoot("MacTrash");
        var trashRoot = Path.Combine(workDir, ".Trash");
        var file = Path.Combine(workDir, "note.txt");
        File.WriteAllText(file, "hello");
        try
        {
            var provider = new MacTrashProvider(trashRoot);
            var result = provider.MoveToTrash(file);

            Assert.True(result.Success);
            Assert.False(File.Exists(file));
            Assert.True(File.Exists(Path.Combine(trashRoot, "note.txt")));
        }
        finally { Directory.Delete(workDir, recursive: true); }
    }

    [Fact]
    public void MoveToTrash_Directory_MovesDirectoryIntoTrashRoot()
    {
        var workDir = CreateTempRoot("MacTrash");
        var trashRoot = Path.Combine(workDir, ".Trash");
        var folder = Path.Combine(workDir, "project");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "file.txt"), "hello");
        try
        {
            var provider = new MacTrashProvider(trashRoot);
            var result = provider.MoveToTrash(folder);

            Assert.True(result.Success);
            Assert.False(Directory.Exists(folder));
            Assert.True(File.Exists(Path.Combine(trashRoot, "project", "file.txt")));
        }
        finally { Directory.Delete(workDir, recursive: true); }
    }

    [Fact]
    public void MoveToTrash_MissingPath_ReturnsFailure()
    {
        var workDir = CreateTempRoot("MacTrash");
        var trashRoot = Path.Combine(workDir, ".Trash");
        try
        {
            var provider = new MacTrashProvider(trashRoot);
            var result = provider.MoveToTrash(Path.Combine(workDir, "does-not-exist.txt"));

            Assert.False(result.Success);
        }
        finally { Directory.Delete(workDir, recursive: true); }
    }

    [Fact]
    public void MoveToTrash_NameCollision_AppendsNumberLikeFinder()
    {
        var workDir = CreateTempRoot("MacTrash");
        var trashRoot = Path.Combine(workDir, ".Trash");
        Directory.CreateDirectory(trashRoot);
        File.WriteAllText(Path.Combine(trashRoot, "note.txt"), "already in trash");

        var file = Path.Combine(workDir, "note.txt");
        File.WriteAllText(file, "new copy");
        try
        {
            var provider = new MacTrashProvider(trashRoot);
            var result = provider.MoveToTrash(file);

            Assert.True(result.Success);
            Assert.True(File.Exists(Path.Combine(trashRoot, "note.txt")));
            Assert.True(File.Exists(Path.Combine(trashRoot, "note 2.txt")));
            Assert.Equal("already in trash", File.ReadAllText(Path.Combine(trashRoot, "note.txt")));
            Assert.Equal("new copy", File.ReadAllText(Path.Combine(trashRoot, "note 2.txt")));
        }
        finally { Directory.Delete(workDir, recursive: true); }
    }

    [Fact]
    public void EmptyTrash_RemovesAllContents()
    {
        var workDir = CreateTempRoot("MacTrash");
        var trashRoot = Path.Combine(workDir, ".Trash");
        Directory.CreateDirectory(trashRoot);
        File.WriteAllText(Path.Combine(trashRoot, "a.txt"), "a");
        var subDir = Path.Combine(trashRoot, "b");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "c.txt"), "c");
        try
        {
            var provider = new MacTrashProvider(trashRoot);
            var result = provider.EmptyTrash();

            Assert.True(result.Success);
            Assert.Empty(Directory.EnumerateFileSystemEntries(trashRoot));
        }
        finally { Directory.Delete(workDir, recursive: true); }
    }

    [Fact]
    public void EmptyTrash_TrashRootDoesNotExist_ReturnsSuccessAlreadyEmpty()
    {
        var workDir = CreateTempRoot("MacTrash");
        var trashRoot = Path.Combine(workDir, ".Trash");
        try
        {
            var provider = new MacTrashProvider(trashRoot);
            var result = provider.EmptyTrash();

            Assert.True(result.Success);
        }
        finally { Directory.Delete(workDir, recursive: true); }
    }
}

using DiskCleanup.Core;

namespace DiskCleanup.Tests;

// Pure tests - paths are strings only, nothing on disk is touched.
public class GroupBuildDirsTests
{
    const string Repo = @"\wsl.localhost\Ubuntu\home\delia\projects\harness";
    const string Other = @"\wsl.localhost\Ubuntu\home\delia\.config\opencode\node_modules";

    static CheckItem Build(string path, string risk = "SAFE", long size = 10) =>
        new(path, size, risk, path,
            Action: risk == "SAFE" ? ActionKind.DeleteFolder : ActionKind.MoveFolderToRecycleBin,
            Reason: risk == "SAFE" ? "safe reason" : "no lockfile");

    static List<CheckItem> Group(params CheckItem[] items) =>
        Scanners.GroupBuildDirsByProject(items,
            path => path.StartsWith(Repo + @"\") ? Repo : null,
            root => "WSL (Ubuntu) ~/projects/harness");

    [Fact]
    public void SameRepo_FoldsIntoOneRow_WithAllPathsAndSummedSize()
    {
        var a = Build(Repo + @"\packages\a\node_modules");
        var b = Build(Repo + @"\packages\b\node_modules");

        var row = Assert.Single(Group(a, b));

        Assert.Equal(Repo, row.Path);
        Assert.Equal(new[] { a.Path!, b.Path! }, row.GroupPaths);
        Assert.Equal(20, row.SizeBytes);
        Assert.Equal("SAFE", row.Risk);
        Assert.Equal(ActionKind.DeleteFolder, row.Action);
        Assert.Contains("2 build folders", row.Label);
    }

    [Fact]
    public void ReviewFolder_FoldsIn_AndMakesRowReview_WithItsReasonListed()
    {
        var safe = Build(Repo + @"\packages\a\node_modules");
        var review = Build(Repo + @"\packages\b\node_modules", "REVIEW");

        var row = Assert.Single(Group(safe, review));

        Assert.Equal("REVIEW", row.Risk);
        Assert.Equal(ActionKind.MoveFolderToRecycleBin, row.Action);
        Assert.Contains("packages/b/node_modules: no lockfile", row.Reason);
    }

    [Fact]
    public void SingleBuildDirInRepo_StaysAsItsOwnRow()
    {
        var only = Build(Repo + @"\node_modules");

        Assert.Same(only, Assert.Single(Group(only)));
    }

    [Fact]
    public void DirsOutsideAnyRepo_AreNeverGrouped()
    {
        var x = Build(Other);
        var y = Build(@"\wsl.localhost\Ubuntu\home\delia\.opencode\node_modules");

        var rows = Group(x, y);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Null(r.GroupPaths));
    }

    [Fact]
    public void Execute_GroupedItem_IsRefused_SoTheProjectFolderIsNeverDeleted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var item = new CheckItem("grouped", 0, "SAFE", dir, Action: ActionKind.DeleteFolder, GroupPaths: new[] { dir });

            var result = ActionExecutor.Execute(item);

            Assert.False(result.Success);
            Assert.StartsWith("Refused", result.Message);
            Assert.True(Directory.Exists(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ExecuteAll_GroupedItemWithNonWslPath_RefusesWholeGroup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var item = new CheckItem("grouped", 0, "SAFE", Repo, Action: ActionKind.DeleteFolder, GroupPaths: new[] { dir });

            var result = Assert.Single(ActionExecutor.ExecuteAll(new[] { item }));

            Assert.False(result.Success);
            Assert.StartsWith("Refused", result.Message);
            Assert.True(Directory.Exists(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}

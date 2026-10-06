using DiskCleanup.Core;

namespace DiskCleanup.Tests;

// Pure string tests - nothing here touches a real WSL distro.
public class WslPathsTests
{
    [Fact]
    public void TryParse_NodeModulesPath_ReturnsDistroAndLinuxPath()
    {
        var result = WslPaths.TryParse(@"\\wsl.localhost\Ubuntu\home\delia\projects\app\node_modules");

        Assert.Equal(new WslPath("Ubuntu", "/home/delia/projects/app/node_modules"), result);
    }

    [Fact]
    public void TryParse_DotFolderUnderHome_IsAllowed()
    {
        var result = WslPaths.TryParse(@"\\wsl.localhost\Ubuntu\home\delia\.cache");

        Assert.Equal(new WslPath("Ubuntu", "/home/delia/.cache"), result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(@"C:\Users\delia\node_modules")]                         // not WSL
    [InlineData(@"\\server\share\home\delia\node_modules")]               // other UNC share
    [InlineData(@"\\wsl.localhost\Ubuntu\home\delia")]                    // the home folder itself
    [InlineData(@"\\wsl.localhost\Ubuntu\home")]                          // /home
    [InlineData(@"\\wsl.localhost\Ubuntu\etc\something")]                 // outside /home
    [InlineData(@"\\wsl.localhost\Ubuntu\home\delia\..\bob\node_modules")] // traversal
    [InlineData(@"\\wsl.localhost\Ubuntu\home\delia\.vscode-server\extensions\x\node_modules")]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\delia\.cursor-server")]
    public void TryParse_UnsafeOrNonWslPath_ReturnsNull(string? path)
    {
        Assert.Null(WslPaths.TryParse(path));
    }

    const string Repo = @"\\wsl.localhost\Ubuntu\home\delia\projects\harness";

    [Fact]
    public void FindProjectRoot_NestedPackage_ReturnsNearestGitAncestor()
    {
        var root = WslPaths.FindProjectRoot(Repo + @"\packages\util\time\node_modules", dir => dir == Repo);

        Assert.Equal(Repo, root);
    }

    [Fact]
    public void FindProjectRoot_NoRepo_ReturnsNull()
    {
        Assert.Null(WslPaths.FindProjectRoot(@"\\wsl.localhost\Ubuntu\home\delia\.config\opencode\node_modules", _ => false));
    }

    [Fact]
    public void FindProjectRoot_DotfilesRepoAtHome_IsIgnored()
    {
        var home = @"\\wsl.localhost\Ubuntu\home\delia";

        Assert.Null(WslPaths.FindProjectRoot(home + @"\.cache", dir => dir == home));
    }

    [Fact]
    public void FindProjectRoot_DoesNotCountTheItemItself()
    {
        var item = Repo + @"\node_modules";

        Assert.Equal(Repo, WslPaths.FindProjectRoot(item, dir => dir == Repo || dir == item));
    }
}

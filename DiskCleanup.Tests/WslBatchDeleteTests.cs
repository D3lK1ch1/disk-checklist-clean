using System.Diagnostics;
using DiskCleanup.Core;

namespace DiskCleanup.Tests;

public class WslBatchDeleteTests
{
    [Fact]
    public void ExecuteAll_NonWslItems_ReturnResultsInInputOrder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var missing = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_DoesNotExist_" + Guid.NewGuid());
        var items = new[]
        {
            new CheckItem("missing", 0, "SAFE", missing, Action: ActionKind.DeleteFolder),
            new CheckItem("real", 0, "SAFE", dir, Action: ActionKind.DeleteFolder),
        };

        var results = ActionExecutor.ExecuteAll(items);

        Assert.Equal(new[] { "missing", "real" }, results.Select(r => r.Item.Label));
        Assert.False(results[0].Success);
        Assert.True(results[1].Success);
        Assert.False(Directory.Exists(dir));
    }

    // End-to-end against the real default WSL distro. Builds a throwaway pnpm-shaped
    // repo under ~/diskcleanup-e2e-<guid>: a .git folder, two packages whose
    // node_modules hold *symlinks* (the exact case that fails through \\wsl.localhost\),
    // and a shared "store" folder the symlinks point at. Deletes both node_modules via
    // ExecuteAll and checks: both gone, both reported OK as one batch, and the symlink
    // target untouched. Only ever touches its own folder. Passes without asserting
    // anything when WSL isn't available (xunit 2 has no runtime skip).
    [Fact]
    public void E2E_PnpmSymlinkNodeModules_DeletedInOneBatch_TargetsSurvive()
    {
        var repo = CreatePnpmShapedRepo();
        if (repo == null) return;
        var (linuxRoot, uncRoot) = repo.Value;
        try
        {
            var items = new[]
            {
                new CheckItem("a", 0, "REVIEW", $@"{uncRoot}\packages\a\node_modules", Action: ActionKind.MoveFolderToRecycleBin),
                new CheckItem("b", 0, "SAFE", $@"{uncRoot}\packages\b\node_modules", Action: ActionKind.DeleteFolder),
            };

            var results = ActionExecutor.ExecuteAll(items);

            Assert.All(results, r => Assert.True(r.Success, r.Message));
            Assert.All(results, r => Assert.Contains("batch of 2", r.Message));
            Assert.False(Directory.Exists(items[0].Path));
            Assert.False(Directory.Exists(items[1].Path));
            Assert.Equal("x", Wsl($"cat {linuxRoot}/store/lodash/index.js"));
        }
        finally
        {
            Wsl($"rm -rf {linuxRoot}");
        }
    }

    // Same throwaway repo, but scanned for real: the WSL scanner should fold both
    // node_modules into ONE row for the repo, and cleaning that one row should delete
    // both folders while leaving the repo folder, its .git, and the symlink targets alone.
    [Fact]
    public void E2E_ScannerFoldsRepoIntoOneRow_CleaningItDeletesOnlyItsFolders()
    {
        var repo = CreatePnpmShapedRepo();
        if (repo == null) return;
        var (linuxRoot, uncRoot) = repo.Value;
        try
        {
            var row = WindowsScanners.Wsl().SingleOrDefault(i => string.Equals(i.Path, uncRoot, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(row);
            Assert.Equal(2, row!.GroupPaths!.Count);
            Assert.Contains("2 build folders", row.Label);

            var result = Assert.Single(ActionExecutor.ExecuteAll(new[] { row }));

            Assert.True(result.Success, result.Message);
            Assert.Contains("all 2 folders", result.Message);
            Assert.All(row.GroupPaths, p => Assert.False(Directory.Exists(p)));
            Assert.Equal("ok", Wsl($"test -d {linuxRoot}/.git && test -f {linuxRoot}/packages/a/package.json && echo ok"));
            Assert.Equal("x", Wsl($"cat {linuxRoot}/store/lodash/index.js"));
        }
        finally
        {
            Wsl($"rm -rf {linuxRoot}");
        }
    }

    // Builds ~/diskcleanup-e2e-<guid>: a .git folder, two packages (with package.json +
    // pnpm-lock.yaml so the scanner classifies them SAFE) whose node_modules hold
    // absolute, relative and .bin symlinks into a shared "store" folder. Null when WSL
    // isn't available.
    static (string LinuxRoot, string UncRoot)? CreatePnpmShapedRepo()
    {
        var (distro, home) = (Wsl("echo $WSL_DISTRO_NAME"), Wsl("echo $HOME"));
        if (distro == null || home == null) return null;

        var name = "diskcleanup-e2e-" + Guid.NewGuid().ToString("N")[..8];
        var r = $"{home}/{name}";
        var setup = Wsl(
            $"mkdir -p {r}/.git {r}/store/lodash {r}/packages/a/node_modules/.bin {r}/packages/b/node_modules" +
            $" && echo x > {r}/store/lodash/index.js" +
            $" && for p in a b; do echo '{{}}' > {r}/packages/$p/package.json; touch {r}/packages/$p/pnpm-lock.yaml; done" +
            $" && ln -s {r}/store/lodash {r}/packages/a/node_modules/lodash" +
            $" && ln -s {r}/store/lodash/index.js {r}/packages/a/node_modules/.bin/lodash" +
            $" && ln -s ../../../store/lodash {r}/packages/b/node_modules/lodash && echo ok");
        Assert.Equal("ok", setup);

        return (r, $@"\\wsl.localhost\{distro}{home.Replace('/', '\\')}\{name}");
    }

    static string? Wsl(string script)
    {
        try
        {
            var psi = new ProcessStartInfo("wsl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in new[] { "--exec", "sh", "-c", script }) psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(30000);
            return proc.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch { return null; }
    }
}

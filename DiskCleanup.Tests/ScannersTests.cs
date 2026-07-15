using DiskCleanup.Core;

namespace DiskCleanup.Tests;

// All tests build a throwaway fake .claude/.codex-shaped tree under the OS
// temp directory - never against the real ~/.claude or ~/.codex.
public class ScannersTests
{
    static string CreateFakeClaudeTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_Claude_" + Guid.NewGuid());
        Directory.CreateDirectory(root);

        // Top-level files that must never be touched or offered.
        File.WriteAllText(Path.Combine(root, ".credentials.json"), "{}");
        File.WriteAllText(Path.Combine(root, "settings.json"), "{}");

        var snapshots = Path.Combine(root, "shell-snapshots");
        Directory.CreateDirectory(snapshots);
        File.WriteAllText(Path.Combine(snapshots, "snap.sh"), "echo hi");

        // A project with one stale session file and an adjacent memory/ dir
        // that must never appear in results.
        var project = Path.Combine(root, "projects", "fake-project");
        Directory.CreateDirectory(project);
        var staleFile = Path.Combine(project, "old.jsonl");
        File.WriteAllText(staleFile,
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"Help me clean up disk space please\"}}\n");
        File.SetLastWriteTime(staleFile, DateTime.Now.AddDays(-100));

        var memoryDir = Path.Combine(project, "memory");
        Directory.CreateDirectory(memoryDir);
        File.WriteAllText(Path.Combine(memoryDir, "should-never-appear.txt"), "persistent memory, never prune");

        // A project with only a memory/ dir, no loose session files.
        var memoryOnlyProject = Path.Combine(root, "projects", "memory-only-project");
        Directory.CreateDirectory(Path.Combine(memoryOnlyProject, "memory"));
        File.WriteAllText(Path.Combine(memoryOnlyProject, "memory", "notes.txt"), "keep");

        return root;
    }

    [Fact]
    public void ScanClaudeFolder_NeverReturnsRootItself()
    {
        var root = CreateFakeClaudeTree();
        try
        {
            var items = Scanners.ScanClaudeFolder(root);
            Assert.DoesNotContain(items, i => string.Equals(i.Path, root, StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ScanClaudeFolder_NeverReturnsMemoryContents()
    {
        var root = CreateFakeClaudeTree();
        try
        {
            var items = Scanners.ScanClaudeFolder(root);
            Assert.DoesNotContain(items, i => i.Path != null && i.Path.Contains("should-never-appear.txt"));
            Assert.DoesNotContain(items, i => i.Path != null &&
                i.Path.Contains($"{Path.DirectorySeparatorChar}memory{Path.DirectorySeparatorChar}"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ScanClaudeFolder_FindsOldSessionFileWithExcerptAndCorrectAction()
    {
        var root = CreateFakeClaudeTree();
        try
        {
            var items = Scanners.ScanClaudeFolder(root);
            var item = Assert.Single(items, i => i.Path != null && i.Path.EndsWith("old.jsonl"));

            Assert.Equal(ActionKind.MoveFileToRecycleBin, item.Action);
            Assert.Contains("Help me clean up disk space", item.Reason);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ScanClaudeFolder_ProjectWithOnlyMemory_ProducesNoItems()
    {
        var root = CreateFakeClaudeTree();
        try
        {
            var items = Scanners.ScanClaudeFolder(root);
            Assert.DoesNotContain(items, i => i.Label.Contains("memory-only-project"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ScanClaudeFolder_VeryRecentSessionFile_StillShown()
    {
        // No date filter anymore - a brand-new, never-revisited session must
        // show up just as readily as an old one (the whole point of dropping
        // the day-based threshold).
        var root = CreateFakeClaudeTree();
        try
        {
            var recentDir = Path.Combine(root, "projects", "recent-project");
            Directory.CreateDirectory(recentDir);
            var recentFile = Path.Combine(recentDir, "recent.jsonl");
            File.WriteAllText(recentFile, "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}\n");

            var items = Scanners.ScanClaudeFolder(root);
            Assert.Contains(items, i => i.Path == recentFile);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ScanClaudeFolder_ActiveSessionExcludedRegardlessOfAge()
    {
        var root = CreateFakeClaudeTree();
        try
        {
            const string activeSessionId = "11111111-2222-3333-4444-555555555555";

            var sessionsDir = Path.Combine(root, "sessions");
            Directory.CreateDirectory(sessionsDir);
            File.WriteAllText(Path.Combine(sessionsDir, "9999.json"),
                $"{{\"pid\":9999,\"sessionId\":\"{activeSessionId}\",\"status\":\"busy\"}}");

            var activeProject = Path.Combine(root, "projects", "active-project");
            Directory.CreateDirectory(activeProject);
            var activeFile = Path.Combine(activeProject, activeSessionId + ".jsonl");
            File.WriteAllText(activeFile, "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"in progress\"}}\n");
            File.SetLastWriteTime(activeFile, DateTime.Now.AddDays(-200)); // old write time must not matter

            var items = Scanners.ScanClaudeFolder(root);
            Assert.DoesNotContain(items, i => i.Path == activeFile);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ScanClaudeFolder_CascadesSessionFolderIntoMatchingJsonl()
    {
        var root = CreateFakeClaudeTree();
        try
        {
            var project = Path.Combine(root, "projects", "cascade-project");
            Directory.CreateDirectory(project);

            const string sessionId = "cascade-session";
            var jsonl = Path.Combine(project, sessionId + ".jsonl");
            File.WriteAllText(jsonl, "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}\n");

            var sessionDir = Path.Combine(project, sessionId);
            var subagents = Path.Combine(sessionDir, "subagents");
            Directory.CreateDirectory(subagents);
            File.WriteAllText(Path.Combine(subagents, "agent-a1.jsonl"), new string('x', 500));

            var items = Scanners.ScanClaudeFolder(root);

            var item = Assert.Single(items, i => i.Path == jsonl);
            Assert.Equal(sessionDir, item.SecondaryPath);
            Assert.True(item.SizeBytes > 500); // includes the subagents folder's bytes, not just the jsonl

            // The session folder must not also surface as its own orphan row.
            Assert.DoesNotContain(items, i => i.Path == sessionDir);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ScanClaudeFolder_OrphanedSessionFolder_FlaggedAsSafeRow()
    {
        var root = CreateFakeClaudeTree();
        try
        {
            var project = Path.Combine(root, "projects", "orphan-project");
            Directory.CreateDirectory(project);

            const string sessionId = "orphan-session";
            var sessionDir = Path.Combine(project, sessionId);
            var subagents = Path.Combine(sessionDir, "subagents");
            Directory.CreateDirectory(subagents);
            File.WriteAllText(Path.Combine(subagents, "agent-a1.jsonl"), new string('x', 500));
            // Deliberately no <sessionId>.jsonl next to it - conversation already deleted.

            var items = Scanners.ScanClaudeFolder(root);

            var item = Assert.Single(items, i => i.Path == sessionDir);
            Assert.Equal("SAFE", item.Risk);
            Assert.Equal(ActionKind.MoveFolderToRecycleBin, item.Action);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ScanClaudeFolder_ActiveSessionOrphanFolder_NotFlagged()
    {
        var root = CreateFakeClaudeTree();
        try
        {
            const string activeSessionId = "active-orphan-session";

            var sessionsDir = Path.Combine(root, "sessions");
            Directory.CreateDirectory(sessionsDir);
            File.WriteAllText(Path.Combine(sessionsDir, "8888.json"),
                $"{{\"pid\":8888,\"sessionId\":\"{activeSessionId}\",\"status\":\"busy\"}}");

            var project = Path.Combine(root, "projects", "active-orphan-project");
            var sessionDir = Path.Combine(project, activeSessionId);
            Directory.CreateDirectory(Path.Combine(sessionDir, "subagents"));
            File.WriteAllText(Path.Combine(sessionDir, "subagents", "agent-a1.jsonl"), "content");

            var items = Scanners.ScanClaudeFolder(root);

            Assert.DoesNotContain(items, i => i.Path == sessionDir);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    static string CreateFakeCodexTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_Codex_" + Guid.NewGuid());
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "auth.json"), "{}");

        var sandboxBin = Path.Combine(root, ".sandbox-bin");
        Directory.CreateDirectory(sandboxBin);
        File.WriteAllText(Path.Combine(sandboxBin, "tool.exe"), "binary");

        var memories = Path.Combine(root, "memories");
        Directory.CreateDirectory(memories);
        File.WriteAllText(Path.Combine(memories, "should-never-appear.txt"), "persistent, never prune");

        var sessionsDir = Path.Combine(root, "sessions", "2026", "06", "17");
        Directory.CreateDirectory(sessionsDir);
        var staleFile = Path.Combine(sessionsDir, "rollout-old.jsonl");
        File.WriteAllText(staleFile,
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"<environment_context>noise</environment_context>\"}]}}\n" +
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"Summarize this codebase for me\"}]}}\n");
        File.SetLastWriteTime(staleFile, DateTime.Now.AddDays(-100));

        return root;
    }

    [Fact]
    public void ScanCodexFolder_NeverReturnsRootOrSandboxOrMemories()
    {
        var root = CreateFakeCodexTree();
        try
        {
            var items = Scanners.ScanCodexFolder(root);
            Assert.DoesNotContain(items, i => string.Equals(i.Path, root, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(items, i => i.Path != null && i.Path.Contains(".sandbox-bin"));
            Assert.DoesNotContain(items, i => i.Path != null && i.Path.Contains("should-never-appear.txt"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ScanCodexFolder_FindsOldSessionSkippingEnvironmentContextNoise()
    {
        var root = CreateFakeCodexTree();
        try
        {
            var items = Scanners.ScanCodexFolder(root);
            var item = Assert.Single(items, i => i.Path != null && i.Path.EndsWith("rollout-old.jsonl"));

            Assert.Equal(ActionKind.MoveFileToRecycleBin, item.Action);
            Assert.Contains("Summarize this codebase", item.Reason);
            Assert.DoesNotContain("environment_context", item.Reason);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("Microsoft.VCLibs.140.00_8wekyb3d8bbwe", true)]
    [InlineData("Microsoft.WindowsAppRuntime.1.4_8wekyb3d8bbwe", true)]
    [InlineData("AdobeAcrobatReaderCoreApp_pc75e8sa7ep4e", false)]
    [InlineData("45442stefano64.GPXviewerandrecorder_bszswgksnzmf2", false)]
    public void IsSharedRuntimePackage_MatchesKnownFrameworkPrefixesOnly(string folderName, bool expected)
    {
        Assert.Equal(expected, Scanners.IsSharedRuntimePackage(folderName));
    }

    [Fact]
    public void ScanCodexFolder_VeryRecentlyModifiedFile_ExcludedAsLikelyActive()
    {
        // Codex has no live-PID-to-session mapping to check precisely (unlike
        // Claude's sessions/*.json), so anything modified within the last
        // ~30 minutes is treated as possibly still being written to.
        var root = CreateFakeCodexTree();
        try
        {
            var sessionsDir = Path.Combine(root, "sessions", "2026", "06", "22");
            Directory.CreateDirectory(sessionsDir);
            var freshFile = Path.Combine(sessionsDir, "rollout-fresh.jsonl");
            File.WriteAllText(freshFile,
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"just started\"}]}}\n");
            // LastWriteTime defaults to now.

            var items = Scanners.ScanCodexFolder(root);
            Assert.DoesNotContain(items, i => i.Path == freshFile);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // --- FindBuildDirs / HasProjectMarker (WSL whole-home walk safety fix) ---

    [Fact]
    public void FindBuildDirs_AppServerDirIsExcludedAndNeverWalkedInto()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_Wsl_" + Guid.NewGuid());
        var vscodeServer = Path.Combine(root, ".vscode-server", "extensions", "copilot", "node_modules");
        Directory.CreateDirectory(vscodeServer);
        File.WriteAllText(Path.Combine(vscodeServer, "index.js"), "// bundled extension code");
        try
        {
            var scan = Scanners.FindBuildDirs(root);

            Assert.Contains(scan.ExcludedAppDataDirs, p => p.EndsWith(".vscode-server"));
            // Never recursed into it, so the node_modules inside must not surface as a build dir.
            Assert.DoesNotContain(scan.BuildDirs, p => p.Contains("node_modules"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FindBuildDirs_SkipsCacheAndNpmDirsAlreadyScannedElsewhere()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_Wsl_" + Guid.NewGuid());
        var buriedInCache = Path.Combine(root, ".cache", "some-tool", "node_modules");
        Directory.CreateDirectory(buriedInCache);
        try
        {
            var scan = Scanners.FindBuildDirs(root);

            Assert.DoesNotContain(scan.BuildDirs, p => p.Contains("node_modules"));
            Assert.DoesNotContain(scan.ExcludedAppDataDirs, p => p.EndsWith(".cache"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FindBuildDirs_FindsOrdinaryProjectBuildDirs()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_Wsl_" + Guid.NewGuid());
        var nodeProject = Path.Combine(root, "projects", "my-app");
        Directory.CreateDirectory(Path.Combine(nodeProject, "node_modules"));
        var rustProject = Path.Combine(root, "projects", "rust-app");
        Directory.CreateDirectory(Path.Combine(rustProject, "target"));
        try
        {
            var scan = Scanners.FindBuildDirs(root);

            Assert.Contains(scan.BuildDirs, p => p == Path.Combine(nodeProject, "node_modules"));
            Assert.Contains(scan.BuildDirs, p => p == Path.Combine(rustProject, "target"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void HasProjectMarker_NodeModulesWithPackageJson_ReturnsTrue()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_Wsl_" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), "{}");
        try { Assert.True(Scanners.HasProjectMarker(root, "node_modules")); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void HasProjectMarker_NodeModulesWithoutPackageJson_ReturnsFalse()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_Wsl_" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try { Assert.False(Scanners.HasProjectMarker(root, "node_modules")); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("Cargo.toml")]
    [InlineData("pom.xml")]
    [InlineData("build.sbt")]
    public void HasProjectMarker_TargetWithAnyKnownBuildFile_ReturnsTrue(string markerFile)
    {
        var root = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_Wsl_" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, markerFile), "");
        try { Assert.True(Scanners.HasProjectMarker(root, "target")); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void HasProjectMarker_TargetWithNoKnownBuildFile_ReturnsFalse()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_Wsl_" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try { Assert.False(Scanners.HasProjectMarker(root, "target")); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("package-lock.json")]
    [InlineData("pnpm-lock.yaml")]
    [InlineData("yarn.lock")]
    public void HasLockfile_WithAnyKnownLockfile_ReturnsTrue(string lockfileName)
    {
        var root = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_Lockfile_" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, lockfileName), "");
        try { Assert.True(Scanners.HasLockfile(root)); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void HasLockfile_NoLockfile_ReturnsFalse()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_Lockfile_" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try { Assert.False(Scanners.HasLockfile(root)); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void IsWorkspaceRoot_PackageJsonWithWorkspacesField_ReturnsTrue()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_Workspace_" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), "{\"workspaces\": [\"packages/*\"]}");
        try { Assert.True(Scanners.IsWorkspaceRoot(root)); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void IsWorkspaceRoot_PnpmWorkspaceYamlSibling_ReturnsTrue()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_Workspace_" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "pnpm-workspace.yaml"), "packages:\n  - 'packages/*'\n");
        try { Assert.True(Scanners.IsWorkspaceRoot(root)); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void IsWorkspaceRoot_PlainPackageJson_ReturnsFalse()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_Workspace_" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), "{\"name\": \"my-app\"}");
        try { Assert.False(Scanners.IsWorkspaceRoot(root)); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void IsWorkspaceRoot_MalformedPackageJson_ReturnsFalse()
    {
        // A malformed package.json isn't a workspace signal either way - it
        // shouldn't crash the scan (see ClassifyBuildDir's JsonException catch).
        var root = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_Workspace_" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), "{not valid json");
        try { Assert.False(Scanners.IsWorkspaceRoot(root)); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FindNativeBuildDirs_FindsOrdinaryProjectBuildDirs()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_Native_" + Guid.NewGuid());
        var nodeProject = Path.Combine(root, "projects", "my-app");
        Directory.CreateDirectory(Path.Combine(nodeProject, "node_modules"));
        var rustProject = Path.Combine(root, "projects", "rust-app");
        Directory.CreateDirectory(Path.Combine(rustProject, "target"));
        try
        {
            var buildDirs = Scanners.FindNativeBuildDirs(root);

            Assert.Contains(buildDirs, p => p == Path.Combine(nodeProject, "node_modules"));
            Assert.Contains(buildDirs, p => p == Path.Combine(rustProject, "target"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("AppData")]
    [InlineData("Program Files")]
    [InlineData("Windows")]
    public void FindNativeBuildDirs_NeverDescendsIntoOsVendorDirs(string vendorDirName)
    {
        var root = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_Native_" + Guid.NewGuid());
        var bundledNodeModules = Path.Combine(root, vendorDirName, "SomeApp", "resources", "node_modules");
        Directory.CreateDirectory(bundledNodeModules);
        try
        {
            var buildDirs = Scanners.FindNativeBuildDirs(root);

            Assert.DoesNotContain(buildDirs, p => p.Contains("node_modules"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FindNativeBuildDirs_DoesNotFollowJunctionsWhileDiscovering()
    {
        // Junctions (unlike symlinks) need no special privilege to create,
        // so this runs the real guard logic on every machine.
        var root = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_Native_" + Guid.NewGuid());
        var target = Path.Combine(Path.GetTempPath(), "DiskCleanupTests_NativeLinkTarget_" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(target, "node_modules"));
        Directory.CreateDirectory(root);

        var junction = Path.Combine(root, "linked-project");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(5000);
        Assert.True(Directory.Exists(junction), $"mklink /J failed to create the junction: {proc.StandardError.ReadToEnd()}");

        try
        {
            var buildDirs = Scanners.FindNativeBuildDirs(root);

            Assert.DoesNotContain(buildDirs, p => p.Contains("node_modules"));
        }
        finally
        {
            // Remove the junction itself (not recursively - that would
            // follow it into target) before cleaning up both directories.
            Directory.Delete(junction, recursive: false);
            Directory.Delete(root, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }
}

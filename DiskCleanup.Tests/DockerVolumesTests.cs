using System.Diagnostics;
using DiskCleanup.Core;

namespace DiskCleanup.Tests;

public class DockerVolumesTests
{
    // Trimmed copy of real `docker system df -v --format "{{json .Volumes}}"`
    // output: one anonymous unused, one compose-project database unused, one in use.
    const string SampleJson = """
        [
          {"Driver":"local","Labels":"com.docker.volume.anonymous=","Links":"0","Name":"060de1dd6a8bf01a17c1b19c6ce5a1077cf5944effbdbe22cf00704c1187c9c8","Size":"2.429MB"},
          {"Driver":"local","Labels":"com.docker.compose.project=hairlosspro,com.docker.compose.volume=pgdata","Links":"0","Name":"hairlosspro_pgdata","Size":"48.2MB"},
          {"Driver":"local","Labels":"","Links":"1","Name":"kanboard_agents_bot_plugins","Size":"0B"}
        ]
        """;

    [Fact]
    public void Parse_UnusedVolume_GetsRunDockerRemoveAction()
    {
        var items = Scanners.ParseDockerVolumes(SampleJson);

        var pg = items.Single(i => i.Label == "Docker volume hairlosspro_pgdata");
        Assert.Equal("REVIEW", pg.Risk);
        Assert.Equal(ActionKind.RunDocker, pg.Action);
        Assert.Equal("docker volume rm hairlosspro_pgdata", pg.CommandSuggestion);
        Assert.Equal("48.2MB", pg.FormattedSize);
        Assert.Contains("hairlosspro", pg.Reason);
        Assert.Contains("database", pg.Reason);
        Assert.Contains("permanent", pg.Reason);
    }

    [Fact]
    public void Parse_AnonymousVolume_ShortensLabelButRemovesByFullName()
    {
        var items = Scanners.ParseDockerVolumes(SampleJson);

        var anon = items.Single(i => i.Label.Contains("anonymous"));
        Assert.Equal("Docker volume (anonymous) 060de1dd6a8b", anon.Label);
        Assert.Equal("docker volume rm 060de1dd6a8bf01a17c1b19c6ce5a1077cf5944effbdbe22cf00704c1187c9c8", anon.CommandSuggestion);
    }

    [Fact]
    public void Parse_InUseVolume_IsInfoOnlyWithNoAction()
    {
        var items = Scanners.ParseDockerVolumes(SampleJson);

        var inUse = items.Single(i => i.Label == "Docker volume kanboard_agents_bot_plugins");
        Assert.Equal("INFO", inUse.Risk);
        Assert.Equal(ActionKind.None, inUse.Action);
    }

    [Fact]
    public void Parse_EmptyArray_ReturnsNoItems()
    {
        Assert.Empty(Scanners.ParseDockerVolumes("[]"));
    }

    // End-to-end against the real Docker daemon: creates a throwaway volume,
    // checks the scanner lists it as removable, removes it via the RunDocker
    // action, and checks it's really gone. Only ever touches its own
    // "diskcleanup-e2e-<guid>" volume. Passes without asserting anything when
    // Docker isn't running (xunit 2 has no runtime skip).
    [Fact]
    public void E2E_ScanThenRemove_DeletesRealVolume()
    {
        if (RunDockerCli("info").ExitCode != 0) return;

        var name = "diskcleanup-e2e-" + Guid.NewGuid().ToString("N")[..8];
        Assert.Equal(0, RunDockerCli($"volume create {name}").ExitCode);
        try
        {
            var item = Scanners.DockerVolumes().SingleOrDefault(i => i.Label == $"Docker volume {name}");
            Assert.NotNull(item);
            Assert.Equal(ActionKind.RunDocker, item!.Action);

            var result = ActionExecutor.Execute(item);

            Assert.True(result.Success, result.Message);
            Assert.DoesNotContain(name, RunDockerCli("volume ls -q").Output);
        }
        finally
        {
            RunDockerCli($"volume rm {name}"); // no-op if the test already removed it
        }
    }

    static (int ExitCode, string Output) RunDockerCli(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("docker", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(30000);
            return (proc.ExitCode, output);
        }
        catch { return (-1, ""); }
    }
}

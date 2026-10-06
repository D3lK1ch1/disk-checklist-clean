using System.Diagnostics;
using System.Text;

namespace DiskCleanup.Core;

// Deletes WSL folders from *inside* the distro. Deleting through \\wsl.localhost\
// from Windows can't remove Linux symlinks (pnpm node_modules are mostly symlinks),
// so it fails with "The directory is not empty". rm inside Linux handles them, and
// only ever removes the link itself, never its target.
static class WslBatchDelete
{
    // A whole monorepo's node_modules in one call can take a while on a cold 9P cache.
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    // Runs one `wsl.exe -d <distro> --exec xargs -0 -r rm -rf --` for the whole batch.
    // Paths go over stdin NUL-separated: no shell is involved (--exec), so spaces/quotes
    // in paths can't be misread, and the Windows command-line length limit never applies
    // however many folders are in the batch.
    //
    // The exit code is not trusted per item - after the run, each folder is checked for
    // existence, so every item still gets its own OK/FAILED.
    public static List<ActionResult> Run(string distro, string groupLabel, IReadOnlyList<(CheckItem Item, WslPath Wsl)> batch)
    {
        var (output, error) = RunWsl(distro, batch.Select(b => b.Wsl.LinuxPath));
        var outputLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var batchNote = batch.Count > 1 ? $" (batch of {batch.Count} in {groupLabel})" : "";

        var results = new List<ActionResult>();
        foreach (var (item, wsl) in batch)
        {
            if (!Directory.Exists(item.Path))
            {
                results.Add(new ActionResult(item, true,
                    $"Permanently deleted inside WSL{batchNote} - cannot be undone." + ActionExecutor.WslCompactionNote(item.Path)));
                continue;
            }

            var mine = outputLines.Where(l => l.Contains(wsl.LinuxPath, StringComparison.Ordinal)).Take(5).ToList();
            var detail = error ?? (mine.Count > 0
                ? string.Join(Environment.NewLine, mine)
                : string.Join(Environment.NewLine, outputLines.Take(5)));
            if (string.IsNullOrWhiteSpace(detail)) detail = "folder still exists after rm, no error output.";

            // Root-owned files (e.g. created by a Docker container) need root inside WSL.
            var asRoot = detail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ? "-u root " : "";
            results.Add(new ActionResult(item, false,
                $"Could not delete inside WSL. {detail}{Environment.NewLine}" +
                $"Run yourself: wsl -d {distro} {asRoot}-- rm -rf '{wsl.LinuxPath}'"));
        }
        return results;
    }

    // Returns combined stdout+stderr, plus an error string only when wsl.exe itself
    // couldn't run or timed out (rm failures show up in output, not here).
    static (string Output, string? Error) RunWsl(string distro, IEnumerable<string> linuxPaths)
    {
        var psi = new ProcessStartInfo("wsl")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // wsl.exe's own messages (e.g. "no distribution with the supplied name") are
        // UTF-16 by default, which garbles when read as UTF-8.
        psi.Environment["WSL_UTF8"] = "1";
        foreach (var arg in new[] { "-d", distro, "--exec", "xargs", "-0", "-r", "rm", "-rf", "--" })
            psi.ArgumentList.Add(arg);

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return ("", "Could not start wsl.exe.");

            // Start reading before writing stdin - see RunDocker's deadlock note.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            foreach (var path in linuxPaths)
            {
                proc.StandardInput.Write(path);
                proc.StandardInput.Write('\0');
            }
            proc.StandardInput.Close();

            if (!proc.WaitForExit(Timeout))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (stdout.IsCompleted ? stdout.Result : "", $"Timed out after {Timeout.TotalMinutes:0} minutes.");
            }

            return ((stdout.Result + stderr.Result).Trim(), null);
        }
        catch (Exception ex)
        {
            return ("", $"Could not run wsl.exe - is WSL installed? ({ex.Message})");
        }
    }
}

namespace DiskCleanup.Core;

public record WslPath(string Distro, string LinuxPath);

public static class WslPaths
{
    const string Prefix = @"\\wsl.localhost\";

    // Remote dev-server folders (see WindowsScanners.WslAppServerDirNames and project
    // memory: WSL cleanup corrupting VS Code Server). Duplicated here as a last line of
    // defence - anything handed to `wsl rm -rf` must never land inside one of these,
    // even if a future scanner change forgets to exclude it.
    static readonly string[] ProtectedDirNames =
    {
        ".vscode-server",
        ".vscode-server-insiders",
        ".cursor-server",
        ".windsurf-server",
    };

    // Converts \\wsl.localhost\<distro>\home\<user>\<rest...> into the distro name and
    // the Linux path /home/<user>/<rest...>. Returns null (refuses) for anything that
    // isn't safely deletable from inside the distro:
    //   - not a \\wsl.localhost\ path
    //   - not under /home/<user>/ with at least one segment below the user's home
    //     (so the home folder itself can never be targeted)
    //   - contains "." / ".." segments, or passes through a protected dev-server folder
    public static WslPath? TryParse(string? uncPath)
    {
        if (uncPath == null || !uncPath.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var segments = uncPath[Prefix.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);

        // distro, "home", user, at least one more
        if (segments.Length < 4) return null;
        if (!segments[1].Equals("home", StringComparison.Ordinal)) return null;
        if (segments.Any(s => s is "." or "..")) return null;
        if (segments.Any(s => ProtectedDirNames.Contains(s, StringComparer.OrdinalIgnoreCase))) return null;

        return new WslPath(segments[0], "/" + string.Join('/', segments.Skip(1)));
    }

    // Nearest ancestor of uncPath that contains a .git entry, searching only folders
    // strictly below the user's home (so a dotfiles repo at ~ doesn't lump every WSL
    // item into one batch). Returns null when there is none - the caller then gives
    // the item a batch of its own. hasGit is injected so tests don't need a real distro.
    public static string? FindProjectRoot(string uncPath, Func<string, bool> hasGit)
    {
        if (!uncPath.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var segments = uncPath[Prefix.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);

        // segments[0..2] = distro, "home", user - ancestors start at length 4 (~/<x>).
        for (int len = segments.Length - 1; len >= 4; len--)
        {
            var dir = Prefix + string.Join('\\', segments.Take(len));
            if (hasGit(dir)) return dir;
        }
        return null;
    }
}

using DiskCleanup.Core;

namespace DiskCleanup.Avalonia;

// Runtime OS branch for the two things Core can't do portably: which
// ITrashProvider to install, and which scanners are safe to run. Branches on
// WINDOWS_BUILD, a symbol this project's own .csproj defines for the
// net10.0-windows TFM only - the SDK does NOT auto-define a per-TFM symbol
// (verified via `dotnet msbuild -getProperty:DefineConstants`, both TFMs
// return just TRACE;DEBUG), so relying on an assumed one like NET10_0_WINDOWS
// silently always takes the #else branch on every build. Only the Windows
// branch is reachable today - the project still only ships a
// net10.0-windows exe (see DiskCleanup.Avalonia.csproj) - but it's written
// against the plain-net10.0 TFM too so the Mac branch compiles now and just
// needs a net10.0 apphost/publish profile to actually run, not a code change.
public static class PlatformSetup
{
    public static void ConfigureTrashProvider()
    {
#if WINDOWS_BUILD
        ActionExecutor.TrashProvider = new WindowsTrashProvider();
#else
        ActionExecutor.TrashProvider = new MacTrashProvider();
#endif
    }

    public static List<CheckItem> RunScanners(List<string> warnings)
    {
        var result = new List<CheckItem>();

#if WINDOWS_BUILD
        result.AddRange(WindowsScanners.RecycleBin(warnings));
        result.AddRange(Scanners.TempFolders());
        result.AddRange(WindowsScanners.WindowsUpdateCache());
        result.AddRange(WindowsScanners.WindowsTempFolder(warnings));
        result.AddRange(Scanners.VsCodeCache(warnings));
        result.AddRange(Scanners.DevPackageCaches(warnings));
        result.AddRange(WindowsScanners.Wsl(warnings));
        result.AddRange(Scanners.NativeBuildDirs(warnings));
        result.AddRange(Scanners.Docker(warnings));
        result.AddRange(WindowsScanners.DockerVhdxBloat(warnings: warnings));
        result.AddRange(WindowsScanners.SystemRootClutter(warnings: warnings));
        result.AddRange(Scanners.DownloadsTopFolders(warnings: warnings));
        result.AddRange(WindowsScanners.StalePackages(warnings: warnings));
        result.AddRange(WindowsScanners.RoamingAppData(warnings: warnings));
        result.AddRange(Scanners.AiFolders(warnings));
        result.AddRange(WindowsScanners.InstalledAppsBySize(warnings: warnings));
        result.AddRange(Scanners.PersonalFolders(warnings: warnings));
#else
        // No Mac-specific scanners exist yet (see SPEC.md's per-scanner scope
        // table) - only Category A (already-portable) scanners run here.
        result.AddRange(Scanners.TempFolders());
        result.AddRange(Scanners.VsCodeCache(warnings));
        result.AddRange(Scanners.DevPackageCaches(warnings));
        result.AddRange(Scanners.NativeBuildDirs(warnings));
        result.AddRange(Scanners.Docker(warnings));
        result.AddRange(Scanners.DownloadsTopFolders(warnings: warnings));
        result.AddRange(Scanners.AiFolders(warnings));
        result.AddRange(Scanners.PersonalFolders(warnings: warnings));
#endif

        return result;
    }
}

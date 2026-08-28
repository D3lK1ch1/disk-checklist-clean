using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DiskCleanup.Core;

namespace DiskCleanup.Core.Windows;

public static class WindowsScanners
{
    // ... existing code ...

    public static List<CheckItem> UserDotfolders(List<string>? warnings = null)
    {
        var items = new List<CheckItem>();
        var userDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Directory.Exists(userDir)) return items;

        try
        {
            var dotfolders = new[] { ".cargo", ".cisco", ".cursor" };
            foreach (var dotfolder in dotfolders)
            {
                var path = System.IO.Path.Combine(userDir, dotfolder);
                if (!Directory.Exists(path)) continue;

                var size = Scanners.GetDirectorySize(path);
                var reason = dotfolder switch
                {
                    ".cargo" => "Cargo toolchain folder. If cargo.exe is found on PATH, this folder is likely still in use. REVIEW risk, do not offer as SAFE.",
                    ".cursor" => "Cursor app folder. This folder may contain real authored data even if the app itself was uninstalled. REVIEW risk.",
                    ".cisco" => "No reliable install cross-check implemented for this one yet. REVIEW risk.",
                    _ => "Unknown dotfolder. REVIEW risk."
                };

                items.Add(new CheckItem($"{dotfolder} folder", size, "REVIEW", path,
                    Action: ActionKind.MoveFolderToRecycleBin, Reason: reason));
            }
        }
        catch (Exception ex) { warnings?.Add($"User dotfolders scan: could not check {dotfolder} ({ex.Message})"); }
        return items;
    }
}

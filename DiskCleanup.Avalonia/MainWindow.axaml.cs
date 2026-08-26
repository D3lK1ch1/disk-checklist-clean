using Avalonia.Controls;
using Avalonia.Interactivity;
using DiskCleanup.Core;

namespace DiskCleanup.Avalonia;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private async void ScanButton_Click(object? sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        StatusText.Text = "Scanning...";

        var warnings = new List<string>();

        var items = await Task.Run(() =>
        {
            var result = new List<CheckItem>();
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
            return result;
        });

        ItemsList.ItemsSource = items
            .Select(i => $"{i.Label} — {i.FormattedSize} — {i.Risk}")
            .ToList();

        StatusText.Text = warnings.Count > 0
            ? $"{items.Count} items found ({warnings.Count} warning(s))."
            : $"{items.Count} items found.";
        ScanButton.IsEnabled = true;
    }
}

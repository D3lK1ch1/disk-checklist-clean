using Avalonia.Controls;
using Avalonia.Interactivity;
using DiskCleanup.Core;

namespace DiskCleanup.Avalonia;

public partial class MainWindow : Window
{
    // Wraps a CheckItem so the plain ListBox (no DataTemplate yet) can display it via
    // ToString() while still letting SelectionChanged get back the underlying item.
    private record ScanRow(CheckItem Item)
    {
        public override string ToString() => $"{Item.Label} — {Item.FormattedSize} — {Item.Risk}";
    }

    private List<CheckItem> _allItems = new();

    public MainWindow()
    {
        InitializeComponent();
        RiskFilterCombo.SelectedIndex = 0;
        UpdateFreeSpaceText();
    }

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

        _allItems = items;
        ApplyFilter();
        UpdateFreeSpaceText();

        StatusText.Text = warnings.Count > 0
            ? $"{_allItems.Count} items found ({warnings.Count} warning(s) - see log below)."
            : $"{_allItems.Count} items found.";
        LogBox.Text = warnings.Count > 0
            ? "Warnings (some categories may be incomplete):\n" + string.Join("\n", warnings.Select(w => $"- {w}"))
            : "";
        ScanButton.IsEnabled = true;
    }

    private void RiskFilterCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ItemsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ItemsList.SelectedItem is not ScanRow row)
        {
            DetailsText.Text = "Select an item to see its full path and why it's categorized that way.";
            return;
        }

        var details = row.Item.Label;
        if (!string.IsNullOrWhiteSpace(row.Item.Path))
            details += $"\n\nFull path: {row.Item.Path}";
        if (!string.IsNullOrWhiteSpace(row.Item.Reason))
            details += $"\n\n{row.Item.Reason}";
        DetailsText.Text = details;
    }

    private void ApplyFilter()
    {
        var selected = (RiskFilterCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "All";
        var filtered = selected == "All" ? _allItems : _allItems.Where(i => i.Risk == selected);
        ItemsList.ItemsSource = filtered.Select(i => new ScanRow(i)).ToList();
    }

    private void UpdateFreeSpaceText()
    {
        var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!;
        var free = new DriveInfo(systemDrive).AvailableFreeSpace;
        FreeSpaceText.Text = $"Free space on {systemDrive}: {FormatBytes(free)}";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#}{units[unit]}";
    }
}

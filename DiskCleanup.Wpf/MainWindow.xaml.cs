using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DiskCleanup.Core;

namespace DiskCleanup.Wpf;

public partial class MainWindow : Window
{
    private List<CheckItemViewModel> _allItems = new();
    private readonly ObservableCollection<CheckItemViewModel> _visibleItems = new();

    public MainWindow()
    {
        InitializeComponent();
        ItemsGrid.ItemsSource = _visibleItems;
        UpdateFreeSpaceText();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        StatusText.Text = "Scanning...";
        LogBox.Clear();

        var warnings = new List<string>();

        var items = await Task.Run(() =>
        {
            var result = new List<CheckItem>();
            result.AddRange(Scanners.RecycleBin(warnings));
            result.AddRange(Scanners.TempFolders());
            result.AddRange(Scanners.WindowsUpdateCache());
            result.AddRange(Scanners.WindowsTempFolder(warnings));
            result.AddRange(Scanners.VsCodeCache(warnings));
            result.AddRange(Scanners.DevPackageCaches(warnings));
            result.AddRange(Scanners.Wsl(warnings));
            result.AddRange(Scanners.NativeBuildDirs(warnings));
            result.AddRange(Scanners.Docker(warnings));
            result.AddRange(Scanners.DockerVhdxBloat(warnings: warnings));
            result.AddRange(Scanners.DownloadsTopFolders(warnings: warnings));
            result.AddRange(Scanners.StalePackages(warnings: warnings));
            result.AddRange(Scanners.AiFolders(warnings));
            result.AddRange(Scanners.InstalledAppsBySize(warnings: warnings));
            result.AddRange(Scanners.PersonalFolders(warnings: warnings));
            return result;
        });

        _allItems = items.Select(i => new CheckItemViewModel(i)).ToList();
        ApplyFilter();
        UpdateFreeSpaceText();

        if (warnings.Count > 0)
        {
            StatusText.Text = $"{_allItems.Count} items found ({warnings.Count} warning(s) — see log below).";
            LogBox.Text = "Warnings (some categories may be incomplete):\n" +
                string.Join("\n", warnings.Select(w => $"- {w}"));
        }
        else
        {
            StatusText.Text = $"{_allItems.Count} items found.";
        }
        ScanButton.IsEnabled = true;
    }

    private void RiskFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void ItemsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsGrid.SelectedItem is not CheckItemViewModel vm)
        {
            DetailsText.Text = "Select an item to see its full path and why it's categorized that way.";
            return;
        }

        var details = vm.Label;
        if (!string.IsNullOrWhiteSpace(vm.Item.Path))
            details += $"\n\nFull path: {vm.Item.Path}";
        if (!string.IsNullOrWhiteSpace(vm.Reason))
            details += $"\n\n{vm.Reason}";
        DetailsText.Text = details;
    }

    private void ApplyFilter()
    {
        var selected = (RiskFilterCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "All";

        _visibleItems.Clear();
        foreach (var vm in _allItems)
        {
            if (selected == "All" || vm.Risk == selected)
                _visibleItems.Add(vm);
        }
    }

    private void ItemCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: CheckItemViewModel vm } cb)
            vm.IsSelected = cb.IsChecked == true;
    }

    private void SelectAllSafeButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var vm in _allItems)
        {
            if (vm.Risk == "SAFE" && vm.CanSelect)
                vm.IsSelected = true;
        }
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var vm in _allItems)
            vm.IsSelected = false;
    }

    private async void CleanButton_Click(object sender, RoutedEventArgs e)
    {
        // Flush any pending checkbox edit in the grid before reading IsSelected —
        // a click can register visually without committing to the bound source
        // until the cell/row edit is explicitly committed.
        ItemsGrid.CommitEdit();

        var selected = _allItems.Where(vm => vm.IsSelected && vm.CanSelect).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "Nothing selected.";
            return;
        }

        var message = "The following items will be processed:\n\n" +
            string.Join("\n", selected.Select(vm => $"- {vm.Label} ({vm.Size}, {vm.Risk})")) +
            "\n\nProceed?";

        var confirm = MessageBox.Show(message, "Confirm cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            StatusText.Text = "Cancelled. Nothing was changed.";
            return;
        }

        CleanButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        StatusText.Text = $"Cleaning {selected.Count} item(s)...";

        var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!;
        var freeBefore = new DriveInfo(systemDrive).AvailableFreeSpace;

        // The actual delete/move work (file I/O, SHFileOperation, process
        // spawns for SuggestCommand-less actions) runs off the UI thread so
        // large cleanups don't freeze the window. Touching _allItems/
        // _visibleItems must stay on the UI thread, so that happens after.
        var results = await Task.Run(() =>
            selected.Select(vm => (Vm: vm, Result: ActionExecutor.Execute(vm.Item))).ToList());

        var log = new System.Text.StringBuilder();
        foreach (var (vm, result) in results)
        {
            var status = result.Success ? "OK" : "FAILED";
            log.AppendLine($"[{status}] {vm.Label}: {result.Message}");

            // Remove cleaned (non-informational) items from the list so the grid reflects the new state.
            if (result.Success && vm.Item.Action != ActionKind.SuggestCommand && vm.Item.Action != ActionKind.None)
            {
                _allItems.Remove(vm);
                _visibleItems.Remove(vm);
            }
        }

        var freeAfter = new DriveInfo(systemDrive).AvailableFreeSpace;
        log.AppendLine();
        log.AppendLine($"Free space on {systemDrive} before: {FormatBytes(freeBefore)}");
        log.AppendLine($"Free space on {systemDrive} after:  {FormatBytes(freeAfter)}");
        log.AppendLine($"Difference: {FormatBytes(freeAfter - freeBefore)}");

        LogBox.Text = log.ToString();
        StatusText.Text = "Done.";
        CleanButton.IsEnabled = true;
        ScanButton.IsEnabled = true;
        UpdateFreeSpaceText();
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
        var sign = size < 0 ? "-" : "";
        size = Math.Abs(size);
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{sign}{size:0.#}{units[unit]}";
    }
}

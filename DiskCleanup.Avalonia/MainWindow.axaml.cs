using Avalonia.Controls;
using Avalonia.Interactivity;
using DiskCleanup.Core;

namespace DiskCleanup.Avalonia;

public partial class MainWindow : Window
{
    private List<CheckItemViewModel> _allItems = new();

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

        _allItems = items.Select(i => new CheckItemViewModel(i)).ToList();
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

    private void ItemsGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
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
        var filtered = selected == "All" ? _allItems : _allItems.Where(vm => vm.Risk == selected);

        // Reassigning ItemsSource (instead of mutating a persistent ObservableCollection in
        // place) forces the DataGrid to fully rebind every call - mutating in place left the
        // grid blank after the very first Scan (empty-to-populated transition never rendered,
        // confirmed by a rescan working every time after that).
        ItemsGrid.ItemsSource = filtered.ToList();
    }

    private void ItemCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: CheckItemViewModel vm } cb)
            vm.IsSelected = cb.IsChecked == true;
    }

    private void SelectAllSafeButton_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var vm in _allItems)
        {
            if (vm.Risk == "SAFE" && vm.CanSelect)
                vm.IsSelected = true;
        }
    }

    private void ClearSelectionButton_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var vm in _allItems)
            vm.IsSelected = false;
    }

    private async void CleanButton_Click(object? sender, RoutedEventArgs e)
    {
        // No ItemsGrid.CommitEdit() here (unlike WPF) - Avalonia's DataGrid has no such
        // method, and none is needed: ItemCheckBox_Click already writes vm.IsSelected
        // directly and synchronously, not through a deferred row/cell edit commit.
        var selected = _allItems.Where(vm => vm.IsSelected && vm.CanSelect).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "Nothing selected.";
            return;
        }

        var message = "The following items will be processed:\n\n" +
            string.Join("\n", selected.Select(vm => $"- {vm.Label} ({vm.Size}, {vm.Risk})")) +
            "\n\nProceed?";

        var confirmed = await new ConfirmDialog(message).ShowDialog<bool>(this);
        if (!confirmed)
        {
            StatusText.Text = "Cancelled. Nothing was changed.";
            return;
        }

        CleanButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        StatusText.Text = $"Cleaning {selected.Count} item(s)...";

        var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!;
        var freeBefore = new DriveInfo(systemDrive).AvailableFreeSpace;

        var results = await Task.Run(() =>
            selected.Select(vm => (Vm: vm, Result: ActionExecutor.Execute(vm.Item))).ToList());

        var log = new System.Text.StringBuilder();
        foreach (var (vm, result) in results)
        {
            var status = result.Success ? "OK" : "FAILED";
            log.AppendLine($"[{status}] {vm.Label}: {result.Message}");

            // Remove cleaned (non-informational) items from _allItems, then let ApplyFilter()
            // regenerate ItemsGrid.ItemsSource - Avalonia has no persistent _visibleItems
            // collection to remove from directly (see ApplyFilter()'s reassignment note).
            if (result.Success && vm.Item.Action != ActionKind.SuggestCommand && vm.Item.Action != ActionKind.None)
                _allItems.Remove(vm);
        }

        var freeAfter = new DriveInfo(systemDrive).AvailableFreeSpace;
        log.AppendLine();
        log.AppendLine($"Free space on {systemDrive} before: {FormatBytes(freeBefore)}");
        log.AppendLine($"Free space on {systemDrive} after:  {FormatBytes(freeAfter)}");
        log.AppendLine($"Difference: {FormatBytes(freeAfter - freeBefore)}");

        LogBox.Text = log.ToString();
        StatusText.Text = "Done.";
        ApplyFilter();
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
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#}{units[unit]}";
    }
}

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DiskCleanup.Avalonia;

// Hand-rolled modal - Avalonia has no MessageBox.Show equivalent. Mirrors the WPF
// CleanButton_Click confirm step (message + Yes/No) with no new NuGet dependency.
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(string message) : this()
    {
        MessageText.Text = message;
    }

    private void YesButton_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void NoButton_Click(object? sender, RoutedEventArgs e) => Close(false);
}

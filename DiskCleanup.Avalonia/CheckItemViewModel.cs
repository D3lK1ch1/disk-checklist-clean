using System.ComponentModel;
using DiskCleanup.Core;

namespace DiskCleanup.Avalonia;

public class CheckItemViewModel : INotifyPropertyChanged
{
    public CheckItem Item { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }

    public string Label => Item.Label;
    public string Size => Item.FormattedSize;
    public string Risk => Item.Risk;
    public string? Reason => Item.Reason;
    public string ActionDescription => Item.ActionDescription;

    // INFO items (e.g. installed-app list) and items with no action have nothing to clean.
    public bool CanSelect => Item.Action != ActionKind.None;

    public CheckItemViewModel(CheckItem item)
    {
        Item = item;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

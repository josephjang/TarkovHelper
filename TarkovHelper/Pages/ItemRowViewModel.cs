using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Imaging;
using TarkovHelper.Models;

namespace TarkovHelper.Pages;

/// <summary>
/// One row in an item list: the item itself, how many units the player still has to find, how
/// many are owned, and everything the row shows that follows from those two numbers.
/// <para>
/// A base class rather than a shape repeated per page, because the Items page and the Collector
/// page render the same row: the same icon and subtitle, the same FIR and non-FIR quantity
/// halves, the same fulfillment rule (<see cref="ItemFulfillment"/>), the same count strings
/// (<see cref="ItemCountDisplay"/>) and the same dimmed, struck-through look once a row is
/// satisfied. Only the sources a row aggregates differ, so that is all a page's own row type
/// adds. Every member here is bound by name from both pages' XAML, which sees inherited members
/// exactly as it sees declared ones.
/// </para>
/// </summary>
public abstract class ItemRowViewModel : INotifyPropertyChanged
{
    public string ItemId { get; set; } = string.Empty;
    public string ItemNormalizedName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string SubtitleName { get; set; } = string.Empty;
    public Visibility SubtitleVisibility { get; set; } = Visibility.Collapsed;
    public int QuestCount { get; set; }
    public int QuestFIRCount { get; set; }
    public int TotalCount { get; set; }
    public int TotalFIRCount { get; set; }
    public bool FoundInRaid { get; set; }
    public Visibility FirVisibility => FoundInRaid ? Visibility.Visible : Visibility.Collapsed;

    private BitmapImage? _iconSource;
    public BitmapImage? IconSource
    {
        get => _iconSource;
        set
        {
            if (_iconSource != value)
            {
                _iconSource = value;
                OnPropertyChanged(nameof(IconSource));
            }
        }
    }
    public string? IconLink { get; set; }
    public string? WikiLink { get; set; }

    // Inventory quantities (user's owned items)
    private int _ownedFirQuantity;
    private int _ownedNonFirQuantity;

    public int OwnedFirQuantity
    {
        get => _ownedFirQuantity;
        set
        {
            if (_ownedFirQuantity != value)
            {
                _ownedFirQuantity = value;
                OnPropertyChanged(nameof(OwnedFirQuantity));
                RaiseOwnedQuantityDependents();
            }
        }
    }

    public int OwnedNonFirQuantity
    {
        get => _ownedNonFirQuantity;
        set
        {
            if (_ownedNonFirQuantity != value)
            {
                _ownedNonFirQuantity = value;
                OnPropertyChanged(nameof(OwnedNonFirQuantity));
                RaiseOwnedQuantityDependents();
            }
        }
    }

    /// <summary>
    /// How many units of one kind the row holds, and the setter that writes them, for the pages'
    /// quantity controls: a spinner or a text box means one half of the quantity, and says which
    /// with a <see cref="FirKind"/> instead of picking one of two property names. Both go through
    /// the properties above, so a write from here notifies exactly as a direct one does.
    /// </summary>
    public int Owned(FirKind kind) => kind == FirKind.Fir ? OwnedFirQuantity : OwnedNonFirQuantity;

    /// <inheritdoc cref="Owned"/>
    public void SetOwned(FirKind kind, int quantity)
    {
        if (kind == FirKind.Fir)
        {
            OwnedFirQuantity = quantity;
        }
        else
        {
            OwnedNonFirQuantity = quantity;
        }
    }

    /// <summary>
    /// Everything the row derives from what the player owns, raised by both quantity
    /// setters so the list and the detail pane cannot show one half of a change.
    /// </summary>
    private void RaiseOwnedQuantityDependents()
    {
        OnPropertyChanged(nameof(OwnedTotalQuantity));
        OnPropertyChanged(nameof(FulfillmentStatus));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(IsFulfilled));
        OnPropertyChanged(nameof(FulfilledVisibility));
        OnPropertyChanged(nameof(ItemOpacity));
        OnPropertyChanged(nameof(NameTextDecorations));
        OnPropertyChanged(nameof(OwnedDisplay));
    }

    public int OwnedTotalQuantity => OwnedFirQuantity + OwnedNonFirQuantity;

    // Fulfillment over both halves of the aggregate: TotalCount units in all (the quest
    // requirements, plus the hideout modules' on the Items page), TotalFIRCount of them FIR. An
    // item one quest wants FIR and another quest or a hideout module wants plain has TotalCount
    // above TotalFIRCount, and owning the FIR half alone does not satisfy it (see ItemFulfillment).
    public ItemFulfillmentStatus FulfillmentStatus =>
        ItemFulfillment.StatusOf(OwnedFirQuantity, OwnedNonFirQuantity, TotalCount, TotalFIRCount);

    public double ProgressPercent =>
        ItemFulfillment.ProgressPercent(OwnedFirQuantity, OwnedNonFirQuantity, TotalCount, TotalFIRCount);

    public bool IsFulfilled => FulfillmentStatus == ItemFulfillmentStatus.Fulfilled;
    public Visibility FulfilledVisibility => IsFulfilled ? Visibility.Visible : Visibility.Collapsed;
    public double ItemOpacity => IsFulfilled ? 0.5 : 1.0;
    public TextDecorationCollection? NameTextDecorations => IsFulfilled ? TextDecorations.Strikethrough : null;

    // The count strings both pages' XAML binds by name. The shapes themselves belong to neither
    // page, so they live in one place (see ItemCountDisplay).
    public string OwnedDisplay => ItemCountDisplay.Owned(OwnedFirQuantity, OwnedNonFirQuantity);

    // Display strings for UI - shows FIR/non-FIR breakdown (see ItemCountDisplay.Required)
    public string QuestDisplay => ItemCountDisplay.Required(QuestCount, QuestFIRCount);
    public string TotalDisplay => ItemCountDisplay.Required(TotalCount, TotalFIRCount);

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

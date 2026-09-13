using System.ComponentModel;
using TarkovHelper.Models;
using TarkovHelper.Pages;
using TextDecorations = System.Windows.TextDecorations;
using Visibility = System.Windows.Visibility;

namespace TarkovHelper.Tests;

/// <summary>
/// When an item requirement counts as satisfied on the Items page rows, the sibling of
/// <see cref="CollectorItemFulfillmentTests"/> over <see cref="AggregatedItemViewModel"/>.
/// <para>
/// The Items page aggregates quest and hideout requirements into the same two halves the
/// Collector page uses: <c>TotalCount</c> units in all (QuestCount + HideoutCount), of which
/// <c>TotalFIRCount</c> must be Found In Raid (QuestFIRCount + HideoutFIRCount). An item one
/// quest wants FIR and a hideout module wants plain is mixed, so owning the FIR half alone is
/// not owning the item: these cases pin that both halves have to be met before a row reads
/// fulfilled, reads 100%, or is struck through. FIR units count toward the non-FIR remainder
/// (a raid-found item is still an item), never the other way round.
/// </para>
/// </summary>
public sealed class ItemsViewModelFulfillmentTests
{
    /// <summary>An Items page row for a requirement of <paramref name="total"/> units, <paramref name="fir"/> of them FIR.</summary>
    private static AggregatedItemViewModel Row(int total, int fir, int ownedFir = 0, int ownedNonFir = 0) =>
        new()
        {
            ItemId = "item-1",
            DisplayName = "Item",
            TotalCount = total,
            TotalFIRCount = fir,
            OwnedFirQuantity = ownedFir,
            OwnedNonFirQuantity = ownedNonFir
        };

    /// <summary>The names raised whenever an owned quantity changes, so no dependent is dropped.</summary>
    private static readonly string[] OwnedQuantityDependents =
    {
        nameof(AggregatedItemViewModel.OwnedTotalQuantity),
        nameof(AggregatedItemViewModel.FulfillmentStatus),
        nameof(AggregatedItemViewModel.ProgressPercent),
        nameof(AggregatedItemViewModel.IsFulfilled),
        nameof(AggregatedItemViewModel.FulfilledVisibility),
        nameof(AggregatedItemViewModel.ItemOpacity),
        nameof(AggregatedItemViewModel.NameTextDecorations),
        nameof(AggregatedItemViewModel.OwnedDisplay)
    };

    private static List<string> Raised(AggregatedItemViewModel row, Action<AggregatedItemViewModel> change)
    {
        var names = new List<string>();
        PropertyChangedEventHandler handler = (_, e) => names.Add(e.PropertyName ?? string.Empty);
        row.PropertyChanged += handler;
        try
        {
            change(row);
        }
        finally
        {
            row.PropertyChanged -= handler;
        }
        return names;
    }

    // 3 units wanted, 2 of them FIR: the shape a quest's FIR requirement plus a hideout
    // module's plain requirement on the same item produces. Owning the FIR half is two thirds
    // of the way, so the row must not read 100% or strike itself through.
    [Fact]
    public void MixedRequirement_WithOnlyTheFirHalfOwned_IsNotFulfilled()
    {
        var row = Row(total: 3, fir: 2, ownedFir: 2);

        Assert.Equal(ItemFulfillmentStatus.PartiallyFulfilled, row.FulfillmentStatus);
        Assert.False(row.IsFulfilled);
        Assert.Equal(200d / 3d, row.ProgressPercent, 6);
        Assert.Null(row.NameTextDecorations);
        Assert.Equal(1.0, row.ItemOpacity);
        Assert.Equal(Visibility.Collapsed, row.FulfilledVisibility);
    }

    [Fact]
    public void MixedRequirement_WithBothHalvesOwned_IsFulfilled()
    {
        var row = Row(total: 3, fir: 2, ownedFir: 2, ownedNonFir: 1);

        Assert.Equal(ItemFulfillmentStatus.Fulfilled, row.FulfillmentStatus);
        Assert.True(row.IsFulfilled);
        Assert.Equal(100, row.ProgressPercent);
        Assert.Same(TextDecorations.Strikethrough, row.NameTextDecorations);
        Assert.Equal(0.5, row.ItemOpacity);
        Assert.Equal(Visibility.Visible, row.FulfilledVisibility);
    }

    // A FIR unit satisfies a plain unit, so three FIR cover "3 units, 2 of them FIR".
    [Fact]
    public void MixedRequirement_AllFirCoversTheNonFirRemainder()
    {
        var row = Row(total: 3, fir: 2, ownedFir: 3);

        Assert.Equal(ItemFulfillmentStatus.Fulfilled, row.FulfillmentStatus);
        Assert.Equal(100, row.ProgressPercent);
    }

    // The reverse does not hold: a flea market copy cannot fill a FIR slot.
    [Fact]
    public void MixedRequirement_NonFirDoesNotFillTheFirHalf()
    {
        var row = Row(total: 3, fir: 2, ownedNonFir: 3);

        Assert.Equal(ItemFulfillmentStatus.PartiallyFulfilled, row.FulfillmentStatus);
        Assert.Equal(100d / 3d, row.ProgressPercent, 6);
    }

    [Fact]
    public void FirOnlyRequirement_IsFulfilledByTheFirUnitsAlone()
    {
        Assert.Equal(ItemFulfillmentStatus.Fulfilled, Row(total: 2, fir: 2, ownedFir: 2).FulfillmentStatus);
        Assert.Equal(100, Row(total: 2, fir: 2, ownedFir: 2).ProgressPercent);
    }

    [Fact]
    public void FirOnlyRequirement_IgnoresNonFirUnits()
    {
        var row = Row(total: 2, fir: 2, ownedNonFir: 5);

        Assert.Equal(ItemFulfillmentStatus.PartiallyFulfilled, row.FulfillmentStatus);
        Assert.Equal(0, row.ProgressPercent);
    }

    [Fact]
    public void NonFirRequirement_CountsEitherKind()
    {
        Assert.Equal(ItemFulfillmentStatus.Fulfilled, Row(total: 2, fir: 0, ownedNonFir: 2).FulfillmentStatus);
        Assert.Equal(ItemFulfillmentStatus.Fulfilled, Row(total: 2, fir: 0, ownedFir: 2).FulfillmentStatus);
        Assert.Equal(ItemFulfillmentStatus.Fulfilled, Row(total: 2, fir: 0, ownedFir: 1, ownedNonFir: 1).FulfillmentStatus);
        Assert.Equal(50, Row(total: 2, fir: 0, ownedNonFir: 1).ProgressPercent);
    }

    // Nothing owned is NotStarted, which is what the page's "Not Started" filter selects on.
    [Fact]
    public void NothingOwned_IsNotStarted()
    {
        var row = Row(total: 3, fir: 2);

        Assert.Equal(ItemFulfillmentStatus.NotStarted, row.FulfillmentStatus);
        Assert.Equal(0, row.ProgressPercent);
    }

    // A row with nothing required (an item listed for reference) is already satisfied.
    [Fact]
    public void ZeroRequirement_IsFulfilledAndReadsFull()
    {
        var row = Row(total: 0, fir: 0);

        Assert.Equal(ItemFulfillmentStatus.Fulfilled, row.FulfillmentStatus);
        Assert.Equal(100, row.ProgressPercent);
    }

    [Fact]
    public void OwningMoreThanNeeded_StaysAtFullProgress()
    {
        var row = Row(total: 3, fir: 2, ownedFir: 40, ownedNonFir: 60);

        Assert.Equal(ItemFulfillmentStatus.Fulfilled, row.FulfillmentStatus);
        Assert.Equal(100, row.ProgressPercent);
    }

    // Corrupt data (more FIR units than units) must not read fulfilled on the smaller number.
    [Fact]
    public void FirCountAboveTotalCount_StillNeedsEveryFirUnit()
    {
        var row = Row(total: 1, fir: 3, ownedFir: 1);

        Assert.Equal(ItemFulfillmentStatus.PartiallyFulfilled, row.FulfillmentStatus);
        Assert.Equal(100d / 3d, row.ProgressPercent, 6);
        Assert.Equal(ItemFulfillmentStatus.Fulfilled, Row(total: 1, fir: 3, ownedFir: 3).FulfillmentStatus);
    }

    // The three count columns beside the row, which have to name the same two halves the rule reads.
    [Fact]
    public void CountDisplays_NameBothHalvesOfTheRequirement()
    {
        var row = new AggregatedItemViewModel
        {
            QuestCount = 2,
            QuestFIRCount = 2,
            HideoutCount = 1,
            HideoutFIRCount = 0,
            TotalCount = 3,
            TotalFIRCount = 2
        };

        Assert.Equal("2 (FIR)", row.QuestDisplay);
        Assert.Equal("1", row.HideoutDisplay);
        Assert.Equal("2F+1", row.TotalDisplay);

        var empty = new AggregatedItemViewModel();
        Assert.Equal("0", empty.QuestDisplay);
        Assert.Equal("0", empty.HideoutDisplay);
        Assert.Equal("0", empty.TotalDisplay);

        // Bad data: more FIR units than units reads as all FIR, never a negative remainder.
        Assert.Equal("3 (FIR)", Row(total: 1, fir: 3).TotalDisplay);

        // A column carrying a FIR count but no units is bad data too, and every column now
        // reports what it has instead of the quest and hideout columns alone printing "0".
        var firWithoutUnits = new AggregatedItemViewModel
        {
            QuestFIRCount = 2,
            HideoutFIRCount = 1,
            TotalFIRCount = 3
        };
        Assert.Equal("2 (FIR)", firWithoutUnits.QuestDisplay);
        Assert.Equal("1 (FIR)", firWithoutUnits.HideoutDisplay);
        Assert.Equal("3 (FIR)", firWithoutUnits.TotalDisplay);
    }

    [Fact]
    public void OwnedDisplay_NamesWhatIsOwned()
    {
        Assert.Equal("0", Row(total: 3, fir: 2).OwnedDisplay);
        Assert.Equal("2F", Row(total: 3, fir: 2, ownedFir: 2).OwnedDisplay);
        Assert.Equal("1", Row(total: 3, fir: 2, ownedNonFir: 1).OwnedDisplay);
        Assert.Equal("2F+1", Row(total: 3, fir: 2, ownedFir: 2, ownedNonFir: 1).OwnedDisplay);
    }

    [Fact]
    public void SettingFirQuantity_RaisesEveryDerivedProperty()
    {
        var row = Row(total: 3, fir: 2);

        var raised = Raised(row, r => r.OwnedFirQuantity = 2);

        Assert.Equal(nameof(AggregatedItemViewModel.OwnedFirQuantity), raised[0]);
        Assert.Equal(
            OwnedQuantityDependents.OrderBy(n => n, StringComparer.Ordinal),
            raised.Skip(1).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void SettingNonFirQuantity_RaisesEveryDerivedProperty()
    {
        var row = Row(total: 3, fir: 2);

        var raised = Raised(row, r => r.OwnedNonFirQuantity = 1);

        Assert.Equal(nameof(AggregatedItemViewModel.OwnedNonFirQuantity), raised[0]);
        Assert.Equal(
            OwnedQuantityDependents.OrderBy(n => n, StringComparer.Ordinal),
            raised.Skip(1).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void SettingTheSameQuantity_RaisesNothing()
    {
        var row = Row(total: 3, fir: 2, ownedFir: 2, ownedNonFir: 1);

        Assert.Empty(Raised(row, r => r.OwnedFirQuantity = 2));
        Assert.Empty(Raised(row, r => r.OwnedNonFirQuantity = 1));
    }
}

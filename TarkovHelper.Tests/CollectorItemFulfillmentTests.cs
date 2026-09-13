using System.ComponentModel;
using TarkovHelper.Models;
using TarkovHelper.Pages;
using TextDecorations = System.Windows.TextDecorations;
using Visibility = System.Windows.Visibility;

namespace TarkovHelper.Tests;

/// <summary>
/// When an item requirement counts as satisfied, for the Collector item rows and for the
/// shared <see cref="ItemFulfillmentInfo"/> the hideout and quest detail panes read, plus the
/// shared count shapes (<see cref="ItemCountDisplay"/>) both item pages print.
/// <para>
/// A requirement has two halves: <c>TotalCount</c> units in all, of which <c>TotalFIRCount</c>
/// must be Found In Raid. A mixed row happens whenever one item is wanted FIR by one quest and
/// plain by another, so owning the FIR half alone is not owning the item: these cases pin that
/// both halves have to be met before a row reads fulfilled, reads 100%, or is struck through.
/// FIR units do count toward the non-FIR remainder (a raid-found item is still an item), but
/// never the other way round.
/// </para>
/// </summary>
public sealed class CollectorItemFulfillmentTests
{
    /// <summary>A Collector row for a requirement of <paramref name="total"/> units, <paramref name="fir"/> of them FIR.</summary>
    private static CollectorItemViewModel Row(int total, int fir, int ownedFir = 0, int ownedNonFir = 0) =>
        new()
        {
            ItemId = "item-1",
            DisplayName = "Item",
            TotalCount = total,
            TotalFIRCount = fir,
            OwnedFirQuantity = ownedFir,
            OwnedNonFirQuantity = ownedNonFir
        };

    private static ItemFulfillmentInfo Info(int total, int fir, int ownedFir = 0, int ownedNonFir = 0) =>
        new()
        {
            ItemNormalizedName = "item-1",
            RequiredTotal = total,
            RequiredFir = fir,
            OwnedFir = ownedFir,
            OwnedNonFir = ownedNonFir
        };

    /// <summary>The names raised whenever an owned quantity changes, so no dependent is dropped.</summary>
    private static readonly string[] OwnedQuantityDependents =
    {
        nameof(CollectorItemViewModel.OwnedTotalQuantity),
        nameof(CollectorItemViewModel.FulfillmentStatus),
        nameof(CollectorItemViewModel.ProgressPercent),
        nameof(CollectorItemViewModel.IsFulfilled),
        nameof(CollectorItemViewModel.FulfilledVisibility),
        nameof(CollectorItemViewModel.ItemOpacity),
        nameof(CollectorItemViewModel.NameTextDecorations),
        nameof(CollectorItemViewModel.OwnedDisplay)
    };

    private static List<string> Raised(CollectorItemViewModel row, Action<CollectorItemViewModel> change)
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

    // 3 units wanted, 2 of them FIR: the shape a Collector FIR requirement plus a prerequisite's
    // plain requirement on the same item produces. Owning the FIR half is two thirds of the way.
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
        var row = Row(total: 3, fir: 2, ownedFir: 0, ownedNonFir: 3);

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

    // The counts column next to the row, which has to name the same two halves the rule reads.
    [Fact]
    public void TotalDisplay_NamesBothHalvesOfTheRequirement()
    {
        Assert.Equal("2F+1", Row(total: 3, fir: 2).TotalDisplay);
        Assert.Equal("2 (FIR)", Row(total: 2, fir: 2).TotalDisplay);
        Assert.Equal("3", Row(total: 3, fir: 0).TotalDisplay);
        Assert.Equal("0", Row(total: 0, fir: 0).TotalDisplay);
        // Bad data: more FIR units than units reads as all FIR, never a negative remainder.
        Assert.Equal("3 (FIR)", Row(total: 1, fir: 3).TotalDisplay);
    }

    // The quest column and the owned column, the other two bound strings on the row.
    [Fact]
    public void QuestDisplay_AndOwnedDisplay_NameTheirOwnCounts()
    {
        var row = new CollectorItemViewModel
        {
            QuestCount = 3,
            QuestFIRCount = 2,
            TotalCount = 3,
            TotalFIRCount = 2,
            OwnedFirQuantity = 2,
            OwnedNonFirQuantity = 1
        };

        Assert.Equal("2F+1", row.QuestDisplay);
        Assert.Equal("2F+1", row.OwnedDisplay);

        var empty = new CollectorItemViewModel();
        Assert.Equal("0", empty.QuestDisplay);
        Assert.Equal("0", empty.OwnedDisplay);

        Assert.Equal("2F", Row(total: 3, fir: 2, ownedFir: 2).OwnedDisplay);
        Assert.Equal("1", Row(total: 3, fir: 2, ownedNonFir: 1).OwnedDisplay);
    }

    // The shared shapes themselves, which both item pages bind and neither owns: they live
    // beside the rule in ItemInventory.cs, so they are pinned here directly and not only
    // through a row.
    [Fact]
    public void CountDisplay_Required_NamesBothHalves()
    {
        Assert.Equal("3", ItemCountDisplay.Required(requiredTotal: 3, requiredFir: 0));
        Assert.Equal("2 (FIR)", ItemCountDisplay.Required(requiredTotal: 2, requiredFir: 2));
        Assert.Equal("2F+1", ItemCountDisplay.Required(requiredTotal: 3, requiredFir: 2));
        Assert.Equal("0", ItemCountDisplay.Required(requiredTotal: 0, requiredFir: 0));
    }

    [Fact]
    public void CountDisplay_Required_OnBadData_NeverPrintsANegative()
    {
        // More FIR units than units: the FIR half wins, the same units
        // ItemFulfillment.RequiredUnits asks for, so no negative remainder is printed.
        Assert.Equal("3 (FIR)", ItemCountDisplay.Required(requiredTotal: 1, requiredFir: 3));
        Assert.Equal("2 (FIR)", ItemCountDisplay.Required(requiredTotal: 0, requiredFir: 2));
        Assert.Equal(
            ItemFulfillment.RequiredUnits(1, 3).ToString() + " (FIR)",
            ItemCountDisplay.Required(requiredTotal: 1, requiredFir: 3));

        // A negative count reads as nothing required instead of a minus sign on screen.
        Assert.Equal("0", ItemCountDisplay.Required(requiredTotal: -2, requiredFir: 0));
        Assert.Equal("3", ItemCountDisplay.Required(requiredTotal: 3, requiredFir: -1));
        Assert.Equal("0", ItemCountDisplay.Required(requiredTotal: -2, requiredFir: -1));
    }

    [Fact]
    public void CountDisplay_Owned_NamesWhatIsHeld()
    {
        Assert.Equal("0", ItemCountDisplay.Owned(ownedFir: 0, ownedNonFir: 0));
        Assert.Equal("2F", ItemCountDisplay.Owned(ownedFir: 2, ownedNonFir: 0));
        Assert.Equal("1", ItemCountDisplay.Owned(ownedFir: 0, ownedNonFir: 1));
        Assert.Equal("2F+1", ItemCountDisplay.Owned(ownedFir: 2, ownedNonFir: 1));
    }

    [Fact]
    public void CountDisplay_Owned_OnBadData_ClampsEachKindOnItsOwn()
    {
        Assert.Equal("0", ItemCountDisplay.Owned(ownedFir: -1, ownedNonFir: 0));
        Assert.Equal("1", ItemCountDisplay.Owned(ownedFir: -1, ownedNonFir: 1));
        // A negative of one kind must not cancel out units of the other.
        Assert.Equal("2F", ItemCountDisplay.Owned(ownedFir: 2, ownedNonFir: -3));
    }

    [Fact]
    public void SettingFirQuantity_RaisesEveryDerivedProperty()
    {
        var row = Row(total: 3, fir: 2);

        var raised = Raised(row, r => r.OwnedFirQuantity = 2);

        Assert.Equal(nameof(CollectorItemViewModel.OwnedFirQuantity), raised[0]);
        Assert.Equal(
            OwnedQuantityDependents.OrderBy(n => n, StringComparer.Ordinal),
            raised.Skip(1).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void SettingNonFirQuantity_RaisesEveryDerivedProperty()
    {
        var row = Row(total: 3, fir: 2);

        var raised = Raised(row, r => r.OwnedNonFirQuantity = 1);

        Assert.Equal(nameof(CollectorItemViewModel.OwnedNonFirQuantity), raised[0]);
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

    // The same rule through the shared model the hideout and quest detail panes use: the
    // hideout's remaining-items list hands it genuinely mixed counts.
    [Fact]
    public void FulfillmentInfo_MixedRequirement_NeedsBothHalves()
    {
        var firHalfOnly = Info(total: 3, fir: 2, ownedFir: 2);

        Assert.Equal(ItemFulfillmentStatus.PartiallyFulfilled, firHalfOnly.Status);
        Assert.True(firHalfOnly.IsFirFulfilled);
        Assert.False(firHalfOnly.IsTotalFulfilled);
        Assert.Equal(200d / 3d, firHalfOnly.ProgressPercent, 6);

        var bothHalves = Info(total: 3, fir: 2, ownedFir: 2, ownedNonFir: 1);

        Assert.Equal(ItemFulfillmentStatus.Fulfilled, bothHalves.Status);
        Assert.True(bothHalves.IsTotalFulfilled);
        Assert.Equal(100, bothHalves.ProgressPercent);
    }

    [Fact]
    public void FulfillmentInfo_UniformRequirements_KeepTheirAnswers()
    {
        // All FIR: the FIR half is the whole requirement.
        Assert.Equal(ItemFulfillmentStatus.Fulfilled, Info(total: 2, fir: 2, ownedFir: 2).Status);
        Assert.Equal(ItemFulfillmentStatus.PartiallyFulfilled, Info(total: 2, fir: 2, ownedNonFir: 2).Status);

        // No FIR: either kind counts.
        Assert.Equal(ItemFulfillmentStatus.Fulfilled, Info(total: 2, fir: 0, ownedNonFir: 2).Status);
        Assert.Equal(ItemFulfillmentStatus.NotStarted, Info(total: 2, fir: 0).Status);

        // Nothing required.
        Assert.Equal(ItemFulfillmentStatus.Fulfilled, Info(total: 0, fir: 0).Status);
        Assert.Equal(100, Info(total: 0, fir: 0).ProgressPercent);
    }
}

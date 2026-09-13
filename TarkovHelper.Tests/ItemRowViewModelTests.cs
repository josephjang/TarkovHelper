using System.ComponentModel;
using System.Reflection;
using TarkovHelper.Models;
using TarkovHelper.Pages;
using TextDecorations = System.Windows.TextDecorations;
using Visibility = System.Windows.Visibility;

namespace TarkovHelper.Tests;

/// <summary>
/// The one item row both item pages render (<see cref="ItemRowViewModel"/>): the Collector page's
/// rows and the Items page's rows are the same row, and everything a row shows is answered by a
/// single implementation.
/// <para>
/// Asserted THROUGH the base type against both concrete rows, so a page growing a second copy of
/// the fulfillment rule, the dimming, the strikethrough or a count string is a failure here and
/// not a divergence found later by a reader. The two pages held byte-identical copies of all of
/// it before, which is the defect these tests pin shut.
/// </para>
/// </summary>
public sealed class ItemRowViewModelTests
{
    public static TheoryData<string> RowTypes() => new()
    {
        nameof(CollectorItemViewModel),
        nameof(AggregatedItemViewModel)
    };

    /// <summary>An empty row of the named page's type, seen as the row both pages share.</summary>
    private static ItemRowViewModel NewRow(string rowType) => rowType switch
    {
        nameof(CollectorItemViewModel) => new CollectorItemViewModel(),
        nameof(AggregatedItemViewModel) => new AggregatedItemViewModel(),
        _ => throw new ArgumentOutOfRangeException(nameof(rowType), rowType, "Unknown item row type")
    };

    // 3 units wanted, 2 of them FIR: owning the FIR half alone is not owning the item, so the
    // row must stay lit and unstruck until the plain unit is found too.
    [Theory]
    [MemberData(nameof(RowTypes))]
    public void Fulfilling_a_row_dims_strikes_and_badges_it_from_one_implementation(string rowType)
    {
        var row = NewRow(rowType);
        row.TotalCount = 3;
        row.TotalFIRCount = 2;

        row.OwnedFirQuantity = 2;                       // FIR half only
        Assert.Equal(ItemFulfillmentStatus.PartiallyFulfilled, row.FulfillmentStatus);
        Assert.Equal(1.0, row.ItemOpacity);
        Assert.Null(row.NameTextDecorations);
        Assert.Equal(Visibility.Collapsed, row.FulfilledVisibility);

        row.OwnedNonFirQuantity = 1;                    // both halves
        Assert.Equal(ItemFulfillmentStatus.Fulfilled, row.FulfillmentStatus);
        Assert.Equal(100, row.ProgressPercent);
        Assert.Equal(0.5, row.ItemOpacity);
        Assert.Same(TextDecorations.Strikethrough, row.NameTextDecorations);
        Assert.Equal(Visibility.Visible, row.FulfilledVisibility);
    }

    // The edges of the rule, through the base: nothing owned, nothing required, and more owned
    // than needed all read the same on either page.
    [Theory]
    [MemberData(nameof(RowTypes))]
    public void The_boundaries_of_the_rule_read_the_same_on_both_pages(string rowType)
    {
        var nothingOwned = NewRow(rowType);
        nothingOwned.TotalCount = 3;
        nothingOwned.TotalFIRCount = 2;
        Assert.Equal(ItemFulfillmentStatus.NotStarted, nothingOwned.FulfillmentStatus);
        Assert.Equal(0, nothingOwned.ProgressPercent);

        // A row with nothing required is already satisfied, and reads full rather than 0%.
        var nothingRequired = NewRow(rowType);
        Assert.Equal(ItemFulfillmentStatus.Fulfilled, nothingRequired.FulfillmentStatus);
        Assert.Equal(100, nothingRequired.ProgressPercent);

        // Overshooting stays at full instead of running past it.
        var hoarded = NewRow(rowType);
        hoarded.TotalCount = 3;
        hoarded.TotalFIRCount = 2;
        hoarded.OwnedFirQuantity = 40;
        hoarded.OwnedNonFirQuantity = 60;
        Assert.Equal(ItemFulfillmentStatus.Fulfilled, hoarded.FulfillmentStatus);
        Assert.Equal(100, hoarded.ProgressPercent);
    }

    // The three count strings the rows bind by name, from the one set of shapes.
    [Theory]
    [MemberData(nameof(RowTypes))]
    public void The_count_columns_name_the_same_halves_on_both_pages(string rowType)
    {
        var row = NewRow(rowType);
        row.QuestCount = 3;
        row.QuestFIRCount = 2;
        row.TotalCount = 3;
        row.TotalFIRCount = 2;
        row.OwnedFirQuantity = 2;
        row.OwnedNonFirQuantity = 1;

        Assert.Equal("2F+1", row.QuestDisplay);
        Assert.Equal("2F+1", row.TotalDisplay);
        Assert.Equal("2F+1", row.OwnedDisplay);

        var empty = NewRow(rowType);
        Assert.Equal("0", empty.QuestDisplay);
        Assert.Equal("0", empty.TotalDisplay);
        Assert.Equal("0", empty.OwnedDisplay);
    }

    /// <summary>The names raised whenever an owned quantity changes, so no dependent is dropped.</summary>
    private static readonly string[] OwnedQuantityDependents =
    {
        nameof(ItemRowViewModel.OwnedTotalQuantity),
        nameof(ItemRowViewModel.FulfillmentStatus),
        nameof(ItemRowViewModel.ProgressPercent),
        nameof(ItemRowViewModel.IsFulfilled),
        nameof(ItemRowViewModel.FulfilledVisibility),
        nameof(ItemRowViewModel.ItemOpacity),
        nameof(ItemRowViewModel.NameTextDecorations),
        nameof(ItemRowViewModel.OwnedDisplay)
    };

    // Both pages' rows notify from the same setters, so neither list nor detail pane can show
    // one half of a quantity change.
    [Theory]
    [MemberData(nameof(RowTypes))]
    public void A_quantity_change_raises_every_dependent_on_both_pages(string rowType)
    {
        var row = NewRow(rowType);
        row.TotalCount = 3;
        row.TotalFIRCount = 2;

        var raised = new List<string>();
        PropertyChangedEventHandler handler = (_, e) => raised.Add(e.PropertyName ?? string.Empty);
        row.PropertyChanged += handler;
        try
        {
            row.OwnedFirQuantity = 2;
        }
        finally
        {
            row.PropertyChanged -= handler;
        }

        Assert.Equal(nameof(ItemRowViewModel.OwnedFirQuantity), raised[0]);
        Assert.Equal(
            OwnedQuantityDependents.OrderBy(n => n, StringComparer.Ordinal),
            raised.Skip(1).OrderBy(n => n, StringComparer.Ordinal));
    }

    // The kind-parameterised accessors the pages' quantity controls write through: one pair on
    // the base for both rows, reaching the same properties a direct write reaches.
    [Theory]
    [MemberData(nameof(RowTypes))]
    public void A_row_reads_and_writes_the_half_its_kind_names(string rowType)
    {
        var row = NewRow(rowType);

        row.SetOwned(FirKind.Fir, 4);
        row.SetOwned(FirKind.NonFir, 2);

        Assert.Equal(4, row.Owned(FirKind.Fir));
        Assert.Equal(2, row.Owned(FirKind.NonFir));
        Assert.Equal(4, row.OwnedFirQuantity);
        Assert.Equal(2, row.OwnedNonFirQuantity);
        Assert.Equal(6, row.OwnedTotalQuantity);
        Assert.Equal("4F+2", row.OwnedDisplay);

        // Writing one half leaves the other where it was.
        row.SetOwned(FirKind.Fir, 0);
        Assert.Equal(0, row.Owned(FirKind.Fir));
        Assert.Equal(2, row.Owned(FirKind.NonFir));
    }

    // A write through SetOwned notifies exactly as a direct property write does, so a spinner or
    // a text box cannot move a quantity without the dependent columns following it.
    [Theory]
    [MemberData(nameof(RowTypes))]
    public void A_kind_write_raises_the_same_dependents_a_direct_write_raises(string rowType)
    {
        foreach (var (kind, property) in new[]
                 {
                     (FirKind.Fir, nameof(ItemRowViewModel.OwnedFirQuantity)),
                     (FirKind.NonFir, nameof(ItemRowViewModel.OwnedNonFirQuantity)),
                 })
        {
            var row = NewRow(rowType);
            row.TotalCount = 3;
            row.TotalFIRCount = 2;

            var raised = new List<string>();
            PropertyChangedEventHandler handler = (_, e) => raised.Add(e.PropertyName ?? string.Empty);
            row.PropertyChanged += handler;
            try
            {
                row.SetOwned(kind, 2);
                row.SetOwned(kind, 2);      // the same number again is not a change
            }
            finally
            {
                row.PropertyChanged -= handler;
            }

            Assert.Equal(property, raised[0]);
            Assert.Equal(
                OwnedQuantityDependents.OrderBy(n => n, StringComparer.Ordinal),
                raised.Skip(1).OrderBy(n => n, StringComparer.Ordinal));
        }
    }

    // The structural half of the same point: a page's row type may only add what that page
    // genuinely aggregates. Re-declaring a shared member here (a second fulfillment rule, a
    // second count string) fails this, which is how the duplication stays gone.
    [Fact]
    public void A_page_row_declares_only_what_that_page_aggregates()
    {
        Assert.Empty(DeclaredMemberNames(typeof(CollectorItemViewModel)));

        Assert.Equal(
            new[]
            {
                nameof(AggregatedItemViewModel.Category),
                nameof(AggregatedItemViewModel.HideoutCount),
                nameof(AggregatedItemViewModel.HideoutDisplay),
                nameof(AggregatedItemViewModel.HideoutFIRCount),
                nameof(AggregatedItemViewModel.ParentCategory)
            },
            DeclaredMemberNames(typeof(AggregatedItemViewModel)));

        Assert.True(typeof(ItemRowViewModel).IsAssignableFrom(typeof(CollectorItemViewModel)));
        Assert.True(typeof(ItemRowViewModel).IsAssignableFrom(typeof(AggregatedItemViewModel)));
    }

    private static string[] DeclaredMemberNames(Type rowType) =>
        rowType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
}

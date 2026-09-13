using System.Text.RegularExpressions;
using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// The quantity axis of <see cref="ItemInventoryService"/> now that the FIR / non-FIR
/// distinction is a value (<see cref="FirKind"/>) instead of two of every method: one
/// <c>GetQuantity</c>, one <c>SetQuantity</c> and one <c>AdjustQuantity</c>, with the six
/// per-kind names kept as delegations.
/// <para>
/// Every case that can be is a <see cref="TheoryAttribute"/> over both kinds, so "the two halves
/// behave identically" is asserted by construction rather than by reading two method bodies side
/// by side - the divergence risk the twins carried. The writes are the persistence-adjacent
/// ones, so each case also pins what the write scheduled (<c>_pendingSaves</c>, staged by
/// <c>ScheduleSave</c>), what it removed (<c>CleanupEmptyInventory</c>) and whether
/// <c>InventoryChanged</c> was raised.
/// </para>
/// <para>
/// The service is built uninitialized (see <see cref="TestReflection"/>) so no singleton
/// constructor runs and no user_data.db is opened; only the fields these paths read are seeded.
/// The debounce timer is deliberately left null, which makes <c>ScheduleSave</c>'s
/// <c>_saveTimer?.Stop()</c> a no-op and keeps the flush out of these cases.
/// </para>
/// </summary>
public sealed class ItemInventoryKindTests
{
    private const string Item = "salewa";

    public static TheoryData<FirKind> Kinds() => new() { FirKind.Fir, FirKind.NonFir };

    /// <summary>The other half of the quantity, which every write here must leave alone.</summary>
    private static FirKind Other(FirKind kind) => kind == FirKind.Fir ? FirKind.NonFir : FirKind.Fir;

    private sealed record Harness(
        ItemInventoryService Service,
        ItemInventoryData Inventory,
        Dictionary<string, string> PendingSaves)
    {
        public int Changed { get; set; }
    }

    private static Harness NewService(params (string Item, int Fir, int NonFir)[] seed)
    {
        var service = TestReflection.Uninitialized<ItemInventoryService>();
        var inventory = new ItemInventoryData();
        foreach (var (item, fir, nonFir) in seed)
        {
            inventory.Items[item] = new ItemInventory
            {
                ItemNormalizedName = item,
                FirQuantity = fir,
                NonFirQuantity = nonFir,
            };
        }

        var pending = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        TestReflection.SetPrivateField(service, "_lock", new object());
        TestReflection.SetPrivateField(service, "_pendingSaves", pending);
        TestReflection.SetPrivateField(service, "_inventoryData", inventory);
        TestReflection.SetPrivateField(service, "_loadedProfileId", "test");

        var harness = new Harness(service, inventory, pending);
        service.InventoryChanged += (_, _) => harness.Changed++;
        return harness;
    }

    // The happy path, for either half: the number lands, the OTHER half is untouched, the item
    // is staged for a save and the UI is told once.
    [Theory]
    [MemberData(nameof(Kinds))]
    public void Setting_one_half_stores_it_schedules_a_save_and_leaves_the_other_half_alone(FirKind kind)
    {
        var h = NewService((Item, Fir: 1, NonFir: 1));

        h.Service.SetQuantity(Item, kind, 4);

        Assert.Equal(4, h.Service.GetQuantity(Item, kind));
        Assert.Equal(1, h.Service.GetQuantity(Item, Other(kind)));
        Assert.Equal(5, h.Service.GetTotalQuantity(Item));
        Assert.Equal(Item, Assert.Single(h.PendingSaves).Key);
        Assert.Equal(1, h.Changed);
    }

    // An item nobody owned yet: the write creates the entry rather than dropping the quantity.
    [Theory]
    [MemberData(nameof(Kinds))]
    public void Setting_a_half_on_an_unknown_item_creates_the_entry(FirKind kind)
    {
        var h = NewService();

        h.Service.SetQuantity(Item, kind, 2);

        Assert.Equal(2, h.Service.GetQuantity(Item, kind));
        Assert.Equal(0, h.Service.GetQuantity(Item, Other(kind)));
        Assert.Equal(Item, Assert.Single(h.Inventory.Items).Key);
        Assert.Equal(1, h.Changed);
    }

    // Below zero is not a quantity: a negative write stores nothing owned. The other half holds
    // the entry open, so this case is about the clamp and not about the cleanup.
    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_negative_quantity_clamps_to_zero(FirKind kind)
    {
        var h = NewService((Item, Fir: 3, NonFir: 3));

        h.Service.SetQuantity(Item, kind, -7);

        Assert.Equal(0, h.Service.GetQuantity(Item, kind));
        Assert.Equal(3, h.Service.GetQuantity(Item, Other(kind)));
        Assert.True(h.Inventory.Items.ContainsKey(Item));
        Assert.Equal(1, h.Changed);
    }

    // A delta bigger than what is held empties that half instead of going negative, and once
    // both halves are empty the entry goes with it (CleanupEmptyInventory), for either kind.
    [Theory]
    [MemberData(nameof(Kinds))]
    public void An_adjustment_past_zero_empties_the_half_and_drops_an_emptied_entry(FirKind kind)
    {
        // Only the half under test is held, so emptying it empties the row.
        var h = NewService((Item,
            Fir: kind == FirKind.Fir ? 2 : 0,
            NonFir: kind == FirKind.NonFir ? 2 : 0));

        h.Service.AdjustQuantity(Item, kind, -5);

        Assert.Equal(0, h.Service.GetQuantity(Item, kind));
        Assert.Empty(h.Inventory.Items);
        Assert.Equal(Item, Assert.Single(h.PendingSaves).Key);
        Assert.Equal(1, h.Changed);
    }

    // A plain nudge in either direction, for either half.
    [Theory]
    [MemberData(nameof(Kinds))]
    public void An_adjustment_moves_only_its_own_half(FirKind kind)
    {
        var h = NewService((Item, Fir: 2, NonFir: 2));

        h.Service.AdjustQuantity(Item, kind, 5);
        Assert.Equal(7, h.Service.GetQuantity(Item, kind));

        h.Service.AdjustQuantity(Item, kind, -3);
        Assert.Equal(4, h.Service.GetQuantity(Item, kind));

        Assert.Equal(2, h.Service.GetQuantity(Item, Other(kind)));
        Assert.Equal(2, h.Changed);
    }

    // Writing the quantity that is already held is not a change: no save is staged and no
    // repaint is asked for. A spinner held down at zero must not queue a write per click.
    [Theory]
    [MemberData(nameof(Kinds))]
    public void Writing_the_quantity_already_held_schedules_nothing(FirKind kind)
    {
        var h = NewService((Item, Fir: 3, NonFir: 3));

        h.Service.SetQuantity(Item, kind, 3);
        h.Service.AdjustQuantity(Item, kind, 0);

        Assert.Equal(3, h.Service.GetQuantity(Item, kind));
        Assert.Empty(h.PendingSaves);
        Assert.Equal(0, h.Changed);
    }

    // The same rule for an item nobody owns: "you own none of it" is what the store already
    // says, so nothing is staged - and no empty entry is left behind to be counted as an item
    // the player holds. (The per-kind twins this replaced inserted one before testing for a
    // change, so a write of 0 to an unknown item added a phantom entry.)
    [Theory]
    [MemberData(nameof(Kinds))]
    public void Writing_zero_for_an_unknown_item_leaves_no_entry_behind(FirKind kind)
    {
        var h = NewService();

        h.Service.SetQuantity(Item, kind, 0);
        h.Service.SetQuantity(Item, kind, -4);          // clamps to the same zero

        Assert.Empty(h.Inventory.Items);
        Assert.Empty(h.PendingSaves);
        Assert.Equal(0, h.Changed);
        Assert.Equal(0, h.Service.GetQuantity(Item, kind));
    }

    // An unknown item reads as nothing owned rather than throwing or creating an entry.
    [Theory]
    [MemberData(nameof(Kinds))]
    public void Reading_an_unknown_item_is_nothing_owned(FirKind kind)
    {
        var h = NewService();

        Assert.Equal(0, h.Service.GetQuantity(Item, kind));
        Assert.Empty(h.Inventory.Items);
    }

    // The six per-kind names the rest of the app still calls. IntegratedItemService reads two of
    // them and ConfigMigrationService's comment describes a third, so "the old name still means
    // the same half" is a fact worth running rather than assuming.
    [Theory]
    [MemberData(nameof(Kinds))]
    public void The_per_kind_names_still_read_and_write_the_same_half(FirKind kind)
    {
        var h = NewService((Item, Fir: 1, NonFir: 1));

        // Written through the legacy name, read through the kind-parameterised one...
        if (kind == FirKind.Fir) h.Service.SetFirQuantity(Item, 6);
        else h.Service.SetNonFirQuantity(Item, 6);
        Assert.Equal(6, h.Service.GetQuantity(Item, kind));
        Assert.Equal(1, h.Service.GetQuantity(Item, Other(kind)));

        // ...and back the other way, adjustment included.
        h.Service.SetQuantity(Item, kind, 2);
        if (kind == FirKind.Fir)
        {
            h.Service.AdjustFirQuantity(Item, 3);
            Assert.Equal(5, h.Service.GetFirQuantity(Item));
        }
        else
        {
            h.Service.AdjustNonFirQuantity(Item, 3);
            Assert.Equal(5, h.Service.GetNonFirQuantity(Item));
        }

        Assert.Equal(5, h.Service.GetQuantity(Item, kind));
        Assert.Equal(6, h.Service.GetTotalQuantity(Item));
    }

    // The legacy names clamp exactly as the kind-parameterised ones do, because they ARE the
    // kind-parameterised ones: a delta past zero through the old name empties the half.
    [Fact]
    public void The_per_kind_names_clamp_through_the_same_body()
    {
        var h = NewService((Item, Fir: 2, NonFir: 2));

        h.Service.AdjustFirQuantity(Item, -9);
        h.Service.AdjustNonFirQuantity(Item, -9);

        Assert.Equal(0, h.Service.GetFirQuantity(Item));
        Assert.Equal(0, h.Service.GetNonFirQuantity(Item));
        Assert.Equal(0, h.Service.GetTotalQuantity(Item));
        Assert.Empty(h.Inventory.Items);
    }

    // The model accessors the service branches on, at the one place a kind becomes a field.
    [Theory]
    [MemberData(nameof(Kinds))]
    public void The_inventory_row_reads_and_writes_the_field_its_kind_names(FirKind kind)
    {
        var inventory = new ItemInventory { ItemNormalizedName = Item };

        inventory.SetQuantityOf(kind, 4);

        Assert.Equal(4, inventory.QuantityOf(kind));
        Assert.Equal(0, inventory.QuantityOf(Other(kind)));
        Assert.Equal(4, kind == FirKind.Fir ? inventory.FirQuantity : inventory.NonFirQuantity);
        Assert.Equal(4, inventory.TotalQuantity);
    }
}

/// <summary>
/// The page half of the same axis, asserted against the source because neither item page can be
/// constructed in this suite (their markup resolves App.xaml's resources and their constructors
/// reach the singletons that open the databases), and no reachable behavioural test can drive a
/// click handler. What is pinned here is structure, not behaviour: that the per-kind twins are
/// gone, that what replaced them takes the kind as a parameter, and that the bodies doing the
/// work name no kind at all.
/// </summary>
public sealed class ItemQuantityHandlerSourceTests
{
    public static TheoryData<string> ItemPages() => new() { "CollectorPage", "ItemsPage" };

    private static string CodeBehind(string page) =>
        SourceGuards.Read("TarkovHelper", "Pages", page + ".xaml.cs");

    private static string Markup(string page) =>
        SourceGuards.Read("TarkovHelper", "Pages", page + ".xaml");

    /// <summary>How many times <paramref name="pattern"/> occurs in <paramref name="text"/>.</summary>
    private static int Occurrences(string text, string pattern) => Regex.Matches(text, pattern).Count;

    /// <summary>The per-kind method names both pages carried, one copy per kind of every rule.</summary>
    public static TheoryData<string, string> RetiredPerKindMethods()
    {
        var data = new TheoryData<string, string>();
        foreach (var page in new[] { "CollectorPage", "ItemsPage" })
        {
            foreach (var method in new[]
                     {
                         "AdjustFirQuantity", "AdjustNonFirQuantity",
                         "AdjustDetailFirQuantity", "AdjustDetailNonFirQuantity",
                         "ApplyFirQuantityFromTextBox", "ApplyNonFirQuantityFromTextBox",
                         "TxtDetailOwnedFir_LostFocus", "TxtDetailOwnedNonFir_LostFocus",
                         "TxtDetailOwnedFir_KeyDown", "TxtDetailOwnedNonFir_KeyDown",
                     })
            {
                data.Add(page, method);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RetiredPerKindMethods))]
    public void A_page_declares_no_per_kind_quantity_method(string page, string method)
    {
        Assert.DoesNotContain(method, CodeBehind(page), StringComparison.Ordinal);
    }

    /// <summary>
    /// The three methods that replaced them, with the signature that makes the kind data: one
    /// set per page, each appearing exactly once so a second copy cannot creep back in.
    /// </summary>
    public static TheoryData<string, string> KindParameterisedMethods()
    {
        var data = new TheoryData<string, string>();
        foreach (var page in new[] { "CollectorPage", "ItemsPage" })
        {
            data.Add(page, "private void AdjustRowQuantity(object sender, FirKind kind, int delta)");
            data.Add(page, "private void AdjustDetailQuantity(FirKind kind, int delta)");
            data.Add(page, "private void ApplyQuantityFromTextBox(TextBox box, FirKind kind)");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(KindParameterisedMethods))]
    public void A_page_writes_each_quantity_rule_once_with_the_kind_as_a_parameter(
        string page, string signature)
    {
        Assert.Equal(1, Occurrences(CodeBehind(page), Regex.Escape(signature)));
    }

    /// <summary>
    /// The names a body may not mention if the kind really is a parameter: the per-kind service
    /// calls, the per-kind row properties, a hard-coded kind and the two named boxes.
    /// </summary>
    private static readonly string[] PerKindNames =
    {
        "GetFirQuantity", "GetNonFirQuantity",
        "SetFirQuantity", "SetNonFirQuantity",
        "AdjustFirQuantity", "AdjustNonFirQuantity",
        "OwnedFirQuantity", "OwnedNonFirQuantity",
        "FirKind.Fir", "FirKind.NonFir",
        "TxtDetailOwnedFir", "TxtDetailOwnedNonFir",
    };

    [Theory]
    [MemberData(nameof(KindParameterisedMethods))]
    public void The_body_that_does_the_work_names_no_kind_at_all(string page, string signature)
    {
        var body = SourceGuards.MemberBody(CodeBehind(page), signature);

        foreach (var name in PerKindNames)
        {
            Assert.DoesNotContain(name, body, StringComparison.Ordinal);
        }
    }

    // Both quantity boxes raise one LostFocus and one KeyDown, the sharing their
    // PreviewTextInput already used. WPF's generated connector turns a handler named in markup
    // and missing from the code-behind into a build error, so what needs pinning is the pairing.
    [Theory]
    [MemberData(nameof(ItemPages))]
    public void Both_quantity_boxes_share_one_pair_of_text_handlers(string page)
    {
        var markup = Markup(page);

        Assert.Equal(2, Occurrences(markup, "LostFocus=\"TxtDetailOwned_LostFocus\""));
        Assert.Equal(2, Occurrences(markup, "KeyDown=\"TxtDetailOwned_KeyDown\""));
        Assert.Equal(2, Occurrences(markup, "PreviewTextInput=\"TxtDetailOwned_PreviewTextInput\""));

        var source = CodeBehind(page);
        Assert.Equal(1, Occurrences(source, "private void TxtDetailOwned_LostFocus"));
        Assert.Equal(1, Occurrences(source, "private void TxtDetailOwned_KeyDown"));
    }

    // The one place either page is allowed to say which box means which kind: the sender-keyed
    // resolver the shared pair calls. Anywhere else, a kind read off a control is the branch the
    // parameter was meant to replace.
    [Theory]
    [MemberData(nameof(ItemPages))]
    public void The_kind_is_read_from_the_sender_in_exactly_one_place(string page)
    {
        var source = CodeBehind(page);
        var resolver = SourceGuards.MemberBody(
            source, "private void ApplyQuantityFromSender(object sender)");

        Assert.Contains(
            "ApplyQuantityFromTextBox(TxtDetailOwnedFir, FirKind.Fir)", resolver, StringComparison.Ordinal);
        Assert.Contains(
            "ApplyQuantityFromTextBox(TxtDetailOwnedNonFir, FirKind.NonFir)", resolver, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(source, "private void ApplyQuantityFromSender"));
    }

    /// <summary>
    /// The service half, structurally: the six per-kind names survive only as one-line
    /// delegations over the kind-parameterised bodies, so there is no second copy of the write
    /// rule for a change to land in only one of.
    /// </summary>
    [Theory]
    [InlineData(
        "public int GetFirQuantity(string itemNormalizedName) =>",
        "GetQuantity(itemNormalizedName, FirKind.Fir);")]
    [InlineData(
        "public int GetNonFirQuantity(string itemNormalizedName) =>",
        "GetQuantity(itemNormalizedName, FirKind.NonFir);")]
    [InlineData(
        "public void SetFirQuantity(string itemNormalizedName, int quantity) =>",
        "SetQuantity(itemNormalizedName, FirKind.Fir, quantity);")]
    [InlineData(
        "public void SetNonFirQuantity(string itemNormalizedName, int quantity) =>",
        "SetQuantity(itemNormalizedName, FirKind.NonFir, quantity);")]
    [InlineData(
        "public void AdjustFirQuantity(string itemNormalizedName, int delta) =>",
        "AdjustQuantity(itemNormalizedName, FirKind.Fir, delta);")]
    [InlineData(
        "public void AdjustNonFirQuantity(string itemNormalizedName, int delta) =>",
        "AdjustQuantity(itemNormalizedName, FirKind.NonFir, delta);")]
    public void Each_per_kind_service_name_is_a_one_line_delegation(string declaration, string delegation)
    {
        var source = SourceGuards.Read("TarkovHelper", "Services", "ItemInventoryService.cs");

        var at = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{declaration}' no longer exists; update this test with it.");
        Assert.StartsWith(delegation, source[(at + declaration.Length)..].TrimStart(), StringComparison.Ordinal);
    }

    // And one body does the storing for both kinds: the dictionary write, the cleanup, the save
    // and the event live in SetQuantity alone.
    [Fact]
    public void One_body_stores_a_quantity_for_both_kinds()
    {
        var source = SourceGuards.Read("TarkovHelper", "Services", "ItemInventoryService.cs");
        var body = SourceGuards.MemberBody(
            source, "public void SetQuantity(string itemNormalizedName, FirKind kind, int quantity)");

        Assert.Equal(1, Occurrences(source, @"CleanupEmptyInventory\(itemNormalizedName\);"));
        Assert.Contains("CleanupEmptyInventory(itemNormalizedName);", body, StringComparison.Ordinal);
        Assert.Contains("ScheduleSave(itemNormalizedName);", body, StringComparison.Ordinal);
        Assert.Contains("InventoryChanged?.Invoke(this, EventArgs.Empty);", body, StringComparison.Ordinal);
        Assert.DoesNotContain("FirQuantity", body, StringComparison.Ordinal);
    }
}

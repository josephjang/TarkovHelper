using System.Text.Json.Serialization;

namespace TarkovHelper.Models
{
    /// <summary>
    /// Which half of an item's quantity is meant: the units Found In Raid, or the plain ones
    /// (bought on the flea market, crafted, looted out of raid).
    /// <para>
    /// The distinction is a value, not a pair of names. Spelling it as two of everything is how
    /// the app ended up with byte-identical twins of every quantity read, write and handler, one
    /// per kind, in the service and in both item pages; carrying it as data means the rule is
    /// written once and a caller picks the half at the boundary.
    /// </para>
    /// </summary>
    public enum FirKind
    {
        /// <summary>Found In Raid units.</summary>
        Fir,

        /// <summary>Units that are not Found In Raid.</summary>
        NonFir
    }

    /// <summary>
    /// Represents user's inventory quantity for an item with FIR/Non-FIR separation
    /// </summary>
    public class ItemInventory
    {
        /// <summary>
        /// Item normalized name (key for lookup)
        /// </summary>
        [JsonPropertyName("itemNormalizedName")]
        public string ItemNormalizedName { get; set; } = string.Empty;

        /// <summary>
        /// Found in Raid quantity
        /// </summary>
        [JsonPropertyName("firQuantity")]
        public int FirQuantity { get; set; }

        /// <summary>
        /// Non-FIR quantity (purchased from flea market, etc.)
        /// </summary>
        [JsonPropertyName("nonFirQuantity")]
        public int NonFirQuantity { get; set; }

        /// <summary>
        /// Total quantity (FIR + Non-FIR)
        /// </summary>
        [JsonIgnore]
        public int TotalQuantity => FirQuantity + NonFirQuantity;

        /// <summary>
        /// The units held of one kind. The only place a <see cref="FirKind"/> is turned into a
        /// field read, so nothing above this class has to know which field holds which half.
        /// </summary>
        public int QuantityOf(FirKind kind) => kind == FirKind.Fir ? FirQuantity : NonFirQuantity;

        /// <summary>
        /// Writes the units held of one kind, the mirror of <see cref="QuantityOf"/> and the only
        /// place a <see cref="FirKind"/> is turned into a field write.
        /// </summary>
        public void SetQuantityOf(FirKind kind, int quantity)
        {
            if (kind == FirKind.Fir)
            {
                FirQuantity = quantity;
            }
            else
            {
                NonFirQuantity = quantity;
            }
        }
    }

    /// <summary>
    /// Container for all item inventory data (for JSON serialization)
    /// </summary>
    public class ItemInventoryData
    {
        [JsonPropertyName("lastUpdated")]
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("items")]
        public Dictionary<string, ItemInventory> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fulfillment status for an item requirement
    /// </summary>
    public enum ItemFulfillmentStatus
    {
        /// <summary>
        /// No items owned (0/required)
        /// </summary>
        NotStarted,

        /// <summary>
        /// Some items owned but not enough
        /// </summary>
        PartiallyFulfilled,

        /// <summary>
        /// All requirements met
        /// </summary>
        Fulfilled
    }

    /// <summary>
    /// The one rule for "does what the player owns satisfy an item requirement".
    /// <para>
    /// A requirement has two halves: <c>requiredTotal</c> units in all, of which
    /// <c>requiredFir</c> must be Found In Raid. The two differ whenever the same item is wanted
    /// FIR by one quest and plain by another (the Collector page aggregates exactly that), so
    /// both halves have to be met: the FIR units cover the FIR half, and the units left over
    /// cover the remainder. A FIR unit counts toward the plain remainder because a raid-found
    /// item is still an item; a non-FIR unit never fills a FIR slot.
    /// </para>
    /// </summary>
    public static class ItemFulfillment
    {
        /// <summary>
        /// Units the requirement really asks for. Normally <paramref name="requiredTotal"/>; the
        /// FIR half wins if data ever reports more FIR units than units, so a bad row cannot read
        /// satisfied on the smaller number.
        /// </summary>
        public static int RequiredUnits(int requiredTotal, int requiredFir) =>
            Math.Max(Math.Max(requiredTotal, 0), Math.Max(requiredFir, 0));

        /// <summary>Both halves met: the FIR units owned and the units owned in all.</summary>
        public static bool IsFulfilled(int ownedFir, int ownedNonFir, int requiredTotal, int requiredFir) =>
            Clamp(ownedFir) >= Math.Max(requiredFir, 0)
            && Clamp(ownedFir) + Clamp(ownedNonFir) >= RequiredUnits(requiredTotal, requiredFir);

        /// <summary>
        /// The requirement's status: fulfilled when both halves are met, not started while
        /// nothing at all is owned, partially fulfilled in between.
        /// </summary>
        public static ItemFulfillmentStatus StatusOf(int ownedFir, int ownedNonFir, int requiredTotal, int requiredFir)
        {
            if (IsFulfilled(ownedFir, ownedNonFir, requiredTotal, requiredFir))
                return ItemFulfillmentStatus.Fulfilled;
            if (Clamp(ownedFir) + Clamp(ownedNonFir) > 0)
                return ItemFulfillmentStatus.PartiallyFulfilled;
            return ItemFulfillmentStatus.NotStarted;
        }

        /// <summary>
        /// How much of the requirement is covered, 0 to 100, counting the units that actually
        /// fill a slot: the FIR units up to the FIR half, then whatever is left over up to the
        /// plain remainder. So a row is only 100% once it is fulfilled. A requirement of nothing
        /// reads full.
        /// </summary>
        public static double ProgressPercent(int ownedFir, int ownedNonFir, int requiredTotal, int requiredFir)
        {
            var required = RequiredUnits(requiredTotal, requiredFir);
            if (required == 0) return 100;

            var firNeeded = Math.Min(Math.Max(requiredFir, 0), required);
            var firUsed = Math.Min(Clamp(ownedFir), firNeeded);
            var anyUsed = Math.Min(Clamp(ownedFir) + Clamp(ownedNonFir) - firUsed, required - firNeeded);

            return (double)(firUsed + anyUsed) / required * 100;
        }

        private static int Clamp(int owned) => Math.Max(owned, 0);
    }

    /// <summary>
    /// The one way item counts are written for the UI. Both item lists (the Items page and the
    /// Collector page) print what a requirement asks for and what the player owns in these two
    /// shapes, so the shapes live here beside <see cref="ItemFulfillment"/>, the rule they
    /// describe. The Hideout page's per module line is a different shape and stays its own.
    /// </summary>
    public static class ItemCountDisplay
    {
        /// <summary>
        /// What a requirement asks for: "3" when nothing has to be Found In Raid, "3 (FIR)" when
        /// every unit does, "2F+1" when it is mixed. The units printed are the ones
        /// <see cref="ItemFulfillment.RequiredUnits"/> asks for, so a row reporting more FIR units
        /// than units (bad data) reads as all FIR instead of printing a negative remainder, and a
        /// negative count reads as nothing required instead of putting a minus sign on screen.
        /// </summary>
        public static string Required(int requiredTotal, int requiredFir)
        {
            var units = ItemFulfillment.RequiredUnits(requiredTotal, requiredFir);
            var fir = Math.Min(Math.Max(requiredFir, 0), units);
            if (fir == 0)
                return units.ToString();
            if (fir == units)
                return $"{fir} (FIR)";
            return $"{fir}F+{units - fir}";
        }

        /// <summary>
        /// What the player owns: "0" while nothing is, "2F" when every unit owned is Found In
        /// Raid, "2" when none is, "2F+1" when both kinds are held. A negative quantity (bad
        /// data) counts as none of that kind, so a row cannot print a negative and one kind
        /// cannot cancel the other out.
        /// </summary>
        public static string Owned(int ownedFir, int ownedNonFir)
        {
            var fir = Math.Max(ownedFir, 0);
            var nonFir = Math.Max(ownedNonFir, 0);
            if (fir + nonFir == 0)
                return "0";
            if (nonFir == 0)
                return $"{fir}F";
            if (fir == 0)
                return nonFir.ToString();
            return $"{fir}F+{nonFir}";
        }
    }

    /// <summary>
    /// Detailed fulfillment information for an item
    /// </summary>
    public class ItemFulfillmentInfo
    {
        /// <summary>
        /// Item normalized name
        /// </summary>
        public string ItemNormalizedName { get; set; } = string.Empty;

        /// <summary>
        /// Total required quantity
        /// </summary>
        public int RequiredTotal { get; set; }

        /// <summary>
        /// Required FIR quantity (if FIR is required)
        /// </summary>
        public int RequiredFir { get; set; }

        /// <summary>
        /// User's FIR quantity owned
        /// </summary>
        public int OwnedFir { get; set; }

        /// <summary>
        /// User's Non-FIR quantity owned
        /// </summary>
        public int OwnedNonFir { get; set; }

        /// <summary>
        /// Total owned (FIR + Non-FIR)
        /// </summary>
        public int OwnedTotal => OwnedFir + OwnedNonFir;

        /// <summary>
        /// Whether the FIR half of the requirement is met
        /// </summary>
        public bool IsFirFulfilled => OwnedFir >= RequiredFir;

        /// <summary>
        /// Whether the requirement's unit count is met, FIR units included
        /// </summary>
        public bool IsTotalFulfilled =>
            OwnedTotal >= ItemFulfillment.RequiredUnits(RequiredTotal, RequiredFir);

        /// <summary>
        /// Overall fulfillment status: both halves of the requirement, never the FIR half alone
        /// (see <see cref="ItemFulfillment"/>)
        /// </summary>
        public ItemFulfillmentStatus Status =>
            ItemFulfillment.StatusOf(OwnedFir, OwnedNonFir, RequiredTotal, RequiredFir);

        /// <summary>
        /// Progress percentage (0-100) over both halves, so it reads 100 only when fulfilled
        /// </summary>
        public double ProgressPercent =>
            ItemFulfillment.ProgressPercent(OwnedFir, OwnedNonFir, RequiredTotal, RequiredFir);
    }
}

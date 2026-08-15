namespace TarkovHelper.Services;

/// <summary>
/// The shared revision gate behind every profile-scoped reload.
/// <para>
/// Four services reload their caches when the active profile changes, and all four guard the
/// reload the same way: claim the event's revision before the store read, then re-check after it
/// and discard the result if a newer reload has since claimed the counter. This type owns the
/// claim half, which was previously copied byte-for-byte into
/// <see cref="SettingsService"/>, <see cref="QuestProgressService"/>,
/// <see cref="HideoutProgressService"/> and <see cref="ItemInventoryService"/>.
/// </para>
/// <para>
/// It stays a static helper over a caller-owned <c>long</c> rather than a struct wrapping the
/// counter: each service already exposes its own <c>_latestRevision</c> to other guard code
/// (the post-read check reads it directly through <see cref="Interlocked.Read"/>), so hiding the
/// field behind a type would force that half through an accessor for no added safety.
/// </para>
/// </summary>
internal static class RevisionGate
{
    /// <summary>
    /// Raises <paramref name="latest"/> to <paramref name="revision"/> if it is newer, leaving it
    /// alone otherwise. Safe to call from any thread.
    /// <para>
    /// The CAS loop, rather than a plain compare-and-store, is what makes the counter monotonic
    /// under concurrent reloads: two handler threads can read the same <c>current</c>, and the one
    /// whose exchange loses re-reads instead of overwriting the winner's newer value.
    /// </para>
    /// </summary>
    /// <param name="latest">The caller's revision counter, updated in place.</param>
    /// <param name="revision">The revision this reload is claiming.</param>
    internal static void Claim(ref long latest, long revision)
    {
        while (true)
        {
            var current = Interlocked.Read(ref latest);
            if (revision <= current) return;
            if (Interlocked.CompareExchange(ref latest, revision, current) == current) return;
        }
    }
}

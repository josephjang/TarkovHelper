using System.Globalization;
using System.Text.Json;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// The versioned data channel's wire protocol: where the endpoints are and what the
/// documents they serve are allowed to say. Everything here is static and stateless,
/// so it can be read and tested without an update ever running.
/// <para>
/// <see cref="DatabaseUpdateService"/> is the client of this protocol and owns
/// everything that happens to a machine: when to poll, what to download, and how the
/// payload reaches Assets\. Kept apart because the two drift for different reasons -
/// the protocol changes when the published documents do, the client when the install
/// procedure does - and every rule about a document belongs on the same side as the
/// parser that enforces it.
/// </para>
/// <para>
/// Which data format this build reads is deliberately NOT here: it identifies the
/// build rather than the wire, and most of its readers ask it that way (which seed the
/// repo ships, which pin the csproj carries, which baseline the drift test compares).
/// It stays on <see cref="DatabaseUpdateService.DataFormatVersion"/>, and this class
/// reads it in exactly one place, to name the endpoint such a build polls.
/// </para>
/// Design: feature-versioned-data-channel.spec.md.
/// </summary>
internal static class DataChannel
{
    private static readonly ILogger _log = Log.For(nameof(DataChannel));

    private const string INDEX_FILE = "index.json";
    private const string MANIFEST_FILE = "manifest.json";

    /// <summary>
    /// Document shapes this build can read. Only ever compared as an upper bound: a
    /// lower bound is unnecessary because the endpoint URL already selects which
    /// documents this build can meet at all.
    /// <para>
    /// The two documents answer a newer shape differently, on purpose. A manifest above
    /// this bound is refused outright (<see cref="DatabaseUpdateService.RunCheckAsync"/>),
    /// because declining an install is always safe. An index above it is still read for
    /// the one field every index schema promises to carry (<see cref="ParseIndex"/>),
    /// because refusing it would cost a stranded build the only notice it will ever get.
    /// </para>
    /// <para>
    /// "Schema version" here means the shape of the JSON document itself, the sense
    /// Docker's manifest <c>schemaVersion</c> and TUF's <c>spec_version</c> use. The
    /// contract of the database the document describes is the data format, which is a
    /// different thing and covers more (see
    /// <see cref="DatabaseUpdateService.DataFormatVersion"/>).
    /// </para>
    /// </summary>
    internal const int MAX_SUPPORTED_SCHEMA_VERSION = 1;

    /// <summary>Channel root, holding index.json beside one directory per data format version.</summary>
    internal static readonly string DATA_ROOT_URL =
        "https://raw.githubusercontent.com/josephjang/TarkovHelper/refs/heads/main/data";
    internal static readonly string INDEX_URL = BuildIndexUrl(DATA_ROOT_URL);
    internal static readonly string CHANNEL_BASE_URL =
        BuildChannelBaseUrl(DATA_ROOT_URL, DatabaseUpdateService.DataFormatVersion);
    internal static readonly string MANIFEST_URL = BuildManifestUrl(CHANNEL_BASE_URL);

    /// <summary>
    /// The channel's three constant URLs, in one place: the static fields above (which the
    /// tests pin) and the per-instance URLs <see cref="DatabaseUpdateService"/>'s
    /// constructor derives both go through these, so a build can never fetch a constant
    /// part of the layout different from the one under test.
    /// <para>
    /// A fourth URL these do not cover completes the layout, because it is the only one
    /// built from data rather than constants: the payload, joined onto the channel base in
    /// <see cref="DatabaseUpdateService.DownloadDatabaseAsync"/> from the name the manifest
    /// gave. What keeps that one on this endpoint is <see cref="IsBarePayloadName"/>,
    /// checked at the parse boundary, and the served-channel tests, which pin the path the
    /// download actually requests.
    /// </para>
    /// </summary>
    internal static string BuildIndexUrl(string dataRootUrl) => $"{dataRootUrl}/{INDEX_FILE}";

    /// <summary>
    /// The endpoint directory a build reading <paramref name="dataFormatVersion"/> polls.
    /// The format is a parameter rather than a read of
    /// <see cref="DatabaseUpdateService.DataFormatVersion"/>, so the URL grammar this class
    /// owns stays independent of which build is asking; the caller supplies its own identity.
    /// </summary>
    internal static string BuildChannelBaseUrl(string dataRootUrl, int dataFormatVersion) => string.Format(
        CultureInfo.InvariantCulture, "{0}/v{1}", dataRootUrl, dataFormatVersion);

    internal static string BuildManifestUrl(string channelBaseUrl) => $"{channelBaseUrl}/{MANIFEST_FILE}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// The payload an endpoint serves. Integrity fields are optional by design.
    /// <para>
    /// <c>Digest</c> is algorithm-qualified, <c>"sha256:&lt;hex&gt;"</c>, following OCI and
    /// Sigstore. The prefix is what lets a build that only knows sha256 recognize a
    /// digest it cannot check, instead of mistaking it for an absent one and skipping
    /// verification without ever noticing. A digest missing the prefix defeats exactly
    /// that, so it is refused rather than read as an unknown algorithm.
    /// </para>
    /// </summary>
    internal sealed record Payload(string File, string? Digest, long? Size);

    /// <summary>data/v&lt;N&gt;/manifest.json: what this endpoint currently offers.</summary>
    internal sealed record Manifest(
        int SchemaVersion, int DataFormatVersion, string Version, Payload Database);

    /// <summary>data/index.json: the data format version the project publishes right now.</summary>
    internal sealed record Index(int SchemaVersion, int CurrentDataFormatVersion);

    /// <summary>
    /// Parses a manifest document. Returns null for anything unreadable, which callers
    /// treat as a failed check: no download and no local state change. Unknown fields
    /// are ignored, so an endpoint can carry information newer builds use without
    /// disturbing the ones already shipped.
    /// <para>
    /// "Unreadable" includes a payload name this build will not turn into a URL (see
    /// <see cref="IsBarePayloadName"/>) and a version token that would not survive the
    /// local bookmark (see <see cref="IsBareVersionToken"/>). Rejecting them here rather
    /// than at the download keeps the whole document either usable or absent, with no
    /// half-trusted middle.
    /// </para>
    /// <para>
    /// Both tokens are trimmed before they are judged, the same way the digest is: a
    /// stray space around a value in a hand-edited document is a formatting slip, not a
    /// different value, and refusing the whole manifest over one would stop every install
    /// updating until someone noticed.
    /// </para>
    /// </summary>
    internal static Manifest? ParseManifest(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(content, JsonOptions);
            if (manifest == null) return null;

            var version = manifest.Version?.Trim();
            var database = manifest.Database;
            var payloadFile = database?.File?.Trim();

            // Required fields, checked explicitly: System.Text.Json leaves a missing
            // string null rather than failing, and a null version would compare unequal
            // to every local version and re-download the database forever. A missing
            // "database" object is rejected on its own line rather than left to the
            // payload-name check it already implies, so the non-null object the record
            // update below copies from is proven here rather than argued about.
            if (manifest.SchemaVersion < 1
                || manifest.DataFormatVersion < 1
                || database == null
                || !IsBareVersionToken(version)
                || !IsBarePayloadName(payloadFile))
            {
                return null;
            }

            // The trimmed tokens are what the rest of the check uses: the version is
            // written to the bookmark and the name is pasted onto the endpoint URL, so
            // whatever was validated here has to be the value that travels on.
            return manifest with
            {
                Version = version!,
                Database = database with { File = payloadFile! },
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a manifest's version token is one this build can carry through its local
    /// bookmark unchanged.
    /// <para>
    /// The token is written verbatim to db_version.txt and read back as the first
    /// non-blank line of that file (see
    /// <see cref="DatabaseUpdateService.LoadLocalVersion"/>), so a token carrying a line
    /// break comes back truncated, compares unequal to the published version on every
    /// launch, and re-downloads the whole database hourly for the life of the install. An
    /// allowlist rather than a line-break check for the same reason
    /// <see cref="IsBarePayloadName"/> is one: it cannot be walked around by a separator
    /// nobody thought of.
    /// </para>
    /// <para>
    /// The permitted set is semver's own version grammar (<c>[0-9A-Za-z.-]</c> plus
    /// <c>+</c> for build metadata) with <c>_</c>, which covers every shape this channel
    /// or a CalVer successor could publish. Ordering is deliberately not implied: the
    /// check compares tokens for equality only.
    /// </para>
    /// </summary>
    internal static bool IsBareVersionToken(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;

        foreach (var c in version)
        {
            var allowed = (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9')
                || c is '.' or '-' or '_' or '+';

            if (!allowed) return false;
        }

        return true;
    }

    /// <summary>
    /// Whether a manifest's payload name is a plain file sitting in the endpoint
    /// directory that named it.
    /// <para>
    /// The name is concatenated onto the channel base URL, and URI normalization
    /// resolves a <c>..</c> segment against that base before the request goes out, so an
    /// unchecked name can point the download at another format's directory (or, with a
    /// scheme, at another host) while the manifest that named it still looks internally
    /// consistent. The digest and size are optional, so nothing downstream is guaranteed
    /// to notice the substitution.
    /// </para>
    /// <para>
    /// Deliberately an allowlist rather than a blocklist of separators: the channel only
    /// ever publishes names like <c>tarkov_data.db</c>, and an allowlist cannot be
    /// walked around by percent-encoding, a URI scheme, a query string, or a drive
    /// letter. With separators excluded the whole name is one path segment, so only a
    /// segment that is entirely <c>.</c> or <c>..</c> can still traverse.
    /// </para>
    /// </summary>
    internal static bool IsBarePayloadName(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return false;
        if (file is "." or "..") return false;

        foreach (var c in file)
        {
            var allowed = (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9')
                || c is '.' or '-' or '_';

            if (!allowed) return false;
        }

        return true;
    }

    /// <summary>
    /// Splits a manifest digest into the algorithm that produced it and the hex it
    /// expects, or null when the string is not in <c>"&lt;algorithm&gt;:&lt;hex&gt;"</c>
    /// form.
    /// <para>
    /// The grammar lives here, beside the other channel-document rules and reachable
    /// from a table of cases, rather than inline in the one caller: it is what lets a
    /// build tell "a digest I cannot check" apart from "no digest at all", and getting
    /// it subtly wrong turns verification off without anything saying so.
    /// </para>
    /// <para>
    /// Both halves are trimmed, so a hand-edited manifest carrying a stray space around
    /// the value is still checked. Neither half may be empty: the whole point of the
    /// prefix is that it names something, so <c>"sha256:"</c> and <c>":abc"</c> are
    /// malformed rather than partially usable. Absence is NOT this method's business;
    /// the caller decides what a missing digest means, so the two stay distinguishable.
    /// </para>
    /// </summary>
    internal static (string Algorithm, string Hex)? ParseDigest(string? digest)
    {
        if (digest == null) return null;

        var separator = digest.IndexOf(':');
        if (separator <= 0) return null;

        var algorithm = digest[..separator].Trim();
        var hex = digest[(separator + 1)..].Trim();

        if (algorithm.Length == 0 || hex.Length == 0) return null;

        return (algorithm, hex);
    }

    /// <summary>
    /// Parses the channel index. Returns null for anything unreadable; the caller keeps
    /// its previous knowledge rather than assuming the build is current.
    /// <para>
    /// A shape newer than <see cref="MAX_SUPPORTED_SCHEMA_VERSION"/> is deliberately NOT
    /// unreadable here, unlike the manifest path. The two documents fail in opposite
    /// directions: refusing a manifest this build cannot read means declining an install,
    /// which is always safe, while refusing the index means never learning this build was
    /// left behind. The moment index.json is most likely to change shape is the publish
    /// that bumps the data format, which is the very publish that strands the builds
    /// reading this code, so treating a newer shape as unreadable would switch the notice
    /// off for exactly the users it exists for.
    /// </para>
    /// <para>
    /// What makes that safe is a compatibility promise about one field:
    /// <c>currentDataFormatVersion</c> keeps its name and its meaning in every future
    /// index schema, because fielded builds derive their stranded notice from it and can
    /// never be taught a replacement. A schema that renames or drops it degrades to the
    /// old behavior rather than lying, since the field then reads 0 and the document is
    /// refused below; only repurposing it under the same name could mislead, which is
    /// what this promise forbids. Fields this build has never heard of are ignored, the
    /// way the manifest ignores its own.
    /// </para>
    /// </summary>
    internal static Index? ParseIndex(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        try
        {
            var index = JsonSerializer.Deserialize<Index>(content, JsonOptions);
            if (index is not { SchemaVersion: >= 1, CurrentDataFormatVersion: >= 1 })
            {
                return null;
            }

            if (index.SchemaVersion > MAX_SUPPORTED_SCHEMA_VERSION)
            {
                _log.Warning(
                    $"Channel index declares schema version {index.SchemaVersion}, above the "
                    + $"{MAX_SUPPORTED_SCHEMA_VERSION} this build understands. Reading only the "
                    + "data format version it publishes, which every index schema carries.");
            }

            return index;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

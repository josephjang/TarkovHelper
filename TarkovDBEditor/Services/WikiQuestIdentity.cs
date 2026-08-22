using System;
using System.Text;

namespace TarkovDBEditor.Services
{
    /// <summary>
    /// The encoding that turns a wiki quest page title into the row key the published
    /// database and every build in the field use, and back again.
    /// <para>
    /// A quest's <c>Quests.Id</c> is base64 of its wiki page URL. Until the 1.1 refresh it
    /// was recomputed from the current title on every run, which is why a renamed page
    /// detached the user's recorded progress. It is now minted once, when the quest is first
    /// imported, and carried forward by external ID
    /// (<see cref="QuestIdentityResolver"/>) - so <see cref="TitleOf"/> answers "which title
    /// was this row minted under", not "what is this quest called today".
    /// </para>
    /// <para>
    /// That distinction is what the publish guard checks:
    /// <c>NormalizedName == QuestNormalizedName.SqlForm(TitleOf(Id))</c> holds for every row,
    /// renamed or not, because both sides describe the original title.
    /// </para>
    /// </summary>
    public static class WikiQuestIdentity
    {
        /// <summary>Prefix every quest page URL carries; also the marker <see cref="TitleOf"/> strips.</summary>
        public const string WikiUrlPrefix = "https://escapefromtarkov.fandom.com/wiki/";

        /// <summary>
        /// The page URL for a title, in the exact spelling tarkov.dev's <c>wikiLink</c> uses:
        /// spaces to underscores, percent-encoded, except parentheses which the wiki leaves bare.
        /// </summary>
        public static string PageLinkFor(string title)
        {
            ArgumentNullException.ThrowIfNull(title);

            var encoded = Uri.EscapeDataString(title.Replace(" ", "_"))
                .Replace("%28", "(")
                .Replace("%29", ")");
            return WikiUrlPrefix + encoded;
        }

        /// <summary>Base64 of the page URL: the value stored in <c>Quests.Id</c>.</summary>
        public static string IdFor(string title) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(PageLinkFor(title)));

        /// <summary>
        /// The inverse of <see cref="IdFor"/>: the page title the row key was minted from.
        /// Returns null when the key is not base64 of a wiki page URL, which is how a hand
        /// edited or foreign key shows up rather than as an exception mid-publish.
        /// </summary>
        public static string? TitleOf(string questId)
        {
            if (string.IsNullOrEmpty(questId))
                return null;

            string url;
            try
            {
                url = Encoding.UTF8.GetString(Convert.FromBase64String(questId));
            }
            catch (FormatException)
            {
                return null;
            }

            if (!url.StartsWith(WikiUrlPrefix, StringComparison.Ordinal))
                return null;

            var encoded = url.Substring(WikiUrlPrefix.Length);
            if (encoded.Length == 0)
                return null;

            try
            {
                return Uri.UnescapeDataString(encoded).Replace("_", " ");
            }
            catch (UriFormatException)
            {
                return null;
            }
        }
    }
}

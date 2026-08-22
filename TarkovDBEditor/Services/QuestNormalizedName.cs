using System;
using System.Text;

namespace TarkovDBEditor.Services
{
    /// <summary>
    /// The C# equivalent of the normalized quest name both TarkovHelper builds compute for
    /// themselves when the <c>Quests.NormalizedName</c> column is absent:
    /// <c>LOWER(REPLACE(REPLACE(REPLACE(Name, ' ', '-'), '''', ''), '.', ''))</c>
    /// (<c>QuestDbService.LoadBaseQuestsAsync</c>).
    /// <para>
    /// Recorded progress is keyed by that value (<c>QuestProgress.NormalizedName</c>), so the
    /// column the pipeline now writes has to reproduce the expression exactly. Writing the
    /// tarkov.dev style instead (<c>sew-it-good-part-4</c> rather than
    /// <c>sew-it-good---part-4</c>) would un-key 228 of the 488 published quests in every
    /// build in the field while looking, to the schema drift guard, like a purely additive
    /// column. See docs/decisions/feature-quest-data-1-1-refresh.spec.md, "NormalizedName is
    /// pinned to the app's SQL expression".
    /// </para>
    /// </summary>
    public static class QuestNormalizedName
    {
        /// <summary>
        /// Reproduces the app's SQL expression: spaces become dashes, the ASCII apostrophe
        /// (U+0027) and the period are dropped, and A-Z is lowered.
        /// <para>
        /// Only A-Z is lowered because that is all SQLite's <c>LOWER</c> does: the bundled
        /// e_sqlite3 is built without ICU, so it leaves every non-ASCII letter alone. The
        /// typographic apostrophe U+2019 in "What's on the Flash Drive?" survives here for
        /// the same reason it survives in SQLite's <c>REPLACE</c> chain, which only ever
        /// looks for U+0027.
        /// </para>
        /// </summary>
        public static string SqlForm(string name)
        {
            ArgumentNullException.ThrowIfNull(name);

            var result = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                switch (c)
                {
                    case ' ':
                        result.Append('-');
                        break;
                    case '\'':
                    case '.':
                        break;
                    default:
                        result.Append(c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c);
                        break;
                }
            }

            return result.ToString();
        }
    }
}

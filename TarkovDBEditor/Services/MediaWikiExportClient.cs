using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TarkovDBEditor.Services
{
    /// <summary>
    /// Fetches page wikitext as MediaWiki export XML, through the wiki's <c>api.php</c>.
    /// <para>
    /// The crawl used to POST to <c>/wiki/Special:Export</c>. Fandom put that path behind a
    /// Cloudflare challenge, which answers every request with 403 and a
    /// <c>cf-mitigated: challenge</c> header no user agent gets past, so an export of any size
    /// failed and the whole regeneration stopped at the wiki step. <c>api.php</c> is not
    /// challenged, and <c>action=query&amp;export=1&amp;exportnowrap=1</c> returns the same
    /// <c>export-0.11</c> document the old path did, down to the revision id each page carries,
    /// so only the request changes and every parser downstream stays as it was.
    /// </para>
    /// <para>
    /// The one difference is the title limit: <c>Special:Export</c> took an unbounded
    /// newline-separated list, and <c>api.php</c> takes at most
    /// <see cref="MaxTitlesPerRequest"/> pipe-separated titles per request for an anonymous
    /// caller, silently answering only a prefix of a longer list. Callers already batch at that
    /// size; <see cref="CreateRequest"/> refuses a larger batch rather than let a future one
    /// lose pages without saying so.
    /// </para>
    /// </summary>
    public sealed class MediaWikiExportClient
    {
        /// <summary>
        /// Titles one export request may name. The wiki's own cap for an anonymous caller, and
        /// the batch size every caller of this client chunks to.
        /// </summary>
        public const int MaxTitlesPerRequest = 50;

        private const string ApiUrl = "https://escapefromtarkov.fandom.com/api.php";

        private readonly HttpClient _httpClient;

        public MediaWikiExportClient(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        /// <summary>
        /// Builds the export request for <paramref name="titles"/> without sending it, for the
        /// caller that streams the response instead of buffering it.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The batch is empty, or names more than <see cref="MaxTitlesPerRequest"/> pages.
        /// </exception>
        public HttpRequestMessage CreateRequest(IReadOnlyCollection<string> titles)
        {
            if (titles == null) throw new ArgumentNullException(nameof(titles));
            if (titles.Count == 0)
                throw new ArgumentException("An export request must name at least one page.", nameof(titles));
            if (titles.Count > MaxTitlesPerRequest)
                throw new ArgumentException(
                    $"An export request may name at most {MaxTitlesPerRequest} pages, and this one names "
                    + $"{titles.Count}. The wiki answers a longer list with a prefix of it, so the pages "
                    + "past the limit would go missing without an error. Batch the list first.",
                    nameof(titles));

            // Titles are pipe-separated here, where Special:Export took them newline-separated.
            // Special:Export's curonly has no counterpart: the query module exports the current
            // revision and nothing else, and passing curonly to it changes no byte of the reply.
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("action", "query"),
                new KeyValuePair<string, string>("export", "1"),
                new KeyValuePair<string, string>("exportnowrap", "1"),
                new KeyValuePair<string, string>("titles", string.Join("|", titles)),
            });

            return new HttpRequestMessage(HttpMethod.Post, ApiUrl) { Content = content };
        }

        /// <summary>
        /// Fetches the export XML for one batch of titles. A title that names no page is absent
        /// from the document rather than an error, exactly as it was under Special:Export.
        /// </summary>
        public async Task<string> ExportXmlAsync(
            IReadOnlyCollection<string> titles,
            CancellationToken cancellationToken = default)
        {
            using var request = CreateRequest(titles);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        /// <summary>
        /// Splits <paramref name="titles"/> into batches this client will accept.
        /// </summary>
        public static IEnumerable<List<string>> Batch(IEnumerable<string> titles)
        {
            var batch = new List<string>(MaxTitlesPerRequest);
            foreach (var title in titles)
            {
                batch.Add(title);
                if (batch.Count < MaxTitlesPerRequest) continue;

                yield return batch;
                batch = new List<string>(MaxTitlesPerRequest);
            }

            if (batch.Count > 0) yield return batch;
        }
    }
}

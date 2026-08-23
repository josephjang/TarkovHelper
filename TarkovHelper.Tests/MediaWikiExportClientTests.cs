using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TarkovDBEditor.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// The wire shape of the wiki crawl's page fetch.
/// <para>
/// The crawl POSTed to <c>/wiki/Special:Export</c> until Fandom put that path behind a
/// Cloudflare challenge, which answers 403 with <c>cf-mitigated: challenge</c> to every user
/// agent and stopped the regeneration at the wiki step. The replacement is the wiki's own
/// <c>api.php</c> export, which returns the same document but caps a request at
/// <see cref="MediaWikiExportClient.MaxTitlesPerRequest"/> titles and answers a longer list
/// with a prefix of it. A silently truncated batch is the dangerous failure here: the pages
/// that never arrive look to the refresh like pages that no longer exist, and stale rows are
/// deleted. So the cap is enforced on the way out, not hoped for.
/// </para>
/// </summary>
public sealed class MediaWikiExportClientTests
{
    [Fact]
    public void Posts_the_export_query_to_the_wiki_api()
    {
        var client = new MediaWikiExportClient(new HttpClient());

        using var request = client.CreateRequest(new[] { "Stirrup" });

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://escapefromtarkov.fandom.com/api.php", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Asks_for_the_current_revision_as_bare_export_xml()
    {
        var client = new MediaWikiExportClient(new HttpClient());

        using var request = client.CreateRequest(new[] { "Stirrup" });
        var form = await ReadFormAsync(request);

        Assert.Equal("query", form["action"]);
        Assert.Equal("1", form["export"]);
        // Without exportnowrap the XML arrives wrapped in a JSON envelope the parsers cannot read.
        Assert.Equal("1", form["exportnowrap"]);
        // Special:Export's curonly has no counterpart here: the query module exports the current
        // revision only, and sending it would be a parameter the endpoint ignores.
        Assert.DoesNotContain("curonly", form.Keys);
    }

    [Fact]
    public async Task Separates_titles_with_pipes_not_newlines()
    {
        // Special:Export took a newline-separated list; api.php takes a pipe-separated one and
        // would read a newline-joined list as a single title that names no page.
        var client = new MediaWikiExportClient(new HttpClient());

        using var request = client.CreateRequest(new[] { "Stirrup", "Debut", "The Tarkov Shooter - Part 5" });
        var form = await ReadFormAsync(request);

        Assert.Equal("Stirrup|Debut|The Tarkov Shooter - Part 5", form["titles"]);
    }

    [Fact]
    public void Refuses_a_batch_larger_than_one_request_can_carry()
    {
        var client = new MediaWikiExportClient(new HttpClient());
        var titles = Enumerable.Range(0, MediaWikiExportClient.MaxTitlesPerRequest + 1)
            .Select(i => $"Quest {i}")
            .ToArray();

        var error = Assert.Throws<ArgumentException>(() => client.CreateRequest(titles));

        Assert.Contains(MediaWikiExportClient.MaxTitlesPerRequest.ToString(), error.Message);
    }

    [Fact]
    public void Accepts_a_batch_of_exactly_the_limit()
    {
        var client = new MediaWikiExportClient(new HttpClient());
        var titles = Enumerable.Range(0, MediaWikiExportClient.MaxTitlesPerRequest)
            .Select(i => $"Quest {i}")
            .ToArray();

        using var request = client.CreateRequest(titles);

        Assert.NotNull(request.Content);
    }

    [Fact]
    public void Refuses_an_empty_batch()
    {
        var client = new MediaWikiExportClient(new HttpClient());

        Assert.Throws<ArgumentException>(() => client.CreateRequest(Array.Empty<string>()));
    }

    [Fact]
    public async Task Returns_the_export_document_unchanged()
    {
        const string xml = "<mediawiki><page><title>Stirrup</title></page></mediawiki>";
        var handler = new StubHandler(xml);
        var client = new MediaWikiExportClient(new HttpClient(handler));

        var fetched = await client.ExportXmlAsync(new[] { "Stirrup" }, CancellationToken.None);

        Assert.Equal(xml, fetched);
    }

    [Fact]
    public async Task Throws_when_the_wiki_refuses_the_request()
    {
        // The Cloudflare challenge is a 403 carrying an HTML body. Parsing it as export XML
        // would yield zero pages, which downstream reads as "these quests are gone".
        var handler = new StubHandler("<!DOCTYPE html><html><title>Just a moment...</title></html>", HttpStatusCode.Forbidden);
        var client = new MediaWikiExportClient(new HttpClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ExportXmlAsync(new[] { "Stirrup" }, CancellationToken.None));
    }

    [Fact]
    public void Batches_a_long_list_into_requests_the_wiki_accepts()
    {
        var titles = Enumerable.Range(0, MediaWikiExportClient.MaxTitlesPerRequest * 2 + 3)
            .Select(i => $"Quest {i}")
            .ToList();

        var batches = MediaWikiExportClient.Batch(titles).ToList();

        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.InRange(b.Count, 1, MediaWikiExportClient.MaxTitlesPerRequest));
        Assert.Equal(titles, batches.SelectMany(b => b).ToList());
    }

    [Fact]
    public void Batches_nothing_into_no_requests()
    {
        Assert.Empty(MediaWikiExportClient.Batch(Array.Empty<string>()));
    }

    private static async Task<Dictionary<string, string>> ReadFormAsync(HttpRequestMessage request)
    {
        var body = await request.Content!.ReadAsStringAsync();
        return body.Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts[1].Replace("+", " ")));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
    }
}

using System.Net;
using BoundedResearchAgent.App;
using BoundedResearchAgent.Web;

namespace BoundedResearchAgent.Tests;

public sealed class SourceReadingTests
{
    [Fact]
    public async Task Later_article_sections_are_stored_and_indexed_beyond_the_old_prefix_limit()
    {
        var html = "<nav>Navigation noise</nav><main><h1>Test documentation</h1><p>" + new string('a', 15000) +
            "</p><h2>Cache storage</h2><p>This synthetic section describes other servers and their cache entries.</p></main>";
        using var http = new HttpClient(new HtmlHandler(html));
        var web = new LiveWebResearch(new SearchSettings(), http, http);

        var source = await web.ReadAsync(Samples.Url1, "S001", Samples.Run(), default);

        Assert.False(source.Truncated);
        Assert.True(source.Text.Length > 15000);
        Assert.Contains("This synthetic section describes other servers", source.Text);
        var section = Assert.Single(source.Sections!, value => value.Heading == "Cache storage");
        Assert.True(section.Offset > 12000);
        Assert.StartsWith("Cache storage", source.Text[section.Offset..]);
        Assert.Equal(SourcePolicy.Hash(source.Text), source.Sha256);
        Assert.DoesNotContain("Navigation noise", source.Text);
    }

    [Fact]
    public async Task Full_snapshot_has_its_own_hard_bound_and_reports_real_truncation()
    {
        using var http = new HttpClient(new HtmlHandler("<main><h1>Test source</h1><p>" + new string('x', 1000) + "</p></main>"));
        var web = new LiveWebResearch(new SearchSettings(), http, http);
        var run = Samples.Run(new() { MaxPageCharacters = 100, MaxSourceCharacters = 200 });

        var source = await web.ReadAsync(Samples.Url1, "S001", run, default);

        Assert.Equal(200, source.Text.Length);
        Assert.True(source.Truncated);
        Assert.Equal(SourcePolicy.Hash(source.Text), source.Sha256);
    }

    [Fact]
    public async Task Historical_contract_keeps_its_original_page_prefix_bound()
    {
        using var http = new HttpClient(new HtmlHandler("<main><h1>Test source</h1><p>" + new string('x', 1000) + "</p></main>"));
        var web = new LiveWebResearch(new SearchSettings(), http, http);
        var run = Samples.Run(new() { MaxPageCharacters = 100 });
        run.ContractVersion = 1;

        var source = await web.ReadAsync(Samples.Url1, "S001", run, default);

        Assert.Equal(100, source.Text.Length);
        Assert.True(source.Truncated);
        Assert.Null(source.Sections);
    }

    private sealed class HtmlHandler(string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html") });
    }
}

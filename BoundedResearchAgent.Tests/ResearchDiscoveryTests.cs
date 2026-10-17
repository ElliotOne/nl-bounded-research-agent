using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BoundedResearchAgent.Agent;
using BoundedResearchAgent.App;
using BoundedResearchAgent.Domain;
using BoundedResearchAgent.Web;

namespace BoundedResearchAgent.Tests;

public sealed class ResearchDiscoveryTests
{
    [Fact]
    public async Task Default_question_only_brief_can_discover_read_and_cite_two_search_results()
    {
        var brief = Json.Read<ResearchBrief>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "data/briefs/hybrid-cache.json")));
        Assert.Empty(brief.SeedUrls);
        var run = ResearchEngine.Create(brief, new Limits(), "test-deployment", DateTimeOffset.UtcNow);
        run.Mode = "test";
        Assert.Empty(run.DiscoveredUrls);
        var keyName = "RESEARCH_DISCOVERY_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(keyName, "synthetic-search-credential");
        try
        {
            var handler = new DiscoveryHandler();
            using var http = new HttpClient(handler);
            var web = new LiveWebResearch(new SearchSettings { ApiKeyEnvironmentVariable = keyName }, http, http);
            var store = new MemoryStore();
            var model = new ScriptedModel([
                Samples.Turn("SearchWeb", new { query = "primary and distributed cache responsibilities" }),
                Samples.Turn("ReadPage", new { url = Samples.Url1 }),
                Samples.Evidence(),
                Samples.Turn("SearchWeb", new { query = "cross instance invalidation limitations" }),
                Samples.Turn("ReadPage", new { url = Samples.Url2 }),
                Samples.Evidence("S002", "Q2", Samples.Text2),
                Samples.Finish()]);

            var result = await new ResearchEngine(model, web, store).ContinueAsync(run);

            Assert.Equal("completed", result.Status);
            Assert.Equal(2, handler.Searches);
            Assert.Equal(2, handler.Reads);
            Assert.Equal([Samples.Url1, Samples.Url2], result.Sources.Select(s => s.Url));
            Assert.Equal(["SearchWeb", "ReadPage", "RecordEvidence", "SearchWeb", "ReadPage", "RecordEvidence", "FinishResearch"], result.Steps.Select(s => s.Tool));
            Assert.Equal(2, (await store.LoadAsync(run.Id, default)).Evidence.Count);
        }
        finally { Environment.SetEnvironmentVariable(keyName, null); }
    }

    [Fact]
    public async Task An_allowed_url_cannot_be_read_until_search_or_a_seed_admits_it()
    {
        var web = new FixtureWeb();
        var result = await new ResearchEngine(new ScriptedModel([Samples.Turn("ReadPage", new { url = Samples.Url1 })]), web, new MemoryStore())
            .ContinueAsync(Seedless(new Limits { MaxSteps = 1 }));

        Assert.Equal(0, web.Reads);
        Assert.Empty(result.Sources);
        Assert.Contains("not discovered", result.Steps[0].Result);
    }

    [Fact]
    public async Task Search_results_without_reading_sources_cannot_become_evidence()
    {
        var result = await new ResearchEngine(new ScriptedModel([
            Samples.Turn("SearchWeb", new { query = "find the missing source" }),
            Samples.Evidence(),
            Samples.Turn("FinishResearch", new { summary = "Synthetic incomplete result.", findings = Array.Empty<object>(), openQuestions = new[] { "Required evidence is missing." } })]),
            new FixtureWeb(), new MemoryStore()).ContinueAsync(Seedless(new Limits { MaxFinishAttempts = 1 }));

        Assert.Equal("incomplete", result.Status);
        Assert.Empty(result.Sources);
        Assert.Empty(result.Evidence);
        Assert.Contains("Unknown source", result.Steps[1].Result);
    }

    [Fact]
    public async Task Explicit_seeded_research_can_finish_without_an_arbitrary_search_quota()
    {
        var brief = Samples.Run().Brief with { SeedUrls = [Samples.Url1, Samples.Url2] };
        var run = ResearchEngine.Create(brief, new Limits(), "test-deployment", DateTimeOffset.UtcNow);
        run.Mode = "test";
        var result = await new ResearchEngine(new ScriptedModel([
            Samples.Turn("ReadPage", new { url = Samples.Url1 }), Samples.Evidence(),
            Samples.Turn("ReadPage", new { url = Samples.Url2 }), Samples.Evidence("S002", "Q2", Samples.Text2), Samples.Finish()]),
            new FixtureWeb(), new MemoryStore()).ContinueAsync(run);

        Assert.Equal("completed", result.Status);
        Assert.Equal(0, result.Searches);
    }

    private static ResearchRun Seedless(Limits limits)
    {
        var run = ResearchEngine.Create(Samples.Run().Brief with { SeedUrls = [] }, limits, "test-deployment", DateTimeOffset.UtcNow);
        run.Mode = "test";
        return run;
    }

    private sealed class DiscoveryHandler : HttpMessageHandler
    {
        public int Searches { get; private set; }
        public int Reads { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "google.serper.dev")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("synthetic-search-credential", Assert.Single(request.Headers.GetValues("X-API-KEY")));
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                Assert.Contains("site:learn.microsoft.com", body["q"]!.GetValue<string>());
                var url = ++Searches == 1 ? Samples.Url1 : Samples.Url2;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Json.Write(new { organic = new[] { new { link = url, title = "Synthetic documentation", snippet = "A discovery hint, not evidence." } } }))
                };
            }
            Assert.False(request.Headers.Contains("X-API-KEY"));
            Assert.False(request.Headers.Contains("api-key"));
            Assert.Equal("learn.microsoft.com", request.RequestUri.Host);
            Reads++;
            var text = request.RequestUri.AbsoluteUri == Samples.Url1 ? Samples.Text1 : Samples.Text2;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"<main><p>{text}</p></main>", Encoding.UTF8, "text/html") };
        }
    }
}

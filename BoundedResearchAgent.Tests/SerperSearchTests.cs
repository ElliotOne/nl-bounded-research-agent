using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoundedResearchAgent.App;
using BoundedResearchAgent.Web;

namespace BoundedResearchAgent.Tests;

public sealed class SerperSearchTests
{
    [Fact]
    public async Task Sends_authenticated_post_and_admits_only_unique_allowed_organic_links()
    {
        using var harness = new Harness(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://google.serper.dev/search", request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("test-serper-credential", Assert.Single(request.Headers.GetValues("X-API-KEY")));
            Assert.False(request.Headers.Contains("X-Subscription-Token"));
            var body = JsonNode.Parse(await request.Content.ReadAsStringAsync())!;
            Assert.Equal("cache invalidation (site:learn.microsoft.com)", body["q"]!.GetValue<string>());
            Assert.Equal(10, body["num"]!.GetValue<int>());
            return Response("""
                {"organic":[
                  {"title":"First","link":"https://learn.microsoft.com/research/first","snippet":"Useful source"},
                  {"title":"Duplicate","link":"https://learn.microsoft.com/research/first#section"},
                  {"link":"https://evil.example/","snippet":"Untrusted host"},
                  {"link":"http://learn.microsoft.com/insecure"},
                  null,{"link":42},{"title":"Missing link"},
                  {"link":"https://learn.microsoft.com/research/second"}
                ]}
                """);
        });
        var hits = await harness.Web.SearchAsync("cache invalidation", Samples.Run().Brief, default);
        Assert.Equal(2, hits.Count);
        Assert.Equal("Useful source", hits[0].Description);
        Assert.Equal(Samples.Url2, hits[1].Url);
        Assert.Equal("", hits[1].Description);
    }

    [Fact]
    public async Task Applies_configured_result_limit_after_host_filtering()
    {
        using var harness = new Harness(_ => Task.FromResult(Response("""
            {"organic":[{"link":"https://evil.example/"},{"link":"https://learn.microsoft.com/one"},{"link":"https://learn.microsoft.com/two"}]}
            """)), maxResults: 1);
        var hit = Assert.Single(await harness.Web.SearchAsync("cache", Samples.Run().Brief, default));
        Assert.Equal("https://learn.microsoft.com/one", hit.Url);
    }

    [Fact]
    public async Task Empty_organic_results_are_valid()
    {
        using var harness = new Harness(_ => Task.FromResult(Response("{\"organic\":[]}")));
        Assert.Empty(await harness.Web.SearchAsync("cache", Samples.Run().Brief, default));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Authentication_rate_limit_and_provider_errors_are_reported_without_echoing_body(int status)
    {
        using var harness = new Harness(_ => Task.FromResult(Response("sensitive-provider-error-body", (HttpStatusCode)status)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => harness.Web.SearchAsync("cache", Samples.Run().Brief, default));
        Assert.Equal($"Serper returned HTTP {status}.", error.Message);
    }

    [Fact]
    public async Task Malformed_organic_payload_is_rejected()
    {
        using var harness = new Harness(_ => Task.FromResult(Response("{\"organic\":{\"unexpected\":true}}")));
        await Assert.ThrowsAsync<JsonException>(() => harness.Web.SearchAsync("cache", Samples.Run().Brief, default));
    }

    [Fact]
    public async Task Serper_credential_is_never_sent_to_source_pages()
    {
        using var harness = new Harness(request =>
        {
            Assert.Equal("learn.microsoft.com", request.RequestUri!.Host);
            Assert.False(request.Headers.Contains("X-API-KEY"));
            Assert.False(request.Headers.Contains("api-key"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Samples.Text1) });
        });
        var source = await harness.Web.ReadAsync(Samples.Url1, "S001", Samples.Run(), default);
        Assert.Equal(Samples.Text1, source.Text);
    }

    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private sealed class Harness : IDisposable
    {
        private readonly string _keyName = "RESEARCH_SERPER_TEST_" + Guid.NewGuid().ToString("N");
        private readonly HttpClient _http;
        public LiveWebResearch Web { get; }
        public Harness(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler, int maxResults = 5)
        {
            Environment.SetEnvironmentVariable(_keyName, "test-serper-credential");
            _http = new HttpClient(new Handler(handler));
            Web = new LiveWebResearch(new SearchSettings { ApiKeyEnvironmentVariable = _keyName, ResultsPerQuery = maxResults }, _http, _http);
        }
        public void Dispose()
        {
            _http.Dispose();
            Environment.SetEnvironmentVariable(_keyName, null);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}

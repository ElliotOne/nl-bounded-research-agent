using System.Net;
using System.Text.Json.Nodes;
using BoundedResearchAgent.App;
using BoundedResearchAgent.Foundry;

namespace BoundedResearchAgent.Tests;

public sealed class FoundryRequestTests
{
    [Fact]
    public async Task Sends_native_tool_contract_to_configured_foundry_deployment()
    {
        const string variable = "RESEARCH_UNIT_TEST_KEY";
        Environment.SetEnvironmentVariable(variable, "test-only-credential");
        try
        {
            using var http = new HttpClient(new InspectHandler());
            var model = new FoundryResearchModel(new FoundrySettings
            {
                Endpoint = "https://test-resource.openai.azure.com/openai/v1/",
                ApiKeyEnvironmentVariable = variable,
                MaxOutputTokens = 1400
            }, http);
            var turn = await model.NextAsync(Samples.Run(), default);
            Assert.Equal("SearchWeb", turn.Call.Name);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    private sealed class InspectHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://test-resource.openai.azure.com/openai/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-only-credential", Assert.Single(request.Headers.GetValues("api-key")));
            var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            Assert.Equal("test-deployment", payload["model"]!.GetValue<string>());
            Assert.Equal("required", payload["tool_choice"]!.GetValue<string>());
            Assert.False(payload["parallel_tool_calls"]!.GetValue<bool>());
            Assert.Equal(1400, payload["max_completion_tokens"]!.GetValue<int>());
            Assert.Equal(4, payload["tools"]!.AsArray().Count);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"choices":[{"finish_reason":"tool_calls","message":{"tool_calls":[{"id":"c1","type":"function","function":{"name":"SearchWeb","arguments":"{\"query\":\"test\"}"}}]}}],"usage":{"total_tokens":20}}
                    """)
            };
        }
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoundedResearchAgent.Agent;
using BoundedResearchAgent.App;
using BoundedResearchAgent.Domain;
using BoundedResearchAgent.Web;

namespace BoundedResearchAgent.Foundry;

public sealed class FoundryResearchModel(FoundrySettings settings, HttpClient http) : IResearchModel
{
    public async Task<ModelTurn> NextAsync(ResearchRun run, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.ChatEndpoint());
        request.Headers.Add("api-key", Settings.Secret(settings.ApiKeyEnvironmentVariable));
        var payload = new JsonObject
        {
            ["model"] = run.ModelDeployment,
            ["messages"] = new JsonArray(ModelContext.Build(run).Select(m => (JsonNode)m).ToArray()),
            ["tools"] = ToolCatalog.Definitions(),
            ["tool_choice"] = "required",
            ["parallel_tool_calls"] = false,
            ["max_completion_tokens"] = settings.MaxOutputTokens,
            ["store"] = false
        };
        request.Content = JsonContent.Create(payload);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        // Do not print provider error bodies: they can contain echoed source content.
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Foundry returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        var text = await BoundedHttp.ReadAsync(response.Content, 1000000, cancellationToken);
        return Parse(text);
    }

    public static ModelTurn Parse(string response)
    {
        var root = JsonNode.Parse(response) ?? throw new JsonException("Missing Foundry response.");
        var choice = root["choices"]?[0] ?? throw new JsonException("Missing Foundry choice.");
        if (choice["finish_reason"]?.GetValue<string>() != "tool_calls")
            throw new JsonException("Foundry must finish with one tool call; check model tool support and output limit.");
        var calls = choice["message"]?["tool_calls"]?.AsArray();
        if (calls?.Count != 1) throw new JsonException("Exactly one tool call is allowed per turn.");
        var call = calls[0]!;
        var id = call["id"]?.GetValue<string>() ?? throw new JsonException("Tool call ID is missing.");
        var name = call["function"]?["name"]?.GetValue<string>() ?? throw new JsonException("Tool name is missing.");
        var arguments = JsonNode.Parse(call["function"]?["arguments"]?.GetValue<string>() ?? "")?.AsObject()
            ?? throw new JsonException("Tool arguments must be a JSON object.");
        var tokens = root["usage"]?["total_tokens"]?.GetValue<int>() ?? throw new JsonException("Token usage is required.");
        if (tokens < 0 || id.Length is < 1 or > 200) throw new JsonException("Invalid Foundry usage or tool ID.");
        // Persist only the public function call, not provider reasoning fields/content.
        var assistant = new JsonObject { ["role"] = "assistant", ["tool_calls"] = calls.DeepClone() };
        return new ModelTurn(assistant, new ToolCall(id, name, arguments), tokens);
    }
}

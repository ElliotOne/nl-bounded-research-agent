using System.Text.Json.Nodes;
using BoundedResearchAgent.Domain;

namespace BoundedResearchAgent.Agent;

// Explicit replay of an existing run. No synthetic live fallback and no network calls.
public sealed class CapturedReplay(ResearchRun captured) : IResearchModel, IWebResearch
{
    public static ResearchRun CreateRun(ResearchRun captured)
    {
        if (captured.Steps.LastOrDefault()?.Tool != "FinishResearch" || captured.Status is not ("completed" or "incomplete"))
            throw new InvalidOperationException("Decision replay requires a run that reached FinishResearch.");
        var run = ResearchEngine.Create(captured.Brief, captured.Limits, captured.ModelDeployment, captured.StartedAtUtc);
        run.ContractVersion = captured.ContractVersion;
        run.Mode = "replay";
        run.ReplayOf = captured.Id;
        return run;
    }

    public Task<ModelTurn> NextAsync(ResearchRun run, CancellationToken cancellationToken)
    {
        if (run.Steps.Count >= captured.Steps.Count) throw new InvalidOperationException("Captured decisions exhausted.");
        var step = captured.Steps[run.Steps.Count];
        var assistant = captured.Messages[2 + run.Steps.Count * 2].DeepClone().AsObject();
        var id = assistant["tool_calls"]![0]!["id"]!.GetValue<string>();
        return Task.FromResult(new ModelTurn(assistant,
            new ToolCall(id, step.Tool, step.Arguments.DeepClone().AsObject()), step.Tokens));
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(string query, ResearchBrief brief, CancellationToken cancellationToken)
    {
        var step = captured.Steps.First(s => s.Tool == "SearchWeb" && s.Arguments["query"]?.GetValue<string>().Trim() == query);
        var result = JsonNode.Parse(step.Result)!;
        if (result["error"] is { } error) throw new ToolRejectedException(error.GetValue<string>());
        return Task.FromResult<IReadOnlyList<SearchHit>>(Json.Read<List<SearchHit>>(result["untrustedSearchResults"]!.ToJsonString()));
    }

    public Task<SourceSnapshot> ReadAsync(string url, string sourceId, ResearchRun run, CancellationToken cancellationToken)
    {
        var step = captured.Steps.First(s => s.Tool == "ReadPage" && Web.SourcePolicy.CanonicalUrl(s.Arguments["url"]!.GetValue<string>(), run.Brief) == url);
        var result = JsonNode.Parse(step.Result)!;
        if (result["error"] is { } error) throw new ToolRejectedException(error.GetValue<string>());
        var source = result["untrustedPage"] is { } page
            ? captured.Sources.Single(s => s.Id == page["id"]!.GetValue<string>())
            : Json.Read<SourceSnapshot>(result["untrustedSource"]!.ToJsonString());
        if (Web.SourcePolicy.Hash(source.Text) != source.Sha256) throw new InvalidOperationException("Captured source hash mismatch.");
        return Task.FromResult(source with { Id = sourceId });
    }

    public sealed class ReplayClock(DateTimeOffset time) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => time;
    }
}

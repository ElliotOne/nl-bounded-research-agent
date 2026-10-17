using System.Text.Json.Nodes;
using BoundedResearchAgent.Agent;
using BoundedResearchAgent.Domain;
using BoundedResearchAgent.Web;

namespace BoundedResearchAgent.Tests;

public static class Samples
{
    public const string Url1 = "https://learn.microsoft.com/research/first";
    public const string Url2 = "https://learn.microsoft.com/research/second";
    public const string Text1 = "The first source describes the local cache layer and its process-local behavior.";
    public const string Text2 = "The second source explains that another process needs a separate invalidation strategy.";
    public static ResearchRun Run(Limits? limits = null)
    {
        var run = ResearchEngine.Create(
        new ResearchBrief("Investigate cache behavior", [new("Q1", "Local layer?"), new("Q2", "Other instances?")],
            ["learn.microsoft.com"], [Url1]), limits ?? new Limits(), "test-deployment", DateTimeOffset.UtcNow);
        run.Mode = "test";
        return run;
    }
    public static ModelTurn Turn(string tool, object arguments)
    {
        var args = JsonNode.Parse(Json.Write(arguments))!.AsObject();
        var id = Guid.NewGuid().ToString();
        var call = new JsonObject { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = tool, ["arguments"] = args.ToJsonString() } };
        return new ModelTurn(new JsonObject { ["role"] = "assistant", ["tool_calls"] = new JsonArray(call) }, new ToolCall(id, tool, args), 100);
    }
    public static ModelTurn Evidence(string source = "S001", string question = "Q1", string quote = Text1) =>
        Turn("RecordEvidence", new { questionId = question, sourceId = source, quote, claim = "A bounded test claim.", stance = "supports" });
    public static ModelTurn Finish() => Turn("FinishResearch", new { summary = "Test-only research brief.", findings = new[] { new { claim = "First finding", evidenceIds = new[] { "E001" } }, new { claim = "Second finding", evidenceIds = new[] { "E002" } } }, openQuestions = Array.Empty<string>() });
    public static ModelTurn[] HappyPath() =>
    [
        Turn("ReadPage", new { url = Url1 }), Evidence(),
        Turn("SearchWeb", new { query = "cross process invalidation" }),
        Turn("ReadPage", new { url = Url2 }), Evidence("S002", "Q2", Text2), Finish()
    ];
}

public sealed class ScriptedModel(IEnumerable<ModelTurn> turns) : IResearchModel
{
    private readonly Queue<ModelTurn> _turns = new(turns);
    public int Calls { get; private set; }
    public Task<ModelTurn> NextAsync(ResearchRun run, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(_turns.Dequeue());
    }
}

public sealed class MemoryStore : IResearchStore
{
    private readonly Dictionary<Guid, string> _runs = [];
    public Task CreateAsync(ResearchRun run, CancellationToken cancellationToken) { _runs.Add(run.Id, Json.Write(run)); return Task.CompletedTask; }
    public Task<ResearchRun> LoadAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Json.Read<ResearchRun>(_runs[id]));
    public Task SaveAsync(ResearchRun run, CancellationToken cancellationToken)
    {
        run.Version++;
        _runs[run.Id] = Json.Write(run);
        return Task.CompletedTask;
    }
}

public sealed class FixtureWeb : IWebResearch
{
    public int Reads { get; private set; }
    public Task<IReadOnlyList<SearchHit>> SearchAsync(string query, ResearchBrief brief, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SearchHit>>([new(Samples.Url2, "Second source", "An additional source")]);
    public Task<SourceSnapshot> ReadAsync(string url, string sourceId, ResearchRun run, CancellationToken cancellationToken)
    {
        Reads++;
        var text = url == Samples.Url1 ? Samples.Text1 : Samples.Text2;
        return Task.FromResult(new SourceSnapshot(sourceId, url, "Test source", DateTimeOffset.UtcNow, text, SourcePolicy.Hash(text), false));
    }
}

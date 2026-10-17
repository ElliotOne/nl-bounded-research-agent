using System.Text.Json.Nodes;
using BoundedResearchAgent.Agent;
using BoundedResearchAgent.Domain;
using BoundedResearchAgent.Web;

namespace BoundedResearchAgent.Tests;

public sealed class ResearchCompletionTests
{
    private static ModelTurn Partial() => Samples.Turn("FinishResearch", new
    {
        summary = "Partial test answer.",
        findings = new[] { new { claim = "First finding", evidenceIds = new[] { "E001" } } },
        openQuestions = new[] { "How do other instances behave?" }
    });

    [Fact]
    public async Task Premature_finish_returns_gaps_and_research_can_continue_to_completion()
    {
        var run = Samples.Run();
        var turns = Samples.HappyPath().ToList();
        turns.Insert(2, Partial());
        var store = new MemoryStore();
        var result = await new ResearchEngine(new ScriptedModel(turns), new FixtureWeb(), store).ContinueAsync(run);

        Assert.Equal("completed", result.Status);
        Assert.Equal(2, result.FinishAttempts);
        Assert.Equal(7, result.Steps.Count);
        var feedback = JsonNode.Parse(result.Steps[2].Result)!;
        Assert.False(feedback["accepted"]!.GetValue<bool>());
        Assert.Contains("Q2:", feedback["gaps"]!.ToJsonString());
        Assert.Empty(result.Report!.OpenQuestions);
        Assert.Equal("completed", (await store.LoadAsync(run.Id, default)).Status);
    }

    [Fact]
    public async Task Optional_application_decisions_are_caveats_without_blocking_completion()
    {
        var turns = Samples.HappyPath().ToArray();
        turns[^1] = Samples.Turn("FinishResearch", new
        {
            summary = "The required test questions are answered.",
            findings = new[] { new { claim = "First finding", evidenceIds = new[] { "E001" } }, new { claim = "Second finding", evidenceIds = new[] { "E002" } } },
            openQuestions = Array.Empty<string>(),
            limitations = new[] { "Choose an application-specific expiration policy before deployment." }
        });

        var result = await Run(turns);

        Assert.Equal("completed", result.Status);
        Assert.Single(result.Report!.Limitations!);
        Assert.Empty(result.Report.OpenQuestions);
    }

    [Fact]
    public async Task Genuine_gaps_remain_incomplete_after_bounded_finish_attempts()
    {
        var result = await Run([Samples.Turn("ReadPage", new { url = Samples.Url1 }), Samples.Evidence(), Partial(), Partial(), Partial()]);

        Assert.Equal("incomplete", result.Status);
        Assert.Equal("evidence_gaps", result.StopReason);
        Assert.Equal(3, result.FinishAttempts);
        Assert.Equal(5, result.Steps.Count);
        Assert.Contains(result.Report!.OpenQuestions, value => value.StartsWith("Q2:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_budget_stop_preserves_the_partial_report_and_its_actual_gaps()
    {
        var result = await Run([Samples.Turn("ReadPage", new { url = Samples.Url1 }), Samples.Evidence(), Partial(),
            Samples.Turn("SearchWeb", new { query = "research the gap" })], new Limits { MaxSteps = 4 });

        Assert.Equal("step_budget", result.StopReason);
        Assert.Equal("Partial test answer.", result.Report!.Summary);
        Assert.NotEmpty(result.Report.OpenQuestions);
    }

    [Fact]
    public async Task Later_sections_can_be_read_and_quoted_without_another_network_fetch()
    {
        var prefix = new string('x', 14000);
        const string quote = "This synthetic later section describes behavior in other processes.";
        var text = prefix + " Cache storage " + quote;
        var source = new SourceSnapshot("S001", Samples.Url1, "Synthetic long page", DateTimeOffset.UtcNow,
            text, SourcePolicy.Hash(text), false, [new("Cache storage", prefix.Length + 1)]);
        var web = new SnapshotWeb(source);
        var run = Samples.Run(new Limits { MaxSteps = 3, MaxPageCharacters = 250 });
        var result = await new ResearchEngine(new ScriptedModel([
            Samples.Turn("ReadPage", new { url = Samples.Url1 }),
            Samples.Turn("ReadPage", new { url = Samples.Url1, find = "Cache storage" }),
            Samples.Evidence("S001", "Q2", quote)]), web, new MemoryStore()).ContinueAsync(run);

        Assert.Equal(1, web.Reads);
        Assert.Equal(1, result.Pages);
        Assert.Equal(text, Assert.Single(result.Sources).Text);
        Assert.Contains(quote, result.Steps[1].Result);
        Assert.Equal(quote, Assert.Single(result.Evidence).Quote);
        var firstWindow = JsonNode.Parse(result.Steps[0].Result)!["untrustedPage"]!;
        Assert.Equal(250, firstWindow["passages"]![0]!["text"]!.GetValue<string>().Length);
        Assert.Equal(250, firstWindow["nextOffset"]!.GetValue<int>());
        Assert.Contains("Cache storage", firstWindow["sections"]!.ToJsonString());
    }

    [Fact]
    public async Task Offset_reads_continue_the_stored_source_and_bad_offsets_are_rejected()
    {
        var text = new string('a', 250) + new string('b', 250);
        var web = new SnapshotWeb(new SourceSnapshot("S001", Samples.Url1, "Test", DateTimeOffset.UtcNow,
            text, SourcePolicy.Hash(text), false));
        var result = await new ResearchEngine(new ScriptedModel([
            Samples.Turn("ReadPage", new { url = Samples.Url1 }),
            Samples.Turn("ReadPage", new { url = Samples.Url1, offset = 250 }),
            Samples.Turn("ReadPage", new { url = Samples.Url1, offset = -1 })]), web, new MemoryStore())
            .ContinueAsync(Samples.Run(new Limits { MaxSteps = 3, MaxPageCharacters = 250 }));

        Assert.Equal(1, web.Reads);
        var second = JsonNode.Parse(result.Steps[1].Result)!["untrustedPage"]!;
        Assert.Equal(new string('b', 250), second["passages"]![0]!["text"]!.GetValue<string>());
        Assert.Null(second["nextOffset"]);
        Assert.Contains("nonnegative integer", result.Steps[2].Result);
    }

    [Fact]
    public async Task Historical_runs_keep_the_original_completion_policy_during_replay()
    {
        var run = Samples.Run();
        run.ContractVersion = 1;
        var captured = await new ResearchEngine(new ScriptedModel([
            Samples.Turn("ReadPage", new { url = Samples.Url1 }), Samples.Evidence(), Partial()]), new FixtureWeb(), new MemoryStore()).ContinueAsync(run);

        Assert.Equal("incomplete", captured.Status);
        Assert.Contains("untrustedSource", captured.Steps[0].Result);
        var replay = new CapturedReplay(captured);
        var replayRun = CapturedReplay.CreateRun(captured);
        Assert.Equal(1, replayRun.ContractVersion);
        var replayed = await new ResearchEngine(replay, replay, new MemoryStore(), new CapturedReplay.ReplayClock(captured.StartedAtUtc)).ContinueAsync(replayRun);
        Assert.Equal(captured.Status, replayed.Status);
        Assert.Equal(Json.Write(captured.Report), Json.Write(replayed.Report));
    }

    [Fact]
    public async Task Passage_ids_store_the_exact_source_quote_without_model_transcription()
    {
        var turns = Samples.HappyPath().ToArray();
        turns[1] = Samples.Turn("RecordEvidence", new
        {
            questionId = "Q1",
            sourceId = "S001",
            excerptId = "S001:P001",
            claim = "A source-backed test claim.",
            stance = "supports"
        });

        var result = await Run(turns);

        Assert.Equal("completed", result.Status);
        Assert.Equal(Samples.Text1, result.Evidence[0].Quote);
        Assert.Equal("S001:P001", JsonNode.Parse(result.Steps[0].Result)!["untrustedPage"]!["passages"]![0]!["id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("S002:P001")]
    [InlineData("S001:P999")]
    public async Task Invented_or_cross_source_passage_ids_are_rejected(string excerptId)
    {
        var result = await Run([Samples.Turn("ReadPage", new { url = Samples.Url1 }), Samples.Turn("RecordEvidence", new
        {
            questionId = "Q1", sourceId = "S001", excerptId, claim = "An unsupported test claim.", stance = "supports"
        })], new Limits { MaxSteps = 2 });

        Assert.Empty(result.Evidence);
        Assert.Contains("Unknown excerptId", result.Steps[1].Result);
    }

    [Fact]
    public async Task New_replay_preserves_rejected_finish_and_full_snapshot_window_behavior()
    {
        var turns = Samples.HappyPath().ToList();
        turns.Insert(2, Partial());
        var captured = await Run(turns);
        var replay = new CapturedReplay(captured);

        var replayed = await new ResearchEngine(replay, replay, new MemoryStore(), new CapturedReplay.ReplayClock(captured.StartedAtUtc))
            .ContinueAsync(CapturedReplay.CreateRun(captured));

        Assert.Equal("completed", replayed.Status);
        Assert.Equal(2, replayed.FinishAttempts);
        Assert.Equal(Json.Write(captured.Report), Json.Write(replayed.Report));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(ResearchEngine.CurrentContractVersion, 1)]
    public async Task Duplicate_citations_are_normalized_for_new_runs_without_changing_historical_replay(int contractVersion, int expectedReferences)
    {
        var run = Samples.Run();
        run.ContractVersion = contractVersion;
        var turns = Samples.HappyPath();
        turns[^1] = Samples.Turn("FinishResearch", new
        {
            summary = "Synthetic report with a repeated citation.",
            findings = new[] {
                new { claim = "First finding", evidenceIds = new[] { "E001", "E001" } },
                new { claim = "Second finding", evidenceIds = new[] { "E002" } }
            },
            openQuestions = Array.Empty<string>()
        });
        var captured = await new ResearchEngine(new ScriptedModel(turns), new FixtureWeb(), new MemoryStore()).ContinueAsync(run);

        Assert.Equal("completed", captured.Status);
        Assert.Equal(expectedReferences, captured.Report!.Findings[0].EvidenceIds.Count);
        Assert.Equal("E002", Assert.Single(captured.Report.Findings[1].EvidenceIds));
        var submitted = captured.Steps[^1].Arguments;
        Assert.Equal(2, submitted["findings"]![0]!["evidenceIds"]!.AsArray().Count);

        var replay = new CapturedReplay(captured);
        var replayed = await new ResearchEngine(replay, replay, new MemoryStore(), new CapturedReplay.ReplayClock(captured.StartedAtUtc))
            .ContinueAsync(CapturedReplay.CreateRun(captured));

        Assert.Equal(contractVersion, replayed.ContractVersion);
        Assert.Equal("completed", replayed.Status);
        Assert.Equal(Json.Write(captured.Report), Json.Write(replayed.Report));
    }

    private static Task<ResearchRun> Run(IEnumerable<ModelTurn> turns, Limits? limits = null) =>
        new ResearchEngine(new ScriptedModel(turns), new FixtureWeb(), new MemoryStore()).ContinueAsync(Samples.Run(limits));

    private sealed class SnapshotWeb(SourceSnapshot source) : IWebResearch
    {
        public int Reads { get; private set; }
        public Task<IReadOnlyList<SearchHit>> SearchAsync(string query, ResearchBrief brief, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SourceSnapshot> ReadAsync(string url, string sourceId, ResearchRun run, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(source with { Id = sourceId });
        }
    }
}

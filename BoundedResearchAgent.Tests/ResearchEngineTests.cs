using BoundedResearchAgent.Agent;
using BoundedResearchAgent.Domain;

namespace BoundedResearchAgent.Tests;

public sealed class ResearchEngineTests
{
    [Fact]
    public async Task Executes_model_selected_search_after_initial_evidence_and_persists_complete_report()
    {
        var run = Samples.Run();
        var store = new MemoryStore();
        await store.CreateAsync(run, default);
        var result = await new ResearchEngine(new ScriptedModel(Samples.HappyPath()), new FixtureWeb(), store).ContinueAsync(run);
        Assert.Equal("completed", result.Status);
        Assert.Equal(["ReadPage", "RecordEvidence", "SearchWeb", "ReadPage", "RecordEvidence", "FinishResearch"], result.Steps.Select(s => s.Tool));
        Assert.Equal(2, result.Sources.Count);
        Assert.Empty(result.Report!.OpenQuestions);
        Assert.Equal(600, result.TotalTokens);
        Assert.Equal(result.Version, (await store.LoadAsync(result.Id, default)).Version);
    }

    [Fact]
    public async Task Rejects_fabricated_quote_then_allows_model_to_correct_it()
    {
        var turns = Samples.HappyPath().ToList();
        turns.Insert(1, Samples.Evidence(quote: "This quote was never present in the source document."));
        var result = await Execute(Samples.Run(), turns);
        Assert.Equal("completed", result.Status);
        Assert.Contains("exact excerpt", result.Steps[1].Result);
        Assert.Equal(2, result.Evidence.Count);
    }

    [Fact]
    public async Task Finishing_without_question_coverage_is_incomplete()
    {
        var result = await Execute(Samples.Run(new Limits { MaxFinishAttempts = 1 }), [Samples.Turn("ReadPage", new { url = Samples.Url1 }), Samples.Evidence(),
            Samples.Turn("FinishResearch", new { summary = "Partial answer", findings = new[] { new { claim = "One finding", evidenceIds = new[] { "E001" } } }, openQuestions = Array.Empty<string>() })]);
        Assert.Equal("incomplete", result.Status);
        Assert.Contains(result.Report!.OpenQuestions, q => q.StartsWith("Q2:"));
        Assert.Contains(result.Report.OpenQuestions, q => q.Contains("distinct source"));
    }

    [Fact]
    public async Task Cannot_cite_an_invented_evidence_id()
    {
        var run = Samples.Run(new Limits { MaxSteps = 1 });
        var result = await Execute(run, [Samples.Turn("FinishResearch", new { summary = "Invented", findings = new[] { new { claim = "Unsupported", evidenceIds = new[] { "E999" } } }, openQuestions = Array.Empty<string>() })]);
        Assert.Equal("step_budget", result.StopReason);
        Assert.Contains("recorded evidence", result.Steps[0].Result);
    }

    [Fact]
    public async Task Cached_read_does_not_spend_a_second_page_but_does_spend_a_step()
    {
        var web = new FixtureWeb();
        var result = await Execute(Samples.Run(new Limits { MaxSteps = 2 }),
            [Samples.Turn("ReadPage", new { url = Samples.Url1 }), Samples.Turn("ReadPage", new { url = Samples.Url1 })], web);
        Assert.Equal(1, web.Reads);
        Assert.Equal(1, result.Pages);
        Assert.Equal(2, result.Steps.Count);
        Assert.Contains("cached", result.Steps[1].Result);
    }

    [Fact]
    public async Task Unlisted_and_undiscovered_urls_never_reach_web_adapter()
    {
        var web = new FixtureWeb();
        var result = await Execute(Samples.Run(new Limits { MaxSteps = 2 }),
            [Samples.Turn("ReadPage", new { url = "https://evil.example/" }), Samples.Turn("ReadPage", new { url = Samples.Url2 })], web);
        Assert.Equal(0, web.Reads);
        Assert.All(result.Steps, step => Assert.Contains("error", step.Result));
    }

    [Fact]
    public async Task Duplicate_search_is_rejected_and_global_step_limit_stops_repetition()
    {
        var result = await Execute(Samples.Run(new Limits { MaxSteps = 2 }),
            [Samples.Turn("SearchWeb", new { query = "same query" }), Samples.Turn("SearchWeb", new { query = "SAME QUERY" })]);
        Assert.Equal(1, result.Searches);
        Assert.Equal("step_budget", result.StopReason);
        Assert.Contains("already attempted", result.Steps[1].Result);
    }

    [Fact]
    public async Task Unknown_tools_cannot_execute_arbitrary_actions()
    {
        var result = await Execute(Samples.Run(new Limits { MaxSteps = 1 }), [Samples.Turn("RunShell", new { command = "anything" })]);
        Assert.Contains("Unknown tool", result.Steps[0].Result);
    }

    [Fact]
    public async Task Deadline_survives_resume_and_prevents_another_model_call()
    {
        var run = Samples.Run();
        run.DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var model = new ScriptedModel([]);
        var result = await new ResearchEngine(model, new FixtureWeb(), new MemoryStore()).ContinueAsync(run);
        Assert.Equal("deadline", result.StopReason);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Resume_uses_durable_history_instead_of_repeating_successful_tools()
    {
        var run = Samples.Run();
        var store = new MemoryStore();
        await store.CreateAsync(run, default);
        // Pause after a saved prefix by loading that checkpoint into a new engine.
        var prefix = Samples.HappyPath().Take(2).ToArray();
        var model = new InterruptAfterPrefix(prefix);
        await Assert.ThrowsAsync<SimulatedCrashException>(() => new ResearchEngine(model, new FixtureWeb(), store).ContinueAsync(run));
        var saved = await store.LoadAsync(run.Id, default);
        Assert.Equal(2, saved.Steps.Count);
        Assert.Single(saved.Evidence);
        var result = await new ResearchEngine(new ScriptedModel(Samples.HappyPath().Skip(2)), new FixtureWeb(), store).ContinueAsync(saved);
        Assert.Equal("completed", result.Status);
        Assert.Equal(6, result.Steps.Count);
        Assert.Equal(2, result.Pages);
    }

    [Fact]
    public async Task Captured_decisions_replay_without_network_or_another_model_request()
    {
        var captured = await Execute(Samples.Run(), Samples.HappyPath());
        var replay = new CapturedReplay(captured);
        var result = await new ResearchEngine(replay, replay, new MemoryStore(), new CapturedReplay.ReplayClock(captured.StartedAtUtc))
            .ContinueAsync(CapturedReplay.CreateRun(captured));
        Assert.Equal("replay", result.Mode);
        Assert.Equal(captured.Id, result.ReplayOf);
        Assert.Equal(Json.Write(captured.Report), Json.Write(result.Report));
    }

    [Fact]
    public async Task Reading_extra_source_without_citing_it_does_not_satisfy_source_gate()
    {
        var turns = Samples.HappyPath().ToList();
        turns[4] = Samples.Evidence("S001", "Q2", Samples.Text1);
        var result = await Execute(Samples.Run(new Limits { MaxFinishAttempts = 1 }), turns);
        Assert.Equal("incomplete", result.Status);
        Assert.Contains(result.Report!.OpenQuestions, q => q.Contains("distinct source"));
    }

    [Fact]
    public async Task Reported_token_budget_stops_before_next_call()
    {
        var result = await Execute(Samples.Run(new Limits { MaxReportedTokens = 50 }), [Samples.Turn("ReadPage", new { url = Samples.Url1 })]);
        Assert.Equal("reported_token_budget", result.StopReason);
        Assert.Equal(100, result.TotalTokens); // Provider-reported accounting can overshoot by one call.
    }

    private static Task<ResearchRun> Execute(ResearchRun run, IEnumerable<ModelTurn> turns, FixtureWeb? web = null) =>
        new ResearchEngine(new ScriptedModel(turns), web ?? new FixtureWeb(), new MemoryStore()).ContinueAsync(run);

    private sealed class SimulatedCrashException : Exception;
    private sealed class InterruptAfterPrefix(IEnumerable<ModelTurn> turns) : IResearchModel
    {
        private readonly Queue<ModelTurn> _turns = new(turns);
        public Task<ModelTurn> NextAsync(ResearchRun run, CancellationToken cancellationToken) => _turns.Count == 0
            ? throw new SimulatedCrashException() : Task.FromResult(_turns.Dequeue());
    }
}

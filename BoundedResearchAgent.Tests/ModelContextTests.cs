using System.Text.Json.Nodes;
using BoundedResearchAgent.Agent;
using BoundedResearchAgent.Domain;

namespace BoundedResearchAgent.Tests;

public sealed class ModelContextTests
{
    [Fact]
    public void Older_windows_are_compacted_without_changing_the_durable_transcript_or_call_ids()
    {
        var run = WithWindows();
        var original = Json.Write(run.Messages);

        var messages = ModelContext.Build(run);

        Assert.Equal(original, Json.Write(run.Messages));
        Assert.Equal(run.Messages.Count, messages.Count);
        Assert.True(JsonNode.Parse(messages[3]["content"]!.GetValue<string>())!["untrustedPage"]!["textOmitted"]!.GetValue<bool>());
        Assert.Null(JsonNode.Parse(messages[3]["content"]!.GetValue<string>())!["untrustedPage"]!["text"]);
        Assert.Null(JsonNode.Parse(messages[3]["content"]!.GetValue<string>())!["untrustedPage"]!["passages"]);
        Assert.Equal(new string('b', 2500), JsonNode.Parse(messages[5]["content"]!.GetValue<string>())!["untrustedPage"]!["passages"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(new string('c', 2500), JsonNode.Parse(messages[7]["content"]!.GetValue<string>())!["untrustedPage"]!["passages"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(run.Messages[3]["tool_call_id"]!.GetValue<string>(), messages[3]["tool_call_id"]!.GetValue<string>());
        Assert.True(messages.Sum(value => value.ToJsonString().Length) < run.Messages.Sum(value => value.ToJsonString().Length));
    }

    [Fact]
    public async Task Context_limit_applies_to_the_messages_sent_to_the_model_instead_of_the_full_audit_history()
    {
        var run = WithWindows();
        var size = ModelContext.Build(run).Sum(value => value.ToJsonString().Length);
        run.Limits = run.Limits with { MaxContextCharacters = size + 10, MaxFinishAttempts = 1 };
        Assert.True(run.Messages.Sum(value => value.ToJsonString().Length) > run.Limits.MaxContextCharacters);
        var model = new ScriptedModel([Samples.Turn("FinishResearch", new
        {
            summary = "Synthetic partial answer.", findings = Array.Empty<object>(), openQuestions = new[] { "Required facts remain unknown." }
        })]);

        var result = await new ResearchEngine(model, new FixtureWeb(), new MemoryStore()).ContinueAsync(run);

        Assert.Equal(1, model.Calls);
        Assert.Equal("evidence_gaps", result.StopReason);
    }

    [Fact]
    public void Historical_contract_preserves_the_full_original_model_context()
    {
        var run = WithWindows();
        run.ContractVersion = 1;

        Assert.Equal(Json.Write(run.Messages), Json.Write(ModelContext.Build(run)));
    }

    private static ResearchRun WithWindows()
    {
        var run = Samples.Run();
        for (var i = 0; i < 3; i++)
        {
            var turn = Samples.Turn("ReadPage", new { url = Samples.Url1, offset = i * 2500 });
            var result = Json.Write(new
            {
                untrustedPage = new { id = "S001", url = Samples.Url1, passages = new[] { new { id = $"S001:P{i + 1:000}", text = new string((char)('a' + i), 2500) } }, offset = i * 2500, nextOffset = (i + 1) * 2500, sections = Array.Empty<object>() }
            });
            run.Messages.Add(turn.Assistant);
            run.Messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = turn.Call.Id, ["content"] = result });
            run.Steps.Add(new AgentStep(i + 1, turn.Call.Name, turn.Call.Arguments, result, 0, run.StartedAtUtc));
        }
        return run;
    }
}

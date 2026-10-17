using System.Text.Json;
using System.Text.Json.Nodes;

namespace BoundedResearchAgent.Domain;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new JsonException("Empty JSON.");
}

public sealed record ResearchQuestion(string Id, string Text);
public sealed record ResearchBrief(string Question, List<ResearchQuestion> Questions, List<string> AllowedHosts, List<string> SeedUrls);
public sealed record SearchHit(string Url, string Title, string Description);
public sealed record SourceSection(string Heading, int Offset);
public sealed record SourceSnapshot(string Id, string Url, string Title, DateTimeOffset RetrievedAtUtc, string Text, string Sha256, bool Truncated,
    List<SourceSection>? Sections = null);
public sealed record Evidence(string Id, string QuestionId, string SourceId, string Quote, string Claim, string Stance);
public sealed record Finding(string Claim, List<string> EvidenceIds);
public sealed record ResearchReport(string Summary, List<Finding> Findings, List<string> OpenQuestions, List<string>? Limitations = null);
public sealed record ToolCall(string Id, string Name, JsonObject Arguments);
public sealed record ModelTurn(JsonObject Assistant, ToolCall Call, int TotalTokens);
public sealed record AgentStep(int Number, string Tool, JsonObject Arguments, string Result, int Tokens, DateTimeOffset AtUtc);

public sealed class ResearchRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required ResearchBrief Brief { get; set; }
    public required Limits Limits { get; set; }
    public required string ModelDeployment { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset DeadlineUtc { get; set; }
    public string Status { get; set; } = "running";
    public string StopReason { get; set; } = "";
    public int Version { get; set; }
    public string Mode { get; set; } = "live";
    public Guid? ReplayOf { get; set; }
    // Missing in older checkpoints: retain their original stopping and tool-result behavior.
    public int ContractVersion { get; set; } = 1;
    public int FinishAttempts { get; set; }
    public int Searches { get; set; }
    public int Pages { get; set; }
    public int TotalTokens { get; set; }
    public List<string> Queries { get; set; } = [];
    public List<string> DiscoveredUrls { get; set; } = [];
    public List<SourceSnapshot> Sources { get; set; } = [];
    public List<Evidence> Evidence { get; set; } = [];
    public List<AgentStep> Steps { get; set; } = [];
    public List<JsonObject> Messages { get; set; } = [];
    public ResearchReport? Report { get; set; }
}

public sealed record Limits
{
    public int MaxSteps { get; init; } = 20;
    public int MaxSearches { get; init; } = 4;
    public int MaxPages { get; init; } = 6;
    public int MaxEvidence { get; init; } = 16;
    public int MaxRunSeconds { get; init; } = 300;
    public int MaxReportedTokens { get; init; } = 100000;
    public int MaxContextCharacters { get; init; } = 100000;
    public int MaxPageCharacters { get; init; } = 5000;
    public int MaxSourceCharacters { get; init; } = 100000;
    public int MaxFinishAttempts { get; init; } = 3;
    public int MaxResponseBytes { get; init; } = 2000000;
    public int MinSources { get; init; } = 2;
}

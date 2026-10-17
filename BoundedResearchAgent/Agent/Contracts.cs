using BoundedResearchAgent.Domain;

namespace BoundedResearchAgent.Agent;

public interface IResearchModel
{
    Task<ModelTurn> NextAsync(ResearchRun run, CancellationToken cancellationToken);
}
public interface IWebResearch
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, ResearchBrief brief, CancellationToken cancellationToken);
    Task<SourceSnapshot> ReadAsync(string url, string sourceId, ResearchRun run, CancellationToken cancellationToken);
}
public interface IResearchStore
{
    Task CreateAsync(ResearchRun run, CancellationToken cancellationToken);
    Task<ResearchRun> LoadAsync(Guid id, CancellationToken cancellationToken);
    Task SaveAsync(ResearchRun run, CancellationToken cancellationToken);
}

public sealed class ToolRejectedException(string message) : Exception(message);

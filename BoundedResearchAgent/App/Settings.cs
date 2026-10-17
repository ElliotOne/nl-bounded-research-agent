using BoundedResearchAgent.Domain;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace BoundedResearchAgent.App;

public sealed class Settings
{
    public DatabaseSettings Database { get; set; } = new();
    public FoundrySettings Foundry { get; set; } = new();
    public SearchSettings Search { get; set; } = new();
    public Limits Limits { get; set; } = new();
    public static Settings Load() => new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false).AddEnvironmentVariables("RESEARCH_")
        .Build().Get<Settings>() ?? throw new InvalidOperationException("Configuration is missing.");

    public void ValidateRun()
    {
        _ = Foundry.ChatEndpoint();
        if (string.IsNullOrWhiteSpace(Foundry.Deployment)) throw new InvalidOperationException("Set RESEARCH_Foundry__Deployment.");
        _ = Secret(Foundry.ApiKeyEnvironmentVariable);
        _ = Secret(Search.ApiKeyEnvironmentVariable);
        if (Foundry.MaxOutputTokens < 100 || Search.ResultsPerQuery is < 1 or > 10)
            throw new InvalidOperationException("Invalid provider limits.");
        ValidateLimits(Limits);
    }

    public static void ValidateLimits(Limits limits)
    {
        if (limits.MaxSteps is < 1 or > 100 || limits.MaxSearches < 1 || limits.MaxPages < 1 ||
            limits.MaxEvidence < 1 || limits.MaxRunSeconds is < 1 or > 3600 || limits.MaxReportedTokens < 1 ||
            limits.MaxContextCharacters is < 1000 or > 500000 || limits.MaxPageCharacters is < 100 or > 30000 ||
            limits.MaxSourceCharacters is < 100 or > 500000 || limits.MaxFinishAttempts is < 1 or > 5 ||
            limits.MaxResponseBytes is < 1024 or > 5000000 || limits.MinSources < 1 || limits.MinSources > limits.MaxPages)
            throw new InvalidOperationException("Invalid research limits.");
    }

    public static string Secret(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Set credential environment variable '{name}'.");
}

public sealed class DatabaseSettings
{
    public string ConnectionString { get; set; } = "";
    public string Resolve()
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database))
            throw new InvalidOperationException("Set RESEARCH_Database__ConnectionString.");
        builder.ApplicationName = "bounded-research-agent";
        builder.IncludeErrorDetail = false;
        return builder.ConnectionString;
    }
}

public sealed class FoundrySettings
{
    public string Endpoint { get; set; } = "";
    public string Deployment { get; set; } = "";
    public string ApiKeyEnvironmentVariable { get; set; } = "RESEARCH_FOUNDRY_API_KEY";
    public int MaxOutputTokens { get; set; } = 1800;
    public Uri ChatEndpoint()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            !(uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase)) ||
            uri.AbsolutePath.TrimEnd('/') != "/openai/v1")
            throw new InvalidOperationException("Foundry endpoint must be the HTTPS /openai/v1/ inference URL, not a project URL.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/chat/completions");
    }
}

public sealed class SearchSettings
{
    public string ApiKeyEnvironmentVariable { get; set; } = "RESEARCH_SERPER_API_KEY";
    public int ResultsPerQuery { get; set; } = 5;
}

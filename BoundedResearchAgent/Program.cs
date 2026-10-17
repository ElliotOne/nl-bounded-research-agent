using System.Text;
using BoundedResearchAgent.Agent;
using BoundedResearchAgent.App;
using BoundedResearchAgent.Domain;
using BoundedResearchAgent.Foundry;
using BoundedResearchAgent.Persistence;
using BoundedResearchAgent.Web;
using Npgsql;

Console.OutputEncoding = Encoding.UTF8;
if (args.Length == 0 || args[0] is "help" or "--help")
{
    Console.WriteLine("Bounded Research Agent | PostgreSQL + Microsoft Foundry\nCommands: init-db | run [brief.json] | resume <run-id> | report <run-id> | replay <run-id>");
    return 0;
}
try
{
    var settings = Settings.Load();
    if (args[0] is not ("init-db" or "run" or "resume" or "report" or "replay")) throw new InvalidOperationException("Unknown command. Use help.");
    if (args[0] is "run" or "resume") settings.ValidateRun();
    // Dedicated non-pooled session: session advisory locks cannot leak to another CLI run.
    var connectionSettings = new NpgsqlConnectionStringBuilder(settings.Database.Resolve()) { Pooling = false };
    await using var connection = new NpgsqlConnection(connectionSettings.ConnectionString);
    await connection.OpenAsync();
    var store = new PostgresResearchStore(connection);
    if (args[0] == "init-db")
    {
        await store.MigrateAsync(Path.Combine(AppContext.BaseDirectory, "database"), CancellationToken.None);
        Console.WriteLine("PostgreSQL schema is current.");
        return 0;
    }
    ResearchRun run;
    if (args[0] == "run")
    {
        var path = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "data/briefs/hybrid-cache.json");
        var brief = Json.Read<ResearchBrief>(await File.ReadAllTextAsync(path));
        run = ResearchEngine.Create(brief, settings.Limits, settings.Foundry.Deployment, DateTimeOffset.UtcNow);
        await store.CreateAsync(run, CancellationToken.None);
    }
    else
    {
        if (args.Length != 2 || !Guid.TryParse(args[1], out var id)) throw new InvalidOperationException("Supply a valid run UUID.");
        await store.LockAsync(id, CancellationToken.None);
        run = await store.LoadAsync(id, CancellationToken.None);
    }
    Console.WriteLine($"Run: {run.Id}\nQuestion: {run.Brief.Question}");
    if (args[0] == "replay")
    {
        var captured = run;
        var replay = new CapturedReplay(captured);
        run = CapturedReplay.CreateRun(captured);
        await store.CreateAsync(run, default);
        await store.LockAsync(run.Id, default);
        run = await new ResearchEngine(replay, replay, store, new CapturedReplay.ReplayClock(captured.StartedAtUtc)).ContinueAsync(run);
        if (run.Status != captured.Status || Json.Write(run.Report) != Json.Write(captured.Report))
            throw new InvalidOperationException("Replay diverged from the captured report. Inspect the new run's checkpoints.");
        Console.WriteLine($"Replay run: {run.Id} | Parent: {captured.Id} | Decisions match");
    }
    else if (args[0] != "report")
    {
        if (args[0] == "run") await store.LockAsync(run.Id, CancellationToken.None);
        if (run.ModelDeployment != settings.Foundry.Deployment) throw new InvalidOperationException("Resume requires the original Foundry deployment name.");
        using var modelHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(60) };
        using var searchHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) };
        using var pageHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) };
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var engine = new ResearchEngine(new FoundryResearchModel(settings.Foundry, modelHttp),
            new LiveWebResearch(settings.Search, searchHttp, pageHttp), store);
        run = await engine.ContinueAsync(run, cancellation.Token);
    }
    Console.WriteLine($"Status: {run.Status} | Stop: {run.StopReason}\nSteps: {run.Steps.Count} | Searches: {run.Searches} | Pages: {run.Pages} | Evidence: {run.Evidence.Count} | Reported tokens: {run.TotalTokens}");
    var output = Path.Combine("data", "output");
    Directory.CreateDirectory(output);
    var reportPath = Path.Combine(output, $"{run.Id}.json");
    await File.WriteAllTextAsync(reportPath, Json.Write(new { run.Id, run.Mode, run.ReplayOf, run.Brief, run.Status, run.StopReason, run.Report, run.Sources, run.Evidence, run.Steps }));
    Console.WriteLine($"Report: {Path.GetFullPath(reportPath)}");
    if (run.Report is not null)
    {
        Console.WriteLine(run.Report.Summary);
        foreach (var gap in run.Report.OpenQuestions) Console.WriteLine($"Unresolved: {gap}");
        foreach (var limitation in run.Report.Limitations ?? []) Console.WriteLine($"Caveat: {limitation}");
    }
    if (run.StopReason == "reported_token_budget")
        Console.WriteLine("Start a new run with a higher RESEARCH_Limits__MaxReportedTokens value. Resume keeps the original limits and does not reopen terminal runs.");
    return run.Status == "completed" ? 0 : 2;
}
catch (NpgsqlException ex)
{
    Console.Error.WriteLine(DatabaseErrors.Describe(ex));
    return 1;
}
catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

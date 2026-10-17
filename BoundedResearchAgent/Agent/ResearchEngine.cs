using System.Text.Json;
using System.Text.Json.Nodes;
using BoundedResearchAgent.App;
using BoundedResearchAgent.Domain;
using BoundedResearchAgent.Web;

namespace BoundedResearchAgent.Agent;

public sealed class ResearchEngine(IResearchModel model, IWebResearch web, IResearchStore store, TimeProvider? clock = null)
{
    public const int CurrentContractVersion = 3;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public static ResearchRun Create(ResearchBrief brief, Limits limits, string deployment, DateTimeOffset now)
    {
        SourcePolicy.ValidateBrief(brief);
        Settings.ValidateLimits(limits);
        var run = new ResearchRun
        {
            Brief = brief,
            Limits = limits,
            ModelDeployment = deployment,
            ContractVersion = CurrentContractVersion,
            StartedAtUtc = now,
            DeadlineUtc = now.AddSeconds(limits.MaxRunSeconds),
            DiscoveredUrls = brief.SeedUrls.Select(u => SourcePolicy.CanonicalUrl(u, brief)).Distinct().ToList()
        };
        run.Messages.Add(new JsonObject { ["role"] = "system", ["content"] = ToolCatalog.Instructions });
        run.Messages.Add(new JsonObject { ["role"] = "user", ["content"] = Json.Write(new { brief, limits }) });
        return run;
    }

    public async Task<ResearchRun> ContinueAsync(ResearchRun run, CancellationToken cancellationToken = default)
    {
        if (run.Status != "running") return run;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = run.DeadlineUtc - _clock.GetUtcNow();
        if (remaining > TimeSpan.Zero) deadline.CancelAfter(remaining);
        while (run.Status == "running")
        {
            var stop = BudgetReason(run);
            if (stop is not null)
            {
                Stop(run, stop);
                await store.SaveAsync(run, cancellationToken);
                break;
            }

            // Work on a copy. A failed checkpoint never advances the caller's durable version.
            var next = Json.Read<ResearchRun>(Json.Write(run));
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                var turn = await model.NextAsync(next, deadline.Token);
                next.TotalTokens = checked(next.TotalTokens + turn.TotalTokens);
                string result;
                try
                {
                    if (_clock.GetUtcNow() >= next.DeadlineUtc) throw new OperationCanceledException(deadline.Token);
                    result = await ExecuteAsync(next, turn.Call, deadline.Token);
                }
                catch (Exception ex) when (ex is ToolRejectedException or JsonException or HttpRequestException or InvalidOperationException)
                {
                    result = Json.Write(new { error = ex is ToolRejectedException ? ex.Message : "Tool failed; correct the arguments or try another source." });
                }
                next.Messages.Add(turn.Assistant);
                next.Messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = turn.Call.Id, ["content"] = result });
                next.Steps.Add(new AgentStep(next.Steps.Count + 1, turn.Call.Name,
                    turn.Call.Arguments, result, turn.TotalTokens, _clock.GetUtcNow()));
                if (next.TotalTokens >= next.Limits.MaxReportedTokens && next.Status == "running") Stop(next, "reported_token_budget");
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                Stop(next, cancellationToken.IsCancellationRequested ? "operator_cancelled" : "deadline");
            }
            catch (OperationCanceledException)
            {
                next.Status = "failed";
                next.StopReason = "provider_timeout";
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or OverflowException)
            {
                next.Status = "failed";
                next.StopReason = ex is JsonException ? "model_protocol_error" : "provider_error";
            }
            // A short independent token allows a cancelled run to persist its terminal state.
            using var checkpoint = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await store.SaveAsync(next, checkpoint.Token);
            run = next;
        }
        return run;
    }

    private string? BudgetReason(ResearchRun run)
    {
        if (_clock.GetUtcNow() >= run.DeadlineUtc) return "deadline";
        if (run.Steps.Count >= run.Limits.MaxSteps) return "step_budget";
        if (run.TotalTokens >= run.Limits.MaxReportedTokens) return "reported_token_budget";
        if (ModelContext.Build(run).Sum(m => m.ToJsonString().Length) > run.Limits.MaxContextCharacters) return "context_budget";
        return null;
    }

    private async Task<string> ExecuteAsync(ResearchRun run, ToolCall call, CancellationToken token)
    {
        var args = call.Arguments;
        switch (call.Name)
        {
            case "SearchWeb":
                {
                    var query = Required(args, "query", 350).Trim();
                    if (run.Queries.Contains(query, StringComparer.OrdinalIgnoreCase)) throw new ToolRejectedException("Query already attempted. Refine the query.");
                    if (run.Searches >= run.Limits.MaxSearches) throw new ToolRejectedException("Search budget exhausted; use existing evidence or report gaps.");
                    run.Searches++;
                    run.Queries.Add(query);
                    var hits = await web.SearchAsync(query, run.Brief, token);
                    var admitted = new List<SearchHit>();
                    foreach (var hit in hits.Take(10))
                    {
                        try
                        {
                            var url = SourcePolicy.CanonicalUrl(hit.Url, run.Brief);
                            if (!run.DiscoveredUrls.Contains(url)) run.DiscoveredUrls.Add(url);
                            admitted.Add(hit with { Url = url });
                        }
                        catch (ToolRejectedException) { }
                    }
                    return Json.Write(new { untrustedSearchResults = admitted });
                }
            case "ReadPage":
                {
                    var url = SourcePolicy.CanonicalUrl(Required(args, "url", 2000), run.Brief);
                    if (!run.DiscoveredUrls.Contains(url)) throw new ToolRejectedException("URL was not discovered by search or supplied as a seed.");
                    var existing = run.Sources.FirstOrDefault(s => s.Url == url);
                    if (existing is not null) return run.ContractVersion >= 2
                        ? SourcePageView.Render(existing, args, run.Limits.MaxPageCharacters, cached: true)
                        : Json.Write(new { untrustedSource = existing, cached = true });
                    if (run.Pages >= run.Limits.MaxPages) throw new ToolRejectedException("Page budget exhausted.");
                    run.Pages++;
                    var source = await web.ReadAsync(url, $"S{run.Sources.Count + 1:000}", run, token);
                    _ = SourcePolicy.CanonicalUrl(source.Url, run.Brief);
                    if (run.Sources.Any(s => s.Url == source.Url)) throw new ToolRejectedException("Redirect resolved to an already-read source.");
                    run.Sources.Add(source);
                    return run.ContractVersion >= 2
                        ? SourcePageView.Render(source, args, run.Limits.MaxPageCharacters, cached: false)
                        : Json.Write(new { untrustedSource = source });
                }
            case "RecordEvidence":
                {
                    if (run.Evidence.Count >= run.Limits.MaxEvidence) throw new ToolRejectedException("Evidence budget exhausted.");
                    var question = Required(args, "questionId", 30);
                    var sourceId = Required(args, "sourceId", 30);
                    var claim = Required(args, "claim", 1000);
                    var stance = Required(args, "stance", 20);
                    if (!run.Brief.Questions.Any(q => q.Id == question)) throw new ToolRejectedException("Unknown question ID.");
                    var source = run.Sources.FirstOrDefault(s => s.Id == sourceId) ?? throw new ToolRejectedException("Unknown source ID.");
                    var quote = args["excerptId"] is not null
                        ? SourcePassages.Build(source, run.Limits.MaxPageCharacters)
                            .FirstOrDefault(p => p.Id == Required(args, "excerptId", 40))?.Text
                            ?? throw new ToolRejectedException("Unknown excerptId. Choose a passage ID returned by ReadPage for this source.")
                        : Required(args, "quote", 1000);
                    if (args["excerptId"] is not null && args["quote"] is not null && Required(args, "quote", 1000) != quote)
                        throw new ToolRejectedException("When using excerptId, omit quote; the host copies the stored passage exactly.");
                    if (quote.Length < 20 || !source.Text.Contains(quote, StringComparison.Ordinal)) throw new ToolRejectedException("Quote must be an exact excerpt of at least 20 characters from stored source text.");
                    if (stance is not ("supports" or "contradicts")) throw new ToolRejectedException("Unknown evidence stance.");
                    if (run.Evidence.Any(e => e.QuestionId == question && e.SourceId == sourceId && e.Quote == quote && e.Stance == stance))
                        throw new ToolRejectedException("Evidence already recorded.");
                    var evidence = new Evidence($"E{run.Evidence.Count + 1:000}", question, sourceId, quote, claim, stance);
                    run.Evidence.Add(evidence);
                    return Json.Write(evidence);
                }
            case "FinishResearch":
                {
                    var report = Json.Read<ResearchReport>(args.ToJsonString());
                    ValidateReport(run, report);
                    if (run.ContractVersion >= 3)
                        report = report with
                        {
                            Findings = report.Findings.Select(f => f with
                            {
                                EvidenceIds = f.EvidenceIds.Distinct(StringComparer.Ordinal).ToList()
                            }).ToList()
                        };
                    var cited = report.Findings.SelectMany(f => f.EvidenceIds).ToHashSet(StringComparer.Ordinal);
                    var missing = run.Brief.Questions.Where(q => !run.Evidence.Any(e => e.QuestionId == q.Id && cited.Contains(e.Id)))
                        .Select(q => $"{q.Id}: {q.Text}").ToList();
                    var gaps = report.OpenQuestions.Concat(missing).Distinct().ToList();
                    var citedSourceIds = run.Evidence.Where(e => cited.Contains(e.Id)).Select(e => e.SourceId).ToHashSet();
                    if (run.Sources.Where(s => citedSourceIds.Contains(s.Id)).Select(s => s.Url).Distinct().Count() < run.Limits.MinSources)
                        gaps.Add($"Minimum of {run.Limits.MinSources} distinct source URLs was not reached.");
                    run.Report = report with { OpenQuestions = gaps };
                    run.FinishAttempts++;
                    if (run.ContractVersion >= 2 && gaps.Count > 0 && run.FinishAttempts < run.Limits.MaxFinishAttempts &&
                        run.Steps.Count + 1 < run.Limits.MaxSteps && run.TotalTokens < run.Limits.MaxReportedTokens &&
                        _clock.GetUtcNow() < run.DeadlineUtc)
                    {
                        return Json.Write(new
                        {
                            accepted = false,
                            gaps,
                            finishAttemptsRemaining = run.Limits.MaxFinishAttempts - run.FinishAttempts,
                            citedSourceCount = citedSourceIds.Count,
                            requiredSourceCount = run.Limits.MinSources,
                            unreadUrls = run.DiscoveredUrls.Where(url => !run.Sources.Any(s => s.Url == url)).Take(8).ToList(),
                            next = "Resolve the listed gaps before resubmitting. If a second source is missing, READ an unread URL and record a relevant quote from it; search descriptions do not count. Use ReadPage find/offset for later sections. Optional deployment decisions belong in limitations."
                        });
                    }
                    run.Status = gaps.Count == 0 ? "completed" : "incomplete";
                    run.StopReason = gaps.Count == 0 ? "coverage_gate_passed" : "evidence_gaps";
                    return Json.Write(new { run.Status, run.StopReason, gaps });
                }
            default: throw new ToolRejectedException("Unknown tool. Use only the four declared research tools.");
        }
    }

    public static void ValidateReport(ResearchRun run, ResearchReport report)
    {
        if (string.IsNullOrWhiteSpace(report.Summary) || report.Summary.Length > 4000 || report.Findings is null || report.Findings.Count > 16 ||
            report.OpenQuestions is null || report.OpenQuestions.Count > 16 || report.OpenQuestions.Any(q => string.IsNullOrWhiteSpace(q) || q.Length > 1000) ||
            report.Limitations is { Count: > 16 } || report.Limitations?.Any(q => string.IsNullOrWhiteSpace(q) || q.Length > 1000) == true)
            throw new ToolRejectedException("Report shape is invalid.");
        var ids = run.Evidence.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        if (report.Findings.Any(f => f is null || string.IsNullOrWhiteSpace(f.Claim) || f.Claim.Length > 2000 || f.EvidenceIds is not { Count: > 0 and <= 16 } ||
            f.EvidenceIds.Any(id => !ids.Contains(id)))) throw new ToolRejectedException("Every finding must cite recorded evidence IDs.");
    }

    private static string Required(JsonObject args, string key, int max)
    {
        if (args[key] is not JsonValue node || !node.TryGetValue<string>(out var value) || string.IsNullOrWhiteSpace(value) || value.Length > max)
            throw new ToolRejectedException($"Invalid '{key}' argument.");
        return value;
    }

    private static void Stop(ResearchRun run, string reason)
    {
        run.Status = "incomplete";
        run.StopReason = reason;
        run.Report ??= new ResearchReport("Research stopped before a validated final brief was submitted.", [], run.Brief.Questions.Select(q => $"{q.Id}: {q.Text}").ToList());
    }
}

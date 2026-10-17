using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BoundedResearchAgent.Agent;
using BoundedResearchAgent.App;
using BoundedResearchAgent.Domain;

namespace BoundedResearchAgent.Web;

public sealed class LiveWebResearch(SearchSettings settings, HttpClient searchHttp, HttpClient pageHttp) : IWebResearch
{
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, ResearchBrief brief, CancellationToken cancellationToken)
    {
        var scoped = $"{query} ({string.Join(" OR ", brief.AllowedHosts.Select(h => "site:" + h))})";
        if (scoped.Length > 600)
            throw new ToolRejectedException("Scoped query exceeds the application's 600-character limit. Shorten the query or host list.");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://google.serper.dev/search");
        request.Headers.Add("X-API-KEY", Settings.Secret(settings.ApiKeyEnvironmentVariable));
        // Fetch the standard ten-result page; cap admitted results locally after host filtering.
        request.Content = JsonContent.Create(new { q = scoped, num = 10 });
        using var response = await searchHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Serper returned HTTP {(int)response.StatusCode}.");
        var root = JsonNode.Parse(await BoundedHttp.ReadAsync(response.Content, 1000000, cancellationToken)) as JsonObject
            ?? throw new JsonException("Serper response must be a JSON object.");
        if (root["message"] is not null || root["error"] is not null)
            throw new HttpRequestException("Serper returned an error response.");
        if (root["organic"] is not null and not JsonArray)
            throw new JsonException("Serper organic results must be an array.");
        var hits = new List<SearchHit>();
        foreach (var result in root["organic"]?.AsArray() ?? [])
        {
            if (result is not JsonObject item || item["link"] is not JsonValue link ||
                !link.TryGetValue<string>(out var value)) continue;
            try
            {
                var url = SourcePolicy.CanonicalUrl(value, brief);
                hits.Add(new SearchHit(url, OptionalText(item, "title", 200), OptionalText(item, "snippet", 500)));
            }
            catch (ToolRejectedException) { /* Search filters are advisory; enforce hosts here. */ }
        }
        return hits.DistinctBy(h => h.Url).Take(settings.ResultsPerQuery).ToList();
    }

    private static string OptionalText(JsonObject item, string name, int max) =>
        item[name] is JsonValue node && node.TryGetValue<string>(out var text) ? Clip(text, max) : "";

    public async Task<SourceSnapshot> ReadAsync(string url, string sourceId, ResearchRun run, CancellationToken cancellationToken)
    {
        var current = SourcePolicy.CanonicalUrl(url, run.Brief);
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("BoundedResearchAgent/1.0");
            using var response = await pageHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                if (response.Headers.Location is not { } location) throw new ToolRejectedException("Redirect has no target.");
                current = SourcePolicy.CanonicalUrl(new Uri(new Uri(current), location).AbsoluteUri, run.Brief);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Source returned HTTP {(int)response.StatusCode}.");
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not ("text/html" or "text/plain" or "text/markdown"))
                throw new ToolRejectedException("Only HTML and plain-text sources are supported.");
            var raw = await BoundedHttp.ReadAsync(response.Content, run.Limits.MaxResponseBytes, cancellationToken);
            var text = media == "text/html" ? ExtractText(raw) : Normalize(raw);
            var bound = run.ContractVersion >= 2 ? run.Limits.MaxSourceCharacters : run.Limits.MaxPageCharacters;
            var truncated = text.Length > bound;
            text = Clip(text, bound);
            if (text.Length < 40) throw new ToolRejectedException("Source contains too little readable text.");
            return new SourceSnapshot(sourceId, current, new Uri(current).AbsolutePath, DateTimeOffset.UtcNow,
                text, SourcePolicy.Hash(text), truncated,
                run.ContractVersion >= 2 && media == "text/html" ? ExtractSections(raw, text) : null);
        }
        throw new ToolRejectedException("Source exceeded redirect limit.");
    }

    public static string ExtractText(string html)
    {
        var clean = CleanHtml(html);
        clean = Regex.Replace(clean, "<[^>]+>", " ", RegexOptions.None, TimeSpan.FromSeconds(1));
        return Normalize(WebUtility.HtmlDecode(clean));
    }

    private static string CleanHtml(string html)
    {
        var clean = Regex.Replace(html, @"<(script|style|nav|footer|header|aside)\b[^>]*>[\s\S]*?</\1\s*>", " ",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        var main = Regex.Match(clean, @"<main\b[^>]*>([\s\S]*?)</main\s*>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (main.Success) clean = main.Groups[1].Value;
        return clean;
    }

    public static List<SourceSection> ExtractSections(string html, string text)
    {
        var sections = new List<SourceSection>();
        var position = 0;
        foreach (Match heading in Regex.Matches(CleanHtml(html), @"<h[1-6]\b[^>]*>([\s\S]*?)</h[1-6]\s*>",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
        {
            var title = ExtractText(heading.Groups[1].Value);
            if (title.Length is < 1 or > 160) continue;
            var offset = text.IndexOf(title, position, StringComparison.Ordinal);
            if (offset < 0) continue;
            sections.Add(new SourceSection(title, offset));
            position = offset + title.Length;
            if (sections.Count == 64) break;
        }
        return sections;
    }
    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
    private static string Clip(string text, int max) => text.Length <= max ? text
        : text[..(char.IsHighSurrogate(text[max - 1]) ? max - 1 : max)];
}

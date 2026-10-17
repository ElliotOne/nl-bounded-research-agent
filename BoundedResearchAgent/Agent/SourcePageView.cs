using System.Text.Json.Nodes;
using BoundedResearchAgent.Domain;

namespace BoundedResearchAgent.Agent;

internal static class SourcePageView
{
    public static string Render(SourceSnapshot source, JsonObject args, int maxCharacters, bool cached)
    {
        var offset = 0;
        if (args["offset"] is not null)
        {
            if (args["offset"] is not JsonValue value || !value.TryGetValue(out offset) || offset < 0)
                throw new ToolRejectedException("offset must be a nonnegative integer.");
        }
        if (args["find"] is not null)
        {
            if (args["offset"] is not null || args["find"] is not JsonValue value ||
                !value.TryGetValue<string>(out var find) || string.IsNullOrWhiteSpace(find) || find.Length > 160)
                throw new ToolRejectedException("Supply either offset or a nonempty find term of at most 160 characters.");
            offset = source.Text.IndexOf(find, StringComparison.OrdinalIgnoreCase);
            if (offset < 0) throw new ToolRejectedException("find term is absent. Use an exact listed heading or its offset: " +
                string.Join("; ", (source.Sections ?? []).Select(s => $"{s.Heading} (offset {s.Offset})").Take(20)));
        }
        if (offset >= source.Text.Length) throw new ToolRejectedException("offset is past the end of the stored page.");
        if (offset > 0 && char.IsLowSurrogate(source.Text[offset])) offset--;
        var available = SourcePassages.Build(source, maxCharacters).Where(p => p.End > offset).ToList();
        if (available.Count == 0) throw new ToolRejectedException("offset is past the last readable passage.");
        offset = available[0].Offset;
        var passages = available.TakeWhile(p => p.End - offset <= maxCharacters).ToList();
        var end = passages[^1].End;
        var hasMore = available.Count > passages.Count;
        return Json.Write(new
        {
            untrustedPage = new
            {
                source.Id,
                source.Url,
                source.Title,
                passages = passages.Select(p => new { p.Id, p.Offset, p.Text }),
                offset,
                nextOffset = hasMore ? (int?)end : null,
                totalCharacters = source.Text.Length,
                snapshotSha256 = source.Sha256,
                snapshotTruncated = source.Truncated,
                sections = source.Sections ?? []
            },
            cached
        });
    }
}

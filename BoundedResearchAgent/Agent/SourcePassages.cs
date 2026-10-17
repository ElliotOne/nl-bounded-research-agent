using BoundedResearchAgent.Domain;

namespace BoundedResearchAgent.Agent;

internal sealed record SourcePassage(string Id, int Offset, string Text)
{
    public int End => Offset + Text.Length;
}

internal static class SourcePassages
{
    public static List<SourcePassage> Build(SourceSnapshot source, int windowCharacters)
    {
        var passages = new List<SourcePassage>();
        var max = Math.Min(700, windowCharacters);
        var start = 0;
        while (start < source.Text.Length)
        {
            while (start < source.Text.Length && char.IsWhiteSpace(source.Text[start])) start++;
            if (start == source.Text.Length) break;
            var end = Math.Min(source.Text.Length, start + max);
            if (end < source.Text.Length)
            {
                var window = source.Text[start..end];
                var sentence = Math.Max(window.LastIndexOf(". ", StringComparison.Ordinal),
                    Math.Max(window.LastIndexOf("? ", StringComparison.Ordinal), window.LastIndexOf("! ", StringComparison.Ordinal)));
                var word = window.LastIndexOf(' ');
                if (sentence >= Math.Min(100, max / 2)) end = start + sentence + 1;
                else if (word > 0) end = start + word;
                if (char.IsHighSurrogate(source.Text[end - 1])) end--;
            }
            passages.Add(new SourcePassage($"{source.Id}:P{passages.Count + 1:000}", start, source.Text[start..end]));
            start = end;
        }
        return passages;
    }
}

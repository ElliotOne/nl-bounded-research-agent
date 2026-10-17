using System.Net;
using System.Security.Cryptography;
using System.Text;
using BoundedResearchAgent.Agent;
using BoundedResearchAgent.Domain;

namespace BoundedResearchAgent.Web;

public static class SourcePolicy
{
    public static string CanonicalUrl(string value, ResearchBrief brief)
    {
        if (value.Length > 2000 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length > 0 || IPAddress.TryParse(uri.Host, out _) ||
            !brief.AllowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new ToolRejectedException("URL must use HTTPS on an exact allowed public host.");
        var builder = new UriBuilder(uri) { Fragment = "" };
        return builder.Uri.AbsoluteUri;
    }

    public static void ValidateBrief(ResearchBrief brief)
    {
        if (string.IsNullOrWhiteSpace(brief.Question) || brief.Question.Length > 2000 || brief.Questions is not { Count: > 0 and <= 8 } ||
            brief.Questions.Any(q => q is null || string.IsNullOrWhiteSpace(q.Id) || q.Id.Length > 30 || string.IsNullOrWhiteSpace(q.Text) || q.Text.Length > 1000) ||
            brief.Questions.Select(q => q.Id).Distinct(StringComparer.Ordinal).Count() != brief.Questions.Count ||
            brief.AllowedHosts is not { Count: > 0 and <= 12 } || brief.SeedUrls is null || brief.SeedUrls.Count > 12 ||
            brief.AllowedHosts.Any(h => Uri.CheckHostName(h) != UriHostNameType.Dns || !h.Contains('.') ||
                h.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || h.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Invalid research brief. Use unique questions and exact public DNS host names.");
        foreach (var url in brief.SeedUrls) _ = CanonicalUrl(url, brief);
    }

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

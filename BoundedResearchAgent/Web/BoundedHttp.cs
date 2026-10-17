using System.Text;

namespace BoundedResearchAgent.Web;

public static class BoundedHttp
{
    public static async Task<string> ReadAsync(HttpContent content, int maxBytes, CancellationToken token)
    {
        if (content.Headers.ContentLength > maxBytes) throw new InvalidOperationException("Response exceeded byte limit.");
        await using var stream = await content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new InvalidOperationException("Response exceeded byte limit.");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using BoundedResearchAgent.Domain;

namespace BoundedResearchAgent.Agent;

public static class ModelContext
{
    public static List<JsonObject> Build(ResearchRun run)
    {
        var messages = run.Messages.Select(message => message.DeepClone().AsObject()).ToList();
        if (run.ContractVersion < 2) return messages;
        var windows = new List<(int Index, JsonObject Result)>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i]["role"]?.GetValue<string>() != "tool") continue;
            try
            {
                var result = JsonNode.Parse(messages[i]["content"]!.GetValue<string>()) as JsonObject;
                if (result?["untrustedPage"] is JsonObject) windows.Add((i, result));
            }
            catch (JsonException) { }
        }
        // The complete transcript stays in PostgreSQL. Only older read windows are
        // compacted for the next model request; evidence quotations and native call IDs remain.
        foreach (var (index, result) in windows.SkipLast(2))
        {
            var page = result["untrustedPage"]!.AsObject();
            page.Remove("text");
            page.Remove("passages");
            page.Remove("sections");
            page["textOmitted"] = true;
            page["reread"] = "Use ReadPage with this URL and find/offset to retrieve the text again.";
            messages[index]["content"] = Json.Write(result);
        }
        return messages;
    }
}

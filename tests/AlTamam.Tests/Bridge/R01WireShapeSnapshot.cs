using System.Text.Json;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using Xunit;

namespace AlTamam.Tests.Bridge;

// Contract snapshots retain field names/types, never credentials, nonces,
// generated IDs, file paths or person values. Business assertions remain in
// the native lifecycle tests; shape equality alone is not business acceptance.
internal static class R01WireShapeSnapshot
{
    internal static string Shape(BridgeResponse response)
    {
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(response,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return Describe(wire.RootElement);
    }

    internal static void Verify(string action, BridgeResponse response)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        const string relative = "tests/AlTamam.Tests/Bridge/Snapshots/R01NativeConfigurationResponseShapes.json";
        while (root is not null && !File.Exists(Path.Combine(root.FullName, relative))) root = root.Parent;
        Assert.NotNull(root);
        using var snapshots = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!.FullName, relative)));
        Assert.Contains(LegacyShape(response), snapshots.RootElement.GetProperty(action)
            .EnumerateArray().Select(item => item.GetString()!));
    }

    // Legacy R01 snapshots still require exact pre-V017 fields and types.
    // Strip only the separately tested rank extension before comparing them.
    private static string LegacyShape(BridgeResponse response)
    {
        JsonNode root = JsonSerializer.SerializeToNode(response,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        StripRankExtension(root);
        using var document = JsonDocument.Parse(root.ToJsonString());
        return Describe(document.RootElement);
    }

    private static void StripRankExtension(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array) StripRankExtension(child);
            return;
        }
        if (node is not JsonObject obj) return;
        if (obj.TryGetPropertyValue("rank", out var rank))
        {
            Assert.True(rank is JsonValue value && value.TryGetValue<string>(out _),
                "Rank must always be a string in roster responses.");
            obj.Remove("rank");
        }
        if (obj["customFields"] is JsonObject fields)
            fields.Remove("FLD-CORE-RANK-017");
        if (obj["customColumns"] is JsonArray columns)
        {
            for (int i = columns.Count - 1; i >= 0; i--)
                if (columns[i] is JsonObject col &&
                    col["columnId"]?.GetValue<string>() == "FLD-CORE-RANK-017" &&
                    col["caption"]?.GetValue<string>() == "الرتبة")
                    columns.RemoveAt(i);
        }
        foreach (var child in obj.ToArray()) StripRankExtension(child.Value);
    }

    private static string Describe(JsonElement value, string? field = null)
    {
        // Monthly days is an ISO-date-keyed map, not a set of fixed DTO fields.
        // Validate the key contract without pinning snapshots to a calendar day.
        if (field == "days" && value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in value.EnumerateObject())
                Assert.True(DateOnly.TryParseExact(item.Name, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _), "Invalid monthly date key: " + item.Name);
            return "dateMap<" + string.Join("|", value.EnumerateObject()
                .Select(item => Describe(item.Value)).Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)) + ">";
        }
        return value.ValueKind switch
        {
            JsonValueKind.Object => "object{" + string.Join(",", value.EnumerateObject()
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .Select(item => item.Name + ":" + Describe(item.Value, item.Name))) + "}",
            JsonValueKind.Array => "array<" + string.Join("|", value.EnumerateArray()
                .Select(item => Describe(item)).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal)) + ">",
            JsonValueKind.String => "string",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number",
            JsonValueKind.Null => "null",
            _ => throw new InvalidOperationException("Unsupported JSON wire value")
        };
    }
}

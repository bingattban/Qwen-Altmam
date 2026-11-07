using System.Text.Json;
using AlTamam.Contracts.Bridge;
using Xunit;

namespace AlTamam.Tests.Bridge;

// Validate the response-schema vocabulary used by this repository against
// actual dispatcher serialization. Reject unknown schema keywords, rather than
// silently pretending to implement arbitrary JSON Schema semantics.
internal static class R01ResponseSchema
{
    internal static void AssertMatches(BridgeResponse response, string? action = null)
    {
        DirectoryInfo? root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName,
            "src/AlTamam.Contracts/Schemas/bridge-v1.schema.json"))) root = root.Parent;
        Assert.NotNull(root);
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!.FullName,
            "src/AlTamam.Contracts/Schemas/bridge-v1.schema.json")));
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(response,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Validate(wire.RootElement, schema.RootElement.GetProperty("$defs").GetProperty("response"), "$", schema.RootElement);
        if (response.Success && (action is "GetActiveAttendanceStatuses" or "QueryAttendanceStatuses"
            or "SearchPersons" or "SearchActivePersonsForAccount"))
            Validate(wire.RootElement.GetProperty("data"), schema.RootElement.GetProperty("$defs")
                .GetProperty("responseData_" + action), "$.data", schema.RootElement);
        Assert.Equal(response.RequestId, wire.RootElement.GetProperty("requestId").GetString());
        Assert.Equal(response.Code, wire.RootElement.GetProperty("code").GetString());
        Assert.Equal(response.MessageAr, wire.RootElement.GetProperty("messageAr").GetString());
        if (response.Success) Assert.Equal("OK", response.Code);
        else
        {
            Assert.Contains(response.Code, BridgeErrorCodes.AllCanonicalErrorCodes);
            Assert.Equal(response.ErrorCode, response.Code);
            Assert.Equal(response.UserMessage, response.MessageAr);
            Assert.False(wire.RootElement.TryGetProperty("data", out _));
        }
    }

    private static void Validate(JsonElement value, JsonElement shape, string path, JsonElement document)
    {
        foreach (var keyword in shape.EnumerateObject())
            Assert.Contains(keyword.Name, new[] { "type", "required", "properties", "additionalProperties",
                "items", "enum", "minLength", "maxLength", "minimum", "$ref", "description", "title" });
        if (shape.TryGetProperty("$ref", out var reference))
        {
            string[] segments = reference.GetString()!.Split('/');
            Assert.Equal("#", segments[0]);
            var target = document;
            foreach (string segment in segments.Skip(1)) target = target.GetProperty(segment);
            Validate(value, target, path, document);
            return;
        }
        if (shape.TryGetProperty("type", out var type))
        {
            string actual = value.ValueKind switch {
                JsonValueKind.Object => "object", JsonValueKind.Array => "array",
                JsonValueKind.String => "string", JsonValueKind.True or JsonValueKind.False => "boolean",
                JsonValueKind.Null => "null", JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number",
                _ => "invalid"
            };
            string[] allowed = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Select(x => x.GetString()!).ToArray() : new[] { type.GetString()! };
            Assert.True(allowed.Contains(actual, StringComparer.Ordinal), path + ": unexpected " + actual);
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (shape.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray())
                    Assert.True(value.TryGetProperty(name.GetString()!, out _), path + ": missing " + name.GetString());
            if (shape.TryGetProperty("properties", out var properties))
                foreach (var property in value.EnumerateObject())
                {
                    if (properties.TryGetProperty(property.Name, out var field))
                        Validate(property.Value, field, path + "." + property.Name, document);
                    else if (shape.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.False)
                        Assert.Fail(path + ": undocumented serialized field " + property.Name);
                }
        }
        if (value.ValueKind == JsonValueKind.Array && shape.TryGetProperty("items", out var items))
            foreach (var item in value.EnumerateArray()) Validate(item, items, path + "[]", document);
        if (shape.TryGetProperty("enum", out var values))
            Assert.Contains(values.EnumerateArray(), option => option.GetRawText() == value.GetRawText());
        if (value.ValueKind == JsonValueKind.String)
        {
            if (shape.TryGetProperty("minLength", out var minimum)) Assert.True(value.GetString()!.Length >= minimum.GetInt32(), path);
            if (shape.TryGetProperty("maxLength", out var maximum)) Assert.True(value.GetString()!.Length <= maximum.GetInt32(), path);
        }
        if (value.ValueKind == JsonValueKind.Number && shape.TryGetProperty("minimum", out var lower))
            Assert.True(value.GetDecimal() >= lower.GetDecimal(), path);
    }
}

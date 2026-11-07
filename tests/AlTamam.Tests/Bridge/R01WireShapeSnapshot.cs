using System.Text.Json;
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
        Assert.Contains(Shape(response), snapshots.RootElement.GetProperty(action)
            .EnumerateArray().Select(item => item.GetString()!));
    }

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "object{" + string.Join(",", value.EnumerateObject()
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => item.Name + ":" + Describe(item.Value))) + "}",
        JsonValueKind.Array => "array<" + string.Join("|", value.EnumerateArray()
            .Select(Describe).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal)) + ">",
        JsonValueKind.String => "string",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number",
        JsonValueKind.Null => "null",
        _ => throw new InvalidOperationException("Unsupported JSON wire value")
    };
}

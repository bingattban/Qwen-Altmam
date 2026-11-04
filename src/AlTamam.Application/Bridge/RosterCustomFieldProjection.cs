using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// One authoritative PERSON-field projection for all rosters and exports.
/// Keys are immutable FieldDefinitionId values, never mutable Arabic captions.
/// Read the displayed person's current custom values (not historical snapshots).
/// Hidden/retired definitions and their stored values are left untouched.
/// No N+1 queries: one metadata read and one bounded value read per page.
/// </summary>
internal static class RosterCustomFieldProjection
{
    internal sealed record Column(string Id, string Caption, int FieldType, int DisplayOrder);

    internal static async Task<IReadOnlyList<Column>> VisibleAsync(
        SqliteConnection connection, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT FieldDefinitionId, ArabicCaption, FieldType, DisplayOrder
            FROM CustomFieldDefinitions
            WHERE ActiveFlag=1 AND VisibleFlag=1 AND IsSystemProtected=0
            ORDER BY DisplayOrder, FieldDefinitionId;
            """;
        var result = new List<Column>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(new Column(reader.GetString(0), reader.GetString(1),
                reader.GetInt32(2), reader.GetInt32(3)));
        return result;
    }

    internal static JsonArray Metadata(IReadOnlyList<Column> columns)
    {
        var array = new JsonArray();
        foreach (var col in columns)
            array.Add(new JsonObject
            {
                ["columnId"] = col.Id,
                ["caption"] = col.Caption,
                ["fieldType"] = col.FieldType,
                ["displayOrder"] = col.DisplayOrder
            });
        return array;
    }

    internal static string Cell(JsonObject? person, Column column)
    {
        return person?["customFields"]?[column.Id]?.GetValue<string>() ?? "";
    }

    internal static async Task<JsonArray> AttachAsync(SqliteConnection connection,
        IEnumerable<JsonObject> people, CancellationToken ct,
        IReadOnlyList<Column>? columns = null)
    {
        columns ??= await VisibleAsync(connection, ct).ConfigureAwait(false);
        var records = people.ToArray();
        var persons = new Dictionary<string,List<JsonObject>>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            // Always create an isolated JSON object, even when no field exists.
            record["customFields"] = new JsonObject();
            var id = record["personId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!persons.TryGetValue(id, out var instances))
                persons.Add(id, instances = new List<JsonObject>());
            instances.Add(record);
        }

        if (persons.Count == 0 || columns.Count == 0)
            return Metadata(columns);

        // The UI reads at most 200 people; trusted Excel can request 500.
        // Explicit bounds keep SQLite variable counts and memory predictable.
        if (persons.Count > 500)
            throw new InvalidOperationException("The roster projection page exceeds 500 people.");

        var allowed = new HashSet<string>(columns.Select(c => c.Id), StringComparer.Ordinal);
        using var query = connection.CreateCommand();
        var placeholders = new List<string>(persons.Count);
        int index = 0;
        foreach (string id in persons.Keys)
        {
            string key = "$person" + index++;
            placeholders.Add(key);
            query.Parameters.AddWithValue(key, id);
        }
        query.CommandText = """
            SELECT FieldDefinitionId, PersonId, FieldValueText
            FROM CustomFieldValues
            WHERE PersonId IN (
            """ + string.Join(",", placeholders) + ") AND FieldDefinitionId IN (" +
            string.Join(",", columns.Select((_,i) => "$field" + i)) + ");";
        for (int i = 0; i < columns.Count; i++)
            query.Parameters.AddWithValue("$field" + i, columns[i].Id);
        using var reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string field = reader.GetString(0);
            string id = reader.GetString(1);
            if (!allowed.Contains(field) || !persons.TryGetValue(id, out var targets))
                continue;
            string value = reader.IsDBNull(2) ? "" : reader.GetString(2);
            foreach (var target in targets)
                ((JsonObject)target["customFields"]!)[field] = value;
        }
        return Metadata(columns);
    }
}

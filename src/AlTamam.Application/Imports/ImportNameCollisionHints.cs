using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Imports;

/// <summary>
/// Read-only advisory: a normalized name may be shared by multiple people.
/// Never treat a name match as proof of identity or authorize a Return/new
/// person from this hint. Identity decisions remain in Add Person's guarded flow.
/// </summary>
internal static class ImportNameCollisionHints
{
    internal static async Task<JsonArray> QueryAsync(SqliteConnection connection,
        JsonArray accepted,CancellationToken ct)
    {
        var hints=new JsonArray();
        using var lookup=connection.CreateCommand();
        lookup.CommandText="""
            SELECT PersonId,CurrentVisibleName,CurrentServiceNumberA,ActiveFlag
            FROM Persons
            WHERE NormalizedName=$name
            ORDER BY ActiveFlag DESC,PersonId
            LIMIT 6;
            """;
        var nameParameter=lookup.Parameters.Add("$name",SqliteType.Text);
        foreach(var node in accepted)
        {
            ct.ThrowIfCancellationRequested();
            var row=(JsonObject)node!;
            nameParameter.Value=row["normalizedName"]!.GetValue<string>();
            var matched=new JsonArray();
            using(var reader=await lookup.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while(await reader.ReadAsync(ct).ConfigureAwait(false))
                    matched.Add(new JsonObject {
                        ["personId"]=reader.GetString(0),
                        ["name"]=reader.GetString(1),
                        ["serviceNumberA"]=reader.GetString(2),
                        ["active"]=reader.GetInt64(3)==1
                    });
            if(matched.Count==0)continue;
            hints.Add(new JsonObject {
                ["worksheet"]=row["worksheet"]?.GetValue<string>()??"الورقة الأولى",
                ["row"]=row["row"]!.GetValue<int>(),
                ["name"]=row["name"]!.GetValue<string>(),
                ["serviceNumberA"]=row["serviceNumberA"]!.GetValue<string>(),
                ["matches"]=matched,
                ["advisoryOnly"]=true
            });
        }
        return hints;
    }
}

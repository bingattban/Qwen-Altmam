using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Attendance;

/// <summary>
/// Read-only, SQLCipher-grounded attendance rejection details. Never guesses
/// a person's exit date, identity or current eligibility from UI state.
/// A batch reports ALL problems before any attendance write is attempted.
/// </summary>
internal static class AttendanceConflictDetails
{
    internal static JsonObject Known(string id,string name,string number,string reason)
        => new()
        {
            ["personId"]=id,["name"]=name,["serviceNumberA"]=number,
            ["reason"]=reason
        };

    internal static async Task<JsonObject> DescribeAsync(
        SqliteConnection c,SqliteTransaction? tx,string personId,string businessDate,
        string reason,CancellationToken ct)
    {
        var item=Known(personId,"الفرد غير الموجود في الكشف","غير معروف",reason);
        using(var person=c.CreateCommand())
        {
            person.Transaction=tx;
            person.CommandText="""
                SELECT CurrentVisibleName,CurrentServiceNumberA FROM Persons
                WHERE PersonId=$id;
                """;
            person.Parameters.AddWithValue("$id",personId);
            using var r=await person.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if(await r.ReadAsync(ct).ConfigureAwait(false))
            {
                item["name"]=r.GetString(0);
                item["serviceNumberA"]=r.GetString(1);
            }
        }
        using(var movement=c.CreateCommand())
        {
            movement.Transaction=tx;
            movement.CommandText="""
                SELECT MovementType,EffectiveLocalDate FROM PersonnelMovements
                WHERE PersonId=$id AND EffectiveLocalDate<=$date
                  AND MovementType IN(1,2,3)
                ORDER BY EffectiveLocalDate DESC,CommittedAtUtc DESC LIMIT 1;
                """;
            movement.Parameters.AddWithValue("$id",personId);
            movement.Parameters.AddWithValue("$date",businessDate);
            using var r=await movement.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if(await r.ReadAsync(ct).ConfigureAwait(false))
            {
                int kind=r.GetInt32(0);
                string effective=r.GetString(1);
                string movementName=kind switch
                {
                    1=>"نقل داخلي",2=>"نقل خارجي",3=>"فصل",_=>"حركة"
                };
                // A former internal transfer is not a current eligibility
                // problem, so only show an internal move dated TODAY.
                if(kind!=1 || effective==businessDate)
                {
                    item["movementType"]=movementName;
                    item["movementDate"]=effective;
                    item["reason"]=reason+"؛ "+movementName+" بتاريخ "+effective+".";
                }
            }
        }
        return item;
    }

    internal static BridgeResponse Reject(string code,JsonArray conflicts)
    {
        if(conflicts.Count==0)
            return new("",false,null,code,"تعذر اعتماد التمام؛ أعد المعاينة.");
        string details=string.Join("\n",conflicts.OfType<JsonObject>().Select(x=>
            "«"+x["name"]?.GetValue<string>()+"» (الرقم (ع): "+
            x["serviceNumberA"]?.GetValue<string>()+"): "+
            x["reason"]?.GetValue<string>()));
        return new("",false,new JsonObject {["conflicts"]=conflicts},
            code,"لم يُحفظ أي تحضير بسبب التعارضات التالية:\n"+details);
    }
}

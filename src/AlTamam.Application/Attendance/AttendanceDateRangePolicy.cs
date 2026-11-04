using System.Globalization;
using System.Text.Json.Nodes;

namespace AlTamam.Application.Attendance;

/// <summary>Strict calendar and protected system-status rules for bounded attendance.</summary>
public static class AttendanceDateRangePolicy
{
    public static bool RequiresRange(string statusId) =>
        statusId is "STS-LEAVE" or "STS-PERMISSION";

    public static bool TryParse(JsonObject? payload, string statusId, string today,
        out string start, out string end, out string error)
    {
        start="";end="";error="";
        if(!RequiresRange(statusId))
        {
            if(payload?["rangeStartDate"] is not null || payload?["rangeEndDate"] is not null)
            {error="لا تقبل الحالة المختارة مدة زمنية.";return false;}
            return true;
        }
        try
        {
            start=payload?["rangeStartDate"]?.GetValue<string>()?.Trim()??"";
            end=payload?["rangeEndDate"]?.GetValue<string>()?.Trim()??"";
        }
        catch {error="تاريخ بداية المدة أو نهايتها غير صالح.";return false;}
        if(!DateOnly.TryParseExact(start,"yyyy-MM-dd",CultureInfo.InvariantCulture,
               DateTimeStyles.None,out var from) ||
           !DateOnly.TryParseExact(end,"yyyy-MM-dd",CultureInfo.InvariantCulture,
               DateTimeStyles.None,out var to))
        {error="حدد تاريخ البداية والنهاية بصيغة صحيحة.";return false;}
        if(to<from){error="تاريخ نهاية المدة يسبق تاريخ بدايتها.";return false;}
        // The daily attendance preview commits TODAY's status. A future start
        // requires a separate future-only scheduling workflow, not a false today commit.
        if(start!=today){error="يجب أن تبدأ مدة الإجازة أو الإذن من تاريخ التمام الحالي.";return false;}
        if(to.DayNumber-from.DayNumber>365)
        {error="لا يمكن تحديد مدة تتجاوز 366 يوماً.";return false;}
        return true;
    }
}

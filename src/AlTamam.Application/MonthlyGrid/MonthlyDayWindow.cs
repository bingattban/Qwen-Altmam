namespace AlTamam.Application.MonthlyGrid;

/// <summary>
/// Inclusive requested calendar days, resolved to half-open local-date SQL bounds.
/// Caller still pages personnel independently, so neither all history nor all
/// month days need be fetched for a small visible window.
/// </summary>
internal readonly record struct MonthlyDayWindow(
    DateOnly From, DateOnly UntilExclusive, int FirstDay, int LastDay)
{
    internal static bool TryCreate(int year,int month,int first,int last,
        out MonthlyDayWindow window)
    {
        window=default;
        if(year is <1900 or >2200 || month is <1 or >12)return false;
        int days=DateTime.DaysInMonth(year,month);
        if(first<1 || last<first || last>days)return false;
        var start=new DateOnly(year,month,1);
        window=new(start.AddDays(first-1),start.AddDays(last),first,last);
        return true;
    }
}

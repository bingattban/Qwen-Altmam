namespace AlTamam.Application.Reports;

/// <summary>
/// Stable chronological ordering within a single immutable person's worksheet.
/// The fifth field is the committed ISO effective date; the eighth is its
/// description. No rows are collapsed on name or personnel number.
/// </summary>
internal static class PersonReportChronology
{
    internal static void Sort(List<string[]> rows)
    {
        if(rows.Count<=2)return;
        rows.Sort(1,rows.Count-1,Comparer<string[]>.Create((left,right)=>
        {
            int date=string.CompareOrdinal(left[4],right[4]);
            if(date!=0)return date;
            int kind=string.CompareOrdinal(left[3],right[3]);
            return kind!=0?kind:string.CompareOrdinal(left[7],right[7]);
        }));
    }
}

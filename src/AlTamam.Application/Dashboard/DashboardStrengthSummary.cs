namespace AlTamam.Application.Dashboard;

/// <summary>
/// Phase 06: pure aggregation of GROUP BY results returned from the indexed
/// SQLCipher daily summary. A saved Absent status is committed attendance,
/// never "غير محضر"; an internal move is NOT an exit from total strength.
/// UI code may format these counts but must never derive them from page rows.
/// </summary>
internal sealed class DashboardStrengthSummary
{
    private readonly Dictionary<string,long> _statusCounts=new(StringComparer.Ordinal);
    internal long TotalForce { get; private set; }
    internal long Unprepared { get; private set; }
    internal long InternalTransferDay { get; private set; }
    internal long AutomaticallyPrepared { get; private set; }
    internal long Eligible => TotalForce-InternalTransferDay;
    internal int CompletionPercentage => Eligible==0?0:
        (int)Math.Round(100m*(Eligible-Unprepared)/Eligible,
            MidpointRounding.AwayFromZero);
    internal long StatusCount(string id)=>_statusCounts.GetValueOrDefault(id);

    internal void Accumulate(string? committedStatusId,bool movedToday,
        long persons,long automated)
    {
        if(persons<0||automated<0||automated>persons)
            throw new InvalidOperationException("Invalid database aggregation.");
        TotalForce+=persons;
        AutomaticallyPrepared+=automated;
        if(movedToday)InternalTransferDay+=persons;
        if(committedStatusId is null)
        {
            // A person moving internally today is not silently Absent and
            // cannot be included in today's eligible unprepared roster.
            if(!movedToday)Unprepared+=persons;
        }
        else _statusCounts[committedStatusId]=StatusCount(committedStatusId)+persons;
    }
}

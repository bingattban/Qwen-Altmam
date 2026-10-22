using AlTamam.Domain.Entities;

namespace AlTamam.Application.Rules;

/// <summary>Effective-date assignment invariants; date intervals are [start, end).</summary>
public static class AssignmentPeriodPolicy
{
    public static void ValidateInterval(DateOnly from, DateOnly? exclusiveEnd)
    {
        if (exclusiveEnd is not null && exclusiveEnd.Value <= from)
            throw new ArgumentOutOfRangeException(nameof(exclusiveEnd), "يجب أن يكون تاريخ نهاية الانتساب بعد تاريخ بدايته.");
    }

    public static bool Overlaps(PersonAssignment first, PersonAssignment second)
    {
        if (first.PersonId != second.PersonId) return false;
        ValidateInterval(first.ValidFromLocalDate, first.ValidToLocalDate);
        ValidateInterval(second.ValidFromLocalDate, second.ValidToLocalDate);
        return first.ValidFromLocalDate < (second.ValidToLocalDate ?? DateOnly.MaxValue)
            && second.ValidFromLocalDate < (first.ValidToLocalDate ?? DateOnly.MaxValue);
    }

    public static void EnsureNoOverlap(PersonAssignment proposed, IEnumerable<PersonAssignment> existing)
    {
        ValidateInterval(proposed.ValidFromLocalDate, proposed.ValidToLocalDate);
        if (existing.Any(record => record.AssignmentId != proposed.AssignmentId && Overlaps(proposed, record)))
            throw new InvalidOperationException("لا يمكن تسجيل الفرد في موقعين خلال فترة متداخلة.");
    }
}

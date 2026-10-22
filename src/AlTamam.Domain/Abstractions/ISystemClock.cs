namespace AlTamam.Domain.Abstractions;

/// <summary>
/// Clock abstraction for business-date calculations, schedule handling and clock changes.
/// </summary>
public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }
    DateTime LocalNow { get; }
    DateOnly CurrentBusinessDate { get; }
}

public sealed class SystemClock : ISystemClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public DateTime LocalNow => DateTime.Now;
    public DateOnly CurrentBusinessDate => DateOnly.FromDateTime(DateTime.Now);
}

namespace AlTamam.Application.Movements;

/// <summary>
/// Historical movement types. Empty mode preserves legacy transfer-only archive
/// behavior; "all" additionally displays Return movements created by Add Person.
/// Separation remains exclusively in its dedicated, reason-bearing archive.
/// </summary>
internal static class MovementArchivePolicy
{
    internal static bool TryFilter(string? mode, out int exactType, out int includeReturn)
    {
        (exactType, includeReturn) = mode switch
        {
            null or "" => (0, 0),
            "internal" => (1, 0),
            "external" => (2, 0),
            "return" => (4, 1),
            "all" => (0, 1),
            _ => (-1, 0)
        };
        return exactType >= 0;
    }

    internal static string Label(int movementType) => movementType switch
    {
        1 => "داخلي",
        2 => "خارجي",
        4 => "عودة",
        _ => throw new ArgumentOutOfRangeException(nameof(movementType))
    };
}

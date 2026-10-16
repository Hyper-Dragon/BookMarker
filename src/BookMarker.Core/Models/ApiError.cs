namespace BookMarker.Core.Models;

/// <summary>Error payload for 4xx/5xx responses (JSON).</summary>
public sealed record ApiError
{
    public bool Success => false;

    public required string[] Errors { get; init; }

    public string? TraceId { get; init; }

    /// <summary>Tail of the marker log (5xx conversion failures only).</summary>
    public string? LogTail { get; init; }
}

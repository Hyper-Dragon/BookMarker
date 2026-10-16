namespace BookMarker.Core.Models;

/// <summary>Result of one conversion (single attempt or llmfallback pipeline).</summary>
public sealed record ConvertOutcome
{
    public required bool Success { get; init; }

    public string? Markdown { get; init; }

    /// <summary>Pages converted (0 when not determinable).</summary>
    public int Pages { get; init; }

    /// <summary>Total wall time of the whole pipeline for this job (ms), including a fallback re-run.</summary>
    public long ElapsedMs { get; init; }

    public double? SecPerPage { get; init; }

    public required LlmMode RequestedLlmMode { get; init; }

    /// <summary>True when an LLM-enabled marker run produced this result.</summary>
    public bool LlmUsed { get; init; }

    /// <summary>True when llmfallback engaged and this result came from a no-llm run.</summary>
    public bool LlmFellBack { get; init; }

    /// <summary>True when the shim reported LLM call failures during the run.</summary>
    public bool LlmDegraded { get; init; }

    /// <summary>Human note about llm handling (fallback reason, etc).</summary>
    public string? LlmNote { get; init; }

    public string[]? Errors { get; init; }

    /// <summary>Tail of the marker log (for 5xx bodies and diagnostics).</summary>
    public string? LogTail { get; init; }
}

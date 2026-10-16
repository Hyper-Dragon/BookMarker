using System.Text.Json.Serialization;

namespace BookMarker.Core.Models;

/// <summary>Live record of one conversion job, surfaced on the status page / /api/status.</summary>
public sealed class JobRecord
{
    public required string TraceId { get; init; }

    public required string FileName { get; init; }

    public required long FileBytes { get; init; }

    /// <summary>marker page_range string (0-based; e.g. "0-2,5"), or null for the whole document.</summary>
    public string? PageRange { get; init; }

    public required LlmMode LlmMode { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Per-job scratch workspace (service-owned). Never serialized.</summary>
    [JsonIgnore]
    public string JobDir { get; init; } = string.Empty;

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>"running" | "succeeded" | "failed".</summary>
    public string State { get; set; } = "running";

    /// <summary>Current phase label for the status page (e.g. "converting (llm)").</summary>
    public string? Phase { get; set; }

    /// <summary>Pages converted (final).</summary>
    public int Pages { get; set; }

    /// <summary>Live page progress (when parseable from the marker output).</summary>
    public int? PagesDone { get; set; }

    public int? PagesTotal { get; set; }

    public long ResultChars { get; set; }

    public double? SecPerPage { get; set; }

    public bool LlmUsed { get; set; }

    public bool LlmFellBack { get; set; }

    public string? Error { get; set; }

    /// <summary>Most recent marker output line.</summary>
    public string? LastProgress { get; set; }

    /// <summary>Recent marker output lines (capped ring, newest last).</summary>
    public List<string> ProgressLines { get; set; } = [];

    public double ElapsedSeconds =>
        ((FinishedAt ?? DateTimeOffset.UtcNow) - StartedAt).TotalSeconds;
}

public static class BookMarkerInfo
{
    public const string Name = "BookMarker";
    public const string Version = "1.0.0";
    public const string Description = "Standalone marker-pdf wrapper: a concurrency-1 HTTP service that converts PDFs to Markdown.";
}

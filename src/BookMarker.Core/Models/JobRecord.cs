using System.Reflection;
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

    public const string Description =
        "Unofficial container and HTTP service for marker-pdf (Datalab): a concurrency-1 PDF to Markdown service.";

    /// <summary>
    /// Service version, read from the assembly's informational version: version.txt plus the
    /// commit when the build stamps one ("0.1.1+091a8ea"). No other file records it.
    /// </summary>
    public static string Version { get; } = ReadInformationalVersion();

    /// <summary>
    /// The commit this build came from, lifted out of the version's build metadata, or "unknown"
    /// when the build stamped nothing. Surfaced on its own so a bug report can name the commit
    /// without anyone parsing a version string.
    /// </summary>
    public static string Commit { get; } = ReadCommit(Version);

    /// <summary>
    /// Version of the upstream engine this build packages, stamped by the image build
    /// (-p:MarkerVersion). BookMarker adds no conversion logic of its own, so the running
    /// service says which marker-pdf it is packaging rather than leaving that to the docs.
    /// </summary>
    public static string EngineVersion { get; } = ReadMetadata("MarkerVersion") ?? "unknown";

    /// <summary>Human-readable engine identity, e.g. "marker-pdf 1.10.2".</summary>
    public static string Engine { get; } = EngineVersion == "unknown"
        ? "marker-pdf (version not stamped)"
        : $"marker-pdf {EngineVersion}";

    private static Assembly InfoAssembly => typeof(BookMarkerInfo).Assembly;

    private static string ReadInformationalVersion()
    {
        var attribute = InfoAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return string.IsNullOrWhiteSpace(attribute?.InformationalVersion)
            ? InfoAssembly.GetName().Version?.ToString(3) ?? "unknown"
            : attribute.InformationalVersion;
    }

    private static string ReadCommit(string version)
    {
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 && plus < version.Length - 1 ? version[(plus + 1)..] : "unknown";
    }

    private static string? ReadMetadata(string key) =>
        InfoAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)?.Value;
}

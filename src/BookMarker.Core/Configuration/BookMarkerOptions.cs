namespace BookMarker.Core.Configuration;

/// <summary>
/// Service configuration. Every knob is settable from the process environment so a
/// deployment can tune the service without rebuilding the image.
/// </summary>
public sealed class BookMarkerOptions
{
    /// <summary>
    /// The service processes exactly one conversion at a time, by design. Requests that
    /// arrive while a conversion is in flight are refused immediately with 503 +
    /// Retry-After (deliberate backpressure - never queued, never held).
    /// </summary>
    public const int Concurrency = 1;

    /// <summary>LLM service (module.member) passed to marker's --llm_service.</summary>
    public const string LlmServiceClass = "marker_openai_service.OpenAICompatibleService";

    public int Port { get; set; } = 8080;

    public string LogLevel { get; set; } = "Information";

    /// <summary>Retry-After (seconds) sent with a 503 capacity refusal.</summary>
    public int BusyRetryAfterSeconds { get; set; } = 15;

    /// <summary>Hard timeout per conversion attempt (milliseconds). 0 = unlimited.</summary>
    public int MaxTimeMs { get; set; } = 3_600_000;

    /// <summary>Maximum upload size (bytes) accepted by POST /convert.</summary>
    public long MaxUploadBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>marker --workers value (pool size). Tunable per deployment.</summary>
    public int Workers { get; set; } = 1;

    /// <summary>marker --disable_multiprocessing (pdftext_workers=1).</summary>
    public bool DisableMultiprocessing { get; set; } = true;

    /// <summary>marker --disable_image_extraction (markdown only; images are not extracted).</summary>
    public bool DisableImageExtraction { get; set; } = true;

    /// <summary>Root for per-job scratch workspaces (service-owned; never caller paths).</summary>
    public string ScratchRoot { get; set; } = "/tmp/bookmarker";

    /// <summary>marker executable (on PATH inside the image).</summary>
    public string MarkerBinary { get; set; } = "marker";

    /// <summary>Directory holding the LLM shim module (added to PYTHONPATH).</summary>
    public string ShimDir { get; set; } = "/app/shim";

    // ---- LLM backend (OpenAI-compatible). Only used when a request asks for LLM
    // (llmonly / llmfallback). nollm requests never touch any of this. ----

    public string? LlmBaseUrl { get; set; }

    public string? LlmModel { get; set; }

    public string? LlmApiKey { get; set; }

    public int LlmTimeoutSeconds { get; set; } = 600;

    public int LlmMaxRetries { get; set; } = 4;

    public int LlmRetryWaitSeconds { get; set; } = 5;

    /// <summary>True when an LLM backend is configured (base URL + model present).</summary>
    public bool LlmConfigured =>
        !string.IsNullOrWhiteSpace(LlmBaseUrl) && !string.IsNullOrWhiteSpace(LlmModel);

    public static BookMarkerOptions FromEnvironment() => new()
    {
        Port = GetEnvInt("PORT", 8080),
        LogLevel = Environment.GetEnvironmentVariable("LOG_LEVEL") ?? "Information",
        BusyRetryAfterSeconds = GetEnvInt("BUSY_RETRY_AFTER_SECONDS", 15),
        MaxTimeMs = GetEnvInt("MAX_TIME_MS", 3_600_000),
        MaxUploadBytes = GetEnvLong("MAX_UPLOAD_BYTES", 512L * 1024 * 1024),
        Workers = GetEnvInt("WORKERS", 1),
        DisableMultiprocessing = GetEnvBool("DISABLE_MULTIPROCESSING", true),
        DisableImageExtraction = GetEnvBool("DISABLE_IMAGE_EXTRACTION", true),
        ScratchRoot = Environment.GetEnvironmentVariable("SCRATCH_ROOT") ?? "/tmp/bookmarker",
        MarkerBinary = Environment.GetEnvironmentVariable("MARKER_BINARY") ?? "marker",
        ShimDir = Environment.GetEnvironmentVariable("SHIM_DIR") ?? "/app/shim",
        LlmBaseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL"),
        LlmModel = Environment.GetEnvironmentVariable("OPENAI_MODEL"),
        LlmApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
        LlmTimeoutSeconds = GetEnvInt("LLM_TIMEOUT_SECONDS", 600),
        LlmMaxRetries = GetEnvInt("LLM_MAX_RETRIES", 4),
        LlmRetryWaitSeconds = GetEnvInt("LLM_RETRY_WAIT_SECONDS", 5),
    };

    private static int GetEnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

    private static long GetEnvLong(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

    private static bool GetEnvBool(string name, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value switch
        {
            null or "" => fallback,
            "1" or "true" or "True" or "TRUE" or "yes" or "on" => true,
            "0" or "false" or "False" or "FALSE" or "no" or "off" => false,
            _ => fallback,
        };
    }
}

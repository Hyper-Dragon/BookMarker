namespace BookMarker.Core.Models;

/// <summary>
/// Per-request LLM policy (tri-state, specified on the request):
///
/// <list type="bullet">
/// <item><c>nollm</c> (default) - run marker without an LLM backend.</item>
/// <item><c>llmonly</c> - run with the LLM backend; if the LLM path is unavailable
/// (not configured, or the LLM failed during the run) the request FAILS. No silent
/// degradation.</item>
/// <item><c>llmfallback</c> - try the LLM backend; if it is not configured or fails
/// during the run, re-run without the LLM and return that result (the fallback is
/// reported on the job/response).</item>
/// </list>
/// </summary>
public enum LlmMode
{
    NoLlm,
    LlmOnly,
    LlmFallback,
}

public static class LlmModeParser
{
    /// <summary>
    /// Parses the request value. Null/empty means <see cref="LlmMode.NoLlm"/> (the default).
    /// Returns null for unrecognised values (callers should answer 400).
    /// </summary>
    public static LlmMode? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return LlmMode.NoLlm;
        }

        var normalized = value.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
        return normalized switch
        {
            "nollm" => LlmMode.NoLlm,
            "llmonly" => LlmMode.LlmOnly,
            "llmfallback" => LlmMode.LlmFallback,
            _ => null,
        };
    }

    public static string ToWire(this LlmMode mode) => mode switch
    {
        LlmMode.NoLlm => "nollm",
        LlmMode.LlmOnly => "llmonly",
        LlmMode.LlmFallback => "llmfallback",
        _ => "nollm",
    };
}

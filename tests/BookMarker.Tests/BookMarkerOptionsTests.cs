using BookMarker.Core.Configuration;

namespace BookMarker.Tests;

public sealed class BookMarkerOptionsTests
{
    private static readonly string[] AllKeys =
    [
        "PORT", "LOG_LEVEL", "BUSY_RETRY_AFTER_SECONDS", "MAX_TIME_MS", "MAX_UPLOAD_BYTES",
        "WORKERS", "DISABLE_MULTIPROCESSING", "DISABLE_IMAGE_EXTRACTION", "SCRATCH_ROOT",
        "MARKER_BINARY", "SHIM_DIR", "OPENAI_BASE_URL", "OPENAI_MODEL", "OPENAI_API_KEY",
        "LLM_TIMEOUT_SECONDS", "LLM_MAX_RETRIES", "LLM_RETRY_WAIT_SECONDS",
    ];

    private static EnvScope Clear() => AllKeys.Aggregate(new EnvScope(), (scope, key) => scope.Set(key, null));

    [Fact]
    public void Defaults_match_the_documented_contract()
    {
        using var _ = Clear();
        var options = BookMarkerOptions.FromEnvironment();

        Assert.Equal(8080, options.Port);
        Assert.Equal(15, options.BusyRetryAfterSeconds);
        Assert.Equal(3_600_000, options.MaxTimeMs);
        Assert.Equal(512L * 1024 * 1024, options.MaxUploadBytes);
        Assert.Equal(1, options.Workers);
        Assert.True(options.DisableMultiprocessing);
        Assert.True(options.DisableImageExtraction);
        Assert.Equal("/tmp/bookmarker", options.ScratchRoot);
        Assert.Equal("marker", options.MarkerBinary);
        Assert.False(options.LlmConfigured);
    }

    [Fact]
    public void Every_documented_value_is_overridable_from_the_environment()
    {
        using var _ = Clear()
            .Set("PORT", "9000")
            .Set("BUSY_RETRY_AFTER_SECONDS", "3")
            .Set("MAX_UPLOAD_BYTES", "1234")
            .Set("WORKERS", "4")
            .Set("SCRATCH_ROOT", "/scratch")
            .Set("MARKER_BINARY", "/usr/local/bin/marker");

        var options = BookMarkerOptions.FromEnvironment();

        Assert.Equal(9000, options.Port);
        Assert.Equal(3, options.BusyRetryAfterSeconds);
        Assert.Equal(1234, options.MaxUploadBytes);
        Assert.Equal(4, options.Workers);
        Assert.Equal("/scratch", options.ScratchRoot);
        Assert.Equal("/usr/local/bin/marker", options.MarkerBinary);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("off", false)]
    [InlineData("banana", true)] // unrecognised keeps the default
    public void Booleans_accept_the_documented_spellings(string raw, bool expected)
    {
        using var _ = Clear().Set("DISABLE_IMAGE_EXTRACTION", raw);

        Assert.Equal(expected, BookMarkerOptions.FromEnvironment().DisableImageExtraction);
    }

    [Fact]
    public void Llm_is_configured_only_when_both_base_url_and_model_are_present()
    {
        using var scope = new EnvScope();

        scope.Set("OPENAI_BASE_URL", "http://llm:8000/v1").Set("OPENAI_MODEL", null);
        Assert.False(BookMarkerOptions.FromEnvironment().LlmConfigured);

        scope.Set("OPENAI_MODEL", "   ");
        Assert.False(BookMarkerOptions.FromEnvironment().LlmConfigured);

        scope.Set("OPENAI_MODEL", "gpt-oss-120b");
        var configured = BookMarkerOptions.FromEnvironment();
        Assert.True(configured.LlmConfigured);
        Assert.Equal("gpt-oss-120b", configured.LlmModel);
    }

    [Fact]
    public void Non_numeric_values_fall_back_instead_of_throwing()
    {
        using var _ = Clear().Set("PORT", "not-a-port").Set("MAX_UPLOAD_BYTES", "lots");

        var options = BookMarkerOptions.FromEnvironment();

        Assert.Equal(8080, options.Port);
        Assert.Equal(512L * 1024 * 1024, options.MaxUploadBytes);
    }
}

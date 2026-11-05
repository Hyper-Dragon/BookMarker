using BookMarker.Core.Models;

namespace BookMarker.Tests;

public sealed class LlmModeParserTests
{
    [Theory]
    [InlineData(null, LlmMode.NoLlm)]
    [InlineData("", LlmMode.NoLlm)]
    [InlineData("   ", LlmMode.NoLlm)]
    [InlineData("nollm", LlmMode.NoLlm)]
    [InlineData("no-llm", LlmMode.NoLlm)]
    [InlineData("llmonly", LlmMode.LlmOnly)]
    [InlineData("LLM-Only", LlmMode.LlmOnly)]
    [InlineData("llm_only", LlmMode.LlmOnly)]
    [InlineData("llmfallback", LlmMode.LlmFallback)]
    [InlineData(" LLM_Fallback ", LlmMode.LlmFallback)]
    public void Parses_the_documented_values_and_their_common_spellings(string? wire, LlmMode expected)
    {
        Assert.Equal(expected, LlmModeParser.Parse(wire));
    }

    [Theory]
    [InlineData("llm")]
    [InlineData("fallback")]
    [InlineData("nolm")]
    [InlineData("true")]
    public void Unknown_values_return_null_so_the_caller_answers_400(string wire)
    {
        Assert.Null(LlmModeParser.Parse(wire));
    }

    [Theory]
    [InlineData(LlmMode.NoLlm, "nollm")]
    [InlineData(LlmMode.LlmOnly, "llmonly")]
    [InlineData(LlmMode.LlmFallback, "llmfallback")]
    public void ToWire_round_trips(LlmMode mode, string wire)
    {
        Assert.Equal(wire, mode.ToWire());
        Assert.Equal(mode, LlmModeParser.Parse(wire));
    }
}

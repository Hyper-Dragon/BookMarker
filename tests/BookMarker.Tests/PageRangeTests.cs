using BookMarker.Core.Services;

namespace BookMarker.Tests;

public sealed class PageRangeTests
{
    [Theory]
    [InlineData("0", 1)]
    [InlineData("0-2", 3)]
    [InlineData("0,5-10,20", 8)]
    [InlineData("3-3", 1)]
    [InlineData("0;2", 0)] // only commas separate, as in marker
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("abc", 0)]
    [InlineData("5-2", 0)] // reversed range contributes nothing
    [InlineData("-", 0)]
    public void Counts_the_pages_a_marker_page_range_covers(string range, int expected)
    {
        Assert.Equal(expected, MarkerRunner.CountPageRange(range));
    }
}

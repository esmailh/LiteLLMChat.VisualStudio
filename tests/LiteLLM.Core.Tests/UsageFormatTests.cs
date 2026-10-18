using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class UsageFormatTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1000, "1K")]
    [InlineData(1500, "1.5K")]
    [InlineData(12345, "12.3K")]
    [InlineData(999949, "999.9K")]
    [InlineData(999950, "1M")]
    [InlineData(1234567, "1.2M")]
    [InlineData(1500000000, "1.5B")]
    public void Short_KeepsAListRowShortWithoutRoundingIntoTheWrongUnit(long tokens, string expected) =>
        Assert.Equal(expected, UsageFormat.Short(tokens));

    [Fact]
    public void Short_TreatsANegativeCountAsNothing() => Assert.Equal("0", UsageFormat.Short(-5));

    [Fact]
    public void Count_GroupsThousandsWithAnInvariantSeparator() =>
        Assert.Equal("1,234,567", UsageFormat.Count(1234567));
}

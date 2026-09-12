using ResourceGrab.Core.Utils;
using Xunit;

namespace ResourceGrab.Core.Tests.Utils;

public class JmIdParserTests
{
    [Theory]
    [InlineData("JM123456", 123456)]
    [InlineData("jm123456", 123456)]
    [InlineData("Jm123456", 123456)]
    [InlineData("JM-123456", 123456)]
    [InlineData("JM_123456", 123456)]
    [InlineData("JM 123456", 123456)]
    [InlineData("123456", 123456)]
    [InlineData("  jm654321  ", 654321)]
    [InlineData("【JM123456】", 123456)]
    [InlineData("[123456]", 123456)]
    public void TryParse_AcceptsJmIdForms(string input, long expected)
    {
        Assert.True(JmIdParser.TryParse(input, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("JM")]
    [InlineData("jmabc")]
    [InlineData("abc123")]
    [InlineData("12")]
    [InlineData("123")]
    [InlineData("12345678901")]
    [InlineData("JM12")]
    [InlineData("某作者")]
    [InlineData("12 34")]
    public void TryParse_RejectsNonIds(string? input)
    {
        Assert.False(JmIdParser.TryParse(input, out _));
    }
}

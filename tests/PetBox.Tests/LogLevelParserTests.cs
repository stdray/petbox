namespace PetBox.Tests;

public sealed class LogLevelParserTests
{
	[Theory]
	[InlineData("Verbose", LogLevel.Verbose)]
	[InlineData("Trace", LogLevel.Verbose)]
	[InlineData("Debug", LogLevel.Debug)]
	[InlineData("Information", LogLevel.Information)]
	[InlineData("Info", LogLevel.Information)]
	[InlineData("Warning", LogLevel.Warning)]
	[InlineData("Warn", LogLevel.Warning)]
	[InlineData("Error", LogLevel.Error)]
	[InlineData("Fatal", LogLevel.Fatal)]
	[InlineData("Critical", LogLevel.Fatal)]
	[InlineData("ERROR", LogLevel.Error)]
	[InlineData("warn", LogLevel.Warning)]
	public void Parse_KnownLevels(string str, LogLevel expected)
	{
		LogLevelParser.Parse(str).Should().Be(expected);
	}

	[Theory]
	[InlineData("")]
	[InlineData("Bogus")]
	[InlineData(null)]
	public void Parse_Invalid_ReturnsNull(string? str)
	{
		LogLevelParser.Parse(str).Should().BeNull();
	}

	[Fact]
	public void TryParse_Valid_ReturnsTrue()
	{
		LogLevelParser.TryParse("Error", out var l).Should().BeTrue();
		l.Should().Be(LogLevel.Error);
	}

	[Fact]
	public void TryParse_Invalid_ReturnsFalse()
	{
		LogLevelParser.TryParse("nope", out _).Should().BeFalse();
	}
}

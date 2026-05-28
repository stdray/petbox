namespace PetBox.Log.Core.Models;

public static class LogLevelParser
{
	public static LogLevel? Parse(string? s) => s?.ToUpperInvariant() switch
	{
		"VERBOSE" or "TRACE" => LogLevel.Verbose,
		"DEBUG" => LogLevel.Debug,
		"INFORMATION" or "INFO" => LogLevel.Information,
		"WARNING" or "WARN" => LogLevel.Warning,
		"ERROR" => LogLevel.Error,
		"FATAL" or "CRITICAL" => LogLevel.Fatal,
		_ => null,
	};

	public static bool TryParse(string? s, out LogLevel level)
	{
		var parsed = Parse(s);
		if (parsed is { } l)
		{
			level = l;
			return true;
		}
		level = default;
		return false;
	}
}

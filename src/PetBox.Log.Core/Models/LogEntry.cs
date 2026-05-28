using System.Collections.Immutable;
using System.Text.Json;

namespace PetBox.Log.Core.Models;

public enum LogLevel { Verbose = 0, Debug = 1, Information = 2, Warning = 3, Error = 4, Fatal = 5 }

public sealed record LogEntry
{
	public long Id { get; init; }
	public string ServiceKey { get; init; } = string.Empty;
	public DateTime Timestamp { get; init; }
	public LogLevel Level { get; init; }
	public string Message { get; init; } = string.Empty;
	public string MessageTemplate { get; init; } = string.Empty;
	public string? Exception { get; init; }
	public string Properties { get; init; } = "{}";

	public ImmutableDictionary<string, JsonElement> GetProperties()
	{
		if (string.IsNullOrEmpty(Properties) || Properties == "{}")
			return ImmutableDictionary<string, JsonElement>.Empty;

		try
		{
			var doc = JsonDocument.Parse(Properties);
			var builder = ImmutableDictionary.CreateBuilder<string, JsonElement>();
			foreach (var prop in doc.RootElement.EnumerateObject())
				builder[prop.Name] = prop.Value.Clone();
			return builder.ToImmutable();
		}
		catch
		{
			return ImmutableDictionary<string, JsonElement>.Empty;
		}
	}
}

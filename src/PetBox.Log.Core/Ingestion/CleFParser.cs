using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PetBox.Log.Core.Models;

namespace PetBox.Log.Core.Ingestion;

#pragma warning disable CA1822

public sealed class CleFParser
{
	public static CleFLineResult ParseLine(string json, int lineNumber)
	{
		JsonDocument doc;
		try
		{
			doc = JsonDocument.Parse(json);
		}
		catch (JsonException)
		{
			return CleFLineResult.Failure(lineNumber, CleFErrorKind.MalformedJson, "Invalid JSON");
		}

		var root = doc.RootElement;
		if (root.ValueKind != JsonValueKind.Object)
			return CleFLineResult.Failure(lineNumber, CleFErrorKind.MalformedJson, "Expected JSON object");

		if (!root.TryGetProperty("@t", out var tsProp))
			return CleFLineResult.Failure(lineNumber, CleFErrorKind.MissingTimestamp, "Missing @t field");

		if (!DateTime.TryParse(tsProp.GetString(), CultureInfo.InvariantCulture,
				DateTimeStyles.RoundtripKind, out var timestamp))
			return CleFLineResult.Failure(lineNumber, CleFErrorKind.InvalidTimestamp, $"Invalid @t value: {tsProp.GetString()}");

		var level = LogLevel.Information;
		if (root.TryGetProperty("@l", out var lProp))
		{
			var levelStr = lProp.ValueKind switch
			{
				JsonValueKind.String => lProp.GetString(),
				JsonValueKind.Number => lProp.GetRawText(),
				_ => null,
			};
			if (levelStr is not null && LogLevelParser.TryParse(levelStr, out var parsed))
				level = parsed;
		}

		var message = "";
		if (root.TryGetProperty("@m", out var mProp) && mProp.ValueKind == JsonValueKind.String)
			message = mProp.GetString()!;

		var messageTemplate = message;
		if (root.TryGetProperty("@mt", out var mtProp) && mtProp.ValueKind == JsonValueKind.String)
			messageTemplate = mtProp.GetString()!;

		if (string.IsNullOrEmpty(message))
			message = messageTemplate;

		string? exception = null;
		if (root.TryGetProperty("@x", out var xProp) && xProp.ValueKind == JsonValueKind.String)
			exception = xProp.GetString();

		var props = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
		foreach (var prop in root.EnumerateObject())
		{
			var name = prop.Name;
			if (name.Length == 0) continue;
			if (name[0] == '@')
			{
				if (name.Length > 1 && name[1] == '@')
					props[name[1..]] = prop.Value.Clone();
				continue;
			}
			props[name] = prop.Value.Clone();
		}

		var candidate = new LogEntryCandidate
		{
			Timestamp = timestamp,
			Level = level,
			Message = message,
			MessageTemplate = messageTemplate,
			Exception = exception,
			Properties = PropertiesJsonSerializer.Serialize(props.ToImmutable()),
		};

		return CleFLineResult.Success(lineNumber, candidate);
	}

	public async IAsyncEnumerable<CleFLineResult> ParseAsync(
		Stream stream,
		[EnumeratorCancellation] CancellationToken ct)
	{
		using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
		var lineNumber = 0;
		while (!ct.IsCancellationRequested)
		{
			var line = await reader.ReadLineAsync(ct);
			if (line is null) break;
			lineNumber++;
			if (string.IsNullOrWhiteSpace(line)) continue;
			yield return ParseLine(line, lineNumber);
		}
	}
}

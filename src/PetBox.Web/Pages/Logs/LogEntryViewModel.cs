using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using PetBox.Log.Core.Data;

namespace PetBox.Web.Pages.Logs;

public sealed record LogEntryViewModel
{
	public long Id { get; init; }
	public string ServiceKey { get; init; } = string.Empty;
	public DateTime Timestamp { get; init; }
	public int Level { get; init; }
	public string LevelName { get; init; } = string.Empty;
	public string Message { get; init; } = string.Empty;
	public string RenderedMessage { get; init; } = string.Empty;
	public string MessageTemplate { get; init; } = string.Empty;
	public string? Exception { get; init; }
	public string? TraceId { get; init; }
	public string? SpanId { get; init; }
	public int? EventId { get; init; }
	public Dictionary<string, object?> Properties { get; init; } = new();
	public bool IsLive { get; init; }

	static readonly Regex TemplatePlaceholder = new(@"\{(\w+)\}", RegexOptions.Compiled);

	public static LogEntryViewModel FromRecord(LogEntryRecord r)
	{
		var props = ParseProperties(r.PropertiesJson);
		var rendered = RenderMessage(r.MessageTemplate, r.Message, props);

		props.TryGetValue("TraceId", out var traceIdObj);
		props.TryGetValue("SpanId", out var spanIdObj);
		props.TryGetValue("EventId", out var eventIdObj);

		return new LogEntryViewModel
		{
			Id = r.Id,
			ServiceKey = r.ServiceKey,
			Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(r.TimestampMs).UtcDateTime,
			Level = r.Level,
			LevelName = LevelLabel(r.Level),
			Message = r.Message,
			RenderedMessage = rendered,
			MessageTemplate = r.MessageTemplate,
			Exception = r.Exception,
			TraceId = traceIdObj?.ToString(),
			SpanId = spanIdObj?.ToString(),
			EventId = eventIdObj is int ei ? ei : eventIdObj is long el ? (int)el : null,
			Properties = props,
		};
	}

	static Dictionary<string, object?> ParseProperties(string json)
	{
		if (string.IsNullOrWhiteSpace(json) || json == "{}")
			return new();

		try
		{
			return JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
				   ?? new();
		}
		catch
		{
			return new();
		}
	}

	static string RenderMessage(string template, string fallback, Dictionary<string, object?> props)
	{
		if (string.IsNullOrEmpty(template))
			return HtmlEncoder.Default.Encode(fallback);

		return TemplatePlaceholder.Replace(template, match =>
		{
			var key = match.Groups[1].Value;
			if (props.TryGetValue(key, out var val) && val is not null)
				return $"<mark class=\"msg-sub\">{HtmlEncoder.Default.Encode(FormatPropertyValue(val))}</mark>";
			// Null-valued properties are dropped from PropertiesJson at capture, so a missing
			// key means a null argument — render it the way the stored Message does.
			return "<mark class=\"msg-sub\">(null)</mark>";
		});
	}

	public static string LevelLabel(int level) => level switch
	{
		0 => "Verbose",
		1 => "Debug",
		2 => "Information",
		3 => "Warning",
		4 => "Error",
		5 => "Fatal",
		_ => "Unknown",
	};

	public static string LevelBadge(int level) => level switch
	{
		0 or 1 => "badge-ghost",
		2 => "badge-info",
		3 => "badge-warning",
		4 or 5 => "badge-error",
		_ => "badge-ghost",
	};

	public static string KqlString(string? s)
	{
		if (string.IsNullOrEmpty(s))
			return "\"\"";
		var escaped = s.Replace(@"\", @"\\").Replace("\"", "\\\"");
		return $"\"{escaped}\"";
	}

	public static string KqlDatetime(DateTime dt) =>
		$"datetime({dt:yyyy-MM-ddTHH:mm:ss.fffZ})";

	public static string IsoUtc(DateTime dt) =>
		dt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		// Default encoder escapes every non-ASCII char (Cyrillic -> \uXXXX), making the
		// copied event JSON unreadable. Use the shared relaxed encoder so human text stays
		// as-is while HTML-sensitive chars stay escaped (safe inside the data-copy attribute).
		Encoder = PetBox.Core.Json.PetBoxJsonEncoder.Relaxed,
	};

	public string ToJson()
	{
		var obj = new Dictionary<string, object?>
		{
			["Timestamp"] = IsoUtc(Timestamp),
			["Level"] = LevelName,
			["Message"] = Message,
		};
		if (!string.IsNullOrEmpty(MessageTemplate) && MessageTemplate != Message)
			obj["MessageTemplate"] = MessageTemplate;
		if (!string.IsNullOrEmpty(Exception))
			obj["Exception"] = Exception;
		if (!string.IsNullOrEmpty(TraceId))
			obj["TraceId"] = TraceId;
		if (!string.IsNullOrEmpty(SpanId))
			obj["SpanId"] = SpanId;
		if (EventId is { } eid)
			obj["EventId"] = eid;
		foreach (var (k, v) in Properties)
		{
			if (k is "TraceId" or "SpanId" or "EventId")
				continue;
			obj[k] = v;
		}
		return JsonSerializer.Serialize(obj, JsonOptions);
	}

	public static (string Display, string? KqlLiteral) PropertyForDisplay(object? value) => value switch
	{
		null => ("null", "null"),
		string s => (s, KqlString(s)),
		bool b => (b ? "true" : "false", b ? "true" : "false"),
		int or long or double or float or decimal => (FormatNumber(value), FormatNumber(value)),
		_ => (value.ToString() ?? "?", KqlString(value.ToString())),
	};

	static string FormatNumber(object value) =>
		value switch
		{
			double d => d.ToString("G", CultureInfo.InvariantCulture),
			float f => f.ToString("G", CultureInfo.InvariantCulture),
			decimal m => m.ToString("G", CultureInfo.InvariantCulture),
			_ => value.ToString() ?? "0",
		};

	static string FormatPropertyValue(object value) =>
		value switch
		{
			null => "null",
			string s => s,
			bool b => b ? "true" : "false",
			_ => FormatNumber(value),
		};
}

using System.Collections.Immutable;
using System.Text.Json;

namespace YobaBox.Log.Core.Models;

public sealed record LogEntryCandidate
{
	public string ServiceKey { get; init; } = string.Empty;
	public DateTime Timestamp { get; init; }
	public LogLevel Level { get; init; }
	public string Message { get; init; } = string.Empty;
	public string MessageTemplate { get; init; } = string.Empty;
	public string? Exception { get; init; }
	public string Properties { get; init; } = "{}";

	public LogEntry ToEntry(long id) => new()
	{
		Id = id,
		ServiceKey = ServiceKey,
		Timestamp = Timestamp,
		Level = Level,
		Message = Message,
		MessageTemplate = MessageTemplate,
		Exception = Exception,
		Properties = Properties,
	};
}

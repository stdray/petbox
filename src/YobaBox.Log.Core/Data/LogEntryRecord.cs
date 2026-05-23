using LinqToDB.Mapping;

namespace YobaBox.Log.Core.Data;

[Table("LogEntries")]
public sealed class LogEntryRecord
{
	[PrimaryKey, Identity] public long Id { get; set; }

	[Column, NotNull] public string ServiceKey { get; set; } = string.Empty;

	[Column, NotNull] public long TimestampMs { get; set; }

	[Column, NotNull] public int Level { get; set; }

	[Column, NotNull] public string Message { get; set; } = string.Empty;

	[Column, NotNull] public string MessageTemplate { get; set; } = string.Empty;

	[Column, Nullable] public string? Exception { get; set; }

	[Column, NotNull] public string PropertiesJson { get; set; } = "{}";

	[Column, NotNull] public long TemplateHash { get; set; }

	public static LogEntryRecord FromCandidate(Models.LogEntryCandidate c, long templateHash) => new()
	{
		ServiceKey = c.ServiceKey,
		TimestampMs = new DateTimeOffset(c.Timestamp).ToUnixTimeMilliseconds(),
		Level = (int)c.Level,
		Message = c.Message,
		MessageTemplate = c.MessageTemplate,
		Exception = c.Exception,
		PropertiesJson = c.Properties,
		TemplateHash = templateHash,
	};

	public Models.LogEntry ToEntry() => new()
	{
		Id = Id,
		ServiceKey = ServiceKey,
		Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs).UtcDateTime,
		Level = (Models.LogLevel)Level,
		Message = Message,
		MessageTemplate = MessageTemplate,
		Exception = Exception,
		Properties = PropertiesJson,
	};

	public static long ComputeTemplateHash(string template)
	{
		if (string.IsNullOrEmpty(template))
			return 0;
		unchecked
		{
			var hash = 0xcbf29ce484222325UL;
			foreach (var c in template)
				hash = (hash ^ (byte)c) * 0x100000001b3;
			return (long)hash;
		}
	}
}

using YobaBox.Log.Core.Data;

namespace YobaBox.Web.Pages.Logs;

public sealed record LogEntryViewModel(
	long Id,
	string ServiceKey,
	DateTime Timestamp,
	int Level,
	string LevelName,
	string Message,
	string MessageTemplate,
	string? Exception)
{
	public static LogEntryViewModel FromRecord(LogEntryRecord r) => new(
		r.Id,
		r.ServiceKey,
		DateTimeOffset.FromUnixTimeMilliseconds(r.TimestampMs).UtcDateTime,
		r.Level,
		LevelLabel(r.Level),
		r.Message,
		r.MessageTemplate,
		r.Exception);

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
}

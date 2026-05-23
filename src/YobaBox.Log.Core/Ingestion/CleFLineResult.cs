namespace YobaBox.Log.Core.Ingestion;

public enum CleFErrorKind { MalformedJson, MissingTimestamp, InvalidTimestamp, InvalidLevel }

public sealed record CleFParseError(CleFErrorKind Kind, string Message);

public sealed record CleFLineResult
{
	public int LineNumber { get; init; }
	public Models.LogEntryCandidate? Event { get; init; }
	public CleFParseError? Error { get; init; }
	public bool IsSuccess => Event is not null;

	public static CleFLineResult Success(int line, Models.LogEntryCandidate e) => new()
	{
		LineNumber = line,
		Event = e,
	};

	public static CleFLineResult Failure(int line, CleFErrorKind kind, string message) => new()
	{
		LineNumber = line,
		Error = new CleFParseError(kind, message),
	};
}

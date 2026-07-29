namespace PetBox.Log.Core.Ingestion;

public enum CleFErrorKind { MalformedJson, MissingTimestamp, InvalidTimestamp, InvalidLevel }

public sealed record CleFParseError(CleFErrorKind Kind, string Message);

public sealed record CleFLineResult
{
	public int LineNumber { get; private init; }
	public Models.LogEntryCandidate? Event { get; private init; }
	public CleFParseError? Error { get; private init; }
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

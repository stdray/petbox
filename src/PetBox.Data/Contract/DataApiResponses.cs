namespace PetBox.Data.Contract;

// SQLite execution error surfaced to the caller: the raw driver message plus the
// numeric SQLite result code (https://www.sqlite.org/rescode.html).
public sealed record SqliteErrorResponse(string Error, int Code);

// 400 when a migration script fails to apply: the failure reason and the hash of
// the offending (normalized) SQL.
public sealed record SchemaFailedResponse(string? Error, string Hash);

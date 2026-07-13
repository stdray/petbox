namespace PetBox.Data.Contract;

// One DataDb of a project, as the catalog hands it out.
public sealed record DataDbInfo(
	string Name, string? Description, long MaxPageCount, DateTime CreatedAt, DateTime UpdatedAt);

// A table of a DataDb and its columns — what db_describe renders.
public sealed record DataDbTableInfo(string Name, IReadOnlyList<DataDbColumnInfo> Columns);
public sealed record DataDbColumnInfo(string Name, string Type, bool NotNull, bool PrimaryKey);

// The outcome of a catalog write. Refused carries a reason; Conflict is the already-taken
// (project, name) slot; NotFound is a DataDb (or project) that is not there.
public abstract record DataDbChangeResult
{
	DataDbChangeResult() { }

	public sealed record Created(DataDbInfo Db) : DataDbChangeResult;
	public sealed record Deleted : DataDbChangeResult;
	public sealed record NotFound : DataDbChangeResult;
	public sealed record Conflict(string Reason) : DataDbChangeResult;
	public sealed record Refused(string Reason) : DataDbChangeResult;
}

// THE catalog of per-project DataDbs: the DataDbs rows in core.db plus the on-disk file they own.
// The row and the file are created (and deleted) together, in one place, because they are one thing —
// a row without a file is a DataDb that cannot be opened, a file without a row is an orphan the
// cleanup service has to sweep. `projectKey` is part of the ADDRESS of every method, never a filter
// applied afterwards: a name is unique only within its project, so a call naming another project's
// DataDb simply finds nothing.
//
// The database is visible only in the service layer (AGENTS.md) — the db_* MCP tools ask this, they
// do not open core.db, and db_describe's schema introspection lives here rather than in the tool.
public interface IDataDbCatalog
{
	Task<IReadOnlyList<DataDbInfo>> ListAsync(string projectKey, CancellationToken ct = default);

	Task<DataDbInfo?> GetAsync(string projectKey, string name, CancellationToken ct = default);

	Task<DataDbChangeResult> CreateAsync(
		string projectKey, string name, string? description, long? maxPageCount, CancellationToken ct = default);

	Task<DataDbChangeResult> DeleteAsync(string projectKey, string name, CancellationToken ct = default);

	// The tables + columns of one DataDb. NotFound (as a null) when the DataDb is not this project's.
	Task<IReadOnlyList<DataDbTableInfo>?> DescribeAsync(
		string projectKey, string name, CancellationToken ct = default);
}

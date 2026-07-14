using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Data;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;

namespace PetBox.Tests.Tasks;

// A counting IScopedDbFactory<TasksDb> over a real per-project tasks file — the per-project-db
// counterpart of Web's CountingCoreDbFactory (which only covers core.db). Counts connection OPENS
// (NewEnsuredConnection/GetDb calls — one per `using var ctx = _factory.NewEnsuredConnection(...)`
// a store/service makes) and SQL statements executed (linq2db tracing, BeforeExecute — one per
// command). This is the instrument board-page-cost's regression tests use to prove "queries are a
// handful, not hundreds" with a NUMBER, not a timing guess: RelationStore.ListAsync used to open
// its OWN connection per node per direction (~2 x board size), so Opens scaled linearly with board
// size before the fix and stays flat after it.
public sealed class CountingTasksDbFactory : IScopedDbFactory<TasksDb>
{
	readonly ScopedDbFactory<TasksDb> _inner;
	int _opens;
	int _statements;

	public CountingTasksDbFactory(string baseDir) =>
		_inner = new ScopedDbFactory<TasksDb>(baseDir, Scope.Project,
			c => new TasksDb(new DataOptions<TasksDb>(TasksDb.CreateOptions(c).Options.UseTracing(
				System.Diagnostics.TraceLevel.Info,
				info =>
				{
					if (info.TraceInfoStep == TraceInfoStep.BeforeExecute)
						Interlocked.Increment(ref _statements);
				}))),
			TasksSchema.Ensure);

	public Scope Scope => _inner.Scope;
	public string BaseDir => _inner.BaseDir;
	public int Opens => Volatile.Read(ref _opens);
	public int Statements => Volatile.Read(ref _statements);

	public TasksDb GetDb(string scopeKey, string? name = null)
	{
		Interlocked.Increment(ref _opens);
		return _inner.GetDb(scopeKey, name);
	}

	public TasksDb NewEnsuredConnection(string scopeKey, string? name = null)
	{
		Interlocked.Increment(ref _opens);
		return _inner.NewEnsuredConnection(scopeKey, name);
	}

	public ValueTask EvictAsync(string scopeKey, string? name = null) => _inner.EvictAsync(scopeKey, name);

	public ValueTask DisposeAsync() => _inner.DisposeAsync();

	public void Reset()
	{
		Volatile.Write(ref _opens, 0);
		Volatile.Write(ref _statements, 0);
	}
}

using System.Diagnostics;
using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Observability;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;
using PetBox.Web.Observability;

namespace PetBox.Tests.Observability;

// Self-tracing (spec: self-tracing): service-layer writes emit operation-boundary spans
// so a request trace decomposes in the waterfall instead of being a single AspNetCore
// span. Listeners subscribe per-source, mirroring what AddSource does in Program.cs.
// The sources are process-global statics, so under parallel classes the listener also
// sees spans from other tests' hosts — every assertion pins spans to THIS test via the
// unique project key (or span identity), never by operation name alone.
public sealed class SelfTracingTests : IDisposable
{
	readonly string _proj = "proj-" + Guid.NewGuid().ToString("N")[..8];
	readonly string _dir;
	readonly PetBoxDb _db;

	public SelfTracingTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-selftrace-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = _proj, WorkspaceKey = "ws", Name = "P", Description = "" });
	}

	public void Dispose()
	{
		_db.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}

	static (ActivityListener Listener, List<Activity> Started) Listen(string sourceName)
	{
		var started = new List<Activity>();
		var listener = new ActivityListener
		{
			ShouldListenTo = s => s.Name == sourceName,
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
			ActivityStarted = a => { lock (started) started.Add(a); },
		};
		ActivitySource.AddActivityListener(listener);
		return (listener, started);
	}

	// Parallel tests may still be appending when we start asserting — enumerate a copy.
	static Activity[] Snapshot(List<Activity> started)
	{
		lock (started) return started.ToArray();
	}

	[Fact]
	public async Task Tasks_upsert_emits_operation_span_with_segment_children()
	{
		var factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		var tasks = new TasksService(new TaskBoardStore(_db, factory), new RelationStore(factory),
			new TagStore(factory), new CommentService(factory));

		var (listener, started) = Listen(PetBoxActivitySources.TasksSourceName);
		using (listener)
		{
			var r = await tasks.UpsertAsync(_proj, "b", new[] { new NodePatch { Key = "n1", Title = "T", Body = "x" } });
			Assert.True(r.Result.Applied);
		}

		var snap = Snapshot(started);
		var op = Assert.Single(snap, a => a.OperationName == "tasks.upsert" && Equals(a.GetTagItem("petbox.project"), _proj));
		Assert.Equal("b", op.GetTagItem("petbox.board"));
		var temporal = Assert.Single(snap, a => a.OperationName == "tasks.upsert.temporal" && a.ParentSpanId == op.SpanId);
		Assert.Equal(op.SpanId, temporal.ParentSpanId);
		// The applied write also runs the links and fts-tags segments under the same parent.
		Assert.Contains(snap, a => a.OperationName == "tasks.upsert.links" && a.ParentSpanId == op.SpanId);
		Assert.Contains(snap, a => a.OperationName == "tasks.upsert.fts-tags" && a.ParentSpanId == op.SpanId);
	}

	[Fact]
	public async Task Memory_upsert_emits_operation_span()
	{
		var factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		var memory = new MemoryService(new MemoryStore(_db, factory));

		var (listener, started) = Listen(PetBoxActivitySources.MemorySourceName);
		using (listener)
		{
			var r = await memory.UpsertAsync(_proj, "notes",
				new[] { new MemoryEntryInput { Key = "k1", Type = "Project", Body = "x" } },
				Array.Empty<MemoryDelete>());
			Assert.True(r.Result.Applied);
		}

		var op = Assert.Single(Snapshot(started), a => a.OperationName == "memory.upsert" && Equals(a.GetTagItem("petbox.project"), _proj));
		Assert.Equal("notes", op.GetTagItem("petbox.store"));
		Assert.Equal(1, op.GetTagItem("petbox.upsert_count"));
	}

	[Fact]
	public void Mcp_tool_span_is_named_by_tool()
	{
		var (listener, started) = Listen(PetBoxActivitySources.McpSourceName);
		Activity? span;
		using (listener)
		using (span = McpTracingFilter.StartToolSpan("tasks.upsert"))
			Assert.NotNull(span);

		// Other tests' MCP hosts emit identically-named spans — pin by identity.
		var op = Assert.Single(Snapshot(started), a => ReferenceEquals(a, span));
		Assert.Equal("mcp.tool tasks.upsert", op.OperationName);
		Assert.Equal("tasks.upsert", op.GetTagItem("petbox.tool"));
	}

	[Theory]
	[InlineData("https://petbox.3po.su/v1/traces", false)]
	[InlineData("https://petbox.3po.su/v1/traces/proj/petbox", false)]
	[InlineData("https://petbox.3po.su/v1/logs", false)]
	[InlineData("https://collector.example/v1/traces", false)]
	[InlineData("https://api.deepseek.com/v1/embeddings", true)]
	[InlineData("http://localhost:1234/v1/chat/completions", true)]
	public void Self_telemetry_export_is_not_traced(string uri, bool traced) =>
		Assert.Equal(traced, SelfTelemetryFilter.ShouldTrace(new Uri(uri)));
}

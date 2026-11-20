using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;
using PetBox.Web.Tasks;

namespace PetBox.Tests.Tasks;

// work observations-dedup-bypasses-type-validation: the `observations` board's dedup guard runs
// BEFORE tasks.UpsertAsync, and a hit REPLACES the input array — a fully-deduped batch returns
// `applied: true, inserted: 0` without the write path ever resolving the node's `type`, which is
// the only other place a type is judged (TasksService.ApplyWorkflow -> WorkflowEngine.Validate).
// So a create carrying a type that belongs to some OTHER board (`issue` here) was silently
// swallowed: nothing inserted, nothing deduped, no error. These pin the gate that closed it, and
// pin the behaviour it must NOT change: a valid (or untyped) create still dedups.
public sealed class ObservationsUpsertTypeGateTests : IDisposable
{
	const string Proj = "proj";
	const string Title = "Flaky retry in the payment webhook";
	const string Body = "Saw a 3rd retry loop on the payment webhook under load.";

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;
	readonly ObservationSignalStore _signals;
	readonly IHttpContextAccessor _http;

	public ObservationsUpsertTypeGateTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-observation-type-gate-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
		_signals = new ObservationSignalStore(_factory);
		_tasks = new TasksService(new TaskBoardStore(_db.Factory(), _factory), new RelationStore(_factory),
			new TagStore(_factory), new CommentService(_factory), observationSignals: _signals);
		_http = Http(_tasks);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	// The tool resolves IObservationDedupService/IUnlinkedIntakeTwinWarnService from the request's
	// own DI scope (see TasksTools.UpsertAsync), so the accessor MUST carry one for the dedup guard
	// — and therefore for the gate in front of it — to be on the code path at all.
	static IHttpContextAccessor Http(ITasksService tasks)
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", "tasks:write")], "test");
		var services = new ServiceCollection()
			.AddSingleton<IProjectCatalog>(TestProjectCatalog.Instance)
			.AddSingleton<IObservationDedupService>(new ObservationDedupService(tasks, llm: null))
			.BuildServiceProvider();
		return new HttpContextAccessor
		{
			HttpContext = new DefaultHttpContext { RequestServices = services, User = new ClaimsPrincipal(id) },
		};
	}

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Tasks"] = "true",
		}).Build());

	// An existing observation the mistyped write's text WOULD dedup onto — the exact shape that
	// used to swallow the write. Returns its NodeId.
	async Task<(string NodeId, long Version)> SeedExistingObservationAsync()
	{
		await _tasks.CreateBoardAsync(Proj, SystemBoards.Observations, SystemBoards.ObservationKind, "obs", null,
			methodologyInstance: TaskBoardMeta.UtilityWorld);
		var created = await _tasks.UpsertAsync(Proj, SystemBoards.Observations,
			[new NodePatch { Key = "obs-1", Version = 0, Type = SystemBoards.ObservationKind, Title = Title, Body = Body }]);
		created.Result.Applied.Should().BeTrue();
		var nodeId = created.Result.Added.Should().ContainSingle().Subject.NodeId;
		await _tasks.RecordObservationFirstSeenAsync(Proj, nodeId);
		return (nodeId, (await _tasks.GetAsync(Proj, SystemBoards.Observations)).CurrentVersion);
	}

	[Fact]
	public async Task WrongType_IsRefusedNamingTheValidType_AndNothingIsInsertedOrDeduped()
	{
		var (nodeId, versionBefore) = await SeedExistingObservationAsync();

		var act = async () => await TasksTools.UpsertAsync(_http, Flags(), _tasks, Proj, SystemBoards.Observations,
			[new TaskNodeInput { Key = "obs-2", Version = 0, Type = "issue", Title = Title, Body = Body }]);

		var ex = await act.Should().ThrowAsync<ArgumentException>();
		ex.Which.Message.Should().Contain("needs a known type")
			.And.Contain(SystemBoards.ObservationKind, "the error must name the kind")
			.And.Contain("issue", "the error must quote what was sent");
		ex.Which.RejectedNode().Should().Be("obs-2", "the refusal indicts the node, not the whole call shape");

		// The board is UNTOUCHED: the mistyped node was not created...
		var after = await _tasks.GetAsync(Proj, SystemBoards.Observations);
		after.Nodes.Should().ContainSingle().Which.Key.Should().Be("obs-1");
		after.CurrentVersion.Should().Be(versionBefore);

		// ...and, the half the gate exists for, it was not DEDUPED either — a mistyped write must
		// never bump the recurrence of the observation it would otherwise have folded onto.
		(await _signals.GetAsync(Proj, nodeId))!.RecurrenceCount.Should().Be(1);
	}

	[Theory]
	[InlineData(SystemBoards.ObservationKind)]
	[InlineData(null)]
	public async Task ValidOrUntypedCreate_StillDedupsOntoTheExistingObservation(string? type)
	{
		var (nodeId, versionBefore) = await SeedExistingObservationAsync();

		// Near-duplicate text (case/punctuation folded) — a normal, VALID observation write. The
		// type gate sits in front of dedup now; it must pass this through untouched.
		var result = await TasksTools.UpsertAsync(_http, Flags(), _tasks, Proj, SystemBoards.Observations,
			[new TaskNodeInput { Key = "obs-2", Version = 0, Type = type, Title = "flaky retry in the payment webhook", Body = "saw a 3rd retry loop on the payment webhook under load" }]);

		result.Applied.Should().BeTrue();
		result.Inserted.Should().Be(0, "the repeat sighting must not become a second node");
		result.Added.Should().BeEmpty();
		var deduped = result.Deduped.Should().ContainSingle().Subject;
		deduped.RequestedKey.Should().Be("obs-2");
		deduped.ExistingKey.Should().Be("obs-1");
		deduped.ExistingNodeId.Should().Be(nodeId);
		deduped.RecurrenceCount.Should().Be(2);

		var after = await _tasks.GetAsync(Proj, SystemBoards.Observations);
		after.Nodes.Should().ContainSingle().Which.Key.Should().Be("obs-1");
		after.CurrentVersion.Should().Be(versionBefore, "a dedup hit grows the recurrence signal, it mints no node revision");
	}

	// The other half of "do not relax dedup for a valid type": a genuinely NEW valid observation
	// still goes through the ordinary create path (the gate must not turn every create into a
	// dedup-only pass, and an empty type must still resolve to the kind's default).
	[Fact]
	public async Task GenuinelyNewValidObservation_IsStillInserted()
	{
		await SeedExistingObservationAsync();

		var result = await TasksTools.UpsertAsync(_http, Flags(), _tasks, Proj, SystemBoards.Observations,
			[new TaskNodeInput { Key = "obs-9", Version = 0, Title = "Unrelated: stale cache header on /health", Body = "Completely different signal." }]);

		result.Applied.Should().BeTrue();
		result.Inserted.Should().Be(1);
		result.Deduped.Should().BeNullOrEmpty();
		result.Added.Should().ContainSingle().Which.Type.Should().Be(SystemBoards.ObservationKind,
			"an untyped create materializes the kind's default type");
	}
}

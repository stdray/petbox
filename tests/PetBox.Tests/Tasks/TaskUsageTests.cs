using System.Diagnostics;
using System.Security.Claims;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Tasks;

// The usage layer for TASK NODES (spec: task-usage-layer-with-declared-role).
//
// What it must prove, in the order the spec asks for it:
//   1. a delivery is COUNTED, and a non-delivery is NOT — a counter that only ever goes up is
//      not a measurement;
//   2. cost and fit are recorded SEPARATELY, so "expensive and off-target" and "cheap and
//      dead-on" stay distinguishable;
//   3. deliberate and machine traffic land in DIFFERENT counters, from the first migration;
//   4. every entity declares its ROLE, the role rides the delivery event, and an INDEX board
//      with a dead tail is not readable as waste (the `session-digests` regression);
//   5. a board that predates the role column reads `corpus` — not null, not an exception.
public sealed class TaskUsageTests : IDisposable
{
	const string Proj = "proj";

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;
	readonly TaskUsageRecorder _recorder;
	readonly TaskUsageReader _reader;

	public TaskUsageTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-taskusage-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
		var boards = new TaskBoardStore(_db.Factory(), _factory);
		_tasks = new TasksService(boards, new RelationStore(_factory), new TagStore(_factory), new CommentService(_factory));
		_recorder = new TaskUsageRecorder(_factory, _db.Factory());
		_reader = new TaskUsageReader(boards);
	}

	public void Dispose()
	{
		_recorder.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static IHttpContextAccessor Http()
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", "tasks:read,tasks:write")], "test");
		var ctx = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) };
		ctx.Request.Scheme = "https";
		ctx.Request.Host = new HostString("box.test");
		return new HttpContextAccessor { HttpContext = ctx };
	}

	static FeatureFlags Flags()
	{
		var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Tasks"] = "true",
		}).Build();
		return new FeatureFlags(cfg);
	}

	async Task Seed(string board, string? declaredRole, params string[] keys)
	{
		await TasksTools.BoardCreateAsync(Http(), Flags(), _tasks, Proj, board, declaredRole: declaredRole);
		var json = "[" + string.Join(",", keys.Select(k =>
			$$"""{"key":"{{k}}","status":"Todo","title":"{{k}}","body":"тело узла {{k}} про телеметрию доставки"}""")) + "]";
		await TasksTools.UpsertAsync(Http(), Flags(), _tasks, Proj, board, McpInputs.NodesJson(json));
	}

	async Task<Dictionary<string, NodeUsageView>> Usage(string board)
	{
		await _recorder.FlushAsync();
		return (await _reader.GetUsageAsync(Proj, board)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
	}

	async Task<string> NodeId(string board, string key) =>
		(await TasksTools.NodeGetAsync(Http(), Flags(), _tasks, NoopTaskUsage.Recorder, Proj, board, key)).Nodes.Single().Node.NodeId;

	// 1. BOTH DIRECTIONS. A listing that DELIVERED a node counts an impression for it; a node the
	// same board holds but the read did not deliver stays at zero. A counter that rises for
	// everything measures nothing, so the negative half is the half that makes it a measurement.
	// tasks_node_get is the ENGAGEMENT (the mirror of memory_get) and bumps Opened, not Surfaced.
	[Fact]
	public async Task Delivered_Counts_NotDelivered_DoesNot_AndNodeGetIsTheOpen()
	{
		await Seed("b", null, "alpha", "beta");
		var alpha = await NodeId("b", "alpha");
		var beta = await NodeId("b", "beta");

		// A listing narrowed to ONE node: only alpha reaches the caller.
		await TasksTools.SearchAsync(Http(), Flags(), _tasks, _recorder, _reader, Proj, board: "b", nodes: ["alpha"]);

		var afterSearch = await Usage("b");
		afterSearch[alpha].Surfaced.Should().Be(1);
		afterSearch[alpha].Opened.Should().Be(0); // an impression is not an engagement
												  // beta was never delivered — it must have NO usage row at all, not a row of zeroes.
		afterSearch.ContainsKey(beta).Should().BeFalse();

		// The addressed read is the OPEN.
		await TasksTools.NodeGetAsync(Http(), Flags(), _tasks, _recorder, Proj, "b", "alpha");
		var afterOpen = await Usage("b");
		afterOpen[alpha].Opened.Should().Be(1);
		afterOpen[alpha].Surfaced.Should().Be(1); // an open does NOT inflate the impression count
		(await Usage("b")).ContainsKey(beta).Should().BeFalse();
	}

	// 3. THE HONEST SPLIT, IN THE FIRST MIGRATION. A machine pull is real context cost but is not
	// evidence that anything was worth reading. It must bump Surfaced and leave Deliberate alone —
	// and the board aggregate must show the two costs side by side rather than one replacing the
	// other (a board serviced mostly by automation must not read as dead).
	[Fact]
	public async Task DeliberateAndMachine_LandInDifferentCounters()
	{
		await Seed("b", null, "alpha");
		var alpha = await NodeId("b", "alpha");

		await TasksTools.SearchAsync(Http(), Flags(), _tasks, _recorder, _reader, Proj, board: "b", usageSource: "machine");
		var afterMachine = await Usage("b");
		afterMachine[alpha].Surfaced.Should().Be(1);
		afterMachine[alpha].Deliberate.Should().Be(0); // the whole point of the split

		await TasksTools.SearchAsync(Http(), Flags(), _tasks, _recorder, _reader, Proj, board: "b", usageSource: "deliberate");
		var afterDeliberate = await Usage("b");
		afterDeliberate[alpha].Surfaced.Should().Be(2);
		afterDeliberate[alpha].Deliberate.Should().Be(1);

		// …and the two costs stay separately readable on the aggregate.
		var agg = await _reader.GetBoardUsageAsync(Proj, "b");
		agg.Cost.MachineDeliveries.Should().Be(1);
		agg.Cost.DeliberateDeliveries.Should().Be(1);
		agg.Cost.Deliveries.Should().Be(2); // the combined total is untouched by the split
		agg.DeliberatelySurfacedAtLeastOnce.Should().Be(1);
	}

	// An unlabelled source is REFUSED, never folded into "deliberate": silently counting unknown
	// traffic as intentional inflates the one number that is supposed to mean "somebody wanted this".
	[Fact]
	public async Task UnknownUsageSource_IsRejected_NotFoldedIntoDeliberate()
	{
		await Seed("b", null, "alpha");
		var bad = () => TasksTools.SearchAsync(Http(), Flags(), _tasks, _recorder, _reader, Proj, board: "b", usageSource: "auto");
		(await bad.Should().ThrowAsync<ArgumentException>()).WithMessage("*deliberate*machine*");

		var badGet = () => TasksTools.NodeGetAsync(Http(), Flags(), _tasks, _recorder, Proj, "b", "alpha", usageSource: "auto");
		await badGet.Should().ThrowAsync<ArgumentException>();
	}

	// 2. COST AND FIT, SEPARATELY AND RAW. The delivery event records what the row actually SENT
	// (post-bodyLen) against the node's full body, the row's whole wire price, and the fit — and a
	// LISTING, which runs no relevance leg, records cost with a NULL fit rather than inventing one.
	[Fact]
	public async Task DeliveryEvents_RecordCostAndFit_Separately()
	{
		await Seed("b", null, "alpha");
		await TasksTools.SearchAsync(Http(), Flags(), _tasks, _recorder, _reader, Proj, board: "b", bodyLen: 5);
		await _recorder.FlushAsync();

		using var ctx = _factory.NewEnsuredConnection(Proj);
		var e = ctx.NodeDeliveries.Single();
		e.Tool.Should().Be("listing");
		e.DeliveredChars.Should().BeLessThan(e.BodyChars); // bodyLen:5 cut the body — cost is what SHIPPED
		e.RowChars.Should().BeGreaterThan(e.DeliveredChars); // the envelope is part of the honest price
		e.Rank.Should().Be(1);
		e.KRel.Should().BeNull(); // a listing ran no relevance leg: no fit, rather than a fake one
		e.UsageSource.Should().Be("deliberate");
	}

	// 4. THE ROLE RIDES THE EVENT. The declaration is on the BOARD and is stamped into every
	// delivery row, so the cost/fit numbers cannot be queried without the role that makes them
	// interpretable. A caller cannot supply the role and cannot forget it.
	[Fact]
	public async Task DeliveryEvents_CarryTheBoardsDeclaredRole()
	{
		await Seed("entry", "index", "gate");
		await Seed("corp", null, "answer");

		await TasksTools.SearchAsync(Http(), Flags(), _tasks, _recorder, _reader, Proj, board: "entry");
		await TasksTools.SearchAsync(Http(), Flags(), _tasks, _recorder, _reader, Proj, board: "corp");
		await _recorder.FlushAsync();

		using var ctx = _factory.NewEnsuredConnection(Proj);
		ctx.NodeDeliveries.Single(d => d.Board == "entry").DeclaredRole.Should().Be("index");
		ctx.NodeDeliveries.Single(d => d.Board == "corp").DeclaredRole.Should().Be("corpus");
	}

	// 4b. THE `session-digests` REGRESSION. An INDEX board whose nodes are surfaced and never
	// opened is doing its job: it exists to route the reader onward. The aggregate must carry
	// `index` NEXT TO the dead tail and the zero openedFraction, so the numbers cannot be read
	// without the expectation that frames them — and the identical corpus board must read as the
	// waste it is. Same numbers, opposite meanings; the declaration is the whole difference.
	[Fact]
	public async Task IndexBoard_WithDeadTail_IsNotReadableAsWaste()
	{
		await Seed("entry", "index", "gate", "unused-gate");
		await Seed("corp", "corpus", "answer", "unused-answer");
		await TasksTools.SearchAsync(Http(), Flags(), _tasks, _recorder, _reader, Proj, board: "entry", nodes: ["gate"]);
		await TasksTools.SearchAsync(Http(), Flags(), _tasks, _recorder, _reader, Proj, board: "corp", nodes: ["answer"]);
		await _recorder.FlushAsync();

		var index = await _reader.GetBoardUsageAsync(Proj, "entry");
		var corpus = await _reader.GetBoardUsageAsync(Proj, "corp");

		// The measured numbers are IDENTICAL…
		index.TotalNodes.Should().Be(corpus.TotalNodes);
		index.SurfacedAtLeastOnce.Should().Be(corpus.SurfacedAtLeastOnce);
		index.OpenedFraction.Should().Be(0).And.Be(corpus.OpenedFraction);
		index.DeadTail.Count.Should().Be(1).And.Be(corpus.DeadTail.Count);
		index.DeadTail.TopKeys.Should().Equal("unused-gate");

		// …and the aggregate says which expectation each set of numbers is to be read against.
		index.DeclaredRole.Should().Be("index");
		corpus.DeclaredRole.Should().Be("corpus");
	}

	// 5. THE DEFAULT. A board row written before the role column existed (M051 backfills it, but
	// so does a writer that never names one) must read `corpus` — never null, never a throw, on
	// BOTH the board listing and the usage aggregate.
	[Fact]
	public async Task BoardWithoutDeclaredRole_ReadsCorpus()
	{
		await Seed("legacy", null, "alpha");
		// Simulate a pre-M051 row as faithfully as the live schema allows: blank out the column
		// underneath the reader. NOT NULL is the column's own contract, so the value a legacy row
		// can actually carry is the empty string — and an unknown value must still read `corpus`.
		((DataConnection)_db).Execute("UPDATE TaskBoards SET DeclaredRole = '' WHERE ProjectKey = @p AND Name = 'legacy'",
			new DataParameter("p", Proj));

		var listed = (await TasksTools.BoardListAsync(Http(), Flags(), _tasks, _reader, _recorder, Proj)).Boards
			.Single(b => b.Name == "legacy");
		listed.DeclaredRole.Should().Be("corpus");

		var agg = await _reader.GetBoardUsageAsync(Proj, "legacy");
		agg.DeclaredRole.Should().Be("corpus");
	}

	// A typo in the DECLARATION is refused out loud. Filing it as `corpus` would apply the wrong
	// expectation to every number the board ever produces — silently, which is the exact failure
	// this field exists to end.
	[Fact]
	public async Task UnknownDeclaredRole_IsRejected()
	{
		var bad = () => TasksTools.BoardCreateAsync(Http(), Flags(), _tasks, Proj, "oops", declaredRole: "entrypoint");
		(await bad.Should().ThrowAsync<ArgumentException>()).WithMessage("*index*corpus*");
	}

	// THE READ SURFACE (spec acceptance: "usage-метрики задач видны на читающей поверхности с
	// разбивкой по объявленной роли"). Per-row counters ride tasks_search under includeUsage;
	// the board aggregate + the role ride tasks_board_list. Off by default, so no existing caller
	// pays for them.
	[Fact]
	public async Task ReadSurfaces_ExposeUsage_OnlyUnderIncludeUsage()
	{
		await Seed("entry", "index", "alpha");
		await TasksTools.SearchAsync(Http(), Flags(), _tasks, _recorder, _reader, Proj, board: "entry");
		await _recorder.FlushAsync();

		var plain = (await TasksTools.SearchAsync(Http(), Flags(), _tasks, _recorder, _reader, Proj, board: "entry")).Nodes.Single();
		plain.Surfaced.Should().BeNull(); // omitted on the wire without the opt-in

		var withUsage = (await TasksTools.SearchAsync(Http(), Flags(), _tasks, _recorder, _reader, Proj, board: "entry", includeUsage: true)).Nodes.Single();
		withUsage.Surfaced.Should().BeGreaterThan(0);
		withUsage.DeliveredChars.Should().BeGreaterThan(0); // cost, next to the counter, not instead of it

		var board = (await TasksTools.BoardListAsync(Http(), Flags(), _tasks, _reader, _recorder, Proj, includeUsage: true))
			.Boards.Single(b => b.Name == "entry");
		board.DeclaredRole.Should().Be("index");
		board.Usage.Should().NotBeNull();
		board.Usage!.TotalNodes.Should().Be(1);
		board.Usage.DeliveredChars.Should().BeGreaterThan(0);
		board.Usage.DroppedEvents.Should().Be(0); // nothing was lost, and the surface SAYS so

		var plainBoard = (await TasksTools.BoardListAsync(Http(), Flags(), _tasks, _reader, _recorder, Proj))
			.Boards.Single(b => b.Name == "entry");
		plainBoard.Usage.Should().BeNull();
		plainBoard.DeclaredRole.Should().Be("index"); // the ROLE rides every row, opt-in or not
	}

	// LOSS MUST BE VISIBLE. Memory's recorder drops on overflow in silence, which makes a low
	// counter and a lost counter indistinguishable. Here the drop is counted and readable, so a
	// number produced under overflow can be recognized as an undercount instead of trusted.
	[Fact]
	public async Task ChannelOverflow_IsCounted_NotSilent()
	{
		await using var recorder = new TaskUsageRecorder(_factory, _db.Factory());
		// Far past the bounded channel's 10k capacity, enqueued faster than any drain can keep up.
		var ids = Enumerable.Range(0, 60_000).Select(i => "n" + i).ToList();
		recorder.Surfaced(Proj, "b", ids);
		recorder.DroppedEvents.Should().BeGreaterThan(0);
	}

	// The two UsageSource words are a SHARED wire vocabulary with memory (a report that puts task
	// and memory telemetry side by side groups by the same two strings), and PetBox.Tasks cannot
	// reference PetBox.Memory to share the type. This test is what keeps the duplicated pair from
	// drifting into two different vocabularies.
	[Fact]
	public void UsageSourceVocabularyMatchesMemory()
	{
		NodeUsageSourceKind.Deliberate.Should().Be(PetBox.Memory.Contract.UsageSourceKind.Deliberate);
		NodeUsageSourceKind.Machine.Should().Be(PetBox.Memory.Contract.UsageSourceKind.Machine);
	}

	// THE HOT PATH, MEASURED. Recording must not put a database write in front of the answer: the
	// read path only ENQUEUES, and the drain writes later on its own thread. What the caller DOES
	// synchronously pay is exactly two things — one serialization per row to price it
	// (ResponseBudget.CostOf) and one channel write per row — so both are in the loop below;
	// measuring only the enqueue would quote a number the caller never actually sees.
	//
	// Note what this cost IS: a SECOND CostOf pass over each row, because the response budget
	// already ran one and does not hand its numbers back. That is a known, bounded duplicate, and
	// it is measured here rather than assumed away.
	[Fact]
	public async Task Recording_50Rows_CostsTheReadPathAlmostNothing()
	{
		await Seed("bench", null, "alpha");
		var page = (await TasksTools.SearchAsync(Http(), Flags(), _tasks, _recorder, _reader, Proj, board: "bench")).Nodes;
		var row = page.Single(); // a REAL wire row, not a hand-made stand-in: the price is its size

		await using var recorder = new TaskUsageRecorder(_factory, _db.Factory());
		var ids = Enumerable.Range(0, 50).Select(i => "node" + i).ToList();

		double Once()
		{
			var sw = Stopwatch.StartNew();
			var events = new List<TaskDeliveryEvent>(50);
			for (var i = 0; i < 50; i++)
				events.Add(new TaskDeliveryEvent("search", "bench", ids[i], "key" + i,
					240, 2000, PetBox.Core.Contract.ResponseBudget.CostOf(row), i + 1, 0.5, 0.9, null, "deliberate"));
			recorder.Surfaced(Proj, "bench", ids);
			recorder.Delivered(Proj, events);
			return sw.Elapsed.TotalMilliseconds;
		}

		Once(); // warm the JIT + the serializer's per-type metadata
		var runs = Enumerable.Range(0, 20).Select(_ => Once()).ToList();
		var median = runs.Order().ElementAt(runs.Count / 2);
		// Measured on this stand: 0.068 ms (median of 20) for a 50-row delivery, Debug build —
		// 50 CostOf serializations plus 100 channel writes. The bound is deliberately two
		// orders of magnitude above it — the assertion exists to catch a SYNCHRONOUS DB WRITE
		// sneaking onto the read path (which costs milliseconds per row), not to pin a benchmark
		// that would flake on a loaded CI box.
		median.Should().BeLessThan(20, $"recording 50 rows must not block the read path (measured {median:F3} ms)");
	}
}

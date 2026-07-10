using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Workflow;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Tasks;

// methodology-instance-rules-edit: live instance rules patch + declarative status/type
// migration (reuses MethodologyLiveMigration). Template edit must NOT mutate instances;
// unmapped stranded values reject the whole write.
public sealed class MethodologyInstanceRulesEditTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public MethodologyInstanceRulesEditTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-mirules-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_tasks = new TasksService(new TaskBoardStore(_db, _factory), new RelationStore(_db), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static IHttpContextAccessor Http(string scopes)
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", scopes)], "test");
		var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(id) };
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

	// support process: ticket|incident, New → Open → Resolved (parameterized for renames).
	static MethodologyDefinition SupportDef(string openStatus = "Open", string ticketType = "ticket") => new("support-process",
	[
		new MethodologyKindDef("support", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(
				[ticketType, "incident"],
				[
					new WorkflowStatus("New", "New", StatusKind.Open),
					new WorkflowStatus(openStatus, openStatus, StatusKind.Open),
					new WorkflowStatus("Resolved", "Resolved", StatusKind.TerminalOk),
				],
				[
					new MethodologyTransitionDef("New", openStatus),
					new MethodologyTransitionDef(openStatus, "Resolved"),
				]),
		]),
	]);

	async Task<(string Instance, string Board)> SeedSupportInstanceAsync()
	{
		await _tasks.UpsertMethodologyTemplateAsync(Proj, "support-tmpl", SupportDef(), version: 0);
		var ack = await _tasks.CreateMethodologyInstanceAsync(Proj, "help", "template", "support-tmpl");
		var board = ack.Boards.Single().Name;
		var up = await _tasks.UpsertAsync(Proj, board,
		[
			new NodePatch { Key = "t-new", Title = "Still new", Type = "ticket", Body = "x" },
			new NodePatch { Key = "t-open", Title = "In flight", Type = "ticket", Body = "x" },
		]);
		up.Result.Applied.Should().BeTrue();
		// Move t-open to Open (version 1 after create).
		var open = await _tasks.UpsertAsync(Proj, board,
		[
			new NodePatch { Key = "t-open", Version = 1, Status = "Open" },
		]);
		open.Result.Applied.Should().BeTrue();
		return ("help", board);
	}

	async Task<PlanNodeView> NodeOnBoard(string board, string key)
	{
		var detail = await _tasks.GetNodeOnBoardAsync(Proj, board, key);
		return detail.Node;
	}

	// ── rules get ────────────────────────────────────────────────────────────

	[Fact]
	public async Task RulesGet_ReturnsDocumentAndVersion()
	{
		await SeedSupportInstanceAsync();
		var rules = await _tasks.GetMethodologyInstanceRulesAsync(Proj, "help");
		rules.Should().NotBeNull();
		rules!.Name.Should().Be("help");
		rules.Closed.Should().BeFalse();
		rules.Definition.Name.Should().Be("support-process");
		rules.Definition.Kinds.Should().ContainSingle(k => k.Kind == "support");
		rules.Version.Should().BeGreaterThan(0);

		// MCP door.
		var http = Http("tasks:read");
		var mcp = await TasksTools.MethodologyRulesGetAsync(http, Flags(), _tasks, Proj, "help");
		mcp.Found.Should().BeTrue();
		mcp.Name.Should().Be("help");
		mcp.DefinitionName.Should().Be("support-process");
		mcp.Version.Should().Be(rules.Version);
		mcp.Kinds.Should().NotBeNull().And.ContainSingle(k => k.Kind == "support");

		var miss = await TasksTools.MethodologyRulesGetAsync(http, Flags(), _tasks, Proj, "nope");
		miss.Found.Should().BeFalse();
	}

	// ── unmapped reject ──────────────────────────────────────────────────────

	[Fact]
	public async Task StatusRename_WithoutMigration_Rejected_NothingWritten()
	{
		var (inst, board) = await SeedSupportInstanceAsync();
		var before = await _tasks.GetMethodologyInstanceRulesAsync(Proj, inst);
		var openBefore = await NodeOnBoard(board, "t-open");

		var act = () => _tasks.DefineMethodologyInstanceRulesAsync(Proj, inst, SupportDef(openStatus: "Active"), before!.Version);
		var ex = await act.Should().ThrowAsync<ArgumentException>();
		ex.Which.Message.Should().Contain("incompatible with live nodes");
		ex.Which.Message.Should().Contain(board);
		ex.Which.Message.Should().Contain("t-open");
		ex.Which.Message.Should().Contain("Open");
		ex.Which.Message.Should().Contain("migration");

		// Rules + node untouched.
		var after = await _tasks.GetMethodologyInstanceRulesAsync(Proj, inst);
		after.Should().NotBeNull();
		after!.Version.Should().Be(before!.Version);
		after.Definition.Kinds[0].Workflows[0].Statuses.Select(s => s.Slug)
			.Should().Contain("Open").And.NotContain("Active");
		var openAfter = await NodeOnBoard(board, "t-open");
		openAfter.Status.Should().Be("Open");
		openAfter.Version.Should().Be(openBefore.Version);
	}

	// ── migration map applies ────────────────────────────────────────────────

	[Fact]
	public async Task StatusRename_WithMigration_RewritesOnlyInvalidNodes()
	{
		var (inst, board) = await SeedSupportInstanceAsync();
		var before = await _tasks.GetMethodologyInstanceRulesAsync(Proj, inst);

		var ack = await _tasks.DefineMethodologyInstanceRulesAsync(
			Proj, inst, SupportDef(openStatus: "Active"), before!.Version,
			migration:
			[
				new MethodologyMigration("support", Types: [], Statuses: [new MethodologyValueMap("Open", "Active")]),
			]);
		ack.Changed.Should().BeTrue();
		ack.Migrated.Should().Be(1);
		ack.Version.Should().BeGreaterThan(before.Version);

		(await NodeOnBoard(board, "t-open")).Status.Should().Be("Active");
		var fresh = await NodeOnBoard(board, "t-new");
		fresh.Status.Should().Be("New");
		fresh.Version.Should().Be(1, "valid under the new resolution — never rewritten");
	}

	[Fact]
	public async Task TypeAndStatusRename_WithMigration_RewritesBoth()
	{
		var (inst, board) = await SeedSupportInstanceAsync();
		var before = await _tasks.GetMethodologyInstanceRulesAsync(Proj, inst);

		var ack = await _tasks.DefineMethodologyInstanceRulesAsync(
			Proj, inst, SupportDef(openStatus: "Active", ticketType: "request"), before!.Version,
			migration:
			[
				new MethodologyMigration("support",
					Types: [new MethodologyValueMap("ticket", "request")],
					Statuses: [new MethodologyValueMap("Open", "Active")]),
			]);
		ack.Migrated.Should().Be(2);

		var n = await NodeOnBoard(board, "t-new");
		n.Type.Should().Be("request");
		n.Status.Should().Be("New");
		var o = await NodeOnBoard(board, "t-open");
		o.Type.Should().Be("request");
		o.Status.Should().Be("Active");
	}

	// ── scope: only this instance's boards ───────────────────────────────────

	[Fact]
	public async Task RulesEdit_DoesNotTouchOtherInstanceBoards()
	{
		// Two instances from the same support template; only one gets a rules rename.
		await _tasks.UpsertMethodologyTemplateAsync(Proj, "support-tmpl", SupportDef(), version: 0);
		var a = await _tasks.CreateMethodologyInstanceAsync(Proj, "alpha", "template", "support-tmpl");
		var b = await _tasks.CreateMethodologyInstanceAsync(Proj, "beta", "template", "support-tmpl");
		var boardA = a.Boards.Single().Name;
		var boardB = b.Boards.Single().Name;

		await _tasks.UpsertAsync(Proj, boardA, [new NodePatch { Key = "a1", Title = "A", Type = "ticket", Body = "x" }]);
		await _tasks.UpsertAsync(Proj, boardB, [new NodePatch { Key = "b1", Title = "B", Type = "ticket", Body = "x" }]);
		await _tasks.UpsertAsync(Proj, boardA, [new NodePatch { Key = "a1", Version = 1, Status = "Open" }]);
		await _tasks.UpsertAsync(Proj, boardB, [new NodePatch { Key = "b1", Version = 1, Status = "Open" }]);

		var rulesA = await _tasks.GetMethodologyInstanceRulesAsync(Proj, "alpha");
		await _tasks.DefineMethodologyInstanceRulesAsync(
			Proj, "alpha", SupportDef(openStatus: "Active"), rulesA!.Version,
			migration: [new MethodologyMigration("support", [], [new MethodologyValueMap("Open", "Active")])]);

		(await NodeOnBoard(boardA, "a1")).Status.Should().Be("Active");
		(await NodeOnBoard(boardB, "b1")).Status.Should().Be("Open", "beta instance untouched");
		var rulesB = await _tasks.GetMethodologyInstanceRulesAsync(Proj, "beta");
		rulesB!.Definition.Kinds[0].Workflows[0].Statuses.Select(s => s.Slug).Should().Contain("Open");
	}

	// ── template edit isolation ──────────────────────────────────────────────

	[Fact]
	public async Task TemplateUpsert_DoesNotMutateInstanceRulesOrNodes()
	{
		var (inst, board) = await SeedSupportInstanceAsync();
		var rulesBefore = await _tasks.GetMethodologyInstanceRulesAsync(Proj, inst);
		var openBefore = await NodeOnBoard(board, "t-open");

		// Rename Open→Active on the TEMPLATE only — instance must stay on Open.
		var tmpl = await _tasks.GetMethodologyTemplateAsync(Proj, "support-tmpl");
		var ack = await _tasks.UpsertMethodologyTemplateAsync(
			Proj, "support-tmpl", SupportDef(openStatus: "Active"), tmpl!.Version);
		ack.Changed.Should().BeTrue();

		var rulesAfter = await _tasks.GetMethodologyInstanceRulesAsync(Proj, inst);
		rulesAfter!.Version.Should().Be(rulesBefore!.Version);
		rulesAfter.Definition.Kinds[0].Workflows[0].Statuses.Select(s => s.Slug)
			.Should().Contain("Open").And.NotContain("Active");
		var openAfter = await NodeOnBoard(board, "t-open");
		openAfter.Status.Should().Be("Open");
		openAfter.Version.Should().Be(openBefore.Version);
	}

	// ── closed instance rejects ──────────────────────────────────────────────

	[Fact]
	public async Task ClosedInstance_RulesUpsert_Rejected()
	{
		var (inst, _) = await SeedSupportInstanceAsync();
		await _tasks.CloseMethodologyInstanceAsync(Proj, inst);
		var rules = await _tasks.GetMethodologyInstanceRulesAsync(Proj, inst);
		rules!.Closed.Should().BeTrue();

		var act = () => _tasks.DefineMethodologyInstanceRulesAsync(Proj, inst, SupportDef(openStatus: "Active"), rules.Version);
		(await act.Should().ThrowAsync<InvalidOperationException>())
			.Which.Message.Should().Contain("closed");
	}

	// ── MCP upsert door ──────────────────────────────────────────────────────

	[Fact]
	public async Task Mcp_RulesUpsert_WithMigration_Works()
	{
		var (inst, board) = await SeedSupportInstanceAsync();
		var rules = await _tasks.GetMethodologyInstanceRulesAsync(Proj, inst);
		var http = Http("tasks:write");

		// Wire shape: MethodologyDefInput via MethodologyWire.ProjectDefinition reverse —
		// call the service-facing door through TasksTools with a typed definition input.
		var defInput = new PetBox.Web.Mcp.Contract.MethodologyDefInput
		{
			Name = "support-process",
			Kinds =
			[
				new PetBox.Web.Mcp.Contract.MethodologyKindInput
				{
					Kind = "support",
					QuickAddAllowed = true,
					Workflows =
					[
						new PetBox.Web.Mcp.Contract.MethodologyWorkflowInput
						{
							Types = ["ticket", "incident"],
							Statuses =
							[
								new PetBox.Web.Mcp.Contract.MethodologyStatusInput { Slug = "New", Kind = "open" },
								new PetBox.Web.Mcp.Contract.MethodologyStatusInput { Slug = "Active", Kind = "open" },
								new PetBox.Web.Mcp.Contract.MethodologyStatusInput { Slug = "Resolved", Kind = "terminalok" },
							],
							Transitions =
							[
								new PetBox.Web.Mcp.Contract.MethodologyTransitionInput { From = "New", To = "Active" },
								new PetBox.Web.Mcp.Contract.MethodologyTransitionInput { From = "Active", To = "Resolved" },
							],
						},
					],
				},
			],
		};
		var migration = new[]
		{
			new PetBox.Web.Mcp.Contract.MethodologyMigrationInput
			{
				Kind = "support",
				Statuses = [new PetBox.Web.Mcp.Contract.MethodologyValueMapInput { From = "Open", To = "Active" }],
			},
		};

		var result = await TasksTools.MethodologyRulesUpsertAsync(
			http, Flags(), _tasks, Proj, inst, defInput, rules!.Version, migration);
		result.Changed.Should().BeTrue();
		result.Migrated.Should().Be(1);
		(await NodeOnBoard(board, "t-open")).Status.Should().Be("Active");
	}
}

using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Workflow;
using PetBox.Tests.Sessions;
using PetBox.Web.Tasks;

namespace PetBox.Tests.Web;

// idea discipline-rules-warn-in-tool-response, spec unlinked-intake-twin-warns — the rule's
// EMBEDDER branch (a freshly created node resembling an OPEN, still-unlinked node on the
// declared source board). The non-embedder branch (closing without an outgoing link) is covered
// alongside the other two discipline-rule warnings in PetBox.Tests.Tasks.
// DisciplineRuleWarningsTests — it needs no embedder and lives in PetBox.Tasks proper.
//
// Uses the SAME deterministic bag-of-words embedder (PetBox.Tests.Sessions.HashedBagOfWords) as
// ObservationKindAndDedupTests' semantic leg: real cosine over hashed token overlap, so the
// merge/no-merge boundary is a property of the TEXT, not a scripted verdict.
public sealed class UnlinkedIntakeTwinWarnServiceTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly RelationStore _relations;
	readonly TasksService _tasks;

	public UnlinkedIntakeTwinWarnServiceTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-intake-twin-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
		_relations = new RelationStore(_factory);
		_tasks = new TasksService(new TaskBoardStore(_db.Factory(), _factory), _relations, new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	// Live process = open methodology instance, not the legacy project-singleton
	// methodology_defs (see MethodologyEngineV2Tests.InstallLive) — CreateMethodologyInstanceAsync
	// auto-provisions one board per declared kind, named after the kind slug, so "src"/"dst" exist
	// without an explicit tasks_board_create.
	async Task InstallLive(MethodologyDefinition def)
	{
		await _tasks.UpsertMethodologyTemplateAsync(Proj, "tmpl", def, 0);
		await _tasks.CreateMethodologyInstanceAsync(Proj, "inst", "template", "tmpl");
	}

	static MethodologyDefinition LinkDef() => new("wt-twin",
	[
		new MethodologyKindDef("src", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["issue"], [new("triage", "triage", StatusKind.Open)], []),
		]),
		new MethodologyKindDef("dst", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["task"], [new("Pending", "Pending", StatusKind.Open)], []),
		]),
	])
	{
		LinkKinds = [new MethodologyLinkKindDef("promotes", Category: LinkCategory.Process,
			Direction: new MethodologyLinkDirectionDef("src", "dst"))],
	};

	// Same paraphrase fixture ObservationKindAndDedupTests calibrated (cosine ≈ 0.89) — well
	// above this rule's own 0.72 default and ObservationDedupOptions' 0.75, so it also proves the
	// rule uses ITS OWN threshold rather than accidentally inheriting either of those.
	const string OpenTitle = "Background timer drift causes duplicate sends";
	const string OpenBody = "Timer regression background retry timer drifts forward every cycle causing duplicate sends duplicate sends observed in production logs core scheduler.";
	const string TwinTitle = "Duplicate sends: scheduler timer keeps drifting";
	const string TwinBody = "Background scheduler timer drifts forward every cycle causing duplicate sends duplicate sends seen in prod logs core retry timer issue.";
	const string UnrelatedTitle = "Retry queue backlog growing on the core scheduler";
	const string UnrelatedBody = "Scheduler core logs show retry queue backlog growing, unrelated to timer drift — entirely different topic about queue depth alerts.";

	UnlinkedIntakeTwinWarnService Service(double threshold = 0.72) =>
		new(_tasks, _relations, new HashedBagOfWordsLlmClient(),
			Microsoft.Extensions.Options.Options.Create(new UnlinkedIntakeTwinWarnOptions { SemanticThreshold = threshold }));

	[Fact]
	public async Task CreatedNode_ResemblesAnOpenUnlinkedSourceNode_Warns()
	{
		await InstallLive(LinkDef());
		await _tasks.UpsertAsync(Proj, "src",
			[new NodePatch { Key = "i1", Version = 0, Title = OpenTitle, Body = OpenBody, Type = "issue", Status = "triage" }]);

		var svc = Service();
		var created = new TaskNode
		{
			Board = "dst",
			Key = "w1",
			NodeId = Guid.NewGuid().ToString("N"),
			Type = "task",
			Status = "Pending",
			Name = TwinTitle,
			Body = TwinBody,
		};
		var warnings = await svc.WarnOnCreateAsync(Proj, "dst", [created]);

		var w = warnings.Should().ContainSingle().Subject;
		w.Rule.Should().Be("unlinked-intake-twin");
		w.Key.Should().Be("w1");
		w.Message.Should().Contain("i1");
	}

	[Fact]
	public async Task CreatedNode_ResemblesNothing_NoWarning()
	{
		await InstallLive(LinkDef());
		await _tasks.UpsertAsync(Proj, "src",
			[new NodePatch { Key = "i1", Version = 0, Title = OpenTitle, Body = OpenBody, Type = "issue", Status = "triage" }]);

		var svc = Service();
		var created = new TaskNode
		{
			Board = "dst",
			Key = "w1",
			NodeId = Guid.NewGuid().ToString("N"),
			Type = "task",
			Status = "Pending",
			Name = UnrelatedTitle,
			Body = UnrelatedBody,
		};
		var warnings = await svc.WarnOnCreateAsync(Proj, "dst", [created]);

		warnings.Should().BeEmpty();
	}

	[Fact]
	public async Task CreatedNode_ResemblesAnAlreadyLinkedSourceNode_NoWarning()
	{
		await InstallLive(LinkDef());
		var srcBorn = await _tasks.UpsertAsync(Proj, "src",
			[new NodePatch { Key = "i1", Version = 0, Title = OpenTitle, Body = OpenBody, Type = "issue", Status = "triage" }]);
		var srcNode = srcBorn.Result.Added.Single();
		var otherDst = await _tasks.UpsertAsync(Proj, "dst",
			[new NodePatch { Key = "w0", Version = 0, Title = "w0", Type = "task" }]);
		await _relations.CreateAsync(Proj, "promotes", srcNode.NodeId, otherDst.Result.Added.Single().NodeId);

		var svc = Service();
		var created = new TaskNode
		{
			Board = "dst",
			Key = "w1",
			NodeId = Guid.NewGuid().ToString("N"),
			Type = "task",
			Status = "Pending",
			Name = TwinTitle,
			Body = TwinBody,
		};
		var warnings = await svc.WarnOnCreateAsync(Proj, "dst", [created]);

		warnings.Should().BeEmpty("the resembling source node already carries an outgoing 'promotes' edge — it is not an unlinked twin any more");
	}

	// Same shape as ObservationKindAndDedupTests.HashedBagOfWordsLlmClient: real cosine over
	// hashed token overlap, deterministic and offline.
	sealed class HashedBagOfWordsLlmClient : ILlmClient
	{
		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
			Task.FromResult(HashedBagOfWords.Embed(request.Inputs));
		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);
		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
	}
}

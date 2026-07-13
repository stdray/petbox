using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PetBox.Config;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.LlmRouter;
using PetBox.LlmRouter.Contract;
using PetBox.LlmRouter.Http;
using PetBox.LlmRouter.Registry;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Navigation;
using PetBox.Web.Search;
using PetBox.Web.Settings;

namespace PetBox.Tests.Web;

// The cross-scope fan-out over the REAL object graph — the one prod runs and the one the earlier
// repro faked away. Everything from CrossScopeTaskSearchService down is resolved out of a DI
// container wired like Program.cs: scoped PetBoxDb, scoped ITasksService/ITaskBoardStore, and a
// REAL CapabilityRouter -> LlmRegistryLevelResolver -> SettingsResolver behind ILlmClient. Only the
// HTTP hop out to the model is stubbed (IOpenAiCompatibleClient), because that is the one thing
// that must not be real in a test — the registry reads it is reached through are exactly what races.
//
// WHY THE ASSERTIONS LOOK LIKE THIS. Two mistakes to avoid:
//   * "it didn't throw" proves nothing — the fan-out CATCHES a failing branch (partial degradation)
//     and the search facade CATCHES a failing index (degrade honestly, event 400). The old repro
//     asserted NotThrow and stayed green while the embed leg raced on.
//   * so assert on what a user/operator would actually see: (1) every project contributes its hit —
//     the race shows up as MISSING ROWS; (2) no branch was skipped ("project … failed, skipping
//     it"); (3) no index degraded — a race inside the embed leg lands there as
//     "degraded, reason index-error", which is the ONLY trace this leg's corruption leaves.
public sealed class CrossScopeSearchFanOutIntegrationTests : IDisposable
{
	const int Projects = 8; // >= CrossScopeTaskSearchService.MaxProjectConcurrency (6 branches run at once)

	readonly string _dir;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly CapturingLoggerProvider _logs = new();
	readonly ServiceProvider _sp;

	public CrossScopeSearchFanOutIntegrationTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-xscope-fanout-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);

		var secrets = new AesGcmSecretEncryptor(Options.Create(new SecretEncryptorOptions { MasterKey = "test-master-key" }));
		using (var seed = new PetBoxDb(PetBoxDb.CreateOptions(cs)))
		{
			for (var i = 0; i < Projects; i++)
				seed.Insert(new Project { Key = $"proj-{i}", WorkspaceKey = "ws1", Name = $"P{i}", Description = "" });

			// A WORKING system-level embed route, inherited by ws1 — so a clean run degrades NOTHING
			// and any "degraded" line in the log is a real failure, not a missing route.
			new LlmRegistryLevelAdmin(seed.Factory(), secrets).SetAsync(Scope.System, RegistryLevel.SystemScopeKey,
				new LlmRegistry([new LlmEndpoint("stub-ep", "https://stub.example")],
					[new LlmRoute(LlmCapability.Embed, "stub-ep", "stub-embed-v1")]),
				new Dictionary<string, string>(StringComparer.Ordinal) { ["stub-ep"] = "stub-key" })
				.GetAwaiter().GetResult();
		}

		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);

		var services = new ServiceCollection();
		services.AddLogging(b => b.AddProvider(_logs).SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug));
		services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
		services.AddSingleton<INavigationContext, FanOutNavigationContext>();

		// Program.cs shape: PetBoxDb is SCOPED — one non-thread-safe DataConnection per scope.
		services.AddScoped(_ => new PetBoxDb(PetBoxDb.CreateOptions(cs)));
		// …and ICoreDbFactory is the SINGLETON that hands out fresh, caller-owned connections. Same
		// as Program.cs. The services under test here (TaskBoardStore, SettingsResolver,
		// LlmRegistryLevelResolver) now take the factory, which is the whole point of this test: the
		// fan-out branches must not share one connection.
		services.AddSingleton<ICoreDbFactory>(_ => new CoreDbFactory(cs));
		services.AddSingleton<IScopedDbFactory<TasksDb>>(_factory);
		services.AddScoped<ITaskBoardStore, TaskBoardStore>();
		services.AddScoped<IRelationStore, RelationStore>();
		services.AddScoped<ITagStore, TagStore>();
		services.AddScoped<ICommentService, CommentService>();
		services.AddScoped<ITasksService, TasksService>();
		services.AddSingleton<ISecretEncryptor>(secrets);
		// SettingsResolver holds no factory any more — the DB half of settings is ISettingsStore, and
		// it is the store that takes a fresh caller-owned connection per call. Registering both is
		// Program.cs's shape, and it keeps this test's point intact: the fan-out branches drive
		// GetAsync concurrently and must not end up sharing one connection.
		services.AddScoped<ISettingsStore, SettingsStore>();
		services.AddScoped<ISettingsResolver, SettingsResolver>();
		// Registered BEFORE AddLlmRouter, whose TryAddSingleton then leaves it alone: the only stub
		// in the graph is the network hop itself.
		services.AddSingleton<IOpenAiCompatibleClient, StubUpstream>();
		services.AddLlmRouter();
		services.AddScoped<CrossScopeTaskSearchService>();

		_sp = services.BuildServiceProvider();
	}

	public void Dispose()
	{
		_sp.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public async Task FanOut_OverTheRealRouter_ReturnsAHitFromEveryProject_AndDegradesNothing()
	{
		// One full-text (NOT slug) match per project, so every branch reaches HybridCandidatesAsync
		// and therefore the embed leg — no branch short-circuits on the identifier fast-path.
		await using (var seedScope = _sp.CreateAsyncScope())
		{
			var tasks = seedScope.ServiceProvider.GetRequiredService<ITasksService>();
			for (var i = 0; i < Projects; i++)
				await tasks.UpsertAsync($"proj-{i}", "intake", [
					new NodePatch { Key = $"note-{i}", Title = "Router capability llm l5 rollout", Body = "llm l5 tier notes" }]);
		}

		var scope = new Dictionary<string, IReadOnlyList<Project>>(StringComparer.Ordinal)
		{
			["ws1"] = Enumerable.Range(0, Projects)
				.Select(i => new Project { Key = $"proj-{i}", WorkspaceKey = "ws1", Name = $"P{i}", Description = "" })
				.ToList(),
		};

		// The race is probabilistic: run the whole page-load several times, each inside its OWN
		// request scope (exactly one HTTP request = one scope = one PetBoxDb).
		for (var attempt = 0; attempt < 40; attempt++)
		{
			await using var request = _sp.CreateAsyncScope();
			var hits = await request.ServiceProvider.GetRequiredService<CrossScopeTaskSearchService>()
				.SearchAsync(scope, "llm", "https", "box.test");

			hits.Select(h => h.ProjectKey).Distinct().Should().BeEquivalentTo(
				Enumerable.Range(0, Projects).Select(i => $"proj-{i}"),
				$"attempt {attempt}: every project holds a matching node, so a parallel fan-out must return one "
				+ "from EACH — a shared-connection race shows up as missing rows, not as an exception (the "
				+ "fan-out swallows a failed branch)");
		}

		_logs.Lines.Where(l => l.Contains("failed, skipping it", StringComparison.Ordinal)).Should().BeEmpty(
			"no branch may fail: they must not share one scoped PetBoxDb");
		_logs.Lines.Where(l => l.Contains("degraded, reason index-error", StringComparison.Ordinal)).Should().BeEmpty(
			"the embed leg (CapabilityRouter -> LlmRegistryLevelResolver, 4+ reads on the scoped PetBoxDb per "
			+ "query) must not race either — its failures are SWALLOWED by SearchService's degrade-honestly "
			+ "catch, so the log is the only place the corruption surfaces");
	}

	// The network hop, and only it. Latency is deliberate: it holds every branch inside the embed
	// leg at once, which is precisely the alignment prod's trace showed.
	sealed class StubUpstream : IOpenAiCompatibleClient
	{
		public async Task<IReadOnlyList<float[]>> EmbedAsync(HttpClient http, string baseUrl, string? apiKey, string model,
			IReadOnlyList<string> inputs, CancellationToken ct)
		{
			await Task.Delay(10, ct);
			return inputs.Select(i => { var v = new float[1024]; v[Math.Abs(i.GetHashCode(StringComparison.Ordinal)) % 1024] = 1f; return v; }).ToList();
		}

		public Task<IReadOnlyList<RerankHit>> RerankAsync(HttpClient http, string baseUrl, string? apiKey, string model,
			string query, IReadOnlyList<string> documents, int? topN, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<string> ChatAsync(HttpClient http, string baseUrl, string? apiKey, string model,
			IReadOnlyList<ChatMessage> messages, double? temperature, int? maxTokens, LlmThinking? thinking, CancellationToken ct) =>
			throw new NotSupportedException();
	}

	// Unused by the explicit-enumeration overload; must merely be resolvable (prod injects the real one).
	sealed class FanOutNavigationContext : INavigationContext
	{
		public bool IsAuthenticated => throw new NotSupportedException();
		public string? Username => throw new NotSupportedException();
		public string? CurrentWorkspaceKey => throw new NotSupportedException();
		public bool HasWorkspace => throw new NotSupportedException();
		public string? CurrentProjectKey => throw new NotSupportedException();
		public IReadOnlyList<WorkspaceOption> AvailableWorkspaces => throw new NotSupportedException();
		public IReadOnlyList<Project> ProjectsInCurrentWorkspace => throw new NotSupportedException();
		public IReadOnlyDictionary<string, IReadOnlyList<Project>> ProjectsByWorkspace => throw new NotSupportedException();
		public bool DataEnabled => throw new NotSupportedException();
		public bool TasksEnabled => throw new NotSupportedException();
		public bool MemoryEnabled => throw new NotSupportedException();
		public bool LlmRouterEnabled => throw new NotSupportedException();
	}
}

using LinqToDB;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PetBox.Config;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;
using PetBox.LlmRouter.Http;
using PetBox.LlmRouter.Registry;
using PetBox.LlmRouter.Routing;
using PetBox.Web.Settings;

namespace PetBox.Tests.LlmRouter;

// THE FLIP (spec: llm-registry-own-store): CapabilityRouter now resolves through the levelled
// registry in core.db (ILlmRegistryLevelResolver) instead of the ConfigBindings-backed store.
//
// The bug being fixed is not subtle. The old store read `llm/registry` out of config/{workspace}.db,
// and only $system's workspace ever had that binding — so every OTHER project resolved an EMPTY
// registry and every embed threw no-route. Semantic search was structurally dead in 8 projects from
// the day they were created, and it said nothing louder than `degraded:true`.
//
// These tests run the REAL router over the REAL resolver over a REAL AES-GCM encryptor, i.e. the
// production wiring minus the socket, and pin the four things that must be true after the flip:
//   (a) a project whose workspace declares no registry INHERITS $system's and embeds — with the
//       decrypted key of the endpoint it inherited (the whole level travels together);
//   (b) $system itself still resolves exactly what it did, now out of core.db;
//   (c) with inheritance switched OFF the failure is honest and SAYS SO — it does not quietly fall
//       back, and the reason reaches both the exception (→ degradedReason) and log event 305;
//   (d) IsAvailableAsync — the gate the jobs use — agrees with the router on all of the above.
public sealed class LlmRouterRegistryFlipTests : IDisposable
{
	const string SystemProject = "$system"; // built-in project/workspace, seeded by M001
	const string Ws = "ws-orphan";          // a workspace that has NEVER had a registry of its own
	const string Proj = "proj-orphan";

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ISecretEncryptor _secrets;
	readonly SettingsResolver _settings;
	readonly LlmRegistryLevelResolver _resolver;
	readonly LlmRegistryLevelAdmin _admin;
	readonly CapturingLogger<CapabilityRouter> _routerLog = new();
	readonly FakeUpstream _upstream = new();
	readonly CapabilityRouter _router;

	public LlmRouterRegistryFlipTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-llmflip-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);

		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Orphan", Description = "" });

		// The real encryptor with a real master key: the key of an inherited endpoint has to survive
		// AES-GCM round-tripping on the live path, not just in an admin unit test.
		_secrets = new AesGcmSecretEncryptor(Options.Create(new SecretEncryptorOptions { MasterKey = "test-master-key" }));
		_settings = new SettingsResolver(new SettingsStore(_db.Factory()), _secrets);
		_resolver = new LlmRegistryLevelResolver(_db.Factory(), _secrets, _settings, NullLogger<LlmRegistryLevelResolver>.Instance);
		_admin = new LlmRegistryLevelAdmin(_db.Factory(), _secrets);
		_router = new CapabilityRouter(_resolver, new CertPinningHttpClientProvider(), _upstream,
			new EndpointBreaker(new FakeTimeProvider()), _routerLog);
	}

	public void Dispose()
	{
		_db.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}

	// The live shape: one keyed endpoint at $system with an Embed route (home/qwen3-embed-4b).
	Task SeedSystemAsync() => _admin.SetAsync(Scope.System, RegistryLevel.SystemScopeKey,
		new LlmRegistry(
			[new LlmEndpoint("home", "https://home.example")],
			[new LlmRoute(LlmCapability.Embed, "home", "qwen3-embed-4b")]),
		new Dictionary<string, string> { ["home"] = "sk-home" });

	// (a) The fix itself. This project had NO embed route under the old store — every search here
	// threw no-route. It now inherits $system whole and embeds, with $system's decrypted key.
	[Fact]
	public async Task Project_without_its_own_registry_now_embeds_through_the_inherited_system_level()
	{
		await SeedSystemAsync();

		var res = await _router.EmbedAsync(Proj, new EmbedRequest(["hello"]));

		res.ServedBy.Endpoint.Should().Be("home");
		res.Model.Model.Should().Be("qwen3-embed-4b");
		res.Vectors.Should().ContainSingle();
		_upstream.Calls.Should().ContainSingle();
		// The key travelled with the endpoint, through the real encryptor: the call is authenticated,
		// not anonymous (the old store's failure mode).
		_upstream.Calls[0].ApiKey.Should().Be("sk-home");
		_routerLog.Entries.Should().NotContain(e => e.EventId == 305);
	}

	// (b) $system — the ONE place where search worked — must keep working, now out of core.db.
	[Fact]
	public async Task System_project_resolves_the_same_registry_it_always_did()
	{
		await SeedSystemAsync();

		var res = await _router.EmbedAsync(SystemProject, new EmbedRequest(["hello"]));

		res.ServedBy.Endpoint.Should().Be("home");
		res.Model.Model.Should().Be("qwen3-embed-4b");
		_upstream.Calls[0].ApiKey.Should().Be("sk-home");
	}

	// (d) The gate the background jobs check before burning an embed call must agree with the router.
	[Fact]
	public async Task IsAvailable_is_true_for_an_inherited_route()
	{
		await SeedSystemAsync();

		(await _router.IsAvailableAsync(Proj, LlmCapability.Embed)).Should().BeTrue();
		(await _router.IsAvailableAsync(SystemProject, LlmCapability.Embed)).Should().BeTrue();
		// A capability nobody routes stays unavailable — inheritance does not invent routes.
		(await _router.IsAvailableAsync(Proj, LlmCapability.Chat)).Should().BeFalse();
	}

	// (c) Inheritance off at the owner's tap: an HONEST failure that names the cause. Not a silent
	// fallback to $system's credentials, and not a mute `degraded:true`.
	[Fact]
	public async Task System_tap_off_fails_honestly_and_says_why()
	{
		await SeedSystemAsync();
		await _settings.SetAsync(Scope.System, RegistryLevel.SystemScopeKey,
			new LlmRegistryInheritanceSettings { SystemShared = false },
			new LlmRegistryInheritanceSettings(), updatedBy: null);

		var act = async () => await _router.EmbedAsync(Proj, new EmbedRequest(["hello"]));

		var ex = (await act.Should().ThrowAsync<LlmRouterException>()).Which;
		ex.NoRoute.Should().BeTrue();      // → degradedReason "embed-no-route", not a retry loop
		ex.Transient.Should().BeFalse();
		ex.Message.Should().Contain("no route for Embed").And.Contain(Ws)
			.And.Contain("the system registry is not inherited here");
		_upstream.Calls.Should().BeEmpty(); // nothing was called with somebody else's key

		// …and the same reason reaches the log (event 305), where an operator can see it.
		var logged = _routerLog.Entries.Should().ContainSingle(e => e.EventId == 305).Which;
		logged.Level.Should().Be(MsLogLevel.Warning);
		logged.Message.Should().Contain("NO ROUTE").And.Contain(Proj)
			.And.Contain("not inherited here");

		// The gate agrees: the jobs will not even try.
		(await _router.IsAvailableAsync(Proj, LlmCapability.Embed)).Should().BeFalse();
	}

	// The workspace's own opt-out produces the same honesty, and $system is unaffected by it.
	[Fact]
	public async Task Workspace_optout_blocks_only_that_workspace()
	{
		await SeedSystemAsync();
		await _settings.SetAsync(Scope.Workspace, Ws,
			new LlmRegistryInheritanceSettings { WorkspaceInherits = false },
			new LlmRegistryInheritanceSettings(), updatedBy: null);

		var act = async () => await _router.EmbedAsync(Proj, new EmbedRequest(["hello"]));
		(await act.Should().ThrowAsync<LlmRouterException>()).Which.Message
			.Should().Contain("not inherited here");

		(await _router.IsAvailableAsync(SystemProject, LlmCapability.Embed)).Should().BeTrue();
	}

	// No registry ANYWHERE: also no-route, but the reason is the other one — nobody has a route,
	// as opposed to "you are not allowed to inherit the one that exists".
	[Fact]
	public async Task Empty_registry_everywhere_says_there_is_no_route_anywhere()
	{
		var act = async () => await _router.EmbedAsync(Proj, new EmbedRequest(["hello"]));

		var ex = (await act.Should().ThrowAsync<LlmRouterException>()).Which;
		ex.NoRoute.Should().BeTrue();
		ex.Message.Should().Contain("no route for Embed")
			.And.Contain("the system registry has no route for it either");
		ex.Message.Should().NotContain("not inherited");
	}

	// ---- fakes ----

	sealed record UpstreamCall(string BaseUrl, string? ApiKey, string Model);

	sealed class FakeUpstream : IOpenAiCompatibleClient
	{
		public List<UpstreamCall> Calls { get; } = [];

		public Task<IReadOnlyList<float[]>> EmbedAsync(HttpClient http, string baseUrl, string? apiKey, string model,
			IReadOnlyList<string> inputs, CancellationToken ct)
		{
			Calls.Add(new UpstreamCall(baseUrl, apiKey, model));
			return Task.FromResult<IReadOnlyList<float[]>>([[1f, 2f, 3f]]);
		}

		public Task<IReadOnlyList<RerankHit>> RerankAsync(HttpClient http, string baseUrl, string? apiKey, string model,
			string query, IReadOnlyList<string> documents, int? topN, CancellationToken ct) => throw new NotSupportedException();

		public Task<string> ChatAsync(HttpClient http, string baseUrl, string? apiKey, string model,
			IReadOnlyList<ChatMessage> messages, double? temperature, int? maxTokens, LlmThinking? thinking,
			CancellationToken ct) => throw new NotSupportedException();
	}

	sealed record LogEntry(MsLogLevel Level, int EventId, string Message);

	sealed class CapturingLogger<T> : ILogger<T>
	{
		public List<LogEntry> Entries { get; } = [];
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(MsLogLevel logLevel) => true;
		public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter) =>
			Entries.Add(new LogEntry(logLevel, eventId.Id, formatter(state, exception)));
	}
}

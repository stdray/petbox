using LinqToDB;
using Microsoft.Extensions.Logging;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;
using Microsoft.Extensions.Options;
using PetBox.Config;
using PetBox.Config.Data;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;
using PetBox.LlmRouter.Registry;
using PetBox.Web.Settings;

namespace PetBox.Tests.LlmRouter;

// THE IMPORT of the live registry out of the Config bindings into core.db's llm_endpoints/llm_routes
// (LlmRegistryImporter). The thing being pinned is the api keys: they are copied as CIPHERTEXT,
// BYTE FOR BYTE — never decrypted, never re-encrypted. That is what makes the move unable to lose or
// mangle a secret, and it is why the import runs on a host with no PETBOX_MASTER_KEY at all.
//
// Everything else here is about the import being safe to run on every start, forever: it is gated on
// an empty registry AND an absent marker, and a source it cannot parse aborts it WHOLE (the old
// bindings keep serving production; nothing half-lands).
public sealed class LlmRegistryImportTests : IDisposable
{
	// The config module is workspace-partitioned, and $system is the only workspace the live
	// llm/* bindings were ever entered in — hence the only import source.
	const string SystemWs = WorkspaceMemory.SystemWorkspace; // "$system"
	const string MasterKey = "import-test-master-key";

	readonly string _dir;
	readonly string _configDir;
	readonly PetBoxDb _db;
	readonly ISecretEncryptor _secrets;
	readonly ScopedDbFactory<ConfigDb> _scoped;
	readonly ConfigDbFactory _configFactory;
	readonly CapturingLogger _log = new();

	public LlmRegistryImportTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-llmimport-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs); // seeds the built-in $system project/workspace (M001)

		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_secrets = new AesGcmSecretEncryptor(Options.Create(new SecretEncryptorOptions { MasterKey = MasterKey }));

		_configDir = Path.Combine(_dir, "config");
		_scoped = new ScopedDbFactory<ConfigDb>(_configDir, Scope.Workspace,
			c => new ConfigDb(ConfigDb.CreateOptions(c)), ConfigSchema.Ensure);
		_configFactory = new ConfigDbFactory(_scoped);
	}

	public void Dispose()
	{
		_db.Dispose();
		_scoped.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	LlmRegistryImporter Importer() => new(_db.Factory(), _configFactory, _configDir, _log);

	// The source, written by the OLD store exactly as production wrote it: one Plain `llm/registry`
	// JSON binding + one encrypted Secret binding per api key, in config/$system.db.
	async Task SeedLegacyAsync(LlmRegistry registry, Dictionary<string, string> apiKeys) =>
		await new LlmRegistryStore(_configFactory, _secrets, _db.Factory()).SetAsync(SystemWs, registry, apiKeys);

	static LlmRegistry TwoEndpointRegistry() => new(
		[
			new LlmEndpoint("openrouter", "https://openrouter.example/v1", null, 2000, 60000),
			new LlmEndpoint("local", "https://local.example/v1", "AA:BB", 500, 5000),
		],
		[
			new LlmRoute(LlmCapability.Embed, "local", "bge-m3", 10),
			new LlmRoute(LlmCapability.Chat, "openrouter", "big-model", 10, "quality", LlmThinking.Enabled),
			new LlmRoute(LlmCapability.Chat, "local", "small-model", 20),
		]);

	ConfigBinding? ActiveBinding(string path)
	{
		using var cfg = _configFactory.NewConfigDb(SystemWs);
		return cfg.Bindings.Where(b => b.Path == path && !b.IsDeleted)
			.OrderByDescending(b => b.Version).FirstOrDefault();
	}

	// ---- (a) the import itself: rows land, and the ciphertext is BYTE-IDENTICAL ----

	[Fact]
	public async Task Import_CopiesRegistryAndKeys_CiphertextVerbatim()
	{
		await SeedLegacyAsync(TwoEndpointRegistry(), new Dictionary<string, string>
		{
			["openrouter"] = "sk-or-secret",
			["local"] = "local-secret",
		});

		// The source blobs, as the old Secret bindings hold them.
		var srcOr = ActiveBinding("llm/secret/openrouter")!;
		var srcLocal = ActiveBinding("llm/secret/local")!;

		var result = Importer().Import();

		result.Outcome.Should().Be(LlmRegistryImporter.Outcome.Imported);
		(result.Endpoints, result.Routes, result.Keys, result.DroppedRoutes).Should().Be((2, 3, 2, 0));

		var endpoints = _db.LlmEndpoints.ToList();
		endpoints.Should().HaveCount(2);
		endpoints.Should().OnlyContain(e => e.Scope == "System" && e.ScopeKey == "$");

		var or = endpoints.Single(e => e.Name == "openrouter");
		or.BaseUrl.Should().Be("https://openrouter.example/v1");
		or.ConnectTimeoutMs.Should().Be(2000);
		or.RequestTimeoutMs.Should().Be(60000);
		or.CertThumbprint.Should().BeNull();

		var local = endpoints.Single(e => e.Name == "local");
		local.CertThumbprint.Should().Be("AA:BB");
		local.ConnectTimeoutMs.Should().Be(500);

		// BYTE FOR BYTE: same base64 text AND the same decoded bytes. Not "decrypts to the same
		// plaintext" — the bytes themselves never changed, which is the guarantee that matters
		// (a re-encrypt would produce a different, equally valid-looking blob and could silently
		// have used the WRONG key).
		AssertVerbatim(srcOr, or);
		AssertVerbatim(srcLocal, local);

		var routes = _db.LlmRoutes.ToList();
		routes.Should().HaveCount(3);
		routes.Should().OnlyContain(r => r.Scope == "System" && r.ScopeKey == "$");
		var chat = routes.Where(r => r.Capability == "Chat").OrderBy(r => r.Priority).ToList();
		chat[0].Endpoint.Should().Be("openrouter");
		chat[0].Model.Should().Be("big-model");
		chat[0].Tier.Should().Be("quality");
		chat[0].Thinking.Should().Be("Enabled");
		chat[1].Endpoint.Should().Be("local");
		chat[1].Thinking.Should().BeNull();
		routes.Single(r => r.Capability == "Embed").Model.Should().Be("bge-m3");

		// The marker, and the old bindings still standing.
		_db.Settings.Any(s => s.Scope == "System" && s.ScopeKey == "$" && s.Path == LlmRegistryImporter.MarkerPath)
			.Should().BeTrue();
		ActiveBinding("llm/registry").Should().NotBeNull();
		ActiveBinding("llm/secret/openrouter").Should().NotBeNull();
	}

	static void AssertVerbatim(ConfigBinding source, LlmEndpointRow imported)
	{
		imported.KeyCipher.Should().Be(source.Ciphertext);
		imported.KeyIv.Should().Be(source.Iv);
		imported.KeyAuthTag.Should().Be(source.AuthTag);

		Convert.FromBase64String(imported.KeyCipher!).Should().Equal(Convert.FromBase64String(source.Ciphertext!));
		Convert.FromBase64String(imported.KeyIv!).Should().Equal(Convert.FromBase64String(source.Iv!));
		Convert.FromBase64String(imported.KeyAuthTag!).Should().Equal(Convert.FromBase64String(source.AuthTag!));
	}

	// The end-to-end proof that no secret was lost: the NEW resolver, given the same master key,
	// reads back the very plaintext the OLD store was handed.
	[Fact]
	public async Task Import_ImportedKeys_DecryptThroughTheNewResolver()
	{
		await SeedLegacyAsync(TwoEndpointRegistry(), new Dictionary<string, string>
		{
			["openrouter"] = "sk-or-secret",
			["local"] = "local-secret",
		});
		Importer().Import();

		var resolver = new LlmRegistryLevelResolver(_db.Factory(), _secrets, new SettingsResolver(new SettingsStore(_db.Factory()), _secrets),
			new CapturingLogger<LlmRegistryLevelResolver>());
		var resolved = await resolver.ResolveAsync(SystemWs); // the built-in $system project

		resolved.Level.Should().Be(RegistryLevel.System);
		resolved.Registry.Endpoints.Should().HaveCount(2);
		resolved.Registry.Routes.Should().HaveCount(3);
		resolved.ApiKeys["openrouter"].Should().Be("sk-or-secret");
		resolved.ApiKeys["local"].Should().Be("local-secret");
	}

	// ---- (b) idempotence ----

	[Fact]
	public async Task Import_SecondRun_IsNoOp()
	{
		await SeedLegacyAsync(TwoEndpointRegistry(), new Dictionary<string, string> { ["openrouter"] = "sk-or-secret" });
		Importer().Import();

		var firstMarker = _db.Settings.Single(s => s.Path == LlmRegistryImporter.MarkerPath).Value;
		var firstRouteIds = _db.LlmRoutes.Select(r => r.Id).OrderBy(id => id).ToList();

		var second = Importer().Import();

		second.Outcome.Should().Be(LlmRegistryImporter.Outcome.AlreadyDone);
		second.Endpoints.Should().Be(0);
		_db.LlmEndpoints.Count().Should().Be(2);
		_db.LlmRoutes.Count().Should().Be(3);
		_db.LlmRoutes.Select(r => r.Id).OrderBy(id => id).ToList().Should().Equal(firstRouteIds); // not rewritten
		_db.Settings.Single(s => s.Path == LlmRegistryImporter.MarkerPath).Value.Should().Be(firstMarker);
	}

	// A run after the marker exists must not resurrect rows an operator deliberately deleted through
	// the admin — the marker, not the emptiness of the table, is what says "this already happened".
	[Fact]
	public async Task Import_AfterMarker_DoesNotRefillAnEmptiedRegistry()
	{
		await SeedLegacyAsync(TwoEndpointRegistry(), new Dictionary<string, string> { ["local"] = "local-secret" });
		Importer().Import();

		await new LlmRegistryLevelAdmin(_db.Factory(), _secrets)
			.SetAsync(Scope.System, "$", LlmRegistry.Empty, new Dictionary<string, string>());
		_db.LlmEndpoints.Count().Should().Be(0);

		Importer().Import().Outcome.Should().Be(LlmRegistryImporter.Outcome.AlreadyDone);
		_db.LlmEndpoints.Count().Should().Be(0);
		_db.LlmRoutes.Count().Should().Be(0);
	}

	// ---- (c) the gate: a registry already in use is never overwritten ----

	[Fact]
	public async Task Import_NonEmptyTables_WithoutMarker_IsNoOp()
	{
		await SeedLegacyAsync(TwoEndpointRegistry(), new Dictionary<string, string> { ["local"] = "local-secret" });

		// Somebody wrote the new store first (no marker exists — this is not an import).
		await new LlmRegistryLevelAdmin(_db.Factory(), _secrets).SetAsync(Scope.System, "$",
			new LlmRegistry([new LlmEndpoint("hand-made", "https://hand.example")],
				[new LlmRoute(LlmCapability.Embed, "hand-made", "m")]),
			new Dictionary<string, string> { ["hand-made"] = "hand-key" });

		var result = Importer().Import();

		result.Outcome.Should().Be(LlmRegistryImporter.Outcome.AlreadyDone);
		_db.LlmEndpoints.Select(e => e.Name).Should().Equal("hand-made"); // untouched
		_db.LlmRoutes.Count().Should().Be(1);
		_db.Settings.Any(s => s.Path == LlmRegistryImporter.MarkerPath).Should().BeFalse();
	}

	[Fact]
	public void Import_NoConfigFile_IsNoOp_AndCreatesNone()
	{
		var result = Importer().Import();

		result.Outcome.Should().Be(LlmRegistryImporter.Outcome.NoSource);
		_db.LlmEndpoints.Count().Should().Be(0);
		_db.Settings.Any(s => s.Path == LlmRegistryImporter.MarkerPath).Should().BeFalse();
		File.Exists(Path.Combine(_configDir, SystemWs + ".db")).Should().BeFalse();
	}

	[Fact]
	public void Import_ConfigFileWithoutRegistryBinding_IsNoOp()
	{
		using (var cfg = _configFactory.NewConfigDb(SystemWs)) // creates the file + schema, no llm bindings
			cfg.Bindings.Count().Should().Be(0);

		Importer().Import().Outcome.Should().Be(LlmRegistryImporter.Outcome.NoSource);
		_db.LlmEndpoints.Count().Should().Be(0);
	}

	// ---- (d) a source that will not parse ABORTS the import whole ----

	[Fact]
	public async Task Import_UnparsableJson_Aborts_AndWritesNothing()
	{
		await SeedLegacyAsync(TwoEndpointRegistry(), new Dictionary<string, string> { ["local"] = "local-secret" });

		using (var cfg = _configFactory.NewConfigDb(SystemWs))
			cfg.Bindings.Where(b => b.Path == "llm/registry" && !b.IsDeleted)
				.Set(b => b.Value, "{ this is not json")
				.Update();

		var result = Importer().Import();

		result.Outcome.Should().Be(LlmRegistryImporter.Outcome.Aborted);
		_db.LlmEndpoints.Count().Should().Be(0);
		_db.LlmRoutes.Count().Should().Be(0);
		_db.Settings.Any(s => s.Path == LlmRegistryImporter.MarkerPath).Should().BeFalse(); // retried next start
		_log.Entries.Should().Contain(e => e.Level == MsLogLevel.Error && e.Message.Contains("ABORTED", StringComparison.Ordinal));

		// The old bindings — the ones still serving production — are exactly where they were.
		ActiveBinding("llm/registry").Should().NotBeNull();
		ActiveBinding("llm/secret/local")!.Ciphertext.Should().NotBeNull();
	}

	// ---- (e) no PETBOX_MASTER_KEY: the import still moves the key, because it never reads it ----

	[Fact]
	public void Import_WithoutMasterKey_StillCopiesTheCiphertext()
	{
		// A source seeded RAW — no encryptor anywhere in this test. The blobs are opaque strings; the
		// importer must move them without ever asking what they mean.
		const string cipher = "Q0lQSEVSVEVYVC1CTE9C";
		const string iv = "SVYtQkxPQg==";
		const string tag = "VEFHLUJMT0I=";
		SeedRaw(
			"""{"endpoints":[{"name":"openrouter","baseUrl":"https://openrouter.example/v1","connectTimeoutMs":2000,"requestTimeoutMs":60000}],"routes":[{"capability":"Chat","endpoint":"openrouter","model":"m","priority":10}]}""",
			("openrouter", cipher, iv, tag));

		// The encryptor is not even injected into the importer — this asserts the CONTRACT: a host
		// with no master key configured (IsAvailable == false) still completes the import.
		new AesGcmSecretEncryptor(Options.Create(new SecretEncryptorOptions { MasterKey = null }))
			.IsAvailable.Should().BeFalse();

		var result = Importer().Import();

		result.Outcome.Should().Be(LlmRegistryImporter.Outcome.Imported);
		result.Keys.Should().Be(1);
		var ep = _db.LlmEndpoints.Single();
		ep.KeyCipher.Should().Be(cipher);
		ep.KeyIv.Should().Be(iv);
		ep.KeyAuthTag.Should().Be(tag);
	}

	// ---- a route whose endpoint the registry no longer declares would break the composite FK ----

	[Fact]
	public void Import_DanglingRoute_IsDropped_NotFatal()
	{
		SeedRaw(
			"""{"endpoints":[{"name":"live","baseUrl":"https://live.example","connectTimeoutMs":2000,"requestTimeoutMs":60000}],"routes":[{"capability":"Embed","endpoint":"live","model":"m","priority":10},{"capability":"Chat","endpoint":"retired","model":"m","priority":10}]}""");

		var result = Importer().Import();

		result.Outcome.Should().Be(LlmRegistryImporter.Outcome.Imported);
		(result.Endpoints, result.Routes, result.DroppedRoutes).Should().Be((1, 1, 1));
		_db.LlmRoutes.Select(r => r.Endpoint).Should().Equal("live");
		_log.Entries.Should().Contain(e => e.Level == MsLogLevel.Warning && e.Message.Contains("DROPPED", StringComparison.Ordinal));
	}

	// Writes the legacy bindings by hand (no encryptor, no store): the registry JSON verbatim, plus a
	// Secret binding per (endpoint, cipher, iv, tag) triple.
	void SeedRaw(string registryJson, params (string Endpoint, string Cipher, string Iv, string Tag)[] secrets)
	{
		var now = DateTime.UtcNow;
		using var cfg = _configFactory.NewConfigDb(SystemWs);
		cfg.Insert(new ConfigBinding
		{
			Path = "llm/registry",
			Value = registryJson,
			Tags = $"ws:{SystemWs}",
			Kind = BindingKind.Plain,
			Version = 1,
			ContentHash = "x",
			CreatedAt = now,
			UpdatedAt = now,
		});
		foreach (var (endpoint, cipher, iv, tag) in secrets)
			cfg.Insert(new ConfigBinding
			{
				Path = $"llm/secret/{endpoint}",
				Value = string.Empty,
				Tags = $"ws:{SystemWs}",
				Kind = BindingKind.Secret,
				Ciphertext = cipher,
				Iv = iv,
				AuthTag = tag,
				Version = 1,
				ContentHash = "x",
				CreatedAt = now,
				UpdatedAt = now,
			});
	}

	sealed class CapturingLogger : ILogger<LlmRegistryImporter>
	{
		public List<(MsLogLevel Level, string Message)> Entries { get; } = [];

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(MsLogLevel logLevel) => true;

		public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter) =>
			Entries.Add((logLevel, formatter(state, exception)));
	}

	sealed class CapturingLogger<T> : ILogger<T>
	{
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(MsLogLevel logLevel) => true;
		public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter)
		{ }
	}
}

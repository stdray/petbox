using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Memory.Contract;
using PetBox.Web;
using Xunit;

namespace PetBox.Tests.Web;

// THE REFUSAL CONTRACT of the memory MCP family (work `memory-container-authz-throw-vs-skip`):
//
//     READS ANSWER ABSENCE, WRITES REFUSE EXPLICITLY.
//
// A container the caller may not READ answers exactly as if it held nothing — empty stores/items/
// entries for the sweep verbs, and for the addressed verbs (memory_get single `key`, memory_delta)
// the SAME not-found error a genuinely missing entry/store gets, byte-identical. A container the
// caller may not WRITE refuses with an explicit authorization error. The full reasoning lives at
// MemoryTools.AssertMemoryProjectAsync; this file is the pin.
//
// WHAT EACH HALF PROTECTS:
//   * the equality half ("forbidden ≡ absent, byte-for-byte in the answer") is the non-disclosure
//     property inside the CASCADE: if the two answers ever diverge — a different message, an error
//     where absence gives success — the divergence itself becomes the signal that something exists
//     behind the refusal. memory_delta had exactly that divergence before this work: all-legs-
//     refused produced the tool's "store 'X' not found (scope: …)" while a genuinely absent store
//     produced the service's "memory store 'X' not found in project 'Y'" (naming the derived
//     container), so the two cases were distinguishable by message shape.
//   * the foreign-vs-nonexistent half is the same property on the NAMED plane, where the PEP
//     answers before the tool body: a container of another tenant's workspace and a container of a
//     workspace that does not exist must be one indistinguishable refusal (TenantAuthorizer already
//     pins this at the decision level — TenantAuthorizerTests; here it is pinned on the wire).
//   * the write half is the differential: the read silence is a decision about READS, not a
//     property of the predicate — the same predicate refusing a write must stay loudly visible,
//     or a caller's fact silently lands nowhere.
//
// The sandboxOnly key is the shape that exercises the cascade skip for real: its `scope:
// "workspace"` derives its OWN workspace's container, identity authorizes it, and sandbox
// containment refuses it (SandboxContainment — the container is not a sandbox row). Measured on
// production 2026-07-26: items:[], isError:false. That answer is the contract now, and this file
// is what makes changing it a deliberate act instead of a drive-by.
[Collection("WebAppFactory")]
public sealed class MemoryReadRefusalContractTests : IClassFixture<MemoryRefusalContractHost>
{
	readonly MemoryRefusalContractHost _host;

	public MemoryReadRefusalContractTests(MemoryRefusalContractHost host) => _host = host;

	// ── READS ANSWER ABSENCE: the derived/cascade plane ──────────────────────────────────────────
	//
	// Each fact makes the SAME call twice: once with the sandboxOnly key whose workspace leg is
	// REFUSED (and whose container really does hold the probe data — there is something to
	// disclose), once with an ordinary key whose workspace leg is reachable and EMPTY. The two
	// answers must be the same shape, and where they are errors, the same bytes.

	[Fact]
	public async Task StoreList_ForbiddenWorkspaceScope_AnswersLikeAnEmptyOne()
	{
		var forbidden = await _host.CallAsync(_host.KeySandbox, "memory_store_list",
			new() { ["scope"] = "workspace" });
		var absent = await _host.CallAsync(_host.KeyOrdinary, "memory_store_list",
			new() { ["scope"] = "workspace" });

		forbidden.IsError.Should().BeFalse("a refused read leg degrades, it does not fail");
		absent.IsError.Should().BeFalse();
		forbidden.Text.Should().Contain("\"stores\":[]",
			"the refused workspace leg must contribute nothing — not even the store NAMES");
		absent.Text.Should().Contain("\"stores\":[]");
		forbidden.Text.Should().NotContain(MemoryRefusalContractHost.Canary);
		forbidden.Text.Should().NotContain(MemoryRefusalContractHost.ProbeStore,
			"a store name out of a refused container is an existence disclosure");
	}

	[Fact]
	public async Task Search_ForbiddenWorkspaceScope_AnswersLikeAnEmptyOne()
	{
		var forbidden = await _host.CallAsync(_host.KeySandbox, "memory_search",
			new() { ["scope"] = "workspace", ["q"] = MemoryRefusalContractHost.ProbeKey });
		var absent = await _host.CallAsync(_host.KeyOrdinary, "memory_search",
			new() { ["scope"] = "workspace", ["q"] = MemoryRefusalContractHost.ProbeKey });

		forbidden.IsError.Should().BeFalse();
		absent.IsError.Should().BeFalse();
		forbidden.Text.Should().Contain("\"items\":[]",
			"the entry EXISTS in the refused container and matches the query — returning it (or " +
			"erring) instead of the empty answer is the production leak / oracle this pins against");
		absent.Text.Should().Contain("\"items\":[]");
		forbidden.Text.Should().NotContain(MemoryRefusalContractHost.Canary);
	}

	[Fact]
	public async Task GetBatch_ForbiddenContainer_AnswersLikeMissingKeys()
	{
		var forbidden = await _host.CallAsync(_host.KeySandbox, "memory_get", new()
		{
			["projectKey"] = MemoryRefusalContractHost.SandboxProject,
			["store"] = MemoryRefusalContractHost.ProbeStore,
			["keys"] = new[] { MemoryRefusalContractHost.ProbeKey },
			["scope"] = "workspace",
		});
		var absent = await _host.CallAsync(_host.KeyOrdinary, "memory_get", new()
		{
			["projectKey"] = MemoryRefusalContractHost.OrdinaryProject,
			["store"] = MemoryRefusalContractHost.ProbeStore,
			["keys"] = new[] { MemoryRefusalContractHost.ProbeKey },
			["scope"] = "workspace",
		});

		forbidden.IsError.Should().BeFalse("a batch get is a soft filter — misses and refusals alike drop out");
		absent.IsError.Should().BeFalse();
		forbidden.Text.Should().Contain("\"entries\":[]");
		absent.Text.Should().Contain("\"entries\":[]");
		forbidden.Text.Should().NotContain(MemoryRefusalContractHost.Canary);
	}

	// The addressed verbs keep their not-found errors — that IS their absence answer, and the
	// refusal takes exactly that answer. The assertion is byte-equality of the error the refused
	// caller gets with the error a legitimate caller gets for genuinely absent data.
	[Fact]
	public async Task GetSingleKey_ForbiddenContainer_GetsTheSameNotFoundAsAMissingEntry()
	{
		var forbidden = await _host.CallAsync(_host.KeySandbox, "memory_get", new()
		{
			["projectKey"] = MemoryRefusalContractHost.SandboxProject,
			["store"] = MemoryRefusalContractHost.ProbeStore,
			["key"] = MemoryRefusalContractHost.ProbeKey,
			["scope"] = "workspace",
		});
		var absent = await _host.CallAsync(_host.KeyOrdinary, "memory_get", new()
		{
			["projectKey"] = MemoryRefusalContractHost.OrdinaryProject,
			["store"] = MemoryRefusalContractHost.ProbeStore,
			["key"] = MemoryRefusalContractHost.ProbeKey,
			["scope"] = "workspace",
		});

		forbidden.IsError.Should().BeTrue("the single-key contract is a not-found error for a miss, " +
			"and a refusal is answered as a miss — never as a distinct authorization error");
		absent.IsError.Should().BeTrue();

		var f = MemoryRefusalContractHost.ErrorOf(forbidden.Text);
		var a = MemoryRefusalContractHost.ErrorOf(absent.Text);
		f.Type.Should().Be(a.Type);
		f.Message.Should().Be(a.Message,
			"the entry EXISTS in the refused container — if this message differed from the missing-" +
			"entry one in any byte, the difference would be the oracle");
		f.Message.Should().NotContainAny("authoriz", "sandbox", "Unauthorized");
	}

	[Fact]
	public async Task Delta_ForbiddenContainer_GetsTheSameNotFoundAsAMissingStore()
	{
		var forbidden = await _host.CallAsync(_host.KeySandbox, "memory_delta", new()
		{
			["projectKey"] = MemoryRefusalContractHost.SandboxProject,
			["store"] = MemoryRefusalContractHost.ProbeStore,
			["sinceVersion"] = 0,
			["scope"] = "workspace",
		});
		var absent = await _host.CallAsync(_host.KeyOrdinary, "memory_delta", new()
		{
			["projectKey"] = MemoryRefusalContractHost.OrdinaryProject,
			["store"] = MemoryRefusalContractHost.ProbeStore,
			["sinceVersion"] = 0,
			["scope"] = "workspace",
		});

		forbidden.IsError.Should().BeTrue();
		absent.IsError.Should().BeTrue();

		var f = MemoryRefusalContractHost.ErrorOf(forbidden.Text);
		var a = MemoryRefusalContractHost.ErrorOf(absent.Text);
		f.Type.Should().Be(a.Type);
		f.Message.Should().Be(a.Message,
			"before this work these two messages DIFFERED (the refused path said \"store 'X' not " +
			"found (scope: …)\", the absent path leaked the service's \"… not found in project " +
			"'$ws-…'\" naming the derived container) — the divergence was the tell this pin kills");
		f.Message.Should().NotContainAny("authoriz", "sandbox", "Unauthorized");
	}

	// The absence answer must not come at the price of the CASCADE: a store living only in the
	// workspace container is served by a bare delta, exactly as memory_get's documented
	// project-then-workspace walk serves it. Before this work the near (project) leg fed the store
	// name straight to the service and its not-found failed the whole call.
	[Fact]
	public async Task Delta_BareCascade_WalksPastALegThatLacksTheStore()
	{
		var r = await _host.CallAsync(_host.KeyCascade, "memory_delta", new()
		{
			["projectKey"] = MemoryRefusalContractHost.CascadeProject,
			["store"] = MemoryRefusalContractHost.WsOnlyStore,
			["sinceVersion"] = 0,
		});

		r.IsError.Should().BeFalse(
			"the store exists in the caller's own workspace container and the cascade is documented " +
			"as project first, THEN workspace — the project leg lacking the store is a skip, not a " +
			"failure. Observed: " + r.Text);
		r.Text.Should().Contain(MemoryRefusalContractHost.WsOnlyKey);
	}

	// ── WRITES REFUSE EXPLICITLY: the same predicate, the loud half ──────────────────────────────

	[Theory]
	[InlineData("memory_store_create")]
	[InlineData("memory_store_delete")]
	[InlineData("memory_upsert")]
	[InlineData("memory_remember")]
	public async Task WriteVerbs_ForbiddenContainer_RefuseExplicitly(string tool)
	{
		// Only THIS tool's own declared parameters — memory_store_create/_delete/_upsert don't take
		// `text`/`type` at all (that used to be a harmless silently-dropped extra; per work:
		// unknown-param-silently-ignored-breaks-renames-quietly it is now a hard reject, so the probe
		// payload must be shaped per verb, not shared).
		var args = new Dictionary<string, object?>
		{
			["projectKey"] = MemoryRefusalContractHost.SandboxProject,
			["store"] = MemoryRefusalContractHost.ProbeStore,
			["scope"] = "workspace",
		};
		if (tool == "memory_upsert")
			args["entries"] = new[] { new Dictionary<string, object?>
				{ ["key"] = "probe", ["type"] = "Project", ["description"] = "p", ["body"] = "p" } };
		if (tool == "memory_remember")
		{
			args["text"] = "probe write";
			args["type"] = "Reference";
		}

		var r = await _host.CallAsync(_host.KeySandbox, tool, args);

		r.IsError.Should().BeTrue($"{tool} aimed at a container the key cannot write must refuse, " +
			"never silently drop the write. Observed: " + r.Text);
		MemoryRefusalContractHost.ErrorOf(r.Text).Message.Should().Contain("sandboxOnly",
			"the refusal names the caller's own key restriction — a fact about the caller, " +
			"not about what exists on the other side (TenantGate spells out the same rule)");
	}

	// ── THE NAMED PLANE: foreign ≡ nonexistent, per verb, on the wire ────────────────────────────
	//
	// An explicitly NAMED container of another tenant's workspace and one of a workspace that does
	// not exist must produce the same refusal (modulo the caller's own spelling of the target),
	// or the refusal is an existence oracle over workspaces. The PEP decides this
	// (TenantAuthorizer: named-but-unknown ≡ wrong-tenant, both NotAuthorized, message single-
	// sourced in TenantGate) — here the equivalence is pinned where a caller actually sees it.

	[Theory]
	[InlineData("memory_store_list")]
	[InlineData("memory_search")]
	[InlineData("memory_get")]
	[InlineData("memory_delta")]
	[InlineData("memory_upsert")]
	public async Task NamedContainer_ForeignAndNonexistent_AreTheSameAnswer(string tool)
	{
		var results = new List<(string Type, string Message)>();
		foreach (var container in new[]
		{
			MemoryRefusalContractHost.ForeignContainer,
			MemoryRefusalContractHost.NonexistentContainer,
		})
		{
			var args = new Dictionary<string, object?>
			{
				["projectKey"] = container,
				["store"] = MemoryRefusalContractHost.ProbeStore,
				["key"] = MemoryRefusalContractHost.ProbeKey,
				["q"] = "probe",
				["sinceVersion"] = 0,
			};
			if (tool == "memory_upsert")
				args["entries"] = new[] { new Dictionary<string, object?>
					{ ["key"] = "probe", ["type"] = "Project", ["description"] = "p", ["body"] = "p" } };

			var r = await _host.CallAsync(_host.KeyOrdinary, tool, args);
			r.IsError.Should().BeTrue($"{tool} at explicitly named container '{container}' must be " +
				"refused for this project-scoped key. Observed: " + r.Text);
			results.Add(MemoryRefusalContractHost.ErrorOf(r.Text));
		}

		// The target's own spelling is the caller's input, not a disclosure — normalize it out.
		static string Norm(string s) => s
			.Replace(MemoryRefusalContractHost.ForeignWorkspace, "<ws>", StringComparison.Ordinal)
			.Replace(MemoryRefusalContractHost.NonexistentWorkspace, "<ws>", StringComparison.Ordinal);

		results[0].Type.Should().Be(results[1].Type,
			"a foreign and a nonexistent workspace container must be indistinguishable refusals");
		Norm(results[0].Message).Should().Be(Norm(results[1].Message),
			"any residual difference between the two messages is an existence oracle over workspaces");
	}
}

// The three tenant shapes the contract needs, seeded once:
//   * a sandboxOnly key whose derived workspace container ($workspace) is identity-authorized but
//     containment-refused — the cascade-skip shape, with REAL data behind the refusal;
//   * an ordinary project key whose derived workspace container is reachable and EMPTY — the
//     absence comparator (its answers are the bytes the refused caller must receive);
//   * a second ordinary key whose workspace container holds a store its project lacks — the
//     cascade-walk comparator for memory_delta.
// Plus one foreign-but-existing and one nonexistent container key for the named plane.
public sealed class MemoryRefusalContractHost : IAsyncLifetime
{
	public const string OrdinaryWorkspace = "refeqws";
	public const string OrdinaryProject = "refeqproj";
	public const string CascadeWorkspace = "refcascws";
	public const string CascadeProject = "refcascproj";
	public const string ForeignWorkspace = "refotherws";
	public const string NonexistentWorkspace = "refnosuchws";
	public const string SandboxProject = "refsbox";

	public static readonly string ForeignContainer = WorkspaceMemory.ContainerKeyFor(ForeignWorkspace);
	public static readonly string NonexistentContainer = WorkspaceMemory.ContainerPrefix + NonexistentWorkspace;

	// Seeded into $workspace — the container the sandboxOnly key's workspace scope derives and is
	// refused. The probe data EXISTING there is what makes the empty answers meaningful.
	public const string ProbeStore = "refprobestore";
	public const string ProbeKey = "refprobekey";
	public const string Canary = "PETBOX-CANARY-REFUSAL-CONTRACT";

	// Seeded ONLY into the cascade key's workspace container, not its project.
	public const string WsOnlyStore = "refwsonly";
	public const string WsOnlyKey = "refwsentry";

	public string KeySandbox { get; } = $"yb_key_{Guid.NewGuid():N}";
	public string KeyOrdinary { get; } = $"yb_key_{Guid.NewGuid():N}";
	public string KeyCascade { get; } = $"yb_key_{Guid.NewGuid():N}";

	readonly string _baseDir;
	public WebApplicationFactory<Program> Factory { get; }

	public MemoryRefusalContractHost()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-refusal-" + Guid.NewGuid().ToString("N"));
		Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment("Testing");
			b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString("petbox-refusal"),
				["Host:BackgroundServices"] = "false",
				["Features:Memory"] = "true",
			}));
			b.ConfigureServices(svc =>
			{
				var existing = svc.SingleOrDefault(d => d.ServiceType == typeof(IScopedDbFactory<PetBox.Memory.Data.MemoryDb>));
				if (existing is not null) svc.Remove(existing);
				svc.AddSingleton<IScopedDbFactory<PetBox.Memory.Data.MemoryDb>>(_ =>
					new ScopedDbFactory<PetBox.Memory.Data.MemoryDb>(
						Path.Combine(_baseDir, "memory"), PetBox.Core.Settings.Scope.Project,
						c => new PetBox.Memory.Data.MemoryDb(PetBox.Memory.Data.MemoryDb.CreateOptions(c)),
						TestSchema.Memory));
			});
		});
	}

	public async ValueTask InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);

		using (var client = Factory.CreateClient())
		using (var _ = await client.GetAsync("/health")) { }

		using var scope = Factory.Services.CreateScope();
		var sp = scope.ServiceProvider;
		using (var db = sp.GetRequiredService<ICoreDbFactory>().Open())
		{
			var now = DateTime.UtcNow;
			foreach (var ws in new[] { OrdinaryWorkspace, CascadeWorkspace, ForeignWorkspace })
				await db.InsertAsync(new Workspace { Key = ws, Name = ws, Description = "", CreatedAt = now });

			await db.InsertAsync(new Project { Key = OrdinaryProject, WorkspaceKey = OrdinaryWorkspace, Name = "eq", Description = "" });
			await db.InsertAsync(new Project { Key = CascadeProject, WorkspaceKey = CascadeWorkspace, Name = "casc", Description = "" });
			// The foreign container EXISTS as a Projects row — indistinguishability from the
			// nonexistent one is exactly what the named-plane theory asserts.
			await db.InsertAsync(new Project { Key = ForeignContainer, WorkspaceKey = ForeignWorkspace, Name = "foreign shared memory", Description = "" });
			// The cascade workspace's container row, needed up front so the seeding below can
			// create a store in it (the store door validates the Projects row; at call time the
			// directory would ensure it lazily).
			await db.InsertAsync(new Project { Key = WorkspaceMemory.ContainerKeyFor(CascadeWorkspace), WorkspaceKey = CascadeWorkspace, Name = "cascade shared memory", Description = "" });
			// The sandbox project lives in the $system workspace, so the sandboxOnly key's
			// `scope: "workspace"` derives $workspace — the production shape of 2026-07-25/26.
			await db.InsertAsync(new Project { Key = SandboxProject, WorkspaceKey = WorkspaceMemory.SystemWorkspace, Name = "sbox", Description = "", Sandbox = true });

			foreach (var (key, projectClaim, def, sandboxOnly) in new (string, string, string?, bool)[]
			{
				(KeySandbox, ProjectScope.AllProjects, SandboxProject, true),
				(KeyOrdinary, OrdinaryProject, null, false),
				(KeyCascade, CascadeProject, null, false),
			})
			{
				await db.InsertAsync(new ApiKey
				{
					Key = key,
					ProjectKey = projectClaim,
					DefaultProjectKey = def,
					SandboxOnly = sandboxOnly,
					Scopes = string.Join(",", ApiKeyScopes.All.Select(s => s.Value)),
					Name = "refusal contract",
					CreatedAt = now,
				});
			}
		}

		// Probe data straight through the service layer (no tool, no authorization): the refused
		// container must actually HOLD what the refused caller is not told about.
		var memory = sp.GetRequiredService<IMemoryService>();
		await memory.CreateStoreAsync(WorkspaceMemory.SystemContainer, ProbeStore, "refusal probe", default);
		await memory.UpsertAsync(WorkspaceMemory.SystemContainer, ProbeStore,
			[new MemoryEntryInput { Key = ProbeKey, Type = "Reference", Description = Canary, Body = Canary }],
			[], true, default);

		var cascContainer = WorkspaceMemory.ContainerKeyFor(CascadeWorkspace);
		await memory.CreateStoreAsync(cascContainer, WsOnlyStore, "cascade probe", default);
		await memory.UpsertAsync(cascContainer, WsOnlyStore,
			[new MemoryEntryInput { Key = WsOnlyKey, Type = "Reference", Description = "ws-only", Body = "ws-only" }],
			[], true, default);

		await memory.CreateStoreAsync(ForeignContainer, ProbeStore, "foreign probe", default);
	}

	public async Task<(bool IsError, string Text)> CallAsync(string apiKey, string tool, Dictionary<string, object?> args)
	{
		await using var mcp = await ConnectAsync(apiKey);
		var result = await mcp.CallToolAsync(tool, args);
		return (result.IsError == true, string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
	}

	// The caller-visible identity of an error: the envelope's type + message
	// (McpErrorEnvelopeFilter). `detail` is the same exception's ToString and adds no second
	// channel; `traceId` is an owner token. Equality is asserted on what carries the contract.
	public static (string Type, string Message) ErrorOf(string text)
	{
		using var doc = JsonDocument.Parse(text);
		var error = doc.RootElement.GetProperty("error");
		return (error.GetProperty("type").GetString()!, error.GetProperty("message").GetString()!);
	}

	async Task<McpClient> ConnectAsync(string apiKey)
	{
		var http = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		http.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.ApiKeyHeader, apiKey);
		return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { [ApiKeyAuthenticationHandler.ApiKeyHeader] = apiKey },
		}, http), cancellationToken: default);
	}

	public async ValueTask DisposeAsync()
	{
		await Factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}
}

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
using Xunit.Abstractions;

namespace PetBox.Tests.Architecture;

// MEASUREMENT HARNESS for work `memory-container-sandbox-containment-bypass`.
//
// Not a pin — a report. It reproduces the three production observations the work card records, across
// four key shapes that differ only in `sandboxOnly` and in which project the key defaults to, and
// prints what each one actually returned. It exists because the premise handed to this work ("the fix
// is one place, TenantAuthorizer.KeyOnWorkspaceAsync") was REASONED rather than measured, and was
// wrong: `scope:"workspace"` never presents a workspace to the decision point at all.
//
// The assertions here are the ones the measurement settled; the report is written to
// .tmp/sandbox-containment-measurement.txt so the numbers in the commit message can be reproduced
// rather than quoted.
[Collection("WebAppFactory")]
public sealed class SandboxContainmentMeasurement : IClassFixture<SandboxContainmentHost>
{
	readonly SandboxContainmentHost _host;
	readonly ITestOutputHelper _out;

	public SandboxContainmentMeasurement(SandboxContainmentHost host, ITestOutputHelper output)
	{
		_host = host;
		_out = output;
	}

	[Fact]
	public void Report()
	{
		var rendered = string.Join(Environment.NewLine, _host.Observations
			.Select(o => $"{o.Key,-28} {o.Shape,-46} {o.Leaked,-8} {o.Observed}"));

		var dir = Path.Combine(AuthzCrossTenantHost.RepoRoot(), ".tmp");
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, "sandbox-containment-measurement.txt"), rendered + Environment.NewLine);
		_out.WriteLine(rendered);

		_host.Observations.Should().NotBeEmpty();
	}

	// THE RESULT THAT MATTERS, asserted so it cannot rot into a stale note: no key shape, on any of the
	// probed shapes of the call, reaches shared workspace memory it is not contained by.
	[Fact]
	public void NoSandboxOnlyKeyShape_ReachesWorkspaceMemoryItIsNotContainedBy()
	{
		var leaked = _host.Observations
			.Where(o => o.Leaked && o.SandboxOnly)
			.Select(o => $"  {o.Key} / {o.Shape}\n      {o.Observed}")
			.ToList();

		leaked.Should().BeEmpty(
			"each of these returned the CANARY entry seeded into a workspace container that the calling "
			+ "key is not contained by. The canary is the difference between 'the call succeeded' and 'the "
			+ "call disclosed another tenant's memory' — an empty 200 is not a leak, a canary is.\n"
			+ string.Join("\n", leaked));
	}
}

// Four key shapes × the card's observations, measured once.
public sealed class SandboxContainmentHost : IAsyncLifetime
{
	// A sandbox project INSIDE the $system workspace. This shape is the one that makes
	// `scope:"workspace"` dangerous: containment on the PROJECT leg is satisfied (the project really is
	// a sandbox), and the workspace container it then derives is `$workspace` — $system's shared memory.
	public const string SysSandboxProject = "sysbox";

	// A sandbox project in a workspace of its own.
	public const string SandboxWorkspace = "sandboxws";
	public const string SandboxProject = "sandboxproj";

	// The canary: an entry seeded into the $system workspace container. Any response containing it has
	// disclosed shared workspace memory.
	public const string Canary = "PETBOX-CANARY-WORKSPACE-MEMORY";

	readonly string _baseDir;
	public WebApplicationFactory<Program> Factory { get; }

	public sealed record Observation(string Key, bool SandboxOnly, string Shape, bool Leaked, string Observed);

	public IReadOnlyList<Observation> Observations { get; private set; } = [];

	// One call per (memory verb × container target) with the sandboxOnly key — the systematic half of
	// this work's coverage, next to the card-reproduction half above.
	//
	// WHY IT IS NOT BOLTED ONTO AuthzCrossTenantHost, which is where a "extend the cross-tenant probe"
	// instruction points. It was, and it broke that probe's own guard: seeding the extra tenants this
	// question needs pushed `victimproj` out of the 160-char window
	// TheProbe_ActuallyReachedTheSurface inspects, so the 217-surface probe started failing for a
	// reason that had nothing to do with what it measures. That probe's numbers are a calibrated
	// instrument; adding a different question to its fixture perturbs it. Same reasoning the Razor POST
	// sweep is kept in its own list — one inventory, one meaning.
	public sealed record ContainerProbe(string Verb, string Container, bool Denied, string Observed);

	public IReadOnlyList<ContainerProbe> ContainerProbes { get; private set; } = [];
	public IReadOnlyList<(string Name, bool Served, string Observed)> ContainmentControls { get; private set; } = [];

	public SandboxContainmentHost()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-containment-" + Guid.NewGuid().ToString("N"));
		Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment("Testing");
			b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString("petbox-containment"),
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
						PetBox.Memory.Data.MemorySchema.Ensure));
			});
		});
	}

	public string KeySysSandbox { get; } = $"yb_key_{Guid.NewGuid():N}";
	public string KeyOwnSandbox { get; } = $"yb_key_{Guid.NewGuid():N}";
	public string KeyNoDefault { get; } = $"yb_key_{Guid.NewGuid():N}";
	public string KeyPlainWildcard { get; } = $"yb_key_{Guid.NewGuid():N}";

	public async Task InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);

		using (var client = Factory.CreateClient())
		using (var _ = await client.GetAsync("/health")) { }

		await SeedAsync();
		Observations = await MeasureAsync();
		ContainerProbes = await SweepContainersAsync();
		ContainmentControls = await ControlsAsync();
	}

	async Task SeedAsync()
	{
		using var scope = Factory.Services.CreateScope();
		var sp = scope.ServiceProvider;
		using (var db = sp.GetRequiredService<ICoreDbFactory>().Open())
		{
			var now = DateTime.UtcNow;
			await db.InsertAsync(new Workspace { Key = SandboxWorkspace, Name = "Sandbox", Description = "", CreatedAt = now });

			// The sandbox project living in the $system workspace — the dangerous shape.
			await db.InsertAsync(new Project
			{
				Key = SysSandboxProject,
				WorkspaceKey = WorkspaceMemory.SystemWorkspace,
				Name = "Sys sandbox",
				Description = "",
				Sandbox = true,
			});
			await db.InsertAsync(new Project
			{
				Key = SandboxProject,
				WorkspaceKey = SandboxWorkspace,
				Name = "Sandbox",
				Description = "",
				Sandbox = true,
			});
			await db.InsertAsync(new Project
			{
				Key = WorkspaceMemory.ContainerKeyFor(SandboxWorkspace),
				WorkspaceKey = SandboxWorkspace,
				Name = "Sandbox shared memory",
				Description = "",
			});

			foreach (var (key, sandboxOnly, def) in new (string, bool, string?)[]
			{
				(KeySysSandbox, true, SysSandboxProject),
				(KeyOwnSandbox, true, SandboxProject),
				(KeyNoDefault, true, null),
				(KeyPlainWildcard, false, SysSandboxProject),
			})
			{
				await db.InsertAsync(new ApiKey
				{
					Key = key,
					ProjectKey = ProjectScope.AllProjects,
					DefaultProjectKey = def,
					SandboxOnly = sandboxOnly,
					Scopes = string.Join(",", ApiKeyScopes.All.Select(s => s.Value)),
					Name = "containment measurement",
					CreatedAt = now,
				});
			}
		}

		// THE CANARY, straight into the $system workspace container through the service layer (no tool,
		// no authorization) so the measurement starts with something real to disclose.
		var memory = sp.GetRequiredService<IMemoryService>();
		await memory.CreateStoreAsync(WorkspaceMemory.SystemContainer, "notes", "canary store", default);
		await memory.UpsertAsync(
			WorkspaceMemory.SystemContainer, "notes",
			[new MemoryEntryInput { Key = Canary, Type = "Reference", Description = Canary, Body = Canary }],
			[], true, default);

		// THE CANON, in both legs the SessionStart injection reads: the project's own canon and its
		// workspace's shared canon. The blast-radius question this work must answer is whether
		// tightening containment breaks that injection, and it can only be answered by fetching it.
		foreach (var container in new[] { SysSandboxProject, WorkspaceMemory.SystemContainer })
		{
			await memory.CreateStoreAsync(container, "canon", "canon", default);
			await memory.UpsertAsync(container, "canon",
				[new MemoryEntryInput { Key = "index", Type = "Reference", Description = "canon", Body = $"canon of {container}" }],
				[], true, default);
		}
	}

	async Task<IReadOnlyList<Observation>> MeasureAsync()
	{
		var rows = new List<Observation>();

		foreach (var (name, apiKey, sandboxOnly) in new[]
		{
			("sandboxOnly def=sysbox", KeySysSandbox, true),
			("sandboxOnly def=sandboxproj", KeyOwnSandbox, true),
			("sandboxOnly no-default", KeyNoDefault, true),
			("plain wildcard def=sysbox", KeyPlainWildcard, false),
		})
		{
			await using var mcp = await ConnectAsync(apiKey);

			// The card's step 1 — the argument-addressed container.
			rows.Add(await CallAsync(mcp, name, sandboxOnly, "store_list{projectKey:$workspace}",
				"memory_store_list", new() { ["projectKey"] = WorkspaceMemory.SystemContainer }));

			// The card's step 2 — scope:"workspace", NO projectKey. The path the central decision point
			// never sees as a workspace.
			rows.Add(await CallAsync(mcp, name, sandboxOnly, "search{scope:workspace}",
				"memory_search", new() { ["scope"] = "workspace", ["q"] = "CANARY" }));
			rows.Add(await CallAsync(mcp, name, sandboxOnly, "store_list{scope:workspace}",
				"memory_store_list", new() { ["scope"] = "workspace" }));

			// THE CASE THAT FIRES BY ACCIDENT RATHER THAN BY AIMING — no scope, no projectKey, just a
			// query. It is not in the work card, and it is the first thing an ordinary agent search
			// hits: the default cascade adds the caller's workspace leg on its own, so a key that
			// never asked for shared memory is handed it anyway. Measured on production leaking 7891
			// bytes of `$system` workspace memory to the smoke key — the workspace leg being the whole
			// answer, exactly as on the canon route.
			rows.Add(await CallAsync(mcp, name, sandboxOnly, "search{q} (bare, no scope/projectKey)",
				"memory_search", new() { ["q"] = "CANARY" }));

			// The same accident on the listing verb, with nothing supplied at all.
			rows.Add(await CallAsync(mcp, name, sandboxOnly, "store_list{} (bare, nothing supplied)",
				"memory_store_list", new()));

			// The card's step 3 — the CONTROL. Must deny for every sandboxOnly shape.
			rows.Add(await CallAsync(mcp, name, sandboxOnly, "store_list{projectKey:$system} [control]",
				"memory_store_list", new() { ["projectKey"] = WorkspaceMemory.SystemWorkspace }));

			// The WRITE path the card left unverified.
			rows.Add(await CallAsync(mcp, name, sandboxOnly, "remember{scope:workspace} [write]",
				"memory_remember", new() { ["scope"] = "workspace", ["text"] = "probe write", ["type"] = "Reference" }));

			// THE CASCADE MUST DEGRADE, NOT FAIL. A bare listing with no scope still has to serve the
			// PROJECT leg after the workspace leg becomes unreachable — if this came back empty or threw,
			// the fix would have broken the most routine memory call there is.
			rows.Add(await CallAsync(mcp, name, sandboxOnly, "store_list{} (cascade degrades)",
				"memory_store_list", new() { ["projectKey"] = SysSandboxProject }));

			// THE BLAST-RADIUS FLOW: the SessionStart canon injection, over REST, exactly as the wiring
			// hooks call it. Its tenant is the PROJECT in the route; the workspace canon it returns is
			// derived from that project's own row and is never a second target, so this must keep
			// working for a sandboxOnly key aimed at a sandbox project.
			rows.Add(await CanonAsync(apiKey, name, sandboxOnly, $"GET /api/memory/{SysSandboxProject}/canon [wiring]", SysSandboxProject));
		}

		return rows;
	}

	// Every memory verb that takes a projectKey, aimed at both container spellings, with the sandboxOnly
	// key (`KeySysSandbox`, whose default project `sysbox` lives in the $system workspace). Identity
	// explains neither answer — the claim is the wildcard, so it authorizes both — and the two targets
	// cover the two relationships a container can have to the caller:
	//   $workspace    — the key's OWN workspace container. The hardest case and the one that pins the
	//                   semantics: a workspace holding a sandbox project is still not itself a sandbox.
	//                   It is also the exact target read on production on 2026-07-25.
	//   $ws-sandboxws — a container belonging to a workspace the key's project is NOT in.
	async Task<IReadOnlyList<ContainerProbe>> SweepContainersAsync()
	{
		var tools = Factory.Services.GetServices<ModelContextProtocol.Server.McpServerTool>()
			.Select(t => t.ProtocolTool)
			.Where(t => t.Name.StartsWith("memory_", StringComparison.Ordinal))
			.Select(t => t.Name)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToList();

		var probes = new List<ContainerProbe>();
		await using var mcp = await ConnectAsync(KeySysSandbox);
		foreach (var container in new[] { WorkspaceMemory.SystemContainer, WorkspaceMemory.ContainerKeyFor(SandboxWorkspace) })
		{
			foreach (var tool in tools)
			{
				// Required arguments filled with type-shaped garbage, exactly as the cross-tenant probe
				// does it: the call has to reach the tool body, or a binder error would be mistaken for a
				// refusal. `store` and `text` are the only required non-tenant arguments in this family.
				var args = new Dictionary<string, object?>(StringComparer.Ordinal)
				{
					["projectKey"] = container,
					["store"] = "petbox-probe",
					["text"] = "petbox-probe",
					["key"] = "petbox-probe",
				};

				string observed;
				bool denied;
				try
				{
					var result = await mcp.CallToolAsync(tool, args);
					var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
					denied = result.IsError == true && text.Contains("UnauthorizedAccessException", StringComparison.Ordinal);
					observed = (result.IsError == true ? "ERR " : "OK  ") + Short(text);
				}
				catch (Exception ex)
				{
					denied = false;
					observed = "THROW " + ex.GetType().Name + ": " + Short(ex.Message);
				}

				probes.Add(new ContainerProbe(tool, container, denied, observed));
			}
		}

		return probes;
	}

	// The two calls that must be SERVED, so "denied" above is attributable to sandboxOnly rather than
	// to the container path being closed for everyone or the probe key being broken.
	async Task<IReadOnlyList<(string, bool, string)>> ControlsAsync()
	{
		var controls = new List<(string, bool, string)>();

		await using (var mcp = await ConnectAsync(KeySysSandbox))
		{
			// The SAME sandboxOnly key on a real SANDBOX PROJECT: containment is satisfiable there, so
			// it is satisfied. Proves the key works and its refusals are about the target.
			var r = await mcp.CallToolAsync("memory_store_list", new Dictionary<string, object?> { ["projectKey"] = SysSandboxProject });
			controls.Add(($"sandboxOnly key -> sandbox project {SysSandboxProject}", r.IsError != true,
				Short(string.Join(" ", r.Content.OfType<TextContentBlock>().Select(c => c.Text)))));
		}

		await using (var mcp = await ConnectAsync(KeyPlainWildcard))
		{
			// A wildcard key that is NOT sandboxOnly, on the very container the sweep is refused at.
			// Identical claim, identical scopes — the flag is the only difference.
			var r = await mcp.CallToolAsync("memory_store_list", new Dictionary<string, object?> { ["projectKey"] = WorkspaceMemory.SystemContainer });
			controls.Add(($"plain wildcard key -> container {WorkspaceMemory.SystemContainer}", r.IsError != true,
				Short(string.Join(" ", r.Content.OfType<TextContentBlock>().Select(c => c.Text)))));
		}

		return controls;
	}

	static async Task<Observation> CallAsync(
		McpClient mcp, string key, bool sandboxOnly, string what, string tool, Dictionary<string, object?> args)
	{
		try
		{
			var result = await mcp.CallToolAsync(tool, args);
			var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
			var ok = result.IsError != true;

			// A LEAK IS DECIDED ON CONTENT, NEVER ON AN EXCEPTION TYPE — this is the assertion trap this
			// work walked into once and must not walk into again. After the fix the card's own repro
			// step does NOT surface an error: the containment refusal is thrown INSIDE the cascade and
			// swallowed by the per-leg `catch (UnauthorizedAccessException) { continue; }`, so the
			// caller sees HTTP 200, isError:false, empty items. A guard phrased as "expect
			// UnauthorizedAccessException" would be asserting on something no caller can observe, and
			// would pass for the wrong reason the moment a leg started contributing again.
			//
			// So: leaked when the RESPONSE CARRIES workspace-container data. Three tells, any of which
			// is disclosure —
			//   * the canary body seeded into the $system container;
			//   * a row labelled `"scope":"workspace"`, which is the cascade reporting that a workspace
			//     leg contributed at all (catches a leak even with no canary in the matched rows);
			//   * for the write probes, a returned id addressed into a container.
			var leaked = ok && (
				text.Contains(Canary, StringComparison.Ordinal)
				|| text.Contains("\"scope\":\"workspace\"", StringComparison.Ordinal)
				|| (what.Contains("[write]", StringComparison.Ordinal)
					&& text.Contains(WorkspaceMemory.SystemContainer, StringComparison.Ordinal)));

			return new Observation(key, sandboxOnly, what, leaked, (ok ? "OK   " : "ERR  ") + Short(text));
		}
		catch (Exception ex)
		{
			return new Observation(key, sandboxOnly, what, false, "THROW " + ex.GetType().Name + ": " + Short(ex.Message));
		}
	}

	// The canon endpoint, over plain REST with the key in the header — the wiring hook's actual call.
	// A 200 whose `workspace` leg is populated is the injection still working; a 403 is this work
	// having broken it.
	async Task<Observation> CanonAsync(string apiKey, string key, bool sandboxOnly, string what, string projectKey)
	{
		using var http = Factory.CreateClient();
		http.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.ApiKeyHeader, apiKey);
		using var resp = await http.GetAsync($"/api/memory/{projectKey}/canon");
		var body = await resp.Content.ReadAsStringAsync();

		// SITE C, AND THE READING THAT MISSED IT. A 200 here is NOT the finding; a populated
		// `workspace` part is. This probe originally recorded only the status code, so the run that
		// first exercised it reported "200 with both legs populated" and was read as proof the wiring
		// still worked — when the second leg was the $system workspace canon being handed to a
		// contained key. The response shape distinguishes them: `"workspace":null` is the project's
		// own canon alone, anything else is shared workspace memory in the body.
		var leaked = resp.IsSuccessStatusCode
			&& !body.Contains("\"workspace\":null", StringComparison.Ordinal)
			&& body.Contains("\"workspace\":", StringComparison.Ordinal);

		return new Observation(key, sandboxOnly, what, leaked, $"{(int)resp.StatusCode} {resp.StatusCode} " + Short(body));
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

	static string Short(string? s)
	{
		s = (s ?? "").ReplaceLineEndings(" ").Trim();
		return s.Length <= 200 ? s : s[..197] + "...";
	}

	public async Task DisposeAsync()
	{
		await Factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}
}

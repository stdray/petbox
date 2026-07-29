using LinqToDB;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Services;
using PetBox.Web.AgentDefs;
using PetBox.Web.Auth;

namespace PetBox.Tests.Auth;

// Work seed-agent-def-on-project-create: a project created by the server gets the `default` agent
// definition written into its OWN authoritative store, so a newcomer's first session no longer
// opens with "no server-side definition for this project yet — using kit default baseline" and has
// something it can actually edit.
//
// IT HANGS OFF THE SERVICE LAYER, not an endpoint: ProjectDirectory.CreateAsync is the ONE
// user-project writer in the codebase (the only other Projects insert is WorkspaceMemory's $ws-*
// container, which is not a user project). That is what makes the MCP `project_create` tool, the
// admin create page and any future create surface behave identically — a seed bolted onto one
// handler is a seed the next handler forgets.
//
// THE MAIN RISK the card names: a repeat seed must NEVER overwrite an existing definition — the
// curated `$system/default` must survive every scenario, including a re-run bootstrap. That is
// closed twice over (an explicit probe, plus Version 0 meaning "create — nothing to clobber" in
// TemporalStore), and SecondSeedNeverClobbersAnEditedDefinition pins the OUTCOME rather than
// either mechanism. Same shape and same reasoning as ProjectDirectorySeedsCanonTests.
public sealed class ProjectDirectorySeedsAgentDefTests : IDisposable
{
	readonly string _dir;
	readonly ICoreDbFactory _dbf;
	readonly ServiceProvider _provider;
	readonly IAgentDefinitionService _defs;

	public ProjectDirectorySeedsAgentDefTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-seedagentdef-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_dbf = new CoreDbFactory(cs);

		using (var seed = _dbf.Open())
			seed.Insert(new Workspace { Key = "alpha", Name = "alpha", Description = "", CreatedAt = DateTime.UtcNow });

		var services = new ServiceCollection();
		services.AddSingleton(_dbf);
		services.AddScoped<IAgentDefinitionService, AgentDefinitionService>();
		services.AddScoped<IProjectAgentDefSeeder, ProjectAgentDefSeeder>();
		_provider = services.BuildServiceProvider();

		_defs = _provider.GetRequiredService<IAgentDefinitionService>();
	}

	public void Dispose()
	{
		_provider.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}

	ProjectDirectory NewDirectory() =>
		new(_dbf, new MemoryCache(new MemoryCacheOptions()), ttl: null,
			scopes: _provider.GetRequiredService<IServiceScopeFactory>());

	[Fact]
	public async Task CreateAsync_SeedsTheDefaultDefinition_WithEveryRoleAndItsProse()
	{
		var svc = NewDirectory();
		(await svc.CreateAsync("alpha", "app", "App", null)).Should().BeOfType<ProjectChangeResult.Created>();

		var list = await _defs.ListAsync("app");
		list.Select(i => i.Key).Should().Contain(DefaultAgentDefinition.Key,
			"a brand new project must not point its authoritative agent definition at nothing");

		var view = await _defs.GetAsync("app", DefaultAgentDefinition.Key);
		view.Should().NotBeNull();
		view!.Definition.Roles.Select(r => r.Slug).Should().BeEquivalentTo(
			DefaultAgentDefinition.Document.Roles.Select(r => r.Slug));
		view.Definition.Roles.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Notes),
			"the seeded roster must carry the prose — an empty skeleton is what this card exists to end");
	}

	// The whole point of the single canonical file: what lands in the project is the document
	// src/common/default-agents.json declares, not a transcription of it.
	[Fact]
	public async Task SeededDocument_IsTheCanonicalOne()
	{
		var svc = NewDirectory();
		await svc.CreateAsync("alpha", "app", "App", null);

		var view = await _defs.GetAsync("app", DefaultAgentDefinition.Key);
		view!.Definition.Should().BeEquivalentTo(DefaultAgentDefinition.Document);
	}

	[Fact]
	public async Task SecondSeedNeverClobbersAnEditedDefinition()
	{
		var svc = NewDirectory();
		await svc.CreateAsync("alpha", "app", "App", null);

		// The owner edits: a real change at the version the seed landed on.
		var seeded = await _defs.GetAsync("app", DefaultAgentDefinition.Key);
		var edited = new AgentDefinitionDoc("default",
			[new AgentDefinitionRole("solo", "worker", [], Notes: "OWNER EDITED — do not touch")]);
		await _defs.UpsertAsync("app", DefaultAgentDefinition.Key, edited, seeded!.Version);

		// A repeat run of the SAME seed path — a retried request, a re-provisioned host, a
		// re-executed bootstrap — must not win against that edit.
		await svc.SeedAgentDefinitionAsync("app", CancellationToken.None);

		var after = await _defs.GetAsync("app", DefaultAgentDefinition.Key);
		after!.Definition.Roles.Should().ContainSingle().Which.Notes.Should().Be("OWNER EDITED — do not touch");
	}

	[Fact]
	public async Task SecondSeedAgainstAnUntouchedDefinition_IsANoOp_NotANewRevision()
	{
		var svc = NewDirectory();
		await svc.CreateAsync("alpha", "app", "App", null);
		var firstVersion = (await _defs.GetAsync("app", DefaultAgentDefinition.Key))!.Version;

		await svc.SeedAgentDefinitionAsync("app", CancellationToken.None);

		(await _defs.GetAsync("app", DefaultAgentDefinition.Key))!.Version.Should().Be(firstVersion,
			"an identical re-seed must not mint a phantom revision");
	}

	// The seed writes the project it was asked about and nothing else — the neighbouring project
	// keeps whatever it had (the property that lets a curated $system/default sit untouched next to
	// any number of freshly seeded projects).
	[Fact]
	public async Task SeedTouchesOnlyItsOwnProject()
	{
		var svc = NewDirectory();
		await svc.CreateAsync("alpha", "curated", "Curated", null);
		var curated = await _defs.GetAsync("curated", DefaultAgentDefinition.Key);
		await _defs.UpsertAsync("curated", DefaultAgentDefinition.Key,
			new AgentDefinitionDoc("default", [new AgentDefinitionRole("solo", "worker", [], Notes: "CURATED")]),
			curated!.Version);

		await svc.CreateAsync("alpha", "fresh", "Fresh", null);

		(await _defs.GetAsync("curated", DefaultAgentDefinition.Key))!
			.Definition.Roles.Should().ContainSingle().Which.Notes.Should().Be("CURATED");
		(await _defs.GetAsync("fresh", DefaultAgentDefinition.Key))!
			.Definition.Roles.Should().HaveCount(DefaultAgentDefinition.Document.Roles.Count);
	}

	[Fact]
	public async Task CreateAsync_StillSucceeds_WhenTheAgentDefinitionStoreIsUnavailable()
	{
		// No IAgentDefinitionService registered — stands in for a hand-wired host or a store
		// hiccup. The project ROW is already committed by the time the seed runs, so a failed seed
		// must never turn into a refused creation.
		var bareServices = new ServiceCollection();
		using var bareProvider = bareServices.BuildServiceProvider();
		var svc = new ProjectDirectory(_dbf, new MemoryCache(new MemoryCacheOptions()), ttl: null,
			scopes: bareProvider.GetRequiredService<IServiceScopeFactory>());

		(await svc.CreateAsync("alpha", "sad", "Sad", null))
			.Should().BeOfType<ProjectChangeResult.Created>();
	}

	[Fact]
	public async Task CreateAsync_WithNoScopeFactory_StillSucceeds()
	{
		// The plain `new ProjectDirectory(dbf)` shape every pre-existing test/call site uses: the
		// seed must silently skip rather than throw.
		var svc = new ProjectDirectory(_dbf);
		(await svc.CreateAsync("alpha", "plain", "Plain", null))
			.Should().BeOfType<ProjectChangeResult.Created>();
	}
}

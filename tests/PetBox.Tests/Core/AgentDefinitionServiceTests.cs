using System.Text.Json;
using LinqToDB;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Services;

// Namespace deliberately NOT PetBox.Tests.Core — that would shadow PetBox.Core and
// break sibling tests that write Core.Models.* short names (DataDbsApiTests).
namespace PetBox.Tests.AgentDefs;

// Portable agent-definition store (agent-definition-as-data): create/get/list, CAS
// conflict, same-payload no-op, delete, reject role.model, seed roster shape.
public sealed class AgentDefinitionServiceTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly AgentDefinitionService _svc;

	public AgentDefinitionServiceTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-adef-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_svc = new AgentDefinitionService(_db.Factory());
	}

	public void Dispose()
	{
		_db.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}

	// Sample portable roster from the work-card design (no model fields).
	static AgentDefinitionDoc SampleRoster(string name = "default") => new(name,
	[
		new AgentDefinitionRole(
			Slug: "orchestrator",
			Tier: "orchestrator",
			RequiredCapabilities: ["mcp", "spawn"],
			Spawn: new AgentDefinitionSpawn(Allowed: true, AllowedRoles: ["worker", "utility", "explore"]),
			Escalation: new AgentDefinitionEscalation(Available: true, Targets: ["reserve"])),
		new AgentDefinitionRole(
			Slug: "worker",
			Tier: "worker",
			RequiredCapabilities: ["mcp"],
			Spawn: new AgentDefinitionSpawn(Allowed: false),
			Escalation: new AgentDefinitionEscalation(Available: true, Targets: ["orchestrator"])),
	]);

	[Fact]
	public async Task Upsert_Get_List_Delete_RoundTrip()
	{
		var ack = await _svc.UpsertAsync(Proj, "default", SampleRoster(), 0);
		ack.Changed.Should().BeTrue();
		ack.Key.Should().Be("default");
		ack.Version.Should().BeGreaterThan(0);

		var view = await _svc.GetAsync(Proj, "default");
		view.Should().NotBeNull();
		view!.Definition.Name.Should().Be("default");
		view.Definition.Roles.Should().HaveCount(2);
		view.Definition.Roles[0].Slug.Should().Be("orchestrator");
		view.Definition.Roles[0].RequiredCapabilities.Should().Equal("mcp", "spawn");
		view.Definition.Roles[0].Spawn!.Allowed.Should().BeTrue();
		view.Definition.Roles[0].Spawn!.AllowedRoles.Should().Equal("worker", "utility", "explore");
		view.Definition.Roles[0].Escalation!.Available.Should().BeTrue();
		view.Version.Should().Be(ack.Version);

		var list = await _svc.ListAsync(Proj);
		list.Should().ContainSingle(i => i.Key == "default" && i.Name == "default");
		list[0].Version.Should().Be(ack.Version);

		var del = await _svc.DeleteAsync(Proj, "default", ack.Version);
		del.Changed.Should().BeTrue();
		(await _svc.GetAsync(Proj, "default")).Should().BeNull();

		// Idempotent delete of missing.
		var del2 = await _svc.DeleteAsync(Proj, "default", 0);
		del2.Changed.Should().BeFalse();
		del2.Version.Should().Be(0);
	}

	[Fact]
	public async Task SamePayload_Resubmit_IsNoOp()
	{
		var ack = await _svc.UpsertAsync(Proj, "default", SampleRoster(), 0);
		var again = await _svc.UpsertAsync(Proj, "default", SampleRoster(), ack.Version);
		again.Changed.Should().BeFalse();
		again.Version.Should().Be(ack.Version);

		// Stale baseline with identical payload is also a no-op (TemporalStore SamePayload).
		var staleSame = await _svc.UpsertAsync(Proj, "default", SampleRoster(), 0);
		staleSame.Changed.Should().BeFalse();
	}

	[Fact]
	public async Task StaleBaseline_DifferentPayload_Conflicts()
	{
		var ack = await _svc.UpsertAsync(Proj, "default", SampleRoster("v1"), 0);
		// Advance past the create revision.
		var v2 = await _svc.UpsertAsync(Proj, "default", SampleRoster("v2"), ack.Version);
		v2.Changed.Should().BeTrue();
		v2.Version.Should().BeGreaterThan(ack.Version);

		// Submit against the old baseline with a different payload → conflict.
		var act = () => _svc.UpsertAsync(Proj, "default", SampleRoster("v3"), ack.Version);
		(await act.Should().ThrowAsync<InvalidOperationException>())
			.WithMessage("*conflict*stale*");
	}

	[Fact]
	public async Task Create_WhenAlreadyExists_WithBaseline0_Conflicts()
	{
		await _svc.UpsertAsync(Proj, "default", SampleRoster(), 0);
		var act = () => _svc.UpsertAsync(Proj, "default", SampleRoster("other"), 0);
		// Baseline 0 on an existing key with different payload → Stale (active row exists).
		(await act.Should().ThrowAsync<InvalidOperationException>())
			.WithMessage("*conflict*");
	}

	[Fact]
	public async Task Rejects_RoleModel_OnJsonWire()
	{
		const string bad = """
			{
			  "name": "default",
			  "roles": [
			    {
			      "slug": "orchestrator",
			      "tier": "orchestrator",
			      "requiredCapabilities": ["mcp"],
			      "model": "claude-opus"
			    }
			  ]
			}
			""";
		var act = () => _svc.UpsertJsonAsync(Proj, "default", bad, 0);
		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*model*not allowed*");
	}

	[Fact]
	public async Task Rejects_RootModel_OnJsonWire()
	{
		const string bad = """
			{
			  "name": "default",
			  "model": "should-not-be-here",
			  "roles": [
			    {
			      "slug": "worker",
			      "tier": "worker",
			      "requiredCapabilities": []
			    }
			  ]
			}
			""";
		var act = () => _svc.UpsertJsonAsync(Proj, "default", bad, 0);
		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*model*not allowed*");
	}

	[Fact]
	public async Task Rejects_Model_UnderSpawn_OnJsonWire()
	{
		const string bad = """
			{
			  "name": "default",
			  "roles": [
			    {
			      "slug": "orchestrator",
			      "tier": "orchestrator",
			      "requiredCapabilities": [],
			      "spawn": { "allowed": true, "model": "nested-lie" }
			    }
			  ]
			}
			""";
		var act = () => _svc.UpsertJsonAsync(Proj, "default", bad, 0);
		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*model*not allowed*");
	}

	[Fact]
	public void Parse_Rejects_Model_Anywhere_InTree()
	{
		const string nested = """
			{
			  "name": "default",
			  "roles": [
			    {
			      "slug": "orchestrator",
			      "tier": "orchestrator",
			      "requiredCapabilities": [],
			      "escalation": { "available": true, "targets": ["reserve"], "model": "deep" }
			    }
			  ]
			}
			""";
		var act = () => AgentDefinitionJson.Parse(nested);
		act.Should().Throw<ArgumentException>().WithMessage("*model*not allowed*");
	}

	[Fact]
	public async Task Ignores_Unknown_ForwardCompat_Fields()
	{
		const string json = """
			{
			  "name": "default",
			  "futureFlag": true,
			  "roles": [
			    {
			      "slug": "worker",
			      "tier": "worker",
			      "requiredCapabilities": [],
			      "extraHint": "ignored"
			    }
			  ]
			}
			""";
		var ack = await _svc.UpsertJsonAsync(Proj, "default", json, 0);
		ack.Changed.Should().BeTrue();
		var view = await _svc.GetAsync(Proj, "default");
		view!.Definition.Roles.Should().ContainSingle(r => r.Slug == "worker");
	}

	// A definition carrying `notes` (every role of the TS client's DEFAULT_AGENT_DEFINITION does)
	// plus arbitrary unknown fields at the root: the RAW path stores the caller's document
	// verbatim, so a root-level `notes` (not part of AgentDefinitionDoc's schema) and
	// `futureFlag` survive as raw JSON even though the typed view doesn't project them. A
	// role's `notes`, however, IS part of AgentDefinitionRole now — it survives both the raw
	// JSON and the typed projection.
	const string RosterWithUnknownFields = """
		{
		  "name": "default",
		  "notes": "root-level prose",
		  "futureFlag": true,
		  "roles": [
		    {
		      "slug": "worker",
		      "tier": "worker",
		      "requiredCapabilities": ["mcp"],
		      "notes": "does the work",
		      "spawn": { "allowed": false }
		    }
		  ]
		}
		""";

	[Fact]
	public async Task RawUpsert_PreservesUnknownFields_OnRoundTrip()
	{
		var ack = await _svc.UpsertJsonAsync(Proj, "default", RosterWithUnknownFields, 0);
		ack.Changed.Should().BeTrue();

		var raw = await _svc.GetJsonAsync(Proj, "default");
		raw.Should().NotBeNull();
		using var doc = JsonDocument.Parse(raw!);
		var root = doc.RootElement;
		root.GetProperty("notes").GetString().Should().Be("root-level prose");
		root.GetProperty("futureFlag").GetBoolean().Should().BeTrue();
		var role = root.GetProperty("roles")[0];
		role.GetProperty("notes").GetString().Should().Be("does the work");
		role.GetProperty("slug").GetString().Should().Be("worker");

		// The typed view projects only what's in the schema — root-level `notes` (not part of
		// AgentDefinitionDoc) is not exposed here — but a role's `notes` IS schema now and
		// comes through the typed projection too.
		var view = await _svc.GetAsync(Proj, "default");
		view!.Definition.Name.Should().Be("default");
		view.Definition.Roles.Should().ContainSingle(r => r.Slug == "worker");
		view.Definition.Roles[0].Notes.Should().Be("does the work");
	}

	[Fact]
	public async Task RawUpsert_IdenticalResubmit_IsNoOp_NoPhantomRevision()
	{
		var ack = await _svc.UpsertJsonAsync(Proj, "default", RosterWithUnknownFields, 0);
		var again = await _svc.UpsertJsonAsync(Proj, "default", RosterWithUnknownFields, ack.Version);
		again.Changed.Should().BeFalse();
		again.Version.Should().Be(ack.Version);

		// Same document, different whitespace: canonical storage must not mint a revision either.
		var reformatted = JsonSerializer.Serialize(JsonDocument.Parse(RosterWithUnknownFields).RootElement);
		var third = await _svc.UpsertJsonAsync(Proj, "default", reformatted, ack.Version);
		third.Changed.Should().BeFalse();
		third.Version.Should().Be(ack.Version);
	}

	[Fact]
	public async Task RawUpsert_WithoutName_UsesKeySlug()
	{
		const string nameless = """
			{
			  "roles": [
			    { "slug": "worker", "tier": "worker", "requiredCapabilities": [] }
			  ]
			}
			""";
		var ack = await _svc.UpsertJsonAsync(Proj, "squad", nameless, 0);
		ack.Changed.Should().BeTrue();

		(await _svc.GetAsync(Proj, "squad"))!.Definition.Name.Should().Be("squad");
		using var doc = JsonDocument.Parse((await _svc.GetJsonAsync(Proj, "squad"))!);
		doc.RootElement.GetProperty("name").GetString().Should().Be("squad", "no nameless document is ever stored");
	}

	[Fact]
	public async Task RawUpsert_UnknownFields_DoNotSmuggleModel()
	{
		const string bad = """
			{
			  "name": "default",
			  "notes": "prose",
			  "roles": [
			    {
			      "slug": "worker",
			      "tier": "worker",
			      "requiredCapabilities": [],
			      "extra": { "nested": { "model": "smuggled" } }
			    }
			  ]
			}
			""";
		var act = () => _svc.UpsertJsonAsync(Proj, "default", bad, 0);
		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*model*not allowed*");
		(await _svc.ListAsync(Proj)).Should().BeEmpty();
	}

	// The bug this guards: AgentDefinitionRole previously had no Notes member, so the TYPED
	// upsert/get path (used by MCP agent_def_upsert/get and the REST PUT/GET) silently
	// stripped a role's free-text prose on every round trip. Notes is now part of the schema
	// itself (not an "unknown field" surviving only via the RAW path).
	[Fact]
	public async Task TypedUpsert_RoundTrips_RoleNotes()
	{
		var doc = new AgentDefinitionDoc("default",
		[
			new AgentDefinitionRole(
				Slug: "worker",
				Tier: "worker",
				RequiredCapabilities: ["mcp"],
				Notes: "you are a LEAF, never spawn subagents, do only the delegated task."),
		]);

		var ack = await _svc.UpsertAsync(Proj, "default", doc, 0);
		ack.Changed.Should().BeTrue();

		var view = await _svc.GetAsync(Proj, "default");
		view.Should().NotBeNull();
		view!.Definition.Roles.Should().ContainSingle();
		view.Definition.Roles[0].Notes.Should()
			.Be("you are a LEAF, never spawn subagents, do only the delegated task.");
	}

	[Fact]
	public async Task TypedUpsert_RoleWithoutNotes_NotesIsNull()
	{
		var ack = await _svc.UpsertAsync(Proj, "default", SampleRoster(), 0);
		ack.Changed.Should().BeTrue();

		var view = await _svc.GetAsync(Proj, "default");
		view!.Definition.Roles[0].Notes.Should().BeNull();
	}

	// Two documents differing ONLY in a role's `notes` must be treated as DIFFERENT payloads —
	// notes participates in the stored/canonical form, it is not stripped before the
	// same-payload comparison.
	[Fact]
	public async Task TypedUpsert_NotesOnlyDiff_ProducesChangedTrue()
	{
		static AgentDefinitionDoc RosterWithNotes(string notes) => new("default",
		[
			new AgentDefinitionRole(
				Slug: "worker",
				Tier: "worker",
				RequiredCapabilities: ["mcp"],
				Notes: notes),
		]);

		var ack = await _svc.UpsertAsync(Proj, "default", RosterWithNotes("first notes"), 0);
		ack.Changed.Should().BeTrue();

		var again = await _svc.UpsertAsync(Proj, "default", RosterWithNotes("completely different notes"), ack.Version);
		again.Changed.Should().BeTrue("a role differing only in notes is a different stored payload");
		again.Version.Should().BeGreaterThan(ack.Version);

		var view = await _svc.GetAsync(Proj, "default");
		view!.Definition.Roles[0].Notes.Should().Be("completely different notes");
	}

	// model-rejection must remain exactly as strict even when the role also carries notes.
	[Fact]
	public async Task Rejects_RoleModel_EvenWhenRoleCarriesNotes()
	{
		const string bad = """
			{
			  "name": "default",
			  "roles": [
			    {
			      "slug": "worker",
			      "tier": "worker",
			      "requiredCapabilities": [],
			      "notes": "prose that should not smuggle a model binding",
			      "model": "claude-opus"
			    }
			  ]
			}
			""";
		var act = () => _svc.UpsertJsonAsync(Proj, "default", bad, 0);
		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*model*not allowed*");
	}

	[Fact]
	public async Task InvalidKey_Rejected()
	{
		var act = () => _svc.UpsertAsync(Proj, "Bad Key!", SampleRoster(), 0);
		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*valid agent definition key*");
	}

	[Fact]
	public async Task EmptyRoles_Rejected()
	{
		var act = () => _svc.UpsertAsync(Proj, "default", new AgentDefinitionDoc("x", []), 0);
		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*roles*");
	}

	[Fact]
	public async Task ProjectIsolation_SameKeyIndependent()
	{
		_db.Insert(new Project { Key = "other", WorkspaceKey = "ws", Name = "O", Description = "" });
		var a = await _svc.UpsertAsync(Proj, "default", SampleRoster("a"), 0);
		var b = await _svc.UpsertAsync("other", "default", SampleRoster("b"), 0);
		a.Version.Should().BeGreaterThan(0);
		b.Version.Should().BeGreaterThan(0);

		(await _svc.GetAsync(Proj, "default"))!.Definition.Name.Should().Be("a");
		(await _svc.GetAsync("other", "default"))!.Definition.Name.Should().Be("b");
		(await _svc.ListAsync(Proj)).Should().ContainSingle();
		(await _svc.ListAsync("other")).Should().ContainSingle();
	}

	[Fact]
	public async Task Delete_StaleVersion_Conflicts()
	{
		var ack = await _svc.UpsertAsync(Proj, "default", SampleRoster(), 0);
		var v2 = await _svc.UpsertAsync(Proj, "default", SampleRoster("v2"), ack.Version);
		var act = () => _svc.DeleteAsync(Proj, "default", ack.Version);
		(await act.Should().ThrowAsync<InvalidOperationException>())
			.WithMessage("*conflict*");
		// Still present.
		(await _svc.GetAsync(Proj, "default")).Should().NotBeNull();
		// Correct baseline deletes.
		(await _svc.DeleteAsync(Proj, "default", v2.Version)).Changed.Should().BeTrue();
	}
}

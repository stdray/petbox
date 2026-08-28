using System.Text.Json;
using LinqToDB;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Services;

// Namespace deliberately NOT PetBox.Tests.Core — that would shadow PetBox.Core and break sibling
// tests that write Core.Models.* short names (the same note AgentDefinitionServiceTests carries).
namespace PetBox.Tests.AgentDefs;

// work/agent-def-upsert-typed-and-merge-by-role — the STORE-level half of the merge. The wire
// contract is asserted over MCP in Mcp/AgentDefUpsertMergeTests; this pins what the merge does to
// the stored document itself, which the wire tests cannot see: that it edits the document AS
// STORED rather than round-tripping it through the typed record, and therefore cannot drop what the
// typed record has no slot for.
public sealed class AgentDefinitionMergeTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly AgentDefinitionService _svc;

	public AgentDefinitionMergeTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-adef-merge-" + Guid.NewGuid().ToString("N"));
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

	static AgentDefinitionDoc Roster() => new("default",
	[
		new AgentDefinitionRole("orchestrator", "orchestrator", ["mcp", "spawn"],
			Spawn: new AgentDefinitionSpawn(true, ["worker"]),
			Escalation: new AgentDefinitionEscalation(true, ["reserve"]),
			Notes: "orchestrator prose"),
		new AgentDefinitionRole("worker", "worker", ["mcp"], Notes: "worker prose"),
	]);

	async Task<JsonElement> StoredAsync(string key = "default")
	{
		var json = await _svc.GetJsonAsync(Proj, key);
		json.Should().NotBeNull();
		return JsonDocument.Parse(json!).RootElement.Clone();
	}

	static JsonElement RoleOf(JsonElement doc, string slug) =>
		doc.GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("slug").GetString() == slug);

	// A brand-new key merges onto an EMPTY document, so create and edit stay ONE verb.
	[Fact]
	public async Task Merge_OnAMissingKey_CreatesTheDocument()
	{
		var ack = await _svc.MergeRolesAsync(Proj, "fresh", name: null,
			[new RoleMergeEdit("worker", Tier: "worker", RequiredCapabilities: ["mcp"])], version: 0);
		ack.Changed.Should().BeTrue();

		var view = await _svc.GetAsync(Proj, "fresh");
		view!.Definition.Name.Should().Be("fresh", "a document created with no name takes the key slug");
		view.Definition.Roles.Should().ContainSingle().Which.Slug.Should().Be("worker");
	}

	// A new role that arrives half-specified is REFUSED by Validate, not stored: on a NEW role an
	// omitted field starts empty, and tier/requiredCapabilities are required.
	[Fact]
	public async Task Merge_NewRole_WithoutTier_IsRefused_NamingTheRole()
	{
		var ack = await _svc.UpsertAsync(Proj, "default", Roster(), 0);
		var act = () => _svc.MergeRolesAsync(Proj, "default", null, [new RoleMergeEdit("utility")], ack.Version);
		(await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*utility*tier is required*");

		// The refusal is total — the half-specified role was not appended.
		(await _svc.GetAsync(Proj, "default"))!.Definition.Roles.Should().HaveCount(2);
	}

	// The reason the merge walks the JsonNode tree instead of the typed record: a property the
	// typed schema has no slot for — on the root, on ANOTHER role, or on the edited role itself —
	// must survive an edit. A round trip through AgentDefinitionDoc would erase all three.
	[Fact]
	public async Task Merge_PreservesPropertiesOutsideTheTypedSchema()
	{
		const string raw = """
			{
			  "name": "default",
			  "profile": "an unknown ROOT property",
			  "roles": [
			    { "slug": "orchestrator", "tier": "orchestrator", "requiredCapabilities": ["mcp"], "badge": "unknown on ANOTHER role" },
			    { "slug": "worker", "tier": "worker", "requiredCapabilities": ["mcp"], "badge": "unknown on the EDITED role" }
			  ]
			}
			""";
		var ack = await _svc.UpsertJsonAsync(Proj, "default", raw, 0);

		await _svc.MergeRolesAsync(Proj, "default", null,
			[new RoleMergeEdit("worker", Notes: "new prose")], ack.Version);

		var doc = await StoredAsync();
		doc.GetProperty("profile").GetString().Should().Be("an unknown ROOT property");
		RoleOf(doc, "orchestrator").GetProperty("badge").GetString().Should().Be("unknown on ANOTHER role");
		RoleOf(doc, "worker").GetProperty("badge").GetString().Should().Be("unknown on the EDITED role");
		RoleOf(doc, "worker").GetProperty("notes").GetString().Should().Be("new prose");
	}

	// Either half of a flattened spawn/escalation block may be sent alone: the half that is not sent
	// is read off the stored block. Sending only the list must NOT reset the flag — the silent-clear
	// this flattening exists to make impossible.
	[Fact]
	public async Task Merge_FlagBlock_EitherHalfAlone_KeepsTheOther()
	{
		var ack = await _svc.UpsertAsync(Proj, "default", Roster(), 0);

		// only the LIST
		var a = await _svc.MergeRolesAsync(Proj, "default", null,
			[new RoleMergeEdit("orchestrator", SpawnAllowedRoles: ["worker", "utility"])], ack.Version);
		var afterList = await _svc.GetAsync(Proj, "default");
		var orch = afterList!.Definition.Roles.Single(r => r.Slug == "orchestrator");
		orch.Spawn!.Allowed.Should().BeTrue("sending only the allowlist must not clear `allowed`");
		orch.Spawn.AllowedRoles.Should().BeEquivalentTo(["worker", "utility"]);
		orch.Escalation!.Targets.Should().BeEquivalentTo(["reserve"], "the escalation block was not this call's business");

		// only the FLAG
		await _svc.MergeRolesAsync(Proj, "default", null,
			[new RoleMergeEdit("orchestrator", SpawnAllowed: false)], a.Version);
		var afterFlag = (await _svc.GetAsync(Proj, "default"))!.Definition.Roles.Single(r => r.Slug == "orchestrator");
		afterFlag.Spawn!.Allowed.Should().BeFalse();
		afterFlag.Spawn.AllowedRoles.Should().BeEquivalentTo(["worker", "utility"], "sending only the flag must not clear the list");
	}

	// null = keep, "" = clear. The cleared key is REMOVED, never stored as an empty string — the
	// same normalization the form path (PatchRole) already applies.
	[Fact]
	public async Task Merge_Notes_OmitKeeps_EmptyStringClears()
	{
		var ack = await _svc.UpsertAsync(Proj, "default", Roster(), 0);

		var kept = await _svc.MergeRolesAsync(Proj, "default", null,
			[new RoleMergeEdit("worker", Tier: "worker")], ack.Version);
		RoleOf(await StoredAsync(), "worker").GetProperty("notes").GetString().Should().Be("worker prose");

		await _svc.MergeRolesAsync(Proj, "default", null, [new RoleMergeEdit("worker", Notes: "")], kept.Version);
		RoleOf(await StoredAsync(), "worker").TryGetProperty("notes", out _).Should()
			.BeFalse("an explicit clear removes the key rather than storing an empty string");
	}

	// requiredCapabilities: [] is an explicit CLEAR, not "omitted" — the list types keep the
	// omit/clear/replace triple the rest of the surface uses.
	[Fact]
	public async Task Merge_EmptyCapabilityList_ClearsRatherThanKeeps()
	{
		var ack = await _svc.UpsertAsync(Proj, "default", Roster(), 0);
		await _svc.MergeRolesAsync(Proj, "default", null,
			[new RoleMergeEdit("worker", RequiredCapabilities: [])], ack.Version);

		var worker = (await _svc.GetAsync(Proj, "default"))!.Definition.Roles.Single(r => r.Slug == "worker");
		worker.RequiredCapabilities.Should().BeEmpty();
	}

	// FOUND DURING VERIFICATION, do not regress: a BRAND-NEW role sending an explicit
	// `requiredCapabilities: []` must be stored WITH the empty array. The form-path setter treats an
	// absent key and an empty list as equal — true when the key always exists, false for a new role
	// that starts as `{ "slug": … }` — so the field was never written and Validate then rejected the
	// caller's own well-formed payload with "requiredCapabilities is required".
	[Fact]
	public async Task Merge_NewRole_WithExplicitlyEmptyCapabilities_IsStored()
	{
		var ack = await _svc.MergeRolesAsync(Proj, "fresh", null,
			[new RoleMergeEdit("worker", Tier: "worker", RequiredCapabilities: [])], version: 0);
		ack.Changed.Should().BeTrue();

		RoleOf(await StoredAsync("fresh"), "worker").GetProperty("requiredCapabilities")
			.GetArrayLength().Should().Be(0);
	}

	// Two edits addressing one slug in a single call would apply in array order and leave the caller
	// unable to tell which won. Refuse instead of silently picking the last.
	[Fact]
	public async Task Merge_DuplicateSlugInOneCall_IsRefused()
	{
		var ack = await _svc.UpsertAsync(Proj, "default", Roster(), 0);
		var act = () => _svc.MergeRolesAsync(Proj, "default", null,
			[new RoleMergeEdit("worker", Notes: "a"), new RoleMergeEdit("worker", Notes: "b")], ack.Version);
		(await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*worker*twice*");
	}

	// Deleting a role that is not there is an idempotent no-op, the same stance agent_def_delete
	// takes on a missing key — but it must not become a licence to empty the roster.
	[Fact]
	public async Task Merge_DeleteOfAnAbsentRole_IsANoOp()
	{
		var ack = await _svc.UpsertAsync(Proj, "default", Roster(), 0);
		await _svc.MergeRolesAsync(Proj, "default", null,
			[new RoleMergeEdit("no-such-role", Deleted: true)], ack.Version);

		(await _svc.GetAsync(Proj, "default"))!.Definition.Roles.Select(r => r.Slug)
			.Should().BeEquivalentTo(["orchestrator", "worker"]);
	}

	// Validate still owns the floor: a document must keep at least one role, so deleting the last
	// one is refused rather than storing an empty roster.
	[Fact]
	public async Task Merge_DeletingEveryRole_IsRefused()
	{
		var ack = await _svc.UpsertAsync(Proj, "default", Roster(), 0);
		var act = () => _svc.MergeRolesAsync(Proj, "default", null,
			[new RoleMergeEdit("orchestrator", Deleted: true), new RoleMergeEdit("worker", Deleted: true)],
			ack.Version);
		(await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*at least one role*");
	}

	// The cost claim of spec/write-cost-follows-change, measured rather than asserted in prose: the
	// payload that changes one role is the size of that role, not of the document. The intake issue
	// measured 10 520 B for a one-role edit on a six-role roster.
	[Fact]
	public async Task Merge_OneRoleEdit_CostsTheRoleNotTheDocument()
	{
		var big = new AgentDefinitionDoc("default",
		[
			.. Enumerable.Range(0, 6).Select(i => new AgentDefinitionRole(
				$"role{i}", "worker", ["mcp"], Notes: new string('x', 1500))),
		]);
		var ack = await _svc.UpsertAsync(Proj, "default", big, 0);

		var wholeDocument = AgentDefinitionJson.Serialize(big).Length;
		var oneRoleEdit = JsonSerializer.Serialize(
			new[] { new RoleMergeEdit("role3", Notes: "a short new briefing") },
			AgentDefinitionJson.Options).Length;

		oneRoleEdit.Should().BeLessThan(wholeDocument / 10,
			$"editing one role must cost the ROLE ({oneRoleEdit} B), not the document ({wholeDocument} B)");

		// And the small payload really does the edit, leaving the other five roles alone.
		await _svc.MergeRolesAsync(Proj, "default", null, [new RoleMergeEdit("role3", Notes: "a short new briefing")], ack.Version);
		var view = await _svc.GetAsync(Proj, "default");
		view!.Definition.Roles.Should().HaveCount(6);
		view.Definition.Roles.Single(r => r.Slug == "role3").Notes.Should().Be("a short new briefing");
		view.Definition.Roles.Single(r => r.Slug == "role5").Notes.Should().Be(new string('x', 1500));
	}
}

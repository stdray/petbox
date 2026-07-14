using PetBox.Core.Contract;

// Namespace deliberately NOT PetBox.Tests.Core — mirrors AgentDefinitionServiceTests (see its
// header comment): that would shadow PetBox.Core and break sibling tests using Core.Models.*
// short names.
namespace PetBox.Tests.AgentDefs;

// The admin-UI form-mode edit path (agent-def-ui-is-a-json-textarea): AgentDefinitionJson.
// PatchRole/AddRole/RemoveRole patch the STORED raw document in place instead of rebuilding it
// from the typed fields the form shows. The one invariant every test here is really about:
// nothing the form doesn't show may be lost, and an edit-nothing save must reproduce the exact
// same bytes.
public sealed class AgentDefinitionFormPatchTests
{
	// A document with fields OUTSIDE the schema at every level the form touches: a top-level
	// unknown property, a per-role unknown property, and (deliberately) capabilities already in
	// catalog order so a no-op save can be asserted byte-for-byte, not just value-equal.
	const string DocWithUnknownFields =
		"""{"name":"core team","ownerNote":"do not delete","roles":[{"slug":"lead","tier":"orchestrator","requiredCapabilities":["mcp_main_session","spawn_subagents"],"spawn":{"allowed":true,"allowedRoles":["worker"]},"escalation":{"available":false},"notes":"runs the show","favoriteColor":"teal"},{"slug":"worker","tier":"worker","requiredCapabilities":[],"escalation":{"available":true,"targets":["lead"]}}]}""";

	static RoleFormEdit EditFrom(AgentDefinitionRole role) => new(
		Slug: role.Slug,
		Tier: role.Tier,
		RequiredCapabilities: role.RequiredCapabilities,
		SpawnAllowed: role.Spawn?.Allowed ?? false,
		SpawnAllowedRoles: role.Spawn?.AllowedRoles ?? [],
		EscalationAvailable: role.Escalation?.Available ?? false,
		EscalationTargets: role.Escalation?.Targets ?? [],
		Notes: role.Notes);

	[Fact]
	public void PatchRole_NoOpSave_IsByteForByteIdentical()
	{
		var def = AgentDefinitionJson.Parse(DocWithUnknownFields);

		// Re-save role 0 with EXACTLY the values already there — the form's "open, touch
		// nothing, hit save" path.
		var patched = AgentDefinitionJson.PatchRole(DocWithUnknownFields, roleIndex: 0, EditFrom(def.Roles[0]));

		patched.Should().Be(DocWithUnknownFields,
			"a no-op form save must not rewrite a single byte — not the untouched role, not the other role, " +
			"not the top-level ownerNote, not the role's own favoriteColor");
	}

	[Fact]
	public void PatchRole_NoOpSave_OnSecondRole_AlsoIdentical()
	{
		var def = AgentDefinitionJson.Parse(DocWithUnknownFields);
		var patched = AgentDefinitionJson.PatchRole(DocWithUnknownFields, roleIndex: 1, EditFrom(def.Roles[1]));
		patched.Should().Be(DocWithUnknownFields);
	}

	[Fact]
	public void PatchRole_ChangesOnlyTheTouchedRole_PreservesEverythingElse()
	{
		var def = AgentDefinitionJson.Parse(DocWithUnknownFields);
		var edit = EditFrom(def.Roles[0]) with { Tier = "principal" };

		var patched = AgentDefinitionJson.PatchRole(DocWithUnknownFields, roleIndex: 0, edit);
		var reparsed = AgentDefinitionJson.Parse(patched);

		reparsed.Roles[0].Tier.Should().Be("principal");
		// Untouched sibling role and unknown top-level field survive verbatim.
		using var raw = System.Text.Json.JsonDocument.Parse(patched);
		raw.RootElement.GetProperty("ownerNote").GetString().Should().Be("do not delete");
		raw.RootElement.GetProperty("roles")[0].GetProperty("favoriteColor").GetString().Should().Be("teal");
		raw.RootElement.GetProperty("roles")[1].GetProperty("slug").GetString().Should().Be("worker");
	}

	[Fact]
	public void PatchRole_UncheckingSpawnAllowed_RemovesTheBlock_WhenNoOtherSpawnStateRemains()
	{
		var def = AgentDefinitionJson.Parse(DocWithUnknownFields);
		var edit = EditFrom(def.Roles[0]) with { SpawnAllowed = false, SpawnAllowedRoles = [] };

		var patched = AgentDefinitionJson.PatchRole(DocWithUnknownFields, roleIndex: 0, edit);
		using var raw = System.Text.Json.JsonDocument.Parse(patched);
		raw.RootElement.GetProperty("roles")[0].TryGetProperty("spawn", out _).Should().BeFalse(
			"allowed=false with no allowedRoles is the same absent-block shape a role without spawn ever had");
	}

	[Fact]
	public void PatchRole_SettingSpawnAllowed_OnARoleWithNoSpawnBlock_CreatesIt()
	{
		var def = AgentDefinitionJson.Parse(DocWithUnknownFields);
		// Role 1 ("worker") has no `spawn` key at all in the fixture.
		var edit = EditFrom(def.Roles[1]) with { SpawnAllowed = true, SpawnAllowedRoles = ["lead"] };

		var patched = AgentDefinitionJson.PatchRole(DocWithUnknownFields, roleIndex: 1, edit);
		var reparsed = AgentDefinitionJson.Parse(patched);
		reparsed.Roles[1].Spawn.Should().NotBeNull();
		reparsed.Roles[1].Spawn!.Allowed.Should().BeTrue();
		reparsed.Roles[1].Spawn!.AllowedRoles.Should().Equal("lead");
	}

	[Fact]
	public void PatchRole_ClearingNotes_RemovesTheKey_NotWritesEmptyString()
	{
		var def = AgentDefinitionJson.Parse(DocWithUnknownFields);
		var edit = EditFrom(def.Roles[0]) with { Notes = "" };

		var patched = AgentDefinitionJson.PatchRole(DocWithUnknownFields, roleIndex: 0, edit);
		using var raw = System.Text.Json.JsonDocument.Parse(patched);
		raw.RootElement.GetProperty("roles")[0].TryGetProperty("notes", out _).Should().BeFalse();
	}

	[Fact]
	public void PatchRole_NotesGetsLiveNewlines_NotEscapedBackslashN()
	{
		var def = AgentDefinitionJson.Parse(DocWithUnknownFields);
		var edit = EditFrom(def.Roles[0]) with { Notes = "line one\nline two" };

		var patched = AgentDefinitionJson.PatchRole(DocWithUnknownFields, roleIndex: 0, edit);
		var reparsed = AgentDefinitionJson.Parse(patched);
		reparsed.Roles[0].Notes.Should().Be("line one\nline two");
	}

	[Fact]
	public void PatchRole_RejectsAnAttemptAtAnOutOfRangeRoleIndex()
	{
		var act = () => AgentDefinitionJson.PatchRole(DocWithUnknownFields, roleIndex: 5, new RoleFormEdit(
			"x", "worker", [], false, [], false, [], null));
		act.Should().Throw<ArgumentException>().WithMessage("*no longer exists*");
	}

	[Fact]
	public void AddRole_AppendsAMinimalRole_LeavesExistingRolesUntouched()
	{
		var patched = AgentDefinitionJson.AddRole(DocWithUnknownFields, "researcher");
		var reparsed = AgentDefinitionJson.Parse(patched);

		reparsed.Roles.Should().HaveCount(3);
		reparsed.Roles[2].Slug.Should().Be("researcher");
		reparsed.Roles[2].Tier.Should().Be("worker");
		reparsed.Roles[0].Notes.Should().Be("runs the show", "adding a role must not touch existing ones");
	}

	[Fact]
	public void RemoveRole_DropsTheNamedRole_LeavesTheOtherByteForByte()
	{
		var patched = AgentDefinitionJson.RemoveRole(DocWithUnknownFields, roleIndex: 1);
		var reparsed = AgentDefinitionJson.Parse(patched);
		reparsed.Roles.Should().ContainSingle(r => r.Slug == "lead");
	}

	[Fact]
	public void RemoveRole_RefusesToDropTheLastRole()
	{
		var oneRole = """{"name":"solo","roles":[{"slug":"only","tier":"worker","requiredCapabilities":[]}]}""";
		var act = () => AgentDefinitionJson.RemoveRole(oneRole, roleIndex: 0);
		act.Should().Throw<ArgumentException>().WithMessage("*at least one role*");
	}

	// The form can never construct a JSON property literally named "model" — every field it
	// writes is either a known scalar/array VALUE slot (slug, tier, capabilities, notes,
	// allowedRoles/targets) or a fixed key name the form itself controls. Using the literal text
	// "model" as a VALUE (a slug, a note) is legitimate content, not a structural violation, and
	// must NOT be rejected.
	[Fact]
	public void PatchRole_LiteralStringModel_AsAValue_IsNotRejected()
	{
		var def = AgentDefinitionJson.Parse(DocWithUnknownFields);
		var edit = EditFrom(def.Roles[0]) with { Notes = "the word model appears here as prose, not a field" };

		var patched = AgentDefinitionJson.PatchRole(DocWithUnknownFields, roleIndex: 0, edit);
		var act = () => AgentDefinitionJson.Parse(patched); // would throw if RejectModelField mistook this for a key
		act.Should().NotThrow();
	}

	// A document that already carries a role-level `model` cannot legitimately exist (every
	// write path rejects it before storage), but PatchRole must still refuse to launder one back
	// through if it is ever handed one directly — it runs the caller's edit through the same
	// AgentDefinitionJson.Parse gate the raw-JSON textarea uses.
	[Fact]
	public void UpsertPath_StillRejectsModel_EvenWhenOnlyAnUnrelatedRoleIsPatched()
	{
		const string withModel =
			"""{"name":"t","roles":[{"slug":"a","tier":"worker","requiredCapabilities":[],"model":"opus"},{"slug":"b","tier":"worker","requiredCapabilities":[]}]}""";
		var edit = new RoleFormEdit("b", "worker", [], false, [], false, [], null);
		var patched = AgentDefinitionJson.PatchRole(withModel, roleIndex: 1, edit); // role 1 has no model — patch itself succeeds
		var act = () => AgentDefinitionJson.Parse(patched); // but the document STILL carries role[0].model
		act.Should().Throw<ArgumentException>().WithMessage("*model*not allowed*");
	}
}

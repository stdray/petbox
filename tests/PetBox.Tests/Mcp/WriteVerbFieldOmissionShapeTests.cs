using System.Reflection;
using PetBox.Deploy.Contract;
using PetBox.Web.Auth;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Mcp;

// work/patch-vs-put-class-needs-a-mechanical-gate — PART 1 of 2 (structural DTO gate; PART 2,
// the prose gate, is WriteVerbOmissionProseTests). Generalizes the mechanism
// MethodologyKindContractParityTests already proved: reflect over a WIRE CONTRACT record and ask
// one mechanical question, no naming convention, no NLP.
//
// MethodologyKindContractParityTests asked "does the field exist on the input type AT ALL" (the
// four-fields-missing defect). This asks the next question for PATCH-style contracts (a caller
// resends ONE changed field and expects every other field to survive): can the field's own C#
// TYPE even represent "the caller didn't touch this"? A value-type property with a compile-time
// default (`bool Ephemeral = false`) or a non-nullable reference type (`string Tags`) cannot —
// "omitted" and "explicitly sent the default" collapse to the identical value before any merge
// logic ever runs, so the merge layer cannot tell them apart even if it wanted to.
//
// This is exactly the deploy_node_upsert defect named in the parent card: `NodeInput.DisplayName`/
// `.Tags` are plain non-nullable `string`, `.Ephemeral` is plain `bool` — DeployTools.NodeUpsertAsync
// is forced to write `tags ?? ""` / `displayName ?? id` at the call site because the target TYPE has
// nowhere else to put "leave it alone", and an update without an explicit `ephemeral:true` silently
// resets the flag to false. The fix is the same pattern TaskNodeInput.Priority / CommentItemInput.Tags
// / MemoryEntryInputDto.Tags / AgentKeyPatch already use throughout the surface: nullable, with null
// meaning "keep".
//
// OUT OF SCOPE, on purpose:
//   - tasks_methodology_rules_upsert / template_upsert: honest whole-DOCUMENT PUT (the caller
//     resubmits the ENTIRE document every call, by design) — covered by a DIFFERENT failure mode
//     (a field with no slot at all) in MethodologyKindContractParityTests, not this one.
//   - session_upsert: `content` is deliberately the WHOLE payload of an honest PUT (its own
//     description says so); `meta` is already nullable. Nothing to assert here.
//   - tasks_board_set_wire: one nullable string field, already correct, and its omit-vs-clear
//     convention is deliberately INVERTED (see WriteVerbOmissionProseTests) — a shape question,
//     not a nullability one.
public sealed class WriteVerbFieldOmissionShapeTests
{
	// { tool name (for the assertion message only), the CONTRACT record actually handed to the
	// service layer, excluded property names with the reason baked into the comment beside each
	// row }. A newly added *_upsert/*_update tool does NOT get auto-discovered here — same
	// documented trade-off MethodologyKindContractParityTests.Pairs() made: reflection cannot
	// discover WHICH type is "the contract that reaches the service," a human must wire it in,
	// but ThePairTable_CoversTheKnownWireShapes below keeps the table from silently shrinking.
	public static TheoryData<string, Type, string[]> Targets() => new()
	{
		// version = CAS watermark baseline (0 = create) — a sentinel, not a mergeable field.
		// deleted = one-way soft-delete trigger; omitted and explicit-false are the same normal
		// upsert, so there is no "keep vs clear" distinction to lose.
		{ "tasks_upsert", typeof(TaskNodeInput), ["Version", "Deleted"] },
		// version = CAS watermark baseline, same sentinel as above.
		{ "comments_upsert", typeof(CommentItemInput), ["Version"] },
		// deleted = one-way soft-delete trigger, same non-issue as TaskNodeInput.Deleted above.
		{ "memory_upsert", typeof(MemoryEntryInputDto), ["Version", "Deleted"] },
		// key = identity, not a mergeable field (which key gets patched).
		{ "apikey_update", typeof(AgentKeyPatch), ["Key"] },
		// already fully nullable — a green reference instance, not just an exception list.
		{ "llm_config_upsert", typeof(LlmRouterTools.ConfigSetInput), [] },

		// fix/batch4-deploy-patch-semantics (447c148c) landed and made DisplayName/Tags/Ephemeral
		// nullable — no longer excluded, the theory now enforces them like everything else.
		// Id = identity, always required, not mergeable.
		{ "deploy_node_upsert", typeof(NodeInput), ["Id"] },
		// Service/Project/NodeId/ImageDigest = identity/always-required inputs, not mergeable.
		// DesiredState = the tool's own deliberate always-set toggle (`running`, default true),
		// same status as tasks_upsert's `status` — not a silent-merge field.
		// Relocatable/RequiredTags/ConfigTags were the residual, unclosed instance of this same
		// class that fix/batch4-deploy-patch-semantics (447c148c) deliberately left full-PUT —
		// closed by work/deploy-upsert-three-fields-still-full-put: all three are now nullable
		// (omit=keep, same convention as NodeInput.Tags/.Ephemeral), so nothing is excluded here
		// anymore.
		{ "deploy_upsert", typeof(DeploymentInput), ["Id", "Service", "Project", "NodeId", "ImageDigest", "DesiredState"] },

		// work/agent-def-upsert-typed-and-merge-by-role moved this verb OUT of the "honest whole
		// document PUT" exemption this table used to list it under: `definition` (an untyped
		// whole-document replace) became `roles[]`, a real per-role PATCH, so the same mechanical
		// question now applies to it. Slug = identity (which role the edit addresses); Deleted =
		// the one-way removal trigger, where omitted and explicit-false are the same normal upsert.
		{ "agent_def_upsert", typeof(AgentDefRoleInput), ["Slug", "Deleted"] },
	};

	[Theory]
	[MemberData(nameof(Targets))]
	public void MergeableFieldsCanRepresentOmitted(string tool, Type contract, string[] excluded)
	{
		var offenders = contract
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(p => p.CanRead && p.SetMethod is { IsPublic: true })
			.Where(p => !excluded.Contains(p.Name, StringComparer.Ordinal))
			.Where(p => !IsNullable(p))
			.Select(p => $"{p.Name} ({p.PropertyType.Name})")
			.ToList();

		offenders.Should().BeEmpty(
			$"{tool}'s {contract.Name} carries a PATCH surface — every field a caller can omit must "
			+ "be a NULLABLE type so the merge layer can tell 'omitted' apart from 'explicitly sent "
			+ "the default' (work/patch-vs-put-class-needs-a-mechanical-gate). Non-nullable: "
			+ string.Join(", ", offenders));
	}

	// Same non-vacuity guard MethodologyKindContractParityTests.ThePairTable_CoversTheKnownWireShapes
	// uses: an empty/shrunk table would make the theory above pass for nothing.
	[Fact]
	public void TheTargetTable_CoversTheKnownWriteVerbs()
	{
		var tools = Targets().Select(row => row.Data.Item1).ToList();
		tools.Should().Contain([
			"tasks_upsert", "comments_upsert", "memory_upsert", "apikey_update", "llm_config_upsert",
			"deploy_node_upsert", "deploy_upsert", "agent_def_upsert",
		]);
	}

	static bool IsNullable(PropertyInfo p)
	{
		if (p.PropertyType.IsValueType)
			return Nullable.GetUnderlyingType(p.PropertyType) is not null;

		var info = new NullabilityInfoContext().Create(p);
		return info.WriteState == NullabilityState.Nullable || info.ReadState == NullabilityState.Nullable;
	}
}

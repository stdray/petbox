using PetBox.Tasks.Workflow;

namespace PetBox.Tests.Tasks;

// Pure unit tests for the preset workflows + engine (no DB / no host). Resolution goes
// through the preset definitions (MethodologyPresets) exactly like the service does:
// resolve the (kind, type) workflow, then validate with the resolution-agnostic engine.
public sealed class WorkflowEngineTests
{
	static WorkflowResult Validate(
		BoardKind kind, string? type, string? fromSlug, string toSlug,
		bool enforceApproval = false, bool actorCanApprove = false, bool hasReason = true) =>
		WorkflowEngine.Validate(MethodologyPresets.For(kind, type), kind.ToString().ToLowerInvariant(),
			MethodologyPresets.ValidTypes(kind), type, fromSlug, toSlug, enforceApproval, actorCanApprove, hasReason);

	public static IEnumerable<object[]> AllKinds =>
		[[BoardKind.Simple], [BoardKind.Classic], [BoardKind.Spec], [BoardKind.Ideas], [BoardKind.Intake], [BoardKind.Work]];

	[Theory]
	[MemberData(nameof(AllKinds))]
	public void Preset_Graphs_AreWellFormed(BoardKind kind)
	{
		foreach (var wf in MethodologyPresets.Types(kind))
		{
			wf.Statuses.Should().NotBeEmpty();
			wf.Has(wf.Initial).Should().BeTrue($"{kind}/{wf.Type}'s initial status must itself be one of its own declared statuses");
			wf.Statuses.Select(s => s.Slug).Should().OnlyHaveUniqueItems();
			foreach (var t in wf.Transitions)
			{
				wf.Has(t.From).Should().BeTrue($"transition source '{t.From}' must be a known status of {kind}/{wf.Type}");
				wf.Has(t.To).Should().BeTrue($"transition target '{t.To}' must be a known status of {kind}/{wf.Type}");
			}
			wf.Statuses.Any(s => s.Kind is StatusKind.TerminalOk or StatusKind.TerminalCancel)
				.Should().BeTrue($"{kind}/{wf.Type} must have at least one terminal status");
		}
	}

	[Fact]
	public void Free_HasPreset_FreeTransitions_RejectsUnknownStatus()
	{
		// Simple carries a real preset workflow; type is a label within its fixed vocabulary — it
		// does not BRANCH the FSM, so any in-vocab type resolves the same one.
		var untyped = MethodologyPresets.For(BoardKind.Simple, null);
		untyped.Should().NotBeNull();
		var typed = MethodologyPresets.For(BoardKind.Simple, "chore")!;
		typed.Statuses.Should().Equal(untyped!.Statuses);
		typed.Transitions.Should().Equal(untyped.Transitions);

		// Simple is a STRICT data preset (behavior-narrowing, stage2/simple-narrow): an
		// out-of-vocabulary type resolves to null, same as any other preset kind.
		MethodologyPresets.For(BoardKind.Simple, "anything").Should().BeNull();

		// Initial + free transitions: any valid status → any valid status (even straight to terminal).
		Validate(BoardKind.Simple, null, null, "Todo").Ok.Should().BeTrue("Simple's free transitions allow entry directly into any valid status");
		Validate(BoardKind.Simple, null, "Todo", "Done").Ok.Should().BeTrue("Simple's free transitions allow any valid status to any other, including straight to a terminal");
		Validate(BoardKind.Simple, null, "Done", "InProgress").Ok.Should().BeTrue("Simple's free transitions allow leaving a terminal status too — nothing is closed for good at this kind");

		// An out-of-vocab status is rejected, naming the valid set.
		var bad = Validate(BoardKind.Simple, null, null, "literally-anything");
		bad.Ok.Should().BeFalse("an out-of-vocabulary target status must be rejected, not silently accepted");
		bad.Error.Should().Contain("Todo");

		// Legacy tolerance: an unchanged (carried-over) out-of-vocab status still passes — only a
		// CHANGE to an invalid status is rejected (lets pre-migration nodes be edited).
		Validate(BoardKind.Simple, null, "Pending", "Pending").Ok.Should().BeTrue("an unchanged legacy status must stay editable even though it is not in the current vocabulary");
	}

	[Fact]
	public void Work_Membership_And_Transitions()
	{
		Validate(BoardKind.Work, "feature", null, "Pending").Ok.Should().BeTrue("a feature must be creatable directly into Pending");
		Validate(BoardKind.Work, "feature", "Pending", "InProgress").Ok.Should().BeTrue("Pending→InProgress is a declared edge of the feature FSM");

		var noEdge = Validate(BoardKind.Work, "feature", "Pending", "Done");
		noEdge.Ok.Should().BeFalse("Pending→Done has no declared edge for feature and must be rejected");
		noEdge.Error.Should().Contain("InProgress"); // names valid next statuses
	}

	[Fact]
	public void Work_InvalidStatus_ListsValid()
	{
		var r = Validate(BoardKind.Work, "feature", null, "banana");
		r.Ok.Should().BeFalse("an unknown target status must be rejected");
		r.Error.Should().Contain("Pending");
	}

	[Fact]
	public void Work_Chore_SharesFeatureBugFsm()
	{
		// chore is a first-class work type whose FSM is IDENTICAL to feature/bug —
		// same status vocabulary, same edges, same Review→Done approve gate.
		var chore = MethodologyPresets.For(BoardKind.Work, "chore");
		chore.Should().NotBeNull();
		var feature = MethodologyPresets.For(BoardKind.Work, "feature")!;
		chore!.Statuses.Should().Equal(feature.Statuses);
		chore.Transitions.Should().Equal(feature.Transitions);
		chore.Transitions.Should().Contain(new WorkflowTransition("Review", "Done", RequiresApproval: true));

		Validate(BoardKind.Work, "chore", null, "Pending").Ok.Should().BeTrue("chore must share feature/bug's Pending entry edge");
		Validate(BoardKind.Work, "chore", "Pending", "InProgress").Ok.Should().BeTrue("chore must share feature/bug's Pending→InProgress edge");
		Validate(BoardKind.Work, "chore", "InProgress", "Review").Ok.Should().BeTrue("chore must share feature/bug's InProgress→Review edge");
		Validate(BoardKind.Work, "chore", "Pending", "Done").Ok.Should().BeFalse("no Pending→Done shortcut for chores either");
	}

	[Fact]
	public void Work_MissingType_IsRejectedWithValidTypes()
	{
		var r = Validate(BoardKind.Work, null, null, "Pending");
		r.Ok.Should().BeFalse("work requires an explicit type — an untyped node must be rejected, not silently defaulted");
		r.Error.Should().Contain("feature");
	}

	[Fact]
	public void ApproveGate_IsCapability_OffByDefault_OnWhenEnforced()
	{
		// default: NOT enforced (v1) — an agent can reach Done
		Validate(BoardKind.Work, "feature", "Review", "Done").Ok.Should().BeTrue("the approve gate is off by default — an agent must be able to reach Done without an approver");

		// enforced + cannot approve → blocked; enforced + can approve → ok
		Validate(BoardKind.Work, "feature", "Review", "Done", enforceApproval: true, actorCanApprove: false).Ok.Should().BeFalse("when the approve gate is enforced, an actor without approval rights must be blocked");
		Validate(BoardKind.Work, "feature", "Review", "Done", enforceApproval: true, actorCanApprove: true).Ok.Should().BeTrue("when the approve gate is enforced, an actor who can approve must be let through");
	}

	[Fact]
	public void Intake_RequiresReason_ForWontFix()
	{
		Validate(BoardKind.Intake, "issue", "triage", "wontfix", hasReason: false).Ok.Should().BeFalse("wontfix requires a reason — without one the transition must be refused");
		Validate(BoardKind.Intake, "issue", "triage", "wontfix", hasReason: true).Ok.Should().BeTrue("wontfix with a reason supplied must be allowed");
	}

	[Fact]
	public void Spec_And_Ideas_BasicFlow()
	{
		Validate(BoardKind.Spec, null, null, "defined").Ok.Should().BeTrue("a spec node must be creatable directly into defined");
		Validate(BoardKind.Ideas, null, "raw", "exploring").Ok.Should().BeTrue("raw→exploring is the ideas FSM's entry edge");
		Validate(BoardKind.Ideas, null, "raw", "accepted").Ok.Should().BeFalse("an idea cannot skip straight from raw to accepted — it must go through exploring/review first");
	}

	[Fact]
	public void UnchangedStatus_IsAllowed_EvenIfInvalidForKind()
	{
		// A node carrying a legacy/invalid-for-kind status (e.g. "Pending" left by an older
		// creation path on an ideas board) must stay editable: an upsert that doesn't change
		// the status should not re-litigate it. (Bug #2.)
		Validate(BoardKind.Ideas, "idea", "Pending", "Pending").Ok.Should().BeTrue("a legacy status left by an older creation path must remain editable when unchanged, even though it's invalid for this kind now");
		Validate(BoardKind.Spec, "spec", "Pending", "Pending").Ok.Should().BeTrue("a legacy status left by an older creation path must remain editable when unchanged, even though it's invalid for this kind now");
	}

	[Fact]
	public void RecoverFromUnknownStatus_ToValidStatus_IsAllowed()
	{
		// Moving OUT of an unknown current status into a valid one is recovery, not a transition.
		Validate(BoardKind.Ideas, "idea", "Pending", "raw").Ok.Should().BeTrue("moving out of an unknown current status into a valid one must be treated as recovery, not blocked as an illegal transition");
		// ...but the target must still be valid for the kind.
		Validate(BoardKind.Ideas, "idea", "Pending", "banana").Ok.Should().BeFalse("recovery out of an unknown status must still land on a valid target status for the kind");
	}

	[Fact]
	public void ParseKind_DefaultsToFree()
	{
		MethodologyPresets.ParseKind(null).Should().Be(BoardKind.Simple);
		MethodologyPresets.ParseKind("garbage").Should().Be(BoardKind.Simple);
		MethodologyPresets.ParseKind("WORK").Should().Be(BoardKind.Work);
	}
}

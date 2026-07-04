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
			wf.Has(wf.Initial).Should().BeTrue();
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
		// Free carries a real preset workflow; type is a label, so For() ignores it (same FSM).
		var untyped = MethodologyPresets.For(BoardKind.Simple, null);
		untyped.Should().NotBeNull();
		var typed = MethodologyPresets.For(BoardKind.Simple, "anything")!;
		typed.Statuses.Should().Equal(untyped!.Statuses);
		typed.Transitions.Should().Equal(untyped.Transitions);

		// Initial + free transitions: any valid status → any valid status (even straight to terminal).
		Validate(BoardKind.Simple, null, null, "Todo").Ok.Should().BeTrue();
		Validate(BoardKind.Simple, null, "Todo", "Done").Ok.Should().BeTrue();
		Validate(BoardKind.Simple, null, "Done", "InProgress").Ok.Should().BeTrue();

		// An out-of-vocab status is rejected, naming the valid set.
		var bad = Validate(BoardKind.Simple, null, null, "literally-anything");
		bad.Ok.Should().BeFalse();
		bad.Error.Should().Contain("Todo");

		// Legacy tolerance: an unchanged (carried-over) out-of-vocab status still passes — only a
		// CHANGE to an invalid status is rejected (lets pre-migration nodes be edited).
		Validate(BoardKind.Simple, null, "Pending", "Pending").Ok.Should().BeTrue();
	}

	[Fact]
	public void Work_Membership_And_Transitions()
	{
		Validate(BoardKind.Work, "feature", null, "Pending").Ok.Should().BeTrue();
		Validate(BoardKind.Work, "feature", "Pending", "InProgress").Ok.Should().BeTrue();

		var noEdge = Validate(BoardKind.Work, "feature", "Pending", "Done");
		noEdge.Ok.Should().BeFalse();
		noEdge.Error.Should().Contain("InProgress"); // names valid next statuses
	}

	[Fact]
	public void Work_InvalidStatus_ListsValid()
	{
		var r = Validate(BoardKind.Work, "feature", null, "banana");
		r.Ok.Should().BeFalse();
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

		Validate(BoardKind.Work, "chore", null, "Pending").Ok.Should().BeTrue();
		Validate(BoardKind.Work, "chore", "Pending", "InProgress").Ok.Should().BeTrue();
		Validate(BoardKind.Work, "chore", "InProgress", "Review").Ok.Should().BeTrue();
		Validate(BoardKind.Work, "chore", "Pending", "Done").Ok.Should().BeFalse("no Pending→Done shortcut for chores either");
	}

	[Fact]
	public void Work_MissingType_IsRejectedWithValidTypes()
	{
		var r = Validate(BoardKind.Work, null, null, "Pending");
		r.Ok.Should().BeFalse();
		r.Error.Should().Contain("feature");
	}

	[Fact]
	public void ApproveGate_IsCapability_OffByDefault_OnWhenEnforced()
	{
		// default: NOT enforced (v1) — an agent can reach Done
		Validate(BoardKind.Work, "feature", "Review", "Done").Ok.Should().BeTrue();

		// enforced + cannot approve → blocked; enforced + can approve → ok
		Validate(BoardKind.Work, "feature", "Review", "Done", enforceApproval: true, actorCanApprove: false).Ok.Should().BeFalse();
		Validate(BoardKind.Work, "feature", "Review", "Done", enforceApproval: true, actorCanApprove: true).Ok.Should().BeTrue();
	}

	[Fact]
	public void Intake_RequiresReason_ForWontFix()
	{
		Validate(BoardKind.Intake, "issue", "triage", "wontfix", hasReason: false).Ok.Should().BeFalse();
		Validate(BoardKind.Intake, "issue", "triage", "wontfix", hasReason: true).Ok.Should().BeTrue();
	}

	[Fact]
	public void Spec_And_Ideas_BasicFlow()
	{
		Validate(BoardKind.Spec, null, null, "defined").Ok.Should().BeTrue();
		Validate(BoardKind.Ideas, null, "raw", "exploring").Ok.Should().BeTrue();
		Validate(BoardKind.Ideas, null, "raw", "accepted").Ok.Should().BeFalse(); // must go through exploring
	}

	[Fact]
	public void UnchangedStatus_IsAllowed_EvenIfInvalidForKind()
	{
		// A node carrying a legacy/invalid-for-kind status (e.g. "Pending" left by an older
		// creation path on an ideas board) must stay editable: an upsert that doesn't change
		// the status should not re-litigate it. (Bug #2.)
		Validate(BoardKind.Ideas, "idea", "Pending", "Pending").Ok.Should().BeTrue();
		Validate(BoardKind.Spec, "spec", "Pending", "Pending").Ok.Should().BeTrue();
	}

	[Fact]
	public void RecoverFromUnknownStatus_ToValidStatus_IsAllowed()
	{
		// Moving OUT of an unknown current status into a valid one is recovery, not a transition.
		Validate(BoardKind.Ideas, "idea", "Pending", "raw").Ok.Should().BeTrue();
		// ...but the target must still be valid for the kind.
		Validate(BoardKind.Ideas, "idea", "Pending", "banana").Ok.Should().BeFalse();
	}

	[Fact]
	public void ParseKind_DefaultsToFree()
	{
		MethodologyPresets.ParseKind(null).Should().Be(BoardKind.Simple);
		MethodologyPresets.ParseKind("garbage").Should().Be(BoardKind.Simple);
		MethodologyPresets.ParseKind("WORK").Should().Be(BoardKind.Work);
	}
}

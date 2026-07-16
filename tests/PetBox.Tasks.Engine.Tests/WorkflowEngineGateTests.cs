using static PetBox.Tasks.Engine.Tests.EngineFixture;

namespace PetBox.Tasks.Engine.Tests;

// WorkflowEngine.Validate — the single validation point for status/transitions — at the level the
// rest of this suite works at: EXACT error strings (condition 5) and the gate mechanics that the
// older preset-shaped tests in tests/PetBox.Tests/Tasks/WorkflowEngineTests.cs assert only by
// Ok/Contain. Resolution runs through MethodologyRuntime, the way the service resolves it, so the
// data-declared gates (schema v2's per-transition EnforceApproval) are exercised on the same seam
// the presets flow through.
public sealed class WorkflowEngineGateTests
{
	static WorkflowResult Validate(
		MethodologyRuntime runtime, string kindSlug, string? type, string? from, string to,
		bool enforceApproval = false, bool actorCanApprove = false, bool hasReason = true) =>
		WorkflowEngine.Validate(runtime.For(kindSlug, type), runtime.KindName(kindSlug),
			runtime.ValidTypes(kindSlug), type, from, to, enforceApproval, actorCanApprove, hasReason);

	static WorkflowResult Work(string? type, string? from, string to,
		bool enforceApproval = false, bool actorCanApprove = false, bool hasReason = true) =>
		Validate(Quartet, "work", type, from, to, enforceApproval, actorCanApprove, hasReason);

	// ---- Transitions ----

	[Fact]
	public void AKnownEdge_Passes() => Work("feature", "Pending", "InProgress").Should().Be(WorkflowResult.Success);

	[Fact]
	public void Creation_IntoAnOrdinaryStatus_Passes() => Work("feature", null, "Pending").Ok.Should().BeTrue();

	[Fact]
	public void AMissingEdge_IsRefused_ListingTheReachableStatuses()
	{
		var r = Work("feature", "Pending", "Done");
		r.Ok.Should().BeFalse();
		r.Error.Should().Be("no transition 'Pending' -> 'Done'; from 'Pending' you can go to: InProgress|Cancelled");
	}

	[Fact]
	public void AnOutOfVocabTarget_IsRefused_ListingTheVocabulary()
	{
		var r = Work("feature", "Pending", "banana");
		r.Error.Should().Be("invalid status 'banana' for work/feature; valid: Pending|InProgress|Review|Done|Blocked|Cancelled");
	}

	[Fact]
	public void AKindThatNeedsAType_IsRefused_ListingTheValidTypes()
	{
		var r = Work(null, null, "Pending");
		r.Error.Should().Be("board kind 'work' needs a known type (feature|bug|chore); got ''");
	}

	[Fact]
	public void AnUnknownType_IsRefused_TheSameWay() =>
		Work("banana", null, "Pending").Error.Should().Be("board kind 'work' needs a known type (feature|bug|chore); got 'banana'");

	// ---- The three tolerances: unchanged status, recovery, and their boundary ----

	[Fact]
	public void AnUnchangedStatus_IsNeverReLitigated()
	{
		// Short-circuits before the workflow is even resolved — so an out-of-vocab legacy status
		// (and even a kind with no resolvable type) stays editable.
		Work("feature", "Pending", "Pending").Should().Be(WorkflowResult.Success);
		Work("feature", "legacy-status", "legacy-status").Ok.Should().BeTrue();
		Work(null, "whatever", "whatever").Ok.Should().BeTrue("the type isn't even resolved on this path");
		Work("feature", "PENDING", "pending").Ok.Should().BeTrue("status matching is case-insensitive");
	}

	[Fact]
	public void RecoveryFromAnUnknownStatus_IsAFreshStart_ButTheTargetMustBeValid()
	{
		Work("feature", "legacy-status", "Review").Ok.Should().BeTrue("an unknown `from` is treated as creation");
		Work("feature", "legacy-status", "banana").Ok.Should().BeFalse("the target vocabulary still applies");
	}

	// ---- The reason gate ----

	[Fact]
	public void ATransitionRequiringAReason_IsRefusedWithoutOne()
	{
		var r = Validate(Quartet, "intake", "issue", "triage", "wontfix", hasReason: false);
		r.Error.Should().Be("transition 'triage' -> 'wontfix' requires a reason (provide a non-empty reason field on this call)");
	}

	[Fact]
	public void ATransitionRequiringAReason_PassesWithOne() =>
		Validate(Quartet, "intake", "issue", "triage", "wontfix", hasReason: true).Ok.Should().BeTrue();

	[Fact]
	public void TheReasonGate_IsIndependentOfApproval() =>
		Validate(Quartet, "ideas", "idea", "exploring", "rejected", hasReason: false).Error
			.Should().Be("transition 'exploring' -> 'rejected' requires a reason (provide a non-empty reason field on this call)");

	// ---- The approve gate: a capability with two independent switches ----

	[Fact]
	public void ThePresetApproveGate_IsNotEnforcedByDefault()
	{
		// RequiresApproval marks the edge maintainer-only by CONVENTION; the presets declare no
		// EnforceApproval, so the server does not block. This is the v1 posture and it is live
		// behavior, not an accident.
		Work("feature", "Review", "Done").Ok.Should().BeTrue();
		Quartet.For("work", "feature")!.Transition("Review", "Done")!.RequiresApproval.Should().BeTrue();
		Quartet.For("work", "feature")!.Transition("Review", "Done")!.EnforceApproval.Should().BeFalse();
	}

	[Fact]
	public void TheGlobalEnforceApprovalFlag_TurnsTheGateOn()
	{
		Work("feature", "Review", "Done", enforceApproval: true, actorCanApprove: false).Error
			.Should().Be("transition 'Review' -> 'Done' requires maintainer approval");
		Work("feature", "Review", "Done", enforceApproval: true, actorCanApprove: true).Ok.Should().BeTrue();
	}

	[Fact]
	public void APerTransitionEnforcedGate_BlocksWithoutTheGlobalFlag()
	{
		// Schema v2: a definition opts INTO enforcement per transition. Either switch demands the
		// capability — they are ORed, not ANDed.
		Validate(EnforcedRuntime, "gated", "item", "open", "approved", actorCanApprove: false).Error
			.Should().Be("transition 'open' -> 'approved' requires maintainer approval");
		Validate(EnforcedRuntime, "gated", "item", "open", "approved", actorCanApprove: true).Ok.Should().BeTrue();
	}

	[Fact]
	public void AnUngatedEdgeOfAGatedWorkflow_IsUnaffected() =>
		Validate(EnforcedRuntime, "gated", "item", "open", "dropped", actorCanApprove: false).Ok.Should().BeTrue();

	// ---- Birth into a gated status: the gate can't be bypassed by creating the node there ----

	[Fact]
	public void UnderTheGlobalFlag_BirthIntoAnyTerminalOkStatus_IsMaintainerOnly()
	{
		Work("feature", null, "Done", enforceApproval: true, actorCanApprove: false).Error
			.Should().Be("only a maintainer can set status 'Done'");
		Work("feature", null, "Done", enforceApproval: true, actorCanApprove: true).Ok.Should().BeTrue();
		Work("feature", null, "Cancelled", enforceApproval: true, actorCanApprove: false).Ok
			.Should().BeTrue("TerminalCancel is not the approve gate");
		Work("feature", null, "Done", enforceApproval: false, actorCanApprove: false).Ok
			.Should().BeTrue("without the flag, birth-into-Done is the historical v1 posture");
	}

	[Fact]
	public void UnderPerTransitionEnforcement_BirthIntoTheGatedTARGET_IsMaintainerOnly()
	{
		// Note the difference from the global flag: what's gated here is being the TARGET of an
		// enforced transition, not being TerminalOk. `approved` is Open, and still gated at birth.
		Validate(EnforcedRuntime, "gated", "item", null, "approved", actorCanApprove: false).Error
			.Should().Be("only a maintainer can set status 'approved'");
		Validate(EnforcedRuntime, "gated", "item", null, "approved", actorCanApprove: true).Ok.Should().BeTrue();
		Validate(EnforcedRuntime, "gated", "item", null, "dropped", actorCanApprove: false).Ok
			.Should().BeTrue("a TerminalOk status that no ENFORCED transition targets is not gated at birth");
	}

	[Fact]
	public void RecoveryIntoAGatedStatus_CountsAsBirth_AndIsGated() =>
		Validate(EnforcedRuntime, "gated", "item", "legacy-status", "approved", actorCanApprove: false).Error
			.Should().Be("only a maintainer can set status 'approved'");

	// `approved` is Open (so the global TerminalOk rule can't explain a refusal) and is reached by
	// an ENFORCED approval edge; `dropped` is TerminalOk but reached by an ordinary one.
	static readonly MethodologyRuntime EnforcedRuntime = MethodologyRuntime.From(new MethodologyDefinition("enforced",
	[
		new MethodologyKindDef("gated", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["item"],
				[
					new WorkflowStatus("open", "Open", StatusKind.Open),
					new WorkflowStatus("approved", "Approved", StatusKind.Open),
					new WorkflowStatus("dropped", "Dropped", StatusKind.TerminalOk),
				],
				[
					new MethodologyTransitionDef("open", "approved", RequiresApproval: true) { EnforceApproval = true },
					new MethodologyTransitionDef("open", "dropped"),
				]),
		]),
	]));
}

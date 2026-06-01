using PetBox.Tasks.Workflow;

namespace PetBox.Tests.Tasks;

// Pure unit tests for the workflow catalog + engine (no DB / no host).
public sealed class WorkflowEngineTests
{
	public static IEnumerable<object[]> AllKinds =>
		[[BoardKind.Spec], [BoardKind.Ideas], [BoardKind.Intake], [BoardKind.Work]];

	[Theory]
	[MemberData(nameof(AllKinds))]
	public void Catalog_Graphs_AreWellFormed(BoardKind kind)
	{
		foreach (var wf in WorkflowCatalog.Types(kind))
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
	public void Free_AcceptsAnything()
	{
		WorkflowCatalog.For(BoardKind.Free, null).Should().BeNull();
		WorkflowEngine.Validate(BoardKind.Free, null, null, "literally-anything").Ok.Should().BeTrue();
	}

	[Fact]
	public void Work_Membership_And_Transitions()
	{
		WorkflowEngine.Validate(BoardKind.Work, "feature", null, "Pending").Ok.Should().BeTrue();
		WorkflowEngine.Validate(BoardKind.Work, "feature", "Pending", "InProgress").Ok.Should().BeTrue();

		var noEdge = WorkflowEngine.Validate(BoardKind.Work, "feature", "Pending", "Done");
		noEdge.Ok.Should().BeFalse();
		noEdge.Error.Should().Contain("InProgress"); // names valid next statuses
	}

	[Fact]
	public void Work_InvalidStatus_ListsValid()
	{
		var r = WorkflowEngine.Validate(BoardKind.Work, "feature", null, "banana");
		r.Ok.Should().BeFalse();
		r.Error.Should().Contain("Pending");
	}

	[Fact]
	public void Work_MissingType_IsRejectedWithValidTypes()
	{
		var r = WorkflowEngine.Validate(BoardKind.Work, null, null, "Pending");
		r.Ok.Should().BeFalse();
		r.Error.Should().Contain("feature");
	}

	[Fact]
	public void ApproveGate_IsCapability_OffByDefault_OnWhenEnforced()
	{
		// default: NOT enforced (v1) — an agent can reach Done
		WorkflowEngine.Validate(BoardKind.Work, "feature", "Review", "Done").Ok.Should().BeTrue();

		// enforced + cannot approve → blocked; enforced + can approve → ok
		WorkflowEngine.Validate(BoardKind.Work, "feature", "Review", "Done", enforceApproval: true, actorCanApprove: false).Ok.Should().BeFalse();
		WorkflowEngine.Validate(BoardKind.Work, "feature", "Review", "Done", enforceApproval: true, actorCanApprove: true).Ok.Should().BeTrue();
	}

	[Fact]
	public void Intake_RequiresReason_ForWontFix()
	{
		WorkflowEngine.Validate(BoardKind.Intake, "issue", "triage", "wontfix", hasReason: false).Ok.Should().BeFalse();
		WorkflowEngine.Validate(BoardKind.Intake, "issue", "triage", "wontfix", hasReason: true).Ok.Should().BeTrue();
	}

	[Fact]
	public void Spec_And_Ideas_BasicFlow()
	{
		WorkflowEngine.Validate(BoardKind.Spec, null, null, "defined").Ok.Should().BeTrue();
		WorkflowEngine.Validate(BoardKind.Ideas, null, "raw", "exploring").Ok.Should().BeTrue();
		WorkflowEngine.Validate(BoardKind.Ideas, null, "raw", "accepted").Ok.Should().BeFalse(); // must go through exploring
	}

	[Fact]
	public void ParseKind_DefaultsToFree()
	{
		WorkflowCatalog.ParseKind(null).Should().Be(BoardKind.Free);
		WorkflowCatalog.ParseKind("garbage").Should().Be(BoardKind.Free);
		WorkflowCatalog.ParseKind("WORK").Should().Be(BoardKind.Work);
	}
}

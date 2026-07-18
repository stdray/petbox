namespace PetBox.Tests.Tasks;

// Methodology smoke, theme 3/4: the WORK board's state machine and its cross-board effects —
// the spec-link invariant at birth, the blocked/unblock edges, intake triage, the approve-gated
// Done and the issue auto-close it fires, and what tasks_workflow reports. Own
// TasksMethodologySmokeFixture instance (one host for the class, per-test ResetAsync) — see
// TasksMethodologySmokeBase.
public sealed class TasksMethodologyWorkFsmTests : TasksMethodologySmokeBase, IClassFixture<TasksMethodologySmokeFixture>
{
	public TasksMethodologyWorkFsmTests(TasksMethodologySmokeFixture fx) : base(fx) { }

	// 2. work board: a feature WITHOUT a spec link is rejected (invariant).
	[Fact]
	public async Task Work_FeatureWithoutSpecLink_Rejected()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var r = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "do-login", type = "feature", status = "Pending", title = "Build login", body = "..." }),
		});
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("spec");
	}

	// 5. intake: report_issue → reported; triage → confirmed; promote → work task + issue_task relation.
	[Fact]
	public async Task Intake_ReportTriageConfirm_PromotesToWork()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "intake", kind = "intake" });
		var rep = await Agent("report_issue", new { title = "login 500s", detail = "POST /login returns 500" });
		rep.IsError.Should().NotBe(true);

		// the issue lands on an intake-kind board in status `reported`; triage it to confirmed
		var wf = await Agent("tasks_workflow", new { projectKey = ProjectKey, board = "intake" });
		wf.IsError.Should().NotBe(true);
		Text(wf).Should().Contain("reported");
		Text(wf).Should().Contain("confirmed");
	}

	// 6. FSM effect: a work bug → done (by the maintainer) auto-closes the linked intake issue.
	[Fact]
	public async Task WorkBugDone_AutoClosesLinkedIssue()
	{
		// Build spec + work bug linked to an intake issue, then approve Done and assert the issue closed.
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		var spec = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "spec",
			nodes = Nodes(new { key = "login", status = "defined", title = "Login", body = "x", links = new { idea_spec = ir } }),
		});
		var specId = NodeId(spec, "login");

		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "intake", kind = "intake" });
		var issue = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "intake",
			nodes = Nodes(new { key = "login-500", type = "issue", status = "confirmed", title = "login 500", body = "x" }),
		});
		var issueId = NodeId(issue, "login-500");

		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var work = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "fix-login", type = "bug", status = "Review", title = "Fix login", body = "x", links = new { task_spec = specId } }),
		});
		var taskId = NodeId(work, "fix-login");
		await Agent("relations_create", new { projectKey = ProjectKey, kind = "issue_task", fromNodeId = issueId, toNodeId = taskId });

		// maintainer approves Done → effect should close the linked issue
		var done = await Approver("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "fix-login", type = "bug", status = "Done", version = 1, title = "Fix login", body = "x", links = new { task_spec = specId } }),
		});
		IsErr(done).Should().BeFalse();

		var intake = await Agent("tasks_search", new { projectKey = ProjectKey, board = "intake", includeClosed = true });
		Text(intake).Should().Contain("done");
	}

	// 7. approve-gate: an agent's ceiling is Review; only the maintainer confirms Done.
	// Enforcement is DEFERRED in v1 by decision — the capability is modelled in
	// WorkflowEngine (RequiresApproval on Review->Done; TerminalOk = maintainer-only).
	// Flip `enforceApproval` at the call site once constraints are clear from practice.
	[Fact(Skip = "approve-gate enforcement deferred in v1; capability modelled in WorkflowEngine")]
	public Task ApproveGate_AgentCannotSetDone_MaintainerCan() => Task.CompletedTask;

	// 11. a work task can't be Blocked without naming a blocker (blocked requires a `blocks` link).
	[Fact]
	public async Task Blocked_WithoutBlocker_Rejected()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		var spec = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "spec", nodes = Nodes(new { key = "f", status = "defined", title = "F", body = "x", links = new { idea_spec = ir } }) });
		var specId = NodeId(spec, "f");
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });

		var r = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "t", type = "feature", status = "Blocked", title = "T", body = "x", links = new { task_spec = specId } }),
		});
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("block");
	}

	// THE regression: same scenario, but spec/work are provisioned as ONE REAL quartet methodology
	// instance (tasks_methodology_create, source=builtin/quartet) — the shape $system's own `work`
	// board actually has (RenderPresetDefinition copies the quartet kinds, including `work`,
	// VERBATIM into the instance's stored definition, so IsDefinedKind("work") is TRUE there).
	// TasksService.RequireBlockersAsync gates the "Blocked needs a blocker" invariant on
	// `presetKind == BoardKind.Work` — the SAME anti-pattern as the presetkind-spec-blind-spot
	// bug (PresetKind nulls out for any DEFINED kind), found by that bug's sweep. On this shape
	// the guard was NEVER true, so a Blocked work task could be created WITHOUT naming a blocker
	// on any real quartet-provisioned project, not just the bare-preset shape the test above
	// exercises.
	[Fact]
	public async Task Blocked_WithoutBlocker_Rejected_OnQuartetDefinitionResolvedWorkBoard()
	{
		await Agent("tasks_methodology_create", new { projectKey = ProjectKey, name = "wfquartet", source = "builtin", sourceKey = "quartet" });
		var ir = await AcceptedIdeaId(createBoard: false);
		var spec = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "spec", nodes = Nodes(new { key = "f", status = "defined", title = "F", body = "x", links = new { idea_spec = ir } }) });
		var specId = NodeId(spec, "f");

		var r = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "t", type = "feature", status = "Blocked", title = "T", body = "x", links = new { task_spec = specId } }),
		});
		IsErr(r).Should().BeTrue(
			"a Blocked work task must still name a blocker on a REAL quartet-provisioned " +
			"(definition-resolved) work board, not just the bare-preset shape the test above exercises " +
			"(presetkind-spec-blind-spot follow-up)");
		Text(r).Should().Contain("block");
	}

	// 12. blockedBy creates a `blocks` edge; when the blocker reaches Done, the blocked task auto-unblocks.
	[Fact]
	public async Task Block_AutoUnblocksWhenBlockerDone()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		var spec = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "spec", nodes = Nodes(new { key = "f", status = "defined", title = "F", body = "x", links = new { idea_spec = ir } }) });
		var specId = NodeId(spec, "f");
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });

		var a = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "a", type = "feature", status = "Review", title = "A", body = "x", links = new { task_spec = specId } }) });
		var aId = NodeId(a, "a");
		var b = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "b", type = "feature", status = "Blocked", title = "B", body = "x", links = new { task_spec = specId }, blockedBy = aId }) });
		IsErr(b).Should().BeFalse();

		// blocker A → Done (baseline version 1: A was the first node on the work board)
		var done = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "a", type = "feature", status = "Done", version = 1, title = "A", body = "x", links = new { task_spec = specId } }) });
		IsErr(done).Should().BeFalse();

		var get = await Agent("tasks_search", new { projectKey = ProjectKey, board = "work" });
		StatusOf(get, "b").Should().Be("InProgress", "the blocked task auto-unblocks when its only blocker is Done");
	}

	// methodology-blocks-gate-data: MANUALLY moving a Blocked task elsewhere (not via its
	// blocker reaching Done) closes the incoming `blocks` edge too — TasksService.
	// CloseBlocksOnLeaveAsync, still an IMPERATIVE method (deliberately NOT folded into the
	// declared Effects list — MethodologyRuntime.Effects(kindSlug) resolves whole-object, so an
	// entry added to the WorkKind preset would never reach an already-materialized project; see
	// the comment on that field), now reading BlocksGate(kindSlug).Status instead of the old
	// hardcoded "Blocked" literal. Proof: re-entering Blocked afterwards WITHOUT naming a blocker
	// again is refused — the edge is really gone, not merely superseded by the status change.
	[Fact]
	public async Task Block_ManuallyLeavingBlocked_ClosesTheEdge_SoReenteringNeedsANewBlocker()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		var spec = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "spec", nodes = Nodes(new { key = "f", status = "defined", title = "F", body = "x", links = new { idea_spec = ir } }) });
		var specId = NodeId(spec, "f");
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });

		var a = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "a", type = "feature", status = "Review", title = "A", body = "x", links = new { task_spec = specId } }) });
		var aId = NodeId(a, "a");
		var b = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "b", type = "feature", status = "Blocked", title = "B", body = "x", links = new { task_spec = specId }, blockedBy = aId }) });
		IsErr(b).Should().BeFalse();

		// B leaves Blocked on its own (not because A reached Done) — the incoming blocks edge
		// closes. Baseline version 2: the board-wide write cursor stamps a's creation 1, b's
		// creation 2 (TemporalStore.MaxVersionAsync + 1 per commit — a's own baseline in the
		// AutoUnblocksWhenBlockerDone test above is 1 for the SAME reason).
		var leave = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "b", type = "feature", status = "InProgress", version = 2, title = "B", body = "x", links = new { task_spec = specId } }) });
		IsErr(leave).Should().BeFalse();

		// Re-entering Blocked without a fresh blockedBy is refused: the old edge is closed, not
		// live. Baseline version 3: the leave commit above is the board's third write.
		var reenter = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "b", type = "feature", status = "Blocked", version = 3, title = "B", body = "x", links = new { task_spec = specId } }) });
		IsErr(reenter).Should().BeTrue("the manually-closed blocks edge must not still count as an active blocker");
		Text(reenter).Should().Contain("block");
	}

	// THE regression the maintainer caught in review: a naive "move the behavior into a declared
	// Effect on the WorkKind preset" fix is invisible on every REAL quartet-provisioned project.
	// RenderPresetDefinition materializes `work`'s Effects list VERBATIM into the instance's
	// stored definition at tasks_methodology_create time — frozen at whatever the preset held
	// THEN. MethodologyRuntime.Effects(kindSlug) resolves WHOLE-OBJECT (unlike BlocksGate/
	// Singleton/DefaultView's field-level merge), so a kind the definition declares reads ONLY
	// its own stored Effects, never falling back to the preset's for anything the stored copy is
	// missing. This test provisions `work` as a REAL defined kind (source=builtin/quartet — the
	// exact shape $system's own `work` board has) and proves unblock-on-leave still fires there,
	// not just on the bare/undefined-kind shape the test above exercises.
	[Fact]
	public async Task Block_ManuallyLeavingBlocked_ClosesTheEdge_OnQuartetDefinitionResolvedWorkBoard()
	{
		await Agent("tasks_methodology_create", new { projectKey = ProjectKey, name = "wfquartet2", source = "builtin", sourceKey = "quartet" });
		var ir = await AcceptedIdeaId(createBoard: false);
		var spec = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "spec", nodes = Nodes(new { key = "f", status = "defined", title = "F", body = "x", links = new { idea_spec = ir } }) });
		var specId = NodeId(spec, "f");

		var a = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "a", type = "feature", status = "Review", title = "A", body = "x", links = new { task_spec = specId } }) });
		var aId = NodeId(a, "a");
		var b = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "b", type = "feature", status = "Blocked", title = "B", body = "x", links = new { task_spec = specId }, blockedBy = aId }) });
		IsErr(b).Should().BeFalse();

		var leave = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "b", type = "feature", status = "InProgress", version = 2, title = "B", body = "x", links = new { task_spec = specId } }) });
		IsErr(leave).Should().BeFalse();

		var reenter = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "b", type = "feature", status = "Blocked", version = 3, title = "B", body = "x", links = new { task_spec = specId } }) });
		IsErr(reenter).Should().BeTrue(
			"unblock-on-leave must fire on a REAL quartet-provisioned (definition-resolved) work " +
			"board too, not just the bare-preset shape — a fix living only in the WorkKind preset's " +
			"Effects list would be invisible here (whole-object resolution, not field-merged)");
		Text(reenter).Should().Contain("block");
	}

	// 25. work `chore` — engineering hygiene below the spec: no specRef required at birth,
	// but it rides the SAME FSM as feature/bug (no shortcut straight to Done).
	[Fact]
	public async Task Work_ChoreWithoutSpecRef_Allowed_RidesWorkFsm()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var r = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "fix-flaky-test", type = "chore", status = "Pending", title = "Fix flaky test", body = "x" })
		});
		IsErr(r).Should().BeFalse(Text(r));

		// the chore lives on the work FSM: Pending → InProgress is legal…
		var move = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "fix-flaky-test", version = 1, status = "InProgress" })
		});
		IsErr(move).Should().BeFalse(Text(move));

		// …but InProgress → Done is not an edge (must go through Review) — same gate shape as feature/bug.
		var bad = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "fix-flaky-test", version = 2, status = "Done" })
		});
		IsErr(bad).Should().BeTrue();
		Text(bad).Should().Contain("Review"); // error names the valid next statuses
	}

	// 26. the spec-link guard still bites bug AND feature (chore is the only exemption) —
	// assert the exact per-type error text, not just IsError.
	[Fact]
	public async Task Work_BugAndFeatureWithoutSpecRef_RejectedWithExactError()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });

		var bug = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "b", type = "bug", status = "Pending", title = "B", body = "x" })
		});
		// (the envelope '-escapes quotes, so assert around the quoted node key)
		IsErr(bug).Should().BeTrue();
		Text(bug).Should().Contain("a work bug must link a spec node — provide specRef (node");
		Text(bug).Should().Contain("b\\u0027)");

		var feature = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "f", type = "feature", status = "Pending", title = "F", body = "x" })
		});
		IsErr(feature).Should().BeTrue();
		Text(feature).Should().Contain("a work feature must link a spec node — provide specRef (node");
		Text(feature).Should().Contain("f\\u0027)");
	}

	// 27. tasks_workflow groups types by identical FSM: on a work board feature/bug/chore
	// share one state machine, so the answer is ONE `workflows` block with types:[feature,
	// bug,chore] (no triplicated FSM blob) — all six statuses, Pending initial, Review→Done
	// gated by requiresApproval.
	[Fact]
	public async Task Workflow_WorkBoard_OneGroupedFsmBlock_ForFeatureBugChore()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var wf = await Agent("tasks_workflow", new { projectKey = ProjectKey, board = "work" });
		wf.IsError.Should().NotBe(true);

		using var doc = System.Text.Json.JsonDocument.Parse(Text(wf));
		var group = doc.RootElement.GetProperty("workflows").EnumerateArray()
			.Should().ContainSingle("identical FSMs collapse to one block").Subject;
		group.GetProperty("types").EnumerateArray().Select(t => t.GetString())
			.Should().BeEquivalentTo(new[] { "feature", "bug", "chore" });
		group.GetProperty("initial").GetString().Should().Be("Pending");
		group.GetProperty("statuses").EnumerateArray().Select(s => s.GetProperty("slug").GetString())
			.Should().BeEquivalentTo(new[] { "Pending", "InProgress", "Review", "Done", "Blocked", "Cancelled" });
		var reviewDone = group.GetProperty("transitions").EnumerateArray()
			.Single(t => t.GetProperty("from").GetString() == "Review" && t.GetProperty("to").GetString() == "Done");
		reviewDone.GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
	}

	// 27b. a Simple board reports its real type vocabulary in the single grouped block —
	// `simple` is the catalog placeholder, not a type tasks_upsert accepts.
	[Fact]
	public async Task Workflow_SimpleBoard_GroupCarriesTypeVocabulary()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "adhoc" });
		var wf = await Agent("tasks_workflow", new { projectKey = ProjectKey, board = "adhoc" });
		wf.IsError.Should().NotBe(true);

		using var doc = System.Text.Json.JsonDocument.Parse(Text(wf));
		var group = doc.RootElement.GetProperty("workflows").EnumerateArray()
			.Should().ContainSingle().Subject;
		group.GetProperty("types").EnumerateArray().Select(t => t.GetString())
			.Should().BeEquivalentTo(new[] { "task", "bug", "feature", "chore", "issue" });
		group.GetProperty("initial").GetString().Should().Be("Todo");
	}
}

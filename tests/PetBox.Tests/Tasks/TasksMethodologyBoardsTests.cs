namespace PetBox.Tests.Tasks;

// Methodology smoke, theme 1/4: BOARD + NODE mechanics — the status vocabulary a board's kind
// enforces, board close/reopen, per-board key isolation, partial upsert, type immutability, and
// what tasks_search hides by default. Own TasksMethodologySmokeFixture instance (one host for the
// class, per-test ResetAsync) — see TasksMethodologySmokeBase for why the siblings don't share one.
public sealed class TasksMethodologyBoardsTests : TasksMethodologySmokeBase, IClassFixture<TasksMethodologySmokeFixture>
{
	public TasksMethodologyBoardsTests(TasksMethodologySmokeFixture fx) : base(fx) { }

	// 9. invalid status for the board's kind is rejected, and the error names the valid next statuses.
	[Fact]
	public async Task InvalidStatus_RejectedWithValidList()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var r = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "t", type = "feature", status = "banana", title = "T", body = "x" }),
		});
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("Pending"); // the error enumerates valid statuses for this kind/type
	}

	// 10. a simple board (default kind) enforces its preset status vocab but allows free
	// transitions (any valid status → any), and the error on an unknown status names the set.
	[Fact]
	public async Task SimpleBoard_EnforcesVocab_FreeTransitions()
	{
		(await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "scratch" })).IsError.Should().NotBe(true);
		// a valid preset status is accepted
		(await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "scratch", nodes = Nodes(new { key = "ok", status = "Todo", title = "OK", body = "x" }) })).IsError.Should().NotBe(true);
		// an out-of-vocab status is rejected, and the error enumerates the valid statuses
		var bad = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "scratch", nodes = Nodes(new { key = "bad", status = "Frobnicate", title = "B", body = "x" }) });
		IsErr(bad).Should().BeTrue();
		Text(bad).Should().Contain("Todo");
		// free transitions: Todo -> Done directly (no approve gate)
		(await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "scratch", nodes = Nodes(new { key = "ok", version = 1, status = "Done" }) })).IsError.Should().NotBe(true);
	}

	// 14. a closed board rejects writes (agents stop writing by inertia); reopen restores writes.
	[Fact]
	public async Task ClosedBoard_RejectsWrites_UntilReopened()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "tmp" });
		(await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "tmp", nodes = Nodes(new { key = "a", status = "Todo", title = "A", body = "x" }) })).IsError.Should().NotBe(true);

		await Agent("tasks_board_close", new { projectKey = ProjectKey, board = "tmp" });
		var blocked = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "tmp", nodes = Nodes(new { key = "b", status = "Todo", title = "B", body = "x" }) });
		IsErr(blocked).Should().BeTrue();
		Text(blocked).Should().Contain("closed");

		await Agent("tasks_board_reopen", new { projectKey = ProjectKey, board = "tmp" });
		(await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "tmp", nodes = Nodes(new { key = "b", status = "Todo", title = "B", body = "x" }) })).IsError.Should().NotBe(true);
	}

	// 15. partial update: a field omitted from upsert keeps its prior value — a status-only
	// change (path + version + status) must not blank title/body/priority.
	[Fact]
	public async Task PartialUpdate_OmittedFieldsPreserved()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "pu" });
		(await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "pu", nodes = Nodes(new { key = "a", status = "Todo", title = "Alpha", body = "BODY", priority = 5 }) })).IsError.Should().NotBe(true);

		// send ONLY path + version + status
		(await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "pu", nodes = Nodes(new { key = "a", version = 1, status = "InProgress" }) })).IsError.Should().NotBe(true);

		var get = await Agent("tasks_search", new { projectKey = ProjectKey, board = "pu" });
		StatusOf(get, "a").Should().Be("InProgress");
		FieldOf(get, "a", "title").Should().Be("Alpha", "omitted title inherits the prior value");
		FieldOf(get, "a", "body").Should().Be("BODY", "omitted body inherits the prior value");
	}

	// 18. tasks_search hides terminal nodes by default; includeClosed=true returns them.
	[Fact]
	public async Task Get_HidesClosedByDefault_IncludeClosedReturns()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "hc" });
		await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "hc",
			nodes = Nodes(
			new { key = "open1", status = "Todo", title = "Open", body = "x" },
			new { key = "done1", status = "Done", title = "Done", body = "x" })
		});

		var def = await Agent("tasks_search", new { projectKey = ProjectKey, board = "hc" });
		Text(def).Should().Contain("open1");
		Text(def).Should().NotContain("done1");

		var all = await Agent("tasks_search", new { projectKey = ProjectKey, board = "hc", includeClosed = true });
		Text(all).Should().Contain("done1");
	}

	// 19. tasks_search surfaces the board kind and the task->spec link inline (resolved to the spec node).
	[Fact]
	public async Task Get_SurfacesKindAndSpecLink()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		var spec = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "spec", nodes = Nodes(new { key = "login", status = "defined", title = "Login", body = "x", ideaRef = ir }) });
		var specId = NodeId(spec, "login");

		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "do-login", type = "feature", status = "Review", title = "Build login", body = "x", specRef = specId }) });

		var get = await Agent("tasks_search", new { projectKey = ProjectKey, board = "work" });
		Text(get).Should().Contain("\"kind\":\"work\"");
		Text(get).Should().Contain(specId);             // the linked spec node id is surfaced
		Text(get).Should().Contain("\"board\":\"spec\""); // resolved to the spec board it lives on
	}

	// 20. a node's type is immutable once set — reclassifying a work feature to a bug is
	// rejected (Phase 2 declarative invariant), exercised end-to-end through the MCP tool.
	[Fact]
	public async Task Work_FeatureType_IsImmutable()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		var spec = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "spec", nodes = Nodes(new { key = "login", status = "defined", title = "Login", body = "x", ideaRef = ir }) });
		var specId = NodeId(spec, "login");

		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		(await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "do-login", type = "feature", status = "Pending", title = "Build login", body = "x", specRef = specId }) }))
			.IsError.Should().NotBe(true);

		// Editing the feature into a bug must be rejected — type can't change after creation.
		var r = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "do-login", type = "bug", version = 1, title = "Build login", body = "x", specRef = specId }) });
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("immutable");
	}

	// 21. boards of a project now share ONE file, partitioned by Board: the same node key
	// in two boards is independent (own node, own version cursor), and editing one leaves
	// the other untouched. Proves the project-level merge keeps boards isolated.
	[Fact]
	public async Task TwoBoards_SameKey_AreIsolated()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "a" });
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "b" });

		var ia = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "a", nodes = Nodes(new { key = "phase-1", status = "Todo", title = "A node", body = "x" }) });
		IsErr(ia).Should().BeFalse(Text(ia));
		var ib = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "b", nodes = Nodes(new { key = "phase-1", status = "Todo", title = "B node", body = "y" }) });
		IsErr(ib).Should().BeFalse(Text(ib));

		// Same key, different boards → independent rows, no collision.
		FieldOf(await Agent("tasks_search", new { projectKey = ProjectKey, board = "a" }), "phase-1", "title").Should().Be("A node");
		FieldOf(await Agent("tasks_search", new { projectKey = ProjectKey, board = "b" }), "phase-1", "title").Should().Be("B node");

		// Editing A's node (baseline version 1 within board a's cursor) leaves B untouched.
		var edit = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "a", nodes = Nodes(new { key = "phase-1", version = 1, status = "InProgress", title = "A node", body = "x" }) });
		IsErr(edit).Should().BeFalse(Text(edit));
		StatusOf(await Agent("tasks_search", new { projectKey = ProjectKey, board = "a" }), "phase-1").Should().Be("InProgress");
		StatusOf(await Agent("tasks_search", new { projectKey = ProjectKey, board = "b" }), "phase-1").Should().Be("Todo");
	}
}

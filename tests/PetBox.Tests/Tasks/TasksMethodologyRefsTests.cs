using System.Text.Json;

namespace PetBox.Tests.Tasks;

// Methodology smoke, theme 4/4: the REFERENCE surface — specRef / ideaRef in both the NodeId and
// the slug form, the edges they create, the rejections when a ref points at the wrong board or a
// non-existent slug, and the stable-NodeId rename that keeps relations from rotting. Own
// TasksMethodologySmokeFixture instance (one host for the class, per-test ResetAsync) — see
// TasksMethodologySmokeBase.
public sealed class TasksMethodologyRefsTests : TasksMethodologySmokeBase, IClassFixture<TasksMethodologySmokeFixture>
{
	public TasksMethodologyRefsTests(TasksMethodologySmokeFixture fx) : base(fx) { }

	// 3. work feature WITH a spec link: ok, and a task_spec relation is persisted + reverse-resolvable.
	[Fact]
	public async Task Work_FeatureWithSpecLink_CreatesRelation()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		var spec = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "spec",
			nodes = Nodes(new { key = "login", status = "defined", title = "Login", body = "login flow", ideaRef = ir }),
		});
		var specId = NodeId(spec, "login");

		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var work = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "do-login", type = "feature", status = "Pending", title = "Build login", body = "...", specRef = specId }),
		});
		work.IsError.Should().NotBe(true);
		var taskId = NodeId(work, "do-login");

		var rels = await Agent("relations_list", new { projectKey = ProjectKey, nodeId = specId, direction = "to" });
		rels.IsError.Should().NotBe(true);
		Text(rels).Should().Contain(taskId);
	}

	// 4. rename a node (Key changes) → the relation still resolves (NodeId is stable, links don't rot).
	[Fact]
	public async Task Rename_KeepsRelation_ViaStableNodeId()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		var spec = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "spec",
			nodes = Nodes(new { key = "auth", status = "defined", title = "Auth", body = "x", ideaRef = ir }),
		});
		var specId = NodeId(spec, "auth");
		var v = JsonDocument.Parse(Text(spec)).RootElement;

		// rename auth → identity (Key change, same NodeId via prevKey lineage).
		// version = 1 is the baseline the author last saw for "auth".
		var renamed = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "spec",
			nodes = Nodes(new { key = "identity", prevKey = "auth", version = 1, status = "defined", title = "Identity", body = "x", ideaRef = ir }),
		});
		NodeId(renamed, "identity").Should().Be(specId, "rename must preserve the stable NodeId");
	}

	// 16. specRef must point at a spec board: a ref to a non-spec node is rejected.
	[Fact]
	public async Task SpecRef_NonSpecTarget_Rejected()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "notspec" }); // free
		var nf = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "notspec", nodes = Nodes(new { key = "x", status = "Todo", title = "X", body = "x" }) });
		var nonSpecId = NodeId(nf, "x");

		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var r = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "t", type = "feature", status = "Pending", title = "T", body = "x", specRef = nonSpecId }) });
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("not a spec board");
	}

	// 17. a specRef must point at a SPEC board node — a node on a non-spec board is rejected.
	// (The spec kind is now a per-project singleton, so the old two-spec-boards mismatch is
	// unreachable; the meaningful guard is "the target must live on the spec board".)
	[Fact]
	public async Task SpecRef_NonSpecBoardNode_Rejected()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		// A node on a NON-spec (free) board — not a valid spec target.
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "other" });
		var other = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "other", nodes = Nodes(new { key = "r", status = "Todo", title = "R", body = "x" }) });
		var otherId = NodeId(other, "r");

		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" }); // auto-wires to spec

		var r = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "t", type = "feature", status = "Pending", title = "T", body = "x", specRef = otherId }) });
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("not a spec board");
	}

	// 28. specRef accepts the spec node's SLUG (resolved on the board's linked spec board,
	// mirroring partOf) — and the task_spec edge carries the resolved NodeId, not the raw slug.
	// (The NodeId form is covered by test 3, Work_FeatureWithSpecLink_CreatesRelation.)
	[Fact]
	public async Task SpecRef_BySlug_CreatesCorrectTaskSpecEdge()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		var spec = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "spec",
			nodes = Nodes(new { key = "login", status = "defined", title = "Login", body = "x", ideaRef = ir })
		});
		var specId = NodeId(spec, "login");

		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" }); // auto-wires SpecBoard=spec
		var work = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "do-login", type = "feature", status = "Pending", title = "Build login", body = "x", specRef = "login" })
		});
		IsErr(work).Should().BeFalse(Text(work));
		var taskId = NodeId(work, "do-login");

		// edges INTO the spec node: exactly the task_spec edge from the new task — keyed by
		// the resolved NodeId (a raw slug would make this list empty / dangle elsewhere).
		var rels = await Agent("relations_list", new { projectKey = ProjectKey, nodeId = specId, direction = "to" });
		rels.IsError.Should().NotBe(true);
		Text(rels).Should().Contain("task_spec");
		Text(rels).Should().Contain(taskId);

		// and the work board surfaces the link resolved to the spec node (same as the NodeId form)
		var get = await Agent("tasks_search", new { projectKey = ProjectKey, board = "work" });
		Text(get).Should().Contain(specId);
	}

	// 29. an unknown slug specRef is rejected, and the error names the spec board it searched.
	[Fact]
	public async Task SpecRef_UnknownSlug_RejectedNamingSpecBoard()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" }); // auto-wires SpecBoard=spec
		var r = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "t", type = "feature", status = "Pending", title = "T", body = "x", specRef = "no-such-spec" })
		});
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("no-such-spec");
		Text(r).Should().Contain("does not match any node on spec board");
	}

	// 30. a slug specRef on a work board with NO linked spec board can't resolve — rejected
	// with a clear "provide a NodeId" error (a NodeId-form specRef would still be accepted).
	[Fact]
	public async Task SpecRef_SlugWithoutSpecBoard_Rejected()
	{
		// no spec board exists in this test instance → board_create does not auto-wire one
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var r = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "t", type = "feature", status = "Pending", title = "T", body = "x", specRef = "some-spec" })
		});
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("is a slug, but this board has no linked spec board");
		Text(r).Should().Contain("provide the spec node");
	}

	// 31. ideaRef accepts the idea node's SLUG (resolved on the ideas board of this board's
	// methodology instance, mirroring specRef) — and the target is TERMINAL (`accepted`), so
	// the resolver must not filter by status. The idea_spec edge carries the resolved NodeId.
	[Fact]
	public async Task IdeaRef_BySlug_ToAcceptedIdea_CreatesIdeaSpecEdge()
	{
		var ideaId = await AcceptedIdeaId("want-x");
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var spec = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "spec",
			nodes = Nodes(new { key = "x", status = "defined", title = "X", body = "x", ideaRef = "want-x" })
		});
		IsErr(spec).Should().BeFalse(Text(spec));
		var specId = NodeId(spec, "x");

		var rels = await Agent("relations_list", new { projectKey = ProjectKey, nodeId = specId, direction = "to" });
		Text(rels).Should().Contain("idea_spec");
		Text(rels).Should().Contain(ideaId); // the resolved NodeId, never the raw slug
	}

	// 32. the same slug resolution from the WORK board (ideaRef is not spec-board-only): the
	// ideas board is found by kind within the instance bucket, not via the SpecBoard pin.
	[Fact]
	public async Task IdeaRef_BySlug_FromWorkBoard_Resolves()
	{
		var ideaId = await AcceptedIdeaId("want-x");
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var work = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "chore-x", type = "chore", status = "Pending", title = "Chore", body = "x", ideaRef = "want-x" })
		});
		IsErr(work).Should().BeFalse(Text(work));
		var taskId = NodeId(work, "chore-x");

		var rels = await Agent("relations_list", new { projectKey = ProjectKey, nodeId = taskId, direction = "to" });
		Text(rels).Should().Contain("idea_spec");
		Text(rels).Should().Contain(ideaId);
	}

	// 33. slug resolution does NOT weaken the constraint: a slug pointing at a non-accepted
	// idea resolves fine, then dies on the STATUS rule (not on the resolve message).
	[Fact]
	public async Task IdeaRef_BySlug_ToNonAcceptedIdea_RejectedByStatusRule()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "ideas", kind = "ideas" });
		await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "ideas",
			nodes = Nodes(new { key = "drv", type = "idea", status = "exploring", title = "drv", body = "x" })
		});
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var r = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "spec",
			nodes = Nodes(new { key = "x", status = "defined", title = "X", body = "x", ideaRef = "drv" })
		});
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("not accepted");
		Text(r).Should().NotContain("does not match any node on ideas board");
	}

	// 34. an unknown slug ideaRef is rejected, and the error names the ideas board it searched.
	[Fact]
	public async Task IdeaRef_UnknownSlug_RejectedNamingIdeasBoard()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "ideas", kind = "ideas" });
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var r = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "spec",
			nodes = Nodes(new { key = "x", status = "defined", title = "X", body = "x", ideaRef = "no-such-idea" })
		});
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("no-such-idea");
		// (the envelope '-escapes quotes, so assert around the quoted board name)
		Text(r).Should().Contain("does not match any node on ideas board");
		Text(r).Should().Contain("ideas\\u0027");
	}

	// 35. regression: the NodeId form still passes through untouched.
	[Fact]
	public async Task IdeaRef_ByNodeId_StillResolves()
	{
		var ideaId = await AcceptedIdeaId("want-x");
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var spec = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "spec",
			nodes = Nodes(new { key = "x", status = "defined", title = "X", body = "x", ideaRef = ideaId })
		});
		IsErr(spec).Should().BeFalse(Text(spec));
		var rels = await Agent("relations_list", new { projectKey = ProjectKey, nodeId = NodeId(spec, "x"), direction = "to" });
		Text(rels).Should().Contain(ideaId);
	}
}

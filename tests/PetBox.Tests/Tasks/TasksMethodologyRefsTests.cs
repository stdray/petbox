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
			nodes = Nodes(new { key = "login", status = "defined", title = "Login", body = "login flow", links = new { idea_spec = ir } }),
		});
		var specId = NodeId(spec, "login");

		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var work = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "do-login", type = "feature", status = "Pending", title = "Build login", body = "...", links = new { task_spec = specId } }),
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
			nodes = Nodes(new { key = "auth", status = "defined", title = "Auth", body = "x", links = new { idea_spec = ir } }),
		});
		var specId = NodeId(spec, "auth");
		var v = JsonDocument.Parse(Text(spec)).RootElement;

		// rename auth → identity (Key change, same NodeId via prevKey lineage).
		// version = 1 is the baseline the author last saw for "auth".
		var renamed = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "spec",
			nodes = Nodes(new { key = "identity", prevKey = "auth", version = 1, status = "defined", title = "Identity", body = "x", links = new { idea_spec = ir } }),
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
		var r = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "t", type = "feature", status = "Pending", title = "T", body = "x", links = new { task_spec = nonSpecId } }) });
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("not a spec board");
	}

	// 17. a specRef must point at a SPEC board node — a node on a non-spec board is rejected.
	// (The spec kind is now a per-project singleton, so the old two-spec-boards mismatch is
	// unreachable; the meaningful guard is "the target must live on the spec board".)
	[Fact]
	public async Task SpecRef_NonWiredBoardNode_Rejected()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		// A node on a NON-spec (free) board — not a valid spec target.
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "other" });
		var other = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "other", nodes = Nodes(new { key = "r", status = "Todo", title = "R", body = "x" }) });
		var otherId = NodeId(other, "r");

		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" }); // auto-wires to spec

		var r = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "t", type = "feature", status = "Pending", title = "T", body = "x", links = new { task_spec = otherId } }) });
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
			nodes = Nodes(new { key = "login", status = "defined", title = "Login", body = "x", links = new { idea_spec = ir } })
		});
		var specId = NodeId(spec, "login");

		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" }); // auto-wires WiredBoard=spec
		var work = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "do-login", type = "feature", status = "Pending", title = "Build login", body = "x", links = new { task_spec = "login" } })
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
	public async Task SpecRef_UnknownSlug_RejectedNamingWiredBoard()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" }); // auto-wires WiredBoard=spec
		var r = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "t", type = "feature", status = "Pending", title = "T", body = "x", links = new { task_spec = "no-such-spec" } })
		});
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("no-such-spec");
		Text(r).Should().Contain("does not match any node on spec board");
	}

	// 30. a slug specRef on a work board with NO linked spec board can't resolve — rejected
	// with a clear "provide a NodeId" error (a NodeId-form specRef would still be accepted).
	[Fact]
	public async Task SpecRef_SlugWithoutWiredBoard_Rejected()
	{
		// no spec board exists in this test instance → board_create does not auto-wire one
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var r = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "t", type = "feature", status = "Pending", title = "T", body = "x", links = new { task_spec = "some-spec" } })
		});
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("is a slug, but no active spec board exists alongside board");
		Text(r).Should().Contain("provide the target node");
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
			nodes = Nodes(new { key = "x", status = "defined", title = "X", body = "x", links = new { idea_spec = "want-x" } })
		});
		IsErr(spec).Should().BeFalse(Text(spec));
		var specId = NodeId(spec, "x");

		var rels = await Agent("relations_list", new { projectKey = ProjectKey, nodeId = specId, direction = "to" });
		Text(rels).Should().Contain("idea_spec");
		Text(rels).Should().Contain(ideaId); // the resolved NodeId, never the raw slug
	}

	// 32. idea_spec is DIRECTION-TYPED (ideas→spec): a work node is neither end, so addressing
	// idea_spec from a work board by slug is refused (spec methodology-link-kinds-declared — the
	// generic resolver resolves a slug only against the opposite END's kind). Work provenance is
	// expressed by task_spec (work→spec), not by citing an idea directly.
	[Fact]
	public async Task IdeaRef_BySlug_FromWorkBoard_IsRefused_NotAnEndOfIdeaSpec()
	{
		await AcceptedIdeaId("want-x");
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var work = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "chore-x", type = "chore", status = "Pending", title = "Chore", body = "x", links = new { idea_spec = "want-x" } })
		});
		IsErr(work).Should().BeTrue();
		// The refusal survived links-neutral-kinds-unreachable; only its WORDING changed. It now
		// names the kind's declared ends and points at a node that could actually carry the link,
		// instead of advising "declare its direction, or pass a NodeId" — advice that was false on
		// both counts here (the direction IS declared; a NodeId was refused just the same).
		Text(work).Should().Contain("sitting on neither end");
		Text(work).Should().Contain("declare it from a ideas node instead");
		Text(work).Should().NotContain("has no direction to resolve against");
	}

	// 36. a DIRECTED kind still requires its direction when addressed by NODEID too. Moving the
	// target-kind check inside the ref loop (so neutral kinds could resolve) must not turn a NodeId
	// into a bypass for direction enforcement — gate 2 fires before any ref is looked at.
	[Fact]
	public async Task IdeaSpec_ByNodeId_FromWorkBoard_StillRefused_NotAnEndOfIdeaSpec()
	{
		var ideaId = await AcceptedIdeaId("want-x");
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var work = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "chore-x", type = "chore", status = "Pending", title = "Chore", body = "x", links = new { idea_spec = ideaId } })
		});
		IsErr(work).Should().BeTrue();
		Text(work).Should().Contain("sitting on neither end");
	}

	// 37. links-neutral-kinds-unreachable: a NEUTRAL kind resolves BY SLUG through `links:`. The
	// neutral trio pins no target kind, so the slug resolves across the instance's active boards —
	// here work→work, the very edge the bug report was filed about.
	[Fact]
	public async Task NeutralKind_BySlug_ResolvesThroughLinks()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var a = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "peer", type = "chore", status = "Pending", title = "Peer", body = "x" })
		});
		IsErr(a).Should().BeFalse(Text(a));
		var peerId = NodeId(a, "peer");

		var b = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "cites", type = "chore", status = "Pending", title = "Cites", body = "x", links = new { relates_to = "peer" } })
		});
		IsErr(b).Should().BeFalse(Text(b));

		var rels = await Agent("relations_list", new { projectKey = ProjectKey, nodeId = NodeId(b, "cites"), direction = "from" });
		Text(rels).Should().Contain("relates_to");
		Text(rels).Should().Contain(peerId); // the RESOLVED NodeId, never the raw slug
	}

	// 38. the same neutral edge BY NODEID. This is the form the old error message explicitly
	// advised ("or pass a NodeId") while its own early return — hoisted outside the ref loop —
	// made it impossible. The message's remedy is now true.
	[Fact]
	public async Task NeutralKind_ByNodeId_ResolvesThroughLinks()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var a = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "peer", type = "chore", status = "Pending", title = "Peer", body = "x" })
		});
		var peerId = NodeId(a, "peer");

		var b = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "cites", type = "chore", status = "Pending", title = "Cites", body = "x", links = new { depends_on = peerId } })
		});
		IsErr(b).Should().BeFalse(Text(b));

		var rels = await Agent("relations_list", new { projectKey = ProjectKey, nodeId = NodeId(b, "cites"), direction = "from" });
		Text(rels).Should().Contain("depends_on");
		Text(rels).Should().Contain(peerId);
	}

	// 39. a neutral kind crosses board KINDS too — the scope of the bug was categorical, not a
	// work→work special case (it was measured refusing from an ideas node as well).
	[Fact]
	public async Task NeutralKind_FromIdeasBoard_CrossesToWork()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "ideas", kind = "ideas" });
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var w = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "job", type = "chore", status = "Pending", title = "Job", body = "x" })
		});
		var jobId = NodeId(w, "job");

		var i = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "ideas",
			nodes = Nodes(new { key = "notion", type = "idea", status = "raw", title = "Notion", body = "x", links = new { relates_to = "job" } })
		});
		IsErr(i).Should().BeFalse(Text(i));

		var rels = await Agent("relations_list", new { projectKey = ProjectKey, nodeId = NodeId(i, "notion"), direction = "from" });
		Text(rels).Should().Contain("relates_to");
		Text(rels).Should().Contain(jobId);
	}

	// 40. PARITY (spec upsert-link-parity): the `links:` door and relations_create produce THE SAME
	// edge for the same neutral kind. relations_create is idempotent, so asking it for the edge the
	// upsert already declared must return that one rather than mint a second — the two paths agree
	// on the edge itself, not merely on both being permitted.
	[Fact]
	public async Task NeutralKind_LinksPath_AgreesWith_RelationsCreate()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var a = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "peer", type = "chore", status = "Pending", title = "Peer", body = "x" })
		});
		var peerId = NodeId(a, "peer");
		var b = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "cites", type = "chore", status = "Pending", title = "Cites", body = "x", links = new { relates_to = "peer" } })
		});
		IsErr(b).Should().BeFalse(Text(b));
		var citesId = NodeId(b, "cites");

		var viaRelations = await Agent("relations_create", new
		{
			projectKey = ProjectKey,
			kind = "relates_to",
			fromNodeId = citesId,
			toNodeId = peerId,
		});
		IsErr(viaRelations).Should().BeFalse(Text(viaRelations));

		// One edge, not two: the idempotent create returned the row the links door had written.
		var rels = await Agent("relations_list", new { projectKey = ProjectKey, nodeId = citesId, direction = "from" });
		var edges = JsonDocument.Parse(Text(rels)).RootElement.GetProperty("relations").EnumerateArray()
			.Count(e => e.GetProperty("kind").GetString() == "relates_to");
		edges.Should().Be(1, "the links: path and relations_create must resolve to the SAME edge");
	}

	// 41. the vocabulary gate. An unknown kind was refused BY ACCIDENT before this fix — it has no
	// direction, so the old unconditional direction check caught it on the way past. That accident
	// died with the check's hoisting, and LinkRefsAsync writes what the engine resolves WITHOUT
	// re-validating the kind: without an explicit gate, `links:{whatever: <NodeId>}` would mint a
	// junk edge. An illegal kind must stay refused — by slug AND by NodeId.
	[Fact]
	public async Task UnknownKind_IsRefused_BySlugAndByNodeId()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var a = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "peer", type = "chore", status = "Pending", title = "Peer", body = "x" })
		});
		var peerId = NodeId(a, "peer");

		var bySlug = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "c1", type = "chore", status = "Pending", title = "C", body = "x", links = new { florble = "peer" } })
		});
		IsErr(bySlug).Should().BeTrue();
		Text(bySlug).Should().Contain("unknown link kind");
		Text(bySlug).Should().Contain("relates_to"); // the message lists the kinds that ARE valid

		var byNodeId = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "c2", type = "chore", status = "Pending", title = "C", body = "x", links = new { florble = peerId } })
		});
		IsErr(byNodeId).Should().BeTrue();
		Text(byNodeId).Should().Contain("unknown link kind");
	}

	// 42. the structural pair keeps its own door. part_of/supersedes are direction-less like the
	// neutral trio, so the widened path would have happily minted bare edges for them behind the
	// back of the `partOf`/`supersedes` fields that own the tree/chain bookkeeping. Refused, and
	// the refusal names the field that works.
	[Fact]
	public async Task StructuralKinds_AreRefusedThroughLinks_NamingTheirOwnField()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var a = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "parent", type = "chore", status = "Pending", title = "P", body = "x" })
		});
		var parentId = NodeId(a, "parent");

		var r = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "child", type = "chore", status = "Pending", title = "C", body = "x", links = new { part_of = parentId } })
		});
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("is not settable through links");
		Text(r).Should().Contain("partOf");
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
			nodes = Nodes(new { key = "x", status = "defined", title = "X", body = "x", links = new { idea_spec = "drv" } })
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
			nodes = Nodes(new { key = "x", status = "defined", title = "X", body = "x", links = new { idea_spec = "no-such-idea" } })
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
			nodes = Nodes(new { key = "x", status = "defined", title = "X", body = "x", links = new { idea_spec = ideaId } })
		});
		IsErr(spec).Should().BeFalse(Text(spec));
		var rels = await Agent("relations_list", new { projectKey = ProjectKey, nodeId = NodeId(spec, "x"), direction = "to" });
		Text(rels).Should().Contain(ideaId);
	}
}

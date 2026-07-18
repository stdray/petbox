using System.Text.Json;

namespace PetBox.Tests.Tasks;

// Methodology smoke, theme 2/4: the SPEC board and the idea gate in front of it — the spec tree,
// the accepted-idea precondition on every spec write, the spec FSM vocabulary, and the delivery
// rollup computed from linked work tasks. Own TasksMethodologySmokeFixture instance (one host for
// the class, per-test ResetAsync) — see TasksMethodologySmokeBase.
public sealed class TasksMethodologySpecTests : TasksMethodologySmokeBase, IClassFixture<TasksMethodologySmokeFixture>
{
	public TasksMethodologySpecTests(TasksMethodologySmokeFixture fx) : base(fx) { }

	// 1. spec board: create H1/H2/H3 nodes (path depth 1–3), read back as a tree.
	[Fact]
	public async Task Spec_CreateThreeLevels_ReadBackAsTree()
	{
		(await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" })).IsError.Should().NotBe(true);
		var ir = await AcceptedIdeaId();
		(await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "spec",
			nodes = Nodes(
				new { key = "auth", status = "defined", title = "Auth", body = "auth area", links = new { idea_spec = ir } },
				new { key = "login", partOf = "auth", status = "defined", title = "Login", body = "login flow", links = new { idea_spec = ir } },
				new { key = "mfa", partOf = "login", status = "defined", title = "MFA", body = "second factor", links = new { idea_spec = ir } }),
		})).IsError.Should().NotBe(true);

		var tree = await Agent("tasks_search", new { projectKey = ProjectKey, board = "spec" });
		tree.IsError.Should().NotBe(true);
		// mfa is two part_of edges below auth — its parent is login.
		Text(tree).Should().Contain("mfa");
		FieldOf(tree, "mfa", "parentSlug").Should().Be("login");
	}

	// 8. ideas: raw → exploring → accepted produces a spec node + an idea_spec relation.
	[Fact]
	public async Task Idea_Accepted_LinksToSpec()
	{
		// idea reaches `accepted` only through the gate (exploring → review[+spec_plan] → accepted).
		var ideaId = await AcceptedIdeaId("want-x");

		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var spec = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "spec", nodes = Nodes(new { key = "x", status = "defined", title = "X", body = "defined", links = new { idea_spec = ideaId } }) });
		spec.IsError.Should().NotBe(true);
		var specId = NodeId(spec, "x");

		// ideaRef auto-creates the idea_spec edge (accepted idea -> spec node).
		var rels = await Agent("relations_list", new { projectKey = ProjectKey, nodeId = specId, direction = "to" });
		Text(rels).Should().Contain("idea_spec");
		Text(rels).Should().Contain(ideaId);
	}

	// 13. spec node delivery status is COMPUTED from linked tasks (type-aware), rolled up the tree.
	[Fact]
	public async Task SpecRollup_ComputedFromLinkedTasks()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "spec",
			nodes = Nodes(
			new { key = "auth", status = "defined", title = "Auth", body = "x", links = new { idea_spec = ir } },
			new { key = "login", partOf = "auth", status = "defined", title = "Login", body = "x", links = new { idea_spec = ir } })
		});
		var loginId = NodeId(await Agent("tasks_search", new { projectKey = ProjectKey, board = "spec" }), "login");

		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "f", type = "feature", status = "Review", title = "F", body = "x", links = new { task_spec = loginId } }) });

		var s1 = await Agent("tasks_search", new { projectKey = ProjectKey, board = "spec" });
		FieldOf(s1, "login", "delivery").Should().Be("in_progress");
		FieldOf(s1, "auth", "delivery").Should().Be("in_progress", "parent aggregates the part_of subtree");

		await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "f", type = "feature", status = "Done", version = 1, title = "F", body = "x", links = new { task_spec = loginId } }) });
		var s2 = await Agent("tasks_search", new { projectKey = ProjectKey, board = "spec" });
		FieldOf(s2, "login", "delivery").Should().Be("done");
		FieldOf(s2, "auth", "delivery").Should().Be("done");

		await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "bug1", type = "bug", status = "Pending", title = "Bug", body = "x", links = new { task_spec = loginId } }) });
		var s3 = await Agent("tasks_search", new { projectKey = ProjectKey, board = "spec" });
		FieldOf(s3, "login", "delivery").Should().Be("done_with_defects", "all features Done but an open bug remains");
		FieldOf(s3, "auth", "delivery").Should().Be("done_with_defects");
	}

	// 13b. an umbrella spec node with task-less leaves must NOT roll up to `done` just because
	// the flat union of all tasks-in-subtree happens to be all-Done — a leaf with zero linked
	// tasks is `not_started`, so the umbrella reads `in_progress` while any leaf is unstarted.
	[Fact]
	public async Task SpecRollup_UmbrellaWithTaskLessLeaves_IsNotDone()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "spec",
			nodes = Nodes(
				new { key = "umbrella", status = "defined", title = "Umbrella", body = "x", links = new { idea_spec = ir } },
				new { key = "leaf1", partOf = "umbrella", status = "defined", title = "Leaf1", body = "x", links = new { idea_spec = ir } },
				new { key = "leaf2", partOf = "umbrella", status = "defined", title = "Leaf2", body = "x", links = new { idea_spec = ir } },
				new { key = "leaf3", partOf = "umbrella", status = "defined", title = "Leaf3", body = "x", links = new { idea_spec = ir } })
		});
		var searchAfterCreate = await Agent("tasks_search", new { projectKey = ProjectKey, board = "spec" });
		var leaf1Id = NodeId(searchAfterCreate, "leaf1");

		// leaf2 and leaf3 get NO linked tasks at all; only leaf1 gets a Done feature.
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "f", type = "feature", status = "Done", title = "F", body = "x", links = new { task_spec = leaf1Id } }) });

		var s = await Agent("tasks_search", new { projectKey = ProjectKey, board = "spec" });
		FieldOf(s, "leaf1", "delivery").Should().Be("done");
		FieldOf(s, "leaf2", "delivery").Should().Be("not_started", "a leaf with no linked tasks is not_started, not silently absent from the rollup");
		FieldOf(s, "leaf3", "delivery").Should().Be("not_started");
		FieldOf(s, "umbrella", "delivery").Should().Be("in_progress", "one done leaf + two not-started leaves must NOT roll up to done");
	}

	// presetkind-spec-blind-spot: a spec node's `linkedTasks` field (the inbound task_spec edges —
	// the work tasks that implement it) is gated in TasksService.GetAsync on `presetKind ==
	// BoardKind.Spec`, the SAME anti-pattern the strikethrough regression (board-ui-review-findings
	// #2, PR #21/#22) already broke: PresetKind nulls out for any DEFINED kind, and this bare-preset
	// board (kind "spec" with no methodology instance) is the ONLY shape that ever exercised
	// `linkedTasks` before — IsDefinedKind("spec") is false here, so the old
	// `presetKind == BoardKind.Spec` check happened to still work. Kept as the preset-shape half of
	// the pair; see the definition-resolved sibling below for the shape that actually broke in
	// production.
	[Fact]
	public async Task SpecNode_LinkedTasks_ListsWorkTaskLinkedViaSpecRef()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		var spec = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "spec", nodes = Nodes(new { key = "login", status = "defined", title = "Login", body = "x", links = new { idea_spec = ir } }) });
		var specId = NodeId(spec, "login");

		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "f", type = "feature", status = "Review", title = "F", body = "x", links = new { task_spec = specId } }) });

		var s = await Agent("tasks_search", new { projectKey = ProjectKey, board = "spec" });
		using var doc = JsonDocument.Parse(Text(s));
		var loginEl = Descend(doc.RootElement).Single(e =>
			e.ValueKind == JsonValueKind.Object && e.TryGetProperty("key", out var k) && k.GetString() == "login");
		loginEl.TryGetProperty("linkedTasks", out var lt).Should().BeTrue(
			"a spec node with an inbound task_spec edge must report linkedTasks (bare-preset spec board shape)");
		lt.EnumerateArray().Select(x => x.GetProperty("slug").GetString()).Should().Contain("f");
	}

	// THE regression: same scenario, but spec/work are provisioned as ONE REAL quartet methodology
	// instance (tasks_methodology_create, source=builtin/quartet) — the shape $system's boards
	// actually have (RenderPresetDefinition copies the quartet kinds, including `spec`, VERBATIM
	// into the instance's stored definition at creation time, so IsDefinedKind("spec") is TRUE).
	// On that shape `presetKind` (TasksService.GetAsync) reads null, so the old
	// `presetKind == BoardKind.Spec` gate at TasksService.cs:653 was NEVER true — `linkedTasks`
	// silently dropped off every real spec node's response, for every project using the standard
	// quartet methodology (not a hypothetical: this is $system's own shape).
	[Fact]
	public async Task SpecNode_LinkedTasks_ListsWorkTaskLinkedViaSpecRef_OnQuartetDefinitionResolvedBoards()
	{
		await Agent("tasks_methodology_create", new { projectKey = ProjectKey, name = "spquartet", source = "builtin", sourceKey = "quartet" });
		var ir = await AcceptedIdeaId(createBoard: false);
		var spec = await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "spec", nodes = Nodes(new { key = "login", status = "defined", title = "Login", body = "x", links = new { idea_spec = ir } }) });
		var specId = NodeId(spec, "login");

		await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "f", type = "feature", status = "Review", title = "F", body = "x", links = new { task_spec = specId } }) });

		var s = await Agent("tasks_search", new { projectKey = ProjectKey, board = "spec" });
		using var doc = JsonDocument.Parse(Text(s));
		var loginEl = Descend(doc.RootElement).Single(e =>
			e.ValueKind == JsonValueKind.Object && e.TryGetProperty("key", out var k) && k.GetString() == "login");
		loginEl.TryGetProperty("linkedTasks", out var lt).Should().BeTrue(
			"a DEFINITION-RESOLVED spec board (the shape $system actually has) must still report linkedTasks — " +
			"PresetKind nulling out for a defined kind must not silently drop this field (presetkind-spec-blind-spot)");
		lt.EnumerateArray().Select(x => x.GetProperty("slug").GetString()).Should().Contain("f");
	}

	// 22. spec-write-needs-accepted-idea: a spec node without ideaRef is rejected.
	[Fact]
	public async Task Spec_WithoutIdeaRef_Rejected()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var r = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "spec",
			nodes = Nodes(new { key = "x", status = "defined", title = "X", body = "x" })
		});
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("accepted idea");
	}

	// 23. a spec node referencing a NOT-yet-accepted idea (still exploring) is rejected.
	[Fact]
	public async Task Spec_WithNonAcceptedIdea_Rejected()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "ideas", kind = "ideas" });
		var ideaId = NodeId(await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "ideas",
			nodes = Nodes(new { key = "drv", type = "idea", status = "exploring", title = "drv", body = "x" })
		}), "drv");
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var r = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "spec",
			nodes = Nodes(new { key = "x", status = "defined", title = "X", body = "x", links = new { idea_spec = ideaId } })
		});
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("not accepted");
	}

	// 24. spec FSM has no draft: creating a spec node with status `draft` is rejected.
	[Fact]
	public async Task Spec_DraftStatus_Rejected()
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		var r = await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "spec",
			nodes = Nodes(new { key = "x", status = "draft", title = "X", body = "x", links = new { idea_spec = ir } })
		});
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("draft"); // error enumerates valid statuses (defined|deprecated)
	}
}

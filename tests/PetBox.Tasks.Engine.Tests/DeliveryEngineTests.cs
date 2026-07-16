namespace PetBox.Tasks.Engine.Tests;

// The delivery roll-up as a pure function (methodology-engine-extraction, slice 5). Before the
// extraction this judgement was only reachable through TasksService with a live store behind it,
// so every case below had to be spelled as a board fixture; now it is a table of nodes and edges.
//
// The assertions are VALUES (not_started / in_progress / done / done_with_defects), not error
// strings — that is what parity means for this seam.
public class DeliveryEngineTests
{
	// The quartet spec kind's own roles: `feature` drives progress, `bug` is the defect type.
	static readonly MethodologyDeliveryDef Def = new(["feature"], ["bug"]);

	static readonly MethodologyRuntime Runtime = EngineFixture.Quartet;

	// A node addressed by a readable seed; the roll-up reads only NodeId/Status/Type off it.
	static NodeState N(string seed, string type, string status) =>
		new(seed, null, EngineFixture.Id(seed), status, type);

	static Dictionary<string, string> ParentOf(params (string Child, string Parent)[] pairs) =>
		pairs.ToDictionary(p => EngineFixture.Id(p.Child), p => EngineFixture.Id(p.Parent), StringComparer.Ordinal);

	static Dictionary<string, IReadOnlyList<string>> TasksOf(params (string Spec, string[] Tasks)[] pairs) =>
		pairs.ToDictionary(
			p => EngineFixture.Id(p.Spec),
			p => (IReadOnlyList<string>)p.Tasks.Select(EngineFixture.Id).ToList(),
			StringComparer.Ordinal);

	static readonly Dictionary<string, string> NoParents = new(StringComparer.Ordinal);
	static readonly Dictionary<string, IReadOnlyList<string>> NoTasks = new(StringComparer.Ordinal);

	static Dictionary<string, string> Rollup(
		IReadOnlyList<NodeState> nodes,
		Dictionary<string, string> parentOf,
		Dictionary<string, IReadOnlyList<string>> tasksOf,
		params string[] specSeeds) =>
		DeliveryEngine.Rollup(Runtime, Def, specSeeds.Select(EngineFixture.Id).ToList(), nodes, parentOf, tasksOf);

	static string Of(Dictionary<string, string> result, string seed) => result[EngineFixture.Id(seed)];

	// A spec leaf with nothing linked to it has NO inputs at all — it is not_started, not `done`
	// by vacuous truth. This default is the whole reason the walk resolves each leaf independently
	// instead of pooling a subtree's tasks into one flat union (a task-less leaf used to vanish
	// from the union and let an umbrella read `done` with work that never existed).
	[Fact]
	public void Leaf_WithNoTasks_IsNotStarted()
	{
		var r = Rollup([N("s1", "spec", "defined")], NoParents, NoTasks, "s1");

		Of(r, "s1").Should().Be("not_started");
	}

	// A required type present but not terminal-ok: in_progress. Pending is Open, so is Review —
	// neither counts as delivered.
	[Theory]
	[InlineData("Pending")]
	[InlineData("InProgress")]
	[InlineData("Review")]
	[InlineData("Blocked")]
	public void Leaf_WithRequiredNotTerminal_IsInProgress(string status)
	{
		var r = Rollup(
			[N("s1", "spec", "defined"), N("f1", "feature", status), N("f2", "feature", "Done")],
			NoParents,
			TasksOf(("s1", ["f1", "f2"])),
			"s1");

		Of(r, "s1").Should().Be("in_progress");
	}

	// All requireds terminal-ok and no open defect: done.
	[Fact]
	public void Leaf_WithAllRequiredDone_IsDone()
	{
		var r = Rollup(
			[N("s1", "spec", "defined"), N("f1", "feature", "Done"), N("f2", "feature", "Done")],
			NoParents,
			TasksOf(("s1", ["f1", "f2"])),
			"s1");

		Of(r, "s1").Should().Be("done");
	}

	// All requireds terminal-ok but a defect is still OPEN: done_with_defects. This is the case a
	// naive "all tasks terminal?" roll-up gets wrong.
	[Fact]
	public void Leaf_WithAllRequiredDone_AndOpenDefect_IsDoneWithDefects()
	{
		var r = Rollup(
			[N("s1", "spec", "defined"), N("f1", "feature", "Done"), N("b1", "bug", "InProgress")],
			NoParents,
			TasksOf(("s1", ["f1", "b1"])),
			"s1");

		Of(r, "s1").Should().Be("done_with_defects");
	}

	// A CLOSED defect does not taint a finished node — only an Open one does. Terminal-cancel
	// (Cancelled) is not Open either, so a cancelled bug is no defect.
	[Theory]
	[InlineData("Done", "done")]
	[InlineData("Cancelled", "done")]
	[InlineData("Pending", "done_with_defects")]
	[InlineData("Blocked", "done_with_defects")]
	public void Leaf_DefectStatus_DecidesTaint(string bugStatus, string expected)
	{
		var r = Rollup(
			[N("s1", "spec", "defined"), N("f1", "feature", "Done"), N("b1", "bug", bugStatus)],
			NoParents,
			TasksOf(("s1", ["f1", "b1"])),
			"s1");

		Of(r, "s1").Should().Be(expected);
	}

	// A node carrying ONLY defects has no required type at all — no progress is claimed for it,
	// open bug or not. (Delivery measures requireds; a bug alone is not a deliverable.)
	[Fact]
	public void Leaf_WithOnlyDefects_IsNotStarted()
	{
		var r = Rollup(
			[N("s1", "spec", "defined"), N("b1", "bug", "InProgress")],
			NoParents,
			TasksOf(("s1", ["b1"])),
			"s1");

		Of(r, "s1").Should().Be("not_started");
	}

	// A task linked by an edge but absent from the node set (closed out of the active scan) is
	// SKIPPED, not counted as unfinished: here the only surviving task is done, so the node is.
	[Fact]
	public void Leaf_WithTaskMissingFromNodeSet_SkipsIt()
	{
		var r = Rollup(
			[N("s1", "spec", "defined"), N("f1", "feature", "Done")],
			NoParents,
			TasksOf(("s1", ["f1", "ghost"])),
			"s1");

		Of(r, "s1").Should().Be("done");
	}

	// A parent with no tasks of its own is the COMBINATION of its children — each child resolved
	// on its own first. One child done + one not_started => in_progress (not "half done").
	[Fact]
	public void Parent_CombinesChildren()
	{
		var nodes = new[]
		{
			N("root", "spec", "defined"), N("c1", "spec", "defined"), N("c2", "spec", "defined"),
			N("f1", "feature", "Done"),
		};
		var r = Rollup(
			nodes,
			ParentOf(("c1", "root"), ("c2", "root")),
			TasksOf(("c1", ["f1"])), // c2 has no tasks at all -> not_started
			"root", "c1", "c2");

		Of(r, "c1").Should().Be("done");
		Of(r, "c2").Should().Be("not_started");
		Of(r, "root").Should().Be("in_progress");
	}

	// All children done => the parent is done. Nesting is transitive: a grandchild's status
	// reaches the root through its parent's combined value.
	[Fact]
	public void Parent_AllChildrenDone_IsDone()
	{
		var nodes = new[]
		{
			N("root", "spec", "defined"), N("mid", "spec", "defined"), N("leaf", "spec", "defined"),
			N("f1", "feature", "Done"),
		};
		var r = Rollup(
			nodes,
			ParentOf(("mid", "root"), ("leaf", "mid")),
			TasksOf(("leaf", ["f1"])),
			"root", "mid", "leaf");

		Of(r, "leaf").Should().Be("done");
		Of(r, "mid").Should().Be("done");
		Of(r, "root").Should().Be("done");
	}

	// A defect deep in the tree surfaces at the root as done_with_defects rather than being
	// flattened to in_progress or swallowed.
	[Fact]
	public void Parent_DefectInChild_SurfacesAsDoneWithDefects()
	{
		var nodes = new[]
		{
			N("root", "spec", "defined"), N("c1", "spec", "defined"), N("c2", "spec", "defined"),
			N("f1", "feature", "Done"), N("f2", "feature", "Done"), N("b1", "bug", "Pending"),
		};
		var r = Rollup(
			nodes,
			ParentOf(("c1", "root"), ("c2", "root")),
			TasksOf(("c1", ["f1"]), ("c2", ["f2", "b1"])),
			"root", "c1", "c2");

		Of(r, "c1").Should().Be("done");
		Of(r, "c2").Should().Be("done_with_defects");
		Of(r, "root").Should().Be("done_with_defects");
	}

	// A node may be BOTH a decomposition parent AND carry work of its own — the roll-up combines
	// its own tasks with its children's deliveries in one pass, it does not pick one or the other.
	[Fact]
	public void Node_WithOwnTasksAndChildren_CombinesBoth()
	{
		var nodes = new[]
		{
			N("root", "spec", "defined"), N("c1", "spec", "defined"),
			N("own", "feature", "Pending"), N("f1", "feature", "Done"),
		};
		var r = Rollup(
			nodes,
			ParentOf(("c1", "root")),
			TasksOf(("root", ["own"]), ("c1", ["f1"])),
			"root", "c1");

		Of(r, "c1").Should().Be("done");
		// The child is done, root's OWN feature is not — the parent's own work is not ignored.
		Of(r, "root").Should().Be("in_progress");
	}

	// Same shape, both halves finished: the node is done only when its own work AND its children
	// are. This is the direction the previous test cannot distinguish on its own.
	[Fact]
	public void Node_WithOwnTasksAndChildren_BothDone_IsDone()
	{
		var nodes = new[]
		{
			N("root", "spec", "defined"), N("c1", "spec", "defined"),
			N("own", "feature", "Done"), N("f1", "feature", "Done"),
		};
		var r = Rollup(
			nodes,
			ParentOf(("c1", "root")),
			TasksOf(("root", ["own"]), ("c1", ["f1"])),
			"root", "c1");

		Of(r, "root").Should().Be("done");
	}

	// part_of should never cycle, but the walk is guarded rather than trusting: a cycle
	// TERMINATES (no stack overflow) and the re-entered node contributes not_started to the
	// combination that was already in flight. `a` has no work; `b`'s is done; the guard's
	// not_started for the re-entry is what keeps `b` at in_progress rather than done.
	[Fact]
	public void CycleInPartOf_IsGuarded_AndTerminates()
	{
		var nodes = new[] { N("a", "spec", "defined"), N("b", "spec", "defined"), N("f1", "feature", "Done") };
		var r = Rollup(
			nodes,
			ParentOf(("a", "b"), ("b", "a")), // a is under b AND b is under a
			TasksOf(("b", ["f1"])),
			"a", "b");

		Of(r, "b").Should().Be("in_progress");
		Of(r, "a").Should().Be("in_progress");
	}

	// A node that is its own parent — the degenerate one-node cycle — is guarded too.
	[Fact]
	public void SelfCycle_IsGuarded()
	{
		var r = Rollup(
			[N("s1", "spec", "defined")],
			ParentOf(("s1", "s1")),
			NoTasks,
			"s1");

		Of(r, "s1").Should().Be("not_started");
	}

	// Only the ASKED nodes come back, keyed by NodeId — the walk visits whatever the part_of
	// graph reaches, but the map is the caller's board.
	[Fact]
	public void Rollup_KeysResultByNodeId_ForAskedNodesOnly()
	{
		var nodes = new[] { N("root", "spec", "defined"), N("c1", "spec", "defined"), N("f1", "feature", "Done") };
		var r = Rollup(nodes, ParentOf(("c1", "root")), TasksOf(("c1", ["f1"])), "root");

		r.Should().ContainSingle().Which.Key.Should().Be(EngineFixture.Id("root"));
	}

	// ---- Combine: the shared roll-up over an arbitrary bucket (the tag-group projection's use) ----

	// Nulls (a node with no computed delivery) are ignored rather than dragging the group down.
	[Fact]
	public void Combine_IgnoresNulls()
	{
		DeliveryEngine.Combine([null, "done", null]).Should().Be("done");
	}

	[Theory]
	[InlineData(new string?[] { }, "not_started")]
	[InlineData(new string?[] { "not_started", "not_started" }, "not_started")]
	[InlineData(new string?[] { "done", "done" }, "done")]
	[InlineData(new string?[] { "done", "done_with_defects" }, "done_with_defects")]
	[InlineData(new string?[] { "done_with_defects" }, "done_with_defects")]
	[InlineData(new string?[] { "done", "not_started" }, "in_progress")]
	[InlineData(new string?[] { "done_with_defects", "in_progress" }, "in_progress")]
	public void Combine_RollsUpBucket(string?[] parts, string expected)
	{
		DeliveryEngine.Combine(parts).Should().Be(expected);
	}
}

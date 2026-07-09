using PetBox.Core.Data;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Tasks;

// Node comments: a temporal, tree-structured, open-tagged discussion thread under any node.
// Comments live in the per-project tasks file (M007) but never touch ITasksService — so
// these tests drive CommentService directly over a ScopedDbFactory<TasksDb>, no board needed.
public sealed class CommentServiceTests : IDisposable
{
	readonly string _dir;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly CommentService _svc;

	public CommentServiceTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-comments-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		// Ensure the per-project tasks file exists + schema is applied before the
		// comment-service tests hit NewConnection (mirrors CreateAsync in production).
		_factory.GetDb("p");
		_svc = new CommentService(_factory);
	}

	public void Dispose()
	{
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public async Task Add_Reply_BuildsThread_WithOpenTags()
	{
		var root = await _svc.AddAsync("p", "ideas", "node1", null, "alice", "root body",
			new[] { "artifact:spec_plan", "random-tag" }); // open: neither is in any vocab
		root.Applied.Should().BeTrue();
		root.Id.Should().NotBeNull();

		var reply = await _svc.AddAsync("p", "ideas", "node1", root.Id, "bob", "a reply", null);
		reply.Applied.Should().BeTrue();

		var list = await _svc.ListForNodeAsync("p", "ideas", "node1");
		list.Should().HaveCount(2);
		var r = list.Single(c => c.Id == root.Id);
		r.Tags.Should().BeEquivalentTo(new[] { "artifact:spec_plan", "random-tag" });
		r.Author.Should().Be("alice");
		list.Single(c => c.Id == reply.Id).ParentId.Should().Be(root.Id);
	}

	[Fact]
	public async Task Add_CrossThreadParent_Rejected()
	{
		var root = await _svc.AddAsync("p", "ideas", "node1", null, "a", "x", null);
		// parent exists, but the reply claims a DIFFERENT owning node → reject
		var act = async () => await _svc.AddAsync("p", "ideas", "node2", root.Id, "b", "y", null);
		await act.Should().ThrowAsync<ArgumentException>();
	}

	[Fact]
	public async Task Delete_WithChildren_Rejected_ThenLeafThenParent()
	{
		var root = await _svc.AddAsync("p", "ideas", "n", null, "a", "root", null);
		var child = await _svc.AddAsync("p", "ideas", "n", root.Id, "b", "child", null);

		var act = async () => await _svc.DeleteAsync("p", "ideas", root.Id!);
		await act.Should().ThrowAsync<InvalidOperationException>(); // has an active reply

		(await _svc.DeleteAsync("p", "ideas", child.Id!)).Should().BeTrue();
		(await _svc.DeleteAsync("p", "ideas", root.Id!)).Should().BeTrue(); // now a leaf
		(await _svc.ListForNodeAsync("p", "ideas", "n")).Should().BeEmpty();

		(await _svc.DeleteAsync("p", "ideas", root.Id!)).Should().BeFalse(); // idempotent: already gone
	}

	[Fact]
	public async Task Edit_AtBaseline_Applies_StaleBaseline_Conflicts()
	{
		await _svc.AddAsync("p", "ideas", "n", null, "a", "v0", null);
		var c0 = (await _svc.ListForNodeAsync("p", "ideas", "n")).Single();

		var ok = await _svc.EditAsync("p", "ideas", c0.Id, "v1", null, c0.Version);
		ok.Applied.Should().BeTrue();

		// Re-editing at the now-stale baseline must conflict, not clobber.
		var stale = await _svc.EditAsync("p", "ideas", c0.Id, "v2", null, c0.Version);
		stale.Applied.Should().BeFalse();
		stale.Conflicts.Should().NotBeEmpty();

		(await _svc.ListForNodeAsync("p", "ideas", "n")).Single().Body.Should().Be("v1");
	}

	[Fact]
	public async Task Edit_ReplacesTagSet()
	{
		var add = await _svc.AddAsync("p", "ideas", "n", null, "a", "body", new[] { "artifact:old" });
		var v = (await _svc.ListForNodeAsync("p", "ideas", "n")).Single().Version;

		await _svc.EditAsync("p", "ideas", add.Id!, "body2", new[] { "artifact:new", "x" }, v);

		(await _svc.ListForNodeAsync("p", "ideas", "n")).Single().Tags
			.Should().BeEquivalentTo(new[] { "artifact:new", "x" });
	}

	[Fact]
	public async Task ListForBoard_GroupsByOwningNode()
	{
		await _svc.AddAsync("p", "ideas", "n1", null, "a", "x", null);
		await _svc.AddAsync("p", "ideas", "n1", null, "a", "x2", null);
		await _svc.AddAsync("p", "ideas", "n2", null, "a", "y", null);

		var byNode = await _svc.ListForBoardAsync("p", "ideas");
		byNode["n1"].Should().HaveCount(2);
		byNode["n2"].Should().HaveCount(1);
		byNode["missing"].Should().BeEmpty();
	}
}

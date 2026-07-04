using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;

namespace PetBox.Tests.Tasks;

// Enforced node tags: replace-set semantics with SCD-2 soft-close, controlled
// namespaces, and a real FK to the vocabulary (node_tag.Tag -> tag_vocab.Tag).
public sealed class TagStoreTests : IDisposable
{
	const string Proj = "proj";
	const string Board = "spec";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TagStore _tags;

	public TagStoreTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-tags-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_tags = new TagStore(_factory);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public async Task Set_NormalizesAndVivifiesVocab_ThenReplaceSetSoftCloses()
	{
		await _tags.SetAsync(Proj, Board, "n1", ["Area:UI", "concern:security ", "area:ui"]);
		(await _tags.ActiveTagsAsync(Proj, "n1")).Should().BeEquivalentTo(["area:ui", "concern:security"]);

		// vocab auto-vivified with namespaces.
		var ctx = _factory.GetDb(Proj);
		ctx.TagVocab.Select(v => v.Tag).ToList().Should().Contain(["area:ui", "concern:security"]);

		// Replace-set: drop concern, keep area, add a new one.
		await _tags.SetAsync(Proj, Board, "n1", ["area:ui", "area:llm"]);
		(await _tags.ActiveTagsAsync(Proj, "n1")).Should().BeEquivalentTo(["area:ui", "area:llm"]);

		// History kept: the closed concern row still exists (ValidTo set).
		ctx.NodeTags.Count(t => t.NodeId == "n1" && t.Tag == "concern:security" && t.ValidTo != null).Should().Be(1);
	}

	[Fact]
	public async Task Set_UnknownNamespace_IsRejected()
	{
		var act = () => _tags.SetAsync(Proj, Board, "n1", ["status:done"]);
		await act.Should().ThrowAsync<ArgumentException>().WithMessage("*namespace*");
	}

	[Fact]
	public async Task BoardTags_GroupsActiveByNode()
	{
		await _tags.SetAsync(Proj, Board, "n1", ["area:ui", "concern:security"]);
		await _tags.SetAsync(Proj, Board, "n2", ["area:ui"]);
		var lookup = await _tags.BoardTagsAsync(Proj, Board);
		lookup["n1"].Should().BeEquivalentTo(["area:ui", "concern:security"]);
		lookup["n2"].Should().BeEquivalentTo(["area:ui"]);
	}

	[Fact]
	public async Task ForeignKey_RejectsTagNotInVocab()
	{
		// Bypass TagStore's vocab-ensure: a raw node_tag insert for a tag absent from
		// tag_vocab must fail — proving PRAGMA foreign_keys is ON and the FK is enforced.
		var ctx = _factory.GetDb(Proj);
		var act = () => ctx.InsertAsync(new NodeTag { NodeId = "n9", Board = Board, Tag = "area:ghost", ValidFrom = DateTime.UtcNow });
		await act.Should().ThrowAsync<Exception>();
	}
}

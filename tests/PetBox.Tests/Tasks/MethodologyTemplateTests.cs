using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Workflow;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Tasks;

// methodology-template-storage: named templates independent of live process boards /
// instances. Covers CRUD, builtin read, snapshot-from-effective, dual-read of the
// singleton def, and the hard invariant that template writes never provision boards or
// mutate methodology_defs.
public sealed class MethodologyTemplateTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public MethodologyTemplateTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-mtmpl-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_tasks = new TasksService(new TaskBoardStore(_db, _factory), new RelationStore(_db), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static IHttpContextAccessor Http(string scopes)
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", scopes)], "test");
		var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(id) };
		ctx.Request.Scheme = "https";
		ctx.Request.Host = new HostString("box.test");
		return new HttpContextAccessor { HttpContext = ctx };
	}

	static FeatureFlags Flags()
	{
		var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Tasks"] = "true",
		}).Build();
		return new FeatureFlags(cfg);
	}

	static MethodologyDefinition TinyDef(string name = "myflow") => new(name,
	[
		new MethodologyKindDef("simple", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(
				["task"],
				[
					new WorkflowStatus("Todo", "Todo", StatusKind.Open),
					new WorkflowStatus("Done", "Done", StatusKind.TerminalOk),
				],
				[new MethodologyTransitionDef("Todo", "Done")]),
		]),
	]);

	[Fact]
	public async Task Upsert_Get_Delete_RoundTrip_DoesNotCreateBoards()
	{
		var ack = await _tasks.UpsertMethodologyTemplateAsync(Proj, "my-tmpl", TinyDef(), 0);
		ack.Changed.Should().BeTrue();
		ack.Key.Should().Be("my-tmpl");
		ack.Version.Should().BeGreaterThan(0);

		var view = await _tasks.GetMethodologyTemplateAsync(Proj, "my-tmpl");
		view.Should().NotBeNull();
		view!.Source.Should().Be("stored");
		view.Definition.Name.Should().Be("myflow");
		view.Version.Should().Be(ack.Version);

		// HARD INVARIANT: template write never provisions boards.
		(await _tasks.ListBoardsAsync(Proj)).Should().BeEmpty("template upsert must not create boards");
		// HARD INVARIANT: template write never mutates the live singleton definition.
		(await _tasks.GetMethodologyDefinitionAsync(Proj)).Should().BeNull("template upsert must not write methodology_defs");

		// Identical resubmit is a no-op.
		var again = await _tasks.UpsertMethodologyTemplateAsync(Proj, "my-tmpl", TinyDef(), ack.Version);
		again.Changed.Should().BeFalse();

		var del = await _tasks.DeleteMethodologyTemplateAsync(Proj, "my-tmpl", ack.Version);
		del.Changed.Should().BeTrue();
		(await _tasks.GetMethodologyTemplateAsync(Proj, "my-tmpl")).Should().BeNull();

		// Idempotent delete of missing.
		var del2 = await _tasks.DeleteMethodologyTemplateAsync(Proj, "my-tmpl", 0);
		del2.Changed.Should().BeFalse();
	}

	[Fact]
	public async Task Builtin_Get_And_List_AlwaysPresent()
	{
		var list = await _tasks.ListMethodologyTemplatesAsync(Proj);
		list.Select(i => i.Key).Should().Contain(["quartet", "classic", "simple"]);
		list.Where(i => i.Source == "builtin").Should().HaveCount(3);
		list.Should().OnlyContain(i => i.Source == "builtin", "empty project has only builtins");

		foreach (var key in new[] { "quartet", "classic", "simple" })
		{
			var view = await _tasks.GetMethodologyTemplateAsync(Proj, key);
			view.Should().NotBeNull();
			view!.Source.Should().Be("builtin");
			view.Version.Should().Be(0);
			view.Created.Should().BeNull();
			view.Definition.Kinds.Should().NotBeEmpty();
		}

		// Builtin write rejected.
		var upsert = () => _tasks.UpsertMethodologyTemplateAsync(Proj, "quartet", TinyDef("quartet"), 0);
		(await upsert.Should().ThrowAsync<ArgumentException>()).WithMessage("*builtin*");
		var delete = () => _tasks.DeleteMethodologyTemplateAsync(Proj, "classic", 0);
		(await delete.Should().ThrowAsync<ArgumentException>()).WithMessage("*builtin*");
	}

	[Fact]
	public async Task Snapshot_FromEffective_Def_Then_Preset_NoBoardMutation()
	{
		// No def → snapshot uses builtin quartet document.
		var s1 = await _tasks.SnapshotMethodologyTemplateAsync(Proj, "snap-a", 0);
		s1.Changed.Should().BeTrue();
		var v1 = await _tasks.GetMethodologyTemplateAsync(Proj, "snap-a");
		v1!.Source.Should().Be("stored");
		v1.Definition.Name.Should().Be("quartet");
		v1.Definition.Kinds.Select(k => k.Kind).Should().Equal("intake", "ideas", "spec", "work");

		// With a singleton def → effective snapshots THAT document (not the preset).
		var def = TinyDef("custom-live");
		await _tasks.DefineMethodologyAsync(Proj, def, 0);
		var s2 = await _tasks.SnapshotMethodologyTemplateAsync(Proj, "snap-b", 0);
		var v2 = await _tasks.GetMethodologyTemplateAsync(Proj, "snap-b");
		v2!.Definition.Name.Should().Be("custom-live");

		// Explicit preset source still works and does not clobber the live def.
		await _tasks.SnapshotMethodologyTemplateAsync(Proj, "snap-c", 0, from: "preset:classic");
		var v3 = await _tasks.GetMethodologyTemplateAsync(Proj, "snap-c");
		v3!.Definition.Name.Should().Be("classic");
		(await _tasks.GetMethodologyDefinitionAsync(Proj))!.Definition.Name.Should().Be("custom-live",
			"snapshot must not mutate the live singleton definition");

		// instance:<key> requires a real instance (methodology-instance-core).
		var inst = () => _tasks.SnapshotMethodologyTemplateAsync(Proj, "snap-d", 0, from: "instance:main");
		(await inst.Should().ThrowAsync<ArgumentException>()).WithMessage("*instance*not found*");

		(await _tasks.ListBoardsAsync(Proj)).Should().BeEmpty("snapshot must not create boards");
	}

	[Fact]
	public async Task DualRead_LegacyDefinition_AsTemplateKeyMethodology()
	{
		// No def → dual-read miss (null), list has no definition entry.
		(await _tasks.GetMethodologyTemplateAsync(Proj, "methodology")).Should().BeNull();
		(await _tasks.ListMethodologyTemplatesAsync(Proj)).Should().NotContain(i => i.Source == "definition");

		await _tasks.DefineMethodologyAsync(Proj, TinyDef("live-def"), 0);

		var dual = await _tasks.GetMethodologyTemplateAsync(Proj, "methodology");
		dual.Should().NotBeNull();
		dual!.Source.Should().Be("definition");
		dual.Definition.Name.Should().Be("live-def");
		dual.Version.Should().BeGreaterThan(0);

		var list = await _tasks.ListMethodologyTemplatesAsync(Proj);
		list.Should().Contain(i => i.Key == "methodology" && i.Source == "definition");

		// Stored template under key "methodology" shadows the dual-read (source=stored).
		var ack = await _tasks.UpsertMethodologyTemplateAsync(Proj, "methodology", TinyDef("tmpl-shadow"), 0);
		ack.Changed.Should().BeTrue();
		var shadowed = await _tasks.GetMethodologyTemplateAsync(Proj, "methodology");
		shadowed!.Source.Should().Be("stored");
		shadowed.Definition.Name.Should().Be("tmpl-shadow");

		// Live singleton def is untouched by the shadowing template write.
		(await _tasks.GetMethodologyDefinitionAsync(Proj))!.Definition.Name.Should().Be("live-def");
	}

	[Fact]
	public async Task Mcp_List_Get_Builtin_And_DeleteMissing()
	{
		var http = Http("tasks:read,tasks:write");
		var flags = Flags();

		var list = await TasksTools.MethodologyTemplateListAsync(http, flags, _tasks, Proj);
		list.Templates.Should().HaveCount(3);
		list.Templates.Select(t => t.Key).Should().Equal("quartet", "classic", "simple");

		var get = await TasksTools.MethodologyTemplateGetAsync(http, flags, _tasks, Proj, "simple");
		get.Found.Should().BeTrue();
		get.Source.Should().Be("builtin");
		get.Name.Should().Be("simple");
		get.Kinds.Should().NotBeNull().And.NotBeEmpty();

		var miss = await TasksTools.MethodologyTemplateGetAsync(http, flags, _tasks, Proj, "no-such-tmpl");
		miss.Found.Should().BeFalse();

		var del = await TasksTools.MethodologyTemplateDeleteAsync(http, flags, _tasks, Proj, "no-such-tmpl");
		del.Deleted.Should().BeFalse();
	}

	[Fact]
	public void RenderBuiltinTemplate_Simple_IsValidStandaloneKind()
	{
		var def = MethodologyPresets.RenderBuiltinTemplate("simple");
		def.Name.Should().Be("simple");
		def.Kinds.Should().ContainSingle().Which.Kind.Should().Be("simple");
		def.TagAxes.Should().BeEmpty();
	}
}

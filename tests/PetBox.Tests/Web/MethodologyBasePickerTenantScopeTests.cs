using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Workflow;
using PetBox.Web.Auth;
using PetBox.Web.Pages.Admin;

namespace PetBox.Tests.Web;

// methodology-base-picker-cross-tenant-leak: the "create a methodology → choose a base" picker used to
// enumerate EVERY project in EVERY workspace and render each one's live process (kinds/statuses/gates)
// into the page — so a WorkspaceAdmin of one tenant saw the methodologies of all the others. These pin
// the fix: builtin presets stay universal, but a template/instance base is confined to the projects in
// the workspaces the CALLER belongs to (resolved from the same yb:ws_roles claim the WorkspaceAdmin
// policy trusts), with the sysadmin keeping the fleet-wide view.
public sealed class MethodologyBasePickerTenantScopeTests : IDisposable
{
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public MethodologyBasePickerTenantScopeTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-basescope-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		var store = new TaskBoardStore(_db.Factory(), _factory);
		_tasks = new TasksService(store, new RelationStore(_factory), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{ ["Features:Tasks"] = "true" }).Build());

	// A minimal single-kind document — enough for the picker to render a base card + its graph preview.
	static MethodologyDefinition MiniDef(string name) => new(
		name,
		[
			new MethodologyKindDef("work", QuickAddAllowed: true,
			[
				new MethodologyWorkflowDef(["task"],
					[
						new WorkflowStatus("Todo", "Todo", StatusKind.Open),
						new WorkflowStatus("Done", "Done", StatusKind.TerminalOk),
					],
					[new MethodologyTransitionDef("Todo", "Done")]),
			]),
		]);

	void SeedWorkspace(string key)
	{
		using var db = _db.Factory().Open();
		db.Insert(new Workspace { Key = key, Name = key.ToUpperInvariant(), Description = "", CreatedAt = DateTime.UtcNow });
	}

	void SeedProject(string key, string ws)
	{
		using var db = _db.Factory().Open();
		db.Insert(new Project { Key = key, WorkspaceKey = ws, Name = key, Description = "" });
	}

	async Task InstallInstanceAsync(string project, string tmplKey, string instance, string defName)
	{
		await _tasks.UpsertMethodologyTemplateAsync(project, tmplKey, MiniDef(defName), 0);
		await _tasks.CreateMethodologyInstanceAsync(project, instance, "template", tmplKey);
	}

	static ProjectMethodologyModel Page(
		PetBoxDb db, TasksService tasks, ClaimsPrincipal user, string workspaceKey, string projectKey)
	{
		var dbf = db.Factory();
		var page = new ProjectMethodologyModel(
			new ProjectDirectory(dbf), Flags(), tasks, new WorkspaceMembershipService(dbf))
		{
			WorkspaceKey = workspaceKey,
			ProjectKey = projectKey,
			Step = "base",
		};
		page.PageContext = new PageContext
		{
			HttpContext = new DefaultHttpContext { User = user },
		};
		return page;
	}

	static ClaimsPrincipal Member(long userId, params (string WorkspaceKey, WorkspaceRole Role)[] roles) =>
		new(new ClaimsIdentity(
			[
				new Claim(PetBoxClaims.UserId, userId.ToString()),
				new Claim(PetBoxClaims.WorkspaceRoles, WorkspaceRoleAuthorizationHandler.SerializeRoles(roles)),
			],
			"Cookies"));

	static ClaimsPrincipal Sysadmin(long userId) =>
		new(new ClaimsIdentity(
			[
				new Claim(PetBoxClaims.UserId, userId.ToString()),
				new Claim(PetBoxClaims.IsSysAdmin, "true"),
			],
			"Cookies"));

	// THE regression test: the admin edits a project in `alpha` (their only workspace). The base picker
	// must offer builtin presets + `alpha`'s own methodologies, and NOTHING from `beta`.
	[Fact]
	public async Task A_workspace_admin_sees_only_their_own_workspaces_methodologies_as_bases()
	{
		SeedWorkspace("alpha");
		SeedWorkspace("beta");
		SeedProject("app", "alpha");    // the project being edited (no instance → base picker)
		SeedProject("app2", "alpha");   // a sibling in the SAME workspace — its methodology may be a base
		SeedProject("secret", "beta");  // another tenant — must never leak
		await InstallInstanceAsync("app2", "own-tmpl", "main", "alpha-own");
		await InstallInstanceAsync("secret", "sec-tmpl", "hidden", "beta-secret");

		var page = Page(_db, _tasks, Member(1, ("alpha", WorkspaceRole.Admin)), "alpha", "app");
		await page.OnGetAsync(default);

		page.Mode.Should().Be(ProjectMethodologyModel.EditorMode.Base);
		var refs = page.Bases.Select(b => b.Ref).ToList();

		refs.Should().Contain(r => r.StartsWith("preset:", StringComparison.Ordinal),
			"builtin presets stay available to everyone");
		refs.Should().Contain(r => r.Contains("app2", StringComparison.Ordinal),
			"a sibling project in the caller's own workspace still contributes its base");
		refs.Should().NotContain(r => r.Contains("secret", StringComparison.Ordinal),
			"another tenant's template/instance must never appear in the picker");
		page.BasePreviewsJson.Should().NotContain("secret",
			"and the foreign process must not be rendered into the preview island either");
	}

	// The legitimate fleet operator keeps the cross-workspace view — the fix scopes by membership, and a
	// sysadmin administers every workspace, so the free pass must survive.
	[Fact]
	public async Task A_sysadmin_still_sees_every_workspaces_methodologies()
	{
		SeedWorkspace("alpha");
		SeedWorkspace("beta");
		SeedProject("app", "alpha");
		SeedProject("secret", "beta");
		await InstallInstanceAsync("secret", "sec-tmpl", "hidden", "beta-secret");

		var page = Page(_db, _tasks, Sysadmin(9), "alpha", "app");
		await page.OnGetAsync(default);

		page.Mode.Should().Be(ProjectMethodologyModel.EditorMode.Base);
		page.Bases.Select(b => b.Ref).Should().Contain(r => r.Contains("secret", StringComparison.Ordinal),
			"a sysadmin administers every workspace — membership is not their leash");
	}
}

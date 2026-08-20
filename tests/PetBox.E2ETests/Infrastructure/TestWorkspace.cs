using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.E2ETests.Infrastructure;

// Shared helper: ensure a non-$system workspace exists, since $system rejects user-created
// projects. Tests create projects in this workspace.
public static class TestWorkspace
{
	public const string Key = "test";

	public static async Task EnsureAsync(IPage page)
	{
		await page.GotoAsync("/ui/admin/sys/workspaces");
		var row = page.GetByTestId("workspace-row").Filter(new() { HasText = Key });
		if (await row.CountAsync() > 0) return;

		await page.GetByTestId("admin-workspace-create-key").FillAsync(Key);
		await page.GetByTestId("admin-workspace-create-name").FillAsync("Test");
		await page.GetByTestId("admin-workspace-create-desc").FillAsync("E2E fixture workspace");
		await page.GetByTestId("admin-workspace-create-submit").ClickAsync();
		await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
	}

	// Seeds a workspace directly via the DB (fast — skips the admin-UI round trip EnsureAsync
	// takes) AND grants `memberUsername` membership through IWorkspaceMembershipService in the same
	// call — the production door WorkspaceProvisioning.CreateAsync uses for its own creator, and the
	// only door RS0030 (BannedSymbols.txt) allows into WorkspaceMembers.
	//
	// Why this exists: EnsureAsync's admin-UI path grants membership to the acting sysadmin for
	// free — WorkspaceProvisioning.CreateAsync claims the creator's admin slot as part of create
	// (ClaimAdminSlotAsync, see WorkspaceProvisioning.cs). A test that instead seeds a workspace with
	// a raw `db.InsertAsync(new Workspace{...})` — the shortcut most of this suite reaches for
	// because it doesn't need the UI — gets NONE of that: the seeded workspace has no members at
	// all, and under tenant-visibility-by-membership (spec user-zone-tenant-visibility) it is
	// invisible to every account, including the fixture's own sysadmin, on any user-zone route.
	// CrossScopeSearchTests hit exactly this and fixed itself with an inline AddMemberAsync call
	// (agent-toolchain-misleads-2026-08-20 §5); this helper folds that step into seeding itself so
	// the next raw-insert test cannot silently reproduce the gap — call this instead of re-deriving
	// the pattern by hand.
	//
	// Idempotent: an existing workspace row is left alone, and AddMemberAsync no-ops for an account
	// that is already a member — safe to call from InitializeAsync on every test run. A test that
	// deliberately wants a NON-member workspace (the negative half of a visibility test, e.g.
	// CrossScopeSearchTests' WsC) must keep seeding that one directly — this helper is for the
	// common case, not a replacement for the negative case.
	public static async Task SeedAsync(
		IServiceProvider services,
		string workspaceKey,
		string workspaceName,
		string memberUsername = WebAppFixture.AdminUsername,
		WorkspaceRole role = WorkspaceRole.Admin)
	{
		using var scope = services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();

		if (!await db.Workspaces.AnyAsync(w => w.Key == workspaceKey))
			await db.InsertAsync(new Workspace
			{
				Key = workspaceKey,
				Name = workspaceName,
				CreatedAt = DateTime.UtcNow,
			});

		var members = scope.ServiceProvider.GetRequiredService<IWorkspaceMembershipService>();
		await members.AddMemberAsync(
			workspaceKey, memberUsername, AddMemberMode.AddExisting, password: null, workspaceQuota: null, role);
	}
}

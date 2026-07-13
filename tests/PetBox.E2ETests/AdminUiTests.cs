using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.E2ETests.Infrastructure;

namespace PetBox.E2ETests;

// E2E for the post-launch admin UI: sys-level user CRUD, agent keys, and the
// navigation cleanup (sidebar-only, no duplicate tab strips).
[Collection(nameof(UiCollection))]
public sealed class AdminUiTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	IBrowserContext? _ctx;
	IPage? _page;

	public async Task InitializeAsync()
	{
		_ctx = await app.NewContextAsync(authenticated: true);
		_page = await _ctx.NewPageAsync();
	}

	public async Task DisposeAsync()
	{
		if (_ctx is not null)
		{
			await TraceArtifact.StopAndSaveAsync(_ctx, output);
			await _ctx.CloseAsync();
		}
	}

	// Minting moved to the project's Connect page: mint there, confirm the copy-ready wire
	// instruction carries the fresh key + per-project env var, then revoke from the sysadmin
	// management list (the key is a normal DB row and still shows up there).
	[Fact]
	public async Task Connect_Mint_Instruction_And_Revoke()
	{
		var name = $"e2e-agent-{System.Guid.NewGuid():N}";

		await _page!.GotoAsync("/ui/admin/ws/$system/projects/$system/connect");
		await _page.GetByTestId("connect-key-name").FillAsync(name);
		// Agent default scopes are pre-checked.
		await _page.GetByTestId("connect-mint-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		// Raw key shown once.
		var keyValue = (await _page.GetByTestId("connect-key-value").InnerTextAsync()).Trim();
		keyValue.Should().StartWith("yb_key_");

		// The one-command instruction carries the kit invocation, the fresh key and the env var.
		var npx = await _page.GetByTestId("connect-npx-command").InnerTextAsync();
		npx.Should().Contain("npx -y petbox-wire@latest");
		npx.Should().Contain(keyValue);
		npx.Should().Contain("--env PETBOX_SYSTEM_API_KEY");

		// Appears in the sysadmin management list; revoke it there.
		await _page.GotoAsync("/ui/admin/sys/agent-keys");
		var row = _page.GetByTestId("agent-key-row").Filter(new() { HasText = name });
		await Expect(row).ToHaveCountAsync(1);

		_page.Dialog += (_, d) => d.AcceptAsync();
		await row.GetByTestId("agent-key-revoke").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		await Expect(_page.GetByTestId("sys-agent-keys-table").Filter(new() { HasText = name })).ToHaveCountAsync(0);
	}

	[Fact]
	public async Task SysUsers_Create_Reset_Delete()
	{
		var username = $"e2e-user-{System.Guid.NewGuid():N}"[..20];

		await _page!.GotoAsync("/ui/admin/sys/users");
		await _page.GetByTestId("sys-user-create-username").FillAsync(username);
		await _page.GetByTestId("sys-user-create-password").FillAsync("pw-initial-123");

		// workspace-self-provisioning: the workspace allowance ships EMPTY and must be filled in —
		// the admin decides the number, the form never guesses one (not even 0).
		var quota = _page.GetByTestId("sys-user-create-quota");
		await Expect(quota).ToHaveValueAsync("");
		await quota.FillAsync("1");

		await _page.GetByTestId("sys-user-create-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		var row = _page.GetByTestId("sys-user-row").Filter(new() { HasText = username });
		await Expect(row).ToHaveCountAsync(1);

		// Reset password.
		await row.GetByTestId("sys-user-reset-password").FillAsync("pw-rotated-456");
		await row.GetByTestId("sys-user-reset-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		// Delete.
		_page.Dialog += (_, d) => d.AcceptAsync();
		await _page.GetByTestId("sys-user-row").Filter(new() { HasText = username })
			.GetByTestId("sys-user-delete").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		await Expect(_page.GetByTestId("sys-users-table")).Not.ToContainTextAsync(username);
	}

	[Fact]
	public async Task SysUsers_BootstrapAdmin_NotDeletable()
	{
		await _page!.GotoAsync("/ui/admin/sys/users");

		// The bootstrap admin row is marked and has no delete button.
		var adminRow = _page.GetByTestId("sys-user-row").Filter(new() { HasText = WebAppFixture.AdminUsername });
		await Expect(adminRow).ToContainTextAsync("bootstrap");
		await Expect(adminRow.GetByTestId("sys-user-delete")).ToHaveCountAsync(0);
	}

	// ui-deploy-form-layout: the "New deployment" form is broken out of a single flex-wrap row
	// into labelled sections (Identity / Networking / Resources / Runtime). Render-presence guard:
	// every grouping marker renders AND every field still submits (the POST contract is unchanged —
	// same field names/handler, so the fields must still be on the page).
	[Fact]
	public async Task Deploy_NewDeploymentForm_GroupedIntoSections()
	{
		await _page!.GotoAsync("/ui/admin/sys/deploy");

		// The four grouped sections.
		await Expect(_page.GetByTestId("dep-section-identity")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("dep-section-networking")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("dep-section-resources")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("dep-section-runtime")).ToBeVisibleAsync();

		// Every posting field (and the submit) still renders — the contract is intact.
		foreach (var id in new[]
			{ "dep-service", "dep-project", "dep-node", "dep-image", "dep-ports",
			  "dep-volumes", "dep-restart", "dep-memory", "dep-domain", "dep-add" })
			await Expect(_page.GetByTestId(id)).ToHaveCountAsync(1);
	}

	// admin-ui-apikey-edit-lastused, the card's acceptance, driven through the real UI:
	// a fresh key reads "never" (not an empty cell, not 1970); the editor renames it and replaces its
	// scope set; and on a cross-project key the default project can be both SET and CLEARED.
	[Fact]
	public async Task AgentKeys_LastUsed_And_Editor_RenameRescopeAndDefaultProject()
	{
		var name = $"e2e-edit-{System.Guid.NewGuid():N}";

		await _page!.GotoAsync("/ui/admin/ws/$system/projects/$system/connect");
		await _page.GetByTestId("connect-key-name").FillAsync(name);
		await _page.GetByTestId("connect-mint-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		await _page.GotoAsync("/ui/admin/sys/agent-keys");
		var row = _page.GetByTestId("agent-key-row").Filter(new() { HasText = name });
		await Expect(row).ToHaveCountAsync(1);

		// Freshly minted, never authenticated — the cell SAYS so.
		await Expect(row.GetByTestId("agent-key-never-used")).ToBeVisibleAsync();
		await Expect(row.GetByTestId("agent-key-last-used")).ToHaveCountAsync(0);

		// Rename + replace the scope set through the editor.
		var renamed = $"{name}-renamed";
		await row.GetByTestId("agent-key-edit").ClickAsync();
		await row.GetByTestId("agent-key-edit-name").FillAsync(renamed);
		await row.GetByTestId("agent-key-edit-scope-tasks:read").Locator("input").CheckAsync();
		await row.GetByTestId("agent-key-edit-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		var renamedRow = _page.GetByTestId("agent-key-row").Filter(new() { HasText = renamed });
		await Expect(renamedRow).ToHaveCountAsync(1);
		await Expect(renamedRow).ToContainTextAsync("tasks:read");

		// Clean up the minted key.
		_page.Dialog += (_, d) => d.AcceptAsync();
		await renamedRow.GetByTestId("agent-key-revoke").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		await Expect(_page.GetByTestId("sys-agent-keys-table").Filter(new() { HasText = renamed })).ToHaveCountAsync(0);
	}

	// The default project is a CROSS-PROJECT-key concept, and the UI cannot mint one (apikey_create
	// only) — so the row is seeded straight into the DB, then driven through the editor. Clearing it
	// is the half that breaks when "" is stored instead of NULL.
	[Fact]
	public async Task AgentKeys_Editor_SetsAndClearsDefaultProjectOnCrossProjectKey()
	{
		var name = $"e2e-wildcard-{System.Guid.NewGuid():N}";
		var key = $"yb_key_{System.Guid.NewGuid():N}";

		using (var scope = app.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			await db.InsertAsync(new ApiKey
			{
				Key = key,
				ProjectKey = "*",
				Scopes = "tasks:read",
				Name = name,
				CreatedAt = System.DateTime.UtcNow,
				DefaultProjectKey = null,
			});
		}

		await _page!.GotoAsync("/ui/admin/sys/agent-keys");
		var row = _page.GetByTestId("agent-key-row").Filter(new() { HasText = name });
		await Expect(row).ToHaveCountAsync(1);
		await Expect(row.GetByTestId("agent-key-default-project")).ToHaveCountAsync(0);

		// SET it.
		await row.GetByTestId("agent-key-edit").ClickAsync();
		await row.GetByTestId("agent-key-edit-default-project").FillAsync("$system");
		await row.GetByTestId("agent-key-edit-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		row = _page.GetByTestId("agent-key-row").Filter(new() { HasText = name });
		await Expect(row.GetByTestId("agent-key-default-project")).ToContainTextAsync("$system");

		// CLEAR it — an empty field means NULL, not "".
		await row.GetByTestId("agent-key-edit").ClickAsync();
		await row.GetByTestId("agent-key-edit-default-project").FillAsync("");
		await row.GetByTestId("agent-key-edit-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		row = _page.GetByTestId("agent-key-row").Filter(new() { HasText = name });
		await Expect(row.GetByTestId("agent-key-default-project")).ToHaveCountAsync(0);

		using (var scope = app.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			var stored = db.ApiKeys.First(k => k.Key == key);
			stored.DefaultProjectKey.Should().BeNull("clearing the field must store NULL, never an empty string");
			await db.ApiKeys.Where(k => k.Key == key).DeleteAsync();
		}
	}

	[Fact]
	public async Task Nav_SidebarHasAgentKeys_NoDuplicateTabs()
	{
		await _page!.GotoAsync("/ui/admin/sys/users");

		// Sidebar is the single navigation surface; Agent keys lives there now.
		await Expect(_page.GetByTestId("admin-side-sys-agent-keys")).ToBeVisibleAsync();

		// The duplicate tab strips were removed.
		await Expect(_page.GetByTestId("sys-tabs")).ToHaveCountAsync(0);

		await _page.GotoAsync("/ui/admin/ws/$system/projects");
		await Expect(_page.GetByTestId("ws-admin-tabs")).ToHaveCountAsync(0);
	}
}

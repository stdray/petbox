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

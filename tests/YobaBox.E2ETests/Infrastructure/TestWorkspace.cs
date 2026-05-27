using Microsoft.Playwright;

namespace YobaBox.E2ETests.Infrastructure;

// Shared helper: ensure a non-$system workspace exists, since $system rejects user-created
// projects. Tests create projects in this workspace.
public static class TestWorkspace
{
	public const string Key = "test";

	public static async Task EnsureAsync(IPage page)
	{
		await page.GotoAsync("/ui/sys/workspaces");
		var row = page.GetByTestId("workspace-row").Filter(new() { HasText = Key });
		if (await row.CountAsync() > 0) return;

		await page.GetByTestId("admin-workspace-create-key").FillAsync(Key);
		await page.GetByTestId("admin-workspace-create-name").FillAsync("Test");
		await page.GetByTestId("admin-workspace-create-desc").FillAsync("E2E fixture workspace");
		await page.GetByTestId("admin-workspace-create-submit").ClickAsync();
		await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
	}
}

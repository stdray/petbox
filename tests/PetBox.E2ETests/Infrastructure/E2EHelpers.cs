namespace PetBox.E2ETests.Infrastructure;

public static class E2EHelpers
{
	/// <summary>
	/// Clear all localStorage keys so no test leaks state (sidebar pin, kql panel
	/// pin, etc.) to a sibling test sharing the same browser context.  Call at the
	/// START of every test that assumes fresh default state.
	/// </summary>
	public static async Task ClearLocalStorageAsync(IPage page)
	{
		try
		{
			await page.EvaluateAsync("() => localStorage.clear()");
		}
		catch
		{
			// best-effort
		}
	}
}

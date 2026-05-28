namespace PetBox.E2ETests.Pages;

public sealed class LoginPage(IPage page)
{
	public async Task GotoAsync(string? returnUrl = null)
	{
		var path = returnUrl is null ? "/Login" : $"/Login?ReturnUrl={Uri.EscapeDataString(returnUrl)}";
		await page.GotoAsync(path);
	}

	public async Task SubmitAsync(string username, string password)
	{
		await page.GetByTestId("login-username").FillAsync(username);
		await page.GetByTestId("login-password").FillAsync(password);
		await page.GetByTestId("login-submit").ClickAsync();
	}

	public Task AssertErrorVisibleAsync() =>
		Expect(page.GetByTestId("login-error")).ToBeVisibleAsync();

	public Task AssertStillOnLoginAsync() =>
		Expect(page.GetByTestId("login-submit")).ToBeVisibleAsync();
}

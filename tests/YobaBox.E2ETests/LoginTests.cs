using YobaBox.E2ETests.Infrastructure;
using YobaBox.E2ETests.Pages;

namespace YobaBox.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class LoginTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	IBrowserContext? _ctx;
	IPage? _page;

	public async Task InitializeAsync()
	{
		_ctx = await app.NewContextAsync(authenticated: false);
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

	[Fact]
	public async Task Wrong_Password_Shows_Error()
	{
		var login = new LoginPage(_page!);
		await login.GotoAsync();
		await login.SubmitAsync(WebAppFixture.AdminUsername, "definitely-wrong");
		await login.AssertErrorVisibleAsync();
		await login.AssertStillOnLoginAsync();
	}

	[Fact]
	public async Task Admin_Login_Redirects_To_Dashboard()
	{
		var login = new LoginPage(_page!);
		await login.GotoAsync();
		await login.SubmitAsync(WebAppFixture.AdminUsername, WebAppFixture.AdminPassword);

		await Expect(_page!.GetByTestId("dashboard-title")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task Login_Preserves_ReturnUrl()
	{
		var login = new LoginPage(_page!);
		await login.GotoAsync("/ui/$system/$system/settings");
		await login.SubmitAsync(WebAppFixture.AdminUsername, WebAppFixture.AdminPassword);

		// Should redirect to /admin/projects, not /Login
		await Task.Delay(300);
		_page!.Url.Should().Contain("/ui/$system/$system/settings");
	}

	[Fact]
	public async Task Missing_Antiforgery_Returns_400()
	{
		using var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
		using var content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["username"] = WebAppFixture.AdminUsername,
			["password"] = WebAppFixture.AdminPassword,
		});
		var res = await http.PostAsync(new Uri("/Login", UriKind.Relative), content);
		((int)res.StatusCode).Should().Be(400);
	}
}

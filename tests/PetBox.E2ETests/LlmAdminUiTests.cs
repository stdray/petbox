using Microsoft.Extensions.DependencyInjection;
using PetBox.E2ETests.Infrastructure;
using PetBox.LlmRouter.Registry;

namespace PetBox.E2ETests;

// llm-l5: THE WHOLE POINT, END TO END. A save in the admin UI must change what the RUNTIME resolves.
//
// Before this, the page wrote the old ConfigBindings store while the router read the levelled
// registry in core.db: the owner edited a route, the page said "Saved.", and every call still went
// to the old model. A UI-only assertion ("the new value is on the page") would have passed happily
// through that entire bug — the page always showed you what the page had just written. So the
// assertion here is deliberately NOT in the browser: after driving the form, the test resolves the
// project through ILlmRegistryLevelResolver, the same interface CapabilityRouter routes through, and
// demands the NEW value.
[Collection(nameof(UiCollection))]
public sealed class LlmAdminUiTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	// The reserved built-in workspace, whose level IS the system registry.
	const string Url = "/ui/$system/$system/llm";

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

	[Fact]
	public async Task Editing_a_route_in_the_admin_UI_changes_what_the_runtime_resolves()
	{
		await _page!.GotoAsync(Url);

		// 1. a provider…
		await _page.GetByTestId("llm-add").GetByPlaceholder("e.g. home").FillAsync("e2e-home");
		await _page.GetByTestId("llm-add").GetByPlaceholder("https://host:port").FillAsync("https://e2e-home:1234");
		await _page.GetByTestId("llm-add-save").ClickAsync();
		await Expect(_page.GetByTestId("llm-saved")).ToBeVisibleAsync();

		// 2. …and a route through it.
		var add = _page.GetByTestId("llm-route-add");
		await add.GetByPlaceholder("e.g. deepseek-chat").FillAsync("model-before");
		await add.GetByRole(AriaRole.Combobox).Nth(0).SelectOptionAsync("Chat");
		await _page.GetByTestId("llm-route-add-save").ClickAsync();
		await Expect(_page.GetByTestId("llm-saved")).ToBeVisibleAsync();

		(await ResolvedChatModel()).Should().Be("model-before", "the fresh route must already be live for the runtime");

		// 3. THE EDIT. Change the model on the stored row and save it.
		var row = _page.GetByTestId("llm-route-row").First;
		await row.Locator("input[name='model']").FillAsync("model-after");
		await row.GetByTestId("llm-route-save").ClickAsync();
		await Expect(_page.GetByTestId("llm-saved")).ToBeVisibleAsync();

		// 4. The runtime — not the page — must see the new model.
		(await ResolvedChatModel()).Should().Be("model-after",
			"a save in the admin UI that the resolver cannot see is exactly the bug llm-l5 exists to close");
	}

	// The registry as the ROUTER sees it: resolved for the project, out of core.db's llm_* tables.
	async Task<string?> ResolvedChatModel()
	{
		using var scope = app.Services.CreateScope();
		var resolver = scope.ServiceProvider.GetRequiredService<ILlmRegistryLevelResolver>();
		var resolved = await resolver.ResolveAsync("$system");
		return resolved.Registry.Routes.SingleOrDefault(r => r.Endpoint == "e2e-home")?.Model;
	}
}

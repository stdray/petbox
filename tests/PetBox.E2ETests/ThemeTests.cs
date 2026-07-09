using System.Globalization;
using PetBox.E2ETests.Infrastructure;

namespace PetBox.E2ETests;

// UI theme + markdown-typography guarantees (feat/ui-theme):
//   1. The Theme preference is honest: choosing Light actually flips <html data-theme> to
//      "light" AND paints a light background (the daisyUI light theme is live, not a hardcode).
//   2. Node bodies and comment bodies render markdown at the SAME size — the unified _MdBody
//      partial emits `md-body text-base` for both, so a comment is no longer dimmed/shrunk
//      (text-sm/opacity-80) relative to a node body.
//
// The E2E fixture disables the Tasks feature, so there is no real task-node/comment page to
// render both bodies on. The font-size guarantee is therefore asserted against the SHIPPED
// compiled app.css by injecting the exact markup the partial emits (node body; comment body
// inside its text-sm metadata wrapper) onto a live page and comparing computed font-size —
// which is precisely the regression the fix closes.
[Collection(nameof(UiCollection))]
public sealed class ThemeTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
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
			// Restore the shared admin user's theme so this test doesn't tint sibling tests.
			try
			{
				await SetThemeAsync("Dark");
			}
			catch
			{
				// best-effort cleanup
			}
			await TraceArtifact.StopAndSaveAsync(_ctx, output);
			await _ctx.CloseAsync();
		}
	}

	async Task SetThemeAsync(string value)
	{
		await _page!.GotoAsync("/ui/me/preferences");
		await Expect(_page.GetByTestId("setting-input-Theme")).ToBeVisibleAsync();
		await _page.GetByTestId("setting-input-Theme").SelectOptionAsync(value);
		await _page.GetByTestId("me-preferences-form-submit").ClickAsync();
		await Expect(_page.GetByTestId("me-preferences-form-submit")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task Selecting_Light_Theme_Applies_Light_DataTheme_And_Light_Background()
	{
		await SetThemeAsync("Light");

		// Fresh load: the stored preference must render server-side as data-theme="light".
		await _page!.GotoAsync("/ui/me/preferences");
		await Expect(_page.Locator("html[data-theme='light']")).ToHaveCountAsync(1);

		// …and the background must actually be light. `bg-base-100` resolves to oklch(var(--b1));
		// read the computed value and reduce it to a lightness score that tolerates whichever
		// serialization Chromium returns (oklch(L …) or rgb(…)). Light theme L≈1, dark L≈0.13.
		var lightness = await _page.EvaluateAsync<double>(@"() => {
			const s = getComputedStyle(document.body).backgroundColor.trim();
			let m = s.match(/^oklch\(\s*([\d.]+%?)/i);
			if (m) { const v = m[1]; return v.endsWith('%') ? parseFloat(v) / 100 : parseFloat(v); }
			m = s.match(/rgba?\(([^)]+)\)/i);
			if (m) { const [r, g, b] = m[1].split(',').map(x => parseFloat(x)); return (0.2126*r + 0.7152*g + 0.0722*b) / 255; }
			return -1;
		}");
		lightness.Should().BeGreaterThan(0.5, "the light theme must paint a light background");
	}

	[Fact]
	public async Task Node_Body_And_Comment_Body_Render_Same_Font_Size()
	{
		// Any authenticated page loads the compiled app.css; measure against it.
		await _page!.GotoAsync("/ui/$system");
		await Expect(_page.GetByTestId("dashboard-title")).ToBeVisibleAsync();

		// Reproduce exactly what _MdBody emits: node body straight on the surface, and comment
		// body nested inside the _CommentThread `text-sm` metadata wrapper. The partial's explicit
		// `text-base` must win over the inherited `text-sm`, so both end up the same size.
		var sizes = await _page.EvaluateAsync<double[]>(@"() => {
			const holder = document.createElement('div');
			holder.innerHTML =
				'<div class=""md-body text-base"" data-testid=""t-node"">x</div>' +
				'<div class=""text-sm""><div class=""md-body text-base"" data-testid=""t-comment"">x</div></div>';
			document.body.appendChild(holder);
			const px = (sel) => parseFloat(getComputedStyle(holder.querySelector(sel)).fontSize);
			const out = [px('[data-testid=t-node]'), px('[data-testid=t-comment]')];
			holder.remove();
			return out;
		}");

		sizes.Should().HaveCount(2);
		var node = sizes[0];
		var comment = sizes[1];
		node.Should().BeGreaterThan(0);
		comment.Should().Be(node, "node and comment markdown bodies must render at the same font-size");
		// text-base is 1rem = 16px; assert the comment isn't the dimmed text-sm (14px) of before.
		comment.Should().BeApproximately(16.0, 0.5,
			$"comment body should be text-base, not text-sm (got {comment.ToString(CultureInfo.InvariantCulture)}px)");
	}
}

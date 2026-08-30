using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;

namespace PetBox.E2ETests;

// Work `md-code-wrap-not-scroll`. A long command in a code block used to run off the right edge and
// sit behind a horizontal scrollbar; the owner's call is that it wraps instead. The change is two
// CSS declarations on `.md-body pre` (`white-space: pre-wrap` + `overflow-wrap: anywhere`), and
// ts/design-tokens.test.ts already pins that they are DECLARED.
//
// Declared is not the claim, though. The claim is about a laid-out page: no code block on it has
// anything to scroll horizontally, at any width, and the text inside is still the source text
// character for character. Only a browser can answer either, so that is what this file measures —
// scrollWidth against clientWidth on every <pre> the page renders, and the long line read back out
// of the DOM.
//
// The body below is built to cover the three shapes the card called out, plus the boundary it told
// the work not to cross:
//   * a SHORT block (4 source lines) whose one line is 298 characters — wrapping with no fold
//     wrapper anywhere near it;
//   * a LONG block (12 source lines, several of them long) — wrapping underneath the height cap
//     from `md-code-block-height-cap`, which is where the two features meet;
//   * a block inside `## Section`, where the block sits on a different surface;
//   * a wide TABLE, which must STILL scroll — reflowing a table destroys the row alignment that
//     makes it readable, so `.md-table-scroll` was explicitly out of scope.
[Collection(nameof(UiCollection))]
public sealed class CodeBlockWrapTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	const string Ws = "cbw-ws";
	const string Proj = "cbw-proj";
	const string Board = "cbwboard";
	const string NodeKey = "n1";

	// The measured worst case from the live card that prompted the work
	// (kek-devices/classic/vpn-router-guide-for-friend): 298 characters, and the tail of it is one
	// unbroken token, so `pre-wrap` on its own could not break it.
	const int LongLineLength = 298;

	static readonly string LongLine =
		"ip route add 10.8.0.0/24 via 192.168.1.1 dev eth0 src 192.168.1.42 table "
		+ new string('x', LongLineLength - 73);

	// Wide enough that a 298-character line still fits nowhere near it, narrow enough to be a real
	// phone. Both are asserted, because "no horizontal scroll" that only holds on a desktop is the
	// bug with extra steps.
	const int WideWidth = 1440;
	const int NarrowWidth = 380;

	IBrowserContext? _ctx;
	string _nodeUrl = "";

	static string Body()
	{
		// 4 source lines: under FoldLineThreshold (10), so no fold wrapper — plain wrapping.
		var shortBlock = $"```bash\necho start\n{LongLine}\necho done\ncd /tmp\n```";
		// 12 source lines: over the threshold, so this one is folded AND wrapped.
		var longBlock = "```bash\n"
			+ string.Join("\n", Enumerable.Range(1, 10).Select(i => i % 4 == 0 ? LongLine : $"echo step{i}"))
			+ $"\n{LongLine}\necho end\n```";
		var table = "| Key | Value |\n| --- | --- |\n"
			+ $"| a | {new string('w', 200)} |\n| b | {new string('v', 200)} |\n";

		return $"""
			Intro paragraph.

			{shortBlock}

			## Section heading

			A block on the section's own surface:

			{shortBlock}

			{longBlock}

			{table}
			""";
	}

	public async ValueTask InitializeAsync()
	{
		using var scope = app.Services.CreateScope();
		using (var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open())
		{
			if (!await db.Workspaces.AnyAsync(w => w.Key == Ws))
				await db.InsertAsync(new Workspace { Key = Ws, Name = Ws, CreatedAt = DateTime.UtcNow });
			if (!await db.Projects.AnyAsync(p => p.Key == Proj))
				await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Code block wrap" });
		}

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		if (!await tasks.BoardExistsAsync(Proj, Board))
			await tasks.CreateBoardAsync(Proj, Board, "simple", "md-code-wrap-not-scroll fixture", null, null);
		var existing = await tasks.GetAsync(Proj, Board, includeClosed: true);
		if (existing.Nodes.All(n => n.Key != NodeKey))
			await tasks.UpsertAsync(Proj, Board, [new NodePatch { Key = NodeKey, Title = "N1", Body = Body(), Priority = 10 }]);

		_nodeUrl = $"/ui/{Ws}/{Proj}/tasks/{Board}/{NodeKey}";
		_ctx = await app.NewContextAsync(authenticated: true);
	}

	public async ValueTask DisposeAsync()
	{
		if (_ctx is not null)
		{
			await TraceArtifact.StopAndSaveAsync(_ctx, output);
			await _ctx.CloseAsync();
		}
	}

	sealed class BlockProbe
	{
		[JsonPropertyName("index")]
		public int Index { get; set; }
		[JsonPropertyName("scrollWidth")]
		public double ScrollWidth { get; set; }
		[JsonPropertyName("clientWidth")]
		public double ClientWidth { get; set; }
		[JsonPropertyName("whiteSpace")]
		public string WhiteSpace { get; set; } = "";
		[JsonPropertyName("overflowWrap")]
		public string OverflowWrap { get; set; } = "";
		[JsonPropertyName("inFold")]
		public bool InFold { get; set; }
		[JsonPropertyName("clientHeight")]
		public double ClientHeight { get; set; }
		[JsonPropertyName("scrollHeight")]
		public double ScrollHeight { get; set; }
		// Every line of the block, exactly as the DOM holds it — the copy-fidelity side.
		[JsonPropertyName("lines")]
		public string[] Lines { get; set; } = [];
	}

	sealed class PageProbe
	{
		[JsonPropertyName("blocks")]
		public BlockProbe[] Blocks { get; set; } = [];
		// The prose column itself: a block that overflows its own box would push this too.
		[JsonPropertyName("bodyScrollWidth")]
		public double BodyScrollWidth { get; set; }
		[JsonPropertyName("bodyClientWidth")]
		public double BodyClientWidth { get; set; }
		// The out-of-scope boundary.
		[JsonPropertyName("tableFound")]
		public bool TableFound { get; set; }
		[JsonPropertyName("tableOverflowX")]
		public string TableOverflowX { get; set; } = "";
		[JsonPropertyName("tableScrolls")]
		public bool TableScrolls { get; set; }
	}

	const string ProbeJs = """
		() => {
			const body = document.querySelector('[data-testid="node-body"]');
			const cs = (el) => el ? getComputedStyle(el) : null;
			const tableWrap = body?.querySelector('.md-table-scroll') ?? null;
			return {
				blocks: [...(body?.querySelectorAll('pre') ?? [])].map((pre, index) => ({
					index,
					scrollWidth: pre.scrollWidth,
					clientWidth: pre.clientWidth,
					whiteSpace: cs(pre).whiteSpace,
					overflowWrap: cs(pre).overflowWrap,
					inFold: !!pre.closest('.md-code-fold'),
					clientHeight: pre.clientHeight,
					scrollHeight: pre.scrollHeight,
					lines: (pre.textContent ?? '').split('\n'),
				})),
				bodyScrollWidth: body ? body.scrollWidth : NaN,
				bodyClientWidth: body ? body.clientWidth : NaN,
				tableFound: !!tableWrap,
				tableOverflowX: cs(tableWrap)?.overflowX ?? '(missing)',
				tableScrolls: tableWrap ? tableWrap.scrollWidth > tableWrap.clientWidth + 1 : false,
			};
		}
		""";

	async Task<PageProbe> LoadAtAsync(int width)
	{
		var page = await _ctx!.NewPageAsync();
		await page.SetViewportSizeAsync(width, 900);
		await page.GotoAsync(_nodeUrl);
		await Expect(page.GetByTestId("node-body")).ToBeVisibleAsync();

		// The sidebar is DOCKED by default in every zone (SidebarPinTests pins that), and at phone
		// width the dock is 256px of a 380px viewport: `.drawer` resolves to `256px 124px` and the
		// prose column comes out ~24px wide, one character per line. That has nothing to do with
		// this work — measured on the same page with `white-space: pre` forced back on, the whole
		// ancestor chain is identical to the pixel — so a narrow run against a docked sidebar would
		// be asserting about that layout instead of about wrapping. Reading on a phone means the
		// sidebar is not docked, so undock it first and measure the page a reader would actually see.
		if (width < 700)
		{
			var pin = page.GetByTestId("nav-sidebar-pin");
			if (await pin.GetAttributeAsync("aria-pressed") == "true")
			{
				await pin.ClickAsync();
				await Expect(page.GetByTestId("app-drawer")).Not.ToHaveClassAsync(new Regex(@"\bdrawer-open\b"));
			}
		}

		return await page.EvaluateAsync<PageProbe>(ProbeJs);
	}

	[Theory]
	[InlineData(WideWidth)]
	[InlineData(NarrowWidth)]
	public async Task NoCodeBlock_HasAnythingToScrollHorizontally(int width)
	{
		var probe = await LoadAtAsync(width);

		probe.Blocks.Should().HaveCountGreaterThanOrEqualTo(3,
			"the fixture covers a short block, a block inside a section and a folded one");

		foreach (var block in probe.Blocks)
		{
			// One pixel of slack for sub-pixel layout rounding; a 298-character line that failed to
			// wrap overflows by thousands, so nothing real can hide under it.
			block.ScrollWidth.Should().BeLessThanOrEqualTo(block.ClientWidth + 1,
				$"code block #{block.Index} (inFold={block.InFold}) must have nothing to scroll at {width}px — "
				+ $"scrollWidth {block.ScrollWidth} vs clientWidth {block.ClientWidth}");
			block.WhiteSpace.Should().Be("pre-wrap", $"code block #{block.Index} must wrap its long lines");
			block.OverflowWrap.Should().Be("anywhere",
				$"code block #{block.Index} must be able to break a token with no space in it");
		}

		probe.BodyScrollWidth.Should().BeLessThanOrEqualTo(probe.BodyClientWidth + 1,
			$"a code block must not push the prose column wider than the viewport at {width}px");
	}

	[Theory]
	[InlineData(WideWidth)]
	[InlineData(NarrowWidth)]
	public async Task TheLongLine_IsStillTheSourceLine_NoBreakCharactersInserted(int width)
	{
		var probe = await LoadAtAsync(width);

		// Wrapping is a drawing decision: the DOM text is untouched, so a selection or a copy still
		// yields the command that can be pasted into a shell. A soft hyphen or zero-width space
		// would satisfy every layout assertion above and break exactly this.
		var carriers = probe.Blocks.Where(b => b.Lines.Any(l => l.Contains("ip route add", StringComparison.Ordinal))).ToList();
		carriers.Should().NotBeEmpty("the long line must be somewhere in the rendered body");

		foreach (var block in carriers)
		{
			var line = block.Lines.First(l => l.Contains("ip route add", StringComparison.Ordinal));
			line.Should().Be(LongLine,
				$"code block #{block.Index} must hold the exact source line at {width}px, not a re-broken one");
			line.Length.Should().Be(LongLineLength);
		}
	}

	[Theory]
	[InlineData(WideWidth)]
	[InlineData(NarrowWidth)]
	public async Task WrappingDidNotDisableTheHeightCap(int width)
	{
		var probe = await LoadAtAsync(width);

		// The stated interaction with `md-code-block-height-cap`: the renderer folds on SOURCE lines
		// (>10) while the CSS cap is expressed in VISIBLE line boxes (10lh), so with wrapping on, a
		// folded block shows fewer than ten source lines. That is the accepted trade — what must NOT
		// happen is the cap quietly stopping to clip at all, which is what would make a 12-line block
		// with three wrapped lines take over the page again.
		var folded = probe.Blocks.Where(b => b.InFold).ToList();
		folded.Should().NotBeEmpty("the 12-line block must still be folded");
		foreach (var block in folded)
			block.ScrollHeight.Should().BeGreaterThan(block.ClientHeight + 1,
				$"the folded block must still be clipped at {width}px — wrapping made it taller, not exempt");
	}

	[Theory]
	[InlineData(WideWidth)]
	[InlineData(NarrowWidth)]
	public async Task AWideTable_StillScrolls_TheWrappingDecisionIsCodeBlocksOnly(int width)
	{
		var probe = await LoadAtAsync(width);

		probe.TableFound.Should().BeTrue("the fixture body renders a wide table");
		probe.TableOverflowX.Should().Be("auto",
			"a table cannot reflow into a narrow column without destroying its row alignment — "
			+ "`.md-table-scroll` was explicitly out of scope for md-code-wrap-not-scroll");
		probe.TableScrolls.Should().BeTrue(
			$"the wide table must still have somewhere to scroll at {width}px");
	}
}

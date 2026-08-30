using System.Text.Json.Serialization;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;

namespace PetBox.E2ETests;

// Work `md-code-block-height-cap`: a long code block in a markdown body used to render at whatever
// height it wanted and push the rest of the body off the screen — `.md-body pre` carried only
// `overflow-x: auto`, no vertical limit of any kind.
//
// The renderer tests (MarkdownRendererCodeFoldTests) prove which blocks get the wrapper, and the
// CSS-contract tests (ts/design-tokens.test.ts) prove the cap is derived from the right numbers.
// Neither can prove the thing the reader actually cares about: that in a REAL browser the box ends
// up ten lines tall, that the control opens it, and that a short block is untouched. Only a laid-out
// page can, so that is what this file measures — actual clientHeight against actual line metrics
// read from the same elements, never a hard-coded pixel count.
//
// The second run is the one that justifies the whole no-JavaScript design. `.md-body` also renders
// on Pages/ShareNode.cshtml — a PUBLIC, anonymous page on _PublicLayout, which links this
// stylesheet and ships no application JS bundle at all. A fold driven by a script would be
// permanently stuck shut for exactly the readers who cannot log in and fix it. The share run below
// is anonymous (no cookies, no auth) and asserts the same collapse-and-expand, with the page's own
// script tags counted as evidence that nothing of ours is running there.
[Collection(nameof(UiCollection))]
public sealed class CodeBlockHeightCapTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	const string Ws = "cbhc-ws";
	const string Proj = "cbhc-proj";
	const string Board = "cbhcboard";
	const string NodeKey = "n1";

	// 40 lines is comfortably past the 10-line threshold; 3 lines is comfortably under it. Every
	// line is short on purpose: a line wide enough to trigger the block's horizontal scrollbar would
	// spend part of the capped height on that scrollbar and confound the line-count assertion.
	const int LongLines = 40;
	const int ShortLines = 3;

	// The same shape and entropy as a real token (20 random bytes hex-lowercase); fixed here so the
	// URL is stable across runs.
	const string ShareToken = "cbhc00000000000000000000000000000000cbhc";

	IBrowserContext? _ctx;
	IPage? _page;
	string _nodeUrl = "";

	static string Body()
	{
		var longFence = string.Join("\n", Enumerable.Range(1, LongLines).Select(i => $"long{i:D2}();"));
		var shortFence = string.Join("\n", Enumerable.Range(1, ShortLines).Select(i => $"short{i}();"));
		// No `##` heading anywhere: this body must stay flat so the two blocks are direct children
		// of .md-body and the probe below can tell "wrapped" from "not wrapped" structurally.
		return $"Intro paragraph.\n\n```text\n{longFence}\n```\n\nAnd a short one:\n\n```text\n{shortFence}\n```\n";
	}

	public async ValueTask InitializeAsync()
	{
		using var scope = app.Services.CreateScope();
		using (var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open())
		{
			if (!await db.Workspaces.AnyAsync(w => w.Key == Ws))
				await db.InsertAsync(new Workspace { Key = Ws, Name = Ws, CreatedAt = DateTime.UtcNow });
			if (!await db.Projects.AnyAsync(p => p.Key == Proj))
				await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Code block height cap" });
		}

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		if (!await tasks.BoardExistsAsync(Proj, Board))
			await tasks.CreateBoardAsync(Proj, Board, "simple", "md-code-block-height-cap fixture", null, null);
		var existing = await tasks.GetAsync(Proj, Board, includeClosed: true);
		var node = existing.Nodes.FirstOrDefault(n => n.Key == NodeKey);
		if (node is null)
		{
			await tasks.UpsertAsync(Proj, Board, [new NodePatch { Key = NodeKey, Title = "N1", Body = Body(), Priority = 10 }]);
			existing = await tasks.GetAsync(Proj, Board, includeClosed: true);
			node = existing.Nodes.First(n => n.Key == NodeKey);
		}

		// The anonymous half of the proof: a body-scope public link onto the same node.
		var shares = scope.ServiceProvider.GetRequiredService<INodeShareDirectory>();
		if (await shares.FindAsync(ShareToken) is null)
			await shares.CreateAsync(new NodeShare
			{
				Id = ShareToken,
				ProjectKey = Proj,
				Board = Board,
				NodeId = node.NodeId,
				Scope = NodeShareScopes.Body,
				CreatedAt = DateTime.UtcNow,
				CreatedBy = "e2e",
			});

		_nodeUrl = $"/ui/{Ws}/{Proj}/tasks/{Board}/{NodeKey}";
		_ctx = await app.NewContextAsync(authenticated: true);
		_page = await _ctx.NewPageAsync();
	}

	public async ValueTask DisposeAsync()
	{
		if (_ctx is not null)
		{
			await TraceArtifact.StopAndSaveAsync(_ctx, output);
			await _ctx.CloseAsync();
		}
	}

	// Explicit JsonPropertyName on every member: EvaluateAsync<T> deserializes the JS object's
	// camelCase keys, and this pins the mapping rather than leaning on an assumed default.
	sealed class Probe
	{
		// The long block, which must be folded.
		[JsonPropertyName("longFound")]
		public bool LongFound { get; set; }
		[JsonPropertyName("longClientHeight")]
		public double LongClientHeight { get; set; }
		[JsonPropertyName("longScrollHeight")]
		public double LongScrollHeight { get; set; }
		[JsonPropertyName("longOverflowX")]
		public string LongOverflowX { get; set; } = "";
		[JsonPropertyName("padTop")]
		public double PadTop { get; set; }
		[JsonPropertyName("padBottom")]
		public double PadBottom { get; set; }
		// The control.
		[JsonPropertyName("toggleVisible")]
		public bool ToggleVisible { get; set; }
		[JsonPropertyName("toggleLabel")]
		public string ToggleLabel { get; set; } = "";
		// The short block, which must be untouched.
		[JsonPropertyName("shortFound")]
		public bool ShortFound { get; set; }
		[JsonPropertyName("shortInFold")]
		public bool ShortInFold { get; set; }
		[JsonPropertyName("shortClientHeight")]
		public double ShortClientHeight { get; set; }
		[JsonPropertyName("shortScrollHeight")]
		public double ShortScrollHeight { get; set; }
		// How many <script> elements the whole page carries, and how many of them are ours.
		[JsonPropertyName("appScripts")]
		public int AppScripts { get; set; }
	}

	// Every lookup is defensive (optional chaining, sentinels) ON PURPOSE: against the pre-change
	// markup `.md-code-fold` does not exist, and a plain `.querySelector(...).clientHeight` would
	// throw a JS TypeError instead of failing the specific assertion below with a legible number.
	const string ProbeJs = """
		(testId) => {
			const body = document.querySelector(`[data-testid="${testId}"]`);
			const fold = body?.querySelector('.md-code-fold') ?? null;
			const longPre = fold?.querySelector(':scope > pre') ?? null;
			const summary = fold?.querySelector('.md-code-fold-toggle > summary') ?? null;
			// The short block is the one NOT inside a fold wrapper.
			const shortPre = [...(body?.querySelectorAll('pre') ?? [])]
				.find((p) => !p.closest('.md-code-fold')) ?? null;
			const cs = (el) => el ? getComputedStyle(el) : null;
			const num = (v) => v === undefined ? NaN : Number.parseFloat(v);
			return {
				longFound: !!longPre,
				longClientHeight: longPre ? longPre.clientHeight : NaN,
				longScrollHeight: longPre ? longPre.scrollHeight : NaN,
				longOverflowX: cs(longPre)?.overflowX ?? '(missing)',
				padTop: num(cs(longPre)?.paddingTop),
				padBottom: num(cs(longPre)?.paddingBottom),
				toggleVisible: summary ? summary.getClientRects().length > 0 : false,
				toggleLabel: summary ? summary.innerText.trim() : '(missing)',
				shortFound: !!shortPre,
				shortInFold: shortPre ? !!shortPre.closest('.md-code-fold') : false,
				shortClientHeight: shortPre ? shortPre.clientHeight : NaN,
				shortScrollHeight: shortPre ? shortPre.scrollHeight : NaN,
				appScripts: [...document.scripts].filter((s) => (s.src ?? '').includes('/js/')).length,
			};
		}
		""";

	// A folded block shows exactly the threshold's worth of lines.
	//
	// The line pitch is DERIVED FROM THE LAID-OUT BLOCK — a 40-line block's own content height
	// divided by 40 — and never read out of a CSS declaration. That is the whole reason this
	// assertion is trustworthy: the first version of the cap was built from `.md-body pre code`'s
	// line-height (22.72px), which is not what the lines are spaced by, because a line box is never
	// shorter than the <pre>'s own strut (26.73px). The box rendered eight and a half lines while a
	// test comparing it against that same wrong number passed. Measuring the real pitch cannot make
	// that mistake twice: whatever the browser actually did, ten of those must be what is visible.
	//
	// Tolerance is a pixel and a half — sub-pixel metrics round, a whole missing LINE (~27px) cannot
	// hide in that.
	static void AssertCollapsedToTenLines(Probe p)
	{
		p.LongFound.Should().BeTrue("a 40-line block must be wrapped in .md-code-fold");
		var pitch = (p.LongScrollHeight - p.PadTop - p.PadBottom) / LongLines;
		pitch.Should().BeGreaterThan(0);
		var expected = (10 * pitch) + p.PadTop + p.PadBottom;
		p.LongClientHeight.Should().BeApproximately(expected, 1.5,
			$"the collapsed block must show exactly 10 rendered lines plus its own padding "
			+ $"(measured pitch {pitch}px, padding {p.PadTop}+{p.PadBottom}px)");
		p.LongScrollHeight.Should().BeGreaterThan(p.LongClientHeight * 3,
			"the other 30 lines must still be in the box — the cap hides them, it does not delete them");
		p.LongOverflowX.Should().Be("auto", "horizontal scrolling inside a code block must survive the cap");
	}

	static void AssertShortBlockUntouched(Probe p)
	{
		p.ShortFound.Should().BeTrue();
		p.ShortInFold.Should().BeFalse("a 3-line block gets no wrapper at all");
		p.ShortClientHeight.Should().BeApproximately(p.ShortScrollHeight, 1.0,
			"a short block is never clipped — no cap, and no control around three lines");
	}

	[Fact]
	public async Task LongBlock_IsCappedAtTenLines_AndTheControlOpensIt()
	{
		await _page!.GotoAsync(_nodeUrl);
		await Expect(_page.GetByTestId("node-body")).ToBeVisibleAsync();

		var collapsed = await _page.EvaluateAsync<Probe>(ProbeJs, "node-body");
		AssertCollapsedToTenLines(collapsed);
		AssertShortBlockUntouched(collapsed);

		collapsed.ToggleVisible.Should().BeTrue("the way out of the cap must be visible, not implied");
		collapsed.ToggleLabel.Should().Be($"Show all {LongLines} lines",
			"the control names the size, so the reader knows what the click costs");

		await _page.Locator("[data-testid='node-body'] .md-code-fold-toggle > summary").ClickAsync();

		var expanded = await _page.EvaluateAsync<Probe>(ProbeJs, "node-body");
		expanded.LongClientHeight.Should().BeApproximately(expanded.LongScrollHeight, 1.0,
			"opening the disclosure must reveal the WHOLE block, not a taller crop of it");
		expanded.LongClientHeight.Should().BeGreaterThan(collapsed.LongClientHeight * 3);
		expanded.ToggleLabel.Should().Be("Show less", "the same control must offer the way back");
	}

	[Fact]
	public async Task Anonymous_SharePage_FoldsAndExpands_WithNoApplicationScript()
	{
		// A context with no auth state at all — the public reader this page exists for.
		await using var anon = await app.NewContextAsync(authenticated: false);
		var page = await anon.NewPageAsync();
		await page.GotoAsync($"/ui/share/node/{ShareToken}");
		await Expect(page.GetByTestId("share-node-body")).ToBeVisibleAsync();

		var collapsed = await page.EvaluateAsync<Probe>(ProbeJs, "share-node-body");
		AssertCollapsedToTenLines(collapsed);
		AssertShortBlockUntouched(collapsed);
		collapsed.AppScripts.Should().Be(0,
			"_PublicLayout links no application JS bundle — which is precisely why the fold had to be "
			+ "a native <details> read by CSS rather than anything script-driven");

		await page.Locator("[data-testid='share-node-body'] .md-code-fold-toggle > summary").ClickAsync();

		var expanded = await page.EvaluateAsync<Probe>(ProbeJs, "share-node-body");
		expanded.LongClientHeight.Should().BeApproximately(expanded.LongScrollHeight, 1.0,
			"an anonymous reader must be able to open the block, with no script and no session");

		await TraceArtifact.StopAndSaveAsync(anon, output);
	}
}

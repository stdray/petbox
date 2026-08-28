using System.Text.Json.Serialization;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;

namespace PetBox.E2ETests;

// Red-proof for spec `comment-distinct-from-body` (work `comment-vs-body-distinction`): "a
// comment MUST be visually distinct from the node body". The owner's diagnosis (asked and
// answered, not re-opened here): the fix is the FRAME OUTSIDE, not typography inside — a comment
// becomes its own bounded, reply-shaped element with a header row separated from its text and a
// ground token distinct from the body's, while the body itself is left untouched (no measure, no
// heading-scale change — those are separate, very recent, deliberate decisions this task must
// not undo). See _CommentThread.cshtml's `.reply-frame`/`.reply-frame-header` and app.css.
//
// This proves the claim on a REAL rendered node-detail page (not a synthetic DOM injection like
// ThemeTests' font-size check — that check is a control this suite also relies on, extended
// against the real page here) in a project with the Tasks feature live (unlike ThemeTests'
// fixture), so a genuine `.reply-frame`/body-container pair exists to measure.
//
// Every assertion here failed against the PRE-change markup (a `border-l-2` rule with no frame
// and no header separator, sharing implicit inheritance with the body's own container) — see the
// task report for the captured red-run transcript. Nothing here asserts a colour VALUE; every
// check is a structural/distinctness comparison, run in BOTH shipped themes that matter to this
// claim (Dark, Light) so a token that reads distinct in one cannot quietly collapse in the other.
[Collection(nameof(UiCollection))]
public sealed class CommentVsBodyDistinctionTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	const string Ws = "cvbd-ws";
	const string Proj = "cvbd-proj";
	const string Board = "cvbdboard";
	const string NodeKey = "n1";
	const string BodyText = "Node body text — CVBD_BODY_MARKER.";
	const string CommentText = "Reply text — CVBD_COMMENT_MARKER.";

	IBrowserContext? _ctx;
	IPage? _page;
	string _nodeUrl = "";

	public async ValueTask InitializeAsync()
	{
		using var scope = app.Services.CreateScope();
		using (var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open())
		{
			if (!await db.Workspaces.AnyAsync(w => w.Key == Ws))
				await db.InsertAsync(new Workspace { Key = Ws, Name = Ws, CreatedAt = DateTime.UtcNow });
			if (!await db.Projects.AnyAsync(p => p.Key == Proj))
				await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Comment vs Body" });
		}

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		if (!await tasks.BoardExistsAsync(Proj, Board))
			await tasks.CreateBoardAsync(Proj, Board, "simple", "comment-vs-body-distinction fixture", null, null);
		var existing = await tasks.GetAsync(Proj, Board, includeClosed: true);
		var node = existing.Nodes.FirstOrDefault(n => n.Key == NodeKey);
		if (node is null)
		{
			await tasks.UpsertAsync(Proj, Board, [new NodePatch { Key = NodeKey, Title = "N1", Body = BodyText, Priority = 10 }]);
			existing = await tasks.GetAsync(Proj, Board, includeClosed: true);
			node = existing.Nodes.First(n => n.Key == NodeKey);
		}

		var comments = scope.ServiceProvider.GetRequiredService<ICommentService>();
		var thread = await comments.ListForNodeAsync(Proj, Board, node.NodeId);
		if (thread.Count == 0)
			await comments.AddAsync(Proj, Board, node.NodeId, null, "tester", CommentText, null);

		_nodeUrl = $"/ui/{Ws}/{Proj}/tasks/{Board}/{NodeKey}";
		_ctx = await app.NewContextAsync(authenticated: true);
		_page = await _ctx.NewPageAsync();
	}

	public async ValueTask DisposeAsync()
	{
		if (_ctx is not null)
		{
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

	// Explicit JsonPropertyName on every member: EvaluateAsync<T> deserializes the JS object's
	// camelCase keys, and this pins the mapping rather than leaning on an assumed case-insensitive
	// default.
	sealed class Probe
	{
		[JsonPropertyName("bodyBg")]
		public string BodyBg { get; set; } = "";
		[JsonPropertyName("commentBg")]
		public string CommentBg { get; set; } = "";
		[JsonPropertyName("bodyClasses")]
		public string[] BodyClasses { get; set; } = [];
		[JsonPropertyName("commentClasses")]
		public string[] CommentClasses { get; set; } = [];
		[JsonPropertyName("nodeFontSize")]
		public double NodeFontSize { get; set; }
		[JsonPropertyName("commentFontSize")]
		public double CommentFontSize { get; set; }
		[JsonPropertyName("headerBorderBottomWidth")]
		public double HeaderBorderBottomWidth { get; set; }
		[JsonPropertyName("headerBottom")]
		public double HeaderBottom { get; set; }
		[JsonPropertyName("bodyTextTop")]
		public double BodyTextTop { get; set; }
		[JsonPropertyName("dataTheme")]
		public string DataTheme { get; set; } = "";
	}

	[Theory]
	[InlineData("Dark")]
	[InlineData("Light")]
	public async Task Comment_Frame_Is_Structurally_And_Visually_Distinct_From_Body_Container(string theme)
	{
		await SetThemeAsync(theme);
		await _page!.GotoAsync(_nodeUrl);
		await Expect(_page.GetByTestId("node-body")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("comment")).ToBeVisibleAsync();

		// Every lookup below is defensive (optional chaining / fallback sentinels) ON PURPOSE:
		// against the PRE-change markup `.reply-frame-header` does not exist at all, and a plain
		// `.querySelector(...).foo` would throw a JS TypeError instead of failing the specific
		// assertion below with a legible message. NaN/'' sentinels still fail every real
		// assertion — they just fail AS DATA, which is what the red-proof transcript should show.
		var probe = await _page.EvaluateAsync<Probe>(@"() => {
			const nodeBody = document.querySelector('[data-testid=""node-body""]');
			const bodyContainer = nodeBody?.parentElement ?? null; // the rounded/bordered/tinted box around node-body
			const comment = document.querySelector('[data-testid=""comment""]');
			const header = comment?.querySelector('.reply-frame-header') ?? null;
			const commentBody = comment?.querySelector('[data-testid=""comment-body""]') ?? null;

			const cs = (el) => el ? getComputedStyle(el) : null;
			return {
				bodyBg: cs(bodyContainer)?.backgroundColor ?? '(missing body container)',
				commentBg: cs(comment)?.backgroundColor ?? '(missing comment)',
				bodyClasses: bodyContainer ? Array.from(bodyContainer.classList) : [],
				commentClasses: comment ? Array.from(comment.classList) : [],
				nodeFontSize: nodeBody ? parseFloat(cs(nodeBody).fontSize) : NaN,
				commentFontSize: commentBody ? parseFloat(cs(commentBody).fontSize) : NaN,
				headerBorderBottomWidth: header ? parseFloat(cs(header).borderBottomWidth) : NaN,
				headerBottom: header ? header.getBoundingClientRect().bottom : NaN,
				bodyTextTop: commentBody ? commentBody.getBoundingClientRect().top : NaN,
				dataTheme: document.documentElement.getAttribute('data-theme') || ''
			};
		}");

		output.WriteLine($"[{theme}] data-theme={probe.DataTheme} bodyBg={probe.BodyBg} commentBg={probe.CommentBg}");

		// 1. STRUCTURE: the comment is its own bounded element, carrying the reply frame class —
		//    and the body container must NOT have silently acquired that same treatment.
		probe.CommentClasses.Should().Contain("reply-frame", "a comment must carry its own bounded frame");
		probe.BodyClasses.Should().NotContain("reply-frame", "the node body must not pick up the comment's own frame class");

		// 2. STRUCTURE: the comment has a header row separated from its text by a visible rule,
		//    and that header sits fully above the reply text (not inline with it).
		probe.HeaderBorderBottomWidth.Should().BeGreaterThan(0, "the header row must be separated from the reply text by a visible rule");
		probe.HeaderBottom.Should().BeLessThanOrEqualTo(probe.BodyTextTop + 0.5, "the header row must render above the reply text, not beside/inside it");

		// 3. GROUND: the comment's background must resolve to a DIFFERENT computed value than the
		//    body container's — a distinct token role, not merely a different declared class.
		probe.CommentBg.Should().NotBe(probe.BodyBg, $"[{theme}] a comment's ground must be a different token than the body's — same computed background means the frame carries no real visual signal");

		// 4. CONTROL: font-size parity must survive — "distinct" must not have come to mean
		//    "we made comments smaller". Mirrors ThemeTests.Node_Body_And_Comment_Body_Render_Same_Font_Size
		//    but against the REAL rendered page rather than a synthetic DOM injection.
		probe.CommentFontSize.Should().Be(probe.NodeFontSize, $"[{theme}] node and comment markdown bodies must still render at the same font-size");
	}
}

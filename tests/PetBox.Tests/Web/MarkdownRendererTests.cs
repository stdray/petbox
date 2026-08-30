using System.Text.RegularExpressions;
using PetBox.Web.Rendering;

namespace PetBox.Tests.Web;

// The text inside rendered <code> elements, markup stripped and entities decoded.
//
// Tests about a code block's CONTENT have to go through this rather than substring-matching the
// raw HTML, because since `md-code-syntax-highlighting` a block with a known language is tokenized
// and its text is interleaved with <span> elements: `var line1 = 1;` is real, visible, copyable
// text on the page, but it is no longer one contiguous run of markup. Matching the rendered text
// is also the stronger assertion — it is what a reader, a copy/paste and a browser find actually
// see, which is the property those tests were written to protect in the first place.
static class MarkdownCodeText
{
	static readonly Regex TagRx = new("<[^>]*>", RegexOptions.Compiled);

	public static string VisibleCodeText(string html)
	{
		var text = new System.Text.StringBuilder();
		var at = 0;
		while (true)
		{
			var start = html.IndexOf("<code", at, StringComparison.Ordinal);
			if (start < 0) break;
			var open = html.IndexOf('>', start) + 1;
			var end = html.IndexOf("</code>", open, StringComparison.Ordinal);
			if (open <= 0 || end < 0) break;
			text.Append(System.Net.WebUtility.HtmlDecode(TagRx.Replace(html[open..end], ""))).Append('\n');
			at = end + 1;
		}
		return text.ToString();
	}
}

// The server-side markdown renderer (IMarkdownRenderer / MarkdownRenderer): Markdig with the
// client-parity pipeline (advanced extensions + soft-break-as-hard-break) followed by HtmlSanitizer
// (Ganss.Xss). Read surfaces render markdown → sanitized HTML on the SERVER so the initial DOM
// carries real <p>/<article> and Firefox reader-view (isProbablyReaderable) can detect the article.
public sealed class MarkdownRendererTests
{
	static readonly IMarkdownRenderer R = new MarkdownRenderer();

	static string Html(string md) => R.RenderToHtml(md);

	// --- markdown → HTML (block/inline structure) ------------------------------------------------

	[Fact]
	public void Heading_RendersH2()
	{
		Html("## Heading").Should().Contain("<h2").And.Contain("Heading");
	}

	[Fact]
	public void UnorderedList_RendersUlLi()
	{
		var html = Html("- a\n- b");
		html.Should().Contain("<ul>");
		html.Should().Contain("<li>a</li>");
		html.Should().Contain("<li>b</li>");
	}

	[Fact]
	public void OrderedList_RendersOl()
	{
		var html = Html("1. first\n2. second");
		html.Should().Contain("<ol");
		html.Should().Contain("<li>first</li>");
	}

	[Fact]
	public void SoftLineBreak_BecomesHardBreak()
	{
		// breaks:true parity — a bare single \n inside a paragraph is a <br>.
		Html("line one\nline two").Should().Contain("<br");
	}

	[Fact]
	public void Bold_RendersStrong()
	{
		Html("**x**").Should().Contain("<strong>x</strong>");
	}

	[Fact]
	public void Paragraph_RendersP()
	{
		// The whole point: real <p> in the initial DOM → reader-view detectable.
		Html("A plain sentence of body text that a reader can see.").Should().Contain("<p>");
	}

	[Fact]
	public void EmptyOrNull_RendersEmptyString()
	{
		Html("").Should().BeEmpty();
		R.RenderToHtml(null).Should().BeEmpty();
	}

	// --- sanitization (parity with the client DOMPurify path) ------------------------------------

	[Fact]
	public void RawHtml_SafeInlineTag_IsKept()
	{
		// Raw HTML in a body is valid content (HtmlSanitizer keeps a safe subset), not escaped away.
		Html("a <b>bold</b> word").Should().Contain("<b>bold</b>");
	}

	[Fact]
	public void RawHtml_Script_IsRemoved()
	{
		var html = Html("hello <script>alert(1)</script> world");
		html.Should().NotContain("<script");
		html.Should().Contain("hello");
	}

	[Fact]
	public void RawHtml_ImgOnError_IsStripped()
	{
		var html = Html("<img src=x onerror=alert(1)>");
		html.Should().NotContain("onerror");
	}

	[Fact]
	public void Link_JavascriptScheme_IsNeutralized()
	{
		var html = Html("[click](javascript:alert(1))");
		html.Should().NotContain("javascript:");
	}

	[Fact]
	public void Link_HttpScheme_IsKept()
	{
		Html("[site](https://example.com/page)").Should().Contain("href=\"https://example.com/page\"");
	}

	[Fact]
	public void Link_DataUriImage_IsNeutralized()
	{
		// data: is not in the allowlist — an <img> with a data: src must not survive with that src.
		Html("![x](data:text/html;base64,PHNjcmlwdD4=)").Should().NotContain("data:text/html");
	}

	[Fact]
	public void FencedCodeBlock_HtmlStaysEscaped_NotExecuted()
	{
		// HTML inside a fenced code block is visible ESCAPED code, never a live element.
		var html = Html("```html\n<script>alert(1)</script>\n```");
		html.Should().Contain("<pre>");
		html.Should().Contain("<code");
		html.Should().Contain("&lt;script&gt;"); // escaped, shown as text
		html.Should().NotContain("<script"); // no live script element
	}
}

// Commit-hash autolinking (commit-links-impl): when a project declares a commit-view URL template
// (RepoSettings.CommitUrlTemplate, literal {sha} placeholder), standalone git-hash-shaped words
// (7–12 hex abbreviated / exactly 40 hex full) in PLAIN TEXT runs become links to the commit view.
// PetBox's own hex identifiers (32-hex NodeIds, prefixed memory keys like m-…/ac-…) must NOT link.
// Code spans/blocks and existing links are excluded; with no usable template the output is
// byte-identical to the template-less path.
public sealed class MarkdownRendererCommitLinkTests
{
	const string Template = "https://github.com/user/repo/commit/{sha}";

	static readonly IMarkdownRenderer R = new MarkdownRenderer();

	static string Html(string md, string? template = Template) => R.RenderToHtml(md, template);

	[Fact]
	public void ShortHash_7Hex_Autolinks()
	{
		var html = Html("fixed in cc20e34 yesterday");
		html.Should().Contain("<a href=\"https://github.com/user/repo/commit/cc20e34\"");
		html.Should().Contain(">cc20e34</a>");
	}

	[Fact]
	public void FullHash_40Hex_Autolinks()
	{
		var sha = "0123456789abcdef0123456789abcdef01234567";
		Html($"see {sha}.").Should().Contain($"<a href=\"https://github.com/user/repo/commit/{sha}\"");
	}

	[Fact]
	public void Autolink_CarriesTargetBlankAndNoopener_ThroughSanitizer()
	{
		// The generated anchor must SURVIVE sanitization with all three attributes intact.
		var html = Html("fixed in cc20e34");
		html.Should().Contain("href=\"https://github.com/user/repo/commit/cc20e34\"");
		html.Should().Contain("target=\"_blank\"");
		html.Should().Contain("rel=\"noopener\"");
	}

	[Fact]
	public void Hash_InsideCodeSpan_DoesNotLink()
	{
		var html = Html("run `git show cc20e34` locally");
		html.Should().NotContain("<a");
		html.Should().Contain("<code>git show cc20e34</code>");
	}

	[Fact]
	public void Hash_InsideFencedCodeBlock_DoesNotLink()
	{
		var html = Html("```\ngit revert cc20e34\n```");
		html.Should().NotContain("<a");
		html.Should().Contain("cc20e34");
	}

	[Fact]
	public void Hash_InsideExistingLinkText_DoesNotDoubleLink()
	{
		var html = Html("[cc20e34](https://example.com/x)");
		// Exactly the author's link — no nested/extra anchor to the commit view.
		html.Should().Contain("href=\"https://example.com/x\"");
		html.Should().NotContain("github.com/user/repo/commit");
	}

	[Fact]
	public void NonHexWord_DoesNotLink()
	{
		// 7+ chars but with non-hex letters.
		Html("deadbeefx and ggggggg stay plain").Should().NotContain("<a");
	}

	[Fact]
	public void SixHexWord_DoesNotLink()
	{
		Html("abc123 is too short").Should().NotContain("<a");
	}

	[Fact]
	public void AllDigitWord_DoesNotLink()
	{
		// 8-digit dates and 10-digit timestamps are hex-shaped but are numbers, not hashes.
		Html("shipped 20260704, epoch 1751600000").Should().NotContain("<a");
	}

	[Fact]
	public void NodeId_32Hex_DoesNotLink()
	{
		// PetBox NodeIds are bare 32-hex words — inside the old 7–40 range, not a git hash shape.
		Html("node b9ed7a8700aa405c8e5a6a9153a72fa4 mentioned").Should().NotContain("<a");
	}

	[Fact]
	public void MidLengthHex_13To39_DoesNotLink()
	{
		// Between an abbreviation (≤12) and a full sha (40) there is no real git hash form.
		Html("id abcdef0123456 here").Should().NotContain("<a"); // 13 hex
		Html("id abcdef0123456789abcdef0123456789abcdef0 here").Should().NotContain("<a"); // 39 hex
	}

	[Fact]
	public void PrefixedMemoryKeys_DoNotLink()
	{
		// `-` is a \b boundary, so the old regex linked the hex tail of m-…/ac-… memory keys.
		var html = Html("see m-749286fed1d747768aade4bb4b6a006a and ac-8e952de324f3 notes");
		html.Should().NotContain("<a");
	}

	[Fact]
	public void HyphenGluedHash_DoesNotLink()
	{
		// A hex run touching a hyphen is part of a larger identifier, not a standalone hash.
		Html("build cc20e34-hotfix tag").Should().NotContain("<a");
	}

	[Fact]
	public void TwelveHexWord_StillLinks()
	{
		// 12 hex is the upper end of common git abbreviations (core.abbrev up to 12).
		Html("fixed in cc20e34abc12 today").Should().Contain("commit/cc20e34abc12\"");
	}

	[Fact]
	public void NoTemplate_OutputIdenticalToLegacyPath()
	{
		var md = "## Head\nfixed in cc20e34\n\n- item deadbeef1";
		R.RenderToHtml(md, null).Should().Be(R.RenderToHtml(md));
		R.RenderToHtml(md, "").Should().Be(R.RenderToHtml(md));
		R.RenderToHtml(md, null).Should().NotContain("<a");
	}

	[Fact]
	public void TemplateWithoutShaPlaceholder_TreatedAsUnset()
	{
		var html = Html("fixed in cc20e34", "https://github.com/user/repo/commits");
		html.Should().NotContain("<a");
		html.Should().Be(R.RenderToHtml("fixed in cc20e34"));
	}

	[Fact]
	public void MultipleHashes_InOneRun_AllLink_TextPreserved()
	{
		var html = Html("between cc20e34 and 35203f6 words survive");
		html.Should().Contain("commit/cc20e34\"");
		html.Should().Contain("commit/35203f6\"");
		html.Should().Contain("between ");
		html.Should().Contain(" and ");
		html.Should().Contain(" words survive");
	}

	[Fact]
	public void Hash_GluedToLetters_DoesNotLink()
	{
		// \b word boundary: `xcc20e34` is one word, not a standalone hash.
		Html("xcc20e34 is not a hash").Should().NotContain("<a");
	}
}

// `[[slug]]` node-mention autolinking (node-ref-autolink-impl): a mention in a plain text run
// becomes a link to that node's detail page WHEN the slug resolves (the caller hands the renderer
// a prebuilt slug→target map — the renderer never touches the DB). An unmapped mention stays
// literal. Code spans/blocks and existing links are excluded, exactly like the commit-hash pass.
public sealed class MarkdownRendererNodeRefTests
{
	const string Url = "/ui/ws/proj/tasks/spec/some-node";

	static readonly IMarkdownRenderer R = new MarkdownRenderer();

	static IReadOnlyDictionary<string, NodeRefTarget> Map(params (string Slug, string Url, string? Title)[] entries)
		=> entries.ToDictionary(e => e.Slug, e => new NodeRefTarget(e.Url, e.Title), StringComparer.Ordinal);

	static string Html(string md, IReadOnlyDictionary<string, NodeRefTarget>? map)
		=> R.RenderToHtml(md, null, map);

	[Fact]
	public void ResolvableMention_LinksWithSlugTextAndTitleAttribute()
	{
		var html = Html("see [[some-node]] for details", Map(("some-node", Url, "The Spec Node")));
		html.Should().Contain($"<a href=\"{Url}\"");
		html.Should().Contain("title=\"The Spec Node\"");
		// Link TEXT is the bare slug — no brackets.
		html.Should().Contain(">some-node</a>");
		html.Should().NotContain("[[some-node]]");
	}

	[Fact]
	public void ResolvableMention_SurvivesSanitizer()
	{
		// The generated anchor (relative href + title) must live through HtmlSanitizer.
		var html = Html("[[some-node]]", Map(("some-node", Url, "Title")));
		html.Should().Contain($"href=\"{Url}\"");
		html.Should().Contain("title=\"Title\"");
		html.Should().Contain(">some-node</a>");
	}

	[Fact]
	public void UnresolvableMention_StaysLiteral()
	{
		// The slug is not in the map → the original `[[slug]]` text is preserved, no link.
		var html = Html("mentions [[ghost-node]] here", Map(("some-node", Url, "T")));
		html.Should().NotContain("<a");
		html.Should().Contain("[[ghost-node]]");
	}

	[Fact]
	public void Mention_InsideCodeSpan_DoesNotLink()
	{
		var html = Html("type `[[some-node]]` verbatim", Map(("some-node", Url, "T")));
		html.Should().NotContain("<a");
		html.Should().Contain("<code>[[some-node]]</code>");
	}

	[Fact]
	public void Mention_InsideFencedCodeBlock_DoesNotLink()
	{
		var html = Html("```\nref [[some-node]]\n```", Map(("some-node", Url, "T")));
		html.Should().NotContain("<a");
		html.Should().Contain("[[some-node]]");
	}

	[Fact]
	public void Mention_InsideExistingLink_DoesNotDoubleLink()
	{
		// An author's explicit link whose TEXT contains a mention keeps just that one anchor.
		var html = Html("[[[some-node]]](https://example.com/x)", Map(("some-node", Url, "T")));
		html.Should().Contain("href=\"https://example.com/x\"");
		html.Should().NotContain($"href=\"{Url}\"");
	}

	[Fact]
	public void HashAndMention_InOneBody_BothLink()
	{
		const string template = "https://github.com/user/repo/commit/{sha}";
		var html = R.RenderToHtml("fixed cc20e34 for [[some-node]]", template, Map(("some-node", Url, "T")));
		html.Should().Contain("commit/cc20e34\"");
		html.Should().Contain($"href=\"{Url}\"");
		html.Should().Contain(">some-node</a>");
	}

	[Fact]
	public void MultipleMentions_InOneRun_AllLink_TextPreserved()
	{
		var html = Html("both [[node-a]] and [[node-b]] linked",
			Map(("node-a", "/a", "A"), ("node-b", "/b", "B")));
		html.Should().Contain("href=\"/a\"").And.Contain(">node-a</a>");
		html.Should().Contain("href=\"/b\"").And.Contain(">node-b</a>");
		html.Should().Contain("both ").And.Contain(" and ").And.Contain(" linked");
	}

	[Fact]
	public void UnresolvedMentionWrappingHashLikeSlug_StaysFullyLiteral()
	{
		// `[[abc1234]]` — the inner slug is hex-shaped, but an UNRESOLVED mention must render
		// as its original text even with the commit template active (no linked hash inside).
		const string template = "https://github.com/user/repo/commit/{sha}";
		var html = R.RenderToHtml("nope [[abc1234]] here", template, Map(("other", Url, "T")));
		html.Should().NotContain("<a");
		html.Should().Contain("[[abc1234]]");
	}

	[Fact]
	public void NoMap_OutputIdenticalToPlainPath()
	{
		var md = "## Head\nsee [[some-node]] and [[other]]\n\n- item";
		R.RenderToHtml(md, null, null).Should().Be(R.RenderToHtml(md));
		// An empty map is the plain path too (no mention resolves).
		R.RenderToHtml(md, null, Map()).Should().Be(R.RenderToHtml(md));
		R.RenderToHtml(md, null, null).Should().NotContain("<a");
	}

}

// The body design layer (work `node-render-design-layer`): the `##` section container, the
// per-table horizontal scroller and GFM alerts.
//
// Every assertion here is on POST-SANITIZER output, which is the whole point: the design layer is
// CSS keyed on classes, and HtmlSanitizer drops `class` by default. Without the allowlist in
// MarkdownRenderer.BuildSanitizer these elements still render — bare, unstyleable and silently
// unthemed — so a test that only checked for `<section>` or `<div>` would stay green through the
// exact failure this feature is most likely to have.
public sealed class MarkdownDesignLayerTests
{
	static readonly IMarkdownRenderer R = new MarkdownRenderer();

	static string Html(string md) => R.RenderToHtml(md);

	[Fact]
	public void SectionContainer_WrapsContentBetweenH2_AndClassSurvivesSanitizer()
	{
		var html = Html("## First\n\nalpha\n\n## Second\n\nbeta");

		// The CLASS, not just the element — this is the assertion the sanitizer can break.
		html.Should().Contain("<section class=\"md-section\">");
		html.Split("<section class=\"md-section\">").Length.Should().Be(3, "one section per `##`");
		// Each section owns its heading AND the prose that follows it.
		html.Should().Contain("<h2>First</h2>").And.Contain("<p>alpha</p>");
		html.Should().Contain("</section>");
	}

	[Fact]
	public void SectionContainer_IsAstDerived_NotTextMatched()
	{
		// A `## ` line inside a fenced code block is CODE, not a heading. This is the difference
		// between grouping on the AST and running a regex over rendered HTML — several of this
		// repo's own /doc pages carry `#`-prefixed shell comments inside fences.
		var html = Html("## Real\n\n```sh\n## not a heading\n```");

		html.Split("<section class=\"md-section\">").Length.Should().Be(2, "only the real `##` opens a section");
		// Asserted on the block's VISIBLE TEXT rather than on the raw HTML. Since
		// `md-code-syntax-highlighting` a `sh` block is tokenized, so `## not a heading` reaches
		// the browser as a comment split across two <span>s and no longer appears as one literal
		// run anywhere in the markup. The claim being made here was never about the markup — it is
		// that the line stays CODE instead of becoming an <h2> — and stripping the tags back off
		// states exactly that, without caring how the text is coloured.
		MarkdownCodeText.VisibleCodeText(html).Should().Contain("## not a heading", "the fenced line stays literal code");
		html.Should().NotContain("<h2>not a heading</h2>");
	}

	[Fact]
	public void SectionContainer_ContentBeforeFirstH2_StaysAtTopLevel()
	{
		var html = Html("lead paragraph\n\n## Section\n\nbody");
		// The lead is outside the section, so a body with no `##` at all is completely unchanged.
		html.IndexOf("<p>lead paragraph</p>", StringComparison.Ordinal)
			.Should().BeLessThan(html.IndexOf("<section", StringComparison.Ordinal));
	}

	[Fact]
	public void BodyWithoutH2_GetsNoSectionWrapper()
	{
		Html("just prose\n\n### deeper heading").Should().NotContain("<section");
	}

	[Fact]
	public void Table_GetsOwnScroller_AndClassSurvivesSanitizer()
	{
		var html = Html("| a | b |\n|---|---|\n| 1 | 2 |");

		html.Should().Contain("<div class=\"md-table-scroll\">");
		// The wrapper is OUTSIDE the table — it is the element that owns overflow-x.
		html.IndexOf("<div class=\"md-table-scroll\">", StringComparison.Ordinal)
			.Should().BeLessThan(html.IndexOf("<table>", StringComparison.Ordinal));
		html.Should().Contain("<th>a</th>");
	}

	[Fact]
	public void GfmAlert_ReachesTheBrowserStillCarryingItsClasses()
	{
		// Measured, not assumed: Markdig has ALWAYS parsed `> [!NOTE]` here — AlertExtension ships
		// inside UseAdvancedExtensions() in the pinned 1.3.2 — and has always emitted
		// `<div class="markdown-alert markdown-alert-note">`. What reached the browser before this
		// work was `<div><p>Note</p><p>Body</p></div>`: the sanitizer dropped every class, so a
		// callout rendered as two anonymous paragraphs. The classes surviving IS the feature.
		var html = Html("> [!NOTE]\n> Body of the note.");

		html.Should().Contain("class=\"markdown-alert markdown-alert-note\"");
		html.Should().Contain("class=\"markdown-alert-title\"");
		html.Should().NotContain("[!NOTE]", "the marker is consumed, not printed");
		html.Should().Contain("Body of the note.");
	}

	[Fact]
	public void GfmAlert_EachKindKeepsItsOwnClass()
	{
		// The kind class is what selects the semantic colour pair; all five must survive.
		foreach (var (marker, cls) in new[]
		{
			("NOTE", "markdown-alert-note"), ("TIP", "markdown-alert-tip"),
			("IMPORTANT", "markdown-alert-important"), ("WARNING", "markdown-alert-warning"),
			("CAUTION", "markdown-alert-caution"),
		})
			Html($"> [!{marker}]\n> text").Should().Contain(cls);
	}

	[Fact]
	public void PlainBlockquote_StillRendersAsBlockquote()
	{
		// Enabling alerts must not reinterpret ordinary quotes.
		Html("> just a quote").Should().Contain("<blockquote>").And.NotContain("markdown-alert");
	}

	[Fact]
	public void AuthorRawHtml_CannotSmuggleArbitraryClasses()
	{
		// `class` is allowed as an ATTRIBUTE now, and raw HTML in a body is deliberately kept — so
		// the value allowlist is the only thing standing between an author and the whole Tailwind
		// utility set (e.g. a body that covers the page with `fixed inset-0`).
		var html = Html("<div class=\"fixed inset-0 z-50 bg-error\">hi</div>");

		html.Should().Contain("hi");
		html.Should().NotContain("fixed").And.NotContain("inset-0").And.NotContain("z-50");
		html.Should().NotContain("class=", "nothing survived, so the attribute is dropped entirely");
	}

	[Fact]
	public void AuthorRawHtml_KeepingADesignClass_KeepsOnlyThatOne()
	{
		var html = Html("<div class=\"md-section fixed inset-0\">hi</div>");
		html.Should().Contain("class=\"md-section\"");
		html.Should().NotContain("fixed").And.NotContain("inset-0");
	}
}

// The diagram allowlist (spec `body-carries-diagram`): a body may carry a sanitized inline-SVG
// subset. There is NO CSP in this app, so this allowlist is the ENTIRE defence — every forbidden
// construct below gets its own test, plus a CONTROL test proving a legitimate diagram is not
// collateral damage (a green "it was stripped" suite that also strips everything real would be
// worthless). Raw HTML in a body is already kept-then-sanitized (MarkdownRendererTests above), so
// these tests feed the SVG straight as raw HTML in markdown, exactly as an author would.
public sealed class MarkdownRendererSvgDiagramTests
{
	static readonly IMarkdownRenderer R = new MarkdownRenderer();

	static string Html(string md) => R.RenderToHtml(md);

	// A diagram shaped like the reference case that motivated the SVG-over-mermaid decision: a
	// dashed line ("addresses a different id space") crossing a struck-through bridge, arrows via
	// <marker>/<defs>, a reused glyph via <use>, and the disciplined figure contract — a caption
	// stating the claim, and the drawing carrying the same claim as its own text alternative
	// (role="img" + <title>, the standard SVG accessible-name pattern).
	const string LegitimateDiagram = """
		<figure>
		<svg viewBox="0 0 200 100" role="img">
		<title>The bridge is struck through; the dashed line addresses a different id space.</title>
		<defs>
		<marker id="arrow" viewBox="0 0 10 10" refX="5" refY="5" markerWidth="6" markerHeight="6" orient="auto">
		<path d="M0,0 L10,5 L0,10 Z" fill="currentColor" />
		</marker>
		<g id="no-glyph">
		<circle cx="0" cy="0" r="8" fill="none" stroke="currentColor" />
		<line x1="-6" y1="-6" x2="6" y2="6" stroke="currentColor" />
		</g>
		</defs>
		<line x1="10" y1="50" x2="90" y2="50" stroke="currentColor" stroke-width="2" />
		<use href="#no-glyph" x="20" y="20" />
		<use xlink:href="#no-glyph" x="60" y="20" />
		<path d="M100,50 L190,50" stroke="currentColor" stroke-dasharray="4 3" marker-end="url(#arrow)" />
		<text x="10" y="90" font-size="10" fill="currentColor">addresses a different id space</text>
		</svg>
		<figcaption>The bridge is struck through; the dashed line addresses a different id space.</figcaption>
		</figure>
		""";

	// --- control: a legitimate diagram is not collateral damage ----------------------------------

	[Fact]
	public void LegitimateDiagram_SurvivesIntact()
	{
		var html = Html(LegitimateDiagram);

		html.Should().Contain("<svg").And.Contain("role=\"img\"");
		html.Should().Contain("<title>The bridge is struck through");
		html.Should().Contain("<marker").And.Contain("viewBox=\"0 0 10 10\"");
		html.Should().Contain("<path").And.Contain("fill=\"currentColor\"");
		html.Should().Contain("stroke-dasharray=\"4 3\"");
		html.Should().Contain("<use");
		html.Should().Contain("<text").And.Contain("addresses a different id space");
		html.Should().Contain("<figcaption>The bridge is struck through");
		// marker-end and the two <use> refs must still resolve to SOME id after namespacing.
		var arrowId = Regex.Match(html, "<marker id=\"(arrow-[0-9a-f]{12})\"").Groups[1].Value;
		arrowId.Should().NotBeEmpty();
		html.Should().Contain($"marker-end=\"url(#{arrowId})\"");
		var glyphId = Regex.Match(html, "<g id=\"(no-glyph-[0-9a-f]{12})\"").Groups[1].Value;
		glyphId.Should().NotBeEmpty();
		html.Should().Contain($"href=\"#{glyphId}\"").And.Contain($"xlink:href=\"#{glyphId}\"");
	}

	// --- forbidden: <script> ----------------------------------------------------------------------

	[Fact]
	public void Script_InsideSvg_IsRemoved()
	{
		var html = Html("<svg><script>alert(document.cookie)</script><circle r=\"5\" /></svg>");
		html.Should().NotContain("<script");
		html.Should().NotContain("alert(document.cookie)");
		html.Should().Contain("<circle");
	}

	// --- forbidden: foreignObject (an escape hatch for arbitrary HTML) ----------------------------

	[Fact]
	public void ForeignObject_AndItsContents_AreRemoved()
	{
		var html = Html("<svg><foreignObject><script>alert(1)</script><b>hi</b></foreignObject><circle r=\"5\" /></svg>");
		html.ToLowerInvariant().Should().NotContain("foreignobject");
		html.Should().NotContain("<script");
		// KeepChildNodes=false: the smuggled content inside the disallowed wrapper is gone too, not
		// just unwrapped — a body-level <b>hi</b> would otherwise survive as plain bold text.
		html.Should().NotContain("hi");
		html.Should().Contain("<circle");
	}

	// --- forbidden: <image> (a data:/external image inside the diagram) ---------------------------

	[Fact]
	public void Image_InsideSvg_IsRemoved()
	{
		var html = Html("<svg><image href=\"https://evil.example/track.png\" /><circle r=\"5\" /></svg>");
		html.Should().NotContain("<image");
		html.Should().NotContain("evil.example");
		html.Should().Contain("<circle");
	}

	// --- forbidden: on* handlers --------------------------------------------------------------------

	[Fact]
	public void OnClickHandler_OnAShape_IsStripped_RestOfElementSurvives()
	{
		var html = Html("<svg><rect onclick=\"alert(1)\" onmouseover=\"alert(2)\" width=\"10\" height=\"10\" fill=\"currentColor\" /></svg>");
		html.Should().NotContain("onclick");
		html.Should().NotContain("onmouseover");
		html.Should().Contain("width=\"10\"").And.Contain("fill=\"currentColor\"");
	}

	// --- forbidden: javascript: scheme on xlink:href ------------------------------------------------

	[Fact]
	public void JavascriptScheme_OnXlinkHref_IsStripped()
	{
		var html = Html("<svg><use xlink:href=\"javascript:alert(1)\" /></svg>");
		html.Should().NotContain("javascript:");
	}

	// --- forbidden: external href/xlink:href (only an internal #fragment is allowed) ---------------

	[Fact]
	public void ExternalHttpsHref_OnUse_IsStripped_EvenThoughHttpsIsAnAllowedScheme()
	{
		// https is allowed for markdown <a> links — the point of this test is that the SAME scheme
		// is still rejected here, because <use> is in the SVG tag set and must stay in-document.
		var html = Html("<svg><defs><g id=\"shape\"><circle r=\"5\" /></g></defs>"
			+ "<use href=\"https://evil.example/x.svg#shape\" /></svg>");
		html.Should().NotContain("evil.example");
		html.Should().NotContain("href=\"https:");
	}

	[Fact]
	public void ExternalHttpsXlinkHref_OnUse_IsStripped()
	{
		var html = Html("<svg><use xlink:href=\"https://evil.example/x.svg#shape\" /></svg>");
		html.Should().NotContain("evil.example");
		html.Should().NotContain("xlink:href=\"https:");
	}

	[Fact]
	public void InternalFragmentHref_OnUse_Survives()
	{
		var html = Html("<svg><defs><g id=\"shape\"><circle r=\"5\" /></g></defs><use href=\"#shape\" /></svg>");
		html.Should().Contain("<use");
		html.Should().MatchRegex("href=\"#shape-[0-9a-f]{12}\"");
	}

	// --- forbidden: <style> inside SVG (not scoped to the SVG — a global stylesheet) ---------------

	[Fact]
	public void StyleTag_InsideSvg_AndItsContents_AreRemoved()
	{
		var html = Html("<svg><style>*{display:none}</style><circle r=\"5\" fill=\"currentColor\" /></svg>");
		html.Should().NotContain("<style");
		html.Should().NotContain("display:none");
		html.Should().Contain("<circle");
	}

	// --- forbidden: external url() reference on fill/stroke/marker-* --------------------------------

	[Fact]
	public void ExternalUrlReference_OnMarkerEnd_IsStripped()
	{
		var html = Html("<svg><path d=\"M0,0 L10,10\" marker-end=\"url(https://evil.example/x.svg#arrow)\" /></svg>");
		html.Should().NotContain("evil.example");
		html.Should().NotContain("marker-end");
	}

	[Fact]
	public void JavascriptUrlReference_OnFill_IsStripped()
	{
		var html = Html("<svg><rect width=\"1\" height=\"1\" fill=\"url(javascript:alert(1))\" /></svg>");
		html.Should().NotContain("javascript:");
		html.Should().NotContain("fill=\"url(");
	}

	[Fact]
	public void PlainColorValue_OnFillAndStroke_Survives()
	{
		var html = Html("<svg><rect width=\"1\" height=\"1\" fill=\"currentColor\" stroke=\"#3b82f6\" /></svg>");
		html.Should().Contain("fill=\"currentColor\"");
		html.Should().Contain("stroke=\"#3b82f6\"");
	}

	// --- id-namespacing: the suffix must satisfy "different things must differ", not "everything
	// must be unique" — it is derived from the SVG's own serialized markup, not randomized. --------

	[Fact]
	public void TwoRendersOfTheSameDiagram_GetTheSameIds()
	{
		// Inverts the old (now-wrong) "must differ" expectation: `editor-preview-renders-server-side`
		// requires byte-identical HTML between the editor preview and the saved body, and preview
		// and save are two separate renders of the SAME text. A content-derived suffix makes two
		// renders of the same diagram collide on the same id — which is harmless, because the
		// cross-reference lands on an identical shape (see SameSourceText_RendersByteIdentically
		// below for the end-to-end version of this property).
		const string md = "<svg><defs><marker id=\"arrow\" viewBox=\"0 0 10 10\"><path d=\"M0,0Z\" /></marker></defs>"
			+ "<path d=\"M0,0 L1,1\" marker-end=\"url(#arrow)\" /></svg>";
		var html1 = Html(md);
		var html2 = Html(md);

		var id1 = Regex.Match(html1, "id=\"(arrow-[0-9a-f]{12})\"").Groups[1].Value;
		var id2 = Regex.Match(html2, "id=\"(arrow-[0-9a-f]{12})\"").Groups[1].Value;
		id1.Should().NotBeEmpty();
		id2.Should().NotBeEmpty();
		id1.Should().Be(id2, "the same source text rendered twice must produce the same suffix");
		html1.Should().Contain($"marker-end=\"url(#{id1})\"");
		html2.Should().Contain($"marker-end=\"url(#{id2})\"");
	}

	[Fact]
	public void TwoDifferentDiagrams_GetDifferentIds_SoTheyCannotCollideOnOnePage()
	{
		// The property that must survive the switch away from Guid.NewGuid(): node bodies AND
		// comment bodies share one renderer and commonly cohabit one board/thread page — two
		// authors pasting DIFFERENT diagrams that happen to reuse the source id "arrow" must not
		// have the second one's <marker>/<use> silently start resolving against the first one's
		// <defs>. Without this test, a content-derived suffix would be unproven — it could
		// degenerate to a constant and still pass the "same diagram, same id" test above.
		const string md1 = "<svg><defs><marker id=\"arrow\" viewBox=\"0 0 10 10\"><path d=\"M0,0Z\" /></marker></defs>"
			+ "<path d=\"M0,0 L1,1\" marker-end=\"url(#arrow)\" /></svg>";
		const string md2 = "<svg><defs><marker id=\"arrow\" viewBox=\"0 0 10 10\"><path d=\"M0,0 L2,2Z\" /></marker></defs>"
			+ "<path d=\"M0,0 L1,1\" marker-end=\"url(#arrow)\" /></svg>";
		var html1 = Html(md1);
		var html2 = Html(md2);

		var id1 = Regex.Match(html1, "id=\"(arrow-[0-9a-f]{12})\"").Groups[1].Value;
		var id2 = Regex.Match(html2, "id=\"(arrow-[0-9a-f]{12})\"").Groups[1].Value;
		id1.Should().NotBeEmpty();
		id2.Should().NotBeEmpty();
		id1.Should().NotBe(id2, "two genuinely different diagrams sharing a source id must not collide on one page");
	}

	[Fact]
	public void SameSourceText_RendersByteIdentically()
	{
		// The end-to-end property `editor-preview-renders-server-side` actually needs: the SAME body
		// text — containing a diagram with ids — rendered twice (once for the editor preview, once
		// for the saved body) must produce byte-identical HTML. This was NOT provable before this
		// fix: Guid.NewGuid() made every render diverge on the suffix.
		var html1 = Html(LegitimateDiagram);
		var html2 = Html(LegitimateDiagram);
		html1.Should().Be(html2, "identical source text must render byte-identically, ids included");
	}

	// --- id-scoping: `id` only ever survives inside an SVG --------------------------------------

	[Fact]
	public void IdAttribute_OnANonSvgElement_IsStripped()
	{
		// `id` is now allowed at the sanitizer level (SVG needs it) but must stay scoped to SVG —
		// otherwise any body could plant `id="whatever-the-app-relies-on"` on an ordinary element.
		var html = Html("<div id=\"app-shell\">hi</div>");
		html.Should().NotContain("id=\"app-shell\"");
		html.Should().Contain("hi");
	}

	// --- the shared-renderer consequence: comments use the exact same call, so the exact same body
	// renders the exact same diagram regardless of caller. _MdBody.cshtml is the ONE call site for
	// both node bodies and comment bodies (Md.RenderToHtml(Model.Body, ...)) — there is no separate
	// "comment rendering" code path to diverge. Ids differ ONLY by the per-render namespacing token
	// above (by design), so they are normalized out before comparing structural identity. ------------

	[Fact]
	public void SameDiagramBody_RendersStructurallyIdentically_RegardlessOfCaller()
	{
		var html1 = Html(LegitimateDiagram);
		var html2 = Html(LegitimateDiagram);
		Normalize(html1).Should().Be(Normalize(html2));
	}

	static string Normalize(string html) => Regex.Replace(html, "-[0-9a-f]{12}(?=[\"')#])", "-ID");
}

// Long-code-block folding (work `md-code-block-height-cap`). `.md-body pre` had only overflow-x, so
// a long listing grew to any height and pushed the rest of the body off the screen.
//
// The height cap itself is CSS and is not observable here — what IS observable, and what these
// tests pin, is the decision the SERVER makes and CSS cannot: WHICH blocks are long. CSS can only
// cap unconditionally, which would hand a two-line snippet the same clipped box and the same
// control as a 200-line listing. So the renderer counts SOURCE lines and wraps only what exceeds
// the threshold; everything below it must come out exactly as it did before this feature.
//
// The other half these tests hold down is that NOTHING IS TRUNCATED IN THE HTML. The fold is
// presentational: the <pre> still carries every line, so copy/paste, browser find and reader-view
// (the whole reason read surfaces render server-side at all) still see the complete listing.
public sealed class MarkdownRendererCodeFoldTests
{
	static readonly IMarkdownRenderer R = new MarkdownRenderer();

	static string Html(string md) => R.RenderToHtml(md);

	// A fenced block with exactly `lines` content lines, each one identifiable.
	static string Fence(int lines) =>
		"```csharp\n" + string.Join("\n", Enumerable.Range(1, lines).Select(i => $"var line{i} = {i};")) + "\n```";

	// --- the threshold: a short block is left completely alone -----------------------------------

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(9)]
	[InlineData(10)] // the threshold is strictly greater-than: exactly 10 lines is still short
	public void ShortCodeBlock_IsNotWrappedAndGetsNoControl(int lines)
	{
		var html = Html(Fence(lines));
		html.Should().Contain("<pre>");
		html.Should().NotContain("md-code-fold");
		html.Should().NotContain("<details");
		html.Should().NotContain("<summary");
	}

	[Fact]
	public void ShortCodeBlock_RendersExactlyAsItDidBeforeTheFeature()
	{
		// Not "contains" but the WHOLE output: the promise for a short block is that its markup is
		// untouched, and a stray wrapper or attribute would still satisfy every Contain above.
		Html("```\nalpha\nbeta\n```")
			.Should().Be("<pre><code>alpha\nbeta\n</code></pre>\n");
	}

	// --- past the threshold: wrapper + a native disclosure control -------------------------------

	[Theory]
	[InlineData(11)]
	[InlineData(12)]
	[InlineData(40)]
	public void LongCodeBlock_IsWrappedAndGetsADisclosureControl(int lines)
	{
		var html = Html(Fence(lines));
		html.Should().Contain("<div class=\"md-code-fold\">");
		html.Should().Contain("<details class=\"md-code-fold-toggle\">");
		html.Should().Contain("<summary>");
		// The control names the size — a bare "expand" makes the reader click to find out whether
		// clicking was worth it.
		html.Should().Contain($"Show all {lines} lines");
	}

	[Fact]
	public void FoldControl_IsClosedByDefault_AndSitsAfterTheBlock()
	{
		var html = Html(Fence(30));
		html.Should().NotContain("md-code-fold-toggle\" open");
		html.IndexOf("</pre>", StringComparison.Ordinal)
			.Should().BeLessThan(html.IndexOf("<details", StringComparison.Ordinal),
				"the control belongs under the block it expands, in reading order — and the code must "
				+ "not sit inside <summary>, where every click on it would toggle the fold");
	}

	[Fact]
	public void FoldedBlock_StillCarriesEveryLine()
	{
		// The cap is presentational. Truncating the HTML would break copy/paste, browser find and
		// reader-view — the exact things server-side rendering exists for.
		//
		// Read on the block's VISIBLE TEXT, not the raw HTML: since `md-code-syntax-highlighting`
		// a `csharp` block is tokenized, so `var` sits in its own <span> and `var line1 = 1;` is no
		// longer one contiguous run of markup. What this test protects — every line is still THERE
		// — is a claim about the text a reader and a copy/paste get, and that is now what it reads.
		var html = Html(Fence(40));
		var text = MarkdownCodeText.VisibleCodeText(html);
		for (var i = 1; i <= 40; i++) text.Should().Contain($"var line{i} = {i};");
	}

	[Fact]
	public void FoldWrapperClasses_SurviveTheSanitizer()
	{
		// The sanitizer allows `class` but pins its VALUES (MarkdownRenderer.DesignLayerClasses);
		// a name missing from that list renders the control unstyled and uncapped — the quiet way
		// this feature would half-work.
		var html = Html(Fence(20));
		foreach (var name in new[] { "md-code-fold", "md-code-fold-toggle", "md-code-fold-more", "md-code-fold-less" })
			html.Should().Contain($"class=\"{name}\"");
	}

	[Fact]
	public void LineCount_CountsContentLinesOnly_NotTheFences()
	{
		// 12 content lines, two of them blank; the ``` fences are not lines of code.
		var body = "```\n" + string.Join("\n", "a", "", "c", "d", "e", "f", "g", "", "i", "j", "k", "l") + "\n```";
		Html(body).Should().Contain("Show all 12 lines");
	}

	// --- every code form, at every depth ---------------------------------------------------------

	[Fact]
	public void IndentedCodeBlock_IsFoldedToo()
	{
		// The four-space form is a different Markdig type but the same thing on screen.
		var indented = "text\n\n" + string.Join("\n", Enumerable.Range(1, 15).Select(i => $"    line{i}")) + "\n";
		Html(indented).Should().Contain("<div class=\"md-code-fold\">").And.Contain("Show all 15 lines");
	}

	[Fact]
	public void CodeBlockInsideAListItem_IsFolded()
	{
		var md = "- item\n\n  ```\n" + string.Join("\n", Enumerable.Range(1, 14).Select(i => $"  x{i}")) + "\n  ```\n";
		Html(md).Should().Contain("md-code-fold");
	}

	// --- the interaction with the section container ---------------------------------------------

	[Fact]
	public void FoldedBlock_InsideASection_StaysInsideThatSection()
	{
		// `##` groups its content into <section class="md-section">, and that section's "a code
		// block may reach my edges" rule is a DIRECT-child selector. The wrapper landing between
		// the two is exactly what app.css's third selector there covers; if this nesting ever came
		// out differently, that CSS would be aimed at nothing.
		var html = Html("## Heading\n\n" + Fence(20));
		var section = html.IndexOf("<section class=\"md-section\">", StringComparison.Ordinal);
		var fold = html.IndexOf("<div class=\"md-code-fold\">", StringComparison.Ordinal);
		var close = html.IndexOf("</section>", StringComparison.Ordinal);
		section.Should().BeGreaterThanOrEqualTo(0);
		fold.Should().BeGreaterThan(section);
		close.Should().BeGreaterThan(fold);
	}

	[Fact]
	public void FoldedBlock_KeepsHtmlInsideItEscaped()
	{
		// The wrapper must not change what the code block itself is: text, never live markup.
		var html = Html("```html\n" + string.Join("\n", Enumerable.Repeat("<script>alert(1)</script>", 12)) + "\n```");
		html.Should().Contain("md-code-fold");
		html.Should().Contain("&lt;script&gt;");
		html.Should().NotContain("<script");
	}
}

// Work `md-code-wrap-not-scroll`: long lines in a code block wrap instead of hiding behind a
// horizontal scrollbar. That is a decision about how the box DRAWS text, and this file exists to
// hold the line that it stayed one — the owner's own argument for calling it "pure styling" was
// that no break characters get inserted, and the cheap way to make wrapping work is to break that
// promise. Every plausible shortcut is the same bug wearing a different codepoint:
//
//   <wbr>            a real element in the markup; copies as nothing in some browsers, but it is
//                    markup inside the code text and browser-find stops matching across it
//   U+00AD           soft hyphen — copies out, and pastes a stray hyphen into a shell command
//   U+200B           zero-width space — copies out INVISIBLY, which is worse: a pasted command
//                    fails with an error that names a character the reader cannot see
//
// So the contract is exact equality between the source and the text the page shows, not "looks
// right". VisibleCodeText is the right side of that comparison on purpose: it is the text after
// tag-stripping and entity decoding — what a copy/paste and a browser find actually get.
public sealed class MarkdownCodeCopyFidelityTests
{
	static readonly IMarkdownRenderer R = new MarkdownRenderer();

	// The real shape from the card that prompted the work: one `ip route` line, 298 characters, no
	// space anywhere near the wrap point in the middle of it, plus a long unbroken URL and a long
	// Windows path. These are exactly the tokens `overflow-wrap: anywhere` has to break visually —
	// and exactly the tokens a break character would be tempting to insert into.
	static readonly string LongLine =
		"ip route add 10.8.0.0/24 via 192.168.1.1 dev eth0 src 192.168.1.42 metric 100 table "
		+ new string('x', 298 - 84);

	const string LongUrl =
		"https://example.invalid/a/very/long/path/that/never/breaks/because/it/has/no/spaces/in/it/at/all/index.html?token=abcdefghijklmnopqrstuvwxyz0123456789";

	const string LongPath = @"C:\Users\someone\AppData\Local\Programs\SomeVendor\SomeProduct\bin\tools\subtool\runner.exe";

	// Characters a "make it wrap" shortcut inserts. Checked on the RAW HTML, not the visible text:
	// <wbr> would be stripped by the tag regex and a test that only compared visible text would
	// sail straight past it.
	static readonly (string Needle, string What)[] Forbidden =
	[
		("<wbr", "a <wbr> element"),
		("&shy;", "an HTML soft-hyphen entity"),
		("&#173;", "a numeric soft-hyphen entity"),
		("\u00ad", "a soft hyphen (U+00AD)"),
		("\u200b", "a zero-width space (U+200B)"),
		("\u200c", "a zero-width non-joiner (U+200C)"),
		("\u2060", "a word joiner (U+2060)"),
	];

	static void AssertRoundTrips(string sourceLine, string language)
	{
		var html = R.RenderToHtml($"```{language}\n{sourceLine}\n```");

		foreach (var (needle, what) in Forbidden)
			html.Should().NotContain(needle,
				$"wrapping is done by CSS alone — {what} in the markup would paste into the reader's shell");

		MarkdownCodeText.VisibleCodeText(html).TrimEnd('\n').Should().Be(sourceLine,
			"the block must copy out as the EXACT source line, character for character");
	}

	[Theory]
	// No language: the block is emitted verbatim, not tokenized.
	[InlineData("")]
	// With a language the block goes through TextMate highlighting and its text is interleaved with
	// <span class="hl-*"> elements. Those spans are the one place a break character could be
	// smuggled in unnoticed, so the fidelity claim has to be made on this path too.
	[InlineData("bash")]
	public void A298CharacterCommand_CopiesOutUnchanged(string language) => AssertRoundTrips(LongLine, language);

	[Theory]
	[InlineData("")]
	[InlineData("text")]
	public void ALongUrl_CopiesOutUnchanged(string language) => AssertRoundTrips(LongUrl, language);

	[Theory]
	[InlineData("")]
	[InlineData("powershell")]
	public void ALongWindowsPath_CopiesOutUnchanged(string language) => AssertRoundTrips(LongPath, language);

	[Fact]
	public void ALongLine_InsideAFoldedBlock_AlsoCopiesOutUnchanged()
	{
		// The wrapper the fold adds is the other place markup gets injected around code. Twelve
		// lines puts the block past FoldLineThreshold, and the long line is one of them.
		var lines = Enumerable.Range(1, 11).Select(i => $"echo {i}").Append(LongLine);
		var html = R.RenderToHtml("```bash\n" + string.Join("\n", lines) + "\n```");

		html.Should().Contain("md-code-fold", "twelve lines must still be folded");
		foreach (var (needle, what) in Forbidden)
			html.Should().NotContain(needle, $"the fold wrapper must not introduce {what} either");
		MarkdownCodeText.VisibleCodeText(html).Should().Contain(LongLine,
			"folding hides lines visually; it must not rewrite the one long line inside them");
	}
}

using PetBox.Web.Rendering;

namespace PetBox.Tests.Web;

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
// (RepoSettings.CommitUrlTemplate, literal {sha} placeholder), standalone 7–40-hex words in PLAIN
// TEXT runs become links to the commit view. Code spans/blocks and existing links are excluded;
// with no usable template the output is byte-identical to the template-less path.
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

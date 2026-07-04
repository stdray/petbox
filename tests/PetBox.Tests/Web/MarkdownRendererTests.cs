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

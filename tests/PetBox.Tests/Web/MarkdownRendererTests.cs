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

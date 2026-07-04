using Ganss.Xss;
using Markdig;

namespace PetBox.Web.Rendering;

// The ONE server-side markdown renderer. Mirrors the client pipeline (ts/markdown.ts: marked
// gfm:true, breaks:true + DOMPurify):
//   - Markdig .UseAdvancedExtensions()  → GFM-ish (tables, autolinks, task lists, strikethrough)
//   - .UseSoftlineBreakAsHardlineBreak() → breaks:true (a bare \n becomes <br />)
//   - raw HTML in a body is KEPT (valid content, parity with DOMPurify) and sanitized afterwards.
// HtmlSanitizer (Ganss.Xss) strips <script>, event handlers (onerror/onclick/…) and neutralizes
// dangerous URL schemes (javascript:/data:/vbscript:) on links & images, allowing only
// http/https/mailto plus relative paths and in-page #anchors.
//
// Both the MarkdownPipeline and the HtmlSanitizer are built once and reused: Markdig's pipeline is
// documented thread-safe when a fresh renderer is created per call (which ToHtml does), and
// HtmlSanitizer.Sanitize is thread-safe. Registered as a singleton in Program.cs.
public sealed class MarkdownRenderer : IMarkdownRenderer
{
	readonly MarkdownPipeline _pipeline;
	readonly HtmlSanitizer _sanitizer;

	public MarkdownRenderer()
	{
		_pipeline = new MarkdownPipelineBuilder()
			.UseAdvancedExtensions()
			.UseSoftlineBreakAsHardlineBreak()
			.Build();

		_sanitizer = BuildSanitizer();
	}

	public string RenderToHtml(string? markdown)
	{
		if (string.IsNullOrEmpty(markdown)) return "";
		var html = Markdown.ToHtml(markdown, _pipeline);
		return _sanitizer.Sanitize(html);
	}

	static HtmlSanitizer BuildSanitizer()
	{
		// Start from HtmlSanitizer's safe defaults (script/style/event-handlers already stripped)
		// and pin the URL-scheme allowlist to what a markdown body legitimately needs. Relative
		// URLs and in-page #anchors carry no scheme and are kept by default.
		var s = new HtmlSanitizer();
		s.AllowedSchemes.Clear();
		s.AllowedSchemes.Add("http");
		s.AllowedSchemes.Add("https");
		s.AllowedSchemes.Add("mailto");
		return s;
	}
}

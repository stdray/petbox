using System.Text.RegularExpressions;
using Ganss.Xss;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

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
	// A standalone word of 7–40 hex chars (a git commit hash / short ref). \b keeps it to a whole
	// word so a hash glued to letters (`x1234567`) or a 6-hex word is left alone. At least one
	// a-f letter is required: an all-digit word is far more likely a date (20260704) or a
	// timestamp than a commit hash, and false links on plain numbers hurt more than missing the
	// ~4% of short hashes that happen to be all digits.
	static readonly Regex HashRx = new(@"\b(?=[0-9]*[a-fA-F])[0-9a-fA-F]{7,40}\b",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

	public string RenderToHtml(string? markdown, string? commitUrlTemplate = null)
	{
		if (string.IsNullOrEmpty(markdown)) return "";

		// No usable template → the original single-pass path, byte-identical to pre-feature output.
		if (!CommitUrl.HasTemplate(commitUrlTemplate))
			return _sanitizer.Sanitize(Markdown.ToHtml(markdown, _pipeline));

		// Template present: parse to the AST, autolink standalone commit hashes inside plain text
		// runs (code spans/blocks carry no LiteralInline, existing links are skipped), then render
		// with the SAME pipeline. Per-call, no shared mutable state.
		var doc = Markdown.Parse(markdown, _pipeline);
		Linkify(doc, commitUrlTemplate!);

		using var writer = new StringWriter();
		var renderer = new HtmlRenderer(writer);
		_pipeline.Setup(renderer);
		renderer.Render(doc);
		writer.Flush();
		return _sanitizer.Sanitize(writer.ToString());
	}

	// Replace commit-hash words in every text run with links to the commit view. LiteralInline is
	// the only node that carries free body text — code spans (CodeInline) and code blocks keep
	// their text as a raw StringSlice, so they are never visited; link text is skipped so we never
	// nest an <a> inside an <a>.
	static void Linkify(MarkdownDocument doc, string template)
	{
		// Snapshot first — splicing the tree would break a live descendant walk.
		var literals = new List<LiteralInline>();
		foreach (var li in doc.Descendants<LiteralInline>())
			if (!InsideLink(li))
				literals.Add(li);

		foreach (var li in literals)
		{
			var text = li.Content.ToString();
			var matches = HashRx.Matches(text);
			if (matches.Count == 0) continue;

			// Rebuild the run as [text?, link, text?, link, …] spliced in after the original run.
			Inline anchor = li;
			var pos = 0;
			foreach (Match m in matches)
			{
				if (m.Index > pos)
					anchor = InsertAfter(anchor, new LiteralInline(text.Substring(pos, m.Index - pos)));

				var link = new LinkInline(CommitUrl.For(template, m.Value)!, "");
				var attrs = link.GetAttributes();
				attrs.AddProperty("target", "_blank");
				attrs.AddProperty("rel", "noopener");
				link.AppendChild(new LiteralInline(m.Value));
				anchor = InsertAfter(anchor, link);

				pos = m.Index + m.Length;
			}
			if (pos < text.Length)
				InsertAfter(anchor, new LiteralInline(text.Substring(pos)));

			li.Remove();
		}
	}

	static Inline InsertAfter(Inline anchor, Inline node)
	{
		anchor.InsertAfter(node);
		return node;
	}

	static bool InsideLink(Inline inline)
	{
		for (ContainerInline? p = inline.Parent; p is not null; p = p.Parent)
			if (p is LinkInline) return true;
		return false;
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
		// The commit-hash autolinks open in a new tab; allow just the two attributes they carry.
		s.AllowedAttributes.Add("target");
		s.AllowedAttributes.Add("rel");
		return s;
	}
}

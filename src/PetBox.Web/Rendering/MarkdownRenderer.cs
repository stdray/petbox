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

	// A `[[slug]]` node mention: the same flat-slug shape a board key has (a-z start,
	// a-z0-9_- body, ≤100 chars). Agents write these inline; when the slug resolves to a
	// project node the run becomes a link (group 1 = the bare slug, no brackets).
	static readonly Regex NodeRefRx = new(NodeRefs.SlugPattern,
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

	public string RenderToHtml(string? markdown, string? commitUrlTemplate = null,
		IReadOnlyDictionary<string, NodeRefTarget>? nodeRefs = null)
	{
		if (string.IsNullOrEmpty(markdown)) return "";

		var hasTemplate = CommitUrl.HasTemplate(commitUrlTemplate);
		var hasNodeRefs = nodeRefs is { Count: > 0 };

		// No usable context → the original single-pass path, byte-identical to pre-feature output.
		if (!hasTemplate && !hasNodeRefs)
			return _sanitizer.Sanitize(Markdown.ToHtml(markdown, _pipeline));

		// Context present: parse to the AST, autolink standalone commit hashes and/or resolve
		// `[[slug]]` mentions inside plain text runs (code spans/blocks carry no LiteralInline,
		// existing links are skipped), then render with the SAME pipeline. Per-call, no shared
		// mutable state.
		var doc = Markdown.Parse(markdown, _pipeline);
		Linkify(doc, hasTemplate ? commitUrlTemplate! : null, hasNodeRefs ? nodeRefs : null);

		using var writer = new StringWriter();
		var renderer = new HtmlRenderer(writer);
		_pipeline.Setup(renderer);
		renderer.Render(doc);
		writer.Flush();
		return _sanitizer.Sanitize(writer.ToString());
	}

	// Rewrite every plain text run: commit-hash words → commit-view links (when `template` is set)
	// and resolved `[[slug]]` mentions → node links (when `nodeRefs` is set). Both transforms share
	// ONE walk. LiteralInline is the only node that carries free body text — code spans (CodeInline)
	// and code blocks keep their text as a raw StringSlice, so they are never visited; link text is
	// skipped so we never nest an <a> inside an <a>. Consecutive literal siblings are REJOINED
	// before matching: Markdig fragments a `[[slug]]` mention across several literal runs (the
	// bracket-delimiter handling), so a per-run scan would never see the whole pattern.
	static void Linkify(MarkdownDocument doc, string? template, IReadOnlyDictionary<string, NodeRefTarget>? nodeRefs)
	{
		// Snapshot maximal groups of consecutive LiteralInline siblings (outside links) first —
		// splicing the tree would break a live walk. `Descendants<LiteralInline>()` yields them in
		// document order; a group extends while the next literal is the SAME parent's immediate
		// NextSibling (so a link/emphasis/code node between them, or a filtered link-text literal,
		// breaks the run).
		var groups = new List<List<LiteralInline>>();
		List<LiteralInline>? current = null;
		LiteralInline? prev = null;
		foreach (var lit in doc.Descendants<LiteralInline>())
		{
			if (InsideLink(lit)) { prev = null; continue; }
			if (current is not null && prev is not null
				&& ReferenceEquals(lit.Parent, prev.Parent) && ReferenceEquals(prev.NextSibling, lit))
			{
				current.Add(lit);
			}
			else
			{
				if (current is { Count: > 0 }) groups.Add(current);
				current = new List<LiteralInline> { lit };
			}
			prev = lit;
		}
		if (current is { Count: > 0 }) groups.Add(current);

		foreach (var run in groups)
			LinkifyRun(run, template, nodeRefs);
	}

	// Rewrite one run of consecutive literal siblings: match over their COMBINED text, splice the
	// resulting [text?, link, …] sequence in place, and drop the originals.
	static void LinkifyRun(List<LiteralInline> run, string? template, IReadOnlyDictionary<string, NodeRefTarget>? nodeRefs)
	{
		var text = string.Concat(run.Select(l => l.Content.ToString()));

		// Collect every replacement (position, length, link node), ordered by position and
		// non-overlapping. `[[slug]]` spans are computed first (resolved or not) so a commit hash
		// that happens to sit INSIDE an unresolved mention (e.g. `[[abc1234]]`) stays literal — an
		// unresolvable mention renders as its original text, brackets included.
		var repls = new List<(int Index, int Length, LinkInline Link)>();
		var refSpans = new List<(int Start, int End)>();
		if (nodeRefs is not null)
		{
			foreach (Match m in NodeRefRx.Matches(text))
			{
				refSpans.Add((m.Index, m.Index + m.Length));
				if (nodeRefs.TryGetValue(m.Groups[1].Value, out var target))
					repls.Add((m.Index, m.Length, NodeRefLink(target, m.Groups[1].Value)));
			}
		}
		if (template is not null)
		{
			foreach (Match m in HashRx.Matches(text))
				if (!refSpans.Any(s => m.Index >= s.Start && m.Index < s.End))
					repls.Add((m.Index, m.Length, CommitLink(template, m.Value)));
		}
		if (repls.Count == 0) return;
		repls.Sort((a, b) => a.Index.CompareTo(b.Index));

		// Splice the rebuilt sequence in after the run's LAST literal, then remove the originals
		// (the new nodes are already linked after it, so removing the run keeps them in place).
		Inline anchor = run[^1];
		var pos = 0;
		foreach (var (index, length, link) in repls)
		{
			if (index < pos) continue; // defensive: skip any overlap
			if (index > pos)
				anchor = InsertAfter(anchor, new LiteralInline(text.Substring(pos, index - pos)));
			anchor = InsertAfter(anchor, link);
			pos = index + length;
		}
		if (pos < text.Length)
			InsertAfter(anchor, new LiteralInline(text.Substring(pos)));

		foreach (var l in run) l.Remove();
	}

	// A commit-view link opening in a new tab (target/rel survive the sanitizer's attribute allowlist).
	static LinkInline CommitLink(string template, string sha)
	{
		var link = new LinkInline(CommitUrl.For(template, sha)!, "");
		var attrs = link.GetAttributes();
		attrs.AddProperty("target", "_blank");
		attrs.AddProperty("rel", "noopener");
		link.AppendChild(new LiteralInline(sha));
		return link;
	}

	// A node-mention link: href = the resolved node URL, title attribute = the node title, link
	// TEXT = the mentioned slug (plain, no brackets — even if the node was since renamed).
	static LinkInline NodeRefLink(NodeRefTarget target, string slug)
	{
		var link = new LinkInline(target.Url, target.Title ?? "");
		link.AppendChild(new LiteralInline(slug));
		return link;
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

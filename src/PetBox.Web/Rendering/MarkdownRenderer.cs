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
	// A standalone word shaped like a git commit hash: 7–12 hex (abbreviated) or exactly 40 hex
	// (full). NOT the naive 7–40 range — PetBox's own identifiers live in between (32-hex NodeIds,
	// memory-note keys), and hash-autolinking them as commits is worse than missing an unusually
	// long abbreviation. Edges must not touch a word char OR a hyphen (custom lookarounds instead
	// of \b): `\b` treats `-` as a boundary, which turned the hex tail of prefixed keys like
	// `m-<32hex>` / `ac-<12hex>` into "hashes". At least one a-f letter is required: an all-digit
	// word is far more likely a date (20260704) or a timestamp than a commit hash.
	static readonly Regex HashRx = new(
		@"(?<![\w-])(?=[0-9]*[a-fA-F])(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{7,12})(?![\w-])",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	// A `[[slug]]` node mention: the same flat-slug shape a board key has (a-z start,
	// a-z0-9_- body, ≤100 chars). Agents write these inline; when the slug resolves to a
	// project node the run becomes a link (group 1 = the bare slug, no brackets).
	static readonly Regex NodeRefRx = new(NodeRefs.SlugPattern,
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	// A generated memory-entry key (`m-<32hex>` / `ac-<12hex>`) mentioned in prose. Same shape the
	// pre-scan (MemoryRefs) collects; only a key the caller RESOLVED (unambiguously, in a
	// non-sensitive store) is in the map and becomes a link — anything else stays literal.
	static readonly Regex MemoryRefRx = new(MemoryRefs.KeyPattern,
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
		IReadOnlyDictionary<string, NodeRefTarget>? nodeRefs = null,
		IReadOnlyDictionary<string, NodeRefTarget>? memoryRefs = null)
	{
		if (string.IsNullOrEmpty(markdown)) return "";

		var hasTemplate = CommitUrl.HasTemplate(commitUrlTemplate);
		var hasNodeRefs = nodeRefs is { Count: > 0 };
		var hasMemoryRefs = memoryRefs is { Count: > 0 };

		// No usable context → the original single-pass path, byte-identical to pre-feature output.
		if (!hasTemplate && !hasNodeRefs && !hasMemoryRefs)
			return _sanitizer.Sanitize(Markdown.ToHtml(markdown, _pipeline));

		// Context present: parse to the AST, then in ONE walk autolink standalone commit hashes,
		// resolve `[[slug]]` mentions and link resolved memory keys inside plain text runs (code
		// spans/blocks carry no LiteralInline, existing links are skipped), then render with the
		// SAME pipeline. Per-call, no shared mutable state.
		var doc = Markdown.Parse(markdown, _pipeline);
		Linkify(doc, hasTemplate ? commitUrlTemplate! : null, hasNodeRefs ? nodeRefs : null,
			hasMemoryRefs ? memoryRefs : null);

		using var writer = new StringWriter();
		var renderer = new HtmlRenderer(writer);
		_pipeline.Setup(renderer);
		renderer.Render(doc);
		writer.Flush();
		return _sanitizer.Sanitize(writer.ToString());
	}

	// Rewrite every plain text run: commit-hash words → commit-view links (when `template` is set),
	// resolved `[[slug]]` mentions → node links (when `nodeRefs` is set) and resolved memory keys →
	// memory-entry links (when `memoryRefs` is set). All three transforms share
	// ONE walk. LiteralInline is the only node that carries free body text — code spans (CodeInline)
	// and code blocks keep their text as a raw StringSlice, so they are never visited; link text is
	// skipped so we never nest an <a> inside an <a>. Consecutive literal siblings are REJOINED
	// before matching: Markdig fragments a `[[slug]]` mention across several literal runs (the
	// bracket-delimiter handling), so a per-run scan would never see the whole pattern.
	static void Linkify(MarkdownDocument doc, string? template,
		IReadOnlyDictionary<string, NodeRefTarget>? nodeRefs,
		IReadOnlyDictionary<string, NodeRefTarget>? memoryRefs)
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
			LinkifyRun(run, template, nodeRefs, memoryRefs);
	}

	// Rewrite one run of consecutive literal siblings: match over their COMBINED text, splice the
	// resulting [text?, link, …] sequence in place, and drop the originals.
	static void LinkifyRun(List<LiteralInline> run, string? template,
		IReadOnlyDictionary<string, NodeRefTarget>? nodeRefs,
		IReadOnlyDictionary<string, NodeRefTarget>? memoryRefs)
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
		// Memory keys next (before commit hashes, like the mention spans: a key sitting inside an
		// unresolved `[[…]]` mention stays part of that literal). A key present in the map resolved
		// UNAMBIGUOUSLY to one non-sensitive store — a missing/ambiguous/sensitive key is simply
		// absent from the map and therefore stays literal here.
		if (memoryRefs is not null)
		{
			foreach (Match m in MemoryRefRx.Matches(text))
				if (!refSpans.Any(s => m.Index >= s.Start && m.Index < s.End)
					&& memoryRefs.TryGetValue(m.Groups[1].Value, out var target))
				{
					refSpans.Add((m.Index, m.Index + m.Length));
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

	// A mention link (a `[[slug]]` node ref, or a memory key): href = the resolved URL, title
	// attribute = the target's title, link TEXT = the mention as written (a bare slug, no brackets —
	// even if the node was since renamed; or the memory key verbatim).
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

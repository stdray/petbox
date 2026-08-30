using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
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
		var builder = new MarkdownPipelineBuilder()
			.UseAdvancedExtensions()
			.UseSoftlineBreakAsHardlineBreak()
			// GFM alerts (`> [!NOTE]`). Declared EXPLICITLY although it is currently redundant:
			// measured against the pinned Markdig 1.3.2, UseAdvancedExtensions() already registers
			// AlertExtension (it is first in that extension list), and UseAlertBlocks() is
			// AddIfNotAlready — calling it leaves exactly one instance. It is kept so that alerts
			// are a stated requirement of this pipeline rather than a side effect of whatever
			// UseAdvancedExtensions happens to bundle in the next Markdig.
			//
			// So alerts were never the thing that was off. The parser has been producing
			// AlertBlock all along and the renderer has been emitting
			// `<div class="markdown-alert markdown-alert-note">` all along — and the SANITIZER
			// below was deleting the class attribute, so every alert reached the browser as a bare
			// <div> that looked like two ordinary paragraphs. The allowlist in BuildSanitizer is
			// what actually turns a callout into a callout.
			.UseAlertBlocks();
		builder.Extensions.AddIfNotAlready(new MarkdownDesignLayerExtension());
		_pipeline = builder.Build();

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
		// The design layer (work `node-render-design-layer`) is CSS keyed on classes, and
		// HtmlSanitizer does NOT allow `class` by default — it drops the attribute wholesale.
		// Allowing the attribute alone would hand every body author the entire Tailwind utility
		// set (raw HTML in a body is deliberately KEPT, see the header), so `class` is allowed
		// AND its VALUES are pinned: a non-empty AllowedClasses filters each class list down to
		// these names and removes the attribute when nothing survives. An author writing
		// `<div class="fixed inset-0">` in a body still gets a bare <div>.
		s.AllowedAttributes.Add("class");
		foreach (var name in DesignLayerClasses) s.AllowedClasses.Add(name);
		ConfigureSvgSubset(s);
		return s;
	}

	// Every class name the renderer itself emits: the two structural wrappers from
	// MarkdownDesignLayerExtension, plus Markdig's own alert classes (its AlertBlockRenderer emits
	// `markdown-alert markdown-alert-{kind}` and a `markdown-alert-title` paragraph). Anything not
	// listed here is stripped from a body's HTML — so a new wrapper class MUST be added here or it
	// renders unstyled.
	static readonly string[] DesignLayerClasses =
	[
		"md-section",
		"md-table-scroll",
		// The long-code-block fold (work `md-code-block-height-cap`). Four names, one wrapper: the
		// container the CSS cap hangs off, the <details> the `:has()` selector reads, and the two
		// label spans it swaps. `details`/`summary`/`span` are already allowed TAGS in
		// HtmlSanitizer's defaults (measured), so only the classes needed adding — without them the
		// control renders as an unstyled, uncapped disclosure triangle.
		"md-code-fold",
		"md-code-fold-toggle",
		"md-code-fold-more",
		"md-code-fold-less",
		"markdown-alert",
		"markdown-alert-title",
		"markdown-alert-note",
		"markdown-alert-tip",
		"markdown-alert-important",
		"markdown-alert-warning",
		"markdown-alert-caution",
	];

	// ── Diagram support (spec `body-carries-diagram`) ──────────────────────────────────────────
	// A body may carry a sanitized inline-SVG subset: raw HTML is already kept-then-sanitized (see
	// the header comment), so this is an EXTENSION of a live mechanism, not a new one. Owner's
	// decision: a pinned SVG subset, not mermaid — the reference diagram (a struck-through bridge,
	// a dashed "different id space", three distinct kinds of "no") is not expressible in a boxed
	// diagramming language, and mermaid would add a frontend dependency + a post-swap re-init hook
	// for a case it can't even render.
	//
	// There is NO CSP in this app (Program.cs sets HSTS only) — this allowlist is the entire
	// defence, not a second layer behind one. Three things HtmlSanitizer's flat, tag-agnostic model
	// does not give for free, so they are handled explicitly below:
	//   1. `href` is ALREADY a globally-allowed attribute (pre-existing, for markdown `<a>` links)
	//      and `AllowedAttributes` has no per-tag scoping — so once `<path>`/`<rect>`/`<use>`/etc.
	//      are allowed as TAGS, an author gets `href` on them for free, and https (needed by `<a>`)
	//      is an allowed SCHEME. Left alone, `<use href="https://evil.example/x.svg#y">` would
	//      survive: a same-scheme reference that is nonetheless an external fetch this feature must
	//      not permit. `FilterUrl` closes this by requiring an in-document `#fragment` for href/
	//      xlink:href specifically on the SVG tag set (`<a>` is untouched — it keeps http/https).
	//   2. `marker-end`/`fill`/`stroke` can carry a `url(...)` paint-server/marker reference as a
	//      plain string value — NOT a `UriAttributes` entry, so it is never scheme-checked at all.
	//      No gradient/pattern/filter element is on the allowlist, so the only legitimate value
	//      shape is a LOCAL `url(#id)`; `PostProcessNode` strips anything else (an external SVG
	//      resource reference has shipped real CVEs in this exact spot).
	//   3. Node bodies and comments render MANY at once on one board/thread page (the shared-
	//      renderer consequence this card states explicitly), and `id`/`href="#id"` are a
	//      DOCUMENT-global namespace in HTML — two diagrams that each define `id="arrow"` would
	//      collide, with the second silently owning both `<use>`/marker references. `PostProcessDom`
	//      suffixes every id DEFINED inside a rendered `<svg>` (and every local reference to it)
	//      with a fresh per-render token, so two independent renders never collide even when the
	//      author copy-pasted the identical diagram twice.
	//
	// Excluded, deliberately:
	//   - `<script>`, `<foreignObject>`, `<image>` — never added to AllowedTags; the sanitizer's
	//     default KeepChildNodes=false drops the whole disallowed element AND its children (a
	//     `<foreignObject>` cannot be used to smuggle arbitrary HTML/script past the allowlist).
	//   - every `on*` handler — never added to AllowedAttributes; stripped like any other body HTML.
	//   - `<style>` inside SVG — an inline `<style>` element is NOT scoped to its SVG subtree, it is
	//     a normal global stylesheet for the WHOLE PAGE the moment it lands in the DOM. With no CSP
	//     to fall back on, one comment's `<style>` could hide unrelated UI, restyle the page, or run
	//     a CSS-based data-exfiltration attack (attribute-selector timing/`background-image` probes)
	//     for zero diagram benefit — colour already comes through `currentColor` + presentation
	//     attributes. Not added; HtmlSanitizer's default behaviour (drop tag + contents) already
	//     does the right thing without any code here.
	//   - `<linearGradient>`/`<pattern>`/`<filter>`/`<mask>`/`<clipPath>` — the main legitimate
	//     reason `fill`/`stroke` would need a `url(...)` reference. Excluding the paint-server/
	//     filter elements themselves is what makes the local-only `url(#id)` check above sufficient
	//     — there is no allowed element for a malicious `url()` to legitimately point at, on- or
	//     off-document.
	//
	// `<use>` IS included: the FilterUrl fragment-only rule above is required regardless (`href`
	// leaks onto every newly-allowed tag whether or not `<use>` exists), so once that guard exists,
	// `<use xlink:href="#shape">` costs nothing extra and buys authors shape reuse (e.g. the
	// reference diagram's three visually distinct "no" glyphs) without repeating markup.
	// `internal` (+ PetBox.Web's InternalsVisibleTo PetBox.Tests) so the drift-guard test
	// (NodeAuthoringSkillSvgDriftTests) can pin the petbox-node-authoring skill's declared
	// allowlist — shipped to projects with no PetBox sources — against this ground truth.
	internal static readonly HashSet<string> SvgTags = new(StringComparer.OrdinalIgnoreCase)
	{
		"svg", "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
		"text", "tspan", "marker", "defs", "title", "desc", "use",
	};

	// Attribute names whose value may legitimately carry a `url(#id)` local reference. None of
	// these are `UriAttributes` (that mechanism is for href/src-shaped attributes), so their scheme
	// is never checked by the sanitizer's normal pipeline — PostProcessSvgAttributeUrls does it.
	static readonly string[] UrlFunctionAttributes = ["fill", "stroke", "marker-start", "marker-mid", "marker-end"];

	// A value that is EXACTLY a local reference, e.g. `url(#arrowhead)`. Anything else containing
	// `url(` — an external address, a `javascript:` payload, garbage — is rejected outright; a
	// value with no `url(` at all (currentColor, none, #3b82f6, ...) is never touched here.
	static readonly Regex LocalUrlFunctionRx = new(@"^url\(#([A-Za-z][\w:.-]*)\)$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	static void ConfigureSvgSubset(HtmlSanitizer s)
	{
		foreach (var tag in SvgTags) s.AllowedTags.Add(tag);

		// Geometry/structure, presentation, text and marker-linkage attributes the reference
		// diagram's shapes need. `width`/`height`/`href`/`style`/`title`/`class` are already
		// allowed above (existing, non-SVG-specific); `xmlns`/`xmlns:xlink`/`version` are skipped —
		// an <svg> parsed inline in an HTML document renders correctly without them.
		string[] svgAttributes =
		[
			"viewBox", "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry",
			"points", "d", "transform", "preserveAspectRatio",
			"fill", "stroke", "stroke-width", "stroke-dasharray", "stroke-linecap", "stroke-linejoin",
			"stroke-opacity", "fill-opacity", "opacity",
			"font-size", "font-family", "font-weight", "text-anchor", "dominant-baseline", "dx", "dy",
			"marker-start", "marker-mid", "marker-end", "refX", "refY",
			"markerWidth", "markerHeight", "markerUnits", "orient",
			"id", "xlink:href", "role",
		];
		foreach (var attr in svgAttributes) s.AllowedAttributes.Add(attr);
		// `href` is already allowed (for `<a>`); `xlink:href` needs the same URI treatment so its
		// scheme is checked at all (an attribute not in UriAttributes is never scheme-filtered).
		s.UriAttributes.Add("xlink:href");

		// Point (1): href/xlink:href on an SVG element may only address a fragment IN THIS
		// DOCUMENT. `<a>` is not in SvgTags and is untouched — it keeps the http/https/mailto rule
		// the rest of a markdown body relies on.
		s.FilterUrl += (_, e) =>
		{
			if (e.Tag is not { } tag || !SvgTags.Contains(tag.NodeName)) return;
			if (e.SanitizedUrl is null || !e.SanitizedUrl.StartsWith('#'))
				e.SanitizedUrl = null;
		};

		// Point (2): fill/stroke/marker-* may only reference a LOCAL id via url(#id) — anything
		// scheme-shaped is rejected outright — and (scoping) `id` only ever survives on an SVG
		// element; the attribute is allowed globally above only so it can exist there at all.
		s.PostProcessNode += (_, e) =>
		{
			if (e.Node is not IElement el) return;
			if (!SvgTags.Contains(el.NodeName))
			{
				if (el.HasAttribute("id")) el.RemoveAttribute("id");
				return;
			}
			foreach (var attr in UrlFunctionAttributes)
			{
				var value = el.GetAttribute(attr);
				if (value is null || !value.Contains("url(", StringComparison.OrdinalIgnoreCase)) continue;
				if (!LocalUrlFunctionRx.IsMatch(value.Trim())) el.RemoveAttribute(attr);
			}
		};

		// Point (3): namespace every id DEFINED inside each rendered <svg> subtree (and every local
		// reference to it — href="#id", xlink:href="#id", url(#id)) with a token unique to THIS
		// render, so two diagrams sharing an id on the same board/thread page never collide.
		// PostProcessDom fires once per Sanitize() call, after every node has already been
		// validated — the right place to do a whole-subtree rewrite.
		s.PostProcessDom += (_, e) =>
		{
			foreach (var svg in e.Document.QuerySelectorAll("svg"))
				NamespaceSvgIds((IElement)svg);
		};
	}

	static void NamespaceSvgIds(IElement svgRoot)
	{
		var scoped = new[] { svgRoot }.Concat(svgRoot.QuerySelectorAll("*")).ToList();
		var definedIds = new HashSet<string>(StringComparer.Ordinal);
		foreach (var el in scoped)
		{
			var id = el.GetAttribute("id");
			if (!string.IsNullOrEmpty(id)) definedIds.Add(id);
		}
		if (definedIds.Count == 0) return; // no ids to collide over — nothing to rewrite

		// Content-derived, not random: the suffix must satisfy "different things must differ", not
		// "everything must be unique". Hashing the SVG's own serialized markup (its state BEFORE
		// this rewrite — hashing anything post-rewrite would fold the suffix into its own input)
		// means two renders of the *same* diagram get the *same* suffix (a harmless collision — the
		// cross-reference lands on an identical shape) while two *different* diagrams still diverge,
		// restoring byte-identical output for repeated renders of unchanged input (the property the
		// editor-preview-vs-saved-body comparison relies on). SHA256 is used only for its low
		// collision rate at this length, not for any cryptographic property; 12 hex chars (48 bits)
		// keeps a same-page birthday collision between genuinely different diagrams negligible.
		var digest = SHA256.HashData(Encoding.UTF8.GetBytes(svgRoot.OuterHtml));
		var suffix = "-" + Convert.ToHexStringLower(digest)[..12];

		foreach (var el in scoped)
		{
			var id = el.GetAttribute("id");
			if (!string.IsNullOrEmpty(id)) el.SetAttribute("id", id + suffix);

			// `xlink:href` is NOT addressable by that qualified-name string through
			// Get/SetAttribute: AngleSharp reports BOTH the real xlink-namespaced attribute and the
			// plain-`href` compatibility mirror HtmlSanitizer creates for it (SVG2's href/xlink:href
			// duality) as `Name == "href"` — `SetAttribute("xlink:href", …)` silently creates a
			// THIRD, unrelated attribute instead of updating the real one (measured, not assumed —
			// see the sanitizer-behaviour probe in this card's session). Mutating each attribute
			// NODE's `.Value` in place, found by LocalName rather than by qualified-name string,
			// updates both the real attribute and its mirror correctly with no duplicate.
			foreach (var attr in el.Attributes.Where(a => a.LocalName == "href").ToList())
			{
				var href = attr.Value;
				if (href is { Length: > 1 } && href[0] == '#' && definedIds.Contains(href[1..]))
					attr.Value = href + suffix;
			}
			foreach (var urlAttr in UrlFunctionAttributes)
			{
				var value = el.GetAttribute(urlAttr);
				if (value is null) continue;
				var m = LocalUrlFunctionRx.Match(value.Trim());
				if (m.Success && definedIds.Contains(m.Groups[1].Value))
					el.SetAttribute(urlAttr, $"url(#{m.Groups[1].Value}{suffix})");
			}
		}
	}
}

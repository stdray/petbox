using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace PetBox.Web.Rendering;

// The structural half of the body design layer (work `node-render-design-layer`). Two wrappers
// that CSS cannot produce on its own, both derived from the AST — never from a regex over rendered
// HTML, and never from the author's text:
//
//   1. SECTION CONTAINER. Everything from a `##` heading up to the next `##`/`#` becomes one
//      <section class="md-section"> — a visible bordered surface per section. The author already
//      writes `##`; no body changes, no new syntax. Doing this on the AST (rather than by
//      string-splitting the rendered HTML) is what makes a `## ` line inside a fenced code block
//      stay code: Markdig has already decided what is a heading by the time this runs. Several
//      repo doc pages DO carry `# ` shell comments inside fences — a regex would have sectioned
//      on them.
//
//   2. TABLE SCROLLER. Every table is wrapped in <div class="md-table-scroll">, which owns the
//      horizontal overflow. A wide table previously widened its whole container (tables had no
//      overflow-x of their own anywhere in the app).
//
//   3. CODE FOLD. A code block LONGER than FoldLineThreshold lines is wrapped in
//      <div class="md-code-fold"> together with a <details> disclosure control, and CSS caps the
//      <pre> until that control is opened (work `md-code-block-height-cap`). The DECISION is made
//      here, on the AST, because it is a decision about the SOURCE — how many lines the author
//      wrote — and CSS cannot count lines: a `max-height` alone would cap every block, handing a
//      two-line snippet the same clipped box and the same control as a 200-line listing. So the
//      server marks WHICH blocks are long and how long they are, and CSS does the folding. A short
//      block is not wrapped at all and its markup is byte-identical to what it was before.
//
// Both wrappers are emitted with a class, and the sanitizer strips `class` by default — see
// MarkdownRenderer.BuildSanitizer, which allowlists exactly these class names. Emitting them
// without that allowlist entry renders the wrappers invisible (the elements survive, bare and
// unstyleable), which is the quiet way this feature would half-work.
public sealed class MarkdownDesignLayerExtension : IMarkdownExtension
{
	// The `##`-delimited section container. A plain ContainerBlock: it holds the heading plus the
	// blocks that follow it, and carries no parser (it is never parsed — it is assembled after the
	// document is already built).
	public sealed class SectionBlock() : ContainerBlock(null);

	// The per-table horizontal scroller. Wraps exactly one Table.
	public sealed class TableScrollBlock() : ContainerBlock(null);

	// The fold wrapper around ONE long code block. Carries the source line count so the disclosure
	// control can name it ("Show all 42 lines") — a control that only says "expand" makes the
	// reader click to find out whether it is worth clicking.
	public sealed class CodeFoldBlock() : ContainerBlock(null)
	{
		public int CodeLines { get; init; }
	}

	// A code block longer than this many SOURCE lines gets the fold. The number is a line count and
	// not a height because it is a property of the text, not of the viewport: the same body renders
	// on a node page, a board card and an anonymous share page, and "10 lines" means the same thing
	// on all three. The matching VISUAL cap (app.css `.md-code-fold > pre`) is exactly this many
	// code line-boxes plus the block's own padding, so the collapsed box shows precisely the lines
	// the threshold talks about. Strictly greater-than: a block of exactly 10 lines is short.
	internal const int FoldLineThreshold = 10;

	public void Setup(MarkdownPipelineBuilder pipeline) => pipeline.DocumentProcessed += Restructure;

	// NOTE the fully-qualified renderer type: this namespace declares its OWN IMarkdownRenderer
	// (PetBox.Web.Rendering.IMarkdownRenderer, the app-facing service), which shadows Markdig's
	// same-named interface and silently turns this into a non-implementing method.
	public void Setup(MarkdownPipeline pipeline, Markdig.Renderers.IMarkdownRenderer renderer)
	{
		if (renderer is not HtmlRenderer html) return;
		// Prepend: an ObjectRenderer list is probed in order and the first renderer that Accepts the
		// block wins. SectionBlock/TableScrollBlock are ContainerBlocks, so a more general
		// container renderer registered earlier could otherwise claim them.
		if (!html.ObjectRenderers.Contains<SectionBlockRenderer>())
			html.ObjectRenderers.Insert(0, new SectionBlockRenderer());
		if (!html.ObjectRenderers.Contains<TableScrollBlockRenderer>())
			html.ObjectRenderers.Insert(0, new TableScrollBlockRenderer());
		if (!html.ObjectRenderers.Contains<CodeFoldBlockRenderer>())
			html.ObjectRenderers.Insert(0, new CodeFoldBlockRenderer());
	}

	// Runs on every parse (MarkdownPipelineBuilder.DocumentProcessed), so BOTH render paths in
	// MarkdownRenderer get it: the plain Markdown.ToHtml fast path and the Markdown.Parse +
	// Linkify path. Linkify walks Descendants<LiteralInline>(), which recurses through any
	// ContainerBlock, so the new wrappers are transparent to it.
	static void Restructure(MarkdownDocument document)
	{
		Sectionize(document);
		WrapTables(document);
		FoldLongCodeBlocks(document);
	}

	// Group top-level blocks into one SectionBlock per `##`. Content before the first `##` (a lead
	// paragraph, an `#` title) stays at document level — a section is opened by a `##` and closed
	// by the next `##` or by any `#`, so an `#` returns the document to the top level.
	static void Sectionize(MarkdownDocument document)
	{
		var blocks = new List<Block>(document);
		if (!blocks.Exists(b => b is HeadingBlock { Level: 2 })) return;

		document.Clear();
		SectionBlock? current = null;
		foreach (var block in blocks)
		{
			if (block is HeadingBlock { Level: <= 2 } heading)
			{
				current = null;
				if (heading.Level == 2)
				{
					current = new SectionBlock();
					document.Add(current);
				}
			}

			if (current is null) document.Add(block);
			else current.Add(block);
		}
	}

	// Wrap every Table — at any depth, so a table inside a section, a blockquote or a list item is
	// covered too — in its own scroller. Table is itself a ContainerBlock; recursing INTO it would
	// only find rows and cells, so it is not descended.
	static void WrapTables(ContainerBlock container)
	{
		for (var i = 0; i < container.Count; i++)
			switch (container[i])
			{
				case Table table:
					var scroll = new TableScrollBlock();
					container.RemoveAt(i);
					container.Insert(i, scroll);
					scroll.Add(table);
					break;
				case ContainerBlock inner:
					WrapTables(inner);
					break;
			}
	}

	// Wrap every code block longer than the threshold — at any depth, so a listing inside a section,
	// a list item or a blockquote is covered too. Both markdown code forms are CodeBlock subtypes
	// (FencedCodeBlock for ```fences```, CodeBlock itself for the four-space indented form), so
	// matching the base type covers both; `Lines` is the block's CONTENT lines, fences excluded
	// (measured — see this card's probe), which is exactly what a reader sees in the box.
	//
	// CodeBlock is a LeafBlock, so the recursion below can never descend INTO one: a ``` fence
	// drawn inside a fenced block is text, not a block, and cannot be folded twice.
	static void FoldLongCodeBlocks(ContainerBlock container)
	{
		for (var i = 0; i < container.Count; i++)
			switch (container[i])
			{
				case CodeBlock code when code.Lines.Count > FoldLineThreshold:
					var fold = new CodeFoldBlock { CodeLines = code.Lines.Count };
					container.RemoveAt(i);
					container.Insert(i, fold);
					fold.Add(code);
					break;
				case ContainerBlock inner:
					FoldLongCodeBlocks(inner);
					break;
			}
	}

	sealed class SectionBlockRenderer : HtmlObjectRenderer<SectionBlock>
	{
		protected override void Write(HtmlRenderer renderer, SectionBlock obj)
		{
			renderer.EnsureLine();
			renderer.WriteLine("<section class=\"md-section\">");
			renderer.WriteChildren(obj);
			renderer.WriteLine("</section>");
		}
	}

	sealed class TableScrollBlockRenderer : HtmlObjectRenderer<TableScrollBlock>
	{
		protected override void Write(HtmlRenderer renderer, TableScrollBlock obj)
		{
			renderer.EnsureLine();
			renderer.WriteLine("<div class=\"md-table-scroll\">");
			renderer.WriteChildren(obj);
			renderer.WriteLine("</div>");
		}
	}

	// The disclosure control is a NATIVE <details>, and it is emitted AFTER the <pre> rather than
	// around it, for three reasons that all come from where this markup has to work:
	//
	//   * NO SCRIPT. The anonymous share page (Pages/ShareNode.cshtml on _PublicLayout) ships a
	//     stylesheet and no JS bundle at all, so anything script-driven would simply not expand
	//     there. <details> toggles itself in the browser; CSS reads the resulting `open` attribute
	//     (app.css `.md-code-fold:has(> .md-code-fold-toggle[open])`) and lifts the cap.
	//   * THE CODE IS NOT INSIDE THE CONTROL. Putting the <pre> inside <summary> would also have
	//     worked with pure CSS, but then every click on the code toggles the fold — selecting a
	//     line would fight the widget. Here the <details> has no content of its own; it is a
	//     labelled switch that a sibling selector reads.
	//   * REAL MARKUP, IN READING ORDER. The <pre> stays a plain, complete <pre> in document order
	//     with the whole listing in it — reader-view, "select all", copy/paste and text search get
	//     the full block whether or not it is expanded (the cap is presentational only). Nothing is
	//     truncated in the HTML; only its rendered height is.
	sealed class CodeFoldBlockRenderer : HtmlObjectRenderer<CodeFoldBlock>
	{
		protected override void Write(HtmlRenderer renderer, CodeFoldBlock obj)
		{
			renderer.EnsureLine();
			renderer.WriteLine("<div class=\"md-code-fold\">");
			renderer.WriteChildren(obj);
			renderer.WriteLine("<details class=\"md-code-fold-toggle\"><summary>"
				+ $"<span class=\"md-code-fold-more\">Show all {obj.CodeLines} lines</span>"
				+ "<span class=\"md-code-fold-less\">Show less</span></summary></details>");
			renderer.WriteLine("</div>");
		}
	}
}

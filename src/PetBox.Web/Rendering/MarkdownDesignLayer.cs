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
	}

	// Runs on every parse (MarkdownPipelineBuilder.DocumentProcessed), so BOTH render paths in
	// MarkdownRenderer get it: the plain Markdown.ToHtml fast path and the Markdown.Parse +
	// Linkify path. Linkify walks Descendants<LiteralInline>(), which recurses through any
	// ContainerBlock, so the new wrappers are transparent to it.
	static void Restructure(MarkdownDocument document)
	{
		Sectionize(document);
		WrapTables(document);
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
}

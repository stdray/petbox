using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using PetBox.Web.Rendering;

namespace PetBox.Tests.Web;

// Syntax highlighting for fenced code blocks (work `md-code-syntax-highlighting`). Blocks are
// tokenized on the SERVER — TextMateSharp running VS Code's own TextMate grammars — and coloured
// through three CSS classes. The public share page ships no application JS at all, so there is no
// second chance in the browser: whatever these tests do not hold down is not held down anywhere.
//
// Three properties carry the weight, and only one of them is about colour.
//
//  1. THE CLASSES REACH THE BROWSER. The sanitizer's AllowedClasses is an explicit allowlist and it
//     deletes anything absent from it, silently. Markdig's own `language-csharp` has been emitted
//     since day one and has never once survived it. A highlighter whose classes are not allowlisted
//     still emits perfectly good spans, still passes every renderer-level assertion, and still
//     renders a uniformly grey page — so every assertion below runs through the FULL public entry
//     point (RenderToHtml, sanitizer included), never against the highlighter in isolation.
//  2. NOTHING IS LOST. An unknown language, no language at all, an oversized block or a grammar
//     fault must degrade to an ordinary unhighlighted block — never to an empty one, and never to
//     one missing a character.
//  3. IT COMPOSES WITH THE FOLD. Folding wraps the <pre>; highlighting rewrites the inside of the
//     <code>. They are decided at different moments (the fold on the AST from SOURCE line counts,
//     highlighting at render time) and a long block must come out both folded AND highlighted.
public sealed class MarkdownCodeHighlightingTests
{
	static readonly IMarkdownRenderer R = new MarkdownRenderer();

	static string Html(string md) => R.RenderToHtml(md);

	static readonly Regex TagRx = new("<[^>]*>", RegexOptions.Compiled);
	static readonly Regex ClassRx = new("class=\"([^\"]*)\"", RegexOptions.Compiled);

	// The text a reader actually sees inside the rendered block: markup removed, entities decoded.
	// This is the thing that must equal the author's source no matter what the tokenizer did.
	static string VisibleText(string html)
	{
		var start = html.IndexOf("<code", StringComparison.Ordinal);
		var open = html.IndexOf('>', start) + 1;
		var end = html.IndexOf("</code>", StringComparison.Ordinal);
		return WebUtility.HtmlDecode(TagRx.Replace(html[open..end], ""));
	}

	static string CodeInnerHtml(string html)
	{
		var start = html.IndexOf("<code", StringComparison.Ordinal);
		var open = html.IndexOf('>', start) + 1;
		return html[open..html.IndexOf("</code>", StringComparison.Ordinal)];
	}

	// ── 1. the classes survive the sanitizer ────────────────────────────────────────────────────

	[Fact]
	public void HighlightedBlock_CarriesTheMarkerClass_ThroughTheSanitizer()
	{
		Html("```bash\n# note\necho 'hi'\n```")
			.Should().Contain($"<code class=\"{MarkdownCodeHighlighter.HighlightedCodeClass}\">",
				"the marker class is how app.css scopes the token colours — if the sanitizer eats "
				+ "it, every token renders in ordinary body colour and the feature is invisible");
	}

	[Fact]
	public void AllThreeTokenClasses_SurviveTheSanitizer()
	{
		// One block that provably produces all three roles: a comment, a string and a keyword.
		var html = Html("```bash\n# note\nif [[ -n \"$V\" ]]; then echo 'hi'; fi\n```");
		html.Should().Contain($"<span class=\"{MarkdownCodeHighlighter.CommentClass}\">");
		html.Should().Contain($"<span class=\"{MarkdownCodeHighlighter.StringClass}\">");
		html.Should().Contain($"<span class=\"{MarkdownCodeHighlighter.KeywordClass}\">");
	}

	[Fact]
	public void NoClassEscapesTheDeclaredSet()
	{
		// The drift guard. A class the highlighter emits but the allowlist does not carry is
		// deleted silently (failure mode 1 above). A class that reaches the browser without being
		// in the declared set means the emitted vocabulary grew behind the allowlist's back —
		// which is exactly how an open-ended TextMate scope name would leak in.
		var languages = new[]
		{
			"bash", "csharp", "json", "yaml", "ini", "typescript", "python",
			"sql", "xml", "powershell", "go", "rust", "diff", "dockerfile",
		};
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var lang in languages)
		{
			var html = Html($"```{lang}\n# c\nx = \"s\"\nif true then end\n```");
			foreach (Match m in ClassRx.Matches(CodeInnerHtml(html)))
				foreach (var c in m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
					seen.Add(c);
		}
		seen.Should().NotBeEmpty();
		seen.Should().BeSubsetOf(MarkdownCodeHighlighter.EmittedClasses,
			"MarkdownCodeHighlighter.EmittedClasses is precisely what MarkdownRenderer allowlists");
	}

	[Fact]
	public void MarkdigsOwnLanguageClass_StillNeverReachesTheBrowser()
	{
		// Not a new claim — a pinned one. `language-*` is emitted by Markdig and stripped by the
		// allowlist today; it is the standing proof that this allowlist is load-bearing, and the
		// reason the highlighted path carries its own allowlisted marker instead.
		Html("```brainfuck\n+++\n```").Should().NotContain("language-");
		Html("```csharp\nvar x = 1;\n```").Should().NotContain("language-");
	}

	// ── 2. unknown / absent languages degrade, losing nothing ───────────────────────────────────

	[Fact]
	public void UnknownLanguage_RendersAnOrdinaryPlainBlock()
	{
		// Not "contains" but the WHOLE output: an unknown language must be byte-identical to what
		// this renderer produced before the feature existed.
		Html("```brainfuck\n+++[->+<]\n```")
			.Should().Be("<pre><code>+++[-&gt;+&lt;]\n</code></pre>\n");
	}

	[Fact]
	public void NoLanguage_RendersAnOrdinaryPlainBlock_AndIsNeverGuessed()
	{
		// No detection, by decision: a fence with no info string stays plain even when its content
		// is unmistakably shell. Guessing wrong is worse than not colouring.
		Html("```\n#!/usr/bin/env bash\necho 'hi'\n```")
			.Should().Be("<pre><code>#!/usr/bin/env bash\necho 'hi'\n</code></pre>\n");
	}

	[Fact]
	public void IndentedCodeBlock_IsUntouched()
	{
		// The four-space form carries no info string and cannot name a language at all.
		Html("    plain text\n").Should().Be("<pre><code>plain text\n</code></pre>\n");
	}

	[Theory]
	[InlineData("bash", "#!/usr/bin/env bash\nX=\"a<b & c>d\"\ncat <<'EOF'\n[Interface]\nKey = $NOT\nEOF\necho \"$(date -u) 'q'\"")]
	[InlineData("csharp", "var s = \"a<b&c>\";\n// comment & more\nif (x < 3 && y > 1) { }")]
	[InlineData("json", "{\"a\": \"<b>&amp;\", \"n\": [1, true, null]}")]
	[InlineData("yaml", "# c\nkey: value\nlist:\n  - \"quoted <x>\"")]
	[InlineData("brainfuck", "+++[->+<]  <&>\"'")]
	[InlineData("", "raw <text> & 'quotes' \"here\"")]
	public void EveryCharacterOfTheSource_SurvivesRendering(string lang, string code)
	{
		// The single most important property here. Tokens are treated as a colouring HINT over the
		// line, never as the source of the text: gaps the tokenizer skipped, out-of-range indices
		// and a tokenizer that stopped early on its time budget are all still written out. A
		// highlighter that can eat a character is worse than none, because the reader cannot tell.
		VisibleText(Html($"```{lang}\n{code}\n```")).Should().Be(code + "\n");
	}

	[Fact]
	public void OversizedBlock_FallsBackToPlain_WithEveryCharacterIntact()
	{
		// Past the size guard a block is rendered plain rather than tokenized, and must still be a
		// complete, ordinary code block.
		var line = new string('x', 200);
		var count = MarkdownCodeHighlighter.MaxHighlightChars / line.Length + 10;
		var code = string.Join("\n", Enumerable.Repeat(line, count));
		var html = Html($"```bash\n{code}\n```");
		html.Should().NotContain(MarkdownCodeHighlighter.HighlightedCodeClass);
		VisibleText(html).Should().Be(code + "\n");
	}

	// ── 3. composition with the long-block fold ─────────────────────────────────────────────────

	[Fact]
	public void LongBlock_IsBothFoldedAndHighlighted()
	{
		// The two features touch different things — the fold wraps the <pre>, highlighting rewrites
		// the inside of the <code> — and this is the test that says so out loud.
		var body = string.Join("\n", Enumerable.Range(1, 25).Select(i => $"echo 'line{i}'   # note {i}"));
		var html = Html($"```bash\n{body}\n```");

		html.Should().Contain("<div class=\"md-code-fold\">");
		html.Should().Contain("<details class=\"md-code-fold-toggle\">");
		html.Should().Contain("Show all 25 lines");
		html.Should().Contain($"<code class=\"{MarkdownCodeHighlighter.HighlightedCodeClass}\">");
		html.Should().Contain($"<span class=\"{MarkdownCodeHighlighter.CommentClass}\">");
		html.Should().Contain($"<span class=\"{MarkdownCodeHighlighter.StringClass}\">");

		// And nothing was truncated: the fold is presentational, the <pre> still holds every line.
		var visible = VisibleText(html);
		for (var i = 1; i <= 25; i++) visible.Should().Contain($"echo 'line{i}'   # note {i}");
	}

	[Theory]
	[InlineData(10, false)] // the fold threshold is strictly greater-than
	[InlineData(11, true)]
	public void HighlightingDoesNotDisturbTheFoldLineCount(int lines, bool folded)
	{
		// The fold counts SOURCE lines on the AST, before any HTML exists, so highlighting cannot
		// move the boundary. Spans wrap tokens WITHIN a line and add no line of their own — if that
		// ever changed, this pair of cases would separate.
		var body = string.Join("\n", Enumerable.Range(1, lines).Select(i => $"echo 'l{i}'"));
		var html = Html($"```bash\n{body}\n```");
		html.Should().Contain(MarkdownCodeHighlighter.HighlightedCodeClass);
		if (folded) html.Should().Contain("md-code-fold").And.Contain($"Show all {lines} lines");
		else html.Should().NotContain("md-code-fold");
	}

	[Fact]
	public void AHighlightedLine_StaysOneLineBox()
	{
		// app.css caps a folded block at `calc(10lh + 1.2em)` — ten line boxes of the <pre>. That
		// arithmetic holds only while a highlighted line occupies exactly one line box, so the
		// token spans must be plain inline <span>s: no <br>, no block-level element, and exactly as
		// many rendered lines as source lines.
		var inner = CodeInnerHtml(Html("```bash\n# c\nif [[ -n \"$V\" ]]; then echo 'hi'; fi\n```"));
		inner.Should().NotContain("<br");
		inner.Should().NotContain("<div");
		inner.Should().NotContain("<p>");
		inner.Split('\n').Length.Should().Be(3); // two source lines + the trailing newline
	}

	// ── the palette: three roles, and the two scope rules that make it honest ───────────────────

	[Fact]
	public void HeredocBodyReadsAsALiteral_ButCommandSubstitutionDoesNot()
	{
		// Both come back from the shell grammar under a `string.*` scope, and treating them alike
		// is wrong in opposite directions. A heredoc body IS literal text. The contents of `$(...)`
		// are commands — painting a whole substitution as a string is the most visible way this
		// feature could look broken on the body that motivated it (which has 15 of them).
		var html = Html("```bash\ncat <<'EOF'\n[Interface]\nEOF\nCOUNT=$(docker ps -q | wc -l)\n```");
		html.Should().Contain($"<span class=\"{MarkdownCodeHighlighter.StringClass}\">[Interface]</span>");
		html.Should().NotContain($"<span class=\"{MarkdownCodeHighlighter.StringClass}\">docker ps -q ");
	}

	[Fact]
	public void YamlPlainScalars_AreNotPaintedAsStrings()
	{
		// YAML scopes every unquoted scalar AND every key as `string.unquoted.plain.out.yaml`.
		// Taken at face value that paints an entire config block in the string colour, which is
		// indistinguishable from "the highlighter is broken".
		var html = Html("```yaml\nkey: value\nquoted: \"v\"\n```");
		html.Should().NotContain($"<span class=\"{MarkdownCodeHighlighter.StringClass}\">value</span>");
		html.Should().Contain(MarkdownCodeHighlighter.StringClass); // the QUOTED one still is one
	}

	[Fact]
	public void CommonFenceAliases_AllResolve()
	{
		// The fence tags people actually type. `bash` is the tag that decided the library choice,
		// and the grammar set's own GetScopeByLanguageId("bash") returns null — the canonical id is
		// `shellscript`, so the aliases have to be walked explicitly.
		foreach (var alias in new[]
		{
			"bash", "sh", "zsh", "shell", "csharp", "cs", "c#", "js", "ts", "py", "python",
			"json", "yaml", "yml", "xml", "html", "css", "sql", "ps1", "powershell", "pwsh",
			"go", "golang", "rust", "rs", "java", "ruby", "rb", "php", "diff", "patch",
			"dockerfile", "makefile", "make", "ini", "properties", "md", "markdown", "log",
		})
			MarkdownCodeHighlighter.ResolveScope(alias).Should().NotBeNull($"`{alias}` is a tag people write");

		// The spelling variants added on top of the grammar set's own alias table land on exactly
		// the same grammar as the canonical spelling — that is the whole claim being made for them.
		MarkdownCodeHighlighter.ResolveScope("shell").Should().Be(MarkdownCodeHighlighter.ResolveScope("bash"));
		MarkdownCodeHighlighter.ResolveScope("yml").Should().Be(MarkdownCodeHighlighter.ResolveScope("yaml"));
		MarkdownCodeHighlighter.ResolveScope("cs").Should().Be(MarkdownCodeHighlighter.ResolveScope("csharp"));

		// Command OUTPUT is not a script; `conf`/`text` name no particular language. Not guessed.
		foreach (var plain in new[] { "console", "terminal", "conf", "text", "txt", "plaintext" })
			MarkdownCodeHighlighter.ResolveScope(plain).Should().BeNull($"`{plain}` names no grammar honestly");

		// Genuinely absent from this grammar set — these render plain, and that is the honest
		// coverage boundary rather than a bug.
		foreach (var absent in new[] { "toml", "kotlin", "haskell", "elixir", "scala", "graphql", "hcl" })
			MarkdownCodeHighlighter.ResolveScope(absent).Should().BeNull($"`{absent}` has no grammar here");

		MarkdownCodeHighlighter.ResolveScope("brainfuck").Should().BeNull();
		MarkdownCodeHighlighter.ResolveScope("").Should().BeNull();
		MarkdownCodeHighlighter.ResolveScope(null).Should().BeNull();
	}

	[Fact]
	public void RenderingIsThreadSafeAndDeterministic()
	{
		// The renderer is a SINGLETON serving concurrent requests, while TextMateSharp's Registry
		// is not thread-safe on load — 200 parallel LoadGrammar calls were measured producing
		// "An item with the same key has already been added" — and grammars resolve embedded
		// sub-grammars lazily DURING tokenization. The highlighter serializes both behind one lock;
		// this is the test that would catch that guard being removed.
		var md = "```bash\n# c\nNAME=\"x-$1\"\ncat <<'EOF'\nbody\nEOF\n```\n\n```csharp\nvar x = \"s\"; // c\n```";
		var expected = Html(md);
		var results = new ConcurrentBag<string>();
		Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 16 },
			_ => results.Add(new MarkdownRenderer().RenderToHtml(md)));
		results.Distinct().Should().ContainSingle().Which.Should().Be(expected);
	}
}

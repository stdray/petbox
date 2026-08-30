using System.Net;
using System.Text;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace PetBox.Web.Rendering;

// Server-side syntax highlighting for fenced code blocks (work `md-code-syntax-highlighting`).
//
// WHY SERVER-SIDE. The anonymous share page (Pages/ShareNode.cshtml on _PublicLayout) ships a
// stylesheet and NO application JS at all, and an E2E test pins that (`appScripts == 0`). A
// client-side highlighter (highlight.js/Prism) would have to break that property, so the owner
// rejected it. Shiki was rejected too — it needs Node in the render path, and bodies are rendered
// per request and are editable, so there is nothing to precompute.
//
// WHY TEXTMATE. ColorCode-Universal was the first choice and was measured, not assumed: it ships
// exactly 25 hardcoded grammars and NONE of them is a shell (`bash`/`sh`/`shell`/`zsh` all resolve
// to nothing), while the body that motivated this feature carries 20 shell blocks, 13 config
// blocks, 4 heredocs and 15 `$(...)` substitutions. A hand-written ColorCode grammar was spiked and
// worked for the easy cases, but it is regex-per-line by construction and heredocs and `$(...)`
// are exactly where that approach stops being honest. TextMateSharp runs VS Code's OWN grammars
// (`shell-unix-bash.tmLanguage.json` among 64) as a pure .NET library — the same grammar family
// Shiki uses, with none of the Node that got Shiki rejected.
//
// WHAT THIS DELIBERATELY DOES NOT DO:
//   * NO THEME. TextMateSharp can resolve scopes to a VS Code theme's colours; we never call that.
//     Themes emit concrete hex colours, and this app has FOUR daisyUI themes and switches between
//     light and dark — a baked colour cannot follow that, and the sanitizer's CSS-property filter
//     would drop the inline `style` anyway. Scopes are mapped to three CLASSES here, and app.css
//     owns the colours through theme-derived tokens.
//   * NO LANGUAGE DETECTION. A fence with no info string stays a plain, unhighlighted block. The
//     owner asked for this explicitly: guessing is worse than not colouring.
public static class MarkdownCodeHighlighter
{
	// The THREE token roles, and the entire set of class names this emits. A TextMate scope is a
	// dotted hierarchy and grammars mint hundreds of them (`string.quoted.double.shell`,
	// `entity.name.function.shell`, …), which is precisely why the scopes are NOT the classes: the
	// sanitizer's AllowedClasses is an explicit allowlist (see MarkdownRenderer.BuildSanitizer),
	// and an open-ended scope vocabulary cannot be allowlisted. Collapsing scopes to a fixed role
	// set makes the class list finite BY CONSTRUCTION — it stays three names no matter how many
	// grammars or scopes a future TextMateSharp ships.
	//
	// Three is also the owner's palette budget, not an accident of implementation: muted comments,
	// strings in one colour, one accent for keywords, and everything else in ordinary body text.
	// The instruction was "not a Christmas tree", and a scope-per-colour theme is exactly that.
	public const string CommentClass = "hl-comment";
	public const string StringClass = "hl-string";
	public const string KeywordClass = "hl-keyword";

	// Marks a <code> whose content this class produced. Not decorative: it is how app.css scopes
	// the token colours (so a `hl-string` written by hand in a body's raw HTML colours nothing),
	// and how a test can tell "highlighted" from "rendered plain".
	public const string HighlightedCodeClass = "md-hl";

	// Every class name this file can emit — the exact list MarkdownRenderer allowlists.
	public static readonly string[] EmittedClasses =
		[HighlightedCodeClass, CommentClass, StringClass, KeywordClass];

	// Scope → role, FIRST MATCH WINS, tested against each scope from the INNERMOST outwards.
	// A null role means TRANSPARENT: this scope carries no opinion, keep walking outward. That
	// third outcome is not decoration — three entries here exist only because of it, and each was
	// put there by reading real tokenizer output, not by guessing:
	//
	//   `string.unquoted.heredoc` → String, FIRST. A heredoc body is literal text and reads as one,
	//      and it must be decided before the `string.unquoted` rule below strips it.
	//   `string.interpolated`     → transparent. Bash scopes a `$(...)` substitution as
	//      `string.interpolated.dollar.shell`; its CONTENTS are commands, not a literal. Without
	//      this, `COUNT=$(docker ps -q | wc -l)` paints entirely as a string — and that body has 15
	//      of these.
	//   `string.unquoted`         → transparent. YAML scopes every plain scalar AND every key as
	//      `string.unquoted.plain.out.yaml`. Without this, a whole YAML block is one colour.
	//   `keyword.operator`        → transparent. Keeps `|`, `;`, `:-` out of the accent (they are
	//      punctuation, not keywords) while letting an operator INSIDE a string inherit the string
	//      colour from the scope outside it, instead of speckling.
	static readonly (string Prefix, string? Role)[] ScopeRules =
	[
		("string.unquoted.heredoc", StringClass),
		("string.interpolated", null),
		("string.unquoted", null),
		("keyword.operator", null),
		("comment", CommentClass),
		("string", StringClass),
		("keyword", KeywordClass),
		("storage", KeywordClass),
	];

	// TextMateSharp's Registry is NOT thread-safe on load — measured, not assumed: 200 parallel
	// LoadGrammar calls across 8 scopes produced 16 `ArgumentException: An item with the same key
	// has already been added`. Tokenizing an ALREADY-LOADED grammar concurrently IS safe (400
	// parallel runs over a shared grammar produced one single distinct result). Grammars also
	// resolve embedded sub-grammars lazily DURING tokenization, which would race a concurrent load.
	//
	// So: one lock, held across resolve-and-tokenize. The renderer is a singleton serving
	// concurrent requests, so this is a real hazard, not a theoretical one, and the cost is
	// bounded and measured — see the perf note on Highlight below.
	static readonly object Gate = new();
	static readonly Dictionary<string, IGrammar?> GrammarCache = new(StringComparer.Ordinal);
	static RegistryOptions? _options;
	static Registry? _registry;

	// Fence info string → TextMate scope name. Built once, case-insensitively, from the grammar
	// set's OWN language table, so `bash`, `sh`, `zsh`, `ksh` and `csh` all reach `source.shell`
	// without a hand-maintained alias list here. (RegistryOptions.GetScopeByLanguageId matches the
	// canonical Id ONLY — `GetScopeByLanguageId("bash")` returns null — so the aliases have to be
	// walked explicitly.) Note the table legitimately contains duplicate ids (`diff` appears
	// twice), hence indexer assignment rather than Add.
	static Dictionary<string, string>? _scopeByFenceInfo;

	// A block bigger than this is rendered plain. Tokenizing is regex work over untrusted body
	// text, and a body can be arbitrarily long; the largest real block measured on the motivating
	// page is 8.7 KB, so this is ~11x headroom and not a limit anyone will meet by accident.
	internal const int MaxHighlightChars = 100_000;

	// Per-LINE tokenizer budget. TextMateSharp takes this natively and degrades by returning
	// FEWER tokens rather than by throwing (measured with a 1-tick budget: 2 tokens, no exception),
	// and the writer below emits any span the tokenizer did not cover as plain text — so a timeout
	// costs colour, never characters.
	static readonly TimeSpan LineBudget = TimeSpan.FromMilliseconds(50);

	// Resolve a fence info string to a grammar scope, or null when nothing matches. `null` is the
	// ONLY signal callers need: an unknown language, an empty info string and a grammar that fails
	// to load are the same outcome — render the block plain.
	public static string? ResolveScope(string? fenceInfo)
	{
		if (string.IsNullOrWhiteSpace(fenceInfo)) return null;
		var key = fenceInfo.Trim();
		lock (Gate)
		{
			_scopeByFenceInfo ??= BuildScopeTable();
			return _scopeByFenceInfo.GetValueOrDefault(key);
		}
	}

	// Fence tags the grammar set's own alias table does not carry. The rule for being on this list
	// is narrow and worth stating, because the list would otherwise grow into an invented registry:
	// a tag qualifies ONLY when it is another SPELLING (a file extension, an executable name, an
	// abbreviation) of a language ALREADY in the set. Mapping one therefore involves no judgment
	// about what a block contains — `cs` is C#, the way `.cs` is.
	//
	// NOT added, deliberately, though people do write them:
	//   `console`, `terminal`, `shell-session` — those blocks are command OUTPUT, not a script.
	//      Colouring them as bash would assert something untrue about the text.
	//   `conf`, `text`, `txt`, `plaintext` — either "no particular language" or a guess between
	//      several. Both stay plain, which is the honest rendering.
	// And a tag can only be a variant of something that EXISTS: `toml`, `kotlin`, `haskell`,
	// `elixir`, `erlang`, `scala`, `graphql`, `proto`, `hcl`/`tf`, `vue` and `svelte` have no
	// grammar in this set at all, so they are simply unhighlighted — see the coverage note in
	// MarkdownCodeHighlightingTests.
	static readonly (string Tag, string LanguageId)[] SpellingVariants =
	[
		("shell", "shellscript"), // set has bash/sh/zsh/ksh/csh, not the commonest doc spelling
		("yml", "yaml"),          // the other spelling of the same file
		("cs", "csharp"),         // the file extension
		("md", "markdown"),
		("rs", "rust"),
		("fs", "fsharp"),
		("pwsh", "powershell"),   // the executable's name for the same language
		("golang", "go"),
		("patch", "diff"),        // `.patch` is one of diff's own file extensions
		("make", "makefile"),
		("cmd", "bat"),           // `.cmd` is one of bat's own file extensions
		("cshtml", "razor"),      // `.cshtml` is one of razor's own file extensions
	];

	static Dictionary<string, string> BuildScopeTable()
	{
		var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		_options ??= new RegistryOptions(ThemeName.Dark);
		foreach (var language in _options.GetAvailableLanguages())
		{
			var scope = _options.GetScopeByLanguageId(language.Id);
			if (string.IsNullOrEmpty(scope)) continue;
			// Indexer, not Add: the language table legitimately lists some ids twice (`diff`).
			table[language.Id] = scope;
			foreach (var alias in language.Aliases ?? [])
				if (!string.IsNullOrWhiteSpace(alias))
					table[alias] = scope;
		}
		foreach (var (tag, languageId) in SpellingVariants)
			if (table.TryGetValue(languageId, out var scope) && !table.ContainsKey(tag))
				table[tag] = scope;
		return table;
	}

	// Highlight `source` as `scope`, returning the inner HTML for a <code> element, or null when
	// the block should be rendered plain instead.
	//
	// PERF (measured on this box): the worst real block on the motivating page — 87 lines, 8.7 KB,
	// longest line 297 chars — tokenizes in ~7 ms; a median 5-line block is far under a
	// millisecond. Cold grammar load is ~0.4 ms for most grammars and is paid once per grammar
	// per process (markdown is the outlier at ~95 ms, because its grammar embeds every other
	// language for fenced blocks).
	public static string? Highlight(string source, string scope)
	{
		if (source.Length > MaxHighlightChars) return null;

		lock (Gate)
		{
			var grammar = ResolveGrammar(scope);
			if (grammar is null) return null;

			try
			{
				var html = new StringBuilder(source.Length * 2);
				IStateStack? state = null;
				// Split on '\n' only, and keep every piece: Markdig has already normalized line
				// endings, and re-joining with '\n' below reproduces the source byte for byte.
				var lines = source.Split('\n');
				for (var i = 0; i < lines.Length; i++)
				{
					if (i > 0) html.Append('\n');
					var line = lines[i];
					var result = grammar.TokenizeLine(line, state, LineBudget);
					state = result.RuleStack;
					WriteLine(html, line, result.Tokens);
				}
				return html.ToString();
			}
			catch (Exception)
			{
				// A grammar fault must never cost a reader the body. Falling back to null renders
				// the block plain, exactly as an unknown language does.
				return null;
			}
		}
	}

	static IGrammar? ResolveGrammar(string scope)
	{
		if (GrammarCache.TryGetValue(scope, out var cached)) return cached;
		IGrammar? grammar;
		try
		{
			_options ??= new RegistryOptions(ThemeName.Dark);
			_registry ??= new Registry(_options);
			grammar = _registry.LoadGrammar(scope);
		}
		catch (Exception)
		{
			grammar = null;
		}
		// Negative results are cached too — a grammar that failed to load will fail again, and
		// retrying it once per code block per request would be the expensive way to learn that.
		GrammarCache[scope] = grammar;
		return grammar;
	}

	// Write one tokenized line. The invariant this method exists to hold is TEXT PRESERVATION: the
	// concatenated text content of what it writes equals `line`, always. Tokens are therefore
	// treated as a colouring HINT over the line, not as the source of the text — any span the
	// tokenizer skipped, returned out of range, or stopped short of on a timeout is still written
	// out, just uncoloured. A highlighter that can silently eat a character is worse than no
	// highlighter, because the reader cannot tell.
	static void WriteLine(StringBuilder html, string line, IToken[] tokens)
	{
		var pos = 0;
		foreach (var token in tokens)
		{
			var start = Math.Clamp(token.StartIndex, 0, line.Length);
			var end = Math.Clamp(token.EndIndex, 0, line.Length);
			if (end <= pos) continue;
			if (start > pos) AppendEscaped(html, line, pos, start); // gap the tokenizer skipped
			if (start < pos) start = pos; // overlap: never emit a character twice
			var cssClass = RoleFor(token.Scopes);
			if (cssClass is null) AppendEscaped(html, line, start, end);
			else
			{
				html.Append("<span class=\"").Append(cssClass).Append("\">");
				AppendEscaped(html, line, start, end);
				html.Append("</span>");
			}
			pos = end;
		}
		if (pos < line.Length) AppendEscaped(html, line, pos, line.Length); // tail
	}

	static void AppendEscaped(StringBuilder html, string line, int start, int end) =>
		html.Append(WebUtility.HtmlEncode(line[start..end]));

	// Walk the scope stack from the innermost scope outwards and return the first role a rule
	// yields. Matching is on whole dotted SEGMENTS: `string` matches `string.quoted.double.shell`
	// but must not match a scope like `markup.inline.raw.string.markdown`, and prefix matching
	// alone would get that wrong in both directions.
	internal static string? RoleFor(IReadOnlyList<string> scopes)
	{
		for (var i = scopes.Count - 1; i >= 0; i--)
		{
			var scope = scopes[i];
			foreach (var (prefix, role) in ScopeRules)
			{
				if (!scope.StartsWith(prefix, StringComparison.Ordinal)) continue;
				if (scope.Length > prefix.Length && scope[prefix.Length] != '.') continue;
				if (role is null) break; // transparent — keep walking outward
				return role;
			}
		}
		return null;
	}
}

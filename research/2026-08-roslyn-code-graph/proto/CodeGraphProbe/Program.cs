// CodeGraphProbe — worker A, 2026-08-20.
// Measures whether a Roslyn-over-sources core is feasible for PetBox, and whether the three
// NDepend failure cases disappear when the edge source is SEMANTICS instead of IL.
// Every printed finding names its source per 01-legend.md.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;

internal static class Program
{
	private static readonly Stopwatch Wall = Stopwatch.StartNew();
	private static readonly List<string> Log = [];

	private static void Say(string s)
	{
		var line = $"[{Wall.Elapsed.TotalSeconds,7:F2}s] {s}";
		Console.WriteLine(line);
		Log.Add(line);
	}

	public static async Task<int> Main(string[] args)
	{
		var sln = Arg(args, "--sln") ?? throw new ArgumentException("--sln required");
		var phase = Arg(args, "--phase") ?? "all";
		var outDir = Arg(args, "--out") ?? Path.GetTempPath();
		Directory.CreateDirectory(outDir);

		var vs = MSBuildLocator.RegisterDefaults();
		Say($"MSBuildLocator: {vs.Name} {vs.Version} @ {vs.MSBuildPath}");

		int rc;
		try
		{
			rc = await RunAsync(sln, phase, outDir).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			Say($"FATAL {ex.GetType().Name}: {ex.Message}");
			Say(ex.StackTrace ?? "");
			rc = 2;
		}

		var proc = Process.GetCurrentProcess();
		Say($"PeakWorkingSet = {proc.PeakWorkingSet64 / 1024.0 / 1024.0:F0} MB; " +
			$"GC total = {GC.GetTotalMemory(false) / 1024.0 / 1024.0:F0} MB; " +
			$"wall = {Wall.Elapsed.TotalSeconds:F2}s");
		await File.WriteAllLinesAsync(Path.Combine(outDir, $"probe-{phase}.log"), Log).ConfigureAwait(false);
		return rc;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static async Task<int> RunAsync(string sln, string phase, string outDir)
	{
		// ── PATH 1: MSBuildWorkspace over the .slnx directly ─────────────────────────────────
		var ws = MSBuildWorkspace.Create();
		ws.LoadMetadataForReferencedProjects = true;
		var failures = new List<string>();
		ws.WorkspaceFailed += (_, e) => failures.Add($"{e.Diagnostic.Kind}: {e.Diagnostic.Message}");

		var t0 = Stopwatch.StartNew();
		var solution = await ws.OpenSolutionAsync(sln).ConfigureAwait(false);
		t0.Stop();
		Say($"MEASURE open_solution_slnx = {t0.Elapsed.TotalSeconds:F2}s  (MSBuildWorkspace.OpenSolutionAsync)");
		Say($"projects = {solution.ProjectIds.Count}; documents = {solution.Projects.Sum(p => p.DocumentIds.Count)}");
		Say($"workspace.Diagnostics = {ws.Diagnostics.Count} (Failure={ws.Diagnostics.Count(d => d.Kind == WorkspaceDiagnosticKind.Failure)}, " +
			$"Warning={ws.Diagnostics.Count(d => d.Kind == WorkspaceDiagnosticKind.Warning)})");
		foreach (var d in ws.Diagnostics.Take(40)) Say($"  DIAG {d.Kind}: {d.Message}");

		foreach (var p in solution.Projects.OrderBy(p => p.Name, StringComparer.Ordinal))
			Say($"  PROJ {p.Name,-42} docs={p.DocumentIds.Count,4} addl={p.AdditionalDocumentIds.Count,3} " +
				$"analyzers={p.AnalyzerReferences.Count,2} metaRefs={p.MetadataReferences.Count,3} lang={p.Language}");

		// tests/** visible?
		var testProjects = solution.Projects.Where(p => p.FilePath?.Replace('\\', '/').Contains("/tests/", StringComparison.Ordinal) == true).ToList();
		Say($"CHECK tests_visible: {testProjects.Count} test projects, {testProjects.Sum(p => p.DocumentIds.Count)} documents [source: MSBuild project load]");

		if (phase == "open") return failures.Count > 0 ? 1 : 0;

		// ── Compilations ─────────────────────────────────────────────────────────────────────
		var t1 = Stopwatch.StartNew();
		var comps = new Dictionary<ProjectId, Compilation>();
		foreach (var p in solution.Projects)
		{
			var c = await p.GetCompilationAsync().ConfigureAwait(false);
			if (c is not null) comps[p.Id] = c;
		}
		t1.Stop();
		Say($"MEASURE all_compilations = {t1.Elapsed.TotalSeconds:F2}s  (GetCompilationAsync x{comps.Count})");

		var errCount = 0;
		var errSample = new List<string>();
		foreach (var (pid, c) in comps)
		{
			var errs = c.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
			errCount += errs.Count;
			foreach (var e in errs.Take(2))
				errSample.Add($"{solution.GetProject(pid)!.Name}: {e.Id} {e.GetMessage(CultureInfo.InvariantCulture)} @ {e.Location.GetLineSpan().Path}");
		}
		Say($"CHECK compile_errors = {errCount} across {comps.Count} compilations [source: semantics]");
		_compileErrors = errCount;
		_projects = solution.ProjectIds.Count;
		_documents = solution.Projects.Sum(p => p.DocumentIds.Count);
		_workspaceFailures = ws.Diagnostics.Count(d => d.Kind == WorkspaceDiagnosticKind.Failure);
		foreach (var e in errSample.Take(15)) Say($"  ERR {e}");

		// ── Source generators (Razor + others) ───────────────────────────────────────────────
		var tg = Stopwatch.StartNew();
		var genTotal = 0;
		var genByProject = new List<string>();
		foreach (var p in solution.Projects)
		{
			var gen = (await p.GetSourceGeneratedDocumentsAsync().ConfigureAwait(false)).ToList();
			if (gen.Count == 0) continue;
			genTotal += gen.Count;
			var razor = gen.Count(g => g.HintName.EndsWith(".cshtml.g.cs", StringComparison.OrdinalIgnoreCase) ||
				g.HintName.Contains("_Views_", StringComparison.OrdinalIgnoreCase) ||
				g.HintName.Contains("_Pages_", StringComparison.OrdinalIgnoreCase));
			genByProject.Add($"{p.Name}: {gen.Count} generated docs (razor-looking: {razor}); e.g. {string.Join(", ", gen.Take(3).Select(g => g.HintName))}");
		}
		tg.Stop();
		Say($"CHECK source_generated_docs = {genTotal} in {tg.Elapsed.TotalSeconds:F2}s [source: semantics via GetSourceGeneratedDocumentsAsync]");
		foreach (var g in genByProject) Say($"  GEN {g}");
		_generatedDocuments = genTotal;

		// Does the COMPILATION itself carry the generated trees (so semantics/FindReferences see them),
		// or are generated docs a side channel the normal query path never touches?
		var web = solution.Projects.First(p => p.Name == "PetBox.Web");
		var webComp = comps[web.Id];
		Say($"CHECK web_trees_in_compilation = {webComp.SyntaxTrees.Count()} vs project documents = {web.DocumentIds.Count} " +
			$"(delta = generated) [source: semantics]");
		var genTrees = webComp.SyntaxTrees.Where(t => t.FilePath.Contains("Razor", StringComparison.OrdinalIgnoreCase)
			|| t.FilePath.EndsWith("_cshtml.g.cs", StringComparison.OrdinalIgnoreCase)).ToList();
		Say($"CHECK web_generated_trees_matching_razor = {genTrees.Count}; sample paths: " +
			string.Join(" | ", webComp.SyntaxTrees.Skip(web.DocumentIds.Count).Take(3).Select(t => Path.GetFileName(t.FilePath))));
		var razorTypes = AllTypes(webComp.Assembly.GlobalNamespace)
			.Where(t => t.DeclaringSyntaxReferences.Any(r =>
				r.SyntaxTree.FilePath.EndsWith("_cshtml.g.cs", StringComparison.OrdinalIgnoreCase))).ToList();
		foreach (var t in webComp.SyntaxTrees.Skip(web.DocumentIds.Count).Take(3))
		{
			var sm0 = webComp.GetSemanticModel(t);
			var decls = t.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
				.Select(d => sm0.GetDeclaredSymbol(d)?.ToDisplayString() ?? d.Identifier.Text).ToList();
			Say($"  RAZORTREE {Path.GetFileName(t.FilePath)} declares: {string.Join(", ", decls)}");
		}
		Say($"CHECK razor_types_in_model = {razorTypes.Count}" +
			(razorTypes.Count > 0 ? $"; e.g. {razorTypes[0].ToDisplayString()} : {razorTypes[0].BaseType?.ToDisplayString()}" : "") +
			" [source: semantics]");
		// Does a symbol referenced ONLY from a .cshtml body come back? Probe: any member referenced
		// from a generated Razor tree.
		var razorEdge = 0;
		foreach (var t in webComp.SyntaxTrees.Skip(web.DocumentIds.Count))
		{
			var sm = webComp.GetSemanticModel(t);
			foreach (var n in t.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>())
				if (sm.GetSymbolInfo(n).Symbol is { } s2 && s2.ContainingAssembly?.Name.StartsWith("PetBox", StringComparison.Ordinal) == true)
					razorEdge++;
		}
		Say($"CHECK razor_generated_edges_into_petbox = {razorEdge} [source: semantics over generated trees]");

		if (phase == "gen") return 0;

		// ── CASE 1: PetBoxClaims (const string) ──────────────────────────────────────────────
		var results = new List<QueryResult>();
		var claims = FindType(comps.Values, "PetBox.Core.Auth.PetBoxClaims");
		if (claims is null) Say("CASE1 FAILED: PetBoxClaims type symbol not found");
		else
		{
			var tc = Stopwatch.StartNew();
			var typeRefs = await CountRefs(claims, solution).ConfigureAwait(false);
			var perField = new List<(string, int)>();
			foreach (var f in claims.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsConst))
				perField.Add((f.Name, await CountRefs(f, solution).ConfigureAwait(false)));
			tc.Stop();
			var sum = perField.Sum(x => x.Item2);
			Say($"CASE1 PetBoxClaims: type-refs={typeRefs}, const-field-refs sum={sum} " +
				$"({string.Join(", ", perField.Select(x => $"{x.Item1}={x.Item2}"))}) in {tc.Elapsed.TotalSeconds:F2}s [source: semantics]");
			results.Add(Result("who-uses PetBox.Core.Auth.PetBoxClaims (type + every const member)",
				typeRefs + sum, sum > 0));
		}

		// ── CASE 2: enum member reachable only through `case X.Y:` ───────────────────────────
		foreach (var enumName in new[] { "PetBox.Core.Settings.Scope", "PetBox.Core.Auth.ProjectAccess" })
		{
		var scope = FindType(comps.Values, enumName);
		if (scope is null) Say($"CASE2 FAILED: {enumName} symbol not found");
		else
		{
			var te = Stopwatch.StartNew();
			var rows = new List<string>();
			var syntaxOnlyCase = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var m in scope.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsConst))
			{
				var refs = await SymbolFinder.FindReferencesAsync(m, solution).ConfigureAwait(false);
				var locs = refs.SelectMany(r => r.Locations).ToList();
				var inCaseLabel = locs.Count(l => IsInsideCaseLabel(l));
				rows.Add($"{m.Name}: total={locs.Count}, of-which-in-switch-case={inCaseLabel}");
				syntaxOnlyCase[m.Name] = inCaseLabel;
			}
			te.Stop();
			Say($"CASE2 {enumName} members in {te.Elapsed.TotalSeconds:F2}s [source: semantics]: {string.Join(" | ", rows)}");
			results.Add(Result($"who-uses {enumName} members (incl. switch case labels)",
				syntaxOnlyCase.Values.Sum(), syntaxOnlyCase.Values.Sum() > 0));
		}
		}

		// ── CASE 3: attribute carriers at ANY level, one query ───────────────────────────────
		var ta = Stopwatch.StartNew();
		var sites = new List<string>();
		var carriers = AttributeCarriers(comps.Values,
			["ModelContextProtocol.Server.McpServerToolAttribute", "PetBox.Core.Auth.TenantFromAttribute"], sites);
		ta.Stop();
		await File.WriteAllLinesAsync(Path.Combine(outDir, "attribute-sites.txt"), sites.Order(StringComparer.Ordinal)).ConfigureAwait(false);
		foreach (var (attr, byKind, byCarrier) in carriers)
		{
			Say($"CASE3 {attr} in {ta.Elapsed.TotalSeconds:F2}s [source: semantics]: " +
				$"applications={byKind.Values.Sum()} ({string.Join(", ", byKind.Select(k => $"{k.Key}={k.Value}"))}); " +
				$"distinct carriers={byCarrier.Values.Sum()} ({string.Join(", ", byCarrier.Select(k => $"{k.Key}={k.Value}"))})");
			results.Add(Result($"carriers-of {attr} at any declaration level", byCarrier.Values.Sum(), true));
		}

		if (phase == "cases")
		{
			await WriteJson(outDir, results).ConfigureAwait(false);
			return 0;
		}

		// ── Heavy / light symbol reference queries ───────────────────────────────────────────
		var cp = FindType(comps.Values, "System.Security.Claims.ClaimsPrincipal");
		if (cp is not null)
		{
			var th = Stopwatch.StartNew();
			var n = await CountRefs(cp, solution).ConfigureAwait(false);
			th.Stop();
			Say($"MEASURE find_refs_heavy ClaimsPrincipal = {n} refs in {th.Elapsed.TotalSeconds:F2}s [source: semantics]");
			results.Add(Result("who-uses System.Security.Claims.ClaimsPrincipal", n, true));
		}
		var light = FindType(comps.Values, "PetBox.Core.Auth.TenantFromAttribute");
		if (light is not null)
		{
			var tl = Stopwatch.StartNew();
			var n = await CountRefs(light, solution).ConfigureAwait(false);
			tl.Stop();
			Say($"MEASURE find_refs_light TenantFromAttribute = {n} refs in {tl.Elapsed.TotalSeconds:F2}s [source: semantics]");
		}
		// second run of the same heavy query — is the second query cheap once compilations are warm?
		if (cp is not null)
		{
			var th2 = Stopwatch.StartNew();
			var n2 = await CountRefs(cp, solution).ConfigureAwait(false);
			th2.Stop();
			Say($"MEASURE find_refs_heavy_second ClaimsPrincipal = {n2} refs in {th2.Elapsed.TotalSeconds:F2}s [source: semantics]");
		}

		// -- INCREMENTAL EDIT: the live-Workspace-vs-index argument turns on this number.
		// One document is rewritten (as an agent would after an edit); what does the next query cost?
		var editDoc = solution.Projects.First(p => p.Name == "PetBox.Core")
			.Documents.First(d => d.Name.EndsWith("TenantAuthorizer.cs", StringComparison.Ordinal));
		var oldText = await editDoc.GetTextAsync().ConfigureAwait(false);
		var edited = solution.WithDocumentText(editDoc.Id,
			Microsoft.CodeAnalysis.Text.SourceText.From(oldText.ToString() + "\n// probe edit\n"));
		var te2 = Stopwatch.StartNew();
		var editedComp = await edited.GetProject(editDoc.Project.Id)!.GetCompilationAsync().ConfigureAwait(false);
		te2.Stop();
		Say($"MEASURE reparse_one_doc_owning_project = {te2.Elapsed.TotalSeconds:F2}s (WithDocumentText + GetCompilationAsync on PetBox.Core) [source: semantics]");
		if (cp is not null)
		{
			var cp2 = FindType([editedComp!], "System.Security.Claims.ClaimsPrincipal") ?? cp;
			var te3 = Stopwatch.StartNew();
			var n3 = await CountRefs(cp2, edited).ConfigureAwait(false);
			te3.Stop();
			Say($"MEASURE find_refs_heavy_after_edit = {n3} refs in {te3.Elapsed.TotalSeconds:F2}s (all 21 downstream projects re-bind against the changed PetBox.Core) [source: semantics]");
		}

		if (phase == "perf")
		{
			await WriteJson(outDir, results).ConfigureAwait(false);
			return 0;
		}

		// ── Node/edge census: what a SQLite index would have to hold ────────────────────────
		var sink = phase == "sqlite" ? new List<(string File, int Line, string Symbol, string Kind)>() : null;
		var tn = Stopwatch.StartNew();
		long nodes = 0, edges = 0, unresolved = 0;
		foreach (var p in solution.Projects)
		{
			if (!comps.TryGetValue(p.Id, out var c)) continue;
			foreach (var t in AllTypes(c.Assembly.GlobalNamespace))
			{
				nodes++;
				nodes += t.GetMembers().Length;
			}
			foreach (var doc in p.Documents)
			{
				var tree = await doc.GetSyntaxTreeAsync().ConfigureAwait(false);
				if (tree is null) continue;
				var model = c.GetSemanticModel(tree);
				var root = await tree.GetRootAsync().ConfigureAwait(false);
				foreach (var node in root.DescendantNodes())
				{
					if (node is not (IdentifierNameSyntax or GenericNameSyntax or MemberAccessExpressionSyntax
						or ObjectCreationExpressionSyntax or InvocationExpressionSyntax)) continue;
					var si = model.GetSymbolInfo(node);
					if (si.Symbol is not null)
					{
						edges++;
						if (sink is not null)
						{
							var ls = node.GetLocation().GetLineSpan();
							sink.Add((doc.FilePath ?? doc.Name, ls.StartLinePosition.Line + 1,
								si.Symbol.ToDisplayString(), si.Symbol.Kind.ToString()));
						}
					}
					else if (si.CandidateSymbols.Length > 0) unresolved++;
				}
			}
		}
		tn.Stop();
		Say($"MEASURE node_edge_census nodes={nodes} edges={edges} unresolved={unresolved} " +
			$"in {tn.Elapsed.TotalSeconds:F2}s [source: semantics over every syntax node]");

		if (sink is not null)
		{
			var dbPath = Path.Combine(outDir, "codegraph.db");
			if (File.Exists(dbPath)) File.Delete(dbPath);
			var ts = Stopwatch.StartNew();
			using (var cn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
			{
				cn.Open();
				using (var pragma = cn.CreateCommand())
				{
					pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; " +
						"CREATE TABLE edge(file TEXT NOT NULL, line INT NOT NULL, sym TEXT NOT NULL, kind TEXT NOT NULL);";
					pragma.ExecuteNonQuery();
				}
				using var tx = cn.BeginTransaction();
				using var ins = cn.CreateCommand();
				ins.CommandText = "INSERT INTO edge(file,line,sym,kind) VALUES($f,$l,$s,$k)";
				var pf = ins.CreateParameter(); pf.ParameterName = "$f"; ins.Parameters.Add(pf);
				var pl = ins.CreateParameter(); pl.ParameterName = "$l"; ins.Parameters.Add(pl);
				var psy = ins.CreateParameter(); psy.ParameterName = "$s"; ins.Parameters.Add(psy);
				var pk = ins.CreateParameter(); pk.ParameterName = "$k"; ins.Parameters.Add(pk);
				foreach (var (f, l, sy, k) in sink)
				{
					pf.Value = f; pl.Value = l; psy.Value = sy; pk.Value = k;
					ins.ExecuteNonQuery();
				}
				tx.Commit();
				using var idx = cn.CreateCommand();
				idx.CommandText = "CREATE INDEX ix_edge_sym ON edge(sym); CREATE INDEX ix_edge_file ON edge(file);";
				idx.ExecuteNonQuery();
			}
			ts.Stop();
			var sizeMb = new FileInfo(dbPath).Length / 1024.0 / 1024.0;
			Say($"MEASURE sqlite_dump rows={sink.Count} time={ts.Elapsed.TotalSeconds:F2}s size={sizeMb:F1} MB -> {dbPath}");
			using (var cn2 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
			{
				cn2.Open();
				var tq = Stopwatch.StartNew();
				using var q = cn2.CreateCommand();
				q.CommandText = "SELECT COUNT(*) FROM edge WHERE sym LIKE 'System.Security.Claims.ClaimsPrincipal%'";
				var got = Convert.ToInt64(q.ExecuteScalar(), CultureInfo.InvariantCulture);
				tq.Stop();
				Say($"MEASURE sqlite_query_claimsprincipal = {got} rows in {tq.Elapsed.TotalMilliseconds:F1} ms (cold connection)");
			}
		}

		await WriteJson(outDir, results).ConfigureAwait(false);
		return 0;
	}

	// ── helpers ─────────────────────────────────────────────────────────────────────────────

	private static string? Arg(string[] a, string k)
	{
		for (var i = 0; i < a.Length - 1; i++) if (a[i] == k) return a[i + 1];
		return null;
	}

	private static INamedTypeSymbol? FindType(IEnumerable<Compilation> comps, string metadataName)
	{
		foreach (var c in comps)
		{
			var t = c.GetTypeByMetadataName(metadataName);
			if (t is not null) return t;
		}
		return null;
	}

	private static async Task<int> CountRefs(ISymbol s, Solution sol)
	{
		var refs = await SymbolFinder.FindReferencesAsync(s, sol).ConfigureAwait(false);
		return refs.Sum(r => r.Locations.Count());
	}

	private static bool IsInsideCaseLabel(ReferenceLocation l)
	{
		var tree = l.Location.SourceTree;
		if (tree is null) return false;
		var node = tree.GetRoot().FindNode(l.Location.SourceSpan);
		for (var n = node; n is not null; n = n.Parent)
			if (n is CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax or SwitchExpressionArmSyntax) return true;
		return false;
	}

	private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceOrTypeSymbol root)
	{
		foreach (var m in root.GetMembers())
		{
			if (m is INamespaceSymbol ns) { foreach (var t in AllTypes(ns)) yield return t; }
			else if (m is INamedTypeSymbol nt)
			{
				yield return nt;
				foreach (var t in AllTypes(nt)) yield return t;
			}
		}
	}

	private static List<(string Attr, Dictionary<string, int> ByKind, Dictionary<string, int> Carriers)> AttributeCarriers(
		IEnumerable<Compilation> comps, string[] attrMetadataNames, List<string> sites)
	{
		var res = attrMetadataNames.ToDictionary(a => a, _ => new Dictionary<string, int>(StringComparer.Ordinal), StringComparer.Ordinal);
		var car = attrMetadataNames.ToDictionary(a => a, _ => new Dictionary<string, int>(StringComparer.Ordinal), StringComparer.Ordinal);
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var carSeen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var c in comps)
		{
			foreach (var t in AllTypes(c.Assembly.GlobalNamespace))
			{
				Bump(t, "Type");
				foreach (var m in t.GetMembers())
				{
					Bump(m, m.Kind.ToString());
					if (m is IMethodSymbol ms)
						foreach (var p in ms.Parameters) Bump(p, "Parameter");
				}
			}
			Bump(c.Assembly, "Assembly");
		}
		return [.. res.Select(kv => (kv.Key, kv.Value, car[kv.Key]))];

		void Bump(ISymbol s, string level)
		{
			var i = 0;
			foreach (var a in s.GetAttributes())
			{
				var full = a.AttributeClass?.ToDisplayString();
				i++;
				if (full is null || !res.TryGetValue(full, out var byKind)) continue;
				// dedup by the ATTRIBUTE APPLICATION (its own syntax location), not by carrier:
				// AllowMultiple attributes stack on one symbol and each is a separate declaration.
				var appLoc = a.ApplicationSyntaxReference is { } r
					? $"{r.SyntaxTree.FilePath}:{r.Span}"
					: $"{s.ToDisplayString()}#{i}";
				var carrierKey = $"{full}|{s.ToDisplayString()}|{s.Locations.FirstOrDefault()?.GetLineSpan()}";
				if (carSeen.Add(carrierKey)) car[full][level] = car[full].GetValueOrDefault(level) + 1;
				if (!seen.Add($"{full}|{appLoc}")) continue;
				sites.Add($"{full.Split('.')[^1]}	{level}	{appLoc}	{s.ToDisplayString()}");
				byKind[level] = byKind.GetValueOrDefault(level) + 1;
			}
		}
	}

	private static int _compileErrors = -1;
	private static int _projects;
	private static int _documents;
	private static int _generatedDocuments;
	private static int _workspaceFailures;

	private static QueryResult Result(string question, int count, bool exhaustive) => new()
	{
		Question = question,
		Count = count,
		Provenance = new Provenance
		{
			SourcesSearched = ["semantics (Roslyn ISymbol/SymbolFinder over every C# document in the solution)",
				"syntax (declaration sites of the same documents)"],
			NotSearched =
			[
				"strings (claim names / routes / partial names in string literals, .cshtml text, .ts)",
				"runtime-dump (DI registrations, endpoint table)",
				"IL (bin/** assemblies; nothing here was read from compiled output)",
				"reflection (Type.GetType / Activator / attribute discovery at runtime)",
				"non-C# projects (none in this solution) and any project MSBuild failed to load"
			],
			// EMPIRICAL: a solution whose packages are not restored still opens with
			// workspace.Diagnostics == 0 and then answers 0 for questions whose true answer is 98.
			// Completeness therefore keys off compilation health, never off the loader being quiet.
			Completeness = !exhaustive ? "partial"
				: _compileErrors == 0 && _workspaceFailures == 0 ? "exhaustive-within-sources"
				: "UNTRUSTWORTHY-model-does-not-compile",
			ModelHealth = new ModelHealth
			{
				Projects = _projects,
				Documents = _documents,
				GeneratedDocuments = _generatedDocuments,
				CompilationErrors = _compileErrors,
				WorkspaceLoadFailures = _workspaceFailures,
				Verdict = _compileErrors == 0 && _workspaceFailures == 0
					? "every project bound cleanly; a zero from this run is an HONEST ZERO for the semantic source"
					: $"{_compileErrors} binding errors: unresolved names silently drop edges, so a zero here means NOTHING",
			},
			CompletenessNote = exhaustive
				? "Every C# declaration and reference in every loaded project was examined; a zero here is a HONEST ZERO for the semantic source and says nothing about strings/DI/reflection."
				: "The search did not cover every intended source; a zero here means 'did not look', not 'not present'.",
			EdgeSource = "semantics",
			SolutionLoad = "MSBuildWorkspace.OpenSolutionAsync over PetBox.slnx",
		},
	};

	private static async Task WriteJson(string outDir, List<QueryResult> results)
	{
		var path = Path.Combine(outDir, "sample-answers.json");
		var json = JsonSerializer.Serialize(results, new JsonSerializerOptions
		{
			WriteIndented = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		});
		await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
		Say($"wrote {path}");
	}

	private sealed class QueryResult
	{
		public string Question { get; set; } = "";
		public int Count { get; set; }
		public Provenance Provenance { get; set; } = new();
	}

	private sealed class Provenance
	{
		public ImmutableArray<string> SourcesSearched { get; set; } = [];
		public ImmutableArray<string> NotSearched { get; set; } = [];
		public string Completeness { get; set; } = "";
		public string CompletenessNote { get; set; } = "";
		public string EdgeSource { get; set; } = "";
		public string SolutionLoad { get; set; } = "";
		public ModelHealth ModelHealth { get; set; } = new();
	}

	private sealed class ModelHealth
	{
		public int Projects { get; set; }
		public int Documents { get; set; }
		public int GeneratedDocuments { get; set; }
		public int CompilationErrors { get; set; }
		public int WorkspaceLoadFailures { get; set; }
		public string Verdict { get; set; } = "";
	}
}

// CallGraph — worker I, 2026-08-20.
// Builds REAL caller->callee edges from SEMANTICS (01-legend.md), on top of the compilations
// worker A's probe already produces. Every edge carries a stable key (documentation comment id)
// so it can later be joined with IL edges and with a runtime dump.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

internal static class CallGraph
{
	private sealed class KeyStats
	{
		public long WithDocId;
		public long Fallback;
		public readonly Dictionary<string, long> FallbackByKind = new(StringComparer.Ordinal);
	}

	private static readonly SymbolDisplayFormat FqFormat =
		SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
			SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

	public static async Task RunAsync(
		Solution solution,
		Dictionary<ProjectId, Compilation> comps,
		string outDir,
		string repoRoot,
		Action<string> say)
	{
		Directory.CreateDirectory(outDir);
		var edgesPath = Path.Combine(outDir, "call-edges.tsv");
		var nodesPath = Path.Combine(outDir, "call-nodes.tsv");

		var keyStats = new KeyStats();
		var kindCount = new Dictionary<string, long>(StringComparer.Ordinal);
		var kindTests = new Dictionary<string, long>(StringComparer.Ordinal);
		var kindGen = new Dictionary<string, long>(StringComparer.Ordinal);
		var kindExt = new Dictionary<string, long>(StringComparer.Ordinal);
		var reasonCount = new Dictionary<string, long>(StringComparer.Ordinal);
		var viaCount = new Dictionary<string, long>(StringComparer.Ordinal);
		var unattrByContext = new Dictionary<string, long>(StringComparer.Ordinal);
		long edgeRows = 0, callSitesSeen = 0, unattributed = 0, constructedEdges = 0;

		var nodeSeen = new HashSet<string>(StringComparer.Ordinal);
		await using var nodeW = new StreamWriter(nodesPath, false, new UTF8Encoding(false));
		await using var edgeW = new StreamWriter(edgesPath, false, new UTF8Encoding(false));
		await nodeW.WriteLineAsync("key\tdisplay\tkind\tmethod_kind\tasm\text\tgen\ttests\tfile\tline").ConfigureAwait(false);
		await edgeW.WriteLineAsync("caller\tcallee\tkind\treason\tvia\tfile\tline\ttests\tgen\text\tconstructed").ConfigureAwait(false);

		void Node(ISymbol s, string key, bool generated, bool inTests)
		{
			if (!nodeSeen.Add(key)) return;
			var loc = s.Locations.FirstOrDefault(l => l.IsInSource);
			var ls = loc?.GetLineSpan();
			var asm = s.ContainingAssembly?.Name ?? "";
			var ext = asm.StartsWith("PetBox", StringComparison.Ordinal) ? 0 : 1;
			var declGen = generated && ext == 0 ? 1 : 0;
			nodeW.WriteLine(string.Join('\t',
				key,
				Clean(s.ToDisplayString(FqFormat)),
				s.Kind.ToString(),
				s is IMethodSymbol m ? m.MethodKind.ToString() : "",
				asm,
				ext.ToString(CultureInfo.InvariantCulture),
				declGen.ToString(CultureInfo.InvariantCulture),
				inTests ? "1" : "0",
				Rel(ls?.Path ?? "", repoRoot),
				(ls?.StartLinePosition.Line + 1 ?? 0).ToString(CultureInfo.InvariantCulture)));
		}

		void Edge(ISymbol caller, string callerKey, ISymbol callee, string calleeKey, string kind,
			string reason, string via, string file, int line, bool inTests, bool generated, bool constructed)
		{
			var ext = callee.ContainingAssembly?.Name.StartsWith("PetBox", StringComparison.Ordinal) != true;
			edgeW.WriteLine(string.Join('\t',
				callerKey, calleeKey, kind, reason, via,
				Rel(file, repoRoot), line.ToString(CultureInfo.InvariantCulture),
				inTests ? "1" : "0", generated ? "1" : "0", ext ? "1" : "0", constructed ? "1" : "0"));
			edgeRows++;
			kindCount[kind] = kindCount.GetValueOrDefault(kind) + 1;
			if (inTests) kindTests[kind] = kindTests.GetValueOrDefault(kind) + 1;
			if (generated) kindGen[kind] = kindGen.GetValueOrDefault(kind) + 1;
			if (ext) kindExt[kind] = kindExt.GetValueOrDefault(kind) + 1;
			if (constructed) constructedEdges++;
			if (reason.Length > 0) reasonCount[reason] = reasonCount.GetValueOrDefault(reason) + 1;
			if (via.Length > 0) viaCount[via] = viaCount.GetValueOrDefault(via) + 1;
			Node(caller, callerKey, generated, inTests);
			Node(callee, calleeKey, generated, inTests);
		}

		// ── PASS 1: call sites ──────────────────────────────────────────────────────────────
		var tWalk = Stopwatch.StartNew();
		var genTrees = 0;
		foreach (var project in solution.Projects)
		{
			if (!comps.TryGetValue(project.Id, out var comp)) continue;
			var inTests = (project.FilePath ?? "").Replace('\\', '/').Contains("/tests/", StringComparison.Ordinal);
			var docTrees = new HashSet<SyntaxTree>();
			foreach (var d in project.Documents)
			{
				var t = await d.GetSyntaxTreeAsync().ConfigureAwait(false);
				if (t is not null) docTrees.Add(t);
			}

			foreach (var tree in comp.SyntaxTrees)
			{
				var generated = !docTrees.Contains(tree);
				if (generated) genTrees++;
				var model = comp.GetSemanticModel(tree);
				var root = await tree.GetRootAsync().ConfigureAwait(false);
				var file = tree.FilePath ?? "";

				foreach (var node in root.DescendantNodes())
				{
					switch (node)
					{
						case InvocationExpressionSyntax:
						case ObjectCreationExpressionSyntax:
						case ImplicitObjectCreationExpressionSyntax:
						case AttributeSyntax:
						case ConstructorInitializerSyntax:
						case ElementAccessExpressionSyntax:
						case ConstructorDeclarationSyntax:
						case IdentifierNameSyntax:
						case GenericNameSyntax:
							break;
						default:
							continue;
					}

					// Names already consumed by an enclosing invocation / creation are skipped so
					// one call site produces one edge, not two.
					if (node is SimpleNameSyntax && IsConsumedByParent(node)) continue;

					var caller = Caller(model, comp, node, out var via);
					if (caller is null)
					{
						unattributed++;
						var ctx = TopContext(node);
						unattrByContext[ctx] = unattrByContext.GetValueOrDefault(ctx) + 1;
						continue;
					}
					var callerKey = Key(caller, keyStats);

					var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

					if (node is ConstructorDeclarationSyntax ctorDecl)
					{
						// Implicit `: base()` — no syntax exists for it, so it is synthesised here.
						if (ctorDecl.Initializer is not null) continue;
						if (ModelExtensions.GetDeclaredSymbol(model, ctorDecl) is not IMethodSymbol cs || cs.IsStatic) continue;
						var baseCtor = cs.ContainingType?.BaseType?.InstanceConstructors
							.FirstOrDefault(c => c.Parameters.Length == 0);
						if (baseCtor is null) continue;
						var bc = Canon(baseCtor);
						var bk = Key(bc, keyStats);
						Edge(caller, callerKey, bc, bk, "ctor.implicit-base", "", via, file, line, inTests, generated, false);
						continue;
					}

					callSitesSeen++;
					var si = ModelExtensions.GetSymbolInfo(model, node);
					var sym = si.Symbol;

					if (sym is null)
					{
						if (si.CandidateSymbols.Length == 0) continue;
						var reason = si.CandidateReason.ToString();
						foreach (var cand in si.CandidateSymbols)
						{
							if (cand is not (IMethodSymbol or IPropertySymbol)) continue;
							var c = Canon(cand);
							var ck = Key(c, keyStats);
							var akind = node is InvocationExpressionSyntax ? "call.ambiguous" : "ref.ambiguous";
							Edge(caller, callerKey, c, ck, akind, reason, via, file, line, inTests, generated, false);
						}
						continue;
					}

					switch (sym)
					{
						case IMethodSymbol m:
						{
							var canon = (IMethodSymbol)Canon(m);
							var constructed = !SymbolEqualityComparer.Default.Equals(m, canon);
							var kind = node switch
							{
								ObjectCreationExpressionSyntax => "ctor",
								ImplicitObjectCreationExpressionSyntax => "ctor.implicit-new",
								AttributeSyntax => "ctor.attribute",
								ConstructorInitializerSyntax => "ctor.chain",
								InvocationExpressionSyntax => InvocationKind(canon),
								_ => "methodgroup",
							};
							var ck = Key(canon, keyStats);
							Edge(caller, callerKey, canon, ck, kind, "", via, file, line, inTests, generated, constructed);
							break;
						}

						case IPropertySymbol p:
						{
							var canon = (IPropertySymbol)Canon(p);
							var constructed = !SymbolEqualityComparer.Default.Equals(p, canon);
							var prefix = canon.IsIndexer ? "indexer" : "prop";
							var (reads, writes) = ReadWrite(node);
							if (reads)
							{
								var target = (ISymbol?)canon.GetMethod ?? canon;
								Edge(caller, callerKey, target, Key(Canon(target), keyStats),
									prefix + ".get", "", via, file, line, inTests, generated, constructed);
							}
							if (writes)
							{
								var target = (ISymbol?)canon.SetMethod ?? canon;
								Edge(caller, callerKey, target, Key(Canon(target), keyStats),
									prefix + ".set", "", via, file, line, inTests, generated, constructed);
							}
							break;
						}

						case IEventSymbol ev:
						{
							var canon = Canon(ev);
							Edge(caller, callerKey, canon, Key(canon, keyStats), "event", "", via, file, line, inTests, generated, false);
							break;
						}
					}
				}
			}
		}
		tWalk.Stop();
		say($"MEASURE callgraph_walk = {tWalk.Elapsed.TotalSeconds:F2}s; call sites examined = {callSitesSeen}; " +
			$"generated trees walked = {genTrees}; edges so far = {edgeRows}; unattributed sites = {unattributed} [source: semantics]");
		foreach (var u in unattrByContext.OrderByDescending(k => k.Value).Take(10))
			say($"  UNATTRIBUTED {u.Key} = {u.Value}");

		// ── PASS 2: dispatch targets (interface implementations + overrides) ────────────────
		// The call-site edge always goes to the DECLARED member (IFoo.Bar). These separate edges
		// carry the "and here is what could actually run" half, so a consumer picks the semantics
		// it wants instead of getting one lossy merged edge.
		var tImpl = Stopwatch.StartNew();
		var implSeen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var comp in comps.Values)
		{
			if (comp.AssemblyName?.StartsWith("PetBox", StringComparison.Ordinal) != true) continue;
			var inTests = comp.AssemblyName.Contains("Tests", StringComparison.Ordinal);
			foreach (var t in AllTypes(comp.Assembly.GlobalNamespace))
			{
				if (t.TypeKind is TypeKind.Interface) continue;
				foreach (var iface in t.AllInterfaces)
				{
					foreach (var im in iface.GetMembers())
					{
						if (im.Kind is not (SymbolKind.Method or SymbolKind.Property or SymbolKind.Event)) continue;
						var impl = t.FindImplementationForInterfaceMember(im);
						if (impl is null) continue;
						var a = Canon(im.OriginalDefinition);
						var b = Canon(impl.OriginalDefinition);
						var ka = Key(a, keyStats);
						var kb = Key(b, keyStats);
						if (!implSeen.Add(ka + "" + kb + "i")) continue;
						Edge(a, ka, b, kb, "impl.interface", "", "", b.Locations.FirstOrDefault()?.GetLineSpan().Path ?? "", 0, inTests, false, false);
					}
				}
				foreach (var m in t.GetMembers())
				{
					ISymbol? over = m switch
					{
						IMethodSymbol ms when ms.IsOverride => ms.OverriddenMethod,
						IPropertySymbol ps when ps.IsOverride => ps.OverriddenProperty,
						IEventSymbol es when es.IsOverride => es.OverriddenEvent,
						_ => null,
					};
					if (over is null) continue;
					var a = Canon(over.OriginalDefinition);
					var b = Canon(m.OriginalDefinition);
					var ka = Key(a, keyStats);
					var kb = Key(b, keyStats);
					if (!implSeen.Add(ka + "" + kb + "o")) continue;
					Edge(a, ka, b, kb, "impl.override", "", "", b.Locations.FirstOrDefault()?.GetLineSpan().Path ?? "", 0, inTests, false, false);
				}
			}
		}
		tImpl.Stop();
		say($"MEASURE dispatch_sweep = {tImpl.Elapsed.TotalSeconds:F2}s; impl/override edges = {implSeen.Count} " +
			"(INamedTypeSymbol.FindImplementationForInterfaceMember + OverriddenMethod, no solution-wide search) [source: semantics]");

		await edgeW.FlushAsync().ConfigureAwait(false);
		await nodeW.FlushAsync().ConfigureAwait(false);

		// ── Cross-check the cheap sweep against the API the brief names ─────────────────────
		var ourTypes = comps.Values
			.Where(c => c.AssemblyName?.StartsWith("PetBox", StringComparison.Ordinal) == true)
			.SelectMany(c => AllTypes(c.Assembly.GlobalNamespace)).ToList();
		var implCount = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
		var implSynthesized = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
		foreach (var t in ourTypes.Where(t => t.TypeKind != TypeKind.Interface))
			foreach (var iface in t.AllInterfaces)
				foreach (var im0 in iface.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary))
					if (t.FindImplementationForInterfaceMember(im0) is { } impl0)
					{
						var d = (IMethodSymbol)Canon(im0.OriginalDefinition);
						implCount[d] = implCount.GetValueOrDefault(d) + 1;
						if (impl0.IsImplicitlyDeclared)
							implSynthesized[d] = implSynthesized.GetValueOrDefault(d) + 1;
					}
		var probeIfaces = implCount.OrderByDescending(k => k.Value).Take(10)
			.Select(k => (IMethodSymbol)k.Key).ToList();
		say($"CHECK interface_members_with_impls = {implCount.Count} distinct interface methods; " +
			$"top sample = {string.Join(", ", implCount.OrderByDescending(k => k.Value).Take(5).Select(k => $"{k.Key.ContainingType.Name}.{k.Key.Name}={k.Value}"))}");
		var tSf = Stopwatch.StartNew();
		long sfTotal = 0, sfSource = 0, sfMetadata = 0;
		var sfDistinct = new HashSet<string>(StringComparer.Ordinal);
		foreach (var im in probeIfaces)
			foreach (var r in await SymbolFinder.FindImplementationsAsync(im, solution).ConfigureAwait(false))
			{
				sfTotal++;
				if (r.Locations.Any(l => l.IsInSource)) sfSource++; else sfMetadata++;
				sfDistinct.Add(Key(Canon(r.OriginalDefinition), keyStats));
			}
		tSf.Stop();
		say($"CHECK findimplementations_split total={sfTotal} in-source={sfSource} in-metadata={sfMetadata} " +
			$"distinct-by-key={sfDistinct.Count} [source: semantics]");
		var sweepTotal = probeIfaces.Sum(im => implCount.GetValueOrDefault(im));
		var sweepSynth = probeIfaces.Sum(im => implSynthesized.GetValueOrDefault(im));
		say($"CHECK symbolfinder_vs_sweep on {probeIfaces.Count} interface methods: " +
			$"SymbolFinder.FindImplementationsAsync = {sfTotal} impls in {tSf.Elapsed.TotalSeconds:F2}s; " +
			$"symbolic sweep over PetBox source assemblies = {sweepTotal} impls, " +
			$"of which compiler-synthesized (record members etc.) = {sweepSynth} [source: semantics]");

		// Pick virtual/abstract methods that the sweep says DO have overrides — a sample of ten
		// arbitrary ones answers 0 and proves nothing.
		var overCount = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
		foreach (var t in ourTypes)
			foreach (var m in t.GetMembers().OfType<IMethodSymbol>())
				if (m.IsOverride && m.OverriddenMethod is { } om0)
				{
					var d = (IMethodSymbol)Canon(om0.OriginalDefinition);
					overCount[d] = overCount.GetValueOrDefault(d) + 1;
				}
		var virt = overCount.OrderByDescending(k => k.Value).Take(10)
			.Select(k => (IMethodSymbol)k.Key).ToList();
		say($"CHECK overridden_methods_with_overrides = {overCount.Count} distinct base methods; " +
			$"top sample = {string.Join(", ", overCount.OrderByDescending(k => k.Value).Take(5).Select(k => $"{k.Key.Name}={k.Value}"))}");
		var tOv = Stopwatch.StartNew();
		long ovTotal = 0, ovSource = 0;
		var ovDistinct = new HashSet<string>(StringComparer.Ordinal);
		foreach (var m in virt)
			foreach (var r in await SymbolFinder.FindOverridesAsync(m, solution).ConfigureAwait(false))
			{
				ovTotal++;
				if (r.Locations.Any(l => l.IsInSource)) ovSource++;
				ovDistinct.Add(Key(Canon(r.OriginalDefinition), keyStats));
			}
		tOv.Stop();
		say($"CHECK findoverrides on {virt.Count} virtual/abstract methods = {ovTotal} overrides " +
			$"(in-source={ovSource}, distinct-by-key={ovDistinct.Count}) in {tOv.Elapsed.TotalSeconds:F2}s [source: semantics]");

		// ── Summary ─────────────────────────────────────────────────────────────────────────
		var eSize = new FileInfo(edgesPath).Length / 1024.0 / 1024.0;
		var nSize = new FileInfo(nodesPath).Length / 1024.0 / 1024.0;
		say($"MEASURE callgraph_total edges={edgeRows} nodes={nodeSeen.Count} " +
			$"tsv={eSize:F1} MB (edges) + {nSize:F1} MB (nodes) in {(tWalk.Elapsed + tImpl.Elapsed).TotalSeconds:F2}s");
		say($"KEYS doc-comment-id ok={keyStats.WithDocId} fallback={keyStats.Fallback} " +
			$"({string.Join(", ", keyStats.FallbackByKind.OrderByDescending(k => k.Value).Take(8).Select(k => $"{k.Key}={k.Value}"))})");
		foreach (var k in kindCount.OrderByDescending(k => k.Value))
			say($"  KIND {k.Key,-24} total={k.Value,7} tests={kindTests.GetValueOrDefault(k.Key),6} " +
				$"generated={kindGen.GetValueOrDefault(k.Key),6} external-callee={kindExt.GetValueOrDefault(k.Key),7}");
		foreach (var r in reasonCount.OrderByDescending(k => k.Value))
			say($"  CANDIDATEREASON {r.Key} = {r.Value}");
		foreach (var v in viaCount.OrderByDescending(k => k.Value))
			say($"  VIA {v.Key} = {v.Value}");
		say($"  constructed-generic edges = {constructedEdges}");

		var json = new StringBuilder();
		json.Append("{\n  \"generated\": \"2026-08-20\",\n  \"source\": \"semantics\",\n");
		json.Append("  \"producer\": \"research/2026-08-roslyn-code-graph/proto/CodeGraphProbe --phase calls\",\n");
		json.Append("""
			  "tables": {
			    "edges": { "file": "call-edges.parquet", "columns": [
			      { "name": "caller", "type": "VARCHAR", "note": "stable key of the member the call site is attributed to" },
			      { "name": "callee", "type": "VARCHAR", "note": "stable key of the target; always the ORIGINAL DEFINITION, never a constructed generic" },
			      { "name": "kind", "type": "VARCHAR", "note": "one of edgeKinds[].kind" },
			      { "name": "reason", "type": "VARCHAR", "note": "SymbolInfo.CandidateReason; non-empty only on *.ambiguous edges" },
			      { "name": "via", "type": "VARCHAR", "note": "'lambda' when the call site sits inside an anonymous function; local functions are their own caller instead" },
			      { "name": "file", "type": "VARCHAR", "note": "call-site path relative to repo root" },
			      { "name": "line", "type": "BIGINT" },
			      { "name": "tests", "type": "BIGINT", "note": "1 when the call site is in a tests/** project" },
			      { "name": "gen", "type": "BIGINT", "note": "1 when the call site is in source-generator output (Razor, LoggerMessage, Regex, OpenApi)" },
			      { "name": "ext", "type": "BIGINT", "note": "1 when the callee is declared outside the PetBox* assemblies" },
			      { "name": "constructed", "type": "BIGINT", "note": "1 when the call site used a constructed generic / reduced extension form" }
			    ] },
			    "nodes": { "file": "call-nodes.parquet", "columns": [
			      { "name": "key", "type": "VARCHAR", "note": "join key; unique" },
			      { "name": "display", "type": "VARCHAR" },
			      { "name": "kind", "type": "VARCHAR", "note": "ISymbol.Kind" },
			      { "name": "method_kind", "type": "VARCHAR", "note": "IMethodSymbol.MethodKind, empty for non-methods" },
			      { "name": "asm", "type": "VARCHAR" },
			      { "name": "ext", "type": "BIGINT" },
			      { "name": "gen", "type": "BIGINT" },
			      { "name": "tests", "type": "BIGINT" },
			      { "name": "file", "type": "VARCHAR" },
			      { "name": "line", "type": "BIGINT" }
			    ] }
			  },
			  "keyCaveats": [
			    "Local functions have no documentation comment id; they are keyed 'L:<container doc-id>#<name>'.",
			    "Lambdas are never keyed: calls inside them are attributed to the enclosing member with via='lambda'.",
			    "Anonymous-type members DO return a doc id, but a containerless and therefore COLLIDING one ('M:.get_Key'). Treat keys matching '^[MPT]:\\.' as unstable."
			  ],

			""");
		json.Append(CultureInfo.InvariantCulture, $"  \"totals\": {{ \"edges\": {edgeRows}, \"nodes\": {nodeSeen.Count}, \"constructedGeneric\": {constructedEdges}, \"unattributedCallSites\": {unattributed} }},\n");
		json.Append("  \"edgeKinds\": [\n");
		var first = true;
		foreach (var k in kindCount.OrderByDescending(k => k.Value))
		{
			if (!first) json.Append(",\n");
			first = false;
			json.Append(CultureInfo.InvariantCulture,
				$"    {{ \"kind\": \"{k.Key}\", \"count\": {k.Value}, \"fromTests\": {kindTests.GetValueOrDefault(k.Key)}, " +
				$"\"fromGeneratedCode\": {kindGen.GetValueOrDefault(k.Key)}, \"externalCallee\": {kindExt.GetValueOrDefault(k.Key)}, " +
				$"\"description\": \"{Describe(k.Key)}\" }}");
		}
		json.Append("\n  ],\n  \"candidateReasons\": [\n");
		first = true;
		foreach (var r in reasonCount.OrderByDescending(k => k.Value))
		{
			if (!first) json.Append(",\n");
			first = false;
			json.Append(CultureInfo.InvariantCulture, $"    {{ \"reason\": \"{r.Key}\", \"count\": {r.Value} }}");
		}
		json.Append("\n  ],\n  \"callSiteContext\": [\n");
		first = true;
		foreach (var v in viaCount.OrderByDescending(k => k.Value))
		{
			if (!first) json.Append(",\n");
			first = false;
			json.Append(CultureInfo.InvariantCulture, $"    {{ \"via\": \"{v.Key}\", \"count\": {v.Value} }}");
		}
		json.Append(CultureInfo.InvariantCulture,
			$"\n  ],\n  \"keys\": {{ \"scheme\": \"ISymbol.GetDocumentationCommentId()\", \"withDocId\": {keyStats.WithDocId}, \"fallback\": {keyStats.Fallback}, \"fallbackByKind\": {{");
		first = true;
		foreach (var k in keyStats.FallbackByKind.OrderByDescending(k => k.Value))
		{
			if (!first) json.Append(',');
			first = false;
			json.Append(CultureInfo.InvariantCulture, $" \"{k.Key}\": {k.Value}");
		}
		json.Append(" } }\n}\n");
		var kindsPath = Path.Combine(outDir, "edge-kinds.json");
		await File.WriteAllTextAsync(kindsPath, json.ToString()).ConfigureAwait(false);
		say($"wrote {kindsPath}");
		say($"wrote {edgesPath}");
		say($"wrote {nodesPath}");
	}

	// ── helpers ─────────────────────────────────────────────────────────────────────────────

	private static string Describe(string kind) => kind switch
	{
		"call.direct" => "non-virtual invocation; the callee symbol is the one that runs",
		"call.virtual" => "invocation of a virtual/abstract/override member on a class; edge points at the DECLARED member",
		"call.interface" => "invocation through an interface member; edge points at the DECLARED interface member",
		"call.delegate" => "invocation through a delegate variable; callee is the delegate type's Invoke",
		"call.localfunction" => "invocation of a local function",
		"call.ambiguous" => "SymbolInfo.Symbol was null; one edge per CandidateSymbol, see candidateReasons",
		"ref.ambiguous" => "same, at a non-invocation reference site",
		"methodgroup" => "method named without invoking it (method group conversion / delegate creation)",
		"ctor" => "explicit `new T(...)`",
		"ctor.implicit-new" => "target-typed `new()`",
		"ctor.attribute" => "attribute application; callee is the attribute constructor",
		"ctor.chain" => "`: base(...)` / `: this(...)`",
		"ctor.implicit-base" => "synthesised: constructor with no initializer implicitly calls base()",
		"prop.get" => "property read; callee is the get accessor",
		"prop.set" => "property write; callee is the set accessor",
		"indexer.get" => "indexer read",
		"indexer.set" => "indexer write",
		"event" => "event reference (add/remove/raise)",
		"impl.interface" => "declared interface member -> its implementation; NOT a call site, a dispatch target",
		"impl.override" => "overridden member -> the override; NOT a call site, a dispatch target",
		_ => "",
	};

	private static string InvocationKind(IMethodSymbol m)
	{
		if (m.MethodKind == MethodKind.DelegateInvoke) return "call.delegate";
		if (m.MethodKind == MethodKind.LocalFunction) return "call.localfunction";
		if (m.ContainingType?.TypeKind == TypeKind.Interface) return "call.interface";
		if (m.IsVirtual || m.IsAbstract || m.IsOverride) return "call.virtual";
		return "call.direct";
	}

	private static bool IsConsumedByParent(SyntaxNode name)
	{
		var e = (SyntaxNode)name;
		if (e.Parent is MemberAccessExpressionSyntax ma && ma.Name == e) e = ma;
		else if (e.Parent is MemberBindingExpressionSyntax mb && mb.Name == e) e = mb;
		else if (e.Parent is QualifiedNameSyntax qn && qn.Right == e) e = qn;
		return e.Parent switch
		{
			InvocationExpressionSyntax inv when inv.Expression == e => true,
			ObjectCreationExpressionSyntax oc when oc.Type == e => true,
			AttributeSyntax at when at.Name == e => true,
			QualifiedNameSyntax => true,
			_ => false,
		};
	}

	private static (bool Read, bool Write) ReadWrite(SyntaxNode name)
	{
		var e = (SyntaxNode)name;
		while (true)
		{
			if (e.Parent is MemberAccessExpressionSyntax ma && ma.Name == e) { e = ma; continue; }
			if (e.Parent is MemberBindingExpressionSyntax mb && mb.Name == e) { e = mb; continue; }
			break;
		}
		switch (e.Parent)
		{
			case AssignmentExpressionSyntax a when a.Left == e:
				return a.IsKind(SyntaxKind.SimpleAssignmentExpression) ? (false, true) : (true, true);
			case PrefixUnaryExpressionSyntax p when p.IsKind(SyntaxKind.PreIncrementExpression) || p.IsKind(SyntaxKind.PreDecrementExpression):
				return (true, true);
			case PostfixUnaryExpressionSyntax:
				return (true, true);
			case ArgumentSyntax arg when arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword):
				return (false, true);
			case ArgumentSyntax arg2 when arg2.RefKindKeyword.IsKind(SyntaxKind.RefKeyword):
				return (true, true);
			default:
				return (true, false);
		}
	}

	/// <summary>The member a call site is attributed to. Local functions are their own caller
	/// (so member -> local fn -> target stays a chain); lambdas are not (they have no stable key),
	/// so their calls land on the enclosing member with via="lambda".</summary>
	private static ISymbol? Caller(SemanticModel model, Compilation comp, SyntaxNode node, out string via)
	{
		via = "";
		for (var n = node.Parent; n is not null; n = n.Parent)
		{
			switch (n)
			{
				case AnonymousFunctionExpressionSyntax:
					if (via.Length == 0) via = "lambda";
					continue;
				case LocalFunctionStatementSyntax lf:
					if (ModelExtensions.GetDeclaredSymbol(model, lf) is { } lfs) return lfs;
					continue;
				case AccessorDeclarationSyntax acc:
					return ModelExtensions.GetDeclaredSymbol(model, acc);
				case ArrowExpressionClauseSyntax when n.Parent is PropertyDeclarationSyntax or IndexerDeclarationSyntax:
				{
					var s = ModelExtensions.GetDeclaredSymbol(model, n.Parent!);
					return s is IPropertySymbol ps ? (ISymbol?)ps.GetMethod ?? ps : s;
				}
				case VariableDeclaratorSyntax vd when vd.Parent?.Parent is FieldDeclarationSyntax or EventFieldDeclarationSyntax:
					return ModelExtensions.GetDeclaredSymbol(model, vd);
				case EnumMemberDeclarationSyntax em:
					return ModelExtensions.GetDeclaredSymbol(model, em);
				case GlobalStatementSyntax:
					return comp.GetEntryPoint(CancellationToken.None);
				case BaseTypeDeclarationSyntax btd:
					// Only reachable from an attribute on the type or a primary-constructor base list.
					return ModelExtensions.GetDeclaredSymbol(model, btd);
				case MemberDeclarationSyntax md:
					return ModelExtensions.GetDeclaredSymbol(model, md);
				case CompilationUnitSyntax:
					return comp.GetEntryPoint(CancellationToken.None);
			}
		}
		return null;
	}

	/// <summary>Where an unattributable node sat, so "7955 sites had no caller" is diagnosable
	/// instead of being a silent hole.</summary>
	private static string TopContext(SyntaxNode node)
	{
		for (var n = node.Parent; n is not null; n = n.Parent)
			if (n is UsingDirectiveSyntax or AttributeListSyntax or BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
				return n.Kind().ToString();
		return "unknown";
	}

	private static ISymbol Canon(ISymbol s)
	{
		if (s is IMethodSymbol m && m.ReducedFrom is { } rf) return rf.OriginalDefinition;
		return s.OriginalDefinition;
	}

	private static string Key(ISymbol s, KeyStats st)
	{
		if (s is IMethodSymbol { MethodKind: MethodKind.LocalFunction } lf)
		{
			st.Fallback++;
			st.FallbackByKind["Method/LocalFunction"] = st.FallbackByKind.GetValueOrDefault("Method/LocalFunction") + 1;
			return "L:" + Key(lf.ContainingSymbol, st) + "#" + lf.Name;
		}
		var id = s.GetDocumentationCommentId();
		if (!string.IsNullOrEmpty(id) && id[0] != '!')
		{
			st.WithDocId++;
			return id;
		}
		st.Fallback++;
		var bucket = s is IMethodSymbol mm ? $"{s.Kind}/{mm.MethodKind}" : s.Kind.ToString();
		st.FallbackByKind[bucket] = st.FallbackByKind.GetValueOrDefault(bucket) + 1;
		return "X:" + s.ToDisplayString(FqFormat) + "|" + bucket;
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

	private static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

	private static string Rel(string path, string root)
	{
		if (string.IsNullOrEmpty(path)) return "";
		var p = path.Replace('\\', '/');
		var r = root.Replace('\\', '/').TrimEnd('/') + "/";
		return p.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? p[r.Length..] : p;
	}
}

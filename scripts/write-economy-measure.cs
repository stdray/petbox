// Write-economy synthetic measurement for work/write-economy-measure-existing-text.
//
// WHY SYNTHETIC, NOT LOG-MINED: the idea's own falsification criterion (blob-upload-then-
// reference-in-mcp) proposed mining PetBox.Mcp.ToolCalls for "did this body already exist
// outside model output". That premise is false — McpTracingFilter's privacy contract (spec
// trace-mcp-call-shape) logs sizes/shapers only, never content, by design (work
// toolcalls-log-params). SessionMessage.Content is real text, but it is only what an agent
// explicitly chose to log, not an automatic capture of MCP call arguments — not a census.
// Prior TASK/COMMENT body revisions ARE retained on disk (TemporalStore is SCD-2, append-
// only, hard-deleted only on board deletion — src/PetBox.Core/Data/Temporal/TemporalStore.cs),
// but no MCP verb this agent can call exposes that history: data_query/data_exec (DataTools.cs)
// are scoped to the separate per-project "DataDb" sandbox catalog (DataDbFactory, its own
// baseDir/{projectKey}/{dbName}.db tree) which has nothing to do with the internal TasksDb
// file; tasks_delta/comments_delta return only the CURRENT active body of a changed node, never
// a prior revision. So retention is real but not reachable from here — hence synthetic.
//
// METHOD: fixtures/*.md are REAL bodies pulled live from the $system project's ideas/work/spec
// boards + one artifact:verdict comment (see fixtures/manifest.json for board/key/version/date),
// not lorem ipsum. For each fixture (the "AFTER" state of a write) we construct a plausible
// "BEFORE" state for four realistic write shapes and measure how many bytes the MODEL must
// EMIT in the tool-call argument under three transports:
//   - full-replace   (today's mechanism: body/text carries the WHOLE new value, always)
//   - fragment-patch (hypothetical work/write-fragment-patch: only the changed fragment)
//   - by-reference   (this idea's bodyRef: costs the model nothing ONLY for the portion of
//                     the body whose bytes already existed OUTSIDE model output at call time
//                     -- e.g. pasted external file/log/diff content. Per the idea's own
//                     analysis, content the model composes costs the SAME whether it lands in
//                     the tool argument or in a Write(file) call that is then referenced, so
//                     by-reference saves nothing on model-authored text.)
// Each fixture is manually classified for "externalContentChars" (bytes that are a verbatim
// external artifact — log/diff/command-output/file-dump — rather than model-composed prose).
// That classification is a judgment call made while reading each body during recon; a re-run
// should redo it independently rather than trust these numbers blindly.
//
// Cost unit: raw UTF-8 bytes (not chars) — matches what the server actually measures
// (McpWireBodyMeasurementMiddleware / ModuleMcp.SizeWarningOrNull compares wire bytes to raw
// UTF-8 bytes) and what the canon instructs callers to send (raw UTF-8, never \uXXXX-escaped).
//
// Usage: dotnet run scripts/write-economy-measure.cs [--threshold N]   (default threshold 2000)

using System.Globalization;
using System.Text;

var thresholdBytes = 2000;
for (var i = 0; i < args.Length; i++)
	if (args[i] == "--threshold" && i + 1 < args.Length) thresholdBytes = int.Parse(args[++i], CultureInfo.InvariantCulture);

var fixturesDir = Path.Combine(FindRepoScriptsDir(), "write-economy-fixtures");

string FindRepoScriptsDir()
{
	// Walk up from cwd looking for scripts/write-economy-fixtures (works whether `dotnet run`
	// is invoked from the repo root or from within scripts/).
	for (var d = Directory.GetCurrentDirectory(); d is not null; d = Directory.GetParent(d)?.FullName)
	{
		var candidate = Path.Combine(d, "scripts", "write-economy-fixtures");
		if (Directory.Exists(candidate)) return Path.Combine(d, "scripts");
	}
	// Fallback: resolve relative to this script's own source file.
	return Path.GetDirectoryName(Path.GetFullPath("scripts/write-economy-measure.cs"))
		?? Directory.GetCurrentDirectory();
}

var files = Directory.GetFiles(fixturesDir, "*.md").OrderBy(f => f, StringComparer.Ordinal).ToArray();
if (files.Length == 0)
{
	Console.Error.WriteLine($"No fixtures found under {fixturesDir}");
	return 1;
}

// Manual classification: externalContentChars per fixture (0 = fully model-authored prose —
// true for all 10 fixtures in this sample; kept as a field, not a constant, so a future fixture
// that DOES paste external content is handled without changing the shape of the code).
var externalContent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
{
	// none of the sampled bodies contain a verbatim external artifact (log dump, diff, raw
	// command output) — every one is structured analytical prose the model composed. See
	// the report posted to the card for the read-through that established this.
};

var rows = new List<Row>();

foreach (var file in files)
{
	var name = Path.GetFileNameWithoutExtension(file);
	var after = File.ReadAllText(file).TrimEnd('\n', '\r');
	var afterBytes = Utf8Len(after);
	var extBytes = externalContent.GetValueOrDefault(name, 0);

	foreach (var shape in Shapes(after))
		rows.Add(Measure(name, afterBytes, extBytes, shape));
}

// ---- report ----
Console.WriteLine("shape,fixture,afterBytes,fullReplaceBytes,fragmentPatchBytes,byReferenceBytes,fragmentSavingsPct,byRefSavingsPct");
foreach (var r in rows)
	Console.WriteLine($"{r.Shape},{r.Fixture},{r.AfterBytes},{r.FullReplaceBytes},{r.FragmentPatchBytes},{r.ByReferenceBytes},{r.FragmentSavingsPct:0.0},{r.ByRefSavingsPct:0.0}");

Console.WriteLine();
Console.WriteLine($"=== Large-body subset (afterBytes >= {thresholdBytes}) ===");
var large = rows.Where(r => r.AfterBytes >= thresholdBytes).ToList();
Console.WriteLine($"n = {large.Count} of {rows.Count} (fixture x shape rows)");

void ReportDist(string label, IEnumerable<double> vals)
{
	var v = vals.OrderBy(x => x).ToArray();
	if (v.Length == 0) { Console.WriteLine($"{label}: n=0"); return; }
	double P(double p) => v[Math.Clamp((int)Math.Round(p * (v.Length - 1)), 0, v.Length - 1)];
	Console.WriteLine($"{label}: n={v.Length} min={v[0]:0.0} p50={P(0.5):0.0} p90={P(0.9):0.0} max={v[^1]:0.0} mean={v.Average():0.0}");
}

Console.WriteLine("\n-- fragment-patch savings % vs full-replace, BY SHAPE (large-body subset) --");
foreach (var g in large.GroupBy(r => r.Shape))
	ReportDist(g.Key, g.Select(r => r.FragmentSavingsPct));

Console.WriteLine("\n-- by-reference savings % vs full-replace, BY SHAPE (large-body subset) --");
foreach (var g in large.GroupBy(r => r.Shape))
	ReportDist(g.Key, g.Select(r => r.ByRefSavingsPct));

Console.WriteLine("\n-- overall (all shapes pooled, large-body subset) --");
ReportDist("fragment-patch savings %", large.Select(r => r.FragmentSavingsPct));
ReportDist("by-reference savings %", large.Select(r => r.ByRefSavingsPct));

return 0;

static int Utf8Len(string s) => Encoding.UTF8.GetByteCount(s);

static IEnumerable<(string Shape, string Before, string Changed)> Shapes(string after)
{
	// 1. create-from-scratch: nothing carries over.
	yield return ("create-from-scratch", "", after);

	// 2. edit-one-paragraph: pick the paragraph nearest the middle (by \n\n split), treat it
	//    as the text the model produced THIS turn; everything else is unchanged carryover.
	var paras = after.Split("\n\n", StringSplitOptions.None);
	if (paras.Length >= 3)
	{
		var mid = paras.Length / 2;
		var changed = paras[mid];
		var before = string.Join("\n\n", paras.Where((_, i) => i != mid));
		yield return ("edit-one-paragraph", before, changed);
	}

	// 3. append-a-section: split on markdown "## " headers; the LAST section is what got
	//    appended this turn.
	var sections = SplitSections(after);
	if (sections.Count >= 2)
	{
		var appended = sections[^1];
		var before = string.Concat(sections.Take(sections.Count - 1));
		yield return ("append-a-section", before, appended);
	}

	// 4. full-rewrite: an existing large node gets materially rewritten — no carryover
	//    survives recognizably, so treat it like create-from-scratch for byte purposes, but
	//    keep it a distinct shape because the mechanism verdict differs (see report): this is
	//    the case where NONE of full-replace/fragment-patch/by-reference can help, because the
	//    node already existed (so it isn't "new"), yet nothing old survives (so a patch/ref
	//    has nothing to anchor to).
	yield return ("full-rewrite", "", after);
}

static List<string> SplitSections(string body)
{
	var idx = new List<int>();
	var pos = 0;
	while (true)
	{
		var i = body.IndexOf("\n## ", pos, StringComparison.Ordinal);
		if (i < 0) break;
		idx.Add(i + 1);
		pos = i + 4;
	}
	if (idx.Count == 0) return [body];
	var parts = new List<string>();
	var start = 0;
	foreach (var i in idx) { parts.Add(body[start..i]); start = i; }
	parts.Add(body[start..]);
	return parts.Where(p => p.Length > 0).ToList();
}

Row Measure(string fixture, int afterBytesOuter, int extBytes, (string Shape, string Before, string Changed) s)
{
	var afterBytes = afterBytesOuter;
	var changedBytes = Utf8Len(s.Changed);
	var fullReplace = afterBytes; // current mechanism: whole new body, every time
	var fragmentPatch = changedBytes; // hypothetical write-fragment-patch: only the delta
	// by-reference: saves bytes only on the portion that is externally-sourced. For the
	// "changed" fragment specifically (the part authored/produced THIS turn), by-reference
	// saves nothing unless that fragment itself is external content (bounded by extBytes,
	// which in this sample is 0 for every fixture — all sampled text is model-composed prose).
	var byRefSavableInChanged = Math.Min(extBytes, changedBytes);
	var byReference = fullReplace - byRefSavableInChanged;

	double Pct(int out_) => fullReplace == 0 ? 0 : 100.0 * (fullReplace - out_) / fullReplace;

	return new Row(s.Shape, fixture, afterBytes, fullReplace, fragmentPatch, byReference, Pct(fragmentPatch), Pct(byReference));
}

sealed record Row(string Shape, string Fixture, int AfterBytes, int FullReplaceBytes, int FragmentPatchBytes, int ByReferenceBytes, double FragmentSavingsPct, double ByRefSavingsPct);

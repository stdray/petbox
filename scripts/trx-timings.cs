// TRX timing profiler — the analysis `dotnet trx` (verdicts) does not do: per-class
// duration aggregation and the wall-clock critical path of a (parallel) test run.
// Usage:
//   dotnet run scripts/trx-timings.cs                              # newest *.trx under cwd
//   dotnet run scripts/trx-timings.cs -- path/to/run.trx [--top 20]
// Reads: total window (first start → last end), busiest classes (Σ test durations),
// and the timeline tail — classes that finish last define what to optimize next.

using System.Globalization;
using System.Xml.Linq;

var top = 15;
string? target = null;
for (var i = 0; i < args.Length; i++)
{
	if (args[i] == "--top" && i + 1 < args.Length) top = int.Parse(args[++i], CultureInfo.InvariantCulture);
	else target = args[i];
}

var trxPath = target switch
{
	null => NewestTrx(Directory.GetCurrentDirectory()),
	_ when Directory.Exists(target) => NewestTrx(target),
	_ => target,
};
if (trxPath is null || !File.Exists(trxPath))
{
	Console.Error.WriteLine("No .trx found. Pass a file/dir or run from a dir containing TestResults.");
	return 1;
}
Console.WriteLine($"trx: {trxPath}");

XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
var doc = XDocument.Load(trxPath);

var classByTestId = doc.Descendants(ns + "UnitTest").ToDictionary(
	u => (string)u.Attribute("id")!,
	u => ((string)u.Element(ns + "TestMethod")!.Attribute("className")!).Split(',')[0]);

var rows = doc.Descendants(ns + "UnitTestResult")
	.Where(r => r.Attribute("startTime") is not null && r.Attribute("endTime") is not null)
	.Select(r => (
		Cls: classByTestId.GetValueOrDefault((string)r.Attribute("testId")!, "?"),
		Start: DateTimeOffset.Parse((string)r.Attribute("startTime")!, CultureInfo.InvariantCulture),
		End: DateTimeOffset.Parse((string)r.Attribute("endTime")!, CultureInfo.InvariantCulture)))
	.ToList();
if (rows.Count == 0)
{
	Console.Error.WriteLine("No timed results in the trx.");
	return 1;
}

var t0 = rows.Min(r => r.Start);
var t1 = rows.Max(r => r.End);
var busySum = rows.Sum(r => (r.End - r.Start).TotalSeconds);
Console.WriteLine($"window: {(t1 - t0).TotalSeconds:F0}s wall, {rows.Count} results, Σ test durations {busySum:F0}s");

var byClass = rows.GroupBy(r => r.Cls).Select(g => (
	Cls: g.Key,
	N: g.Count(),
	Busy: g.Sum(r => (r.End - r.Start).TotalSeconds),
	First: g.Min(r => r.Start),
	Last: g.Max(r => r.End))).ToList();

Console.WriteLine($"\n-- top {top} classes by busy time (Σ test durations incl. per-test setup) --");
Console.WriteLine($"{"class",-64}{"n",4}{"busy_s",9}");
foreach (var c in byClass.OrderByDescending(c => c.Busy).Take(top))
	Console.WriteLine($"{Trim(c.Cls, 63),-64}{c.N,4}{c.Busy,9:F1}");

Console.WriteLine($"\n-- timeline tail: last {top} classes to finish (the wall-clock critical path) --");
Console.WriteLine($"{"class",-64}{"ends@s",7}{"span_s",9}{"busy_s",9}{"n",4}");
foreach (var c in byClass.OrderBy(c => c.Last).TakeLast(top))
	Console.WriteLine($"{Trim(c.Cls, 63),-64}{(c.Last - t0).TotalSeconds,7:F0}{(c.Last - c.First).TotalSeconds,9:F1}{c.Busy,9:F1}{c.N,4}");

return 0;

static string? NewestTrx(string dir) =>
	Directory.EnumerateFiles(dir, "*.trx", SearchOption.AllDirectories)
		.OrderByDescending(File.GetLastWriteTimeUtc)
		.FirstOrDefault();

static string Trim(string s, int max) => s.Length <= max ? s : "…" + s[^(max - 1)..];

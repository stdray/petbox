using System.Diagnostics;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;

MSBuildLocator.RegisterDefaults();

var sln = args[0];
var redirectRoot = args[1]; // e.g. C:\temp\redirect-obj

var props = new Dictionary<string, string>
{
    ["BaseIntermediateOutputPath"] = Path.Combine(redirectRoot, "obj") + Path.DirectorySeparatorChar,
    ["BaseOutputPath"] = Path.Combine(redirectRoot, "bin") + Path.DirectorySeparatorChar,
};

var ws = MSBuildWorkspace.Create(props);
var failures = new List<string>();
ws.WorkspaceFailed += (_, e) => failures.Add($"{e.Diagnostic.Kind}: {e.Diagnostic.Message}");

var sw = Stopwatch.StartNew();
var solution = await ws.OpenSolutionAsync(sln);
sw.Stop();

Console.WriteLine($"MEASURE open_with_redirected_obj = {sw.Elapsed.TotalSeconds:F2}s");
Console.WriteLine($"projects = {solution.ProjectIds.Count}; documents = {solution.Projects.Sum(p => p.DocumentIds.Count)}");
Console.WriteLine($"workspace.Diagnostics = {ws.Diagnostics.Count}");
foreach (var f in failures.Take(20)) Console.WriteLine("FAILURE: " + f);

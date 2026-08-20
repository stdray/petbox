using KustoLoco.Core;

var methods = new List<MethodRow>
{
    new("A", "T1", true),
    new("B", "T1", false),
    new("C", "T2", true),
    new("D", "T2", false),
};
var calls = new List<CallRow>
{
    new("A", "B"),
    new("B", "C"),
    new("C", "D"),
};

async Task Run(string label, string kql)
{
    var ctx = new KustoQueryContext();
    ctx.CopyDataIntoTable("methods", methods);
    ctx.CopyDataIntoTable("calls", calls);
    Console.WriteLine("=== " + label);
    Console.WriteLine("    " + kql);
    var result = await ctx.RunQuery(kql);
    if (!string.IsNullOrEmpty(result.Error))
    {
        Console.WriteLine("    ERROR: " + result.Error);
        return;
    }
    var cols = result.ColumnNames().ToList();
    Console.WriteLine("    columns: " + string.Join(",", cols));
    foreach (var row in result.EnumerateRows())
        Console.WriteLine("    row: " + string.Join(",", row.Select(v => v?.ToString() ?? "null")));
}

await Run("where", "methods | where HasAttr == true");
await Run("union", "methods | where ParentType == 'T1' | union (methods | where ParentType == 'T2')");
await Run("summarize", "methods | summarize count() by ParentType");
await Run("join", "methods | join kind=inner (calls) on $left.Name == $right.Src");

await Run("unknown column in where", "methods | where TypeCa == true");
await Run("unknown column in project", "methods | project TypeCa");

await Run("make-graph with node table", "calls | make-graph Src --> Dst with methods on Name | graph-match (a)-[e*1..3]->(b) where a.Name == 'A' project b.Name");

record MethodRow(string Name, string ParentType, bool HasAttr);
record CallRow(string Src, string Dst);

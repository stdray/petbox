using System.Diagnostics;
using KustoLoco.Core;
using Microsoft.Data.Sqlite;

var dbPath = args.Length > 0 ? args[0] : @"C:\Users\stdray\tools\codegraph-research\measurements\workerE\codegraph.db";

var rows = new List<EdgeRow>(600_000);
var tRead = Stopwatch.StartNew();
using (var cn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
{
    cn.Open();
    using var cmd = cn.CreateCommand();
    cmd.CommandText = "SELECT file, line, sym, kind FROM edge";
    using var rd = cmd.ExecuteReader();
    while (rd.Read())
        rows.Add(new EdgeRow(rd.GetString(0), rd.GetInt32(1), rd.GetString(2), rd.GetString(3)));
}
tRead.Stop();
Console.WriteLine($"MEASURE sqlite_read rows={rows.Count} time={tRead.Elapsed.TotalSeconds:F2}s");

var ctx = new KustoQueryContext();
var tMat = Stopwatch.StartNew();
ctx.CopyDataIntoTable("edge", rows);
tMat.Stop();
Console.WriteLine($"MEASURE kusto_copy_data_into_table rows={rows.Count} time={tMat.Elapsed.TotalSeconds:F2}s");

var tQ = Stopwatch.StartNew();
var r1 = await ctx.RunQuery("edge | where Sym startswith 'System.Security.Claims.ClaimsPrincipal' | count");
var r2 = await ctx.RunQuery("edge | where Sym startswith 'PetBox.Core.Auth.TenantFromAttribute' | count");
tQ.Stop();
string Fmt(KustoQueryResult r) => string.IsNullOrEmpty(r.Error)
    ? string.Join(",", r.EnumerateRows().Select(row => string.Join("|", row.Select(v => v?.ToString() ?? "null"))))
    : "ERROR:" + r.Error;
Console.WriteLine($"MEASURE kusto_query_D2_where_union time={tQ.Elapsed.TotalSeconds:F3}s r1=[{Fmt(r1)}] r2=[{Fmt(r2)}]");

var proc = Process.GetCurrentProcess();
proc.Refresh();
Console.WriteLine($"PeakWorkingSet = {proc.PeakWorkingSet64 / 1024.0 / 1024.0:F1} MB");

record EdgeRow(string File, int Line, string Sym, string Kind);

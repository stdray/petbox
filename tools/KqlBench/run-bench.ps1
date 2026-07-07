#!/usr/bin/env pwsh
# Driver for the KqlBench isolated-process runner. Spawns KqlBench once per (backend x size) so each
# backend's peak working set is measured in its OWN process (DuckDB's native memory lives outside the
# .NET GC heap), parses the one JSON line each prints, and assembles the SQLite-vs-DuckDB ratio table.
#
#   pwsh tools/KqlBench/run-bench.ps1                      # default sizes 10000,100000,1000000
#   pwsh tools/KqlBench/run-bench.ps1 -Sizes 10000,100000 # custom sizes
#   pwsh tools/KqlBench/run-bench.ps1 -Configuration Debug -NoBuild
[CmdletBinding()]
param(
	[int[]] $Sizes = @(10000, 100000, 1000000),
	[string[]] $Backends = @('sqlite', 'duckdb'),
	[string] $Configuration = 'Release',
	[int] $Warmup = 2,
	[int] $Measured = 5,
	[switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$projDir = $PSScriptRoot
$proj = Join-Path $projDir 'KqlBench.csproj'

if (-not $NoBuild) {
	Write-Host "==> building KqlBench ($Configuration)" -ForegroundColor Cyan
	dotnet build $proj -c $Configuration --nologo | Out-Host
	if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

$dll = Join-Path $projDir "bin/$Configuration/net10.0/KqlBench.dll"
if (-not (Test-Path $dll)) { throw "runner not found: $dll (build first)" }

$results = @()
foreach ($size in $Sizes) {
	foreach ($backend in $Backends) {
		Write-Host "==> $backend @ $size" -ForegroundColor Cyan
		$line = & dotnet $dll $backend $size $Warmup $Measured
		if ($LASTEXITCODE -ne 0) { throw "$backend @ $size exited $LASTEXITCODE" }
		# The runner prints exactly one JSON line on stdout; take the last non-empty line to be safe.
		$json = ($line | Where-Object { $_ -match '^\s*\{' } | Select-Object -Last 1)
		if (-not $json) { throw "no JSON from $backend @ $size; got: $line" }
		$results += ($json | ConvertFrom-Json)
	}
}

$outFile = Join-Path $projDir 'bench-results.jsonl'
$results | ForEach-Object { $_ | ConvertTo-Json -Depth 8 -Compress } | Set-Content -Path $outFile
Write-Host "`nraw JSON written to $outFile" -ForegroundColor DarkGray

function Get-Row($backend, $size) { $results | Where-Object { $_.backend -eq $backend -and $_.size -eq $size } | Select-Object -First 1 }
function Ratio($duck, $sqlite) { if ($sqlite -eq 0) { return 'n/a' } return ('{0:N2}x' -f ($duck / $sqlite)) }
function MiB($b) { '{0:N1}' -f ($b / 1MB) }

Write-Host "`n================ SQLite vs DuckDB — per size ================" -ForegroundColor Green
foreach ($size in $Sizes) {
	$s = Get-Row 'sqlite' $size
	$d = Get-Row 'duckdb' $size
	if (-not $s -or -not $d) { continue }
	Write-Host "`n---- size = $size ----" -ForegroundColor Yellow
	"{0,-26} {1,16} {2,16} {3,14}" -f 'axis', 'sqlite', 'duckdb', 'duck/sqlite' | Write-Host
	"{0,-26} {1,16} {2,16} {3,14}" -f 'peak RSS (MiB)', (MiB $s.peakWorkingSetBytes), (MiB $d.peakWorkingSetBytes), (Ratio $d.peakWorkingSetBytes $s.peakWorkingSetBytes) | Write-Host
	"{0,-26} {1,16} {2,16} {3,14}" -f 'on-disk (MiB)', (MiB $s.onDiskFileBytes), (MiB $d.onDiskFileBytes), (Ratio $d.onDiskFileBytes $s.onDiskFileBytes) | Write-Host
	"{0,-26} {1,16:N0} {2,16:N0} {3,14}" -f 'ingest rows/sec', $s.ingestRowsPerSec, $d.ingestRowsPerSec, (Ratio $d.ingestRowsPerSec $s.ingestRowsPerSec) | Write-Host
	"{0,-26} {1,16} {2,16} {3,14}" -f 'cold-start (ms)', ('{0:N1}' -f $s.coldStartMs), ('{0:N1}' -f $d.coldStartMs), (Ratio $d.coldStartMs $s.coldStartMs) | Write-Host
	"{0,-26} {1,16} {2,16} {3,14}" -f 'cold-start RSS (MiB)', (MiB $s.coldStartWorkingSetBytes), (MiB $d.coldStartWorkingSetBytes), (Ratio $d.coldStartWorkingSetBytes $s.coldStartWorkingSetBytes) | Write-Host

	Write-Host "  per-query p50 ms (rows):" -ForegroundColor DarkGray
	for ($i = 0; $i -lt $s.queries.Count; $i++) {
		$sq = $s.queries[$i]; $dq = $d.queries[$i]
		"{0,-26} {1,16} {2,16} {3,14}" -f $sq.Name, ('{0:N2} ({1})' -f $sq.P50Ms, $sq.RowCount), ('{0:N2} ({1})' -f $dq.P50Ms, $dq.RowCount), (Ratio $dq.P50Ms $sq.P50Ms) | Write-Host
	}
}
Write-Host ""

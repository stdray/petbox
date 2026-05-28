param(
	[string]$Target = "Default",
	[string]$Configuration = "Release"
)

$script = Join-Path $PSScriptRoot "build.cs"

dotnet tool restore
if ($LASTEXITCODE -ne 0) {
	exit $LASTEXITCODE
}

# Cake's GitVersionRunner probes PATH for dotnet-gitversion; local tools aren't on PATH.
if (-not (Get-Command dotnet-gitversion -ErrorAction SilentlyContinue)) {
	dotnet tool install --global GitVersion.Tool --version 6.4.0 2>$null
	if ($LASTEXITCODE -ne 0) { dotnet tool update --global GitVersion.Tool --version 6.4.0 2>$null }
}
$env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"

$arguments = @($script, "--target=$Target", "--configuration=$Configuration") + $args

dotnet run @arguments
exit $LASTEXITCODE

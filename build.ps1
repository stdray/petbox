param(
	[string]$Target = "Default",
	[string]$Configuration = "Release"
)

$script = Join-Path $PSScriptRoot "build.cake"

dotnet tool restore
if ($LASTEXITCODE -ne 0) {
	exit $LASTEXITCODE
}

$arguments = @($script, "--target=$Target", "--configuration=$Configuration") + $args

dotnet cake @arguments
exit $LASTEXITCODE

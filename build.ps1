param(
	[string]$Target = "Default",
	[string]$Configuration = "Release"
)

$script = Join-Path $PSScriptRoot "build.cs"

dotnet tool restore
if ($LASTEXITCODE -ne 0) {
	exit $LASTEXITCODE
}

$arguments = @($script, "--target=$Target", "--configuration=$Configuration") + $args

dotnet run @arguments
exit $LASTEXITCODE

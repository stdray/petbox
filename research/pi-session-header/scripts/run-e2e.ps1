<#
.SYNOPSIS
    e2e: настоящий pi против фейкового шлюза (fake-gateway.mjs), заголовки пишутся в лог.

.DESCRIPTION
    Поднимает ВРЕМЕННЫЙ PI_CODING_AGENT_DIR, в котором у встроенного провайдера
    opencode-go подменён baseUrl на локальный шлюз. Обёртка заголовков у встроенного
    провайдера сохраняется — на этом и держится проверка.

    .\run-e2e.ps1                      # базовый случай
    .\run-e2e.ps1 -Affinity            # + compat.sendSessionAffinityHeaders = true

.PARAMETER PiCli
    Путь к dist/cli.js пакета @earendil-works/pi-coding-agent.

.PARAMETER Port
    Порт фейкового шлюза. Должен совпадать с портом из baseUrl ниже.
#>
[CmdletBinding()]
param(
    [int]$Port = 8792,
    [string]$PiCli = "",
    [switch]$Affinity
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$work = Join-Path $env:TEMP ("pi-e2e-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$log = Join-Path $work "requests.log"
$models = if ($Affinity) {
    @'
{
  "providers": {
    "opencode-go": {
      "baseUrl": "http://127.0.0.1:__PORT__/v1",
      "models": [
        { "id": "deepseek-v4.1-flash", "compat": { "sendSessionAffinityHeaders": true } }
      ]
    }
  }
}
'@
} else {
    @'
{
  "providers": {
    "opencode-go": { "baseUrl": "http://127.0.0.1:__PORT__/v1" }
  }
}
'@
}

New-Item -ItemType Directory -Path $work -Force | Out-Null
$models.Replace("__PORT__", "$Port") | Set-Content -Path (Join-Path $work "models.json") -Encoding utf8
'{ "opencode-go": { "type": "api_key", "key": "dummy-key-e2e" } }' |
    Set-Content -Path (Join-Path $work "auth.json") -Encoding utf8
'{ "defaultProvider": "opencode-go", "defaultModel": "deepseek-v4.1-flash" }' |
    Set-Content -Path (Join-Path $work "settings.json") -Encoding utf8

if (-not $PiCli) {
    $candidate = Join-Path $env:APPDATA "npm/node_modules/@earendil-works/pi-coding-agent/dist/cli.js"
    if (-not (Test-Path $candidate)) {
        throw "pi CLI not found. Pass -PiCli <path to @earendil-works/pi-coding-agent/dist/cli.js>"
    }
    $PiCli = $candidate
}

$gateway = Start-Process node -ArgumentList @((Join-Path $here "fake-gateway.mjs"), $log, $Port) -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Seconds 2
    $env:PI_CODING_AGENT_DIR = $work
    $env:PI_OFFLINE = "1"
    node $PiCli --provider opencode-go --model deepseek-v4.1-flash --no-session -p "list the directory"
}
finally {
    Stop-Process -Id $gateway.Id -ErrorAction SilentlyContinue
}

Write-Host "`n--- captured requests ($log)"
Get-Content $log

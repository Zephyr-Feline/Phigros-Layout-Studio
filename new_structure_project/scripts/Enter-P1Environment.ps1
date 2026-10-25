param([string] $DotnetPath = 'dotnet')

# Dot-source this file. It uses an already installed SDK and changes only this process.
$ErrorActionPreference = 'Stop'
$plsSdkHost = (Get-Command $DotnetPath -CommandType Application -ErrorAction Stop).Source
$plsProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$plsRequiredSdk = (Get-Content -LiteralPath (Join-Path $plsProjectRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$env:DOTNET_ROOT = Split-Path -Parent $plsSdkHost
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
if (-not (($env:PATH -split ';') -contains $env:DOTNET_ROOT)) { $env:PATH = "$env:DOTNET_ROOT;$env:PATH" }
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
Push-Location $plsProjectRoot
try {
    $plsActualSdk = & $plsSdkHost --version
    if ($LASTEXITCODE -ne 0 -or $plsActualSdk -ne $plsRequiredSdk) {
        throw "The already-installed SDK $plsRequiredSdk is required; no download was attempted."
    }
    & $plsSdkHost --info
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the installed SDK.' }
} finally { Pop-Location }

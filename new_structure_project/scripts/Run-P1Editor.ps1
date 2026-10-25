$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Enter-P1Environment.ps1') | Out-Null
Push-Location $projectRoot
try {
    & (Join-Path $env:DOTNET_ROOT 'dotnet.exe') run --project '.\src\Editor.Desktop\Editor.Desktop.csproj' -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "P1 editor exited with code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

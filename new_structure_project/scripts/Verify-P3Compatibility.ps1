param(
    [Parameter(Mandatory = $true)] [string] $OutputRoot,
    [string] $DotnetPath = 'dotnet',
    [string] $LocalCorpusRoot
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputRoot)
if ($outputRoot.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $outputRoot.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must be outside the repository.'
}
if (Test-Path -LiteralPath $outputRoot) {
    if (-not (Test-Path -LiteralPath $outputRoot -PathType Container) -or
        (Get-ChildItem -LiteralPath $outputRoot -Force | Select-Object -First 1)) {
        throw 'OutputRoot must be new or empty; existing evidence is never overwritten.'
    }
} else { New-Item -ItemType Directory -Path $outputRoot | Out-Null }
. (Join-Path $PSScriptRoot 'SourceEvidence.ps1')
$summary = [ordered]@{
    Status = 'FAILED'; StartedUtc = [DateTime]::UtcNow.ToString('o'); FinishedUtc = $null
    Commit = $null; Branch = $null; Dirty = $null; Sdk = $null
    SourceManifestSha256 = $null; SourceFiles = $null; SourceUnchanged = $false
    ReferenceExecutableSha256 = $null; V2AssemblySha256 = $null
    LocalCorpus = $LocalCorpusRoot; Matrix = $null; VisualAcceptance = 'PENDING'
    LegacyWriterDecision = 'PENDING'; Commands = [Collections.Generic.List[object]]::new(); Error = $null
}
function Invoke-Evidence([string] $Executable, [string[]] $Arguments, [string] $LogName) {
    $start = [DateTime]::UtcNow
    $log = Join-Path $outputRoot $LogName
    Write-Host "Running $LogName ..."
    & $Executable @Arguments *> $log
    $code = $LASTEXITCODE
    $summary.Commands.Add([ordered]@{ Executable = $Executable; Arguments = $Arguments; ExitCode = $code
        DurationMs = [math]::Round(([DateTime]::UtcNow - $start).TotalMilliseconds); Log = $LogName })
    if ($code -ne 0) {
        Get-Content -LiteralPath $log -Tail 15 | Write-Host
        throw "$LogName failed with exit code $code."
    }
}
$before = $null
$oldDotnetRoot = $env:DOTNET_ROOT
$oldDotnetRootX64 = $env:DOTNET_ROOT_X64
Push-Location $projectRoot
try {
    $summary.Commit = (& git -C $repositoryRoot rev-parse HEAD)
    $summary.Branch = (& git -C $repositoryRoot branch --show-current)
    $status = @(& git -C $repositoryRoot status --porcelain=v1)
    $summary.Dirty = $status.Count -ne 0
    $status | Set-Content -LiteralPath (Join-Path $outputRoot 'git-status.txt') -Encoding utf8
    $before = @(Get-AcceptanceSourceManifest $repositoryRoot)
    $before | Set-Content -LiteralPath (Join-Path $outputRoot 'source-before.sha256') -Encoding utf8
    $summary.SourceFiles = $before.Count
    $summary.SourceManifestSha256 = Get-AcceptanceManifestHash $before
    if ($LocalCorpusRoot) {
        $LocalCorpusRoot = [IO.Path]::GetFullPath($LocalCorpusRoot)
        if (-not (Test-Path -LiteralPath $LocalCorpusRoot -PathType Container)) { throw 'Explicit local corpus is missing.' }
        $summary.LocalCorpus = $LocalCorpusRoot
    }
    $dotnet = (Get-Command $DotnetPath -ErrorAction Stop).Source
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $summary.Sdk = (& $dotnet --version)
    $required = (Get-Content -LiteralPath (Join-Path $projectRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    if ($LASTEXITCODE -ne 0 -or $summary.Sdk -ne $required) { throw "Already-installed SDK $required is required; no download attempted." }
    Invoke-Evidence $dotnet @('build', 'P1.slnx', '-c', 'Release', '--no-restore', '-warnaserror') 'v2-build.log'
    $referenceRoot = Join-Path $outputRoot 'independent-v1'
    New-Item -ItemType Directory -Path $referenceRoot | Out-Null
    $referenceExe = Join-Path $referenceRoot 'Phigros Preview Enhancer.exe'
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Installed .NET Framework compiler is required.' }
    $legacy = Join-Path $repositoryRoot 'EnhancerSource'
    $compile = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001', '/warnaserror+',
        "/out:$referenceExe", "/win32manifest:$(Join-Path $legacy 'app.manifest')", '/reference:System.Drawing.dll',
        '/reference:System.Windows.Forms.dll', '/reference:System.Xml.dll', '/reference:System.Core.dll')
    $compile += @('Enhancer.cs', 'Tests.cs', 'FreeLayout.cs', 'FreeTests.cs', 'GeometryTransforms.cs', 'AtomicFile.cs') |
        ForEach-Object { Join-Path $legacy $_ }
    Invoke-Evidence $compiler $compile 'v1-build.log'
    $assembly = Join-Path $projectRoot 'src/Editor.Desktop/bin/Release/net10.0-windows/Editor.Desktop.dll'
    $summary.ReferenceExecutableSha256 = (Get-FileHash -LiteralPath $referenceExe -Algorithm SHA256).Hash
    $summary.V2AssemblySha256 = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
    $matrixRoot = Join-Path $outputRoot 'matrix'
    $arguments = @($assembly, '--compat-evidence', $matrixRoot, $referenceExe)
    if ($LocalCorpusRoot) { $arguments += $LocalCorpusRoot }
    try { Invoke-Evidence $dotnet $arguments 'compatibility.log' }
    finally {
        $matrixFile = Join-Path $matrixRoot 'compatibility-evidence.json'
        if (Test-Path -LiteralPath $matrixFile) {
            $matrix = Get-Content -LiteralPath $matrixFile -Raw | ConvertFrom-Json
            $summary.Matrix = [ordered]@{ Status = $matrix.status; Synthetic = $matrix.synthetic; Local = $matrix.local
                Accepted = $matrix.accepted; Rejected = $matrix.rejected; TechnicalFailures = $matrix.technicalFailures
                ReportSha256 = (Get-FileHash -LiteralPath $matrixFile -Algorithm SHA256).Hash }
        }
    }
    if ($summary.Matrix.Status -ne 'PASS') { throw 'Compatibility matrix has incomplete or failed evidence.' }
    $summary.Status = 'PASS'
    Write-Host "P3 EVIDENCE PASS: $matrixRoot/index.html; visual acceptance and legacy Writer remain PENDING."
} catch {
    $summary.Error = $_.Exception.Message
    throw
} finally {
    $env:DOTNET_ROOT = $oldDotnetRoot
    $env:DOTNET_ROOT_X64 = $oldDotnetRootX64
    Pop-Location
    if ($null -ne $before) {
        $after = @(Get-AcceptanceSourceManifest $repositoryRoot)
        $after | Set-Content -LiteralPath (Join-Path $outputRoot 'source-after.sha256') -Encoding utf8
        $summary.SourceUnchanged = (($before -join "`n") -ceq ($after -join "`n"))
        if (-not $summary.SourceUnchanged) { $summary.Status = 'FAILED'; $summary.Error = 'Working-tree sources changed during verification.' }
    }
    $summary.FinishedUtc = [DateTime]::UtcNow.ToString('o')
    $summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $outputRoot 'p3-summary.json') -Encoding utf8
    if ($summary.Error -eq 'Working-tree sources changed during verification.') { throw $summary.Error }
}

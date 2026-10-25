param(
    [Parameter(Mandatory = $true)] [string] $OutputRoot,
    [string] $DotnetPath = 'dotnet',
    [string] $ReferenceImagePath,
    [switch] $IncludeLocalExtensions,
    [string] $LocalCorpusRoot,
    [string] $LocalTtfPath,
    [string] $LocalOtfPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$outputRoot = [IO.Path]::GetFullPath($OutputRoot)
if ($outputRoot.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $outputRoot.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must be outside the repository.'
}
if (Test-Path -LiteralPath $outputRoot) {
    if (-not (Test-Path -LiteralPath $outputRoot -PathType Container) -or
        (Get-ChildItem -LiteralPath $outputRoot -Force | Select-Object -First 1)) {
        throw 'OutputRoot must be new or empty; previous evidence is never overwritten.'
    }
} else { New-Item -ItemType Directory -Path $outputRoot | Out-Null }

. (Join-Path $PSScriptRoot 'SourceEvidence.ps1')
. (Join-Path $PSScriptRoot 'AcceptanceEvidence.ps1')
$summary = [ordered]@{
    EvidenceVersion = 1; Status = 'FAILED'; Scope = 'AUTOMATED_INTERNAL_ONLY'
    StartedUtc = [DateTime]::UtcNow.ToString('o'); FinishedUtc = $null
    Commit = $null; Branch = $null; Dirty = $null; SourceFiles = $null
    SourceManifestSha256 = $null; SourceUnchanged = $false; Evidence = $null
    Stages = [Collections.Generic.List[object]]::new(); Error = $null
    Acceptance = [ordered]@{ CleanMachine = 'PENDING'; WindowsHardware = 'PENDING'
        ChineseIme = 'PENDING'; DpiAndMonitors = 'PENDING'; P3Visual = 'PENDING'
        LegacyWriter = 'PENDING'; ThirdPartyNotices = 'PENDING'; ProjectLicense = 'PENDING'; Publication = 'PENDING' }
}
$before = $null
function Invoke-Gate([string] $Name, [string] $Script, [hashtable] $Parameters) {
    $stage = [ordered]@{ Name = $Name; Status = 'FAILED'; StartedUtc = [DateTime]::UtcNow.ToString('o')
        FinishedUtc = $null; Log = "$Name-driver.log"; Error = $null }
    $summary.Stages.Add($stage)
    Write-Host "Acceptance stage: $Name ..."
    try {
        & (Join-Path $PSScriptRoot $Script) @Parameters *> (Join-Path $outputRoot $stage.Log)
        if (($before -join "`n") -cne ((Get-AcceptanceSourceManifest $repositoryRoot) -join "`n")) {
            throw 'Working-tree sources changed between acceptance stages.'
        }
        $stage.Status = 'PASS'
    } catch { $stage.Error = $_.Exception.Message; throw }
    finally { $stage.FinishedUtc = [DateTime]::UtcNow.ToString('o') }
}
try {
    $summary.Commit = (& git -C $repositoryRoot rev-parse HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the Git commit.' }
    $summary.Branch = (& git -C $repositoryRoot branch --show-current)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the Git branch.' }
    $gitStatus = @(& git -C $repositoryRoot status --porcelain=v1)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the Git status.' }
    $summary.Dirty = $gitStatus.Count -ne 0
    $gitStatus | Set-Content -LiteralPath (Join-Path $outputRoot 'git-status.txt') -Encoding utf8
    $before = @(Get-AcceptanceSourceManifest $repositoryRoot)
    $before | Set-Content -LiteralPath (Join-Path $outputRoot 'source-before.sha256') -Encoding utf8
    $summary.SourceFiles = $before.Count
    $summary.SourceManifestSha256 = Get-AcceptanceManifestHash $before
    if (-not $IncludeLocalExtensions -and ($LocalCorpusRoot -or $LocalTtfPath -or $LocalOtfPath)) {
        throw 'Explicit local inputs require -IncludeLocalExtensions.'
    }
    # Resolve relative inputs before child scripts change their working directory.
    foreach ($inputName in @('ReferenceImagePath', 'LocalCorpusRoot', 'LocalTtfPath', 'LocalOtfPath')) {
        $value = Get-Variable -Name $inputName -ValueOnly
        if ($value) { Set-Variable -Name $inputName -Value ([IO.Path]::GetFullPath($value)) }
    }
    $dotnet = (Get-Command $DotnetPath -CommandType Application -ErrorAction Stop).Source
    Invoke-Gate 'script-tests' 'Test-AcceptanceEvidence.ps1' @{ OutputRoot = (Join-Path $outputRoot 'script-tests') }
    Invoke-Gate 'retention-tests' 'Test-TestArtifactRetention.ps1' @{ OutputRoot = (Join-Path $outputRoot 'retention-tests') }
    Invoke-Gate 'release-integrity-tests' 'Test-ReleasePackageIntegrity.ps1' @{ OutputRoot = (Join-Path $outputRoot 'release-integrity-tests') }
    $regressionParameters = @{ OutputRoot = (Join-Path $outputRoot 'regression'); DotnetPath = $dotnet
        IncludeLocalExtensions = $IncludeLocalExtensions }
    foreach ($inputName in @('ReferenceImagePath', 'LocalCorpusRoot', 'LocalTtfPath', 'LocalOtfPath')) {
        $value = Get-Variable -Name $inputName -ValueOnly
        if ($value) { $regressionParameters[$inputName] = $value }
    }
    Invoke-Gate 'regression' 'Verify-Regression.ps1' $regressionParameters
    $compatibilityParameters = @{ OutputRoot = (Join-Path $outputRoot 'compatibility'); DotnetPath = $dotnet }
    if ($LocalCorpusRoot) { $compatibilityParameters.LocalCorpusRoot = $LocalCorpusRoot }
    Invoke-Gate 'compatibility' 'Verify-P3Compatibility.ps1' $compatibilityParameters
    Invoke-Gate 'portable' 'Verify-P5Portable.ps1' @{ OutputRoot = (Join-Path $outputRoot 'portable'); DotnetPath = $dotnet }
    $summary.Evidence = Get-AcceptanceBundleEvidence $outputRoot $before
    if ($summary.Evidence.Commit -ne $summary.Commit -or $summary.Evidence.Branch -ne $summary.Branch -or
        $summary.Evidence.Dirty -ne $summary.Dirty) { throw 'Git identity changed during acceptance.' }
    $summary.Status = 'PASS'
} catch { $summary.Error = $_.Exception.Message; throw }
finally {
    if ($null -ne $before) {
        $after = @(Get-AcceptanceSourceManifest $repositoryRoot)
        $after | Set-Content -LiteralPath (Join-Path $outputRoot 'source-after.sha256') -Encoding utf8
        $summary.SourceUnchanged = (($before -join "`n") -ceq ($after -join "`n"))
        if (-not $summary.SourceUnchanged) { $summary.Status = 'FAILED'; $summary.Error = 'Working-tree sources changed during acceptance.' }
    }
    $summary.FinishedUtc = [DateTime]::UtcNow.ToString('o')
    $summary | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $outputRoot 'acceptance-summary.json') -Encoding utf8
    Write-AcceptanceOverview $outputRoot $summary
    if ($summary.Error -eq 'Working-tree sources changed during acceptance.') { throw $summary.Error }
}
Write-Host "ACCEPTANCE TECHNICAL PASS: $outputRoot/index.html"
Write-Host 'Physical Windows, clean-machine, P3 decisions and publication remain PENDING.'

# Retention runs after the parent has joined and saved its immutable gate evidence.
# Keep candidate ZIPs and every report; only verified redundant runtime copies are eligible.
try {
    . (Join-Path $PSScriptRoot 'TestArtifactRetention.ps1')
    $retention = Invoke-TestArtifactRetention -ArtifactRoot (Split-Path -Parent $outputRoot) -KeepLatest 2 -Apply
    Write-Host "Acceptance cleanup: $($retention.Deleted.Count) redundant files; $([math]::Round($retention.DeletedBytes / 1MB, 2)) MiB released. Receipt: $($retention.Receipt)"
    if ($retention.Errors.Count) { Write-Warning 'Some older runtime copies were retained; inspect the cleanup receipt.' }
} catch { Write-Warning ("Older runtime copies retained because cleanup could not be verified: " + $_.Exception.Message) }

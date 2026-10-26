param(
    [Parameter(Mandatory = $true)]
    [string] $AssemblyRoot,
    [switch] $PublicationApproved
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($AssemblyRoot)
$release = Join-Path $root 'release'
$extracted = Join-Path $root 'extracted'
$archive = Join-Path $root 'PhigrosLayoutStudio-1.0-win-x64.zip'
$exe = Join-Path $extracted 'Editor.Desktop.exe'
$errors = [Collections.Generic.List[string]]::new()
$integrity = $null
$catalog = $null
$runtimeAttempted = $false
function Require([bool] $condition, [string] $message) {
    if (-not $condition) { $errors.Add($message) }
}

try {
    . (Join-Path $PSScriptRoot 'ReleasePackageIntegrity.ps1')
    # All three payloads, including catalog bytes, must match the reviewed release catalog.
    # A malformed or changed package must fail before the executable is inspected or started.
    $integrity = Assert-ReleasePackageIntegrity $root
    $catalog = $integrity.Catalog
    foreach ($required in @(
        'Editor.Desktop.exe', 'Editor.Desktop.dll', 'Editor.Desktop.deps.json',
        'Editor.Desktop.runtimeconfig.json', 'candidate-files.json',
        'build-evidence.json', 'README.txt', 'LICENSE-NOTICE.txt',
        'THIRD-PARTY-NOTICES.txt', 'RELEASE-STATUS.txt', 'sbom.cdx.json',
        'notices/Avalonia-LICENSE.md', 'notices/ANGLE-LICENSE.txt',
        'notices/HarfBuzzSharp-LICENSE.txt',
        'notices/HarfBuzzSharp-THIRD-PARTY-NOTICES.txt',
        'notices/SkiaSharp-LICENSE.txt',
        'notices/SkiaSharp-THIRD-PARTY-NOTICES.txt',
        'notices/MicroCom-LICENSE.txt', 'notices/Tmds.DBus-COPYING.txt',
        'notices/Inter-OFL-1.1.txt', 'notices/DOTNET-RUNTIME-LICENSE.txt',
        'notices/DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt',
        'notices/WINDOWS-DESKTOP-LICENSE.txt', 'notices/SOURCES.json')) {
        Require (Test-Path -LiteralPath (Join-Path $release $required) -PathType Leaf) "missing $required"
    }
    Require (-not (Test-Path -LiteralPath (Join-Path $release 'notices/NOTICE_REVIEW_REQUIRED.txt'))) 'internal notice draft is still present'

    $build = Get-Content -LiteralPath (Join-Path $release 'build-evidence.json') -Raw | ConvertFrom-Json
    Require ($catalog.SourceManifestSha256 -eq $build.SourceManifestSha256) 'catalog and build source identities differ'
    $expectedStatus = if ($PublicationApproved) { 'RELEASE' } else { 'RELEASE_CANDIDATE' }
    $expectedApproval = if ($PublicationApproved) { 'APPROVED' } else { 'PENDING' }
    Require ($build.CandidateStatus -eq $expectedStatus) 'package status does not match the explicit verification mode'
    Require ($build.Release.Product -eq 'Phigros Layout Studio') 'release product identity is wrong'
    Require ($build.Release.Version -eq '1.0') 'release version is wrong'
    Require ($build.Release.PublicationApproval -eq $expectedApproval) 'publication approval does not match the explicit verification mode'
    if ($PublicationApproved) {
        Require ($build.Acceptance.CleanMachine -eq 'WAIVED_BY_PUBLISHER') 'clean-machine waiver is not recorded accurately'
        Require ($build.Acceptance.LegacyWriter -eq 'NOT_SUPPORTED') 'old-format write-back scope is not recorded accurately'
        Require ($build.Acceptance.ProjectLicense -eq 'RIGHTS_RESERVED') 'first-party rights reservation is missing'
    }
    Require ([IO.File]::ReadAllText((Join-Path $release 'README.txt')).StartsWith('Phigros Layout Studio 1.0')) 'release README product name is wrong'
    $forbidden = @($catalog.Files | Where-Object {
        $_.Path -match '(^|/)(IMAGE|FONT)(/|$)' -or
        ([IO.Path]::GetExtension($_.Path) -ieq '.exe' -and
            [IO.Path]::GetFileName($_.Path) -notin @('Editor.Desktop.exe', 'createdump.exe')) -or
        $_.Path -match '\.(phipreview|phifree|png|jpe?g|ttf|otf|pdb)$'
    })
    Require ($forbidden.Count -eq 0) ('forbidden release payload: ' + (($forbidden | ForEach-Object Path) -join ', '))

    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    Require ($version.ProductName -eq 'Phigros Layout Studio') 'executable product name is wrong'
    Require ($version.FileVersion -eq '1.0.0.0') 'executable file version is wrong'
    Require ($version.ProductVersion -eq '1.0.0') 'executable informational version is wrong'
    $coreVersion = (Get-Item -LiteralPath (Join-Path $extracted 'System.Private.CoreLib.dll')).VersionInfo.ProductVersion
    $desktopVersion = (Get-Item -LiteralPath (Join-Path $extracted 'WindowsBase.dll')).VersionInfo.ProductVersion
    Require ($coreVersion -match '^10\.0\.13') 'published Core runtime is not 10.0.13'
    Require ($desktopVersion -match '^10\.0\.13') 'published Windows Desktop runtime is not 10.0.13'

    $bom = Get-Content -LiteralPath (Join-Path $release 'sbom.cdx.json') -Raw | ConvertFrom-Json
    Require ($bom.bomFormat -eq 'CycloneDX') 'SBOM format is wrong'
    Require ($bom.specVersion -eq '1.5') 'SBOM spec version is wrong'
    Require ($bom.metadata.component.name -eq 'Phigros Layout Studio') 'SBOM product name is wrong'
    Require ($bom.metadata.component.version -eq '1.0.0') 'SBOM product version is wrong'
    Require ($bom.metadata.component.'bom-ref' -eq 'pkg:generic/phigros-layout-studio@1.0.0?arch=x64&os=windows') 'SBOM product reference is wrong'
    Require (@($bom.components).Count -ge 28) 'SBOM does not contain all runtime and project components'
    Require (@($bom.components | Where-Object { $_.name -eq 'Avalonia' }).Count -eq 1) 'SBOM misses Avalonia'
    Require (@($bom.components | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App.Runtime.win-x64' }).Count -eq 1) 'SBOM misses Windows Desktop runtime'

    $runtimeRoot = Join-Path $root 'final-runtime-verification'
    Require (-not (Test-Path -LiteralPath $runtimeRoot)) 'final runtime evidence directory already exists'
    if ($errors.Count -eq 0) {
        $runtimeAttempted = $true
        & $exe --verify-portable $runtimeRoot
        $runtimeExit = $LASTEXITCODE
        $runtimeSummaryPath = Join-Path $runtimeRoot 'runtime-summary.json'
        $runtime = if (Test-Path -LiteralPath $runtimeSummaryPath) {
            Get-Content -LiteralPath $runtimeSummaryPath -Raw | ConvertFrom-Json
        } else { $null }
        Require ($runtimeExit -eq 0) 'final portable runtime verification failed'
        Require ($null -ne $runtime -and $runtime.Status -eq 'PASS') 'final runtime summary is missing or not PASS'
        if ($null -ne $runtime) {
            Require ($runtime.SourceManifestSha256 -eq $catalog.SourceManifestSha256) 'final runtime source identity differs'
            Require ($runtime.PackageUnchanged -eq $true) 'final runtime verification changed package files'
            Require ($runtime.ReopenChecks -eq 11) 'final runtime reopen count differs'
            Require ($runtime.CliPngChecks -eq 44) 'final runtime CLI PNG count differs'
            Require ($runtime.FontPngChecks -eq 16) 'final runtime font PNG count differs'
        }
    }
} catch {
    $errors.Add($_.Exception.Message)
}

# Parse, I/O and integrity failures also produce a failed report. Never reuse an earlier PASS.
$report = [ordered]@{
    Status = if ($errors.Count -eq 0) { 'PASS' } else { 'FAILED' }
    Scope = if ($PublicationApproved) { 'RELEASE_PACKAGE_AND_RUNTIME' } else { 'RELEASE_CANDIDATE_PACKAGE_AND_RUNTIME' }
    Product = 'Phigros Layout Studio'
    Version = '1.0'
    SourceManifestSha256 = if ($null -ne $catalog) { $catalog.SourceManifestSha256 } else { $null }
    CatalogSha256 = if ($null -ne $integrity) { $integrity.CatalogSha256 } else { $null }
    ArchiveSha256 = if ($null -ne $integrity) { $integrity.ArchiveSha256 } else { $null }
    ExecutableSha256 = if ($null -ne $integrity) { $integrity.ExecutableSha256 } else { $null }
    PublishedFiles = if ($null -ne $integrity) { $integrity.PublishedFiles } else { $null }
    RuntimeAttempted = $runtimeAttempted
    PublicationApproval = if ($PublicationApproved) { 'APPROVED' } else { 'PENDING' }
    CleanMachine = 'NOT_ASSERTED'
    Errors = @($errors)
    CheckedUtc = [DateTime]::UtcNow.ToString('o')
}
$reportPath = Join-Path $root 'release-verification.json'
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
if ($errors.Count -ne 0) {
    throw ('Release package verification failed: ' + ($errors -join ' | '))
}
Write-Host "RELEASE PACKAGE VERIFICATION PASS: $reportPath"
Write-Host "ZIP SHA256: $($integrity.ArchiveSha256)"

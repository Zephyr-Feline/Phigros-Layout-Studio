param(
    [Parameter(Mandatory = $true)]
    [string] $OutputRoot,
    [string] $DotnetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputRoot)
if ($outputRoot.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $outputRoot.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must be outside the repository.'
}
if (Test-Path -LiteralPath $outputRoot) {
    if (Get-ChildItem -LiteralPath $outputRoot -Force | Select-Object -First 1) {
        throw 'OutputRoot must be new or empty; this script never deletes an existing package.'
    }
} else {
    New-Item -ItemType Directory -Path $outputRoot | Out-Null
}

. (Join-Path $PSScriptRoot 'SourceEvidence.ps1')
$summary = [ordered]@{
    Status = 'FAILED'; StartedUtc = [DateTime]::UtcNow.ToString('o'); FinishedUtc = $null
    Commit = $null; Branch = $null; Dirty = $null; Sdk = $null
    SourceFiles = $null; SourceManifestSha256 = $null; SourceUnchanged = $false
    PublishExitCode = $null; PublishLogSha256 = $null
    Runtime = $null; FontPngChecks = 0; FontStages = 0
    Package = $null; Notices = $null; ReopenChecks = 0; CliPngChecks = 0
    Commands = [Collections.Generic.List[object]]::new(); Error = $null
    Acceptance = [ordered]@{ CleanMachine = 'PENDING'; WindowsHardware = 'PENDING'
        ThirdPartyNotices = 'PENDING'; ProjectLicense = 'PENDING'; P3Visual = 'PENDING'; LegacyWriter = 'PENDING' }
}
$sourcesBefore = $null
try {
$summary.Commit = (& git -C $repositoryRoot rev-parse HEAD)
$summary.Branch = (& git -C $repositoryRoot branch --show-current)
$gitStatus = @(& git -C $repositoryRoot status --porcelain=v1)
$summary.Dirty = $gitStatus.Count -ne 0
$gitStatus | Set-Content -LiteralPath (Join-Path $outputRoot 'git-status.txt') -Encoding utf8
$sourcesBefore = @(Get-AcceptanceSourceManifest $repositoryRoot)
$sourcesBefore | Set-Content -LiteralPath (Join-Path $outputRoot 'source-before.sha256') -Encoding utf8
$summary.SourceFiles = $sourcesBefore.Count
$summary.SourceManifestSha256 = Get-AcceptanceManifestHash $sourcesBefore
$sdkVersion = & $DotnetPath --version
$summary.Sdk = $sdkVersion
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.') {
    throw "An already-installed .NET 10 SDK is required (found: $sdkVersion)."
}

$publishPath = Join-Path $outputRoot 'publish'
$projectPath = Join-Path $projectRoot 'src/Editor.Desktop/Editor.Desktop.csproj'
$publishLog = Join-Path $outputRoot 'publish.log'
& $DotnetPath publish $projectPath -c Release -r win-x64 --self-contained true --no-restore -o $publishPath *> $publishLog
$summary.PublishExitCode = $LASTEXITCODE
$summary.PublishLogSha256 = (Get-FileHash -LiteralPath $publishLog -Algorithm SHA256).Hash
if ($summary.PublishExitCode -ne 0) { Get-Content -LiteralPath $publishLog -Tail 15 | Write-Host; throw 'Offline publish failed.' }

$symbolsPath = Join-Path $outputRoot 'symbols'
$debugFiles = @(Get-ChildItem -LiteralPath $publishPath -Recurse -File -Filter '*.pdb')
$symbolBytes = ($debugFiles | Measure-Object Length -Sum).Sum
foreach ($debugFile in $debugFiles) {
    $relative = [IO.Path]::GetRelativePath($publishPath, $debugFile.FullName)
    $destination = [IO.Path]::GetFullPath((Join-Path $symbolsPath $relative))
    if (-not $destination.StartsWith($symbolsPath + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Symbol path leaves OutputRoot: $relative"
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Move-Item -LiteralPath $debugFile.FullName -Destination $destination
}

$assetsPath = Join-Path $projectRoot 'src/Editor.Desktop/obj/project.assets.json'
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$cacheRoots = @($assets.packageFolders.PSObject.Properties.Name)
function Find-CachedPackage([string] $id, [string] $version) {
    foreach ($root in $cacheRoots) {
        $folder = Join-Path (Join-Path $root $id.ToLowerInvariant()) $version.ToLowerInvariant()
        if (Test-Path -LiteralPath (Join-Path $folder ($id.ToLowerInvariant() + '.nuspec'))) {
            return $folder
        }
    }
    throw "Approved local package cache lacks $id/$version; no download was attempted."
}

$depsPath = Join-Path $publishPath 'Editor.Desktop.deps.json'
$deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
$targetName = $deps.targets.PSObject.Properties.Name |
    Where-Object { $_ -like '*/win-x64' } | Select-Object -First 1
if (-not $targetName) { throw 'Published win-x64 dependency target is missing.' }
$runtimePackages = @(
    $deps.targets.PSObject.Properties[$targetName].Value.PSObject.Properties |
        Where-Object { $deps.libraries.PSObject.Properties[$_.Name].Value.type -eq 'package' } |
        ForEach-Object {
            $parts = $_.Name.Split('/', 2)
            [pscustomobject]@{ Id = $parts[0]; Version = $parts[1] }
        } | Sort-Object Id
)
$noticeDir = Join-Path $publishPath 'notices'
New-Item -ItemType Directory -Path $noticeDir | Out-Null
$inventory = [System.Collections.Generic.List[string]]::new()
$inventory.Add('INTERNAL NOTICE REVIEW DRAFT - NOT FOR PUBLIC DISTRIBUTION')
$inventory.Add('Package entries are from the published win-x64 .deps.json runtime target.')
$inventory.Add('License labels below are local NuGet metadata, not a rights conclusion.')
$inventory.Add('The embedded Inter font, native components and project-level license still need review.')
$inventory.Add('')
$packageMetadata = [Collections.Generic.List[object]]::new()
foreach ($package in $runtimePackages) {
    $folder = Find-CachedPackage $package.Id $package.Version
    $nuspec = Join-Path $folder ($package.Id.ToLowerInvariant() + '.nuspec')
    [xml]$xml = Get-Content -LiteralPath $nuspec
    $license = $xml.package.metadata.license
    if (-not $license -or -not $license.InnerText) {
        throw "Local license metadata is missing for $($package.Id)/$($package.Version)."
    }
    $inventory.Add("$($package.Id) $($package.Version) - $($license.type): $($license.InnerText)")
    $packageMetadata.Add([ordered]@{ Id = $package.Id; Version = $package.Version
        LicenseType = [string]$license.type; LicenseMetadata = [string]$license.InnerText
        NuspecSha256 = (Get-FileHash -LiteralPath $nuspec -Algorithm SHA256).Hash
        Copyright = [string]$xml.package.metadata.copyright
        Repository = [string]$xml.package.metadata.repository.url
        RepositoryCommit = [string]$xml.package.metadata.repository.commit })
}
$packageMetadata | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $noticeDir 'runtime-metadata.json') -Encoding utf8
$inventory.Add('')
$inventory.Add('Accompanying evidence files are copied verbatim from the already-cached packages.')
$inventory.Add('This draft does not resolve the Inter font license or all distribution notices.')
$inventoryPath = Join-Path $noticeDir 'NOTICE_REVIEW_REQUIRED.txt'

$angle = $runtimePackages | Where-Object Id -eq 'Avalonia.Angle.Windows.Natives' |
    Select-Object -First 1
if (-not $angle) { throw 'ANGLE runtime package is missing from the published dependency graph.' }
$coreVersionText = (Get-Item -LiteralPath (Join-Path $publishPath 'System.Private.CoreLib.dll')).VersionInfo.ProductVersion
$desktopVersionText = (Get-Item -LiteralPath (Join-Path $publishPath 'WindowsBase.dll')).VersionInfo.ProductVersion
if ($coreVersionText -notmatch '^(\d+\.\d+\.\d+)') { throw 'Cannot identify the published .NET runtime pack.' }
$coreVersion = $Matches[1]
if ($desktopVersionText -notmatch '^(\d+\.\d+\.\d+)') { throw 'Cannot identify the published Windows Desktop runtime pack.' }
$desktopVersion = $Matches[1]
$noticeSources = @(
    @{ Folder = (Find-CachedPackage $angle.Id $angle.Version); Source = 'LICENSE'; Target = 'ANGLE-LICENSE.txt' },
    @{ Folder = (Find-CachedPackage 'Microsoft.NETCore.App.Runtime.win-x64' $coreVersion); Source = 'LICENSE.TXT'; Target = 'DOTNET-RUNTIME-LICENSE.txt' },
    @{ Folder = (Find-CachedPackage 'Microsoft.NETCore.App.Runtime.win-x64' $coreVersion); Source = 'THIRD-PARTY-NOTICES.TXT'; Target = 'DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt' },
    @{ Folder = (Find-CachedPackage 'Microsoft.WindowsDesktop.App.Runtime.win-x64' $desktopVersion); Source = 'LICENSE'; Target = 'WINDOWS-DESKTOP-LICENSE.txt' }
)
foreach ($notice in $noticeSources) {
    $source = Join-Path $notice.Folder $notice.Source
    $target = Join-Path $noticeDir $notice.Target
    if (-not (Test-Path -LiteralPath $source)) { throw "Cached notice is missing: $source" }
    Copy-Item -LiteralPath $source -Destination $target
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
        throw "Notice copy changed: $source"
    }
}
$inventory.Add('')
$inventory.Add("Microsoft.NETCore.App.Runtime.win-x64 $coreVersion - DOTNET-RUNTIME-LICENSE.txt, DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt")
$inventory.Add("Microsoft.WindowsDesktop.App.Runtime.win-x64 $desktopVersion - WINDOWS-DESKTOP-LICENSE.txt")
$inventory.Add("Avalonia.Angle.Windows.Natives $($angle.Version) - ANGLE-LICENSE.txt")
$inventory | Set-Content -LiteralPath $inventoryPath -Encoding utf8
$summary.Notices = [ordered]@{ RuntimePackages = $runtimePackages.Count; VerbatimFiles = $noticeSources.Count
    MetadataOnly = $true; EmbeddedInterAttribution = 'UNVERIFIED'; PublicationApproval = 'PENDING' }
$buildEvidence = [ordered]@{ EvidenceVersion = 1; Commit = $summary.Commit; Branch = $summary.Branch
    Dirty = $summary.Dirty; Sdk = $sdkVersion; SourceFiles = $summary.SourceFiles
    SourceManifestSha256 = $summary.SourceManifestSha256; CreatedUtc = [DateTime]::UtcNow.ToString('o')
    CandidateStatus = 'INTERNAL_ONLY'; Acceptance = $summary.Acceptance }
$buildEvidence | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $publishPath 'build-evidence.json') -Encoding utf8

$catalogFiles = @(Get-ChildItem -LiteralPath $publishPath -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{ Path = [IO.Path]::GetRelativePath($publishPath, $_.FullName).Replace('\', '/')
        Length = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
[ordered]@{ EvidenceVersion = 1; SourceManifestSha256 = $summary.SourceManifestSha256
    FileCount = $catalogFiles.Count; Files = $catalogFiles } | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $publishPath 'candidate-files.json') -Encoding utf8

$files = @(Get-ChildItem -LiteralPath $publishPath -Recurse -File)
$forbidden = @($files | Where-Object {
    $relative = [IO.Path]::GetRelativePath($publishPath, $_.FullName)
    $relative -match '(^|[\\/])(IMAGE|FONT)([\\/]|$)' -or
    ($_.Extension -ieq '.exe' -and $_.Name -notin @('Editor.Desktop.exe', 'createdump.exe')) -or
    $_.Name -match '\.(phipreview|phifree|png|jpe?g|ttf|otf|pdb)$'
})
if ($forbidden.Count -ne 0) {
    throw ('Unexpected source or asset files in publish output: ' +
        (($forbidden | ForEach-Object FullName) -join ', '))
}

$archive = Join-Path $outputRoot 'Editor.Desktop-win-x64-internal.zip'
[IO.Compression.ZipFile]::CreateFromDirectory(
    $publishPath, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
$archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
$archiveManifest = Join-Path $outputRoot 'archive-sha256.txt'
"$archiveHash  $(Split-Path -Leaf $archive)" |
    Set-Content -LiteralPath $archiveManifest -Encoding utf8
$extractedPath = Join-Path $outputRoot 'extracted'
[IO.Compression.ZipFile]::ExtractToDirectory($archive, $extractedPath)
$extractedFiles = @(Get-ChildItem -LiteralPath $extractedPath -Recurse -File)
if ($extractedFiles.Count -ne $files.Count) { throw 'Archive extraction changed the file count.' }

$manifest = Join-Path $outputRoot 'publish-sha256.txt'
$entries = $files | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($publishPath, $_.FullName)
    $extractedFile = Join-Path $extractedPath $relative
    if (-not (Test-Path -LiteralPath $extractedFile)) {
        throw "Archive extraction omitted $relative"
    }
    $expectedHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    $actualHash = (Get-FileHash -LiteralPath $extractedFile -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) { throw "Archive extraction changed $relative" }
    "$expectedHash  $relative"
}
$entries | Set-Content -LiteralPath $manifest -Encoding utf8

$exe = Join-Path $extractedPath 'Editor.Desktop.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Archived desktop executable is missing.' }

# Run the extracted candidate's built-in verification command without a script host or SDK.
$runtimeRoot = Join-Path $outputRoot 'runtime-verification'
$runtimeLog = Join-Path $outputRoot 'runtime-driver.log'
& $exe --verify-portable $runtimeRoot *> $runtimeLog
$runtimeExit = $LASTEXITCODE
$runtimeSummaryPath = Join-Path $runtimeRoot 'runtime-summary.json'
if (Test-Path -LiteralPath $runtimeSummaryPath) {
    $runtime = Get-Content -LiteralPath $runtimeSummaryPath -Raw | ConvertFrom-Json
    $summary.Runtime = [ordered]@{ Status = $runtime.Status; Framework = $runtime.Framework
        SourceManifestSha256 = $runtime.SourceManifestSha256; PackageUnchanged = $runtime.PackageUnchanged
        CatalogSha256 = $runtime.CatalogSha256; PackageFiles = $runtime.PackageFiles
        FontProfileDirectory = $runtime.FontProfileDirectory
        ReducedEnvironment = $runtime.ReducedEnvironment; DotnetOnReducedPath = $runtime.DotnetOnReducedPath
        DriverExitCode = $runtimeExit; DriverLogSha256 = (Get-FileHash -LiteralPath $runtimeLog -Algorithm SHA256).Hash
        ReportSha256 = (Get-FileHash -LiteralPath $runtimeSummaryPath -Algorithm SHA256).Hash; Error = $runtime.Error }
    $summary.ReopenChecks = $runtime.ReopenChecks
    $summary.CliPngChecks = $runtime.CliPngChecks
    $summary.FontPngChecks = $runtime.FontPngChecks
    $summary.FontStages = @($runtime.FontStages).Count
    foreach ($command in $runtime.Commands) {
        $command.Log = 'runtime-verification/' + $command.Log
        $summary.Commands.Add($command)
    }
}
if ($runtimeExit -ne 0 -or $null -eq $summary.Runtime -or $summary.Runtime.Status -ne 'PASS' -or
    -not $summary.Runtime.PackageUnchanged -or -not $summary.Runtime.ReducedEnvironment -or
    $summary.Runtime.DotnetOnReducedPath -or $summary.Runtime.PackageFiles -ne $files.Count -or
    $summary.Runtime.SourceManifestSha256 -ne $summary.SourceManifestSha256 -or
    $summary.ReopenChecks -ne 11 -or $summary.CliPngChecks -ne 44 -or
    $summary.FontStages -ne 5 -or $summary.FontPngChecks -ne 16) {
    Get-Content -LiteralPath $runtimeLog -Tail 15 | Write-Host
    throw 'Extracted candidate runtime verification failed.'
}
Get-Content -LiteralPath $runtimeLog | Write-Host
$symbolManifest = Join-Path $outputRoot 'symbols-sha256.txt'
$symbolEntries = $debugFiles | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($publishPath, $_.FullName)
    $symbolFile = Join-Path $symbolsPath $relative
    "$(Get-FileHash -LiteralPath $symbolFile -Algorithm SHA256 | Select-Object -ExpandProperty Hash)  $relative"
}
$symbolEntries | Set-Content -LiteralPath $symbolManifest -Encoding utf8
$bytes = ($files | Measure-Object Length -Sum).Sum
Write-Host "P5 PORTABLE PREFLIGHT PASS: $($files.Count) publish files; $([math]::Round($bytes / 1MB, 2)) MiB."
Write-Host "Symbols retained separately: $($debugFiles.Count) files; $([math]::Round($symbolBytes / 1MB, 2)) MiB."
Write-Host "Notice evidence included: $($runtimePackages.Count) runtime packages, $($noticeSources.Count) verbatim local files; review required."
Write-Host "Internal archive verified after extraction: $archive ($([math]::Round((Get-Item -LiteralPath $archive).Length / 1MB, 2)) MiB)."
Write-Host "Manifests: $archiveManifest; $manifest; $symbolManifest"
$summary.Package = [ordered]@{ Archive = $archive; ArchiveSha256 = $archiveHash
    PublishedFiles = $files.Count; PublishedBytes = $bytes; SymbolFiles = $debugFiles.Count; SymbolBytes = $symbolBytes
    ExecutableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    PublishManifestSha256 = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash
    SymbolManifestSha256 = (Get-FileHash -LiteralPath $symbolManifest -Algorithm SHA256).Hash }
$summary.Status = 'PASS'
} catch {
    $summary.Error = $_.Exception.Message
    throw
} finally {
    if ($null -ne $sourcesBefore) {
        $sourcesAfter = @(Get-AcceptanceSourceManifest $repositoryRoot)
        $sourcesAfter | Set-Content -LiteralPath (Join-Path $outputRoot 'source-after.sha256') -Encoding utf8
        $summary.SourceUnchanged = (($sourcesBefore -join "`n") -ceq ($sourcesAfter -join "`n"))
        if (-not $summary.SourceUnchanged) { $summary.Status = 'FAILED'; $summary.Error = 'Working-tree sources changed during preflight.' }
    }
    $summary.FinishedUtc = [DateTime]::UtcNow.ToString('o')
    $summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $outputRoot 'portable-summary.json') -Encoding utf8
    if ($summary.Error -eq 'Working-tree sources changed during preflight.') { throw $summary.Error }
}

# The verified archive and extracted runtime remain available to the parent acceptance gate.
# Only the redundant publish binaries are retired here, after all evidence has been written.
if ($summary.Status -eq 'PASS') {
    try {
        . (Join-Path $PSScriptRoot 'TestArtifactRetention.ps1')
        $retention = Invoke-TestArtifactRetention -ArtifactRoot (Split-Path -Parent $outputRoot) `
            -PublishOnly $outputRoot -Apply
        Write-Host "Portable cleanup: $($retention.Deleted.Count) redundant files; $([math]::Round($retention.DeletedBytes / 1MB, 2)) MiB released. Receipt: $($retention.Receipt)"
        if ($retention.Errors.Count) { Write-Warning 'Some publish copies were retained; inspect the cleanup receipt.' }
    } catch { Write-Warning ("Publish copies retained because cleanup could not be verified: " + $_.Exception.Message) }
}

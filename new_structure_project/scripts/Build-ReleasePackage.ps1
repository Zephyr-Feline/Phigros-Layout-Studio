param(
    [Parameter(Mandatory = $true)]
    [string] $InternalPortableRoot,
    [Parameter(Mandatory = $true)]
    [string] $MaterialsRoot,
    [Parameter(Mandatory = $true)]
    [string] $OutputRoot,
    [switch] $PublicationApproved
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '..'))
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $InternalPortableRoot 'extracted'))
$materials = [IO.Path]::GetFullPath($MaterialsRoot)
$output = [IO.Path]::GetFullPath($OutputRoot)
if ($output.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must be outside the repository.'
}
if (Test-Path -LiteralPath $output) {
    if (-not (Test-Path -LiteralPath $output -PathType Container) -or
        (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1)) {
        throw 'OutputRoot must be new or empty; existing release evidence is never overwritten.'
    }
} else {
    New-Item -ItemType Directory -Path $output | Out-Null
}
if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
    throw "Accepted portable extraction is missing: $sourceRoot"
}
if (-not (Test-Path -LiteralPath $materials -PathType Container)) {
    throw "Reviewed third-party materials directory is missing: $materials"
}

$release = Join-Path $output 'release'
New-Item -ItemType Directory -Path $release | Out-Null
Get-ChildItem -LiteralPath $sourceRoot -Force |
    Copy-Item -Destination $release -Recurse -Force

$draftNotice = Join-Path $release 'notices/NOTICE_REVIEW_REQUIRED.txt'
if (Test-Path -LiteralPath $draftNotice -PathType Leaf) {
    Remove-Item -LiteralPath $draftNotice
}
foreach ($required in @('Editor.Desktop.exe', 'Editor.Desktop.deps.json',
    'Editor.Desktop.runtimeconfig.json', 'notices/runtime-metadata.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $release $required) -PathType Leaf)) {
        throw "Accepted portable candidate is missing $required"
    }
}

$releaseDocs = Join-Path $projectRoot 'release'
Copy-Item -LiteralPath (Join-Path $releaseDocs 'README.txt') -Destination (Join-Path $release 'README.txt')
Copy-Item -LiteralPath (Join-Path $releaseDocs 'RIGHTS-NOTICE.txt') -Destination (Join-Path $release 'LICENSE-NOTICE.txt')

$noticeDir = Join-Path $release 'notices'
New-Item -ItemType Directory -Path $noticeDir -Force | Out-Null
$copies = @(
    @{ Source = 'official-sources/Avalonia-LICENSE.md'; Target = 'Avalonia-LICENSE.md' },
    @{ Source = 'local-cache/Avalonia.Angle.Windows.Natives/2.1.27548.20260419/LICENSE'; Target = 'ANGLE-LICENSE.txt' },
    @{ Source = 'local-cache/HarfBuzzSharp/8.3.1.3/LICENSE.txt'; Target = 'HarfBuzzSharp-LICENSE.txt' },
    @{ Source = 'local-cache/HarfBuzzSharp.NativeAssets.Win32/8.3.1.3/THIRD-PARTY-NOTICES.txt'; Target = 'HarfBuzzSharp-THIRD-PARTY-NOTICES.txt' },
    @{ Source = 'local-cache/SkiaSharp/3.119.4/LICENSE.txt'; Target = 'SkiaSharp-LICENSE.txt' },
    @{ Source = 'local-cache/SkiaSharp.NativeAssets.Win32/3.119.4/THIRD-PARTY-NOTICES.txt'; Target = 'SkiaSharp-THIRD-PARTY-NOTICES.txt' },
    @{ Source = 'official-sources/MicroCom-LICENSE.txt'; Target = 'MicroCom-LICENSE.txt' },
    @{ Source = 'official-sources/Tmds-DBus-COPYING.txt'; Target = 'Tmds.DBus-COPYING.txt' },
    @{ Source = 'official-sources/Inter-3.019-OFL.txt'; Target = 'Inter-OFL-1.1.txt' },
    @{ Source = 'local-cache/Microsoft.NETCore.App.Runtime.win-x64/10.0.13/LICENSE.TXT'; Target = 'DOTNET-RUNTIME-LICENSE.txt' },
    @{ Source = 'local-cache/Microsoft.NETCore.App.Runtime.win-x64/10.0.13/THIRD-PARTY-NOTICES.TXT'; Target = 'DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt' },
    @{ Source = 'local-cache/Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.13/LICENSE'; Target = 'WINDOWS-DESKTOP-LICENSE.txt' }
)
$noticeHashes = [Collections.Generic.List[object]]::new()
foreach ($copy in $copies) {
    $source = [IO.Path]::GetFullPath((Join-Path $materials $copy.Source))
    if (-not $source.StartsWith($materials + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Third-party material leaves the reviewed directory: $($copy.Source)"
    }
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Reviewed third-party material is missing: $($copy.Source)"
    }
    $target = Join-Path $noticeDir $copy.Target
    Copy-Item -LiteralPath $source -Destination $target
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    if ($sourceHash -ne $targetHash) {
        throw "Third-party material changed while copied: $($copy.Source)"
    }
    $noticeHashes.Add([ordered]@{ File = "notices/$($copy.Target)"; Sha256 = $targetHash })
}

$thirdPartyNotice = @'
Phigros Layout Studio 1.0 - Third-Party Notices

This package contains third-party software and embedded font software. The
complete applicable texts are provided in this notices directory. Package
license metadata is not substituted for the upstream license text.

Avalonia 12.1.3 (Avalonia, Avalonia.Desktop, Avalonia.Fonts.Inter,
Avalonia.FreeDesktop, Avalonia.FreeDesktop.AtSpi, Avalonia.HarfBuzz,
Avalonia.Headless, Avalonia.Native, Avalonia.Remote.Protocol, Avalonia.Skia,
Avalonia.Themes.Fluent, Avalonia.Win32 and Avalonia.X11)
Copyright (c) the Avalonia project contributors.
License: MIT. Source: https://github.com/AvaloniaUI/Avalonia
Reviewed source revision: 8eeda4f6f546165b3f72e63c9f42247abb306905
License text: notices/Avalonia-LICENSE.md

Avalonia.Angle.Windows.Natives 2.1.27548.20260419
Source: https://github.com/AvaloniaUI/angle
Reviewed source revision: 1c89805903c1482166356d3b950d474973180e61
License text: notices/ANGLE-LICENSE.txt

HarfBuzzSharp 8.3.1.3 and HarfBuzzSharp.NativeAssets.Win32 8.3.1.3
Copyright (c) Microsoft Corporation.
License: MIT. Native-component notices are provided separately.
Reviewed source revision: 2888c737ad016d584c74525e2d35db5097ea8576
License text: notices/HarfBuzzSharp-LICENSE.txt
Additional notices: notices/HarfBuzzSharp-THIRD-PARTY-NOTICES.txt

SkiaSharp 3.119.4 and SkiaSharp.NativeAssets.Win32 3.119.4
Copyright (c) Microsoft Corporation.
License: MIT. Native-component notices are provided separately.
Reviewed source revision: f568ac94dd768ef9a2f593537cfde2dd0d348ef5
License text: notices/SkiaSharp-LICENSE.txt
Additional notices: notices/SkiaSharp-THIRD-PARTY-NOTICES.txt

MicroCom.Runtime 0.11.6
Copyright (c) the MicroCom project contributors.
License: MIT. Source: https://github.com/kekekeks/MicroCom
Reviewed source revision: 76785efcafd91b5902fd19dd11145f6dd655b7b4
License text: notices/MicroCom-LICENSE.txt

Tmds.DBus.Protocol 0.94.1
Copyright (c) the Tmds.DBus project contributors.
License text: notices/Tmds.DBus-COPYING.txt
Reviewed source revision: b4a7fed0b878f74cb54f7cca84d2889af4e596ba

Inter 3.019 font software embedded by Avalonia.Fonts.Inter
Copyright 2020 The Inter Project Authors (https://github.com/rsms/inter)
License: SIL Open Font License 1.1.
Reviewed font revision: 0a5106e0b
License text: notices/Inter-OFL-1.1.txt

Microsoft .NET and Windows Desktop runtime 10.0.13
The self-contained win-x64 package includes Microsoft.NETCore.App and
Microsoft.WindowsDesktop.App runtime files.
License texts: notices/DOTNET-RUNTIME-LICENSE.txt and
notices/WINDOWS-DESKTOP-LICENSE.txt
Additional notices: notices/DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt

The package does not grant any right to game artwork, user-imported images
or user-supplied fonts.
'@
Set-Content -LiteralPath (Join-Path $release 'THIRD-PARTY-NOTICES.txt') -Value $thirdPartyNotice -Encoding utf8

$sourceMap = [ordered]@{
    EvidenceVersion = 1
    Product = 'Phigros Layout Studio'
    Version = '1.0'
    Materials = $noticeHashes
    AvaloniaSource = 'https://github.com/AvaloniaUI/Avalonia/tree/8eeda4f6f546165b3f72e63c9f42247abb306905'
    InterSource = 'https://github.com/rsms/inter/tree/0a5106e0b'
    AngleSource = 'https://github.com/AvaloniaUI/angle/tree/1c89805903c1482166356d3b950d474973180e61'
    HarfBuzzSource = 'https://github.com/mono/SkiaSharp/tree/2888c737ad016d584c74525e2d35db5097ea8576'
    SkiaSharpSource = 'https://github.com/mono/SkiaSharp/tree/f568ac94dd768ef9a2f593537cfde2dd0d348ef5'
    MicroComSource = 'https://github.com/kekekeks/MicroCom/tree/76785efcafd91b5902fd19dd11145f6dd655b7b4'
    TmdsSource = 'https://github.com/tmds/Tmds.DBus/tree/b4a7fed0b878f74cb54f7cca84d2889af4e596ba'
}
$sourceMap | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $noticeDir 'SOURCES.json') -Encoding utf8

$deps = Get-Content -LiteralPath (Join-Path $release 'Editor.Desktop.deps.json') -Raw | ConvertFrom-Json
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
if ($runtimePackages.Count -ne 20) {
    throw "Expected 20 runtime NuGet packages, found $($runtimePackages.Count)."
}
$runtimeMetadata = Get-Content -LiteralPath (Join-Path $noticeDir 'runtime-metadata.json') -Raw | ConvertFrom-Json
$metadataById = @{}
foreach ($item in $runtimeMetadata) { $metadataById["$($item.Id)/$($item.Version)"] = $item }
$components = [Collections.Generic.List[object]]::new()
$componentRefs = [Collections.Generic.List[string]]::new()
foreach ($package in $runtimePackages) {
    $key = "$($package.Id)/$($package.Version)"
    if (-not $metadataById.ContainsKey($key)) { throw "Runtime metadata is missing $key" }
    $metadata = $metadataById[$key]
    $ref = "pkg:nuget/$($package.Id.ToLowerInvariant())@$($package.Version)"
    $componentRefs.Add($ref)
    $license = if ($metadata.LicenseType -eq 'expression' -and $metadata.LicenseMetadata) {
        [ordered]@{ expression = [string]$metadata.LicenseMetadata }
    } else {
        [ordered]@{ license = [ordered]@{
            name = if ($metadata.LicenseMetadata) { [string]$metadata.LicenseMetadata } else { 'See notices directory' }
        } }
    }
    $component = [ordered]@{
        type = 'library'; 'bom-ref' = $ref; name = $package.Id; version = $package.Version
        scope = 'required'; purl = $ref; licenses = @($license)
    }
    if ($metadata.Repository) { $component.externalReferences = @([ordered]@{ type = 'vcs'; url = [string]$metadata.Repository }) }
    $components.Add($component)
}
foreach ($runtimePack in @(
    @{ Name = 'Microsoft.NETCore.App.Runtime.win-x64'; Version = '10.0.13' },
    @{ Name = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'; Version = '10.0.13' }
)) {
    $ref = "pkg:nuget/$($runtimePack.Name.ToLowerInvariant())@$($runtimePack.Version)"
    $componentRefs.Add($ref)
    $components.Add([ordered]@{
        type = 'framework'; 'bom-ref' = $ref; name = $runtimePack.Name; version = $runtimePack.Version
        scope = 'required'; purl = $ref
        licenses = @([ordered]@{ license = [ordered]@{ name = 'Microsoft .NET Library License; see notices directory' } })
    })
}
foreach ($assembly in @('Editor.Core.dll', 'Editor.Application.dll', 'Editor.Formats.dll',
    'Editor.Rendering.dll', 'Editor.Imaging.Windows.dll', 'Editor.Desktop.dll')) {
    $version = (Get-Item -LiteralPath (Join-Path $release $assembly)).VersionInfo.FileVersion
    $name = [IO.Path]::GetFileNameWithoutExtension($assembly)
    $components.Add([ordered]@{
        type = 'library'; 'bom-ref' = "urn:phigros-layout-studio:$name"; name = $name
        version = $version; scope = 'required'
        licenses = @([ordered]@{ license = [ordered]@{ name = 'Rightsholder terms; see LICENSE-NOTICE.txt' } })
    })
}
$applicationRef = 'pkg:generic/phigros-layout-studio@1.0.0?arch=x64&os=windows'
$bom = [ordered]@{
    bomFormat = 'CycloneDX'; specVersion = '1.5'; version = 1
    serialNumber = 'urn:uuid:' + [guid]::NewGuid().ToString()
    metadata = [ordered]@{
        timestamp = [DateTime]::UtcNow.ToString('o')
        component = [ordered]@{
            type = 'application'; 'bom-ref' = $applicationRef; name = 'Phigros Layout Studio'
            version = '1.0.0'
            licenses = @([ordered]@{ license = [ordered]@{ name = 'Rightsholder terms; see LICENSE-NOTICE.txt' } })
        }
    }
    components = $components
    dependencies = @(@([ordered]@{ ref = $applicationRef; dependsOn = @($componentRefs) }))
}
$bom | ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath (Join-Path $release 'sbom.cdx.json') -Encoding utf8

$buildPath = Join-Path $release 'build-evidence.json'
$build = Get-Content -LiteralPath $buildPath -Raw | ConvertFrom-Json -AsHashtable
$packageStatus = if ($PublicationApproved) { 'RELEASE' } else { 'RELEASE_CANDIDATE' }
$approvalStatus = if ($PublicationApproved) { 'APPROVED' } else { 'PENDING' }
$build.CandidateStatus = $packageStatus
if ($PublicationApproved) {
    $build.Acceptance = [ordered]@{
        CleanMachine = 'WAIVED_BY_PUBLISHER'
        WindowsHardware = 'LIMITED_SCOPE_EVIDENCE_REUSED'
        ThirdPartyNotices = 'VERIFIED_FOR_PAYLOAD'
        ProjectLicense = 'RIGHTS_RESERVED'
        P3Visual = 'DOCUMENTED_CONVERSION_DIFFERENCES'
        LegacyWriter = 'NOT_SUPPORTED'
        ScreenReader = 'NOT_VERIFIED_OUTSIDE_CORE_ACCEPTANCE'
    }
}
$build.Release = [ordered]@{
    Product = 'Phigros Layout Studio'
    Version = '1.0'
    Package = 'win-x64 self-contained portable ZIP'
    SupportedPlatform = 'Windows 11 25H2 x64'
    SupportedScaling = 'single display 100%, 125%, 150%, 200%'
    PublicationApproval = $approvalStatus
    Notes = if ($PublicationApproved) {
        'Publisher-authorized release. Support, waived checks and unverified areas are documented; no blanket hardware or security guarantee.'
    } else { 'Technical release candidate; publication and physical acceptance are separate.' }
}
$build | ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath $buildPath -Encoding utf8

$releaseStatus = if ($PublicationApproved) { @'
Status: RELEASE
Product: Phigros Layout Studio
Version: 1.0
Architecture: Windows x64
Runtime: self-contained .NET 10.0.13
Publication approval: APPROVED

Supported target: Windows 11 25H2 x64, single display 100/125/150/200%.
Second clean Windows machine: waived by the publisher, not a tested PASS.
Screen-reader speech: not separately verified, outside core acceptance.
Mixed DPI, multiple displays and other platforms are outside support scope.
Compatible inputs are read-only conversions with documented visual differences;
there is no old-format write-back or pixel-identical rendering promise.

First-party rights remain reserved under LICENSE-NOTICE.txt. Third-party
notices and licenses remain applicable. The executable is not code-signed.
Official release: https://github.com/Zephyr-Feline/Phigros-Layout-Studio/releases/tag/v1.0.0
'@ } else { @'
Status: RELEASE_CANDIDATE
Product: Phigros Layout Studio
Version: 1.0
Architecture: Windows x64
Runtime: self-contained .NET 10.0.13
Publication approval: PENDING

This directory is technically assembled for final review. It is not evidence
of a clean-machine pass, complete Chinese IME coverage, legal approval or
permission to upload. The publisher must bind the final archive, checksums,
support statement and approvals before publication.
'@ }
Set-Content -LiteralPath (Join-Path $release 'RELEASE-STATUS.txt') -Value $releaseStatus -Encoding utf8

$forbidden = @(Get-ChildItem -LiteralPath $release -Recurse -File | Where-Object {
    $relative = [IO.Path]::GetRelativePath($release, $_.FullName)
    $relative -match '(^|[\\/])(IMAGE|FONT)([\\/]|$)' -or
    ($_.Extension -ieq '.exe' -and $_.Name -notin @('Editor.Desktop.exe', 'createdump.exe')) -or
    $_.Name -match '\.(phipreview|phifree|png|jpe?g|ttf|otf|pdb)$'
})
if ($forbidden.Count -ne 0) {
    throw ('Unexpected release payload: ' + (($forbidden | ForEach-Object FullName) -join ', '))
}

$catalogName = 'candidate-files.json'
$catalogPath = Join-Path $release $catalogName
if (Test-Path -LiteralPath $catalogPath) { Remove-Item -LiteralPath $catalogPath }
$catalogFiles = @(Get-ChildItem -LiteralPath $release -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        Path = [IO.Path]::GetRelativePath($release, $_.FullName).Replace('\', '/')
        Length = $_.Length
        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
})
[ordered]@{
    EvidenceVersion = 1
    SourceManifestSha256 = $build.SourceManifestSha256
    FileCount = $catalogFiles.Count
    Files = $catalogFiles
} | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $catalogPath -Encoding utf8

$archive = Join-Path $output 'PhigrosLayoutStudio-1.0-win-x64.zip'
[IO.Compression.ZipFile]::CreateFromDirectory(
    $release, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
$archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
"$archiveHash  $(Split-Path -Leaf $archive)" |
    Set-Content -LiteralPath ($archive + '.sha256') -Encoding ascii

$extracted = Join-Path $output 'extracted'
[IO.Compression.ZipFile]::ExtractToDirectory($archive, $extracted)
$summary = [ordered]@{
    Status = $packageStatus + '_ASSEMBLED'
    Product = 'Phigros Layout Studio'
    Version = '1.0'
    SourceManifestSha256 = $build.SourceManifestSha256
    PublishedFiles = $catalogFiles.Count + 1
    Archive = $archive
    ArchiveBytes = (Get-Item -LiteralPath $archive).Length
    ArchiveSha256 = $archiveHash
    ExecutableSha256 = (Get-FileHash -LiteralPath (Join-Path $extracted 'Editor.Desktop.exe') -Algorithm SHA256).Hash
    PublicationApproval = $approvalStatus
}
$summary | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $output 'release-assembly-summary.json') -Encoding utf8
Write-Host "$packageStatus ASSEMBLED: $archive"
Write-Host "SHA256: $archiveHash"

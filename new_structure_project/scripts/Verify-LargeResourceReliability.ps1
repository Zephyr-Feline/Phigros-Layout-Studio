param(
    [Parameter(Mandatory = $true)] [string] $ExePath,
    [Parameter(Mandatory = $true)] [string] $OutputRoot,
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '../..'),
    [ValidateRange(1, 7200)] [int] $TimeoutSeconds = 1800
)

# Runs an already-built candidate; never restores, builds or downloads anything.
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($RepositoryRoot))
$outputRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputRoot))
$executable = [IO.Path]::GetFullPath($ExePath)
if ($outputRoot.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $outputRoot.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must be outside the repository.'
}
# Reject junction/symlink parents as well as the final directory so lexical containment
# checks cannot write inside the repository through an existing redirected ancestor.
$ancestor = $outputRoot
while ($ancestor) {
    if (Test-Path -LiteralPath $ancestor) {
        $item = Get-Item -LiteralPath $ancestor -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'OutputRoot and its existing ancestors must not be reparse points.'
        }
    }
    $parent = [IO.Path]::GetDirectoryName($ancestor)
    if ($parent -eq $ancestor) { break }
    $ancestor = $parent
}
if (Test-Path -LiteralPath $outputRoot) {
    if (-not (Test-Path -LiteralPath $outputRoot -PathType Container) -or
        (Get-ChildItem -LiteralPath $outputRoot -Force | Select-Object -First 1)) {
        throw 'OutputRoot must be new or empty; previous evidence is never overwritten.'
    }
} else { New-Item -ItemType Directory -Path $outputRoot | Out-Null }

function Write-NewText([string] $Path, [string] $Text) {
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
        [IO.FileShare]::Read)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
        $stream.Write($bytes, 0, $bytes.Length)
    } finally { $stream.Dispose() }
}
function Require([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}
function Field($Record, [string] $Name) {
    if ($null -eq $Record -or $null -eq $Record.PSObject.Properties[$Name]) {
        throw "Required evidence field is missing: $Name"
    }
    $Record.PSObject.Properties[$Name].Value
}
function Require-True($Record, [string] $Name) {
    $value = Field $Record $Name
    Require ($value -is [bool] -and $value) "Required evidence flag is not true: $Name"
}
function Number($Record, [string] $Name) {
    $value = Field $Record $Name
    Require ($value -is [int] -or $value -is [long] -or $value -is [double] -or
        $value -is [decimal]) "Required evidence field is not numeric: $Name"
    Require ([double]::IsFinite([double]$value)) "Evidence number is not finite: $Name"
    return $value
}
function Require-Number($Record, [string] $Name, [long] $Expected) {
    Require ((Number $Record $Name) -eq $Expected) "Unexpected evidence value: $Name (expected $Expected)."
}
function Resolve-EvidenceFile([string] $Directory, $Name) {
    Require ($Name -is [string] -and $Name -match '^[A-Za-z0-9][A-Za-z0-9._-]{0,100}$' -and
        $Name -ne '.' -and $Name -ne '..') 'Evidence file name must be a controlled leaf name.'
    $path = [IO.Path]::GetFullPath((Join-Path $Directory $Name))
    Require ($path.StartsWith($Directory + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) 'Evidence file escaped its output directory.'
    Require (Test-Path -LiteralPath $path -PathType Leaf) "Evidence artifact is missing: $Name"
    Require (((Get-Item -LiteralPath $path -Force).Attributes -band
        [IO.FileAttributes]::ReparsePoint) -eq 0) 'Evidence artifacts must not be reparse points.'
    return $path
}
function Check-Hash([string] $Path, $Expected) {
    Require ($Expected -is [string] -and $Expected -cmatch '^[A-Fa-f0-9]{64}$') 'Artifact SHA256 is missing or invalid.'
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    Require ($actual -eq $Expected) "Artifact SHA256 mismatch: $([IO.Path]::GetFileName($Path))"
    return $actual
}
function Read-PngHeader([IO.Stream] $Stream, [int] $Width, [int] $Height) {
    $header = [byte[]]::new(24)
    $offset = 0
    while ($offset -lt $header.Length) {
        $read = $Stream.Read($header, $offset, $header.Length - $offset)
        Require ($read -gt 0) 'PNG artifact has a truncated header.'
        $offset += $read
    }
    $signature = [byte[]] @(137, 80, 78, 71, 13, 10, 26, 10)
    for ($index = 0; $index -lt $signature.Length; $index++) {
        Require ($header[$index] -eq $signature[$index]) 'Artifact is not a PNG.'
    }
    Require ([Text.Encoding]::ASCII.GetString($header, 12, 4) -ceq 'IHDR') 'PNG IHDR is missing.'
    Require ($header[8] -eq 0 -and $header[9] -eq 0 -and $header[10] -eq 0 -and
        $header[11] -eq 13) 'PNG IHDR length is invalid.'
    $pngWidth = [long]$header[16] * 16777216 + [long]$header[17] * 65536 +
        [long]$header[18] * 256 + $header[19]
    $pngHeight = [long]$header[20] * 16777216 + [long]$header[21] * 65536 +
        [long]$header[22] * 256 + $header[23]
    Require ($pngWidth -eq $Width -and $pngHeight -eq $Height) "PNG header dimensions differ from ${Width}x${Height}."
}
function Check-Png([string] $Path, [int] $Width, [int] $Height) {
    $stream = [IO.File]::OpenRead($Path)
    try { Read-PngHeader $stream $Width $Height } finally { $stream.Dispose() }
}
function Check-ProductionEvidence($Report, [string] $Directory) {
    Require ((Field $Report 'scenario') -ceq 'large-resources') 'Wrong probe scenario.'
    Require ((Field $Report 'status') -ceq 'PASS') 'The child probe did not pass.'
    Require ((Field $Report 'configurationKind') -ceq 'PRODUCTION') 'Scaled test evidence cannot satisfy production acceptance.'
    Require ($null -eq (Field $Report 'error')) 'Passing probe evidence contains an error.'
    Require-Number $Report 'canvasWidth' 7680
    Require-Number $Report 'canvasHeight' 4320
    Require-Number $Report 'layerCount' 200
    Require-Number $Report 'imageCount' 10
    Require-Number $Report 'imageSize' 2048
    Require-Number $Report 'distinctImagePayloads' 10
    Require-Number $Report 'decodedImagePopulationBytes' 167772160
    Require ((Number $Report 'encodedAssetBytes') -gt 0) 'Encoded image bytes are missing.'

    $history = Field $Report 'history'
    Require-Number $history 'historyLimit' 64
    Require-Number $history 'gestureEdits' 90
    Require-Number $history 'retainedUndoStatesWalked' 64
    Require-Number $history 'retainedRedoStatesWalked' 64
    Require-True $history 'payloadsShared'
    Require-True $history 'cleanupUndoRestoredSameBlob'
    $cache = Field $Report 'previewCache'
    Require-Number $cache 'pixelBudget' 134217728
    Require-Number $cache 'entryLimit' 64
    Require ((Number $Report 'decodedImagePopulationBytes') -gt (Number $cache 'pixelBudget')) 'Image population did not exceed the decoded-cache budget.'
    Require ((Number $cache 'retainedImagesAfterPopulation') -ge 1 -and
        (Number $cache 'retainedImagesAfterPopulation') -lt 10 -and
        (Number $cache 'retainedImagesAfterPopulation') -le (Number $cache 'entryLimit')) 'Unexpected decoded-cache entry count.'
    Require ((Number $cache 'retainedPixelBytesAfterPopulation') -ge 16777216 -and
        (Number $cache 'retainedPixelBytesAfterPopulation') -le (Number $cache 'pixelBudget')) 'Decoded-cache retention is outside its budget.'
    foreach ($flag in @('evictionObserved', 'cachedPixelsMatchFresh', 'clearVerified')) {
        Require-True $cache $flag
    }
    $cached = Resolve-EvidenceFile $Directory (Field $cache 'cachedFileName')
    $fresh = Resolve-EvidenceFile $Directory (Field $cache 'freshFileName')
    Check-Png $cached 640 360
    Check-Png $fresh 640 360
    $cachedHash = (Get-FileHash -LiteralPath $cached -Algorithm SHA256).Hash
    Require ($cachedHash -eq (Get-FileHash -LiteralPath $fresh -Algorithm SHA256).Hash) 'Actual cached and fresh preview artifacts differ.'

    $expected = @(
        @{ Name = 'canvas'; Width = 7680; Height = 4320; Transparent = $false; Crop = $null },
        @{ Name = 'pixel-limit'; Width = 8000; Height = 10000; Transparent = $true; Crop = @(0, 0, 3456, 4320) },
        @{ Name = 'dimension-limit'; Width = 16384; Height = 2304; Transparent = $false; Crop = @(0, 0, 7680, 1080) }
    )
    $outputs = @(Field $Report 'outputs')
    Require ($outputs.Count -eq $expected.Count) 'The production probe must produce exactly three output specifications.'
    $verified = [Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $expected.Count; $index++) {
        $actual = $outputs[$index]
        $spec = $expected[$index]
        Require ((Field $actual 'name') -ceq $spec.Name) 'Unexpected output specification order or name.'
        Require ((Field $actual 'fileName') -ceq ($spec.Name + '.png')) 'Unexpected output artifact file name.'
        Require-Number $actual 'width' $spec.Width
        Require-Number $actual 'height' $spec.Height
        Require-Number $actual 'pixels' ([long]$spec.Width * $spec.Height)
        $transparent = Field $actual 'transparent'
        Require ($transparent -is [bool] -and $transparent -eq $spec.Transparent) 'Unexpected output transparency mode.'
        $region = Field $actual 'cropRegion'
        if ($null -eq $spec.Crop) { Require ($null -eq $region) 'Full-canvas output contains an unexpected crop.' }
        else {
            $names = @('x', 'y', 'width', 'height')
            for ($axis = 0; $axis -lt $names.Count; $axis++) {
                Require-Number $region $names[$axis] $spec.Crop[$axis]
            }
        }
        $path = Resolve-EvidenceFile $Directory (Field $actual 'fileName')
        Check-Png $path $spec.Width $spec.Height
        $hash = Check-Hash $path (Field $actual 'sha256')
        Require-Number $actual 'encodedBytes' (Get-Item -LiteralPath $path).Length
        Require ((Number $actual 'durationMs') -ge 0) 'Output duration measurement is invalid.'
        $verified.Add([ordered]@{ Name = $spec.Name; FileName = [IO.Path]::GetFileName($path)
            Width = $spec.Width; Height = $spec.Height; Pixels = [long]$spec.Width * $spec.Height
            Transparent = $spec.Transparent; Sha256 = $hash; EncodedBytes = (Get-Item -LiteralPath $path).Length })
    }
    $reopened = Resolve-EvidenceFile $Directory 'reopened-canvas.png'
    Check-Png $reopened 7680 4320
    [void](Check-Hash $reopened $verified[0].Sha256)
    $package = Field $Report 'package'
    Require ((Field $package 'fileName') -ceq 'fixture.ppgproj') 'Unexpected native fixture file name.'
    $packagePath = Resolve-EvidenceFile $Directory (Field $package 'fileName')
    $packageHash = Check-Hash $packagePath (Field $package 'sha256')
    Require-Number $package 'assetEntries' 10
    Require-True $package 'roundTripStateEqual'
    Require-True $package 'sourceUnchanged'
    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $assets = @($archive.Entries | Where-Object { $_.FullName.StartsWith('assets/', [StringComparison]::Ordinal) })
        Require ($assets.Count -eq 10) 'Native fixture does not contain ten actual image payloads.'
        $hashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $encodedBytes = 0L
        foreach ($asset in $assets) {
            $stream = $asset.Open()
            try { Read-PngHeader $stream 2048 2048 } finally { $stream.Dispose() }
            $stream = $asset.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { [void]$hashes.Add([Convert]::ToHexString($sha.ComputeHash($stream))) }
            finally { $sha.Dispose(); $stream.Dispose() }
            $encodedBytes += $asset.Length
        }
        Require ($hashes.Count -eq 10) 'Native fixture image payloads are not distinct.'
        Require-Number $Report 'encodedAssetBytes' $encodedBytes
        $manifest = $archive.GetEntry('project.json')
        Require ($null -ne $manifest -and $manifest.Length -le 4MB) 'Native fixture manifest is missing or unbounded.'
        $reader = [IO.StreamReader]::new($manifest.Open())
        try { $native = $reader.ReadToEnd() | ConvertFrom-Json -Depth 32 } finally { $reader.Dispose() }
        Require-Number $native 'canvasWidth' 7680
        Require-Number $native 'canvasHeight' 4320
        Require (@(Field $native 'layers').Count -eq 200) 'Native fixture does not contain two hundred layers.'
    } finally { $archive.Dispose() }
    foreach ($flag in @('reopenedOutputMatches', 'originalDocumentUnchanged', 'readOnlyOperationsPreservedSession')) {
        Require-True $Report $flag
    }
    $memory = @(Field $Report 'memory')
    Require ($memory.Count -ge 8) 'Phase memory observations are incomplete.'
    foreach ($sample in $memory) {
        Require ((Field $sample 'phase') -is [string] -and (Field $sample 'phase').Length -gt 0) 'Memory phase name is missing.'
        foreach ($metric in @('workingSetBytes', 'privateBytes', 'peakWorkingSetLifetimeBytes',
            'managedHeapBytes', 'totalAllocatedManagedBytes')) {
            Require ((Number $sample $metric) -ge 0) "Invalid memory measurement: $metric"
        }
    }
    Require ((Field $Report 'memoryScope') -is [string] -and (Field $Report 'memoryScope').Length -gt 0) 'Memory measurement scope is missing.'
    [ordered]@{ Outputs = $verified; PackageSha256 = $packageHash; ActualDistinctPackageImages = $hashes.Count
        PreviewSha256 = $cachedHash; MemoryObservations = $memory.Count }
}

$summary = [ordered]@{
    EvidenceVersion = 1; Status = 'FAILED'; Scope = 'AUTOMATED_INTERNAL_LARGE_RESOURCE_ONLY'
    StartedUtc = [DateTime]::UtcNow.ToString('o'); FinishedUtc = $null
    Commit = $null; Branch = $null; Dirty = $null; SourceFiles = $null
    SourceManifestSha256 = $null; SourceUnchanged = $false
    ExePath = $executable; ExecutableSha256Before = $null; ExecutableSha256After = $null
    ExecutableUnchanged = $false; BuildEvidence = $null
    ProcessId = $null; ExitCode = $null; TimedOut = $false; TimeoutSeconds = $TimeoutSeconds
    Stdout = 'stdout.log'; Stderr = 'stderr.log'; Report = 'run/large-resources/large-resource-evidence.json'
    ReportSha256 = $null; Verification = $null; Probe = $null; Error = $null
}
$sourcesBefore = $null
$ownedProcess = $null
$fontVariable = 'PPG_INTERNAL_VERIFY_FONT_DIRECTORY'
$previousFontDirectory = [Environment]::GetEnvironmentVariable($fontVariable, 'Process')
$fontEnvironmentChanged = $false
$failure = $null
try {
    . (Join-Path $repositoryRoot 'new_structure_project/scripts/SourceEvidence.ps1')
    Require (Test-Path -LiteralPath $executable -PathType Leaf) 'ExePath must name an already-built executable.'
    Require ([IO.Path]::GetFileName($executable) -ieq 'Editor.Desktop.exe') 'ExePath must name Editor.Desktop.exe.'
    $summary.Commit = (& git -C $repositoryRoot rev-parse HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the Git commit.' }
    $summary.Branch = (& git -C $repositoryRoot branch --show-current)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the Git branch.' }
    $gitStatus = @(& git -C $repositoryRoot status --porcelain=v1)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the Git status.' }
    $summary.Dirty = $gitStatus.Count -ne 0
    Write-NewText (Join-Path $outputRoot 'git-status.txt') ($gitStatus -join "`n")
    $sourcesBefore = @(Get-AcceptanceSourceManifest $repositoryRoot)
    Write-NewText (Join-Path $outputRoot 'source-before.sha256') ($sourcesBefore -join "`n")
    $summary.SourceFiles = $sourcesBefore.Count
    $summary.SourceManifestSha256 = Get-AcceptanceManifestHash $sourcesBefore
    $summary.ExecutableSha256Before = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    $buildPath = Join-Path ([IO.Path]::GetDirectoryName($executable)) 'build-evidence.json'
    if (Test-Path -LiteralPath $buildPath -PathType Leaf) {
        $build = Get-Content -LiteralPath $buildPath -Raw | ConvertFrom-Json -Depth 16
        Require ((Field $build 'SourceManifestSha256') -ceq $summary.SourceManifestSha256) 'Candidate build evidence belongs to another source snapshot.'
        $summary.BuildEvidence = [ordered]@{ Path = $buildPath
            Sha256 = (Get-FileHash -LiteralPath $buildPath -Algorithm SHA256).Hash
            SourceManifestSha256 = $build.SourceManifestSha256 }
    }
    $runDirectory = Join-Path $outputRoot 'run'
    $fontDirectory = Join-Path $outputRoot 'fonts'
    New-Item -ItemType Directory -Path $fontDirectory | Out-Null
    [Environment]::SetEnvironmentVariable($fontVariable, $fontDirectory, 'Process')
    $fontEnvironmentChanged = $true
    $arguments = @('--performance', ('"' + $runDirectory + '"'), '--large-resources')
    Write-Host 'Large-resource probe: starting the existing candidate in a separate process ...'
    $ownedProcess = Start-Process -FilePath $executable -ArgumentList $arguments -PassThru -WindowStyle Hidden -WorkingDirectory $outputRoot `
        -RedirectStandardOutput (Join-Path $outputRoot $summary.Stdout) -RedirectStandardError (Join-Path $outputRoot $summary.Stderr)
    $summary.ProcessId = $ownedProcess.Id
    if (-not $ownedProcess.WaitForExit($TimeoutSeconds * 1000)) {
        $summary.TimedOut = $true
        $ownedProcess.Kill() # Only the process launched above; no process-name or tree-wide kill.
        $ownedProcess.WaitForExit()
        throw 'The owned probe process exceeded the explicit execution timeout.'
    }
    $ownedProcess.WaitForExit() # Flush redirected output after the timed wait.
    $summary.ExitCode = $ownedProcess.ExitCode
    Require ($summary.ExitCode -eq 0) "The child probe failed with exit code $($summary.ExitCode)."
    $reportDirectory = Join-Path $runDirectory 'large-resources'
    $reportPath = Resolve-EvidenceFile $reportDirectory 'large-resource-evidence.json'
    $summary.ReportSha256 = (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash
    $summary.Probe = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json -Depth 32
    $summary.Verification = Check-ProductionEvidence $summary.Probe $reportDirectory
    $summary.Status = 'PASS'
} catch {
    $failure = $_
    $summary.Error = $_.Exception.Message
    $summary.Status = 'FAILED'
} finally {
    if ($fontEnvironmentChanged) {
        [Environment]::SetEnvironmentVariable($fontVariable, $previousFontDirectory, 'Process')
    }
    if ($null -ne $ownedProcess) { $ownedProcess.Dispose() }
    try {
        if ($null -ne $sourcesBefore) {
            $sourcesAfter = @(Get-AcceptanceSourceManifest $repositoryRoot)
            Write-NewText (Join-Path $outputRoot 'source-after.sha256') ($sourcesAfter -join "`n")
            $summary.SourceUnchanged = (($sourcesBefore -join "`n") -ceq ($sourcesAfter -join "`n"))
        }
        if ($summary.ExecutableSha256Before) {
            $summary.ExecutableSha256After = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
            $summary.ExecutableUnchanged = $summary.ExecutableSha256After -ceq $summary.ExecutableSha256Before
        }
        Require ($null -ne $sourcesBefore -and $summary.SourceUnchanged) 'Working-tree source identity is missing or changed during the large-resource probe.'
        Require ($summary.ExecutableSha256Before -and $summary.ExecutableUnchanged) 'Candidate executable identity is missing or changed during the large-resource probe.'
        if ($summary.BuildEvidence) {
            [void](Check-Hash $summary.BuildEvidence.Path $summary.BuildEvidence.Sha256)
        }
    } catch {
        $summary.Status = 'FAILED'
        $summary.Error = if ($summary.Error) { $summary.Error + ' Identity verification: ' + $_.Exception.Message } else { $_.Exception.Message }
        if ($null -eq $failure) { $failure = $_ }
    }
    $summary.FinishedUtc = [DateTime]::UtcNow.ToString('o')
    Write-NewText (Join-Path $outputRoot 'large-resource-summary.json') ($summary | ConvertTo-Json -Depth 32)
}
if ($null -ne $failure) { throw $failure }
Require ($summary.Status -ceq 'PASS') 'Large-resource verification failed.'
Write-Host "LARGE RESOURCE TECHNICAL PASS: $outputRoot/large-resource-summary.json"
Write-Host 'Memory values are local measurements; physical Windows, DPI/IME and publication acceptance are separate.'

param([Parameter(Mandatory = $true)] [string] $OutputRoot)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$outputRoot = [IO.Path]::GetFullPath($OutputRoot)
if ($outputRoot.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $outputRoot.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Test output must be outside the repository.'
}
if (Test-Path -LiteralPath $outputRoot) {
    if (-not (Test-Path -LiteralPath $outputRoot -PathType Container) -or
        (Get-ChildItem -LiteralPath $outputRoot -Force | Select-Object -First 1)) { throw 'Test output must be new or empty.' }
} else { New-Item -ItemType Directory -Path $outputRoot | Out-Null }
. (Join-Path $PSScriptRoot 'ReleasePackageIntegrity.ps1')
$verifyScript = Join-Path $PSScriptRoot 'Verify-ReleasePackage.ps1'
$results = [Collections.Generic.List[object]]::new()
$testError = $null
function Check([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Put-Json([string] $Path, $Value) {
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8
}
function Read-Catalog([string] $Root, [string] $Folder = 'release') {
    return Get-Content -LiteralPath (Join-Path $Root "$Folder/candidate-files.json") -Raw | ConvertFrom-Json -AsHashtable
}
function Write-ArchiveHash([string] $Root) {
    $archive = Join-Path $Root 'PhigrosLayoutStudio-1.0-win-x64.zip'
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    "$hash  PhigrosLayoutStudio-1.0-win-x64.zip" | Set-Content -LiteralPath ($archive + '.sha256') -Encoding ascii
}
function Write-Archive([string] $Root, [string] $Folder = 'release') {
    $archive = Join-Path $Root 'PhigrosLayoutStudio-1.0-win-x64.zip'
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
    [IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $Root $Folder), $archive)
    Write-ArchiveHash $Root
}
function Write-ZipEntry($Zip, [string] $Path, [string] $Content) {
    $entry = $Zip.CreateEntry($Path)
    $stream = $entry.Open()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Content)
        $stream.Write($bytes, 0, $bytes.Length)
    } finally { $stream.Dispose() }
}
function Change-Zip([string] $Root, [scriptblock] $Action) {
    $archive = Join-Path $Root 'PhigrosLayoutStudio-1.0-win-x64.zip'
    $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Update)
    try { & $Action $zip } finally { $zip.Dispose() }
    Write-ArchiveHash $Root
}
function New-Fixture([string] $Name) {
    $root = Join-Path $outputRoot $Name
    $release = Join-Path $root 'release'
    New-Item -ItemType Directory -Path (Join-Path $release 'notices') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $release 'Editor.Desktop.exe'), 'text fixture only; never executed')
    [IO.File]::WriteAllText((Join-Path $release 'README.txt'), 'reviewed README')
    [IO.File]::WriteAllText((Join-Path $release 'notices/LICENSE.txt'), 'synthetic license fixture')
    $files = @(Get-ChildItem -LiteralPath $release -Recurse -File | ForEach-Object {
        @{ Path = [IO.Path]::GetRelativePath($release, $_.FullName).Replace('\', '/')
            Length = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    Put-Json (Join-Path $release 'candidate-files.json') @{
        EvidenceVersion = 1; SourceManifestSha256 = ('A' * 64); FileCount = $files.Count; Files = $files }
    Copy-Item -LiteralPath $release -Destination (Join-Path $root 'extracted') -Recurse
    Write-Archive $root
    return $root
}
function Must-Reject([string] $Root, [string] $Reason) {
    $message = $null
    try { Assert-ReleasePackageIntegrity $Root | Out-Null } catch { $message = $_.Exception.Message }
    Check ($null -ne $message -and $message -match $Reason) "Expected rejection /$Reason/, got: $message"
    # Exercise the public verifier too: every structural failure must be reported before EXE startup.
    $failed = $false
    try { & $verifyScript -AssemblyRoot $Root | Out-Null } catch { $failed = $true }
    Check $failed 'The public verifier accepted an invalid package.'
    $report = Get-Content -LiteralPath (Join-Path $Root 'release-verification.json') -Raw | ConvertFrom-Json
    Check ($report.Status -eq 'FAILED' -and $report.RuntimeAttempted -eq $false -and @($report.Errors).Count -gt 0) 'Failed report missing, or runtime was attempted.'
    Check (-not (Test-Path -LiteralPath (Join-Path $Root 'final-runtime-verification'))) 'Invalid package started runtime verification.'
}
function Test-Case([string] $Name, [scriptblock] $Action) {
    try { & $Action; $results.Add(@{ Name = $Name; Status = 'PASS'; Error = $null }); Write-Host "PASS: $Name" }
    catch { $results.Add(@{ Name = $Name; Status = 'FAILED'; Error = $_.Exception.Message }); throw }
}

try {
    Test-Case 'complete package binds one reviewed inventory including catalog bytes' {
        $root = New-Fixture 'complete'
        $result = Assert-ReleasePackageIntegrity $root
        Check ($result -is [Collections.IDictionary] -and $result.PublishedFiles -eq 4) 'Integrity result is not one record with the correct count.'
        Check ($result.CatalogSha256 -eq (Get-FileHash -LiteralPath (Join-Path $root 'release/candidate-files.json')).Hash) 'Catalog itself is not fingerprinted.'
        Check ($result.ArchiveSha256 -eq (Get-FileHash -LiteralPath (Join-Path $root 'PhigrosLayoutStudio-1.0-win-x64.zip')).Hash) 'Archive hash is incorrect.'
    }
    foreach ($folder in @('release', 'extracted')) {
        foreach ($change in @('extra', 'missing', 'hash', 'length')) {
            Test-Case "$folder-$change" {
                $root = New-Fixture "$folder-$change"
                $path = Join-Path $root "$folder/README.txt"
                switch ($change) {
                    'extra' { [IO.File]::WriteAllText((Join-Path $root "$folder/extra.txt"), 'extra') }
                    'missing' { Remove-Item -LiteralPath $path }
                    'hash' { [IO.File]::WriteAllText($path, 'modified README') }
                    'length' { [IO.File]::AppendAllText($path, 'longer') }
                }
                Must-Reject $root "$folder (has extra or missing|differs from reviewed catalog)"
            }
        }
    }
    Test-Case 'reviewed release cannot be bypassed by rewriting extracted catalog README ZIP and external checksum' {
        $root = New-Fixture 'old-audit-negative'
        $reviewedHash = (Get-FileHash -LiteralPath (Join-Path $root 'release/candidate-files.json')).Hash
        $readme = Join-Path $root 'extracted/README.txt'
        [IO.File]::WriteAllText($readme, 'unreviewed changed README')
        $catalog = Read-Catalog $root 'extracted'
        $record = $catalog.Files | Where-Object Path -eq 'README.txt'
        $record.Length = (Get-Item -LiteralPath $readme).Length
        $record.Sha256 = (Get-FileHash -LiteralPath $readme).Hash
        Put-Json (Join-Path $root 'extracted/candidate-files.json') $catalog
        Write-Archive $root 'extracted'
        Must-Reject $root 'extracted differs from reviewed catalog'
        Check ($reviewedHash -eq (Get-FileHash -LiteralPath (Join-Path $root 'release/candidate-files.json')).Hash) 'Reviewed release was changed by the test.'
    }
    Test-Case 'extracted catalog bytes cannot be reformatted independently' {
        $root = New-Fixture 'catalog-bytes'
        [IO.File]::AppendAllText((Join-Path $root 'extracted/candidate-files.json'), ' ')
        Must-Reject $root 'extracted differs from reviewed catalog: candidate-files.json'
    }
    $invalidPaths = @('../outside.txt', '/absolute.txt', 'C:/outside.txt', 'notices\LICENSE.txt',
        'notices/../README.txt', 'notices//LICENSE.txt', './README.txt', 'README.txt.', 'README.txt ',
        'CON.txt', 'NUL', 'file:ads', 'a?b', 'notices/', '')
    for ($index = 0; $index -lt $invalidPaths.Count; $index++) {
        Test-Case "catalog-invalid-path-$index" {
            $root = New-Fixture "catalog-invalid-path-$index"
            $catalog = Read-Catalog $root
            $catalog.Files[0].Path = $invalidPaths[$index]
            Put-Json (Join-Path $root 'release/candidate-files.json') $catalog
            Must-Reject $root 'Invalid package relative path'
        }
    }
    foreach ($change in @('duplicate', 'case-collision', 'self', 'count', 'hash', 'length', 'unknown-field', 'missing-field', 'version')) {
        Test-Case "catalog-$change" {
            $root = New-Fixture "catalog-$change"
            $catalog = Read-Catalog $root
            switch ($change) {
                'duplicate' { $catalog.Files += $catalog.Files[0]; $catalog.FileCount++ }
                'case-collision' { $copy = $catalog.Files[0].Clone(); $copy.Path = $copy.Path.ToUpperInvariant(); $catalog.Files += $copy; $catalog.FileCount++ }
                'self' { $catalog.Files[0].Path = 'candidate-files.json' }
                'count' { $catalog.FileCount++ }
                'hash' { $catalog.Files[0].Sha256 = 'invalid' }
                'length' { $catalog.Files[0].Length = -1 }
                'unknown-field' { $catalog.Files[0].Extra = 1 }
                'missing-field' { [void] $catalog.Remove('SourceManifestSha256') }
                'version' { $catalog.EvidenceVersion = 2 }
            }
            Put-Json (Join-Path $root 'release/candidate-files.json') $catalog
            Must-Reject $root 'catalog'
        }
    }
    Test-Case 'malformed catalog overwrites stale PASS with failed report' {
        $root = New-Fixture 'malformed-json'
        Put-Json (Join-Path $root 'release-verification.json') @{ Status = 'PASS'; Errors = @() }
        [IO.File]::WriteAllText((Join-Path $root 'release/candidate-files.json'), '{invalid')
        Must-Reject $root 'Parse|JSON'
    }
    Test-Case 'duplicate JSON key is rejected' {
        $root = New-Fixture 'duplicate-json-key'
        $path = Join-Path $root 'release/candidate-files.json'
        $json = [IO.File]::ReadAllText($path).Replace('"EvidenceVersion": 1', '"EvidenceVersion": 1, "EvidenceVersion": 1')
        [IO.File]::WriteAllText($path, $json)
        Must-Reject $root 'Duplicate package JSON key'
    }
    foreach ($change in @('extra', 'missing', 'duplicate', 'case-collision', 'traversal', 'backslash', 'hash', 'length', 'catalog-bytes', 'directory', 'symlink')) {
        Test-Case "zip-$change" {
            $root = New-Fixture "zip-$change"
            Change-Zip $root {
                param($zip)
                switch ($change) {
                    'extra' { Write-ZipEntry $zip 'extra.txt' 'extra' }
                    'missing' { $zip.GetEntry('README.txt').Delete() }
                    'duplicate' { Write-ZipEntry $zip 'README.txt' 'reviewed README' }
                    'case-collision' { Write-ZipEntry $zip 'readme.txt' 'reviewed README' }
                    'traversal' { Write-ZipEntry $zip '../escape.txt' 'outside' }
                    'backslash' { Write-ZipEntry $zip 'notices\LICENSE.txt' 'synthetic license fixture' }
                    'hash' { $zip.GetEntry('README.txt').Delete(); Write-ZipEntry $zip 'README.txt' 'modified README' }
                    'length' { $zip.GetEntry('README.txt').Delete(); Write-ZipEntry $zip 'README.txt' 'longer changed README' }
                    'catalog-bytes' {
                        $entry = $zip.GetEntry('candidate-files.json'); $entry.Delete()
                        Write-ZipEntry $zip 'candidate-files.json' ([IO.File]::ReadAllText((Join-Path $root 'release/candidate-files.json')) + ' ')
                    }
                    'directory' { Write-ZipEntry $zip 'extra/' '' }
                    'symlink' { $zip.GetEntry('README.txt').ExternalAttributes = -1577123840 }
                }
            }
            Must-Reject $root 'ZIP|relative path'
        }
    }
    foreach ($change in @('missing-checksum', 'wrong-checksum', 'wrong-checksum-name', 'corrupt-zip', 'missing-release', 'missing-extracted')) {
        Test-Case $change {
            $root = New-Fixture $change
            $archive = Join-Path $root 'PhigrosLayoutStudio-1.0-win-x64.zip'
            switch ($change) {
                'missing-checksum' { Remove-Item -LiteralPath ($archive + '.sha256') }
                'wrong-checksum' { ('B' * 64) + '  PhigrosLayoutStudio-1.0-win-x64.zip' | Set-Content -LiteralPath ($archive + '.sha256') }
                'wrong-checksum-name' { (Get-FileHash -LiteralPath $archive).Hash + '  other.zip' | Set-Content -LiteralPath ($archive + '.sha256') }
                'corrupt-zip' { [IO.File]::WriteAllText($archive, 'corrupt ZIP'); Write-ArchiveHash $root }
                'missing-release' { Move-Item -LiteralPath (Join-Path $root 'release') -Destination (Join-Path $root 'saved-release') }
                'missing-extracted' { Move-Item -LiteralPath (Join-Path $root 'extracted') -Destination (Join-Path $root 'saved-extracted') }
            }
            Must-Reject $root '.'
        }
    }
    Test-Case 'metadata failure also prevents executable startup' {
        $root = New-Fixture 'invalid-metadata'
        $failed = $false
        try { & $verifyScript -AssemblyRoot $root | Out-Null } catch { $failed = $true }
        $report = Get-Content -LiteralPath (Join-Path $root 'release-verification.json') -Raw | ConvertFrom-Json
        Check ($failed -and $report.Status -eq 'FAILED' -and -not $report.RuntimeAttempted) 'Invalid metadata allowed application startup.'
    }
} catch { $testError = $_.Exception.Message }
finally {
    $passed = @($results | Where-Object Status -eq 'PASS').Count
    $failed = @($results | Where-Object Status -eq 'FAILED').Count
    [ordered]@{ Status = if ($null -eq $testError -and $failed -eq 0 -and $passed -gt 0) { 'PASS' } else { 'FAILED' }
        Error = $testError; Checks = $results.Count; Passed = $passed; Failed = $failed; Tests = @($results) } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $outputRoot 'release-integrity-tests.json') -Encoding utf8
}
if ($null -ne $testError) { throw $testError }
Write-Host "Release integrity script tests: $passed PASS"

param([Parameter(Mandatory = $true)] [string] $OutputRoot)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\', '/')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\', '/')
if ($root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\', '/') -or
    $root.Equals($repository, [StringComparison]::OrdinalIgnoreCase) -or
    $root.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    $repository.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Test output must be outside the repository and cannot be its ancestor or a volume root.'
}
$cursor = $root
while ($cursor) {
    if (Test-Path -LiteralPath $cursor) {
        $item = Get-Item -LiteralPath $cursor -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Test output ancestors must not be reparse points.' }
    }
    $parent = [IO.Path]::GetDirectoryName($cursor)
    if ($parent -eq $cursor) { break }
    $cursor = $parent
}
if (Test-Path -LiteralPath $root) {
    if (-not (Test-Path -LiteralPath $root -PathType Container) -or
        (Get-ChildItem -LiteralPath $root -Force | Select-Object -First 1)) { throw 'Test output must be new or empty.' }
} else { [void](New-Item -ItemType Directory -Path $root) }
. (Join-Path $PSScriptRoot 'TestArtifactRetention.ps1')
$results = [Collections.Generic.List[object]]::new()
$completed = $false
$failure = $null
function Check([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Put([string] $Path, [string] $Value) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}
function Hash([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function New-Candidate([string] $Parent, [string] $Name, [int] $Day, [string] $ExecutableSource) {
    $portable = Join-Path $Parent "$Name/portable"
    $publish = Join-Path $portable 'publish'
    Put (Join-Path $publish 'Editor.Desktop.exe') "synthetic exe $Name"
    if ($ExecutableSource) { Copy-Item -LiteralPath $ExecutableSource -Destination (Join-Path $publish 'Editor.Desktop.exe') }
    Put (Join-Path $publish 'a.dll') "synthetic dll $Name"
    Put (Join-Path $publish 'build-evidence.json') '{"CandidateStatus":"INTERNAL_ONLY"}'
    Put (Join-Path $publish 'candidate-files.json') '{"EvidenceVersion":1}'
    Put (Join-Path $publish 'notices/keep.txt') 'synthetic notice'
    Put (Join-Path $portable 'symbols/a.pdb') "synthetic symbol $Name"
    $entries = @(Get-ChildItem -LiteralPath $publish -File -Recurse | Sort-Object FullName | ForEach-Object {
        "$(Hash $_.FullName)  $([IO.Path]::GetRelativePath($publish, $_.FullName))"
    })
    Put (Join-Path $portable 'publish-sha256.txt') ($entries -join "`n")
    Put (Join-Path $portable 'symbols-sha256.txt') "$(Hash (Join-Path $portable 'symbols/a.pdb'))  a.pdb"
    $archive = Join-Path $portable 'Editor.Desktop-win-x64-internal.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($publish, $archive)
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, (Join-Path $portable 'extracted'))
    $source = ('A' * 64) + '  synthetic.cs'
    Put (Join-Path $portable 'source-before.sha256') $source
    Put (Join-Path $portable 'source-after.sha256') $source
    $sourceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($source)))
    $summary = [ordered]@{ Status = 'PASS'; Error = $null; SourceUnchanged = $true; PublishExitCode = 0
        SourceFiles = 1; SourceManifestSha256 = $sourceHash; FinishedUtc = "2026-10-$('{0:00}' -f $Day)T01:00:00Z"
        Runtime = @{ Status = 'PASS'; PackageUnchanged = $true }
        Package = @{ Archive = $archive; ArchiveSha256 = (Hash $archive); PublishedFiles = $entries.Count
            PublishedBytes = [long]((Get-ChildItem -LiteralPath $publish -Recurse -File | Measure-Object Length -Sum).Sum)
            SymbolFiles = 1; PublishManifestSha256 = (Hash (Join-Path $portable 'publish-sha256.txt'))
            SymbolManifestSha256 = (Hash (Join-Path $portable 'symbols-sha256.txt')) } }
    Put (Join-Path $portable 'portable-summary.json') ($summary | ConvertTo-Json -Depth 6)
    Put (Join-Path $Parent "$Name/results.log") 'keep log'
    Put (Join-Path $Parent "$Name/results.trx") 'keep trx'
    Put (Join-Path $Parent "$Name/image.png") 'keep image'
    Put (Join-Path $Parent "$Name/fixture.ppgproj") 'keep project'
    return $portable
}
function Case([string] $Name, [scriptblock] $Action) {
    try { & $Action; $results.Add([pscustomobject]@{ Name = $Name; Status = 'PASS' }); Write-Host "PASS: $Name" }
    catch { $results.Add([pscustomobject]@{ Name = $Name; Status = 'FAILED'; Error = $_.Exception.Message }); throw }
}
function Reject([scriptblock] $Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Check $rejected 'Unsafe operation was accepted.'
}
try {
    $parent = Join-Path $root 'rotation'
    $first = New-Candidate $parent 'first' 1
    $second = New-Candidate $parent 'second' 2
    $third = New-Candidate $parent 'third' 3
    $summaryHashes = @($first, $second, $third | ForEach-Object { Hash (Join-Path $_ 'portable-summary.json') })
    Case 'preview is one object and never deletes or writes receipts' {
        $report = Invoke-TestArtifactRetention -ArtifactRoot $parent
        Check ($report -is [pscustomobject] -and $report.Status -eq 'PREVIEW') 'Unexpected preview return shape.'
        Check (@($report.Planned).Count -eq 9) ('Expected nine binary copies; got ' + @($report.Planned).Count + ' ' + ($report.Candidates | ConvertTo-Json -Depth 3))
        Check (Test-Path -LiteralPath (Join-Path $first 'publish/a.dll')) 'Preview deleted a file.'
        Check (-not (Test-Path -LiteralPath (Join-Path $parent 'retention-receipts'))) 'Preview created receipts.'
    }
    Case 'apply keeps ZIPs, metadata, newest two and all evidence' {
        $report = Invoke-TestArtifactRetention -ArtifactRoot $parent -Apply
        Check ($report.Status -eq 'COMPLETE' -and @($report.Deleted).Count -eq 9 -and $report.DeletedBytes -gt 0) 'Deletion result incomplete.'
        Check (Test-Path -LiteralPath $report.Receipt) 'Cleanup receipt is missing.'
        Check (-not (Test-Path -LiteralPath (Join-Path $first 'extracted/a.dll'))) 'Old extracted DLL survived.'
        foreach ($portable in @($first, $second, $third)) {
            foreach ($relative in @('Editor.Desktop-win-x64-internal.zip', 'publish/build-evidence.json',
                'extracted/build-evidence.json', 'extracted/candidate-files.json', 'publish/notices/keep.txt',
                'extracted/notices/keep.txt', 'publish-sha256.txt', 'symbols-sha256.txt')) {
                Check (Test-Path -LiteralPath (Join-Path $portable $relative)) "Evidence removed: $relative"
            }
        }
        foreach ($portable in @($second, $third)) {
            Check (Test-Path -LiteralPath (Join-Path $portable 'extracted/a.dll')) 'Recent extracted binary removed.'
            Check (Test-Path -LiteralPath (Join-Path $portable 'symbols/a.pdb')) 'Recent symbols removed.'
        }
        foreach ($relative in @('results.log', 'results.trx', 'image.png', 'fixture.ppgproj')) {
            Check (Test-Path -LiteralPath (Join-Path $parent "first/$relative")) 'Test evidence was deleted.'
        }
        $after = @($first, $second, $third | ForEach-Object { Hash (Join-Path $_ 'portable-summary.json') })
        Check (($summaryHashes -join ',') -ceq ($after -join ',')) 'Historical summary was rewritten.'
    }
    Case 'repeat is a no-op and later candidates rotate retained copies' {
        $report = Invoke-TestArtifactRetention -ArtifactRoot $parent -Apply
        Check (@($report.Planned).Count -eq 0 -and @($report.Deleted).Count -eq 0) 'Repeat planned deletion or rejected prior receipts.'
        Check (@($report.Candidates | Where-Object State -eq 'PRUNED').Count -eq 1) 'Fully pruned candidate was not recognised.'
        [void](New-Candidate $parent 'fourth' 4)
        $rotated = Invoke-TestArtifactRetention -ArtifactRoot $parent -Apply
        Check ($rotated.Status -eq 'COMPLETE' -and @($rotated.Deleted).Count -eq 5) 'New candidate did not rotate oldest retained copy.'
        Check (-not (Test-Path -LiteralPath (Join-Path $second 'extracted/a.dll'))) 'Old second candidate was not rotated.'
    }
    Case 'PublishOnly deletes just that candidate publish copies' {
        $parent = Join-Path $root 'publish-only'
        $a = New-Candidate $parent 'a' 1
        $b = New-Candidate $parent 'b' 2
        $report = Invoke-TestArtifactRetention -ArtifactRoot $parent -PublishOnly $a -KeepLatest 0 -Apply
        Check (@($report.Deleted).Count -eq 2) 'PublishOnly deleted an incorrect set.'
        Check (Test-Path -LiteralPath (Join-Path $a 'extracted/a.dll')) 'PublishOnly removed extracted binary.'
        Check (Test-Path -LiteralPath (Join-Path $b 'publish/a.dll')) 'PublishOnly touched another candidate.'
    }
    Case 'standalone cleanup receipts support later parent rotation' {
        $parent = Join-Path $root 'nested-receipts'
        $portable = New-Candidate $parent 'candidate' 1
        $first = Invoke-TestArtifactRetention -ArtifactRoot (Join-Path $parent 'candidate') -PublishOnly $portable -Apply
        Check (@($first.Deleted).Count -eq 2) 'Standalone publish cleanup did not run.'
        $second = Invoke-TestArtifactRetention -ArtifactRoot $parent -KeepLatest 0 -Apply
        Check (@($second.Deleted).Count -eq 3) 'Parent could not use standalone cleanup receipts.'
        $third = Invoke-TestArtifactRetention -ArtifactRoot $parent -KeepLatest 0 -Apply
        Check (@($third.Deleted).Count -eq 0 -and $third.Candidates[0].State -eq 'PRUNED') 'Rotated candidate did not fast-skip.'
    }
    foreach ($scenario in @('extra', 'changed', 'bad-zip', 'failed', 'missing', 'pin', 'traversal', 'source-string', 'runtime-string')) {
        Case "unsafe or preserved candidate stays intact: $scenario" {
            $parent = Join-Path $root $scenario
            $portable = New-Candidate $parent 'candidate' 1
            switch ($scenario) {
                'extra' { Put (Join-Path $portable 'publish/user.txt') 'do not delete' }
                'changed' { Put (Join-Path $portable 'publish/a.dll') 'edited binary' }
                'bad-zip' { Put (Join-Path $portable 'Editor.Desktop-win-x64-internal.zip') 'bad zip' }
                'failed' {
                    $summary = Get-Content -LiteralPath (Join-Path $portable 'portable-summary.json') -Raw | ConvertFrom-Json
                    $summary.Status = 'FAILED'
                    Put (Join-Path $portable 'portable-summary.json') ($summary | ConvertTo-Json -Depth 6)
                }
                'source-string' {
                    $summary = Get-Content -LiteralPath (Join-Path $portable 'portable-summary.json') -Raw | ConvertFrom-Json
                    $summary.SourceUnchanged = 'True'
                    Put (Join-Path $portable 'portable-summary.json') ($summary | ConvertTo-Json -Depth 6)
                }
                'runtime-string' {
                    $summary = Get-Content -LiteralPath (Join-Path $portable 'portable-summary.json') -Raw | ConvertFrom-Json
                    $summary.Runtime.PackageUnchanged = 'True'
                    Put (Join-Path $portable 'portable-summary.json') ($summary | ConvertTo-Json -Depth 6)
                }
                'missing' { [IO.File]::Delete((Join-Path $portable 'publish/a.dll')) }
                'pin' { Put (Join-Path $portable '.keep-test-artifacts') 'review pending' }
                'traversal' {
                    Put (Join-Path $portable 'publish-sha256.txt') ((('A' * 64) + '  ../outside.dll'))
                    $summary = Get-Content -LiteralPath (Join-Path $portable 'portable-summary.json') -Raw | ConvertFrom-Json
                    $summary.Package.PublishManifestSha256 = Hash (Join-Path $portable 'publish-sha256.txt')
                    Put (Join-Path $portable 'portable-summary.json') ($summary | ConvertTo-Json -Depth 6)
                }
            }
            $report = Invoke-TestArtifactRetention -ArtifactRoot $parent -KeepLatest 0 -Apply
            Check (@($report.Deleted).Count -eq 0) "Unsafe candidate was deleted: $scenario"
            Check (Test-Path -LiteralPath (Join-Path $portable 'publish/Editor.Desktop.exe')) 'Candidate executable was removed.'
        }
    }
    Case 'volume roots, repositories, caches and PublishOnly escapes are rejected' {
        Reject { Invoke-TestArtifactRetention -ArtifactRoot ([IO.Path]::GetPathRoot($root)) -Apply }
        Reject { Invoke-TestArtifactRetention -ArtifactRoot $repository -Apply }
        Reject { Invoke-TestArtifactRetention -ArtifactRoot $root -PublishOnly ([IO.Path]::GetDirectoryName($root)) -Apply }
        if (Test-Path -LiteralPath 'E:\NuGet') { Reject { Invoke-TestArtifactRetention -ArtifactRoot 'E:\NuGet' -Apply } }
    }
    if ($IsWindows) {
        Case 'a pin created after preview prevents the deletion batch' {
            $parent = Join-Path $root 'late-pin'
            $script:latePinPortable = New-Candidate $parent 'candidate' 1
            $script:latePinScans = 0
            function Get-Process {
                $script:latePinScans++
                if ($script:latePinScans -eq 2) { Put (Join-Path $script:latePinPortable '.keep-test-artifacts') 'created after preview' }
                Microsoft.PowerShell.Management\Get-Process
            }
            $report = Invoke-TestArtifactRetention -ArtifactRoot $parent -KeepLatest 0 -Apply
            Check ($script:latePinScans -ge 2 -and @($report.Deleted).Count -eq 0 -and
                $report.Candidates[0].State -eq 'PINNED') 'Late pin was not honoured.'
        }
        Case 'activity is refreshed after preview and prevents deletion' {
            $parent = Join-Path $root 'late-active'
            $script:lateActivePortable = New-Candidate $parent 'candidate' 1 (Join-Path $env:WINDIR 'System32/ping.exe')
            $script:lateActiveScans = 0
            $script:lateActiveOwned = $null
            function Get-Process {
                $script:lateActiveScans++
                if ($script:lateActiveScans -eq 2) {
                    $script:lateActiveOwned = Start-Process -FilePath (Join-Path $script:lateActivePortable 'extracted/Editor.Desktop.exe') `
                        -ArgumentList @('-t', '127.0.0.1') -WindowStyle Hidden -PassThru `
                        -RedirectStandardOutput (Join-Path $root 'late-active-stdout.txt') `
                        -RedirectStandardError (Join-Path $root 'late-active-stderr.txt')
                }
                Microsoft.PowerShell.Management\Get-Process
            }
            try {
                $report = Invoke-TestArtifactRetention -ArtifactRoot $parent -KeepLatest 0 -Apply
                Check ($script:lateActiveScans -ge 2 -and @($report.Deleted).Count -eq 0 -and
                    $report.Candidates[0].State -eq 'ACTIVE') 'Late activity was not honoured.'
            } finally {
                if ($script:lateActiveOwned) {
                    if (-not $script:lateActiveOwned.HasExited) { $script:lateActiveOwned.Kill(); $script:lateActiveOwned.WaitForExit() }
                    $script:lateActiveOwned.Dispose()
                }
            }
        }
        Case 'a running candidate is retained without stopping it' {
            $parent = Join-Path $root 'active'
            $portable = New-Candidate $parent 'candidate' 1 (Join-Path $env:WINDIR 'System32/ping.exe')
            $owned = Start-Process -FilePath (Join-Path $portable 'extracted/Editor.Desktop.exe') `
                -ArgumentList @('-t', '127.0.0.1') -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput (Join-Path $root 'active-stdout.txt') `
                -RedirectStandardError (Join-Path $root 'active-stderr.txt')
            try {
                Check (-not $owned.HasExited) 'Owned test process exited too early.'
                $report = Invoke-TestArtifactRetention -ArtifactRoot $parent -KeepLatest 0 -Apply
                Check (@($report.Deleted).Count -eq 0 -and $report.Candidates[0].State -eq 'ACTIVE') 'Running candidate was not retained.'
                Check (-not $owned.HasExited) 'Retention stopped a running process.'
            } finally {
                if (-not $owned.HasExited) { $owned.Kill(); $owned.WaitForExit() }
                $owned.Dispose()
            }
        }
        Case 'junction ancestors and candidate contents are rejected' {
            $parent = Join-Path $root 'junction'
            $portable = New-Candidate $parent 'candidate' 1
            $target = Join-Path $root 'junction-target'
            [void](New-Item -ItemType Directory -Path $target)
            $link = Join-Path $parent 'redirect'
            [void](New-Item -ItemType Junction -Path $link -Target $target)
            Reject { Invoke-TestArtifactRetention -ArtifactRoot $link -Apply }
            Reject { & (Join-Path $PSScriptRoot 'Test-TestArtifactRetention.ps1') -OutputRoot (Join-Path $link 'child') }
            Check (-not (Test-Path -LiteralPath (Join-Path $target 'child'))) 'Test setup wrote through a junction ancestor.'
            [void](New-Item -ItemType Junction -Path (Join-Path $portable 'publish/redirect') -Target $target)
            $report = Invoke-TestArtifactRetention -ArtifactRoot $parent -KeepLatest 0 -Apply
            Check (@($report.Deleted).Count -eq 0) 'Candidate containing a junction was cleaned.'
        }
        foreach ($sourceEvidenceName in @('source-before.sha256', 'source-after.sha256')) {
            Case "source evidence junction is rejected: $sourceEvidenceName" {
                $parent = Join-Path $root ($sourceEvidenceName + '-junction')
                $portable = New-Candidate $parent 'candidate' 1
                $target = Join-Path $root ($sourceEvidenceName + '-target')
                [void](New-Item -ItemType Directory -Path $target)
                $path = Join-Path $portable $sourceEvidenceName
                [IO.File]::Delete($path)
                [void](New-Item -ItemType Junction -Path $path -Target $target)
                $report = Invoke-TestArtifactRetention -ArtifactRoot $parent -KeepLatest 0 -Apply
                Check (@($report.Deleted).Count -eq 0 -and $report.Candidates[0].Reason -match 'Reparse') 'Redirected source evidence was not rejected.'
            }
        }
    }
    $completed = $true
} catch {
    $failure = $_.Exception.Message
    throw
} finally {
    $record = [ordered]@{ Status = if ($completed -and $results.Count -gt 0 -and
        @($results | Where-Object Status -eq 'FAILED').Count -eq 0) { 'PASS' } else { 'FAILED' }
        Checks = $results.Count; Error = $failure; Results = @($results.ToArray()) }
    Put (Join-Path $root 'retention-script-tests.json') ($record | ConvertTo-Json -Depth 8)
}
Write-Host "RETENTION SCRIPT TESTS PASS: $($results.Count) checks; synthetic local files only."

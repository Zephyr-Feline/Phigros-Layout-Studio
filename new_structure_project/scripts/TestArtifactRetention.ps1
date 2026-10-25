# Only verified, reproducible candidate binaries are eligible. Reports and ZIPs remain evidence.
function Invoke-TestArtifactRetention {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $ArtifactRoot,
        [ValidateRange(0, 100)] [int] $KeepLatest = 2,
        [switch] $Apply,
        [string] $PublishOnly
    )
    $ErrorActionPreference = 'Stop'
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\', '/')
    function Full([string] $Path) {
        $absolute = [IO.Path]::GetFullPath($Path)
        if ($absolute -eq [IO.Path]::GetPathRoot($absolute)) { return $absolute }
        $absolute.TrimEnd('\', '/')
    }
    function Require([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
    function Within([string] $Path, [string] $Root) {
        $Path.Equals($Root, [StringComparison]::OrdinalIgnoreCase) -or
            $Path.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    }
    function No-Reparse([string] $Path) {
        $cursor = $Path
        while ($cursor) {
            if (Test-Path -LiteralPath $cursor) {
                $item = Get-Item -LiteralPath $cursor -Force
                Require (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "Reparse point is not eligible: $cursor"
            }
            $next = [IO.Path]::GetDirectoryName($cursor)
            if ($next -eq $cursor) { break }
            $cursor = $next
        }
    }
    function Hash([string] $Path) { No-Reparse $Path; (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
    function Need($Record, [string] $Name) {
        Require ($null -ne $Record -and $null -ne $Record.PSObject.Properties[$Name]) "Missing evidence field: $Name"
        $Record.PSObject.Properties[$Name].Value
    }
    function Safe-Relative([string] $Path) {
        Require (-not [string]::IsNullOrWhiteSpace($Path) -and -not [IO.Path]::IsPathRooted($Path) -and
            $Path -notmatch '[:\x00-\x1f]' -and $Path -notmatch '^[\\/]') "Unsafe manifest path: $Path"
        $parts = $Path -split '[\\/]'
        Require (@($parts | Where-Object { $_ -in @('', '.', '..') -or $_.EndsWith(' ') -or $_.EndsWith('.') }).Count -eq 0) "Unsafe manifest path: $Path"
        $parts -join '/'
    }
    function Tree-Files([string] $Root) {
        No-Reparse $Root
        Require (Test-Path -LiteralPath $Root -PathType Container) "Missing candidate directory: $Root"
        $stack = [Collections.Generic.Stack[string]]::new()
        $stack.Push($Root)
        while ($stack.Count) {
            foreach ($item in Get-ChildItem -LiteralPath $stack.Pop() -Force) {
                Require (Within $item.FullName $Root) 'Candidate file leaves its directory.'
                Require (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "Reparse point is not eligible: $($item.FullName)"
                if ($item.PSIsContainer) { $stack.Push($item.FullName) } else { $item }
            }
        }
    }
    function Read-Manifest([string] $Path) {
        No-Reparse $Path
        $result = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($line in Get-Content -LiteralPath $Path) {
            Require ($line -match '^([A-Fa-f0-9]{64})  (.+)$') "Invalid manifest: $Path"
            $sha = $Matches[1].ToUpperInvariant()
            $relative = Safe-Relative $Matches[2]
            Require (-not $result.ContainsKey($relative)) "Duplicate manifest path: $relative"
            $result.Add($relative, $sha)
        }
        Require ($result.Count -gt 0) "Empty manifest: $Path"
        return ,$result
    }
    function Write-NewReceipt([string] $Path, $Record) {
        No-Reparse $Path
        Require (Within $Path $root) 'Receipt leaves ArtifactRoot.'
        $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        $stream = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Record | ConvertTo-Json -Depth 12))
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        } finally { $stream.Dispose() }
        try { [IO.File]::Move($temporary, $Path) }
        finally { if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) } }
    }
    function Find-Active {
        $paths = [Collections.Generic.List[string]]::new()
        foreach ($process in Get-Process) {
            try { if ($process.Path) { $paths.Add((Full $process.Path)) } } catch { }
        }
        $commands = @()
        if ($IsWindows -and (Get-Command Get-CimInstance -ErrorAction SilentlyContinue)) {
            $commands = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
                $_.ProcessId -ne $PID -and $_.Name -match '^(dotnet|Editor\.Desktop)(\.exe)?$'
            } | Select-Object -ExpandProperty CommandLine)
        }
        [pscustomobject]@{ Paths = @($paths); Commands = $commands }
    }
    function Is-Active([string] $Portable, $Snapshot) {
        @($Snapshot.Paths | Where-Object { Within $_ $Portable }).Count -gt 0 -or
            @($Snapshot.Commands | Where-Object {
                $_ -and $_.IndexOf($Portable, [StringComparison]::OrdinalIgnoreCase) -ge 0
            }).Count -gt 0
    }
    function Protection-State([string] $Portable, $Snapshot) {
        $pin = Join-Path $Portable '.keep-test-artifacts'
        if (Test-Path -LiteralPath $pin) { return 'PINNED' }
        if ($null -eq $Snapshot) { $Snapshot = Find-Active }
        if (Is-Active $Portable $Snapshot) { return 'ACTIVE' }
        # A process inventory can take time; honour a pin created during that inventory.
        if (Test-Path -LiteralPath $pin) { return 'PINNED' }
        return $null
    }
    function Load-PastReceipts([string] $Directory) {
        Require (Within $Directory $root) 'Receipt directory leaves ArtifactRoot.'
        No-Reparse $Directory
        if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { return }
        foreach ($receipt in Get-ChildItem -LiteralPath $Directory -File -Filter '*.result.json') {
            No-Reparse $receipt.FullName
            try {
                $record = Get-Content -LiteralPath $receipt.FullName -Raw | ConvertFrom-Json
                $receiptRoot = Full $record.ArtifactRoot
                if ($record.EvidenceVersion -ne 1 -or -not (Within $receiptRoot $root) -or
                    (Full (Join-Path $receiptRoot 'retention-receipts')) -ne (Full $Directory)) { continue }
                foreach ($deleted in $record.Deleted) {
                    $deletedPath = Full $deleted.Path
                    if (-not (Within $deletedPath $root) -or $deleted.Sha256 -notmatch '^[A-Fa-f0-9]{64}$') { continue }
                    $past[$deletedPath] = [string]$deleted.Sha256
                }
            } catch { continue } # An unreadable receipt never authorizes another deletion.
        }
    }

    $root = Full $ArtifactRoot
    Require (-not [string]::IsNullOrWhiteSpace($root) -and $root -ne (Full ([IO.Path]::GetPathRoot($root)))) 'ArtifactRoot cannot be a volume root.'
    Require (Test-Path -LiteralPath $root -PathType Container) 'ArtifactRoot must already exist.'
    Require (-not (Within $root $repository) -and -not (Within $repository $root)) 'ArtifactRoot cannot be the repository or its ancestor.'
    foreach ($protected in @('E:\NuGet', 'E:\DevTools\dotnet10', 'E:\DevTools\dotnet-cli-home',
        $env:DOTNET_ROOT, $env:NUGET_PACKAGES, $env:WINDIR) | Where-Object { $_ }) {
        $protectedPath = Full $protected
        Require (-not (Within $root $protectedPath) -and -not (Within $protectedPath $root)) 'ArtifactRoot cannot contain a tool or dependency cache.'
    }
    No-Reparse $root
    $receiptDirectory = Join-Path $root 'retention-receipts'
    No-Reparse $receiptDirectory
    $past = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    Load-PastReceipts $receiptDirectory
    $portableRoots = [Collections.Generic.List[string]]::new()
    if ($PublishOnly) {
        $only = Full $PublishOnly
        Require (Within $only $root) 'PublishOnly must be within ArtifactRoot.'
        No-Reparse $only
        $portableRoots.Add($only)
    } else {
        foreach ($child in Get-ChildItem -LiteralPath $root -Directory -Force) {
            if ($child.Name -eq 'retention-receipts' -or ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            foreach ($candidate in @($child.FullName, (Join-Path $child.FullName 'portable'))) {
                if (Test-Path -LiteralPath (Join-Path $candidate 'portable-summary.json') -PathType Leaf) {
                    $portableRoots.Add((Full $candidate)); break
                }
            }
        }
    }
    $activity = Find-Active
    $candidates = [Collections.Generic.List[object]]::new()
    foreach ($portable in $portableRoots | Select-Object -Unique) {
        $candidate = [pscustomobject]@{ PortableRoot = $portable; State = 'SKIPPED'; Reason = $null
            FinishedUtc = $null; ArchiveSha256 = $null; Files = @(); KeepExtracted = $true }
        $candidates.Add($candidate)
        try {
            No-Reparse $portable
            $protectedState = Protection-State $portable $activity
            if ($protectedState) { $candidate.State = $protectedState; continue }
            # Standalone P5 cleanup may have used its immediate parent as ArtifactRoot.
            $localReceipts = Join-Path ([IO.Path]::GetDirectoryName($portable)) 'retention-receipts'
            if (Within $localReceipts $root) { Load-PastReceipts $localReceipts }
            $summaryPath = Join-Path $portable 'portable-summary.json'
            No-Reparse $summaryPath
            $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
            $sourceUnchanged = Need $summary 'SourceUnchanged'
            Require ((Need $summary 'Status') -ceq 'PASS' -and $null -eq (Need $summary 'Error') -and
                $sourceUnchanged -is [bool] -and $sourceUnchanged -and (Need $summary 'PublishExitCode') -eq 0) 'Only complete successful candidates are eligible.'
            $runtime = Need $summary 'Runtime'
            $packageUnchanged = Need $runtime 'PackageUnchanged'
            Require ((Need $runtime 'Status') -ceq 'PASS' -and $packageUnchanged -is [bool] -and $packageUnchanged) 'Runtime candidate evidence is incomplete.'
            $sourceHash = Need $summary 'SourceManifestSha256'
            No-Reparse (Join-Path $portable 'source-before.sha256')
            No-Reparse (Join-Path $portable 'source-after.sha256')
            $before = @(Get-Content -LiteralPath (Join-Path $portable 'source-before.sha256'))
            $after = @(Get-Content -LiteralPath (Join-Path $portable 'source-after.sha256'))
            $actualSource = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($before -join "`n"))))
            Require ($sourceHash -match '^[A-Fa-f0-9]{64}$' -and $actualSource -eq $sourceHash -and
                ($before -join "`n") -ceq ($after -join "`n") -and $before.Count -eq (Need $summary 'SourceFiles')) 'Source evidence is incomplete or changed.'
            $candidate.FinishedUtc = [DateTimeOffset]::Parse((Need $summary 'FinishedUtc')).UtcDateTime
            $package = Need $summary 'Package'
            $archive = Join-Path $portable 'Editor.Desktop-win-x64-internal.zip'
            No-Reparse $archive
            Require ((Full (Need $package 'Archive')) -eq (Full $archive)) 'Archive belongs to another directory.'
            $archiveHash = Hash $archive
            Require ($archiveHash -eq (Need $package 'ArchiveSha256')) 'Archive hash mismatch.'
            $candidate.ArchiveSha256 = $archiveHash
            $manifestPath = Join-Path $portable 'publish-sha256.txt'
            $symbolPath = Join-Path $portable 'symbols-sha256.txt'
            Require ((Hash $manifestPath) -eq (Need $package 'PublishManifestSha256') -and
                (Hash $symbolPath) -eq (Need $package 'SymbolManifestSha256')) 'Candidate manifest hash mismatch.'
            $manifest = Read-Manifest $manifestPath
            $symbolManifest = Read-Manifest $symbolPath
            Require ($manifest.Count -eq (Need $package 'PublishedFiles') -and
                $symbolManifest.Count -eq (Need $package 'SymbolFiles')) 'Candidate manifest count mismatch.'
            Require ($manifest.ContainsKey('build-evidence.json') -and $manifest.ContainsKey('candidate-files.json')) 'Candidate metadata is missing.'
            $eligible = [Collections.Generic.List[object]]::new()
            $previouslyRemoved = 0
            foreach ($kind in @('publish', 'extracted', 'symbols')) {
                $directory = Join-Path $portable $kind
                $expected = if ($kind -eq 'symbols') { $symbolManifest } else { $manifest }
                $actual = @{}
                foreach ($file in @(Tree-Files $directory)) {
                    $relative = Safe-Relative ([IO.Path]::GetRelativePath($directory, $file.FullName))
                    Require ($expected.ContainsKey($relative)) "Unknown file in candidate directory: $($file.FullName)"
                    Require ((Hash $file.FullName) -eq $expected[$relative]) "Changed candidate file: $($file.FullName)"
                    $actual[$relative] = $true
                    $deletable = if ($kind -eq 'symbols') { $file.Extension -ieq '.pdb' } else { $file.Extension -iin @('.dll', '.exe') }
                    if ($deletable) {
                        $eligible.Add([pscustomobject]@{ Path = $file.FullName; Sha256 = $expected[$relative]
                            Bytes = $file.Length; Kind = $kind; PortableRoot = $portable; ArchiveSha256 = $archiveHash })
                    }
                }
                foreach ($relative in $expected.Keys) {
                    if ($actual.ContainsKey($relative)) { continue }
                    $missing = Full (Join-Path $directory $relative)
                    Require ($past.ContainsKey($missing) -and $past[$missing] -eq $expected[$relative]) "Missing file without cleanup receipt: $missing"
                    $previouslyRemoved++
                }
            }
            # No remaining eligible file means no deletion; avoid repeatedly expanding old ZIPs.
            if ($eligible.Count -eq 0 -and $previouslyRemoved -gt 0) { $candidate.State = 'PRUNED'; continue }
            $zip = [IO.Compression.ZipFile]::OpenRead($archive)
            try {
                $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                [long]$expanded = 0
                foreach ($entry in $zip.Entries) {
                    if ($entry.FullName.EndsWith('/')) { continue }
                    $relative = Safe-Relative $entry.FullName
                    Require ($seen.Add($relative) -and $manifest.ContainsKey($relative)) 'ZIP contains an unexpected or duplicate file.'
                    $expanded += $entry.Length
                    Require ($expanded -le 4GB) 'ZIP is outside the bounded candidate size.'
                    $stream = $entry.Open()
                    try { $entryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
                    finally { $stream.Dispose() }
                    Require ($entryHash -eq $manifest[$relative]) "ZIP content differs from manifest: $relative"
                }
                Require ($seen.Count -eq $manifest.Count -and $expanded -eq (Need $package 'PublishedBytes')) 'ZIP file count or expanded size differs from evidence.'
            } finally { $zip.Dispose() }
            $candidate.Files = @($eligible)
            $candidate.State = 'READY'
        } catch { $candidate.Reason = $_.Exception.Message }
    }
    $newest = @($candidates | Where-Object State -eq 'READY' | Sort-Object FinishedUtc -Descending | Select-Object -First $KeepLatest)
    $planned = [Collections.Generic.List[object]]::new()
    foreach ($candidate in $candidates | Where-Object State -eq 'READY') {
        $candidate.KeepExtracted = [bool]($PublishOnly -or $candidate -in $newest)
        foreach ($file in $candidate.Files) {
            if ($file.Kind -eq 'publish' -or (-not $candidate.KeepExtracted)) { $planned.Add($file) }
        }
    }
    $report = [ordered]@{ EvidenceVersion = 1; Status = 'PREVIEW'; ArtifactRoot = $root; KeepLatest = $KeepLatest
        PublishOnly = $PublishOnly; StartedUtc = [DateTime]::UtcNow.ToString('o'); FinishedUtc = $null
        Candidates = @($candidates); Planned = @($planned); Deleted = @(); Errors = @()
        PlannedBytes = [long](($planned | Measure-Object Bytes -Sum).Sum); DeletedBytes = [long]0; Receipt = $null }
    if ($Apply -and $planned.Count -gt 0) {
        if (-not (Test-Path -LiteralPath $receiptDirectory)) { [void](New-Item -ItemType Directory -Path $receiptDirectory) }
        No-Reparse $receiptDirectory
        $id = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N')
        $report.Receipt = Join-Path $receiptDirectory ($id + '.result.json')
        Write-NewReceipt (Join-Path $receiptDirectory ($id + '.planned.json')) $report
        $deleted = [Collections.Generic.List[object]]::new()
        $errors = [Collections.Generic.List[object]]::new()
        foreach ($candidate in $candidates | Where-Object State -eq 'READY') {
            $files = @($planned | Where-Object PortableRoot -eq $candidate.PortableRoot)
            if (-not $files.Count) { continue }
            try {
                $protectedState = Protection-State $candidate.PortableRoot $null
                if ($protectedState) { $candidate.State = $protectedState; continue }
                # Recheck the complete deletion batch before deleting its first file.
                foreach ($file in $files) {
                    Require (Within (Full $file.Path) $candidate.PortableRoot) 'Deletion leaves candidate directory.'
                    No-Reparse $file.Path
                    Require ((Hash $file.Path) -eq $file.Sha256) "File changed after preview: $($file.Path)"
                }
                $protectedState = Protection-State $candidate.PortableRoot $null
                if ($protectedState) { $candidate.State = $protectedState; continue }
                foreach ($file in $files) {
                    No-Reparse $file.Path
                    Require ((Hash $file.Path) -eq $file.Sha256) "File changed before deletion: $($file.Path)"
                    Remove-Item -LiteralPath $file.Path -Force -ErrorAction Stop
                    $deleted.Add($file)
                    $report.DeletedBytes += $file.Bytes
                }
            } catch { $errors.Add([pscustomobject]@{ PortableRoot = $candidate.PortableRoot; Error = $_.Exception.Message }) }
        }
        $report.Deleted = @($deleted)
        $report.Errors = @($errors)
        $report.Status = if ($errors.Count) { 'PARTIAL' } else { 'COMPLETE' }
        $report.FinishedUtc = [DateTime]::UtcNow.ToString('o')
        Write-NewReceipt $report.Receipt $report
    } elseif ($Apply) { $report.Status = 'COMPLETE' }
    $report.FinishedUtc = [DateTime]::UtcNow.ToString('o')
    return [pscustomobject]$report
}

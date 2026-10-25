# Pure package checks: never load or execute the packaged application.
function Assert-ReleaseRelativePath([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathRooted($Path) -or
        $Path.Contains('\') -or $Path -match '[\x00-\x1f<>:"|?*]') {
        throw "Invalid package relative path: $Path"
    }
    foreach ($part in $Path.Split('/')) {
        if ($part.Length -eq 0 -or $part -in @('.', '..') -or $part -match '[. ]$' -or
            $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9\u00b9\u00b2\u00b3]|LPT[1-9\u00b9\u00b2\u00b3])(?:\.|$)') {
            throw "Invalid package relative path: $Path"
        }
    }
}

function Get-ReleaseStreamFingerprint([IO.Stream] $Stream, [long] $ExpectedLength) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $buffer = [byte[]]::new(81920)
        [long] $length = 0
        while (($read = $Stream.Read($buffer, 0, $buffer.Length)) -ne 0) {
            $length += $read
            if ($length -gt $ExpectedLength) { throw 'Package stream exceeds its expected length.' }
            [void] $sha.TransformBlock($buffer, 0, $read, $buffer, 0)
        }
        if ($length -ne $ExpectedLength) { throw 'Package stream length differs from its expected length.' }
        [void] $sha.TransformFinalBlock([byte[]]::new(0), 0, 0)
        return @{ Length = $length; Sha256 = [Convert]::ToHexString($sha.Hash) }
    } finally { $sha.Dispose() }
}

function Get-ReleaseFileFingerprint([string] $Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { return Get-ReleaseStreamFingerprint $stream $stream.Length }
    finally { $stream.Dispose() }
}

function Assert-ReleaseJsonKeys([Text.Json.JsonElement] $Element) {
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $keys.Add($property.Name)) { throw "Duplicate package JSON key: $($property.Name)" }
            Assert-ReleaseJsonKeys $property.Value
        }
    } elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        foreach ($item in $Element.EnumerateArray()) { Assert-ReleaseJsonKeys $item }
    }
}

function Assert-ReleaseRecordFields($Record, [string[]] $Names) {
    if ($Record -isnot [Collections.IDictionary] -or $Record.Count -ne $Names.Count) {
        throw 'Package catalog has missing or unexpected fields.'
    }
    foreach ($key in $Record.Keys) {
        if ($key -cnotin $Names) { throw "Unexpected package catalog field: $key" }
    }
}

function Get-ReleaseDirectoryFiles([string] $Root) {
    $rootItem = Get-Item -LiteralPath $Root -Force
    if (-not $rootItem.PSIsContainer -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Package directory is missing or a reparse point: $Root"
    }
    $files = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Package contains a reparse point: $($item.FullName)"
            }
            $relative = [IO.Path]::GetRelativePath($Root, $item.FullName).Replace('\', '/')
            Assert-ReleaseRelativePath $relative
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
            elseif (-not $files.TryAdd($relative, $item)) { throw "Duplicate package path: $relative" }
        }
    }
    return ,$files
}

function Assert-ReleaseFileSet($Files, $Expected, [string] $Label) {
    if ($Files.Count -ne $Expected.Count) { throw "$Label has extra or missing files." }
    foreach ($path in $Files.Keys) {
        if (-not $Expected.ContainsKey($path) -or $path -cne $Expected[$path].Path) {
            throw "$Label has an unexpected or noncanonical path: $path"
        }
        $fingerprint = Get-ReleaseFileFingerprint $Files[$path].FullName
        if ($fingerprint.Length -ne $Expected[$path].Length -or
            $fingerprint.Sha256 -cne $Expected[$path].Sha256) {
            throw "$Label differs from reviewed catalog: $path"
        }
    }
}

function Assert-ReleasePackageIntegrity([string] $AssemblyRoot) {
    $root = [IO.Path]::GetFullPath($AssemblyRoot)
    $releaseFiles = Get-ReleaseDirectoryFiles (Join-Path $root 'release')
    $catalogName = 'candidate-files.json'
    if (-not $releaseFiles.ContainsKey($catalogName) -or $releaseFiles[$catalogName].Name -cne $catalogName) {
        throw 'Reviewed package catalog is missing or has a noncanonical name.'
    }
    $catalogPath = $releaseFiles[$catalogName].FullName
    $catalogFingerprint = Get-ReleaseFileFingerprint $catalogPath
    $json = [IO.File]::ReadAllText($catalogPath)
    $document = [Text.Json.JsonDocument]::Parse($json)
    try { Assert-ReleaseJsonKeys $document.RootElement } finally { $document.Dispose() }
    $catalog = ConvertFrom-Json -InputObject $json -AsHashtable
    Assert-ReleaseRecordFields $catalog @('EvidenceVersion', 'SourceManifestSha256', 'FileCount', 'Files')
    if ($catalog.EvidenceVersion -isnot [long] -or $catalog.EvidenceVersion -ne 1 -or
        $catalog.SourceManifestSha256 -isnot [string] -or $catalog.SourceManifestSha256 -cnotmatch '^[0-9A-F]{64}$' -or
        $catalog.FileCount -isnot [long] -or $catalog.FileCount -lt 1 -or
        $catalog.Files -isnot [array] -or $catalog.FileCount -ne $catalog.Files.Count) {
        throw 'Package catalog version, source hash, count or records are invalid.'
    }
    $expected = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $catalog.Files) {
        Assert-ReleaseRecordFields $entry @('Path', 'Length', 'Sha256')
        if ($entry.Path -isnot [string] -or $entry.Length -isnot [long] -or $entry.Length -lt 0 -or
            $entry.Sha256 -isnot [string] -or $entry.Sha256 -cnotmatch '^[0-9A-F]{64}$') {
            throw 'Package catalog file record is invalid.'
        }
        Assert-ReleaseRelativePath $entry.Path
        if ($entry.Path -ieq $catalogName -or -not $expected.TryAdd($entry.Path, $entry)) {
            throw "Duplicate or self-referencing catalog path: $($entry.Path)"
        }
    }
    # Bind the catalog itself as bytes; an extracted/ZIP catalog cannot redefine the reviewed inventory.
    $expected.Add($catalogName, @{ Path = $catalogName; Length = $catalogFingerprint.Length; Sha256 = $catalogFingerprint.Sha256 })
    Assert-ReleaseFileSet $releaseFiles $expected 'release'
    $extractedFiles = Get-ReleaseDirectoryFiles (Join-Path $root 'extracted')
    Assert-ReleaseFileSet $extractedFiles $expected 'extracted'

    $archive = Join-Path $root 'PhigrosLayoutStudio-1.0-win-x64.zip'
    $archiveItem = Get-Item -LiteralPath $archive -Force
    if ($archiveItem.PSIsContainer -or ($archiveItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Package ZIP must be a regular file.'
    }
    $archiveFingerprint = Get-ReleaseFileFingerprint $archive
    $recordPath = $archive + '.sha256'
    $recordItem = Get-Item -LiteralPath $recordPath -Force
    if ($recordItem.PSIsContainer -or ($recordItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Archive SHA-256 record must be a regular file.'
    }
    $hashRecord = [IO.File]::ReadAllText($recordPath).Trim()
    if ($hashRecord -cnotmatch '^([0-9A-F]{64})  PhigrosLayoutStudio-1\.0-win-x64\.zip$' -or
        $Matches[1] -cne $archiveFingerprint.Sha256) { throw 'External archive SHA-256 record differs or is invalid.' }

    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($path in $expected.Keys) {
            $separator = $path.LastIndexOf('/')
            while ($separator -gt 0) {
                [void] $directories.Add($path.Substring(0, $separator))
                $separator = $path.LastIndexOf('/', $separator - 1)
            }
        }
        $fileCount = 0
        foreach ($entry in $zip.Entries) {
            $isDirectory = $entry.FullName.EndsWith('/')
            $path = if ($isDirectory) { $entry.FullName.Substring(0, $entry.FullName.Length - 1) } else { $entry.FullName }
            Assert-ReleaseRelativePath $path
            if (-not $seen.Add($path)) { throw "Duplicate ZIP path: $path" }
            # Unix symlinks and Windows reparse entries must never stand in for catalog files.
            $unixType = ($entry.ExternalAttributes -shr 16) -band 0xF000
            if ($unixType -eq 0xA000 -or ($entry.ExternalAttributes -band 0x400)) {
                throw "ZIP contains a link: $path"
            }
            if ($isDirectory) {
                if ($entry.Length -ne 0 -or -not $directories.Contains($path)) { throw "Unexpected ZIP directory: $path" }
                continue
            }
            if (-not $expected.ContainsKey($path) -or $path -cne $expected[$path].Path) {
                throw "Unexpected or noncanonical ZIP path: $path"
            }
            $reference = $expected[$path]
            if ($entry.Length -ne $reference.Length) { throw "ZIP length differs from reviewed catalog: $path" }
            $stream = $entry.Open()
            try { $fingerprint = Get-ReleaseStreamFingerprint $stream $reference.Length }
            finally { $stream.Dispose() }
            if ($fingerprint.Sha256 -cne $reference.Sha256) { throw "ZIP hash differs from reviewed catalog: $path" }
            $fileCount++
        }
        if ($fileCount -ne $expected.Count) { throw 'ZIP has missing files.' }
    } finally { $zip.Dispose() }
    return @{ Catalog = $catalog; CatalogSha256 = $catalogFingerprint.Sha256
        ArchiveSha256 = $archiveFingerprint.Sha256; PublishedFiles = $expected.Count
        ExecutableSha256 = if ($expected.ContainsKey('Editor.Desktop.exe')) { $expected['Editor.Desktop.exe'].Sha256 } else { $null } }
}

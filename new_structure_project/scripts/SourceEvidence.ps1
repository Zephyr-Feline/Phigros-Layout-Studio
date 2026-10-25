# Read-only working-tree identity for acceptance artifacts. No Git mutation or package operation.
function Get-AcceptanceSourceManifest([string] $RepositoryRoot) {
    $paths = & git -C $RepositoryRoot ls-files --cached --others --exclude-standard
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate the Git working tree.' }
    @($paths | Where-Object {
        $_ -match '^(EnhancerSource/|new_structure_project/(src/|tests/|scripts/|global\.json$|NuGet\.Config$|P1\.slnx$))' -and
        $_ -match '\.(cs|csproj|ps1|json|Config|slnx|manifest)$'
    } | Sort-Object -Unique | ForEach-Object {
        $file = Join-Path $RepositoryRoot $_
        if (Test-Path -LiteralPath $file -PathType Leaf) {
            "$(Get-FileHash -LiteralPath $file -Algorithm SHA256 | Select-Object -ExpandProperty Hash)  $_"
        } else { "MISSING  $_" }
    })
}

function Get-AcceptanceManifestHash([string[]] $Manifest) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Manifest -join "`n"))
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}

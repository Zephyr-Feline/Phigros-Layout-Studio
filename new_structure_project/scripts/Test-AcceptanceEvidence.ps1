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
. (Join-Path $PSScriptRoot 'SourceEvidence.ps1')
. (Join-Path $PSScriptRoot 'AcceptanceEvidence.ps1')
$testSources = @('AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA  fixture.cs')
$sourceHash = Get-AcceptanceManifestHash $testSources
$results = [Collections.Generic.List[object]]::new()
function Check([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Put-Record([string] $Root, [string] $Relative, $Record) {
    $Record | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Root $Relative) -Encoding utf8
}
function New-Fixture([string] $Name) {
    $root = Join-Path $outputRoot $Name
    foreach ($folder in @('script-tests', 'retention-tests', 'release-integrity-tests', 'regression', 'compatibility/matrix', 'portable/runtime-verification', 'portable/extracted')) {
        New-Item -ItemType Directory -Path (Join-Path $root $folder) -Force | Out-Null
    }
    Put-Record $root 'script-tests/script-tests.json' @{ Status = 'PASS'; Checks = 1; Results = @(@{ Status = 'PASS' }) }
    Put-Record $root 'retention-tests/retention-script-tests.json' @{ Status = 'PASS'; Error = $null; Checks = 1; Results = @(@{ Status = 'PASS' }) }
    Put-Record $root 'release-integrity-tests/release-integrity-tests.json' @{ Status = 'PASS'; Error = $null; Checks = 1; Passed = 1; Failed = 0; Tests = @(@{ Status = 'PASS' }) }
    foreach ($relative in @('regression/source-sha256.txt', 'compatibility/source-before.sha256',
        'compatibility/source-after.sha256', 'portable/source-before.sha256', 'portable/source-after.sha256')) {
        $testSources | Set-Content -LiteralPath (Join-Path $root $relative) -Encoding utf8
    }
    foreach ($relative in @('regression/v2-tests.trx', 'regression/v2-local-tests.trx',
        'compatibility/matrix/compatibility-evidence.json', 'compatibility/matrix/index.html',
        'portable/runtime-verification/runtime-summary.json', 'portable/Editor.Desktop-win-x64-internal.zip')) {
        'synthetic evidence only' | Set-Content -LiteralPath (Join-Path $root $relative) -Encoding utf8
    }
    function FixtureHash([string] $Relative) { (Get-FileHash -LiteralPath (Join-Path $root $Relative) -Algorithm SHA256).Hash }
    $identity = @{ Status = 'PASS'; Error = $null; Commit = 'fixture-commit'; Branch = 'fixture-branch'; Dirty = $true; Sdk = '10.0.401' }
    $regression = $identity.Clone()
    $regression.V2 = @{
        Baseline = @{ Status = 'PASS'; ExitCode = 0; Passed = 10; Failed = 0; Skipped = 0; Discovered = 10; Executed = 10; ResultCount = 10 }
        LocalExtensions = @{ Status = 'NOT_REQUESTED'; Passed = $null; Skipped = $null }
    }
    $regression.Classic = @{ Checks = 280; ReferenceImage = $false }; $regression.FreeLayout = @{ Checks = 483 }
    Put-Record $root 'regression/regression-summary.json' $regression
    $compatibility = $identity.Clone()
    $compatibility.SourceUnchanged = $true; $compatibility.SourceFiles = 1; $compatibility.SourceManifestSha256 = $sourceHash
    $compatibility.Matrix = @{ Status = 'PASS'; TechnicalFailures = 0; Synthetic = 3; Local = 0; Accepted = 2; Rejected = 1
        ReportSha256 = (FixtureHash 'compatibility/matrix/compatibility-evidence.json') }
    $compatibility.VisualAcceptance = 'PENDING'; $compatibility.LegacyWriterDecision = 'PENDING'
    Put-Record $root 'compatibility/p3-summary.json' $compatibility
    $portable = $identity.Clone()
    $portable.SourceUnchanged = $true; $portable.SourceFiles = 1; $portable.SourceManifestSha256 = $sourceHash
    $portable.PublishExitCode = 0
    $portable.Runtime = @{ Status = 'PASS'; DriverExitCode = 0; PackageUnchanged = $true; ReducedEnvironment = $true
        DotnetOnReducedPath = $false; SourceManifestSha256 = $sourceHash; PackageFiles = 2
        ReportSha256 = (FixtureHash 'portable/runtime-verification/runtime-summary.json') }
    $portable.ReopenChecks = 11; $portable.CliPngChecks = 44; $portable.FontStages = 5; $portable.FontPngChecks = 16
    $portable.Package = @{ Archive = (Join-Path $root 'portable/Editor.Desktop-win-x64-internal.zip'); PublishedFiles = 2
        ArchiveSha256 = (FixtureHash 'portable/Editor.Desktop-win-x64-internal.zip') }
    $portable.Acceptance = @{ CleanMachine = 'PENDING'; WindowsHardware = 'PENDING'; ThirdPartyNotices = 'PENDING'
        ProjectLicense = 'PENDING'; P3Visual = 'PENDING'; LegacyWriter = 'PENDING' }
    Put-Record $root 'portable/portable-summary.json' $portable
    Put-Record $root 'portable/extracted/build-evidence.json' @{ SourceManifestSha256 = $sourceHash; CandidateStatus = 'INTERNAL_ONLY' }
    return $root
}
function Read-Fixture([string] $Root, [string] $Relative) {
    Get-Content -LiteralPath (Join-Path $Root $Relative) -Raw | ConvertFrom-Json -AsHashtable
}
function Test-Case([string] $Name, [scriptblock] $Action) {
    try { & $Action; $results.Add(@{ Name = $Name; Status = 'PASS' }); Write-Host "PASS: $Name" }
    catch { $results.Add(@{ Name = $Name; Status = 'FAILED'; Error = $_.Exception.Message }); throw }
}
function Must-Reject([scriptblock] $Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Check $rejected 'Incomplete or changed evidence was accepted.'
}
try {
    foreach ($scenario in @('missing', 'failed', 'empty', 'count', 'result-status', 'error', 'zero', 'passed-count')) {
        Test-Case "release-integrity-tests-$scenario" {
            $root = New-Fixture "release-integrity-tests-$scenario"
            $relative = 'release-integrity-tests/release-integrity-tests.json'
            $record = Read-Fixture $root $relative
            switch ($scenario) {
                'missing' { Move-Item -LiteralPath (Join-Path $root $relative) -Destination (Join-Path $root 'release-integrity-tests/moved-report.json') }
                'failed' { $record.Status = 'FAILED'; $record.Failed = 1 }
                'empty' { $record.Tests = @() }
                'count' { $record.Checks = 2 }
                'result-status' { $record.Tests[0].Status = 'SKIPPED' }
                'error' { $record.Error = 'synthetic failure' }
                'zero' { $record.Checks = 0; $record.Passed = 0; $record.Tests = @() }
                'passed-count' { $record.Passed = 0 }
            }
            if ($scenario -ne 'missing') { Put-Record $root $relative $record }
            Must-Reject { Get-AcceptanceBundleEvidence $root $testSources }
        }
    }
    $retentionFailures = [Collections.Generic.List[string]]::new()
    foreach ($scenario in @('missing', 'failed', 'empty', 'count', 'result-status', 'error', 'missing-error', 'zero')) {
        try {
            Test-Case "retention-tests-$scenario" {
                $root = New-Fixture "retention-tests-$scenario"
                $relative = 'retention-tests/retention-script-tests.json'
                $record = Read-Fixture $root $relative
                switch ($scenario) {
                    'missing' { Move-Item -LiteralPath (Join-Path $root $relative) -Destination (Join-Path $root 'retention-tests/moved-report.json') }
                    'failed' { $record.Status = 'FAILED' }
                    'empty' { $record.Results = @() }
                    'count' { $record.Checks = 2 }
                    'result-status' { $record.Results[0].Status = 'SKIPPED' }
                    'error' { $record.Error = 'synthetic failure' }
                    'missing-error' { [void]$record.Remove('Error') }
                    'zero' { $record.Checks = 0; $record.Results = @() }
                }
                if ($scenario -ne 'missing') { Put-Record $root $relative $record }
                Must-Reject { Get-AcceptanceBundleEvidence $root $testSources }
            }
        } catch { $retentionFailures.Add($scenario) }
    }
    Check ($retentionFailures.Count -eq 0) ("Retention evidence rejection regressions failed: " + ($retentionFailures -join ', '))
    Test-Case 'complete technical evidence; optional inputs not requested' {
        $root = New-Fixture 'complete'
        $joined = Get-AcceptanceBundleEvidence $root $testSources
        Check ($joined.Baseline.Passed -eq 10 -and $joined.LocalExtensions.Status -eq 'NOT_REQUESTED') 'Incorrect test scopes.'
        Check ($joined.RetentionChecks -eq 1 -and $joined.ReleaseIntegrityChecks -eq 1 -and $joined.ArtifactSha256.Count -eq 12) 'Missing script checks or linked artifact hashes.'
        $retentionHash = (Get-FileHash -LiteralPath (Join-Path $root 'retention-tests/retention-script-tests.json') -Algorithm SHA256).Hash
        Check ($joined.ArtifactSha256['retention-tests/retention-script-tests.json'] -eq $retentionHash) 'Retention report hash is incorrect.'
    }
    Test-Case 'joined evidence is one record and remains an object in JSON' {
        $root = New-Fixture 'record-shape'
        $joined = Get-AcceptanceBundleEvidence $root $testSources
        Check ($joined -is [Collections.IDictionary]) 'Evidence includes stray pipeline output instead of one record.'
        $summary = @{ Evidence = $joined; Status = 'PASS'; Error = $null; Commit = 'fixture-commit'
            Branch = 'fixture-branch'; Dirty = $true; SourceManifestSha256 = $sourceHash }
        $roundTrip = $summary | ConvertTo-Json -Depth 16 | ConvertFrom-Json -AsHashtable
        Check ($roundTrip.Evidence -is [Collections.IDictionary] -and $roundTrip.Evidence.Baseline.Passed -eq 10) 'JSON evidence is not one named record.'
        Write-AcceptanceOverview $root $summary
        $html = Get-Content -LiteralPath (Join-Path $root 'index.html') -Raw
        Check ($html.Contains('V2 mandatory 10;') -and -not $html.Contains('Extracted candidate belongs')) 'Successful overview contains diagnostics or incorrect counts.'
        Check ($html.Contains('retention checks 1;') -and $html.Contains('href="retention-tests/retention-script-tests.json"') -and
            $html.Contains($joined.ArtifactSha256['retention-tests/retention-script-tests.json'])) 'Retention evidence is missing from the overview.'
    }
    Test-Case 'compatibility and candidate rejection report specific reasons' {
        foreach ($scenario in @('visual', 'candidate')) {
            $root = New-Fixture "reason-$scenario"
            if ($scenario -eq 'visual') {
                $relative = 'compatibility/p3-summary.json'; $record = Read-Fixture $root $relative
                $record.VisualAcceptance = 'PASS'
                $expected = 'Automated compatibility evidence cannot grant visual or Writer acceptance.'
            } else {
                $relative = 'portable/extracted/build-evidence.json'; $record = Read-Fixture $root $relative
                $record.SourceManifestSha256 = ('B' * 64)
                $expected = 'Extracted candidate belongs to another source version.'
            }
            Put-Record $root $relative $record
            $reason = $null
            try { Get-AcceptanceBundleEvidence $root $testSources | Out-Null } catch { $reason = $_.Exception.Message }
            Check ($reason -eq $expected) "Missing specific rejection reason for $scenario."
        }
    }
    Test-Case 'optional missing inputs remain explicit partial evidence' {
        $root = New-Fixture 'partial'
        $record = Read-Fixture $root 'regression/regression-summary.json'
        $record.V2.LocalExtensions = @{ Status = 'PARTIAL_MISSING_INPUTS'; ExitCode = 0; Failed = 0; Executed = 0
            Passed = 0; Discovered = 3; Skipped = 3; ResultCount = 3; MissingInputs = @('corpus', 'ttf', 'otf') }
        Put-Record $root 'regression/regression-summary.json' $record
        $joined = Get-AcceptanceBundleEvidence $root $testSources
        Check ($joined.LocalExtensions.Status -eq 'PARTIAL_MISSING_INPUTS' -and $joined.LocalExtensions.Skipped -eq 3) 'Partial evidence was hidden.'
        Check ($joined.ArtifactSha256.Count -eq 13) 'Local TRX was omitted.'
    }
    foreach ($scenario in @('child-failed', 'source-list', 'source-summary', 'git', 'sdk', 'baseline-skip',
        'baseline-count', 'local-failed', 'matrix-count', 'matrix-bytes', 'runtime-bytes', 'runtime-source',
        'runtime-incomplete', 'archive-bytes', 'archive-path', 'build-source', 'visual-approved', 'release-approved', 'missing-summary', 'script-tests-incomplete')) {
        Test-Case $scenario {
            $root = New-Fixture $scenario
            $relative = 'portable/portable-summary.json'
            $record = Read-Fixture $root $relative
            switch ($scenario) {
                'child-failed' { $record.Status = 'FAILED' }
                'source-list' { 'different' | Set-Content -LiteralPath (Join-Path $root 'regression/source-sha256.txt') }
                'source-summary' { $record.SourceManifestSha256 = ('B' * 64) }
                'git' { $record.Commit = 'other-commit' }
                'sdk' { $record.Sdk = 'other-sdk' }
                'baseline-skip' { $relative = 'regression/regression-summary.json'; $record = Read-Fixture $root $relative; $record.V2.Baseline.Skipped = 1 }
                'baseline-count' { $relative = 'regression/regression-summary.json'; $record = Read-Fixture $root $relative; $record.V2.Baseline.Executed = 9 }
                'local-failed' { $relative = 'regression/regression-summary.json'; $record = Read-Fixture $root $relative; $record.V2.LocalExtensions.Status = 'FAILED' }
                'matrix-count' { $relative = 'compatibility/p3-summary.json'; $record = Read-Fixture $root $relative; $record.Matrix.Accepted = 1 }
                'matrix-bytes' { 'changed' | Set-Content -LiteralPath (Join-Path $root 'compatibility/matrix/compatibility-evidence.json') }
                'runtime-bytes' { 'changed' | Set-Content -LiteralPath (Join-Path $root 'portable/runtime-verification/runtime-summary.json') }
                'runtime-source' { $record.Runtime.SourceManifestSha256 = ('B' * 64) }
                'runtime-incomplete' { $record.FontPngChecks = 15 }
                'archive-bytes' { 'changed' | Set-Content -LiteralPath (Join-Path $root 'portable/Editor.Desktop-win-x64-internal.zip') }
                'archive-path' { $record.Package.Archive = (Join-Path $outputRoot 'another.zip') }
                'build-source' { Put-Record $root 'portable/extracted/build-evidence.json' @{ SourceManifestSha256 = ('B' * 64); CandidateStatus = 'INTERNAL_ONLY' } }
                'visual-approved' { $relative = 'compatibility/p3-summary.json'; $record = Read-Fixture $root $relative; $record.VisualAcceptance = 'PASS' }
                'release-approved' { $record.Acceptance.ProjectLicense = 'PASS' }
                'missing-summary' { Move-Item -LiteralPath (Join-Path $root 'regression/regression-summary.json') -Destination (Join-Path $root 'regression/moved-summary.json') }
                'script-tests-incomplete' { Put-Record $root 'script-tests/script-tests.json' @{ Status = 'PASS'; Checks = 2; Results = @(@{ Status = 'PASS' }) } }
            }
            Put-Record $root $relative $record
            Must-Reject { Get-AcceptanceBundleEvidence $root $testSources }
        }
    }
    Test-Case 'HTML encodes untrusted identity and failure text' {
        $root = Join-Path $outputRoot 'html'; New-Item -ItemType Directory -Path $root | Out-Null
        Write-AcceptanceOverview $root @{ Evidence = $null; Status = 'FAILED'; Error = '<script>bad</script>'
            Commit = '<img src=x>'; Branch = '"<&'; Dirty = $true; SourceManifestSha256 = ('A' * 64) }
        $html = Get-Content -LiteralPath (Join-Path $root 'index.html') -Raw
        Check ($html.Contains('&lt;script&gt;bad&lt;/script&gt;') -and -not $html.Contains('<script>bad')) 'Unsafe HTML text.'
    }
    Test-Case 'output safeguards preserve existing evidence' {
        $marker = Join-Path $outputRoot 'preserve.txt'; 'original' | Set-Content -LiteralPath $marker
        Must-Reject { & (Join-Path $PSScriptRoot 'Verify-Acceptance.ps1') -OutputRoot $outputRoot }
        Must-Reject { & (Join-Path $PSScriptRoot 'Verify-Acceptance.ps1') -OutputRoot $repositoryRoot }
        Check ((Get-Content -LiteralPath $marker -Raw).Trim() -eq 'original') 'Existing evidence changed.'
    }
    Test-Case 'missing SDK leaves FAILED summary without running any gate' {
        $root = Join-Path $outputRoot 'missing-sdk'
        $beforeLocation = (Get-Location).Path; $beforeDotnet = $env:DOTNET_ROOT
        Must-Reject { & (Join-Path $PSScriptRoot 'Verify-Acceptance.ps1') -OutputRoot $root -DotnetPath (Join-Path $root 'absent-sdk.exe') }
        $record = Read-Fixture $root 'acceptance-summary.json'
        Check ($record.Status -eq 'FAILED' -and $record.Stages.Count -eq 0 -and $record.Acceptance.Publication -eq 'PENDING') 'Missing SDK falsely passed.'
        Check ((Get-Location).Path -eq $beforeLocation -and $env:DOTNET_ROOT -eq $beforeDotnet) 'Caller environment changed.'
        Check (Test-Path -LiteralPath (Join-Path $root 'index.html')) 'Failure overview missing.'
    }
} finally {
    @{ Status = $(if (@($results | Where-Object Status -eq 'FAILED').Count -eq 0) { 'PASS' } else { 'FAILED' })
        Checks = $results.Count; Results = $results } | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $outputRoot 'script-tests.json') -Encoding utf8
}
Write-Host "SCRIPT TESTS PASS: $($results.Count) checks; synthetic fixtures only."

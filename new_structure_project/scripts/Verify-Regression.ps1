param(
    [Parameter(Mandatory = $true)]
    [string] $OutputRoot,
    [string] $DotnetPath = 'dotnet',
    [string] $ReferenceImagePath,
    [switch] $IncludeLocalExtensions,
    [string] $LocalCorpusRoot,
    [string] $LocalTtfPath,
    [string] $LocalOtfPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputRoot)
if ($ReferenceImagePath) { $ReferenceImagePath = [IO.Path]::GetFullPath($ReferenceImagePath) }
if ($LocalCorpusRoot) { $LocalCorpusRoot = [IO.Path]::GetFullPath($LocalCorpusRoot) }
if ($LocalTtfPath) { $LocalTtfPath = [IO.Path]::GetFullPath($LocalTtfPath) }
if ($LocalOtfPath) { $LocalOtfPath = [IO.Path]::GetFullPath($LocalOtfPath) }
if ($outputRoot.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $outputRoot.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must be outside the repository.'
}
if (Test-Path -LiteralPath $outputRoot) {
    if (-not (Test-Path -LiteralPath $outputRoot -PathType Container) -or
        (Get-ChildItem -LiteralPath $outputRoot -Force | Select-Object -First 1)) {
        throw 'OutputRoot must be new or empty; existing evidence is never overwritten.'
    }
} else {
    New-Item -ItemType Directory -Path $outputRoot | Out-Null
}

function Invoke-Logged([string] $Executable, [string[]] $Arguments, [string] $LogName) {
    Write-Host "Running $LogName ..."
    $log = Join-Path $outputRoot $LogName
    & $Executable @Arguments *> $log
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $log -Tail 20 | Write-Host
        throw "$LogName failed with exit code $LASTEXITCODE. See $log"
    }
}

function Invoke-V2Matrix([string] $Name, [string] $Filter, [string] $TrxName, [string] $LogName,
    [bool] $AllowMissingInputs) {
    $matrix = [ordered]@{
        Status = 'FAILED'; Filter = $Filter; Discovered = $null; Executed = $null
        Passed = $null; Failed = $null; Skipped = $null; ExitCode = $null
        ReportedNotExecuted = $null; ResultCount = $null; ResultOutcomes = $null
        SkippedTests = @(); MissingInputs = @(); Error = $null
    }
    Write-Host "Running V2 $Name ..."
    $log = Join-Path $outputRoot $LogName
    & $dotnet test P1.slnx -c Release --no-restore --no-build --settings $runSettingsPath --filter $Filter `
        --logger "trx;LogFileName=$TrxName" --results-directory $outputRoot *> $log
    $matrix.ExitCode = $LASTEXITCODE
    $trxPath = Join-Path $outputRoot $TrxName
    if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
        $matrix.Error = "V2 $Name did not produce TRX evidence. See $log"
        return $matrix
    }
    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    if ($null -eq $counters) {
        $matrix.Error = "V2 $Name TRX has no counters. See $trxPath"
        return $matrix
    }
    $matrix.Discovered = [int]$counters.total
    $matrix.Executed = [int]$counters.executed
    $matrix.Passed = [int]$counters.passed
    $matrix.Failed = [int]$counters.failed
    $matrix.ReportedNotExecuted = [int]$counters.notExecuted
    $results = @($trx.TestRun.Results.UnitTestResult | Where-Object { $null -ne $_ })
    $matrix.ResultCount = $results.Count
    $matrix.ResultOutcomes = [ordered]@{
        Passed = @($results | Where-Object { $_.outcome -eq 'Passed' }).Count
        Failed = @($results | Where-Object { $_.outcome -eq 'Failed' }).Count
        NotExecuted = @($results | Where-Object { $_.outcome -eq 'NotExecuted' }).Count
        Unknown = @($results | Where-Object { $_.outcome -notin @('Passed', 'Failed', 'NotExecuted') }).Count
    }
    # NUnit/VSTest may emit NotExecuted results while its notExecuted counter remains zero.
    # Count the actual results and retain the adapter counter separately for diagnosis.
    $matrix.Skipped = $matrix.ResultOutcomes.NotExecuted
    $matrix.SkippedTests = @($results | Where-Object {
        $_.outcome -eq 'NotExecuted'
    } | ForEach-Object {
        [ordered]@{ Test = $_.testName; Reason = [string]$_.Output.ErrorInfo.Message }
    })
    $matrix.MissingInputs = @($matrix.SkippedTests | Where-Object {
        $_.Reason -match '^Missing local input:'
    })
    # Optional inputs may be absent, but failures, unexpected skips and aborted runs remain fatal.
    if ($matrix.ExitCode -ne 0 -or $trx.TestRun.ResultSummary.outcome -ne 'Completed' -or
        $matrix.Discovered -le 0 -or $matrix.Failed -ne 0 -or
        $matrix.ResultCount -ne $matrix.Discovered -or $matrix.ResultOutcomes.Unknown -ne 0 -or
        $matrix.ResultOutcomes.Passed -ne $matrix.Passed -or
        $matrix.ResultOutcomes.Failed -ne $matrix.Failed -or
        $matrix.Executed -ne ($matrix.ResultOutcomes.Passed + $matrix.ResultOutcomes.Failed) -or
        $matrix.Executed -ne $matrix.Passed -or
        $matrix.Discovered -ne ($matrix.Executed + $matrix.Skipped) -or
        $matrix.SkippedTests.Count -ne $matrix.Skipped -or
        ($matrix.Skipped -gt 0 -and (-not $AllowMissingInputs -or
            $matrix.MissingInputs.Count -ne $matrix.Skipped))) {
        Get-Content -LiteralPath $log -Tail 20 | Write-Host
        $matrix.Error = "V2 $Name must pass all executed tests; only explicitly missing local extension inputs may be skipped. See $trxPath"
        return $matrix
    }
    $matrix.Status = if ($matrix.Skipped -gt 0) { 'PARTIAL_MISSING_INPUTS' } else { 'PASS' }
    return $matrix
}

function Get-SourceManifest {
    $paths = & git -C $repositoryRoot ls-files --cached --others --exclude-standard
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate the Git working tree.' }
    @($paths | Where-Object {
        $_ -match '^(EnhancerSource/|new_structure_project/(src/|tests/|scripts/|global\.json$|NuGet\.Config$|P1\.slnx$))' -and
        $_ -match '\.(cs|csproj|ps1|json|Config|slnx|manifest)$'
    } | Sort-Object -Unique | ForEach-Object {
        $file = Join-Path $repositoryRoot $_
        if (Test-Path -LiteralPath $file -PathType Leaf) {
            "$(Get-FileHash -LiteralPath $file -Algorithm SHA256 | Select-Object -ExpandProperty Hash)  $_"
        } else {
            "MISSING  $_"
        }
    })
}

$summaryPath = Join-Path $outputRoot 'regression-summary.json'
$summary = [ordered]@{
    Status = 'FAILED'
    StartedUtc = [DateTime]::UtcNow.ToString('o')
    FinishedUtc = $null
    Commit = $null
    Branch = $null
    Dirty = $null
    Sdk = $null
    V2 = [ordered]@{
        Baseline = [ordered]@{ Status = 'NOT_RUN'; Discovered = $null; Executed = $null
            Passed = $null; Failed = $null; Skipped = $null }
        LocalExtensions = [ordered]@{ Status = 'NOT_REQUESTED'; Discovered = $null; Executed = $null
            Passed = $null; Failed = $null; Skipped = $null }
    }
    LocalInputs = @()
    Classic = $null
    FreeLayout = $null
    Error = $null
}
$previousDotnetRoot = $env:DOTNET_ROOT
$previousDotnetRootX64 = $env:DOTNET_ROOT_X64
$previousLocalCorpus = $env:PPG_TEST_LOCAL_CORPUS_ROOT
$previousLocalTtf = $env:PPG_TEST_LOCAL_TTF_PATH
$previousLocalOtf = $env:PPG_TEST_LOCAL_OTF_PATH
Push-Location $projectRoot
try {
    $summary.Commit = (& git -C $repositoryRoot rev-parse HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the Git commit.' }
    $summary.Branch = (& git -C $repositoryRoot branch --show-current)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the Git branch.' }
    $statusBefore = @(& git -C $repositoryRoot status --porcelain=v1)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the Git working tree status.' }
    $summary.Dirty = $statusBefore.Count -ne 0
    $statusBefore | Set-Content -LiteralPath (Join-Path $outputRoot 'git-status.txt') -Encoding utf8
    $sourcesBefore = @(Get-SourceManifest)
    $sourcesBefore | Set-Content -LiteralPath (Join-Path $outputRoot 'source-sha256.txt') -Encoding utf8

    if (-not $IncludeLocalExtensions -and ($LocalCorpusRoot -or $LocalTtfPath -or $LocalOtfPath)) {
        throw 'LocalCorpusRoot, LocalTtfPath and LocalOtfPath require -IncludeLocalExtensions.'
    }
    # These are explicit local inputs, never silently discovered outside the checkout. Ignore any
    # inherited variables for this run, then restore the caller's environment in finally.
    $env:PPG_TEST_LOCAL_CORPUS_ROOT = $LocalCorpusRoot
    $env:PPG_TEST_LOCAL_TTF_PATH = $LocalTtfPath
    $env:PPG_TEST_LOCAL_OTF_PATH = $LocalOtfPath
    $summary.LocalInputs = @(
        @{ Name = 'LocalCorpus'; Path = $LocalCorpusRoot; Type = 'Container' }
        @{ Name = 'LocalTtf'; Path = $LocalTtfPath; Type = 'Leaf' }
        @{ Name = 'LocalOtf'; Path = $LocalOtfPath; Type = 'Leaf' }
    ) | ForEach-Object {
        $available = [bool]($_.Path -and (Test-Path -LiteralPath $_.Path -PathType $_.Type))
        [ordered]@{ Name = $_.Name; Path = $_.Path; Requested = [bool]$IncludeLocalExtensions
            Available = $available; Reason = if ($available) { $null } elseif ($_.Path) {
                'The specified local input does not exist.'
            } else { 'No local input was specified.' } }
    }
    $summary.LocalInputs | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $outputRoot 'local-inputs.json') -Encoding utf8

    $dotnet = (Get-Command $DotnetPath -CommandType Application -ErrorAction Stop).Source
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $summary.Sdk = (& $dotnet --version)
    $requiredSdk = (Get-Content -LiteralPath (Join-Path $projectRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    if ($LASTEXITCODE -ne 0 -or $summary.Sdk -ne $requiredSdk) {
        throw "The already-installed SDK $requiredSdk is required; no download was attempted."
    }
    # Keep render screenshots and all test fixtures beside their TRX evidence, outside the checkout.
    # A settings file also avoids VSTest's command-line backslash escaping for NUnit.WorkDirectory.
    $runSettingsPath = Join-Path $outputRoot 'tests.runsettings'
    $escapedTestRoot = [Security.SecurityElement]::Escape($outputRoot)
    "<RunSettings><NUnit><WorkDirectory>$escapedTestRoot</WorkDirectory></NUnit></RunSettings>" |
        Set-Content -LiteralPath $runSettingsPath -Encoding utf8
    Invoke-Logged $dotnet @('build', 'P1.slnx', '-c', 'Release', '--no-restore', '-warnaserror') 'v2-build.log'
    $summary.V2.Baseline = Invoke-V2Matrix 'baseline' 'TestCategory!=LocalCorpus&TestCategory!=LocalFonts' `
        'v2-tests.trx' 'v2-tests.log' $false
    if ($summary.V2.Baseline.Status -ne 'PASS') { throw $summary.V2.Baseline.Error }
    if ($IncludeLocalExtensions) {
        $summary.V2.LocalExtensions = Invoke-V2Matrix 'local extensions' 'TestCategory=LocalCorpus|TestCategory=LocalFonts' `
            'v2-local-tests.trx' 'v2-local-tests.log' $true
        if ($summary.V2.LocalExtensions.Status -eq 'FAILED') { throw $summary.V2.LocalExtensions.Error }
    }

    # Build from current V1 source without replacing the usable enhancer in the repository.
    $legacyRoot = Join-Path $outputRoot 'v1'
    New-Item -ItemType Directory -Path (Join-Path $legacyRoot 'EnhancerSource') | Out-Null
    $legacyExe = Join-Path $legacyRoot 'Phigros Preview Enhancer.exe'
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    if (-not (Test-Path -LiteralPath $compiler)) { throw 'The installed .NET Framework C# compiler is required.' }
    $legacySource = Join-Path $repositoryRoot 'EnhancerSource'
    $compileArguments = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001',
        '/warnaserror+', "/out:$legacyExe", "/win32manifest:$(Join-Path $legacySource 'app.manifest')",
        '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
        '/reference:System.Xml.dll', '/reference:System.Core.dll')
    $compileArguments += @('Enhancer.cs', 'Tests.cs', 'FreeLayout.cs', 'FreeTests.cs',
        'GeometryTransforms.cs', 'AtomicFile.cs') | ForEach-Object { Join-Path $legacySource $_ }
    Invoke-Logged $compiler $compileArguments 'v1-build.log'
    $classicRoot = Join-Path $legacyRoot 'classic'
    New-Item -ItemType Directory -Path $classicRoot | Out-Null
    if ($ReferenceImagePath) {
        Copy-Item -LiteralPath $ReferenceImagePath -Destination (Join-Path $classicRoot '参考底图.png')
    }
    foreach ($mode in @('classic', 'free')) {
        $flag = if ($mode -eq 'classic') { '--self-test' } else { '--free-test' }
        $folder = Join-Path $legacyRoot $mode
        Write-Host "Running V1 $mode ..."
        $process = Start-Process -FilePath $legacyExe -ArgumentList @($flag, ('"' + $folder + '"')) `
            -WorkingDirectory $legacyRoot -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) {
            throw "V1 $mode failed with exit code $($process.ExitCode). See $legacyRoot/EnhancerSource/last-error.txt"
        }
        $resultName = if ($mode -eq 'classic') { '测试结果.txt' } else { '自由排版测试结果.txt' }
        $result = Get-Content -LiteralPath (Join-Path $folder $resultName) -Raw -Encoding utf8
        if ($result -notmatch '^PASS: ([1-9][0-9]*) checks') { throw "V1 $mode has no passing result." }
        $checks = [int]$Matches[1]
        if ($mode -eq 'classic') {
            $summary.Classic = [ordered]@{ Checks = $checks; ReferenceImage = [bool]$ReferenceImagePath }
        } else { $summary.FreeLayout = [ordered]@{ Checks = $checks } }
    }
    if (@(Compare-Object $sourcesBefore @(Get-SourceManifest)).Count -ne 0) {
        throw 'Source files changed during regression; rerun against a stable working tree.'
    }
    $summary.Status = 'PASS'
    Write-Host "REGRESSION PASS: V2 baseline $($summary.V2.Baseline.Passed); classic $($summary.Classic.Checks); free $($summary.FreeLayout.Checks)."
    Write-Host "Local extensions: $($summary.V2.LocalExtensions.Status) (passed $($summary.V2.LocalExtensions.Passed), skipped $($summary.V2.LocalExtensions.Skipped))."
    Write-Host "Evidence: $summaryPath"
}
catch {
    $summary.Error = $_.Exception.Message
    throw
}
finally {
    $summary.FinishedUtc = [DateTime]::UtcNow.ToString('o')
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    $env:DOTNET_ROOT = $previousDotnetRoot
    $env:DOTNET_ROOT_X64 = $previousDotnetRootX64
    $env:PPG_TEST_LOCAL_CORPUS_ROOT = $previousLocalCorpus
    $env:PPG_TEST_LOCAL_TTF_PATH = $previousLocalTtf
    $env:PPG_TEST_LOCAL_OTF_PATH = $previousLocalOtf
    Pop-Location
}

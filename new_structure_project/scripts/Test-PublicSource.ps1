param(
    [Parameter(Mandatory = $true)] [string] $OutputRoot,
    [string] $DotnetPath = 'dotnet',
    [switch] $BuildOnly
)

# Public-source ZIPs have no .git directory. This gate fingerprints files directly.
# It never restores packages, installs tools or invokes candidate-retention cleanup.
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '..')).TrimEnd('\', '/')
$output = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\', '/')
if ($output -eq [IO.Path]::GetPathRoot($output).TrimEnd('\', '/') -or
    $output.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    $repositoryRoot.StartsWith($output + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must be outside the source tree, and cannot be its ancestor or a volume root.'
}
if (Test-Path -LiteralPath $output) {
    if (-not (Test-Path -LiteralPath $output -PathType Container) -or
        (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1)) {
        throw 'OutputRoot must be new or empty; existing evidence is never overwritten.'
    }
} else { New-Item -ItemType Directory -Path $output | Out-Null }

function Source-Manifest {
    @(Get-ChildItem -LiteralPath $repositoryRoot -File -Recurse -Force | Where-Object {
        $relative = [IO.Path]::GetRelativePath($repositoryRoot, $_.FullName).Replace('\', '/')
        $relative -notmatch '(^|/)(\.git|bin|obj)(/|$)' -and
        ($relative -match '^EnhancerSource/.*\.(cs|ps1|manifest)$' -or
         $relative -match '^new_structure_project/(src/|tests/|scripts/|global\.json$|NuGet\.Config$|P1\.slnx$)' -and
         $relative -match '\.(cs|csproj|ps1|json|Config|slnx|manifest)$')
    } | ForEach-Object {
        if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Source files cannot be reparse points.' }
        $relative = [IO.Path]::GetRelativePath($repositoryRoot, $_.FullName).Replace('\', '/')
        "$(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256 | Select-Object -ExpandProperty Hash)  $relative"
    } | Sort-Object)
}
function Invoke-Logged([string] $Executable, [string[]] $Arguments, [string] $Name) {
    Write-Host "Running $Name ..."
    & $Executable @Arguments *> (Join-Path $output $Name)
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath (Join-Path $output $Name) -Tail 20 | Write-Host
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}
function Require([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }

$summary = [ordered]@{
    Status = 'FAILED'; Scope = 'PUBLIC_SOURCE_AUTOMATION'; StartedUtc = [DateTime]::UtcNow.ToString('o')
    FinishedUtc = $null; Sdk = $null; BuildOnly = [bool]$BuildOnly
    SourceManifestSha256 = $null; SourceFiles = 0; SourceUnchanged = $false
    RequiredFilter = 'TestCategory!=LocalCorpus&TestCategory!=LocalFonts'
    RequiredTests = $null; Classic = $null; FreeLayout = $null; Compatibility = $null
    ScriptTests = @(); Error = $null
    OptionalTests = [ordered]@{
        Status = 'NOT_REQUESTED'; Reason = 'User-owned projects/fonts are not distributed with public source.'
        Categories = @('LocalCorpus', 'LocalFonts')
        Tests = @('P3CompatibilityTests.RealLegacyCorpusIsReadWithoutModifyingTheSources',
            'P4LocalFontTests.ExistingLocalOtfCanBeImportedWithoutBundlingItWithTests',
            'P4LocalFontTests.ExistingLocalTtfCanBeImportedWithoutBundlingItWithTests')
    }
}
$savedEnvironment = @{}
foreach ($name in @('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'AVALONIA_TELEMETRY_OPTOUT',
    'DOTNET_CLI_TELEMETRY_OPTOUT', 'TESTINGPLATFORM_TELEMETRY_OPTOUT',
    'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE', 'DOTNET_GENERATE_ASPNET_CERTIFICATE',
    'PPG_TEST_LOCAL_CORPUS_ROOT', 'PPG_TEST_LOCAL_TTF_PATH', 'PPG_TEST_LOCAL_OTF_PATH')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$before = @(Source-Manifest)
$before | Set-Content -LiteralPath (Join-Path $output 'source-before.sha256') -Encoding utf8
$summary.SourceFiles = $before.Count
$summary.SourceManifestSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($before -join "`n"))))
Push-Location $projectRoot
try {
    Require ($IsWindows) 'This gate requires Windows and PowerShell 7.'
    $dotnet = (Get-Command $DotnetPath -CommandType Application -ErrorAction Stop).Source
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:AVALONIA_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    foreach ($name in @('PPG_TEST_LOCAL_CORPUS_ROOT', 'PPG_TEST_LOCAL_TTF_PATH', 'PPG_TEST_LOCAL_OTF_PATH')) {
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
    $requiredSdk = (Get-Content -LiteralPath (Join-Path $projectRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $summary.Sdk = & $dotnet --version
    Require ($LASTEXITCODE -eq 0 -and $summary.Sdk -eq $requiredSdk) "Installed SDK $requiredSdk is required; no download was attempted."
    Invoke-Logged $dotnet @('build', 'P1.slnx', '-c', 'Release', '--no-restore', '--no-incremental', '-warnaserror', '-p:UseSharedCompilation=false') 'release-build.log'
    $legacy = Join-Path $output 'compatibility-reference'
    New-Item -ItemType Directory -Path (Join-Path $legacy 'EnhancerSource') | Out-Null
    $legacyExe = Join-Path $legacy 'Phigros Preview Enhancer.exe'
    $legacySource = Join-Path $repositoryRoot 'EnhancerSource'
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    Require (Test-Path -LiteralPath $compiler -PathType Leaf) 'The installed .NET Framework compiler is required.'
    $arguments = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001', '/warnaserror+',
        "/out:$legacyExe", "/win32manifest:$(Join-Path $legacySource 'app.manifest')", '/reference:System.Drawing.dll',
        '/reference:System.Windows.Forms.dll', '/reference:System.Xml.dll', '/reference:System.Core.dll')
    $arguments += @('Enhancer.cs', 'Tests.cs', 'FreeLayout.cs', 'FreeTests.cs', 'GeometryTransforms.cs', 'AtomicFile.cs') |
        ForEach-Object { Join-Path $legacySource $_ }
    Invoke-Logged $compiler $arguments 'compatibility-reference-build.log'
    if ($BuildOnly) { $summary.Status = 'BUILD_ONLY'; return }

    # Synthetic bitmap enables ALL classic interaction checks without copyrighted artwork.
    Add-Type -AssemblyName System.Drawing
    $classic = Join-Path $legacy 'classic'
    New-Item -ItemType Directory -Path $classic | Out-Null
    $bitmap = [Drawing.Bitmap]::new(1920, 1080)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([Drawing.Color]::FromArgb(50, 65, 80))
        $bitmap.Save((Join-Path $classic '参考底图.png'), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
    foreach ($mode in @('classic', 'free')) {
        $flag = if ($mode -eq 'classic') { '--self-test' } else { '--free-test' }
        $folder = Join-Path $legacy $mode
        $process = Start-Process -FilePath $legacyExe -ArgumentList @($flag, ('"' + $folder + '"')) -WorkingDirectory $legacy -WindowStyle Hidden -Wait -PassThru
        try {
            if ($process.ExitCode -ne 0) {
                $diagnostic = Join-Path $legacy 'EnhancerSource/last-error.txt'
                if (Test-Path -LiteralPath $diagnostic -PathType Leaf) {
                    Write-Host "Compatibility $mode failure details:"
                    Get-Content -LiteralPath $diagnostic -Tail 60 -Encoding utf8 | Write-Host
                }
            }
            Require ($process.ExitCode -eq 0) "Compatibility $mode self-test failed."
        } finally { $process.Dispose() }
        $resultFile = if ($mode -eq 'classic') { '测试结果.txt' } else { '自由排版测试结果.txt' }
        $text = Get-Content -LiteralPath (Join-Path $folder $resultFile) -Raw -Encoding utf8
        Require ($text -match '^PASS: ([1-9][0-9]*) checks') "Missing $mode checks."
        $checks = [int]$Matches[1]
        if ($mode -eq 'classic') {
            Require ($checks -ge 317) 'Classic interaction checks must not be silently omitted.'
            $summary.Classic = @{ Checks = $checks; ReferenceImage = 'SYNTHETIC_1920x1080' }
        } else {
            Require ($checks -ge 483) 'Free-layout checks must not be silently omitted.'
            $summary.FreeLayout = @{ Checks = $checks }
        }
    }
    $settings = Join-Path $output 'tests.runsettings'
    $escaped = [Security.SecurityElement]::Escape($output)
    "<RunSettings><NUnit><WorkDirectory>$escaped</WorkDirectory></NUnit></RunSettings>" |
        Set-Content -LiteralPath $settings -Encoding utf8
    Invoke-Logged $dotnet @('test', 'P1.slnx', '-c', 'Release', '--no-restore', '--no-build', '--settings', $settings,
        '--filter', $summary.RequiredFilter, '--logger', 'trx;LogFileName=required.trx', '--results-directory', $output) 'required-tests.log'
    [xml]$trx = Get-Content -LiteralPath (Join-Path $output 'required.trx') -Raw
    $counts = $trx.TestRun.ResultSummary.Counters
    $results = @($trx.TestRun.Results.UnitTestResult | Where-Object { $null -ne $_ })
    Require ($null -ne $counts -and $results.Count -gt 0) 'Required suite must produce nonempty TRX results.'
    $summary.RequiredTests = [ordered]@{
        Discovered = [int]$counts.total; Executed = [int]$counts.executed; Passed = [int]$counts.passed
        Failed = [int]$counts.failed; ReportedNotExecuted = [int]$counts.notExecuted
        ResultCount = $results.Count; NonPassingResults = @($results | Where-Object outcome -ne 'Passed').Count
    }
    Require ($trx.TestRun.ResultSummary.outcome -eq 'Completed' -and
        [int]$counts.total -eq $results.Count -and [int]$counts.executed -eq $results.Count -and
        [int]$counts.passed -eq $results.Count -and [int]$counts.failed -eq 0 -and
        [int]$counts.notExecuted -eq 0 -and $summary.RequiredTests.NonPassingResults -eq 0) 'Every required test must execute and pass; skipped tests fail the gate.'

    $assembly = Join-Path $projectRoot 'src/Editor.Desktop/bin/Release/net10.0-windows/Editor.Desktop.dll'
    $matrixRoot = Join-Path $output 'compatibility-matrix'
    Invoke-Logged $dotnet @($assembly, '--compat-evidence', $matrixRoot, $legacyExe) 'compatibility-matrix.log'
    $matrix = Get-Content -LiteralPath (Join-Path $matrixRoot 'compatibility-evidence.json') -Raw | ConvertFrom-Json
    Require ($matrix.status -eq 'PASS' -and $matrix.synthetic -gt 0 -and $matrix.technicalFailures -eq 0 -and $matrix.local -eq 0) 'Synthetic compatibility matrix failed or used unexpected private inputs.'
    $summary.Compatibility = @{ Status = $matrix.status; Synthetic = $matrix.synthetic; Local = $matrix.local
        Accepted = $matrix.accepted; Rejected = $matrix.rejected; TechnicalFailures = $matrix.technicalFailures }

    foreach ($gate in @(
        @{ Script = 'Test-AcceptanceEvidence.ps1'; Folder = 'acceptance-script-tests'; Report = 'script-tests.json'; Results = 'Results' },
        @{ Script = 'Test-TestArtifactRetention.ps1'; Folder = 'retention-script-tests'; Report = 'retention-script-tests.json'; Results = 'Results' },
        @{ Script = 'Test-ReleasePackageIntegrity.ps1'; Folder = 'release-script-tests'; Report = 'release-integrity-tests.json'; Results = 'Tests' }
    )) {
        $folder = Join-Path $output $gate.Folder
        & (Join-Path $PSScriptRoot $gate.Script) -OutputRoot $folder *> (Join-Path $output ($gate.Folder + '.log'))
        $report = Get-Content -LiteralPath (Join-Path $folder $gate.Report) -Raw | ConvertFrom-Json
        $cases = @($report.($gate.Results))
        Require ($report.Status -eq 'PASS' -and $report.Checks -gt 0 -and $report.Checks -eq $cases.Count -and
            @($cases | Where-Object Status -ne 'PASS').Count -eq 0) "Script gate $($gate.Script) has failed or incomplete evidence."
        $summary.ScriptTests += @{ Script = $gate.Script; Checks = $report.Checks; Status = $report.Status }
    }
    $summary.Status = 'PASS'
} catch { $summary.Error = $_.Exception.Message; throw }
finally {
    Pop-Location
    foreach ($entry in $savedEnvironment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
    $after = @(Source-Manifest)
    $after | Set-Content -LiteralPath (Join-Path $output 'source-after.sha256') -Encoding utf8
    $summary.SourceUnchanged = (($before -join "`n") -ceq ($after -join "`n"))
    if (-not $summary.SourceUnchanged) { $summary.Status = 'FAILED'; $summary.Error = 'Source tree changed during verification.' }
    $summary.FinishedUtc = [DateTime]::UtcNow.ToString('o')
    $summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'public-test-summary.json') -Encoding utf8
    if (-not $summary.SourceUnchanged) { throw $summary.Error }
}

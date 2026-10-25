# Joins existing gate evidence; it never grants visual, hardware or publication acceptance.
function Get-AcceptanceBundleEvidence([string] $OutputRoot, [string[]] $ExpectedSources) {
    Set-StrictMode -Version Latest
    function Require([bool] $Condition, [string] $Message) {
        if (-not $Condition) { throw $Message }
    }
    function Read-Record([string] $Relative) {
        Get-Content -LiteralPath (Join-Path $OutputRoot $Relative) -Raw | ConvertFrom-Json
    }
    function Hash([string] $Relative) {
        (Get-FileHash -LiteralPath (Join-Path $OutputRoot $Relative) -Algorithm SHA256).Hash
    }
    function Check-Hash([string] $Relative, [string] $Expected) {
        Require ($Expected -match '^[0-9A-Fa-f]{64}$' -and (Hash $Relative) -eq $Expected) "Changed artifact: $Relative"
    }
    $sourceHash = Get-AcceptanceManifestHash $ExpectedSources
    $scriptTests = Read-Record 'script-tests/script-tests.json'
    Require ($scriptTests.Status -eq 'PASS' -and $scriptTests.Checks -gt 0 -and
        @($scriptTests.Results).Count -eq $scriptTests.Checks -and
        @($scriptTests.Results | Where-Object { $_.Status -ne 'PASS' }).Count -eq 0) 'Acceptance script regression is incomplete.'
    $retentionTests = Read-Record 'retention-tests/retention-script-tests.json'
    Require ($retentionTests.Status -eq 'PASS' -and $null -eq $retentionTests.Error -and $retentionTests.Checks -gt 0 -and
        @($retentionTests.Results).Count -eq $retentionTests.Checks -and
        @($retentionTests.Results | Where-Object { $_.Status -ne 'PASS' }).Count -eq 0) 'Retention script regression is incomplete.'
    $regression = Read-Record 'regression/regression-summary.json'
    $releaseTests = Read-Record 'release-integrity-tests/release-integrity-tests.json'
    Require ($releaseTests.Status -eq 'PASS' -and $null -eq $releaseTests.Error -and $releaseTests.Checks -gt 0 -and
        $releaseTests.Passed -eq $releaseTests.Checks -and $releaseTests.Failed -eq 0 -and
        @($releaseTests.Tests).Count -eq $releaseTests.Checks -and
        @($releaseTests.Tests | Where-Object { $_.Status -ne 'PASS' }).Count -eq 0) 'Release integrity script regression is incomplete.'
    $compatibility = Read-Record 'compatibility/p3-summary.json'
    $portable = Read-Record 'portable/portable-summary.json'
    foreach ($record in @($regression, $compatibility, $portable)) {
        Require ($record.Status -eq 'PASS' -and $null -eq $record.Error) 'A child gate failed or has incomplete evidence.'
        Require ($record.Commit -eq $regression.Commit -and $record.Branch -eq $regression.Branch -and
            $record.Dirty -eq $regression.Dirty -and $record.Sdk -eq $regression.Sdk) 'Child gate Git/SDK identities differ.'
        Require (-not [string]::IsNullOrWhiteSpace($record.Commit) -and
            -not [string]::IsNullOrWhiteSpace($record.Sdk)) 'Missing child gate identity.'
    }
    foreach ($relative in @('regression/source-sha256.txt', 'compatibility/source-before.sha256',
        'compatibility/source-after.sha256', 'portable/source-before.sha256', 'portable/source-after.sha256')) {
        $actual = @(Get-Content -LiteralPath (Join-Path $OutputRoot $relative))
        Require (($actual -join "`n") -ceq ($ExpectedSources -join "`n")) "Different working-tree sources: $relative"
    }
    foreach ($record in @($compatibility, $portable)) {
        Require ($record.SourceUnchanged -eq $true -and $record.SourceFiles -eq $ExpectedSources.Count -and
            $record.SourceManifestSha256 -eq $sourceHash) 'Child gate source identity is stale.'
    }
    $baseline = $regression.V2.Baseline
    Require ($baseline.Status -eq 'PASS' -and $baseline.ExitCode -eq 0 -and $baseline.Passed -gt 0 -and
        $baseline.Failed -eq 0 -and $baseline.Skipped -eq 0 -and
        $baseline.Discovered -eq $baseline.Passed -and $baseline.Executed -eq $baseline.Passed -and
        $baseline.ResultCount -eq $baseline.Passed) 'Mandatory regression is incomplete.'
    Require ($regression.Classic.Checks -gt 0 -and $regression.FreeLayout.Checks -gt 0) 'V1 regression is incomplete.'
    $local = $regression.V2.LocalExtensions
    Require ($local.Status -in @('PASS', 'NOT_REQUESTED', 'PARTIAL_MISSING_INPUTS')) 'Local extensions failed.'
    if ($local.Status -ne 'NOT_REQUESTED') {
        Require ($local.ExitCode -eq 0 -and $local.Failed -eq 0 -and $local.Executed -eq $local.Passed -and
            $local.Discovered -gt 0 -and $local.Discovered -eq ($local.Passed + $local.Skipped) -and
            $local.ResultCount -eq $local.Discovered -and
            (($local.Status -eq 'PASS' -and $local.Skipped -eq 0) -or
             ($local.Status -eq 'PARTIAL_MISSING_INPUTS' -and $local.Skipped -gt 0 -and
              @($local.MissingInputs).Count -eq $local.Skipped))) 'Local extension counts are inconsistent.'
    }
    $matrix = $compatibility.Matrix
    Require ($matrix.Status -eq 'PASS' -and $matrix.TechnicalFailures -eq 0 -and $matrix.Accepted -gt 0 -and
        $matrix.Rejected -ge 0 -and $matrix.Synthetic -gt 0 -and $matrix.Local -ge 0 -and
        ($matrix.Accepted + $matrix.Rejected) -eq ($matrix.Synthetic + $matrix.Local)) 'Compatibility matrix is incomplete.'
    Check-Hash 'compatibility/matrix/compatibility-evidence.json' $matrix.ReportSha256
    Require ($compatibility.VisualAcceptance -eq 'PENDING' -and $compatibility.LegacyWriterDecision -eq 'PENDING') 'Automated compatibility evidence cannot grant visual or Writer acceptance.'
    $runtime = $portable.Runtime
    Require ($portable.PublishExitCode -eq 0 -and $runtime.Status -eq 'PASS' -and $runtime.DriverExitCode -eq 0 -and
        $runtime.PackageUnchanged -eq $true -and $runtime.ReducedEnvironment -eq $true -and
        $runtime.DotnetOnReducedPath -eq $false -and $runtime.SourceManifestSha256 -eq $sourceHash -and
        $runtime.PackageFiles -gt 0 -and $runtime.PackageFiles -eq $portable.Package.PublishedFiles -and
        $portable.ReopenChecks -eq 11 -and $portable.CliPngChecks -eq 44 -and
        $portable.FontStages -eq 5 -and $portable.FontPngChecks -eq 16) 'Portable runtime evidence is incomplete.'
    Check-Hash 'portable/runtime-verification/runtime-summary.json' $runtime.ReportSha256
    $archiveName = 'portable/Editor.Desktop-win-x64-internal.zip'
    Require ([IO.Path]::GetFullPath($portable.Package.Archive) -eq
        [IO.Path]::GetFullPath((Join-Path $OutputRoot $archiveName))) 'Candidate archive belongs to another bundle.'
    Check-Hash $archiveName $portable.Package.ArchiveSha256
    $build = Read-Record 'portable/extracted/build-evidence.json'
    Require ($build.SourceManifestSha256 -eq $sourceHash -and $build.CandidateStatus -eq 'INTERNAL_ONLY') 'Extracted candidate belongs to another source version.'
    foreach ($gate in @('CleanMachine', 'WindowsHardware', 'ThirdPartyNotices', 'ProjectLicense', 'P3Visual', 'LegacyWriter')) {
        Require ($portable.Acceptance.$gate -eq 'PENDING') "Automated bundle cannot grant acceptance: $gate"
    }
    $artifacts = [ordered]@{}
    foreach ($relative in @('script-tests/script-tests.json', 'retention-tests/retention-script-tests.json',
        'release-integrity-tests/release-integrity-tests.json',
        'regression/regression-summary.json', 'regression/v2-tests.trx',
        'compatibility/p3-summary.json', 'compatibility/matrix/compatibility-evidence.json',
        'compatibility/matrix/index.html', 'portable/portable-summary.json',
        'portable/runtime-verification/runtime-summary.json', 'portable/extracted/build-evidence.json', $archiveName)) {
        $artifacts[$relative] = Hash $relative
    }
    if ($local.Status -ne 'NOT_REQUESTED') { $artifacts['regression/v2-local-tests.trx'] = Hash 'regression/v2-local-tests.trx' }
    [ordered]@{
        Commit = $regression.Commit; Branch = $regression.Branch; Dirty = $regression.Dirty; Sdk = $regression.Sdk
        SourceManifestSha256 = $sourceHash; SourceFiles = $ExpectedSources.Count
        ScriptChecks = $scriptTests.Checks; RetentionChecks = $retentionTests.Checks
        ReleaseIntegrityChecks = $releaseTests.Checks
        Baseline = $baseline; LocalExtensions = $local; Classic = $regression.Classic; FreeLayout = $regression.FreeLayout
        Matrix = $matrix; ReopenChecks = $portable.ReopenChecks; CliPngChecks = $portable.CliPngChecks
        FontStages = $portable.FontStages; FontPngChecks = $portable.FontPngChecks
        Package = $portable.Package; ArtifactSha256 = $artifacts
    }
}

function Write-AcceptanceOverview([string] $OutputRoot, $Summary) {
    function Encode($Value) { [Net.WebUtility]::HtmlEncode([string]$Value) }
    $evidence = $Summary.Evidence
    $counts = if ($null -eq $evidence) { 'Not completed; inspect child logs and the error below.' } else {
        "Acceptance script checks $($evidence.ScriptChecks) / retention checks $($evidence.RetentionChecks); " +
        "V2 mandatory $($evidence.Baseline.Passed); local extensions $($evidence.LocalExtensions.Status) " +
        "(passed $($evidence.LocalExtensions.Passed), skipped $($evidence.LocalExtensions.Skipped)); " +
        "V1 classic $($evidence.Classic.Checks) / free $($evidence.FreeLayout.Checks); " +
        "P3 accepted $($evidence.Matrix.Accepted) / rejected $($evidence.Matrix.Rejected); " +
        "11 reopens / 44 CLI PNGs / 5 font processes / 16 font PNGs."
    }
    $retentionChecks = if ($null -eq $evidence) { 'PENDING' } else { $evidence.RetentionChecks }
    $retentionHash = if ($null -eq $evidence) { 'PENDING' } else { $evidence.ArtifactSha256['retention-tests/retention-script-tests.json'] }
    $html = @"
<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>V2 同步验收包</title><style>body{font:16px/1.7 system-ui,sans-serif;max-width:1000px;margin:40px auto;padding:0 24px;background:#f5f7fa;color:#182230}section{background:white;padding:20px 28px;margin:20px 0;border:1px solid #d5dce5;border-radius:12px}code{overflow-wrap:anywhere}a{color:#175bb3}li{margin:8px 0}</style>
<h1>V2 同步验收包</h1><section><h2>$(Encode $Summary.Status) — 自动技术验证 / 内部试用</h2>
<p>$(Encode $counts)</p><p>错误：$(Encode $Summary.Error)</p>
<p>源码：<code>$(Encode $Summary.SourceManifestSha256)</code><br>Git 起点：$(Encode $Summary.Commit)，$(Encode $Summary.Branch)，Dirty=$(Encode $Summary.Dirty)</p>
<p>测试通过不等于视觉、真机或发行验收。所有正式门槛仍待完成。</p></section>
<section><h2>审阅与试用入口</h2><ol>
<li><a href="compatibility/matrix/index.html">P3 逐项对照：V1 / V2 / 差分 / 叠加 / 字段</a>。决定旧格式写回是否必需，以及 Badge、数字顺序、文字、裁剪、渐变与柔化差异是否接受。</li>
<li><a href="portable/Editor.Desktop-win-x64-internal.zip">当前源码的内部便携候选 ZIP</a>。完整解压后运行 Editor.Desktop.exe；保留原始工程副本。程序不写回旧格式。</li>
<li>在另一 Windows 主机运行 <code>Editor.Desktop.exe --verify-portable &quot;&lt;新证据目录&gt;&quot;</code>，再实际检查选择器、三/五曲完整流程、中文 IME、字体、125–200% DPI 与跨屏。当前主机的自动结果不证明干净机器。</li>
<li>单独审阅第三方通知、自研许可及发布范围。候选包供内部验收，尚未批准公开发布。</li>
</ol></section><section><h2>机器可读证据</h2><ul>
<li><a href="acceptance-summary.json">同步摘要及所有关联文件 SHA-256</a></li>
<li><a href="retention-tests/retention-script-tests.json">产物清理脚本回归</a>：$(Encode $retentionChecks) 项；SHA-256：<code>$(Encode $retentionHash)</code></li>
<li><a href="regression/regression-summary.json">V2 / V1 回归摘要</a></li>
<li><a href="compatibility/p3-summary.json">P3 技术对照摘要</a></li>
<li><a href="portable/portable-summary.json">候选发布预检摘要</a></li>
<li><a href="portable/runtime-verification/runtime-summary.json">独立进程与字体重启摘要</a></li>
</ul><p>链接对应本目录中的实际文件；失败时部分文件可能尚未生成，失败日志保留。</p></section></html>
"@
    $html | Set-Content -LiteralPath (Join-Path $OutputRoot 'index.html') -Encoding utf8
}

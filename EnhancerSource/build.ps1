$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '找不到系统自带的 .NET Framework C# 编译器。' }
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 "/out:$projectRoot\LayoutCompatibility.Reference.exe" /win32manifest:"$PSScriptRoot\app.manifest" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll /reference:System.Core.dll "$PSScriptRoot\Enhancer.cs" "$PSScriptRoot\Tests.cs" "$PSScriptRoot\FreeLayout.cs" "$PSScriptRoot\FreeTests.cs" "$PSScriptRoot\GeometryTransforms.cs" "$PSScriptRoot\AtomicFile.cs"
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Write-Output "已生成 $projectRoot\LayoutCompatibility.Reference.exe"

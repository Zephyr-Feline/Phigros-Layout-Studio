# Phigros Layout Studio 1.0 源码构建

PLS 是 Windows 桌面应用。源码公开供阅读、构建和按权利声明允许的范围使用，不表示授予开源许可证；参见仓库权利声明和第三方归属材料。

## 环境与依赖

- Windows x64，PowerShell 7、Git（源码压缩包构建不强制 Git）。
- .NET SDK **10.0.402**，`new_structure_project/global.json` 禁止 SDK 自动换版。
- 兼容实现回归需要 Windows 已安装的 .NET Framework C# 编译器：`%WINDIR%/Microsoft.NET/Framework64/v4.0.30319/csc.exe`。
- NuGet 依赖由七份 `packages.lock.json` 固定。运行库和构建/测试包来自 NuGet.org；还原时使用 locked mode，不改写锁文件。

仓库不包含原第三方生成器 EXE、曲绘、用户工程或私有字体。自动测试生成合成图片、文档和测试数据，字体基础测试使用既有依赖内的 Inter，不需要额外下载字体。不要为满足测试随意加入有版权的素材。

下列联网命令会取得 SDK（若自行安装）、已锁定 NuGet 包和必要运行库。请先按自己的环境审批这些下载。配置文件并不证明这些操作已执行。具备完整本地缓存时可按离线小节执行。

## 还原、构建与测试

在仓库根目录打开 PowerShell 7：

```powershell
Set-Location ./new_structure_project
. ./scripts/Enter-P1Environment.ps1
dotnet restore P1.slnx --locked-mode --configfile NuGet.Config -p:NuGetAudit=true -p:DisableTransitiveFrameworkReferenceDownloads=true
if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
./scripts/Test-PublicSource.ps1 -OutputRoot (Join-Path ([IO.Path]::GetTempPath()) ('pls-check-' + [Guid]::NewGuid().ToString('N')))
```

`Enter-P1Environment.ps1` 只使用已安装的 SDK；可传 `-DotnetPath <dotnet.exe 的完整路径>`。脚本不会安装 SDK。关闭该 PowerShell 进程即可结束会话环境变更。

`Test-PublicSource.ps1` 不联网还原。它以 Release、警告视为错误、禁止增量构建和共享编译器的方式构建全部工程，执行必需 NUnit 测试、兼容实现 classic/free 回归、合成兼容矩阵，以及验收证据、文件保留和发行包完整性三个 PowerShell 自检。输出必须是源码目录以外的新目录；保留报告、日志、TRX 和源码指纹供复核。它使用文件树指纹，因此源码 ZIP 中没有 `.git` 时也能运行。

必需测试的退出码、TRX 计数和每条结果必须一致且全部通过；跳过、不完整和零测试均失败。经典交互测试使用脚本生成的参考图片，不能靠缺少图片绕过。文件保留自检只操作独立输出目录中的合成 fixture；该入口不清理历史候选。

三项既有本地扩展测试单列为 `NOT_REQUESTED`：一项用户工程语料（`LocalCorpus`）和两项用户自备 TTF/OTF（`LocalFonts`）。它们没有被算作必需测试 PASS。需要运行时可在有合法本地资源的环境按源码中的显式变量提供输入；不要把工程和字体上传到公共 CI。其余字体、图片、格式和兼容测试仍在必需集合中。

`DisableTransitiveFrameworkReferenceDownloads=true` 阻止 SDK 为未使用的传递框架引用自动获取额外运行库，不关闭锁检查，不删除包引用或排除测试。不要以修改 `RuntimeIdentifiers`、更新锁文件或静默跳过测试修复还原失败。

## 已有缓存的离线环境

先确认 SDK 和所需包已存在。设置 `NUGET_PACKAGES` 指向已安装的缓存，并只把该本地目录作为源：

```powershell
$env:NUGET_PACKAGES = '<已有 NuGet 全局包目录>'
dotnet restore P1.slnx --locked-mode --source $env:NUGET_PACKAGES -p:NuGetAudit=false -p:DisableTransitiveFrameworkReferenceDownloads=true
if ($LASTEXITCODE -ne 0) { throw 'Offline locked restore failed; no network fallback is allowed.' }
./scripts/Test-PublicSource.ps1 -OutputRoot '<源码目录以外的新输出目录>'
```

离线时 `NuGetAudit=false` 仅表示无法查询在线漏洞源，不能写成最新依赖漏洞扫描已通过。缺失包或 SDK 时应停止，核对所需内容并取得下载批准，不追加联网源、不安装新工具、不使用 `--ignore-failed-sources` 掩盖问题。

## 运行与输出

```powershell
dotnet run --project ./src/Editor.Desktop/Editor.Desktop.csproj -c Release --no-restore --no-build
```

常规 Release 构建的输出不是正式便携发行包。自包含打包还需要 `win-x64` 的已锁定依赖和运行库、完整第三方许可/归属材料、源码及包文件清单、最终 SHA-256 和针对新包的验证。源码、可执行文件、说明或 ZIP 内容发生改变后，应生成新的候选身份和哈希；不要沿用历史候选的散列值。

## Windows CI 与 CodeQL

仓库准备了 `.github/workflows/windows-ci.yml` 和 `codeql.yml`。它们采用 `windows-2025`、固定 SDK、locked restore 和完整 Action commit SHA。Windows CI 执行以上公共自动化集合；CodeQL 使用手工构建覆盖编辑器和兼容 C# 实现。CodeQL 查询集为 `security-extended`。

运行 CI 会在 GitHub 托管 runner 上下载 Action、SDK（若 runner 缺失）、锁定依赖及 CodeQL bundle/query 内容。本机准备这些文件不代表已在 GitHub 实跑，更不代表扫描无告警。需在目标仓库确认构建、测试和 CodeQL 提取/上传成功，再按实际检查名配置 `main` 合并要求。

CI 使用 `pull_request`，不使用 `pull_request_target`，不接入发布凭据。默认仅 `contents: read`，CodeQL job 仅额外申请上传扫描结果所需的 `security-events: write`；checkout 不保留凭据。不要为解决 fork PR 扫描失败而给不可信 PR 发布 token。CodeQL 配置和权限依据 [GitHub 官方 Action 文档](https://github.com/github/codeql-action/tree/9b3254085494b55a0f7c4c2b3d2d52cc712ce370)；构建模式参见 [官方编译语言文档](https://docs.github.com/en/code-security/reference/code-scanning/codeql/build-options-for-compiled-languages)。

自动化不能代替真实 Windows 设备、中文输入法、多显示器、DPI 或干净机器的人工验收。已经完成且仍适用的人工记录保持其原身份；发行说明应区分支持范围、未测项与明确免测项。

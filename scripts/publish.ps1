# publish.ps1 — 发布包构建：dist/v<Version>/win-x64/（依赖客户端 .NET 的单文件 win-x64 + SHA256SUMS + 许可 + README 摘要）
# 用法：.\scripts\publish.ps1 [-Version 0.1.0]
# 依据 docs/release.md §1/§3：产物 = EXE + README 摘要 + LICENSE/THIRD-PARTY-NOTICES + SHA256SUMS.txt；
# 不分发 NuGet 缓存、SDK、真实会话数据、配置或测试 fixtures。未签名，明确标注。
#Requires -Version 5
[CmdletBinding()]
param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
. (Join-Path $repo 'scripts/enter-env.ps1')
$env:TEMP = $env:TMP = Join-Path $repo '.artifacts/tmp'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $repo '.artifacts/nuget-http-cache'

$out = Join-Path $repo "dist/v$Version/win-x64"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null

# 发布（保留项目 NuGet 审计；程序集版本经命令行注入，不改 csproj）
dotnet publish src/DeepSeekHarnessCleaner.UI -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:Version=$Version -p:AssemblyVersion=$Version -p:FileVersion=$Version `
    -o $out | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Output "PUBLISH_FAILED exit=$LASTEXITCODE"; exit 1 }

# 实际 EXE 名以 AssemblyName 为准（csproj 未覆写 → 项目名）
$exe = Join-Path $out 'DeepSeekHarnessCleaner.UI.exe'
if (-not (Test-Path $exe)) { Write-Output 'EXE_MISSING'; exit 1 }
# 调试符号不随发布分发
Get-ChildItem $out -Filter *.pdb | Remove-Item -Force

# LICENSE 与第三方声明
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $out 'LICENSE.txt')
@"
THIRD-PARTY NOTICES — DeepSeek Harness Cleaner v$Version
本发布包内嵌以下第三方组件：
- ZstdSharp.Port 0.8.8 (MIT) — Zstandard 解码，https://www.nuget.org/packages/ZstdSharp.Port
运行依赖（不随本包分发）：.NET 10 Windows Desktop Runtime x64 (MIT, .NET Foundation)，https://dotnet.microsoft.com
"@ | Set-Content (Join-Path $out 'THIRD-PARTY-NOTICES.txt') -Encoding utf8

# README 摘要（发布包内）
@"
DeepSeek Harness Cleaner v$Version（win-x64 单文件，不捆绑 .NET 运行时，未签名）
- 运行：客户端先安装 .NET 10 Windows Desktop Runtime x64，再双击 DeepSeekHarnessCleaner.UI.exe（Windows 10 x64）。
- 离线工具：无主动联网功能；本次构建未执行运行期联网验证。
- 安全默认：扫描只读；会话及附件删除须明确确认，原始 ZIP 备份可选；保留 SHA-256、路径与进程复验，Harness 未完全退出时阻断删除。
- 已适配本机 Harness 0.2.1-rc.2 的原生 v0/v4 元数据及整会话离线删除；未知版本/损坏日志禁止清理。预览展示已验证文本，不声称完整历史重放。
- 附件：完成扫描后从顶部“附件”进入，展示可核实名称/类型及关联会话；人工勾选后独立确认删除，备份可选；Unknown 不代表孤儿，会话删除不自动删除附件。
- 会话：归档状态单独展示，元数据无法核实则显示未知；解析状态明确显示正常或异常。
- 校验：SHA256SUMS.txt（Get-FileHash 对比）。许可：LICENSE.txt（MIT）。
"@ | Set-Content (Join-Path $out 'README.md') -Encoding utf8

# SHA256SUMS（不含自身）
$sums = Get-ChildItem $out -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name |
    ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)" }
$sums | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii

Write-Output "PUBLISH_OK version=$Version out=$out"
Write-Output ("EXE_MB=" + [math]::Round((Get-Item $exe).Length / 1MB, 1))
Write-Output ("EXE_SHA256=" + (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower())
Write-Output ("FILES=" + ((Get-ChildItem $out -File).Name -join ','))
exit 0

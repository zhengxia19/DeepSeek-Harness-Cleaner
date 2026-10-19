# publish.ps1 — 发布包构建：dist/v<版本>/win-x64/（framework-dependent 单文件 win-x64 + SHA256SUMS + 许可 + README 摘要）
# 用法：.\scripts\publish.ps1 [-Version 0.1.1]
# 依据 docs/release.md §1/§3：产物 = EXE + README 摘要 + LICENSE/THIRD-PARTY-NOTICES + SHA256SUMS.txt；
# 自 v0.2.0 起 framework-dependent（N-08/R9）：不捆绑 .NET 运行时，需预装 .NET 10 Windows Desktop Runtime（x64）。
# 不分发 NuGet 缓存、SDK、真实会话数据、配置或测试 fixtures。未签名，明确标注。
#Requires -Version 5
[CmdletBinding()]
param([string]$Version = '0.1.1')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
. (Join-Path $repo 'scripts/enter-env.ps1')
$env:TEMP = $env:TMP = Join-Path $repo '.artifacts/tmp'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $repo '.artifacts/nuget-http-cache'

$out = Join-Path $repo "dist/v$Version/win-x64"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null

# 发布（离线：NuGetAudit=false；程序集版本经命令行注入，不改 csproj；N-08：framework-dependent，不捆绑运行时）
dotnet publish src/DeepSeekHarnessCleaner.UI -c Release -r win-x64 -p:SelfContained=false `
    -p:PublishSingleFile=true -p:NuGetAudit=false -p:Version=$Version -p:AssemblyVersion=$Version -p:FileVersion=$Version `
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
本发布包为 framework-dependent（N-08）：不捆绑 .NET 运行时，需预装 .NET 10 Windows Desktop Runtime（x64）。
包内嵌以下第三方组件：
- ZstdSharp.Port 0.8.8 (MIT) — Zstandard 解码，https://www.nuget.org/packages/ZstdSharp.Port
预装运行时的第三方声明见 https://dotnet.microsoft.com/en-us/legal/dotnet/third-party-notices
"@ | Set-Content (Join-Path $out 'THIRD-PARTY-NOTICES.txt') -Encoding utf8

# README 摘要（发布包内）
@"
DeepSeek Harness Cleaner v$Version（win-x64 framework-dependent 单文件，未签名）
- 运行：双击 DeepSeekHarnessCleaner.UI.exe（Windows 10 x64，需预装 .NET 10 Windows Desktop Runtime（x64））。
- 离线工具：运行期不进行任何主动 TCP 联网（TC-18 已验证）。
- 安全默认：扫描只读；删除前必须明确确认，ZIP 备份可选（建议）、提供时逐项 SHA-256 校验；Harness 未退出时阻断删除。
- 已知限制：语义投影仅覆盖已确证的 DeepSeek Harness 0.2.x 事件（header 契约与消息/tool-result 事件，v0.1.1 实测确证）；未知事件仅按“类型/行号 + 不完整”摘要；v0–v3 旧代际仅提供元数据级预览（详见仓库 docs/release.md）。
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

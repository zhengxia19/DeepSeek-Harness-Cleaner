<#
.SYNOPSIS
    在项目 .dotnet 目录安装经核验的 .NET SDK（用户态，不修改系统 PATH / 注册表）。
.DESCRIPTION
    1) 从官方端点 https://dot.net/v1/dotnet-install.ps1 下载官方安装脚本到 scripts/，
       并校验其 SHA-256（首次下载时把哈希写入 scripts/dotnet-install.ps1.sha256 固定）。
    2) 若 global.json 已固定 SDK 版本，则安装该精确版本；否则安装频道内最新稳定版并写入 global.json。
    3) 只在当前进程设置 DOTNET_ROOT / PATH / NUGET_PACKAGES / DOTNET_CLI_HOME 等变量，绝不使用 setx。
.EXAMPLE
    .\scripts\bootstrap.ps1
    .\scripts\bootstrap.ps1 -Channel 10.0
#>
[CmdletBinding()]
param(
    [string]$Channel = '10.0',
    [string]$Architecture = 'x64',
    [switch]$ForceReinstall
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$installDir = Join-Path $root '.dotnet'
$installerPath = Join-Path $PSScriptRoot 'dotnet-install.ps1'
$installerHashPath = "$installerPath.sha256"
$installerUrl = 'https://dot.net/v1/dotnet-install.ps1'
$globalJsonPath = Join-Path $root 'global.json'

function Get-Sha256([string]$path) {
    (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# --- 1. 获取并核验官方安装脚本 ---
if (-not (Test-Path $installerPath)) {
    Write-Host "下载官方安装脚本 $installerUrl"
    Invoke-WebRequest -Uri $installerUrl -OutFile $installerPath -UseBasicParsing
    if (-not (Test-Path $installerHashPath)) {
        Get-Sha256 $installerPath | Set-Content -Path $installerHashPath -NoNewline -Encoding utf8
        Write-Host "已记录安装脚本 SHA-256 到 scripts/dotnet-install.ps1.sha256"
    }
}
if (Test-Path $installerHashPath) {
    $expected = (Get-Content -Path $installerHashPath -Raw).Trim().ToLowerInvariant()
    $actual = Get-Sha256 $installerPath
    if ($actual -ne $expected) {
        throw "官方安装脚本 SHA-256 校验失败：期望 $expected，实际 $actual（来自 $installerUrl）"
    }
    Write-Host "安装脚本 SHA-256 校验通过：$actual"
}

# --- 2. 决定要安装的 SDK 版本 ---
$pinnedVersion = $null
if (Test-Path $globalJsonPath) {
    $globalJson = Get-Content -Path $globalJsonPath -Raw | ConvertFrom-Json
    if ($globalJson.sdk -and $globalJson.sdk.version) {
        $pinnedVersion = [string]$globalJson.sdk.version
    }
}

$sdkDir = Join-Path $installDir 'sdk'
$installedVersions = @()
if (Test-Path $sdkDir) {
    $installedVersions = @(Get-ChildItem -Path $sdkDir -Directory | ForEach-Object { $_.Name })
}

if ($pinnedVersion -and ($installedVersions -contains $pinnedVersion) -and -not $ForceReinstall) {
    Write-Host "已存在 global.json 固定的 SDK $pinnedVersion，跳过下载。"
}
elseif (-not $pinnedVersion -and $installedVersions.Count -gt 0 -and -not $ForceReinstall) {
    Write-Host "global.json 尚未固定版本，且 .dotnet\sdk 已存在 $installedVersions，跳过下载。"
}
else {
    New-Item -ItemType Directory -Force $installDir | Out-Null
    if ($pinnedVersion) {
        Write-Host "安装固定版本 .NET SDK $pinnedVersion 到 $installDir"
        & $installerPath -Version $pinnedVersion -InstallDir $installDir -Architecture $Architecture -NoPath
    }
    else {
        Write-Host "安装频道 $Channel 的最新稳定 .NET SDK 到 $installDir"
        & $installerPath -Channel $Channel -InstallDir $installDir -Architecture $Architecture -NoPath
    }
}

# --- 3. 从安装结果提取实际 SDK 版本并固定到 global.json ---
if (-not (Test-Path $sdkDir)) { throw "安装后未找到 SDK 目录 $sdkDir" }
$versions = @(Get-ChildItem -Path $sdkDir -Directory | ForEach-Object { $_.Name } | Sort-Object { [version]($_ -replace '-.*$','') })
if ($versions.Count -eq 0) { throw "SDK 目录为空：$sdkDir" }
$selected = if ($pinnedVersion -and ($versions -contains $pinnedVersion)) { $pinnedVersion } else { $versions[-1] }

$globalJson = [ordered]@{
    sdk = [ordered]@{
        version    = $selected
        rollForward = 'disable'
    }
}
($globalJson | ConvertTo-Json -Depth 4) | Set-Content -Path $globalJsonPath -Encoding utf8 -NoNewline
Write-Host "global.json 固定 SDK 版本：$selected"

# --- 4. 当前进程环境（不写系统环境） ---
. (Join-Path $PSScriptRoot 'enter-env.ps1')

& (Join-Path $installDir 'dotnet.exe') --version | ForEach-Object { Write-Host "dotnet --version: $_" }
Write-Host "bootstrap 完成。DOTNET_ROOT=$env:DOTNET_ROOT"

<#
.SYNOPSIS
    为当前 PowerShell 进程配置项目本地 .NET SDK 环境（不修改系统环境）。
.DESCRIPTION
    只设置进程级环境变量：DOTNET_ROOT、PATH 前置、NUGET_PACKAGES、DOTNET_CLI_HOME、
    DOTNET_MULTILEVEL_LOOKUP=0、DOTNET_CLI_TELEMETRY_OPTOUT=1、DOTNET_NOLOGO=1。
    不调用 setx，不写注册表，不需要管理员权限。退出该 shell 后系统环境不变。
.EXAMPLE
    . .\scripts\enter-env.ps1
    dotnet --info
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$installDir = Join-Path $root '.dotnet'
$dotnetExe = Join-Path $installDir 'dotnet.exe'

if (-not (Test-Path -LiteralPath $dotnetExe)) {
    throw "未找到项目本地 SDK：$dotnetExe。请先运行 .\scripts\bootstrap.ps1"
}

$env:DOTNET_ROOT = $installDir
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:NUGET_PACKAGES = Join-Path $root '.nuget\packages'
$env:DOTNET_CLI_HOME = Join-Path $root '.artifacts\dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

New-Item -ItemType Directory -Force -Path $env:NUGET_PACKAGES | Out-Null
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null

# 仅当前进程 PATH 前置，确保优先使用项目本地 dotnet。
if (($env:PATH -split ';') -notcontains $installDir) {
    $env:PATH = "$installDir;$env:PATH"
}

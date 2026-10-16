# 本地开发环境（Development）

> 工具方案为 .NET 10 SDK + WinForms。Windows PowerShell 5.1 或 PowerShell 7 均可；操作路径有空格时全部使用引号。**项目 SDK 安装及依赖缓存必须隔离**。

## 1. 目录与配置（实施 T-01/T-02 创建）

```text
project/
  .dotnet/                  # 本项目 SDK，不提交
  .nuget/packages/          # 本项目 NuGet 缓存，不提交
  .artifacts/               # obj/bin/发布中间结果，不提交
  scripts/bootstrap.ps1
  scripts/enter-env.ps1
  scripts/build.ps1
  scripts/test.ps1
  scripts/publish.ps1
  global.json               # SDK 实际安装版本，精确固定
  Directory.Packages.props  # 三方包版本唯一来源
```

> **SDK 具体补丁版本**：在首次执行 T-02 时从微软已发布的稳定 .NET 10 SDK 选择一版，记录在 `global.json`，并提交脚本运行结果。当前文档不伪造尚未装机核实的版本号。

## 2. 环境初始化的规范化 PowerShell 流程

```powershell
# 在项目根目录执行；以下展示 bootstrap.ps1 应实现的动作，真实命令以脚本为准
$root = (Resolve-Path '.').Path
$install = Join-Path $root '.dotnet'
New-Item -ItemType Directory -Force $install | Out-Null
# 官方脚本先保存到项目 scripts/，校验来自 dot.net 官方端点
# powershell -NoProfile -File .\scripts\dotnet-install.ps1 -Channel 10.0 -InstallDir $install -Architecture x64
# bootstrap 完成后，从安装结果中提取具体 SDK 版本并写入 global.json
$env:DOTNET_ROOT = $install
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:DOTNET_CLI_HOME = (Join-Path $root '.artifacts\dotnet-home')
$env:NUGET_PACKAGES = (Join-Path $root '.nuget\packages')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:PATH = "$install;$env:PATH"  # 仅当前进程，不使用 setx
& (Join-Path $install 'dotnet.exe') --info
```

**隔离要求**：`enter-env.ps1` 只设置当前 PowerShell 进程环境，所有 build/test/publish 均显式调用 `.dotnet/dotnet.exe`；不修改机器 PATH、用户配置文件或注册表。`.dotnet/`、`.nuget/`、`.artifacts/` 全部加入 `.gitignore`。

## 3. 项目创建与命令（T-03 后有效）

```powershell
.\scripts\bootstrap.ps1
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\publish.ps1
```

脚本最终调用等效于：

```powershell
.\.dotnet\dotnet.exe restore .\DeepSeekHarnessCleaner.sln --locked-mode
.\.dotnet\dotnet.exe build .\DeepSeekHarnessCleaner.sln -c Release --no-restore -warnaserror
.\.dotnet\dotnet.exe test .\DeepSeekHarnessCleaner.sln -c Release --no-build
.\.dotnet\dotnet.exe publish .\src\DeepSeekHarnessCleaner.UI\DeepSeekHarnessCleaner.UI.csproj `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

NuGet 锁文件应由 `RestorePackagesWithLockFile=true` 生成并纳入版本控制；`--locked-mode` 只在已生成锁文件后启用，初次创建锁文件时使用正常 `restore`。`Directory.Build.props` 应避免 WinForms 项目输出路径与测试项目冲突。

## 4. 推荐项目拆分

- `src/DeepSeekHarnessCleaner.Domain`：实体与规则，`net10.0`。
- `src/DeepSeekHarnessCleaner.Application`：业务服务接口/应用编排，`net10.0`。
- `src/DeepSeekHarnessCleaner.Infrastructure`：文件系统、Zstd、备份、配置，`net10.0`。
- `src/DeepSeekHarnessCleaner.UI`：WinForms，`net10.0-windows` 且 `<UseWindowsForms>true</UseWindowsForms>`。
- `tests/DeepSeekHarnessCleaner.Tests`：xUnit，临时目录模拟；Windows 专项测试另外划分类别。

## 5. 数据与调试边界

- 所有默认测试指向 `tests/fixtures` 或临时目录，不指向 `%USERPROFILE%\.dsh`。
- 调试真实数据只允许只读扫描与预览；删除功能通过 UI 测试 fixture 驱动。
- 若程序出现格式或权限异常，记录错误码、相对位置与 stack trace；禁止把完整聊天正文写到日志。
- 确认 Windows Defender / Controlled Folder Access 阻断时，只显示诊断，不尝试关闭防护。

## 6. 环境复现步骤

新 Windows 10 x64 环境：克隆项目 → 运行 bootstrap（用户态安装）→ 检查 SDK / NuGet 锁文件 → 测试 → 构建 → 在 VM 中做 WinForms Smoke 测试。若 Windows 版本不是微软所列支持的 Windows 10 LTSC/Enterprise，应在发布报告标为**实测兼容但不属官方支持**，不可直接宣称保证支持。

参考：<https://learn.microsoft.com/en-us/dotnet/core/install/windows>、<https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script>。

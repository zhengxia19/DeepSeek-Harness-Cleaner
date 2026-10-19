# 发布与回滚（Release）

## 1. 发布版本和产物

- 当前发布：**`0.1.1`**（真实格式修正维护版，见 §7）；首发 `0.1.0`，方案为 `win-x64` 自包含 WinForms EXE；单文件优先，但若 ZstdSharp 与 single-file 组合造成真实问题，保留自包含目录发布并追加 ADR，不能偷偷改路线。
- 发布目录 `dist/v<版本>/win-x64/`，包含程序 EXE、README 摘要、LICENSE/THIRD-PARTY-NOTICES（如适用）、`SHA256SUMS.txt`。
- 不分发 NuGet 缓存、SDK、真实会话数据、配置或测试 fixtures。

## 2. 放行检查

- [x] 任务 T-01～T-34 均验收并附证据（`artifacts/evidence/T-01.txt`～`T-34.txt`）；TC-01～TC-20、PERF-01、SEC-01～03、COMPAT-01、WIN10-01 见 `artifacts/evidence/T-32.txt`、`T-33*.txt`、`T-34.txt`。
- [x] 源码/依赖审计：无任何联网 API/遥测（TC-18 运行期 0 条 TCP 连接）；依赖仅 ZstdSharp.Port 0.8.8（MIT，已核准）+ .NET 10 运行时；`dotnet package list --vulnerable --include-transitive` 两工程均“没有易受攻击的包”（见 `artifacts/evidence/T-34.txt`）。
- [x] 目标 OS：Windows 10 Pro 22H2 (19045.6466) x64 VMware 隔离虚拟机（非物理裸机，亦非全新预置干净 VM）；.NET 10（SDK 10.0.402，项目本地）官方支持期内；实测结果见 `artifacts/evidence/T-33-win10.txt`、`T-34.txt`。
- [x] 冷启动/退出/重复扫描、fixture 备份/删除/失败回退：内置 Smoke 72 断言全过（含备份→校验→删除→回退路径），见 `artifacts/evidence/T-34.txt`。
- [x] 安全路径测试：UNC、软链/junction、未退出进程、文件变化均拒绝写入（TC-13～TC-16，见 `artifacts/evidence/T-32.txt`）。
- [x] CHANGELOG 已更新为真实 Added/Changed/Fixed 与已知限制，不含未实现功能。

## 3. 构建与校验

在项目本地环境执行（`test.ps1` 未创建，实际命令如下）：

```powershell
. .\scripts\enter-env.ps1
$env:TEMP = $env:TMP = "$PWD\.artifacts\tmp"
dotnet build .\DeepSeekHarnessCleaner.sln -c Release --no-restore -warnaserror -m:1
dotnet test .\DeepSeekHarnessCleaner.sln -c Release --no-build
dotnet format .\DeepSeekHarnessCleaner.sln --verify-no-changes --no-restore
.\scripts\publish.ps1 -Version 0.1.1
Get-FileHash .\dist\v0.1.1\win-x64\DeepSeekHarnessCleaner.UI.exe -Algorithm SHA256
```

发布产物实际文件名为 `DeepSeekHarnessCleaner.UI.exe`（以 `*.csproj` 的 AssemblyName 为准）。本版本**未签名**（无代码签名证书），禁止暗示已签名。注意：`publish.ps1` 以 `-p:Version=<版本>` 注入版本，会使 `packages.lock.json` 项目引用版本改写为 `[<版本>, )`，属副作用，发布后应 `git checkout -- src/*/packages.lock.json` 复原，不提交。

## 7. 0.1.1 发布记录（真实格式修正，M 系列维护）

- 版本 0.1.1，日期 2026-10-19；产物 `dist/v0.1.1/win-x64/DeepSeekHarnessCleaner.UI.exe`（102.8 MB，SHA-256 `0b5f2e0fa674e7f606e0c35bf92c7a74d4306fe874a04f649b19058439eb9046`，另见 `dist/v0.1.1/win-x64/SHA256SUMS.txt`）。
- 变更范围：M-02 会话 header 契约对齐真实 0.2.x（`type:"session"`/`id`/`version` 0–4/`createdAt` epoch-millis）；M-03 事件信封与角色/时间映射对齐（消息正文在 `data` 或 `data.message`，仅提取 `text` 块，epoch-millis 时间，47 类实测词表）。修复前真实根 46/46 文件 `HEADER_MISSING` 全部不可清理；修复后全部 Valid。
- 门禁：build 0 警告（`-warnaserror`）；测试 282/282；`dotnet format --verify-no-changes` 干净；`dotnet list package --vulnerable --include-transitive` 两工程均“没有易受攻击的包”。
- 冒烟：`scripts/smoke-win10.ps1` 全 9 项 PASS（自包含发布、Smoke 72 PASS/0 FAIL 含删除测试仅合成根、TC-18 运行期 0 条 TCP 连接）；发布包 EXE 本身另跑一次 72 PASS/0 FAIL。证据 `artifacts/evidence/M-04-win10.txt`、`M-04.txt`。
- 真实根只读核验（COMPAT-01，前后快照证明零写入）：51 会话全部 `Valid`（Corrupt=0、Ambiguous=0、异常文件=0），51/51 带 `createdAt`（日期可筛选），快照 changed=0/deleted=0/added=0。任务书预期“45 会话”为诊断时点计数，真实根为活动根（51 为核验时点），如实记录。
- 验证环境同 §6：Windows 10 Pro 22H2 (19045.6466) x64 VMware 隔离 VM；**freshly-provisioned 干净 VM 仍未验证**，不宣称兼容通过。
- 逐项证据：`artifacts/evidence/M-01.txt`～`M-04.txt`、`M-04-win10.txt`；诊断 `DIAG-event-format.txt`、`DIAG-scan-coverage.txt`。

## 4. 验证及回滚

- 安装：解压/复制独立 EXE 到用户任意非系统目录；无管理员权限，首次运行不修改 Harness 数据。
- 更新：退出旧工具后替换 EXE；旧 `settings.json` 可继续使用或安全降级；不要自动迁移 Harness 原始会话日志。
- 回滚：关闭工具，保留上一个 EXE，覆盖新 EXE 后启动；只对工具自身设置采用版本化迁移/备份。
- **删除后的会话不随程序回滚而恢复**。需要恢复时参考原始 ZIP manifest 手工还原；本版本没有自动恢复能力，也不承诺能直接通过官方导入恢复。
- 发生根目录越界误删、无备份删除、ZIP 校验失效、Win10 启动失败时必须阻断发布。

## 5. 发布记录格式

版本、日期、commit、SDK、目标 OS Build、自动测试统计、手工检查、包 SHA-256、依赖风险扫描摘要、剩余限制、发布审批人。

## 6. 0.1.0 发布记录与验证环境说明

完整发布记录（含逐项命令、退出码、SHA-256、扫描结果）见 `artifacts/evidence/T-34.txt`。摘要：

- 版本 0.1.0，日期 2026-10-18/19；产物 `dist/v0.1.0/win-x64/DeepSeekHarnessCleaner.UI.exe`（102.8 MB，SHA-256 `c5464a2df7fc7b04d99520c790b0706dffcd51abc670089adf68a7cf91cf6eea`）；`dist/` 被 `.gitignore` 排除，哈希以 `SHA256SUMS.txt` 与证据文件记录。
- 验证环境：Windows 10 Pro 22H2 (19045.6466) x64，VMware 隔离 VM（目标系统环境验证，非物理裸机）。Windows Sandbox 不可用，未做“全新预置（freshly-provisioned）干净 VM”验证；以等效证据替代：自包含单文件 EXE 复制到任意非仓库目录、`DOTNET_ROOT` 指向不存在目录仍 72/72 通过、运行期 0 条 TCP 连接、真实 `%USERPROFILE%\.dsh\sessions` 前后快照 0 改动。残余风险（全新 VM 首启）已列入 CHANGELOG 已知限制。
- 删除操作仅对测试 fixtures 执行；真实会话数据全程只读。

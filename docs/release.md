# 发布与回滚（Release）

> 当前发布默认（2026-10-20 用户指定）：`win-x64`、Release、单文件，`--self-contained false -p:PublishSingleFile=true`，不捆绑 .NET 运行时。客户端须自行安装 .NET 10 Windows Desktop Runtime x64。下述 0.1.0 自包含包及其验收属于历史记录。

## 当前修复包 0.2.4（2026-10-20）

- `dist/v0.2.4/win-x64/DeepSeekHarnessCleaner.UI.exe`，未签名，970,763 字节；SHA-256 `7c5c371b02a14e226abfb5822aaff2fe3e2938d9f84c3d91eb7aa025236ef3e4`。
- 结果报告布局/居中/复制/滚动、附件右键默认应用打开、主窗记忆、选择表头全选及四表标题样式已实施。
- Release -warnaserror 零警告/错误；350 自动测试、91 Windows 检查、实际默认应用打开 5 项、发布 EXE 完整冒烟 72 项、单文件 EXE 跨工作目录两次启动各 30 项及同目录窗口记录、包内 4 项哈希校验通过。证据 `artifacts/evidence/2026-10-20/T-38.txt`。
- 普通 smoke 默认不保存窗口记录；仅测试时通过 `--window-state-file` 注入夹具路径，或在独立 EXE 副本上显式 `--persist-window-state` 验证默认同目录规则。正式发布目录未混入验收生成的窗口记录。
- 150% 实际 DPI 按用户指示交由用户验收；不以字体模拟代替。未验收干净机器安装或完整真实 DPI 矩阵。旧版与本轮临时包均保留在既有目录/忽略的回退目录；程序回退不恢复未备份删除的数据。

## 历史修复包 0.2.3（2026-10-20）

- `dist/v0.2.3/win-x64/DeepSeekHarnessCleaner.UI.exe`，未签名，945,163 字节；SHA-256 `8a9db0a9553a0b3d59e5d48239b1373b6bb1e7bf259ad4bf80144c0b6f23c545`。
- 会话和附件均可不备份直接确认删除；选择备份时仍须校验。修复删除/附件窗口父窗体居中；附件名称、类型、关联会话与会话归档、解析状态明确显示；无法核实的信息显示未知。
- Release 编译零警告/错误；324 自动测试、38 Windows 窗口专项、72 发布 EXE 合成夹具冒烟及包内 4 项哈希校验通过。证据 `artifacts/evidence/2026-10-20/T-31.txt`。
- 未对真实 Harness 数据执行删除；完整真实 DPI 矩阵、干净机器安装及运行中删除不在本轮验收范围。旧包保留；回退程序不能恢复未备份删除的文件。

## 历史修复包 0.2.2（2026-10-20）

- `dist/v0.2.2/win-x64/DeepSeekHarnessCleaner.UI.exe`，未签名，937,995 字节；SHA-256 `1edd6aa76a1ccbd10840ea5b91d30e785b911716fcab0e450897a10cfb31e7af`。
- 修复原生 v0/v4 会话删除计划、预览正常事件分类与附件页入口；会话及附件强制 ZIP 备份，仅 Harness 完全退出允许执行删除。未知版本继续阻断。
- 自动测试 305 项、本机 Windows 实际窗口专项 15 项、发布 EXE 的合成夹具冒烟 72 项通过；证据 `artifacts/evidence/2026-10-20/T-30.txt`。不代表干净机器安装、完整真实 DPI 矩阵或运行中删除通过。
- 旧 0.2.0/0.2.1 发布目录保留，回退程序可选择旧包；回退程序不等于恢复已删除文件，恢复依据仍是操作前的原始 ZIP。

## 1. 发布版本和产物

- 首次发布候选：`0.1.0`，方案为 `win-x64` 自包含 WinForms EXE；单文件优先，但若 ZstdSharp 与 single-file 组合造成真实问题，保留自包含目录发布并追加 ADR，不能偷偷改路线。
- 发布目录 `dist/v0.1.0/win-x64/`，包含程序 EXE、README 摘要、LICENSE/THIRD-PARTY-NOTICES（如适用）、`SHA256SUMS.txt`。
- 不分发 NuGet 缓存、SDK、真实会话数据、配置或测试 fixtures。

## 2. 放行检查

- [x] 任务 T-01～T-34 均验收并附证据（`artifacts/evidence/T-01.txt`～`T-34.txt`）；TC-01～TC-20、PERF-01、SEC-01～03、COMPAT-01、WIN10-01 见 `artifacts/evidence/T-32.txt`、`T-33*.txt`、`T-34.txt`。
- [x] 源码/依赖审计：无任何联网 API/遥测（TC-18 运行期 0 条 TCP 连接）；依赖仅 ZstdSharp.Port 0.8.8（MIT，已核准）+ .NET 10 运行时；`dotnet package list --vulnerable --include-transitive` 两工程均“没有易受攻击的包”（见 `artifacts/evidence/T-34.txt`）。
- [x] 目标 OS：Windows 10 Pro 22H2 (19045.6466) x64 实机（VMware 隔离 VM）；.NET 10（SDK 10.0.402，项目本地）官方支持期内；实测结果见 `artifacts/evidence/T-33-win10.txt`、`T-34.txt`。
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
.\scripts\publish.ps1
Get-FileHash .\dist\v0.1.0\win-x64\DeepSeekHarnessCleaner.UI.exe -Algorithm SHA256
```

发布产物实际文件名为 `DeepSeekHarnessCleaner.UI.exe`（以 `*.csproj` 的 AssemblyName 为准）。本版本**未签名**（无代码签名证书），禁止暗示已签名。注意：`publish.ps1` 以 `-p:Version=0.1.0` 注入版本，会使 `packages.lock.json` 项目引用版本改写为 `[0.1.0, )`，属副作用，发布后应 `git checkout -- src/*/packages.lock.json` 复原，不提交。

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
- 验证环境：Windows 10 Pro 22H2 (19045.6466) x64，VMware 隔离 VM（实机验证）。Windows Sandbox 不可用，未做“全新预置（freshly-provisioned）干净 VM”验证；以等效证据替代：自包含单文件 EXE 复制到任意非仓库目录、`DOTNET_ROOT` 指向不存在目录仍 72/72 通过、运行期 0 条 TCP 连接、真实 `%USERPROFILE%\.dsh\sessions` 前后快照 0 改动。残余风险（全新 VM 首启）已列入 CHANGELOG 已知限制。
- 删除操作仅对测试 fixtures 执行；真实会话数据全程只读。

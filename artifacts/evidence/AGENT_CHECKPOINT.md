# AGENT_CHECKPOINT — DeepSeek Harness Cleaner

更新时间：2026-10-18（Round 2 结束）
Git：HEAD 应为 `docs(T-32)` 提交，其前为 `feat(T-32)` 无代码改动（T-32 仅证据归档）→ 实际为单个 `docs(T-32)` 提交；基线链：`a758cbe docs(T-31)` ← `fc7e9d1 feat(T-31)` ← `8d920fd docs(T-30)`。
工作区常驻未提交项（永不提交）：`M AGENTS.md`、`?? docs/agent-execution.md`。

## 状态
- 已验收：T-01..T-32 全部 `[x]`（证据 artifacts/evidence/T-01..T-32.txt）。
- 本轮完成：T-32 完整安全与功能回归。267/267 单测 PASS（trx 归档 T-32-results.trx）、UI 冒烟 72/72 PASS（T-32-smoke.txt）、TC-01..20 与 SEC-01..03 全部映射到自动化/冒烟证据并 PASS、REQ→TC→任务映射核对无缺口。
- 整改项 R-01（不阻塞 T-33）：`dotnet format --verify-no-changes --no-restore` exit 2，仅 BackupService.cs 60 处 WHITESPACE（T-22 引入）。归 T-34 发布放行前清零；修复：`dotnet format src\DeepSeekHarnessCleaner.Infrastructure\DeepSeekHarnessCleaner.Infrastructure.csproj --no-restore` 后重跑 build+test。

## 下一个任务：T-33 — 性能与 Win10 兼容性验证（前置 T-32 ✓）
- 修改范围：`scripts/perf.ps1、scripts/smoke-win10.ps1、artifacts/evidence/T-33-*`。
- 内容：PERF-01（1000 小会话 ≤200MB fixture 初扫 ≤15s、筛选 ≤200ms、工作集 <512MiB，记录 CPU/SSD/Build）；WIN10-01（记录 Edition/Build，自包含单文件 win-x64 启动/退出/中文 UI 实测——本机即 Win10 19045，沙箱注册表读不到 Edition，用 winver 或注册表实测记录）；TC-18 运行时联网检查（EXE 运行期间零新增主动请求；可用 Get-Process 网络统计或 Fiddler-like 计数，无工具时记录可执行证据）；COMPAT-01 脱敏 0.2.0.0 只读样本（若无样本按诚实标注处理）。
- 验收：PERF-01/COMPAT-01/WIN10-01 有计时和系统信息；不达标则停工修正重跑回归。
- 发布单文件自包含：`dotnet publish src\DeepSeekHarnessCleaner.UI -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`（输出放项目内，勿建 dist 直到 T-34 范围）。

## 关键复用信息
- 环境：`. .\scripts\enter-env.ps1`（项目本地 .dotnet 10.0.402）；`$env:TEMP=$env:TMP="$PWD\.artifacts\tmp"`。
- 构建/测试：`dotnet build .\DeepSeekHarnessCleaner.sln -c Release --no-restore -warnaserror -m:1`；`dotnet test … -c Release --no-build`（当前 267 用例）。
- 冒烟：exe `--smoke <out> --scan-root .artifacts\tmp\dsh-t30-fixture\sessions --log-dir .artifacts\tmp\dshcleaner-logs`；先重建 fixture：删 `dsh-t30-fixture\sessions` 再从 `dsh-t18-fixture`（307 文件）复制。
- 冒烟断言现共 72 项（绑定/预览/操作/结果对话框/日志脱敏）。
- 坑：PowerShell 无 grep（用 Select-String）；Get-CimInstance 可能被拒（用 Get-Process）；reg query HKLM 在沙箱返回空；xunit 需 `using Xunit;`；record 命名实参用属性名；`scripts/build.ps1/test.ps1/publish.ps1` 不存在（T-33 起可创建 perf.ps1/smoke-win10.ps1）；dotnet format 需 `--no-restore`（离线 NuGet）。
- 提交规范：`feat(T-XX)`（代码+测试）+ `docs(T-XX)`（证据+检查点+勾选）；排除 AGENTS.md 与 docs/agent-execution.md。

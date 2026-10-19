# Agent 开发与维护规程（v0.1.0 后）

**状态**：v0.1.0 首次交付已完成，T-01～T-34 为历史任务，不再作为待领取队列。  
**模型约束**：为 Qwen3.8-Flash-Next IQ3_S（64K 上下文）设计；其他 Agent 亦可依此操作。  
**职责**：本文件描述执行方法；实际需求以用户当轮指令及项目正式契约为准。

## 1. 唯一事实来源

| 类型 | 权威位置 |
|---|---|
| 当前产品能力及限制 | `README.md`、`CHANGELOG.md`、`docs/release.md`，最终以代码/测试为准 |
| 需求和质量目标 | `docs/requirements.md` |
| 架构、设计与格式契约 | `docs/architecture.md`、`docs/design.md`、`docs/interfaces.md`、`docs/decisions/` |
| 验证方法和测试对应关系 | `docs/testing.md`、`tests/`、`scripts/` |
| 已完成 T-01～T-34 历史定义/状态 | `docs/implementation-tasks.md` |
| 历史验收与发布实际结果 | `artifacts/evidence/T-XX.txt`、`T-32-*`、`T-33-*`、`T-34.txt` |
| 最近一次 Agent 恢复摘要 | `artifacts/evidence/AGENT_CHECKPOINT.md`（非权威） |

实际代码和测试与文档冲突时，先记录可复现差异，不能以修改文档来掩盖缺陷。修改已接受的需求/安全契约须先明确变更依据。

## 2. 开始一轮工作

1. 查看 `git status --short`、`git branch --show-current`、`git log -1 --oneline`；区分已提交历史成果、未知未提交修改与本轮改动。
2. 从用户指定的修复/需求/审查范围开始。**T-01～T-34 已关闭，禁止自动重开、重新勾选或推断下一项 T-35**。
3. 定向读取相关源码、测试和规范；勿整批读取全部 `docs/`、`src/`、`tests/`、历史长日志。64K 上下文建议主要工作控制在约 40K tokens 内，并留出验收空间。
4. 定义当前变更的最小边界、影响的 REQ/TC 和可观察验收条件。若有多个真正独立的新工作项，逐项完成和验证；避免重复规划。
5. 遇到未知修改、版本不明、真实 Harness 路径或删除门禁冲突时先停止危险操作并记录阻断原因。

## 3. 实施规则

- 保留项目 C#/.NET 10/WinForms 四层职责：Domain 领域模型、Application 业务编排、Infrastructure 文件与进程边界、UI 展示与交互。无明确证据不增加框架、服务、云 API 或数据库。
- 只处理本轮范围；新增功能不能通过放松路径检查、格式识别、进程检测或备份校验实现。
- 自动化测试只在 `tests/.../Fixtures` 或临时合成根执行会修改/删除文件的动作；开发期间真实 .dsh 只读。
- 0.2.1-rc.2 实测日志的 Zstd 物理解码成功，**但头部/事件 schema 与现有已确认模式不匹配**。若开发兼容性支持，须先获得脱敏只读样本证据，分别验证发现、头部、语义、备份、删除门禁，再变更支持声明。
- 若涉及旧版本安全缺口，新增负向测试确认未知/损坏数据不会进入删除计划。
- 变更需求和数据契约只更新相关规范；历史 T-XX 证据是审计轨迹，不做覆盖式“修好历史”。

## 4. 验证与发布

在 Windows PowerShell 的仓库根目录按需要运行：

```powershell
. .\scripts\enter-env.ps1
dotnet build .\DeepSeekHarnessCleaner.sln -c Release --no-restore -warnaserror -m:1
dotnet test .\DeepSeekHarnessCleaner.sln -c Release --no-build
dotnet format .\DeepSeekHarnessCleaner.sln --verify-no-changes --no-restore
```

以上是推荐验证命令，**不能假定在本轮已执行**。如环境缺少项目本地 SDK，先按 `docs/development.md` 核实初始化步骤。涉及清理/安全/发布的变更，还须按 `docs/testing.md`、`docs/release.md` 运行适用的 Windows Smoke、SEC、COMPAT、PERF 检查。

发布脚本为 `scripts/publish.ps1`；`dist/` 是本地忽略目录，历史发布哈希在 `artifacts/evidence/T-34.txt`。发布脚本曾在历史验证中导致 lock 文件出现版本范围差异，执行后须检查 `git diff`，不可无条件丢弃用户改动。未在全新 Windows 10 VM 验证的事实不得写成“已通过”。

## 5. 结果与记录

新修复或新功能建议使用对应 Git issue/PR、commit 或简短新增验证记录关联，不在已完成的 T-01～T-34 中虚构历史任务。小型文档维护无须额外新建 34 步式管理清单。

结果必须记录：范围、修改文件、测试命令、退出码/断言统计（有则写）、未执行项、风险和提交号。检查点可按需更新为本次恢复摘要，但不得抹去历史 T-34 原始证据。

**状态用语**：`PASS`（实际执行成功）、`FAIL`（执行失败）、`BLOCKED`（受环境/证据限制）、`NOT_RUN`（未执行）。严禁将设计审查、此前成功记录或静态阅读冒充本次通过。

## 6. 一轮交付模板

```text
Scope: 本轮用户指令/Issue/PR
Changes: <修改文件和行为>
Validation: <命令与 PASS/FAIL/BLOCKED/NOT_RUN>
Security regression: <适用检查及结果>
Known gaps: <限制和待验证项>
Git: <真实分支、commit 或 PR；无则 NOT_COMMITTED>
```

如果当前用户请求只有文档修订，则不自动启动功能开发、重新执行 T-01～T-34，也不编造现场 Windows 测试结果。

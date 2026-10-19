# 开发 Agent 执行规程（64K 上下文版）

**项目**：DeepSeek Harness Cleaner  
**目标模型**：Qwen3.8-Flash-Next IQ3_S，64K tokens  
**适用范围**：项目现有 T-01～T-34 原子实施任务  
**定位**：本文件定义“如何执行”；`docs/implementation-tasks.md` 定义“做什么、验收什么”。不得在此维护任务完成状态。

> 每轮应先遵守根目录 `AGENTS.md`，再**按需**阅读本文件与当前任务相关的章节。文件中的命令和路径是操作约定；是否存在、能否运行，以工作区实测为准。此前的设计复审不是代码验收。

## 1. 文件职责与冲突处理

| 文件 | 单一职责 | 读取时机 |
|---|---|---|
| `AGENTS.md` | 短规则：安全、单任务、上下文纪律、验收门禁 | 每轮必读；若 Agent 已自动加载则不用重复读取 |
| `docs/agent-execution.md` | 单轮步骤、异常分支、记录格式与续跑机制 | 每轮按需查相关章节 |
| `docs/implementation-tasks.md` | T-01～T-34 原子任务、依赖、修改范围、验收及 `[ ]/[x]` | 精确定位当前任务与依赖，不通读全部 |
| `docs/requirements.md`、`architecture.md`、`design.md`、`interfaces.md` | 功能/架构/算法/接口契约 | 当前任务涉及相关契约时 |
| `docs/development.md`、`dependencies.md`、`coding-standard.md` | 环境、依赖、编码约束 | 环境和编码任务按需 |
| `docs/testing.md`、`release.md` | 测试用例、发布门槛 | 读取本任务关联 TC/SEC/PERF/WIN10 条目 |
| `docs/decisions/*.md`、`review-report.md` | 已接受的设计决策及未解决验证风险 | 技术冲突、PoC 或发布复审时 |
| `artifacts/evidence/T-XX.txt` | **本任务真实验收证据** | 复核依赖、验收、恢复时 |
| `artifacts/evidence/AGENT_CHECKPOINT.md` | 最近一轮恢复摘要；不是任务状态源 | 每轮开始及结束 |

判定次序：用户已确认的需求与安全边界 → 对应的正式需求/设计/ADR → 当前任务边界与验收 → 实际代码及测试证据。若源文件间出现影响正确性或安全性的实质冲突，记录文件与具体条目，**阻断并报告**，不要私自放宽验收、改写需求或另建第二套规格。

## 2. 64K 上下文预算与读取方式

### 2.1 一轮的工作量

- **一个 Agent 上下文最多领取一个 T-XX**。允许在该任务内反复调试、复测；不能在本轮紧接着领取下一个任务。
- 推荐工作上下文约 **≤40K tokens**，尽量剩余 **12K～16K** 供编译故障、测试与收尾；这是保守建议，不是工具可精确保证的硬限额。
- 优先读取短段、明确文件和命中的符号；不要将 `docs/`、`src/`、`tests/`、完整 Git diff 或长编译日志整批塞入模型。
- 使用命令只查看目录、相关符号和附近代码（例如 PowerShell `Get-ChildItem`、`Select-String`、`Get-Content | Select-Object -Skip ... -First ...`）。工具输出太长时写入项目本地日志文件，仅回读错误摘要及必要上下文。
- 阅读不到足够证据时，不应基于片段猜测完整实现。先定向补读；仍不可验证则按 `BLOCKED` 处理。

### 2.2 按任务阶段的阅读建议（不是强制全读）

| 当前任务 | 优先阅读的专项材料 |
|---|---|
| T-01～T-03 | `development.md`、`dependencies.md`、`coding-standard.md` |
| T-04～T-16 | `design.md`、`interfaces.md`、本任务对应测试条目 |
| T-17～T-20 | `architecture.md`、`design.md`、相关 UI/测试条目 |
| T-21～T-31 | 备份、清理和 UI 相关设计及接口章节、对应测试 |
| T-32～T-34 | `testing.md`、`release.md`、`requirements.md`、实际测试证据 |

**本轮必读不是“全部文档”**：只需当前任务全文、依赖验收状态、相关实现、相关 TC 与必要设计章节。检查点不应重复拷贝这些内容。

## 3. 启动与任务定位（每轮必做）

1. 确认当前工作目录为仓库根目录；运行 `git status --short`，列出未知改动。不能覆盖来源不明的未提交文件。
2. 若存在 `artifacts/evidence/AGENT_CHECKPOINT.md`，读取其摘要；不存在属于首次启动，不得因此报错。
3. 精确查询 `docs/implementation-tasks.md` 的任务勾选和前置条目；**不要仅凭检查点的“下一任务”字段**选择工作。
4. 若检查点指向未验收的在途任务，先核对其变更和证据、继续它；否则选择**编号最小、尚未完成且全部前置任务已验收**的 T-XX。前置任务同时须满足 `[x]` 与真实证据文件可核验。
5. 读取当前任务的完整定义（目标、前置、修改范围、边界、验收、TC），然后定向读取相关代码和专项文档。
6. 如果已有相同功能和可信验收结果，先核对它们；不要重复开发或重复执行未受影响的 PoC。若任务被错误勾选但缺乏证据，必须重新验证，不能直接视为完成。
7. 预先记录本轮任务编号与当前状态；**不要预先打勾**。

### 3.1 PowerShell 定位示例

```powershell
# 在项目根目录执行；仅用于定位，不是项目必备脚本
git status --short
Select-String -Path .\docs\implementation-tasks.md -Pattern '^### T-','\*\*T-.*：' 
Select-String -Path .\docs\implementation-tasks.md -Pattern '^### T-13','^### T-14' -Context 0,18
Get-ChildItem .\artifacts\evidence -ErrorAction SilentlyContinue
```

**注意**：以上 `T-13` 只是查询示例，不代表每轮必须选择 T-13。不同版本任务段落长度不固定，若未读到完整任务要补读；不要只看 `-Context 0,18` 就认为定义已结束。

## 4. 单任务开发闭环

### 4.1 执行前确认

记录以下信息：`Task ID`、目标、前置任务及各自证据、允许修改范围、明确禁止事项、验收条目及相关测试。核查工作树已有变更是否与本任务相关；不相关改动保持原样。

**修改范围例外仅限执行管理记录**：允许写本任务 `artifacts/evidence/T-XX.txt`、覆盖 `AGENT_CHECKPOINT.md`、更新任务清单当前 T-XX 的勾选及完成日期/证据信息。代码和其他正式文档仍受任务的修改范围限制。必要的文档同步应按任务许可和项目文档规范办理；超范围的实质实现/架构变化应停止并报告。

### 4.2 实施

1. 检查现有类、接口、测试和依赖，优先复用。
2. 只按任务边界修改；复杂任务可内部拆成最多约 5 个短步骤，但不要改造任务清单或拆出新一套任务。
3. 先实现当前任务的最小正确方案；避免无关重构、提前开发后续模块、额外 UI 框架或数据库服务。
4. 保持 `.dotnet/`、`.nuget/packages/`、`.artifacts/` 项目隔离：仅当前 PowerShell 进程配置环境变量，不使用 `setx`，不修改系统 PATH/注册表。
5. 未由前置任务创建的脚本不可假设存在。项目约定的脚本包括 `scripts/bootstrap.ps1`、`enter-env.ps1`、`build.ps1`、`test.ps1`、`publish.ps1`，应在对应任务实现后使用。
6. 功能尚不支持时明确返回“不支持/不完整/阻断”，不伪造会话消息、解析成功或兼容结论。

### 4.3 真实验证

根据本任务的**每一条验收标准**逐项执行：

- 运行相关构建/单元/集成测试，逐条记录命令、退出码和结果；要求负向用例时必须真实执行。
- 必要时复测受影响模块和已完成任务的关键回归项；不必每个小任务重复运行全量测试。
- 删改型功能必须使用合成 fixture，验证**未选文件和受保护根外哨兵的 SHA-256 不变**；不能用“命令成功退出”代替验证。
- 交互/UI 或 Windows 10 实机标准要求真实环境时必须在有该环境时执行；**未运行不能记 PASS**。
- 构建报错时保留完整输出到本地测试日志，在模型上下文只读取错误码、首个根因以及必要的相邻输出。

### 4.4 修复 / 结束

- 失败且在当前修改范围可修复：修复后重测全部失败断言及关联回归。
- 必需条件缺失（SDK/fixture/测试设备/权限/设计证据等）：标 `BLOCKED`，说明缺失项、复现命令及最小下一步；不勾选。
- 已实施但验收仍失败：标 `FAILED`，保存失败证据；不勾选。
- **所有验收项实际通过**：写完验收证据并复核之后，才允许在任务清单中勾选当前 `[ ]` → `[x]`，写完成日期和证据路径。
- 需要改其他任务的代码或改变核心契约才能继续：记录阻断，不能为了通过而悄悄扩大范围、削弱测试或改验收标准。

## 5. 安全边界及按对象区分的删除门禁

### 5.1 开发期红线

真实 Harness 本机目录：

```text
%USERPROFILE%\.dsh
%APPDATA%\@deepseek-ai\dsh-desktop
```

在开发和自动化测试中，除用户另行明确授权外，这些目录只能只读扫描。删除、覆盖、移动、写入、清空只能针对**隔离的合成测试根**进行。禁止修改 `.credentials.yaml`、`profiles/`、`dsh-runtimes/`、`workspace.json`、`Local Storage/`、`Session Storage/`、`Network/` 等受保护内容。

### 5.2 必须在实现中保证

- 只在经验证的受控根与白名单目标内执行，拒绝路径越界、reparse/junction、UNC/设备路径等危险输入；源文件变化必须在执行前重新检查。
- Harness 进程为 `Active` 或 `Unknown` 时禁止删除；不能强行结束进程。
- **会话删除（T-24/T-26）和附件删除（T-29）强制完成原始 ZIP 备份及校验**；可读 Markdown/JSON 导出不能代替原始 ZIP。
- **缓存与日志清理（T-27/T-28）**按各自任务和统一安全执行器执行；不得无依据套用会话 ZIP 门禁，但必须有用户显式选择、正确白名单、退出状态检查及结果报告。
- 以**整个会话**作为日期筛选删除单位，覆盖所选会话已识别的所有日志代际；未知格式不得自动进入精确会话删除白名单。
- 附件未知引用关系标 `Unknown`，不得按猜测自动联动删除；共享附件不得自动加入。
- 单会话清理只处理可信匹配的专属投影缓存；`session_projcache.json` 仅按显式全量缓存清理规则处理。
- 部分失败必须报告真实成功/跳过/失败列表和实际释放字节，不允许宣称全成功；工具日志、验收文本及 Git 提交不得包含真实会话正文、凭据或敏感 token。

## 6. 证据、任务状态与检查点

### 6.1 任务证据（持久记录，必须真实）

保存为 `artifacts/evidence/T-XX.txt`；完整原始测试日志可另存到项目本地忽略目录，证据文件记录其路径、命令、结论与可核查摘要。建议格式：

```text
Task: T-XX
Status: ACCEPTED | FAILED | BLOCKED
StartedAt: <实测时间>
FinishedAt: <实测时间>
Prerequisites: T-YY [x] evidence=...
ChangedFiles: <实际文件列表>
Commands: <逐条命令>
ExitCodes: <实际退出码>
Acceptance: <逐项 PASS/FAIL/NOT RUN + 对应证据>
NegativeTests: <逐项 PASS/FAIL/NOT RUN>
Regression: <逐项 PASS/FAIL/NOT RUN>
GitCommit: <真实 hash 或 NOT_COMMITTED>
EvidenceArtifacts: <可检查的文件路径>
BlockersOrDefects: <存在则写明>
```

- `NOT RUN` 不是 `PASS`；不得填充不存在的命令、哈希、样本或数据。
- 状态以**任务清单 `[ ]/[x]` 和本任务真实证据的联合结果**判定；二者冲突时先调查、修复记录，不跳过依赖。
- `FAILED` 或 `BLOCKED` 不允许勾选关闭，不允许继续其下游依赖任务。
- 若项目提交规范要求提交，按规范提交；未实际提交则记 `NOT_COMMITTED`，是否影响验收按当前任务和协作规范确定，不能虚构提交。

### 6.2 统一检查点（只写恢复要点）

路径：`artifacts/evidence/AGENT_CHECKPOINT.md`。建议控制在 **80 行内**，每轮覆盖更新，不无限追加。首次不存在是正常情况；不得把它当成新任务清单，也不要复制源码或长日志。

```markdown
# Agent Checkpoint

LastTask: T-XX
LastResult: ACCEPTED | FAILED | BLOCKED | IN_PROGRESS
NextCandidate: T-YY   # 仅提示，必须重新验证依赖

## Environment
RepoRoot: ...
SDKStatus: ...
BuildStatus: ...

## Current Work
ChangedFiles: ...
PendingChecks: ...
Evidence: artifacts/evidence/T-XX.txt
GitCommit: <hash 或 NOT_COMMITTED>

## Blockers / Next Step
- <最多几条可执行事实>
```

写入检查点前，保留此前证据，不以检查点代替任务证据。若上下文/工具即将中断：及时保存**部分变更文件、待运行测试和下一步**，状态记 `IN_PROGRESS`，清单仍为 `[ ]`，不要勾选也不要开始新任务。

## 7. 新上下文的续跑机制

**一轮只处理一个任务**。通过或失败后按第 6 节保存证据及检查点、结束当前 Agent 会话。仅在新建真正的新会话时，旧的消息/工具输出才不会继续占用窗口；“写检查点”或简单清空回答不等同于重置模型上下文。

短续跑提示词（交由用户或外部调度器在**新会话**提交）：

> 继续 DeepSeek Harness Cleaner 开发。遵守项目根目录 `AGENTS.md`；按需读取 `docs/agent-execution.md`。先检查 Git、`artifacts/evidence/AGENT_CHECKPOINT.md`、`docs/implementation-tasks.md` 与前置验收证据；恢复尚未验收的在途任务，或选编号最小的合格未完成任务。本轮只实施一个 T-XX；只读必要文档和源码。真实执行测试、保存 `artifacts/evidence/T-XX.txt`，仅在全部验收通过后勾选；覆盖更新检查点并结束本轮。禁止改动真实 Harness 数据，禁止虚构验收。立即执行。

项目使用的 Agent 客户端可能不自动读取 `AGENTS.md`。首次接入时应确认客户端确实加载项目指令；未自动加载时需在会话开头明确提供其内容。无外部调度器时，不得声称“已自动启动下一轮”。

## 8. 输出格式与最终发布条件

每轮聊天输出控制为简短摘要：

```text
Task: T-XX
Result: ACCEPTED | FAILED | BLOCKED | IN_PROGRESS
Changes: ...
Build / Tests: <实测 PASS/FAIL/NOT RUN>
Evidence: artifacts/evidence/T-XX.txt
Git Commit: <真实 hash / NOT_COMMITTED>
Next Candidate: T-YY（待下轮核验）
Blocker: ...
```

**不能把“已写代码”当成“已经验收”**。发布完成还必须同时满足：T-01～T-34 均有 `[x]` 与对应有效证据；T-32 自动与安全回归通过；T-33 真实 Win10 版本/性能及运行时联网检查通过；T-34 自包含 EXE 的发布、校验值和干净目标环境冒烟通过。若任一环节未验证，明确写为待验证，不宣布项目完成。

---

**执行原则总结**：小任务、精确阅读、真实验证、唯一状态源、持久检查点；只允许经验证的安全行为进入删除流程。

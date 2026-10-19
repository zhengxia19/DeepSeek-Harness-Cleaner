# DeepSeek Harness Cleaner — Agent 工作规则

> 当前基线：v0.1.0 已完成 T-01～T-34 的历史实施与验收。此文件约束后续维护、修复和新增需求；不是再次执行历史任务的指令。执行细则按需见 `docs/agent-execution.md`。

## 1. 先核实任务，再修改

- 每轮先确认仓库根目录、`git status --short`、分支/HEAD，以及用户本轮要求；不得覆盖未知工作树改动。
- `docs/implementation-tasks.md` 的 T-01～T-34 为**已完成历史基线**，不自动重新领取、不重置勾选、不重跑无关 PoC。对应真实证据为 `artifacts/evidence/T-01.txt`～`T-34.txt`；检查点仅供参考。
- 用户明确指定的缺陷、变更或文档任务是新工作的来源。先写明范围、受影响的 REQ/TC、验收条件；小改动直接执行，不为管理流程另造任务系统。新需求不得擅自塞入既有 T-XX。
- 采用现有 C# / .NET 10 / WinForms / Windows 10 x64 架构；变更技术栈、安全边界或格式支持范围须有明确证据、测试和决策记录。

## 2. 只读优先与删除红线

- 开发、测试和 Agent 操作**不得对真实** `%USERPROFILE%\.dsh` 或 `%APPDATA%\@deepseek-ai\dsh-desktop` 写入、移动、删除；默认仅在合成 fixture 根测试破坏性操作。
- 保持安全门禁：受控根和白名单、禁止 reparse/越界、确认 Harness 已退出、计划与实际文件复验；会话及附件删除前必须有经 SHA-256 校验的原始 ZIP 备份和用户明确确认。
- 未知/损坏/不匹配版本的日志按安全失败处理。**0.2.1-rc.2 当前仅可只读识别，不能宣称已支持语义解析或删除**；不要放宽格式门禁规避错误。
- 不写入或提交真实会话正文、凭据和隐私路径；不添加联网、遥测、自动更新或隐式后台清理。

## 3. 最小改动，真实验证

- 先按需读取相关代码、测试、契约和证据；对 64K 上下文模型禁止无差别通读全部仓库。建议一轮处理一个边界清晰的工作项，避免跨模块顺手重构。
- 根据改动运行必要的 build、test、format、回归与负向测试；涉及路径、备份、删除、兼容性的改动须补充对应安全 fixture 和 Windows 冒烟。未运行不得声称通过。
- 需求和实际行为变更同步 `docs/requirements.md`、`docs/interfaces.md`、`docs/design.md`、`docs/testing.md` 等**确实受到影响**的文档；用户可见行为同步 `CHANGELOG.md`。保留原始历史验收记录，不篡改结果。
- SDK 版本依据 `global.json`，包版本依据 `Directory.Packages.props` 和锁文件；使用 `scripts/enter-env.ps1`，不持久修改系统 PATH、注册表或全局 NuGet 配置。
- 交付时给出修改文件、验证命令/结果、未验证项、提交/PR 链接。失败与阻断明确标注，不虚构提交号或测试报告。

## 4. 文档定位

- 当前介绍/已知限制：`README.md`、`CHANGELOG.md`、`docs/release.md`。
- 规范契约：`docs/requirements.md`、`architecture.md`、`design.md`、`interfaces.md`、`testing.md`、`decisions/`。
- 环境与依赖：`docs/development.md`、`dependencies.md`、`coding-standard.md`、`scripts/`。
- 历史任务与原始验收：`docs/implementation-tasks.md`、`artifacts/evidence/`。
- 历史设计审查和当前交付复核：`docs/review-report.md`；维护流程：`docs/agent-execution.md`。

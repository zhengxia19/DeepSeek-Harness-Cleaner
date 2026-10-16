# 独立视角设计复审报告（Review Report）

日期：2026-10-16。性质：**静态设计复审，不是源代码执行、Windows 构建或真实数据验证报告**。设计阶段所有任务均未验收。

## 1. 文档结构审查

| 审查项 | 结论 | 证据/处置 |
|---|---|---|
| 用户要求的文档类型 | 通过（设计层） | README、需求、架构、详细设计、开发、规范、依赖、接口、测试、发布、变更、贡献及 2 个 ADR 均有实体文件；另加原子任务与本报告 |
| 当前说明与历史决策分离 | 通过 | ADR 记录决策，其他文档维护当前方案 |
| 权威信息单一来源 | 通过 | SDK 与包版本只在未来 global.json/中央包文件锁定；REQ 在 requirements，TC 在 testing |
| 无不必要管理制度 | 通过 | 无数据库、服务、REST API、OpenAPI、自动更新；CONTRIBUTING 仅规定 Agent 执行门禁 |
| 追踪完整 | 待实施核验 | REQ-001～020 映射 TC-01～20；T-01～34 关联测试（静态校验） |

## 2. 四个审查角色的结论

**安全审查：** 删除前要求 Harness 退出、范围白名单、拒绝 reparse/越界、文件重验、会话/附件强制 ZIP 校验，未选目标必须 hash 不变。无法达到跨文件 ACID，明确允许 PartialFailed 并要求重扫。**风险：中；需 T-32 实测关闭。**

**持久化格式审查：** 官网主分支与装机 0.2.0.0 不保证一致；V0/V4 代际可能共存；官方迁移不覆盖旧代际。设计决定保留全部物理代际备份，对旧版语义投影保守退化。**风险：中；需 T-05/T-13 fixture 验证关闭。**

**Windows 工程审查：** WinForms 不引入现代 UI 框架；本地 SDK/包缓存满足项目隔离；.NET 10 在 Windows 10 上的官方支持有限。**风险：中；需 T-33 真实目标 Build 测试关闭。**

**Agent 实施审查：** 34 个任务单一结果、输入/修改范围/前置/验收/TC/证据齐备；Zstd PoC 只在 T-05、压缩事件读取 T-12、语义 T-13；备份规划 T-21/执行 T-22 与删除 T-24/26 明确分离；缓存和附件统一复用删除执行器。**风险：低（设计层）**。

## 3. 交叉一致性检查

| 争议或典型缺陷 | 当前处理 |
|---|---|
| 按日期删除“消息片段”易毁坏日志 | 明确只删除按日期筛选的整会话；REQ-008 / design §3 / TC-08 / T-11/T-24 一致 |
| 仅删除最新 v4 遗留 v0 副本 | ZIP 与清理计划均覆盖同一会话的全部已识别 generation |
| 会话缓存 `session_projcache.json` 意外清空 | 只有全量缓存清理显式勾选，单会话不清空全局缓存 |
| 附件是否“孤儿”无法证明 | Unknown 保留，不自动联动删除 |
| 备份可直接恢复 Harness 的错误承诺 | 声明备份为原始文件留存，不实现自动恢复或官方 ZIP 回导 |
| Harness 进程只检查 GUI，忽略后台使用者 | 进程探测异常/状态 Unknown 拒绝清理，无法建立稳定状态不删除 |
| Windows 10 与 .NET 10 支持不一致 | 发布必做 OS Build 验证，区分官方支持与实测兼容 |
| 默认清理浏览器状态 | 明确排除 Local Storage/Session Storage/Network |
| 自动环境污染 | `.dotnet/.nuget/.artifacts` + 进程级变量，不修改系统 PATH |
| “任务完成”只凭代码提交 | 必须 TC 和负向测试证据，未通过不能勾选 |

## 4. 依赖有向性、数量和精简性

- 任务共 34 个，编号连续且全部前置引用早于任务自身。覆盖环境/解析/展示/备份/清理/测试/发布。
- 没有额外数据库建模、后台任务系统、远程接口、备份恢复 UI、云端删除等范围外任务。
- `README/CHANGELOG/CONTRIBUTING` 不额外独立立项，纳入 T-01、T-34，以减少文档管理任务。
- 首次实施时只开发经样本验证的语义投影，不复制官方 V0→V4 完整迁移系统。

## 5. 开工前与发布前剩余验证（不能视为已通过）

1. 用户实际 Windows 10 Edition/Build 是否在微软官方 .NET 10 支持矩阵内，及自包含 EXE 可运行性。
2. 本地 Harness 0.2.0.0 的实物 V0/V4 header、事件消息角色、压缩帧行为、附件引用和缓存格式。
3. 通过编译、自动测试、Win10 现场测试和性能测试证明代码与设计一致。
4. 核查 ZstdSharp.Port 在选定版本的流式 API 行为，以及 ZIP 校验和坏尾帧的失败模式。

## 6. 结论

**设计层复审：有条件通过。** 当前文件可直接作为开发实施依据；但**不能据此宣称工具已实现或真实文件格式兼容性已验证**。所有关键阻断项已落实到 T-05、T-13、T-25、T-32、T-33 等任务中。代码交付前应在此附实际测试结果和关键缺陷清单。

## 7. 外部来源核验（截至 2026-10-16）

- 官方 JSONL/Zstd backend：<https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/session/session-persistence-jsonl/README.md>
- 官方事件会话模型：<https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/core/session/README.md>
- 投影缓存：<https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/session/session-projection-cache/README.md>
- 格式 V4 变更：<https://github.com/deepseek-ai/deepseek-harness/blob/master/docs/persistence-changes/2026-09-16-session-format-v4.md>
- 迁移与物理 generation：<https://github.com/deepseek-ai/deepseek-harness/blob/master/.agents/notes/implemented/architecture/2026-08-31-released-session-format-migrations.md>
- 官方 Web ZIP 导出：<https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/session-query/session-log-export/README.md>
- NuGet ZstdSharp.Port：<https://www.nuget.org/packages/zstdsharp.port>
- 微软 Win10/.NET 支持矩阵：<https://learn.microsoft.com/en-us/dotnet/core/install/windows>
- 微软本地 SDK 安装：<https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script>

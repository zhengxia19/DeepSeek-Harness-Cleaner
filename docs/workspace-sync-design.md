# Workspace 元数据同步契约与状态机（T12）

本文件规定 T12–T16 的选择、备份、写入及恢复边界。T12 仅实现纯内存提议 DTO/构造器，
不写 Harness 数据，不改变删除执行器，不接入 UI。`Proposed` 不等于允许执行。T12时允许清单为空；当前发行门禁见文末A04补充，后续T13–T16段落保留当时验收范围。

## 1. 固定上游证据与可变字段

源码版本固定为 `deepseek-ai/deepseek-harness@687f282e8625c6b8d11ff58211a90524be2e4317`，
不是本机发行版本已验证的声明。

| 候选字段 | 语义与本项目限制 | 上游位置 |
| --- | --- | --- |
| `global.archivedSessionIds` | 只过滤已证实可同步的选中 ID，保留其余顺序；不触碰成员位置 | [spec.ts L40–58](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/workspace/workspace/src/spec.ts#L40-L58)、[unarchiveSession L369–378](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/workspace/workspace/src/index.ts#L369-L378) |
| `global.pinnedSessionIds` | 只过滤同一合格集合；保留未选置顶顺序 | [unpinSession L422–431](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/workspace/workspace/src/index.ts#L422-L431) |
| `tables.workspaces[id].sessionIds` | 只过滤同一合格集合，不移动/创建工作区，不裁剪其他失效成员 | [spec.ts L17–26](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/workspace/workspace/src/spec.ts#L17-L26)、[detachSession L162–165](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/workspace/workspace/src/entity.ts#L162-L165) |
| 发生成员变化的记录 `updatedAt` | 仅该记录更新为实际 Apply 尝试的 UTC ISO 时间；未变化记录原值保留。提议时间不得冒充应用时间 | [mutate L188–199](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/workspace/workspace/src/entity.ts#L188-L199) |

官方实体 mutate 还会通过 candidateMatches 裁剪其他成员。Cleaner 为遵守精确选择边界，
**不复制该顺带裁剪行为**；源码仅用于核对字段和时间戳语义，不等于授权外部调用/重写。
全局归档/置顶变更本身不更新工作区时间。单次候选文件构造对所有实际变更成员记录使用同一 UTC 毫秒时间。

禁止修改 `unit`、`initialized`、`workspaceIds`、`defaultWorkspaceId`、工作区键、path/title/createdAt、
未选会话引用、未改变工作区的任何字段。不得初始化未知数据、重排其他数组或清除不相关孤儿。
未知字段语义未核实则拒绝写入；只读索引能读取已知字段不代表可无损重写未知字段。
缺失的默认数组且无变更时保持缺失，不为格式整齐补字段。

## 2. 物理删除依赖与提议

实际类型位于 Application：`WorkspaceMetadataPlan`、`WorkspaceSessionDecision`、
`WorkspacePhysicalOutcome`；`WorkspaceMetadataProposal.Build(cleanup, before, outcomes, after)` 无文件 IO。

输入必须属于同一 CleanupPlan ID/根指纹/原目标集合，使用 T10 **当前** Workspace 索引和原字节 SHA-256。
选择集合只来自原预审计中的会话，不能由当前 Workspace 孤儿 ID 自动扩大。
本轮读取后又发生变化，原提议不再是执行依据，T14/T15 必须重新核查。

逐会话同时满足才产生引用移除提议：

1. 原预审计至少有一个日志，且该会话全部已观察日志代际均列入原物理目标；身份未知/未纳入的代际不能绕过。
2. 与该会话证实关联且明确选中的全部物理目标，每个只有一条实际 Deleted 结果；Skipped/Failed/缺失/重复结果均不得通过。
3. 当前复查对每个原关联目标都明确为 Absent，且没有新发现的该会话日志。Present/Unknown 不等于不存在。
4. 当前日志发现/身份覆盖完整；日志根、日志路径、重复身份等异常不能由附件覆盖未知掩盖。
5. 当前 Workspace 引用状态 Known、版本/结构已通过只读索引，快照哈希和根路径可绑定；Unknown/Unsupported 不产生提议。

一个会话失败不移除其任何引用，其他证据充分的成功会话可以独立进入提议；未选、无原日志目标、
仅“原来就没有文件”的会话不进入同步。保留附件或非本次范围的缓存不算物理删除失败；
若某附件/缓存确实被同计划选中并证实关联，失败/未知也会阻止对应会话的提议。
附件索引的全局覆盖未知不授权删除附件，也不被偷换为日志删除失败。

返回 `ReferencesToRemove` 仅保留合格 ID 的既有引用，顺序与当前索引一致；JSON Pointer 是证据位置，
**不是写入指令**。后续写入器应在重新验证后的原 JSON 上按 ID 过滤，避免缓存下标因前一次移除偏移。
DTO 保存 SourceSha256、当前文件路径、根/计划身份及逐会话决策，不保存正文、标题或凭据。
同时只读复制原物理目标（包括原长度/mtime/SHA）、实际删除结果及逐目标缺失复查，供后续阶段
核查/保存依赖证据，不丢弃失败会话的原事实。RecheckedUtc 保留 post-audit 的观察时间，
不是每文件删除时刻；ProposedUtc 仅为提议生成时间，二者不混用。
`ProductionWriteContractIds` 当前只列出受条件约束的0.2.1-rc.2契约；SourceContractId/Commit 描述源码契约，不冒充发行提交号。清单本身不是写入许可。

## 3. 状态机及证明责任

以下是后续阶段的契约；T12 工厂实际只产生 Proposed、NoOp、Blocked，其余状态没有执行证明前不能标记。

| 状态 | 进入依据 | 下一步与失败边界 |
| --- | --- | --- |
| NotRequested | T16 未勾选同步，默认状态 | 永不调用元数据写入 |
| Proposed | T12 合格 ID 的非空引用提议 | 不是执行许可；先验版本/后端、备份和运行状态门禁 |
| NoOp | 合格集合在当前已知快照没有引用 | 不重写、不刷新无关时间戳；不得称已完成一次元数据写入 |
| Blocked | 版本未允许、未知语义/事务、路径/进程/哈希/证据失败 | 不替换文件，保留原因及已有备份；新证据后重新提议 |
| BackupVerified | T13 原字节备份、路径、哈希、校验记录可重新验证 | 会话 ZIP 开关不能跳过；备份校验失败拒绝继续 |
| Prepared | T14 当前版本允许、进程 Inactive、根/文件身份/快照/备份及删除缺失复验全部通过 | 冻结候选变换、恢复记录；即将应用前再检查 |
| Applying | T14 在同目录写候选、flush、验证并受控替换 | 单份 Workspace 受控变更；两次不覆盖重命名，不声称原子 |
| Applied | 替换后重读，预期变换及所有不变量通过 | 保留备份与事实；后续审计如实显示剩余失败/未选引用 |
| RecoveryRequired | 提交点或替换后验证无法确认、旧恢复记录未结清 | 保留原备份/候选/记录；禁止新写入，不猜测恢复结果 |

`pendingMutation` 是上游工作区 create/delete 的恢复标记，见 [spec.ts L31–38](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/workspace/workspace/src/spec.ts#L31-L38)
及 [recoverPendingMutation L595–606](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/workspace/workspace/src/index.ts#L595-L606)。
Cleaner 既不伪造该标记，也不代替 Harness 完成它；出现任何事务标记时拒绝外部同步。
工具恢复记录另存于受控的工具备份/状态区，不把未知事务、锁文件或旧候选当作可自动清除的垃圾。

T14 必须独立重新验证实际文件和进程，不能把公共 DTO 的 State、调用者传入的“成功”或布尔值视为授权。
未知/非允许版本、双布局、reparse、并发变化、运行/未知 DSH、备份失败和未完成恢复记录均拒绝写入。
T13 独立备份、T14 受控写入器已在合成根验证；T15 恢复/重试协调及结果报告已在合成根验证；T16 可选同步/恢复交互 UI 已实现并通过合成 Windows 验收。生产写入仍禁止。

## 4. 不变量与后续验收

实际写入前后的验证以语义树为准：所有未选 ID 的每个数组位置相对顺序、workspaceIds 顺序、
未改记录/字段及字段缺失状态保持一致。允许 JSON 格式化字节变化，但原始字节必须已独立备份；
不得通过字符串替换改 JSON。源 SHA/根/文件身份变动即废弃旧计划。

T13 必须演练原字节备份及可验证恢复；T14 必须验证精确筛选、时间戳、未知字段拒绝、
替换失败/权限/并发/中断门禁；T15 分别报告物理删除和元数据结果，失败不自动恢复已删除日志；
T16 默认关闭、显示引用数/强制备份/禁用原因，未勾选不能写入。

T12 测试仅证明提议选择与源数据不变，不声称已实现/验收上述写入与恢复动作。
当前真实发行映射/样本及真正的进程强制中断证据仍未补全，生产写入仍禁止。

## T13 独立原始备份的已实现边界

`WorkspaceBackupService` 不接受会话 ZIP 开关。先复核源快照和进程 Inactive，再以 CreateNew、
写穿/落盘方式写原字节、manifest 和校验记录，并重读验证。相同操作仅在源与备份仍可验证时复用；
损坏或不完整操作保留，禁止覆盖。文件大小有界，重复/未知字段、路径越界、reparse、身份/哈希
不匹配或读失败均返回失败，不能产生可用备份证据。证据不是元数据写入授权。

20 项新增测试覆盖原始 BOM/空白、真实链接/文件锁、损坏与合成恢复演练。恢复演练只在夹具内
复验原备份后写回源并核对字节与外部哨兵；没有新增生产恢复 API，也不声称恢复文件系统时间戳。
路径检查与后续文件操作不构成原子事务；T14 仍须独立复核当前源/进程/版本/备份及替换边界。
本地哈希用于一致性验证，不提供对全部材料被协调篡改的真实性保证。

## T14 受控写入器的已实现边界

公开生产门禁仍关闭；55 项 Windows 合成测试验证 Prepare/Apply 的结构、初始化、事务、精确引用、
当前物理缺失、进程、备份、源身份/SHA 与严格字段门禁。不透明准备对象绑定写入器且只消费一次。
相同字节但不同 Windows 卷/文件索引也拒绝，硬链接及不可识别身份拒绝；未选数据和工作区状态保持。

实际提交采用 [FILE_RENAME_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info)
的句柄重命名，ReplaceIfExists=false。源和候选以 OPEN_REPARSE_POINT 打开且保持拒绝外部写入/删除的句柄；
源/备份祖先目录保持拒绝删除的句柄，不能在验证后换目录。原源 ACL/owner/group 复制到候选并验证。
原文件先保留到受控备份操作目录，候选再进入已空出的源路径，突然出现的普通文件/链接均不覆盖。
该方案在 Windows 10.0.19045 实测，不声称跨这两次重命名原子。中间中断可以使 workspace.json 缺失；
此时原字节、原文件、候选和记录仍保留，返回 RecoveryRequired，T15 必须复查这些实际材料后才能恢复或重试。

不用按路径的 File.Replace：其关闭文件句柄后的链接替换窗口在合成故障测试中实际移动了链接目标，
因此该候选实现已淘汰。最终真实链接/硬链接、文件锁、ACL、目录替换、候选替换和中断测试均通过。
测试记录包括初始失败；不能把被淘汰实现的结果当作当前能力。最终证据见 T14.txt、T14-final-full.trx。
进程状态与物理缺失在提交前复核，提交后复核变换/备份/物理缺失；取消在提交前生效，开始两步提交后
优先验证结果，不自动逆转。没有新增生产 UI、恢复协调或实际发行映射允许项。

## T15 恢复与重试的已实现边界

WorkspaceSyncCoordinator 保留原物理执行事实，仅重新扫描后提议元数据；失败/跳过会话仍排除。
每次重试使用新快照/操作/独立备份，未结清中断不能跳过。恢复为显式动作，不自动恢复日志。
WorkspaceRecoveryService 重新验证严格记录、备份、身份、候选变换、标记及进程；
原源仍在时只保留候选/标记，源缺失且保留源身份完全匹配时恢复原源，已应用候选只复查确认。
新文件、相同字节但不同身份的源、链接或未知材料均保留并阻断，禁止覆盖。
恢复使用句柄不覆盖重命名，原字节/备份文件始终保留；候选/标记转存备份目录，完成记录复验后结清。
自身中断/权限失败可能已完成部分步骤，必须再读实际状态，不宣称恢复操作原子。

44 项新增 T15 测试及 44 项 Windows 报告/回调检查通过；全量 607 项自动测试通过。
MainForm 默认路径记录 NotRequested，文件结果和元数据结果分开显示/记录；请求及恢复/重试交互已由 T16 接入。
生产运行版本/样本映射仍未验证，公开协调与恢复接口保持拒绝生产写入。

## T16 可选交互与总体待验收边界

默认关闭的同步选项、潜在引用数、独立强制备份目录与禁用原因已接入确认窗口。
可用性只读检查且确认前重做；条件变化时在物理删除前停止，不降级为未请求。
未勾选不写元数据，已勾选且通过门禁才在原物理执行后调用协调器。
复查窗口绑定原操作，恢复明确另行确认，结清后重试；保留备份、候选、旧日志和原物理结果，根切换被阻断。
勾选与备份目录不持久化。13 项可用性测试、72 项同步窗口检查、91 项原功能回归、44 项后审计与21 项根选择检查通过。

这些证据证明合成源契约下的 UI/安全流程，不证明任何未映射实际发行版本可写。
生产允许清单仍为空；A01–A07 必须独立逐项审计，已知真实发行/后端/样本映射与真正进程强制中断验证尚缺。
不得把 T01–T16 全部任务勾选解释为整体验收或发布完成。

A05后续审计已补齐8阶段实际子进程强制终止模拟（120项），并补测锁退役后返回前的完成记录确认。
此前“真正进程强制中断未做”的T16记录为当时状态；当前证据见A05-process-crash.txt。
硬件断电仍未实测；已知上下文恢复与未知孤立操作阻断的边界保持。生产发行/后端/root映射仍待核实。

## A04 当前发行与配置门禁（2026-10-20）

当前允许项为 `dsh-desktop@0.2.1-rc.2/workspace/v2/json-single`，不是全部v2许可。
`WorkspaceProductionGate.Read` 只读检查已核验ASAR资产、发行名/版本、当前进程环境解析的Harness根、desktop profile全部声明的配置层与四个核心插件清单。
安装包先于profile本地包解析；包内声明的多个patch按顺序全部读取，随后叠加profile及home层。YAML限1MiB/层、32层结构深度、50000事件，拒绝锚点/别名、多文档、重复键和未知标签，不执行`!!js`。
存储绑定须为已知storage/storage-json/storage-domain/workspace模块、无未知核心注入/隔离/父级条件，JSON后端、workspace路由为json或缺省、根表达式严格为`dshHomePath('storages')`（或等价双引号）。原生include配置尚不支持，保守拒绝。
兼容性约束仅接受本发行已审阅的精确范围；未知范围或需要兼容豁免的包不放行。手选根与当前环境解析根不一致时仍可只读审计，但不提供同步。

`WorkspaceStoragePolicy`按打包app-boot的数据层语义组合配置：`config`替换而非合并，名称断言不匹配的patch跳过，检查实际父级与重复核心提供者。它不证明任意第三方插件的运行时副作用。
`WorkspaceWritePreparation`保存不可由公开DTO构造的`StorageBinding`指纹。Prepare、Apply及最后替换边界重复核验发行与配置；相关输入变化即拒绝旧准备，必须重新准备，不固定任何个人配置哈希。
原有进程Inactive、严格字段/事务、源身份/哈希、物理删除缺失证据、强制备份、路径及不覆盖重命名条件继续生效。Recovery也通过相同生产作用域门禁。

公开协调器已在生成的隔离根配合本机安装资产执行端到端测试；仅测试进程临时设置DSH_HOME并在finally恢复，进程退出状态由测试探针模拟。真实Harness未停止，真实元数据未写入。原子性/硬件断电/未知孤立恢复边界不变。
当前证据见 `artifacts/evidence/change-plan-2026-10-20/A04-production-gate.txt`。本文此前T编号段落是当时的阶段记录，当前允许状态以本节及支持矩阵为准。
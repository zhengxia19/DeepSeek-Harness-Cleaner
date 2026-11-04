# 接口与数据契约（Interfaces）

## 1. 对外接口

本工具**无网络 API、HTTP 监听端口、CLI 清理命令或注册表协议**。用户通过 WinForms GUI 操作；工具与 Harness 的唯一集成为受控目录只读扫描和用户确认后的文件清理。

## 2. 业务接口（设计契约）

```csharp
interface IRootLocator { ResolvedRoots Resolve(string? selectedDshHome, string? selectedDesktopHome); }
interface ISessionInventory { Task<IReadOnlyList<SessionSummary>> ScanAsync(ResolvedRoots roots, CancellationToken ct); }
interface IEventReader { IAsyncEnumerable<EventRow> ReadAsync(PhysicalLog log, CancellationToken ct); }
interface IBackupService { Task<BackupResult> CreateVerifiedAsync(CleanupPlan plan, string destination, CancellationToken ct); }
interface ICleanupPlanner { CleanupPlan Build(ResolvedRoots roots, Selection selection); }
interface ICleanupExecutor { Task<CleanupResult> ExecuteAsync(CleanupPlan plan, VerifiedBackup? backup, CancellationToken ct); }
```

接口签名为设计意图；具体源码名由 T-03/T-09 固化，不产生独立对外二进制 API 兼容承诺。

### 数据根候选发现（2026-10-20 新计划 T04，实际接口）

`RootLocator.DiscoverCandidates(selectedDshHome, savedDshHome, selectedDesktopHome, savedDesktopHome)`
返回 `RootDiscoveryResult.Candidates`；四个可空参数默认为 null。DSH 参数均为数据 home，
不是 `sessions` 子目录。调用者仅在 `RememberRoots` 启用时传入记忆路径；此方法不读写设置。

每个 `RootCandidate` 包含 `Kind`（DshHome/DesktopHome）、`InputPath`、`NormalizedPath`、
全部 `Sources`（UserSelected/Saved/EnvironmentVariable/Default）、`Status` 与 `Reason`。
规范化失败时 `NormalizedPath=null`，原输入保留用于报告；缺失、无效和不可访问不冒充有效。
同一类别的普通路径按 GetFullPath、尾分隔符和 Windows 大小写规则去重并保留全部来源；
不同类别始终分开。重解析根拒绝，不跟随链接把别名视作第二个有效根。

候选各自探测；坏的 DSH_HOME 不隐藏默认候选。结果不自动选根、不合并数据、不授予清理权限。
仅检查已列出根及其顶层标记，不扫描全盘，不从 EXE 安装目录推断数据根。
`RootLocator.Resolve` 仍保留手选 > 环境变量 > 默认的旧单根行为，未改为自动回退。
无参构造读取当前账户与进程环境；双委托构造可注入 SpecialFolder 和环境查询以隔离测试。

T05 校验限定为上述四类来源：相对/驱动器相对路径、UNC/设备路径、ADS、字面 `..` 和
末尾点/空格歧义段在探测前拒绝。驱动器根、真实用户目录根和运行程序目录由 `PathGuard`
拒绝；逐级检查祖先与根自身，不跟随重解析点。只枚举已列根的顶层标记，并通过 `PathGuard`
复验各受控子目录及其枚举权限；不递归发现其他根。
ROOT-20261104：会话根必备标记为 `sessions` 与 `storages`；`attachments` 为 DSH 懒创建的
可选标记——存在时仍经同一 `PathGuard` 复验（重解析/访问拒绝仍拒绝该根），缺失不影响
有效性，且探测不得创建它。缺失的 `attachments` 由附件盘点以 `ATTACHMENT_ROOT_MISSING`
问题项降级，不阻断会话清理。

候选原因包含 `ROOT_NOT_ABSOLUTE`、`ROOT_UNC_OR_DEVICE`、`ROOT_TRAVERSAL`、
`ROOT_AMBIGUOUS_SEGMENT`、`ROOT_INVALID`、`ROOT_MISSING`、`ROOT_MARKERS_MISSING`、
`PATH_REPARSE`、`FILE_ACCESS_DENIED`。权限不足返回 `Inaccessible`，不伪装成不存在。
`Resolve` 保留原选择优先级并复用同一安全校验；旧 `RootProbeStatus` 不扩充，权限/路径拒绝
映射为 `NotHarness` 加原因，缺失仍为 `Missing`。只读探测不创建目录。

T06 已通过 `RootSelectionDialog` 接入 `MainForm` 的“文件 → 选择数据根”及首次扫描流程。
多个有效会话根不预选；单一有效根仍需确认。桌面根由独立勾选确认，未确认时保持 null。
手选根加入同一候选校验流程。路径记忆仅复用 `SettingsStore`：保存 `SessionRoot=home/sessions`，
读取记忆时只将末段确为 sessions 的路径转换回 home；关闭记忆清除已存根路径。
原有窗口/日期等设置通过 record with 保留；损坏/未知配置仍由 Store 拒绝覆写。

确认新根或开始重新扫描时，清空旧扫描结果、勾选、预览及取消源、清理对话框/结果引用。
扫描（含删除后刷新）、计划准备或删除确认/附件窗口未关闭时禁止切换；删除入口禁止与扫描并行，
后台计划返回后再次比较根路径。取消根选择不改变已确认根。正常 UI 不跨根合并或复用计划。
`Valid` 只表示本次结构和可访问性探测有效，不代替扫描/删除前复验。

## 3. 磁盘契约

### Workspace 只读引用索引（2026-10-20 新计划 T07）

`WorkspaceReferenceReader.Read(string dataHome)` 返回 Domain 的 `WorkspaceReferenceIndex`：
`Status=Known/Unknown/Unsupported`、`EvidencePath`、可空 `SnapshotSha256`、`References`、
可空 `Reason/ErrorPointer`。每条引用包含 `SessionId`、`Kind=Archived/Pinned/WorkspaceMember`、
可空 `WorkspaceId`、RFC 6901 转义的 `JsonPointer`，数组索引对应实际文件顺序。

仅在已解析的 v2 single JSON 快照内，`Known` 且空列表才表示该快照已知字段没有引用。
缺失/损坏/不可读/未初始化/存在事务标记/重复字段或 ID/归档置顶冲突/成员跨工作区歧义/
工作区注册表不一致返回 `Unknown`，不返回半份“完整索引”；其他 unit/版本与 per-record 候选目录
返回 `Unsupported`（single 文件与 per-record 目录并存为 `Unknown`）。不猜测 SQLite 位置。
归档/置顶字段缺失按已核实 schema 默认空数组；成员表和 workspaceIds 必须明确、匹配。

文件只读、有 8 MiB 限制，拒绝重解析点和不安全根。哈希标识当次读取字节，不证明后续磁盘状态、
实际运行后端或已获得真实发行样本。未知字段不产生虚构引用；重复字段即使位于附加对象也拒绝。
引用含工作区标题/路径的记录仅供结构校验，不将其复制进引用结果或错误文本。

`WorkspaceArchiveReader.Read` 仅复用内部 `WorkspaceJson` 的有界加载和对象重复字段工具，
保留旧归档列语义（包括 BOM、超大文件返回 null、缺少归档字段仍未知）。它仍不代替完整索引。
本任务没有新增写接口；元数据写入允许清单仍为空。后续 T08–T11 才接入关联清单、审计和报告。

### 会话关联清单（新计划 T08，未接入删除 UI）

`SessionResidueInventory.Scan(dataHome, selectedSessionIds, IReadOnlyList<CleanupTarget> targets, ct)`
复用物理日志发现/头部身份校验、专属缓存索引、完整 Workspace 只读索引和附件盘点，返回
Application 的 `SessionResidueSnapshot`（Sessions、UnattributedItems、Issues、Workspace、HasUnknowns）。
此接口既不创建计划也不修改传入目标，更不执行删除。

每个 `SessionResidueItem` 包含类别、范围、证据文件/可空 Pointer、关联 ID、已证实引用会话集合和原因码。
范围 `TargetFile` 仅在已证实会话关联与调用方目标的路径/类别/所有者匹配时出现；`NotInScope` 表示
有证据但未纳入本次清理；`UnknownReference` 表示无法证实关联或引用缺失，不推断不存在。
同文件不同类别或不同所有者不能冒充计划目标；越界/重解析计划目标仅报告异常，不访问或扩大范围。

Workspace 引用始终为物理删除范围外证据；未单独选择的附件（含共享附件）为 NotInScope，
不会产生删除失败结论。全量缓存不归属单一会话，单独列为范围外；无法关联的缓存和附件放入
UnattributedItems，不按文件名猜测归属。日志身份不明或重复、扫描异常、Workspace 未知、
未解析附件引用和悬空附件引用均保留为未知。已无日志的元数据引用仍显示；缓存所有权不会由用户输入 ID 自动证实。

`AttachmentInventoryResult` 兼容原构造，新增只读结果属性 DanglingReferences、UnresolvedSessionIds、
GlobalOrphanConclusionAvailable，直接来自既有 AttachmentIndex；悬空引用以附件容器为证据，不虚构磁盘文件路径。
现有附件扫描明确不保证全局解析覆盖，清单会保留 ATTACHMENT_REFERENCE_COVERAGE_UNKNOWN，
即使已显示正向引用也不推断其余附件是孤儿。DTO 不包含正文、附件显示名或用户工作区标题。

HasUnknowns 表示未知事项，不能反向当作执行成功；接口不提供 AllClean 或删除成功标记。
它是当次扫描事实，不保证多个资源的原子快照，T09/T10 仍须分别执行前后审计，且原删除门禁不变。

### 删除前审计与确认（新计划 T09）

`PreCleanupAuditor.Audit(sessionRoot, selectedSessionIds, CleanupPlan, ct)` 为独立只读入口，
校验 sessions 根与原计划根指纹，冻结原目标列表，再调用 T08 清单。返回 Application 的
`PreCleanupAudit(PlanId, RootFingerprint, ObservedUtc, Targets, Snapshot)`；不返回新的删除计划。
`Matches(plan)` 比较计划 ID、根指纹及逐目标完整记录；备份偏好不改变物理范围，不影响匹配。

MainForm 在后台先准备原计划再执行审计，期间阻止根切换和重入。审计异常不打开删除确认；
未知关系不是执行成功，也不会自动阻断用户明确选择的原物理日志删除，原路径/身份/进程等门禁仍保留。
CleanupDialog 的“待删除文件”页保持原目标，“只读审计 / 范围外 / 未知项”页通过
`PreCleanupAuditText.Render` 展示日志代际文件名、专属缓存、Workspace 文件/JSON Pointer、
附件共享关系和无法确认项。默认页也显示引用/异常摘要；审计页只读、双向滚动，不改变目标。

确认时重新检查审计与当前计划是否匹配，不匹配禁用执行、要求重新扫描。历史低层测试/独立附件调用
可不传审计，界面明确写“未提供会话删除前审计”，不据此宣称彻底清理；正常 MainForm 会话确认必传审计。
本任务没有新增元数据写入或删除后成功判定，后者仍待 T10/T11。

### 删除后实际复查（新计划 T10）

`PostCleanupAuditor.Audit(sessionRoot, plan, beforeAudit, ct)` 不接受执行器结果作为输入；
核对计划绑定/根后重新运行清单，并逐原目标只读探测 `Present/Absent/Unknown`。
使用 PathGuard 的缺失状态与明确文件属性异常分流，不用 File.Exists=false 冒充已删除；
重解析/越界/权限/结构变化不能判不存在。可发现原目标已不存在但同会话新代际日志又出现的情况。

返回 `PostCleanupAudit`：PlanId、ObservedUtc、Sessions、Targets、可空 After、Issues、HasUnknowns。
每个会话包含原关联目标的实际状态、已观察日志数、可空 Workspace 引用数和未知标记；
状态为 PhysicalResidue / MetadataResidue / Unknown / NoObservedResidue。
已证实文件残留优先展示；即使没有原物理目标，已知 Workspace 引用仍明确为元数据残留。
复查不完整、缺失/不可读/不支持的元数据保持 Unknown，不能将引用计数缺失填 0。
NoObservedResidue 仅描述本次范围，绝不代表全历史数据彻底清理；附件覆盖限制继续保留未知项。

MainForm.OnCleanupFinished 在后台执行复查与列表刷新，期间继续锁定根切换；捕获 UI 上的
计划/备份事实后再进入后台，不从工作线程读取控件。复查失败显示未知；主窗状态栏先显示
会话复查，再单列原执行器的文件计数/阻断。切换根清空旧复查结果。结果窗口/脱敏日志扩展仍为 T11，
T10 未把会话完整性写入旧文件级 AllSucceeded，也未实现元数据同步。
MainForm 增加可选 ActionsPresenter 注入用于隔离 Windows 测试；生产默认仍使用真实进程探测。

### 会话结果与脱敏审计日志（新计划 T11）

`OutcomeReportInput` 保留原构造，新增可空 init 属性 `SessionAudit`；`ActionsPresenter.BuildReport`
新增末尾可选参数 `PostCleanupAudit? sessionAudit=null`，不同计划 ID 的复查降为未知，文件指标不变。
MainForm 将实际 T10 复查传入结果报告。`DeletedCount/SkippedCount/FailedCount/FreedBytes/PartialFailure`
仍仅表示文件执行，不表示会话完整性。

`OutcomeReport.Sessions` 为 `OutcomeSessionRow`：会话状态、已证实残留类别（日志/专属缓存/附件/
Workspace 元数据/其他文件）、原目标在/无/未知计数、已观察日志数、可空元数据引用数和未知标记。
`PhysicalResidueSessionCount/MetadataResidueSessionCount/UnknownSessionCount` 是独立维度，可重叠；
元数据未知显示“未知”，不填为已知 0。异常输入降为未知，报告不给“彻底清理成功”标记。
`AuditIssues` 汇集并去重 T10/当前清单异常、未知关系和目标探测失败；范围外保留不是文件执行失败。

ResultsDialog 分为文件执行结果、会话复查、审计异常三页，分别展示计数与证据；路径只在界面中显示，
表格支持双向滚动/复制。没有会话审计的旧调用明确标注未提供，继续保持旧文件报告能力。

`BuildLog` 在附带 SessionAudit 时追加 `session_audit_summary/session_audit/session_audit_issue` 事件；
记录会话序号、固定枚举类别/状态、计数、未知标记和实际审计时间。新事件不输出原始会话 ID、
证据路径、JSON Pointer、相关 ID、正文或自由文本；异常码使用闭合集合，未收录码记为 OTHER_AUDIT_ISSUE。
旧文件 item 事件保留既有白名单会话 ID 规则；无会话审计时旧日志行结构不变。
既有文件结果与新增审计指标独立，日志不会把“文件已删除”转换成“逻辑会话已完整清理”。

### Workspace 同步提议 DTO（新计划 T12，仅内存）

`WorkspaceMetadataProposal.Build(CleanupPlan, PreCleanupAudit, IReadOnlyList<WorkspacePhysicalOutcome>, PostCleanupAudit)`
返回 `WorkspaceMetadataPlan`：操作/清理计划 ID、根指纹、数据根/Workspace 路径、当前源 SHA-256、
提议时间、状态、候选引用移除列表、逐会话决策及全局阻断原因。没有文件 IO，也不修改删除执行器或接入 UI。
另只读保留 PhysicalTargets、DeletionOutcomes、AbsenceEvidence 和原 RecheckedUtc，供后续核查/恢复记录使用；
不把提议生成时间当成实际删除或复查时间。

仅对完整选入原计划的日志会话，在对应物理目标全部实际 Deleted、当前逐目标明确 Absent、
没有新日志且当前日志扫描/Workspace 证据完整时提议移除。失败、跳过、缺失/重复执行结果、
未确认缺失、未知/重复身份、错根/计划/快照或没有原日志目标均不能作为同步依据。
成功会话与失败会话独立决策，未选 ID 不扩入；移除引用的证据顺序保持原索引顺序。

DTO 枚举含后续备份/应用/恢复状态，但当前构造器只产生 Proposed/NoOp/Blocked；
`ProductionWriteContractIds` 只读空集，提议不是写入许可。`SourceContractCommit` 是固定源码证据，
不代表本机发行版。字段白名单、变更记录时间戳、不得裁剪未选 ID、不变量及状态证明责任见
[同步设计](workspace-sync-design.md)。T13独立原始备份、T14受控写入器、T15恢复/重试协调及独立结果、T16可选同步和恢复交互均已实现；A04增加受限发行/有效配置门禁，详见文末补充。

### 输入类型

- 会话目录：每个会话的规范代际日志文件 `session.jsonl[.zstd]` 或 `session.vN.jsonl[.zstd]`；遍历时只读读取 `SessionHeader` / 事件。
- 投影缓存：`storages/session_projcache/sessions/<id>.json`、`storages/session_projcache.json`；不是权威会话日志。
- 桌面清理目录：`Cache`、`Code Cache`、`GPUCache`、`DawnGraphiteCache`、`DawnWebGPUCache`、`logs`。

### ZIP 导出契约（schemaVersion=1）

- 文件条目为原字节，使用 `sessions/<relative>` 或 `attachments/<relative>`；`manifest.json` 记录 root、relativePath、length、sha256、sessionId、警告。
- 对同一日志所有代际一并备份，避免把迁移前的历史文件丢失。
- SHA-256 小写 64 位十六进制；ZIP 内不得出现绝对路径、路径遍历、重复规范化路径。
- 可读导出为 UTF-8 Markdown 和机器可读 JSON 概览，必须带 `exportIncomplete=true`（若有未知事件、超限或附件缺失）。可读导出**不是恢复备份**。
- backup ZIP 的生成属于工具私有格式，不声明与官方 `dsh-session-<id>.zip` 导出完全兼容，也不宣称能用 Harness 官方 UI 导入。

## 4. 设置配置（schemaVersion=1）

位置 `%LOCALAPPDATA%\DeepSeekHarnessCleaner\settings.json`：

```json
{
  "schemaVersion": 1,
  "rememberRoots": false,
  "sessionRoot": null,
  "desktopRoot": null,
  "backupRoot": null,
  "dateField": "fileModified",
  "previewMaxItems": 2000,
  "windowWidth": 1100,
  "windowHeight": 700
}
```

- `rememberRoots=false` 时不得持久化所选绝对路径；默认扫描位置为系统环境推导。
- `previewMaxItems` 固定上限 2000；恶意大值按设计上限截断或报配置错误；不得扩展到未授权根。
- 任何未知 `schemaVersion`：只读提示/回退默认，不覆盖用户已有配置。

### 4.1 主窗自动记忆（2026-10-20 用户指定，已实施）

位置为程序目录 `AppContext.BaseDirectory/DeepSeekHarnessCleaner.window-state.json`，不受进程当前工作目录影响，与上述通用设置文件分离：

```json
{
  "schemaVersion": 1,
  "normalBounds": { "x": 100, "y": 100, "width": 1120, "height": 720 },
  "windowState": "Normal",
  "savedDpi": 96
}
```

- `normalBounds` 为正常窗口外框的屏幕坐标/大小；`windowState` 仅保存 Normal 或 Maximized，最小化关闭按 Normal 恢复。启动时按当前工作区和 DPI 校正，不直接信任越界、极端或无效尺寸。
- 缺失文件使用默认；损坏或未知版本只读回退、不覆盖原文件。保存采用同目录唯一临时文件和原子替换；程序目录只读/文件占用时反馈失败，保留已有有效文件，不另找位置静默保存。
- 仅窗口字段落盘，不包含会话内容、扫描路径或凭据；不因关闭窗体写入真实 Harness 目录。测试必须注入独立应用目录/窗口状态路径，禁止自动测试污染发布目录或用户设置。
- 原 settings.json 的 windowWidth/windowHeight 保留旧 schema 兼容性，不再与自动窗口记录双向同步，防止双重来源覆盖。

## 5. 错误码

| 错误码 | 意义 | 结果 |
|---|---|---|
| ROOT_INVALID | 不是受授权 Harness 目录 | 停止 |
| PATH_REPARSE | 存在符号链接/junction | 拒绝目标 |
| PATH_ESCAPE | 目标不在受控白名单根内 | 拒绝目标 |
| PROCESS_ACTIVE | Harness 仍运行 | 禁止清理 |
| PROCESS_UNKNOWN | 无法可靠确定退出状态 | 禁止清理 |
| FORMAT_UNSUPPORTED | 格式未验证 | 仅只读、原始备份 |
| FORMAT_CORRUPT | Zstd/JSON 破损 | 不进入自动清理 |
| BACKUP_INVALID | 归档未通过校验 | 不删除 |
| FILE_CHANGED | 计划/备份后文件变化 | 停止或跳过 |
| FILE_ACCESS_DENIED | 文件权限拒绝/被占用 | 报告，禁止提权 |
| OP_CANCELLED | 用户取消 | 停止于安全边界并报告部分结果 |

## 6. 兼容性与版本

- 文件格式只按受验证的样本适配，未知事件保守退化。
- 配置/备份 manifest 使用 `schemaVersion`；变更不兼容字段时新增版本并留迁移策略；不覆盖旧备份。
- 本工具版本遵循 semver：0.1.0 为首次合格发布，以实测支持矩阵写入 release notes。

### Workspace 原始备份（新计划 T13）

`WorkspaceBackupService.Create(plan, backupRoot, ct)` 返回 `WorkspaceBackupResult`；成功时含
`WorkspaceBackupEvidence`，失败时 Backup 为 null 并返回固定错误码。目标根须预先存在、受控、
无 reparse 且位于源数据根和真实保护根外。每个操作使用独立 GUID 目录，不接受会话 ZIP 开关。
保存 `workspace.original.json`、`manifest.json`、`verification.json`；保留原始 BOM/空白字节、
源路径/哈希/长度/修改时间与计划身份，校验记录绑定 manifest 哈希。失败残留保留，不覆盖旧操作。
`Verify(plan, backupRoot/evidence, ct)` 重读实际文件，严格验证路径、结构、哈希及身份；
不要求当前源仍等于备份，因此源变更后仍可验证恢复材料。调用者不能把 DTO 或 Success 缓存当作写许可。
T13 提供备份与复验；T14 提供受控替换（见下文）；T15 提供受控恢复协调；生产门禁未开放，T16 交互 UI 已实现，见下文。

### Workspace 安全写入（新计划 T14）

`WorkspaceMetadataWriter.Prepare(plan, backup, ct)` 返回 `WorkspacePrepareResult`。成功返回的不透明
`WorkspaceWritePreparation` 绑定当前写入器实例、冻结计划及源文件 Windows 身份，不能由公开 DTO 构造。
`Apply(preparation, ct)` 只允许消费一次，返回 `WorkspaceWriteResult`：Blocked、RecoveryRequired 或 Applied，
含固定错误码、已验证应用哈希/尝试时间及本地意图记录路径。不得把失败的源状态推断为“未变”。

公开构造器仍拒绝生产写入，原因 `SYNC_PRODUCTION_VERSION_NOT_ALLOWED`；测试内部构造器仅允许
规范 `%TEMP%/dshcleaner-fixtures/fx-GUID/.dsh`，没有公开强制开关。支持字段限于固定 v2 源码契约，
未知属性、事务、布局、哈希、身份、进程、备份或物理缺失证明失败均拒绝。文件身份不可验证、硬链接也拒绝。

候选在源同目录写入并落盘；源/候选句柄拒绝其他写入与重命名，目录祖先保持句柄租约。
复制并验证原源 ACL/owner/group 后，先按源句柄将原文件移至独立备份操作目录的
`workspace.replaced.json`，再按候选句柄移入空出的源路径，均不覆盖现有目标。
这两步不是原子替换：中断可能使源路径暂时不存在；原始备份、被保留源、候选、意图与固定工具锁保留。
失败返回 RecoveryRequired，禁止跨备份目录盲目重试；T15 负责恢复复查/重试协调。

独立备份操作目录另存 `write-intent.json`、`write-applying.json`、`write-applied.json`，均 CreateNew，
不覆盖旧记录。意图含 schemaVersion/操作与清理计划/根指纹/原身份与哈希/候选哈希/尝试时间/备份记录哈希。
源 storages 下固定 `.dshcleaner-workspace-sync.lock` 与操作 GUID 候选用于阻止遗留操作被忽略；
成功复验后仅移除本次工具锁，不删除历史备份。中断不自动清除材料，也不自动恢复日志。

### Workspace 同步协调、恢复与重试（新计划 T15）

`WorkspaceSyncCoordinator.Run(cleanup, before, execution, requested, backupRoot, ct)` 返回
`WorkspaceSyncAttempt`：独立 `WorkspaceSyncOutcome`、本次元数据计划/已验证备份和最新后审计。
不再调用物理删除执行器。未请求时不扫描/备份/写元数据；请求时重新扫描后提议，只对原执行已成功且
重新证实不存在的会话调用备份和写入器。原物理结果与释放字节不受元数据结果影响。
`Retry(..., previous, ct)` 核对清理上下文，未请求不能转为重试；恢复未结清时拒绝。
结清后仍重新扫描、生成新操作及备份，不重用旧源快照、不把失败项因“后来不在”变成成功。

`WorkspaceRecoveryService.Inspect(plan, backup, ct)` 返回当下只读复查结果；
`Resolve(plan, backup, ct)` 是显式恢复动作，重新验证实际材料，不能靠缓存 Inspect 结果放行。
状态为 OriginalUnchanged、OriginalRetained、AppliedVerified 或阻断；结清后为 ResolvedOriginal / ResolvedApplied。
原源缺失时仅按保留源的已验证句柄恢复到空路径；已应用时只验证确认，不逆转；新出现或不同身份的源不覆盖。
备份、候选和标记均校验，备份文件持有拒绝并发改写/改名的读句柄；目录和源/候选/标记采用已验证租约。
候选与标记保留到备份操作目录的 recovery-candidate.json / recovery-lock.txt，不删除。
recovery-resolved.json 严格完整、非空、schema/操作/哈希绑定，写入后复验再退役标记。
恢复失败也可能已完成一个有界步骤，不能把 Resolved=false 解释为“源未变”；再次 Inspect 后处理。
未知材料/没有原操作上下文的孤立文件保持阻断，不猜测清除。任何步骤都不恢复会话日志。

OutcomeReport/ActionsPresenter 单独绑定 WorkspaceSyncOutcome，报告移除数量（未知不记零）和备份位置，
日志只记录闭合状态/错误码和数值，不记录完整路径、操作/会话 ID 或任意错误文本。
MainForm.OnCleanupFinished 当前明确记录 NotRequested，结果窗口已显示该独立结果；
T16已接入显式勾选、复查恢复和重试交互入口；默认不请求。当前允许项须同时通过A04发行及有效配置门禁。

### Workspace 可选同步与复查交互（新计划 T16）

`WorkspaceSyncCoordinator.GetOption(cleanup, before, backupRoot)` 是只读可用性检查，返回
`WorkspaceSyncOption`（计划、潜在引用数、强制备份根、可用性与禁用原因），不是写入权限。
CleanupDialog 增加默认未选的“同步清理 Workspace 元数据”，显示具体潜在引用数和独立备份目录；
只统计纳入完整日志目标的会话，最终移除数由实际删除/复查决定。目录可独立选择，不随会话 ZIP 开关失效。
不持久化同步勾选和该次备份目录，避免下次静默沿用意图。未验证版本、未知源、运行状态、路径、未结清操作等均禁用。
确认时重新检查，若已勾选但条件变化，则不开始物理删除，明确要求重新确认；不会静默改成无同步删除。

MainForm 在物理执行后传递冻结请求给协调器，再绑定独立结果和最新审计。结果窗口为失败/中断提供
“复查 / 恢复 / 重试同步”，打开绑定原根与计划的 WorkspaceSyncReviewDialog。
先只读复查，恢复须另行勾选明确确认；已结清后才允许重试。所有服务仍独立复验，不接受 UI 状态作为许可。
复查窗口打开时禁止切换根与开始新清理；重试生成独立结果日志（GUID 文件名），保留原日志和物理执行统计。
公开服务现已增加受控发行条件；Windows自动化另外通过本机安装资产与隔离合成配置调用公开协调器，验证成功同步和阻断。真实数据未执行写入测试，真实Harness未停止。

最终审计补充：完成标记已退役但调用者未收到结果时，恢复读取严格 `write-applied.json`，
操作ID/候选哈希/原尝试时间及当前原文件、备份、精确变换和缺失证明全通过，才认定 ResolvedApplied。
缺少/损坏完成记录不放行。该分支不会恢复日志或再次应用已完成元数据。

## A04 内部发行与配置核验

`WorkspaceReleaseReader.MatchesReviewedRelease`与`WorkspaceProductionGate.Read(dataHome)`均为internal只读接口；后者成功返回配置证据指纹，失败返回null，不输出凭据、不执行Harness模块。不存在公开force或调用者注入的发行允许清单。
`WorkspaceStoragePolicy.Allows(orderedLayers)`只组合数据，不执行表达式；具体支持边界见workspace-sync-design末节。
`WorkspaceWritePreparation.StorageBinding`为实例绑定的内部字段；Prepare保存，Apply重复核验，变化返回`SYNC_STORAGE_BINDING_CHANGED`，版本/配置不被允许返回`SYNC_PRODUCTION_VERSION_NOT_ALLOWED`。已经产生候选/操作材料后继续沿用RecoveryRequired规则，不清理证据。
公开Writer、Coordinator、Recovery保留原调用签名。`WorkspaceMetadataPlan.ProductionWriteContractIds`为只读条件支持清单，不是可写权限。内部合成探针仅允许已有受控夹具根，不能替换生产Read的发行及根验证。
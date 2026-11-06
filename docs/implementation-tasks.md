# 任务与验收索引

当前 0.2.4 交付已由用户于 2026-10-20 确认实机验收完成，详见 [当前验收记录](acceptance.md)。用户已授权按 [2026-10-20 新计划](change-plan-2026-10-20/00-总调度与验收.md) 实施及验收；编号 T01–T16 与历史编号分开。T01 文档契约已完成；T02 合成夹具已通过实际验收，证据见 `artifacts/evidence/change-plan-2026-10-20/T02.txt`；T03 保守回归已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T03.txt；T04 候选发现模型已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T04.txt；T05 安全校验已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T05.txt；T06 根选择与状态失效已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T06.txt；T07 引用索引已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T07.txt；T08 会话关联清单已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T08.txt；T09 删除前审计已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T09.txt；T10 删除后复查已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T10.txt；T11 会话报告已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T11.txt；T12 契约与内存提议已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T12.txt；T13 强制原始备份已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T13.txt；T14 安全写入器已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T14.txt；T15 部分失败、恢复与重试已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T15.txt；T16 可选同步/恢复交互已完成实施及合成验收，证据见 artifacts/evidence/change-plan-2026-10-20/T16.txt；[A01–A07 逐项审计](change-plan-2026-10-20/05-整体验收审计.md)已记录：8阶段强制终止模拟与观察脱敏样本已补齐；A04发行/配置门禁已通过独立验收，A01–A07技术复核已通过；用户于2026-10-21确认同意新增依赖，本次计划实施及验收关闭；未提交或发布新版。

历史唯一来源原样归档：

- [原始 T-01～T-34](../archive/2026-10-20/tree/docs/implementation-tasks.md)。
- [2026-10-20 修复清单](../archive/2026-10-20/tree/docs/2026-10-20-修复任务清单.md)：其编号仅在该文件内有效；最终 T-33～T-38 与旧版计划一并保留。
- [归档索引与校验清单](../archive/2026-10-20/README.md)。

历史任务的原始勾选与证据不重写；旧计划中未勾选条目不自动成为新待办。后续开发先根据用户新需求形成独立任务清单，明确范围、依赖、验收及证据，再按单任务规则实施。

本次维护任务 DOC-20261020：更新用户验收状态、归档历史变更/过程文件、校验哈希与文档引用，提交并推送。证据见 [维护记录](../artifacts/evidence/DOC-20261020.txt)。

## 新任务 ROOT-20261104：会话根标记契约调整（用户 2026-11-04 授权）

背景：目标机 `C:\Users\zx\.dsh` 含 `sessions`/`storages` 会话数据但从未使用附件，`attachments` 为 DSH 懒创建目录（开发机实测其创建时间晚于 sessions/storages 两个月），被现行为"三目录必备"整体判为 `ROOT_MARKERS_MISSING`，无法清理。

范围：`RootLocator` 会话根必备标记改为 `sessions`+`storages`；`attachments` 为可选标记——存在时仍经 `PathGuard` 复验（reparse/访问拒绝仍拒绝），缺失不影响有效性；同步更新选择对话框目录说明与 `interfaces.md` §2 标记契约。不改动桌面根标记、扫描/删除路径白名单与 `Resolve` 派生子根行为（缺失 `attachments` 由 `AttachmentInventoryService` 既有 `ATTACHMENT_ROOT_MISSING` 问题项降级处理）。

验收：[x] 编译通过；[x] 单元测试含"缺 attachments 仍有效且不创建该目录"与"attachments 为 reparse 仍拒绝"；[x] 原必备标记缺失（仅 sessions）仍 `ROOT_MARKERS_MISSING`；[x] 相关回归测试通过（单元测试 683 全过；WindowsTests `--roots-only` 全过；完整 UI 回归存在既有环境失败"报告窗口滚动条"，经未修改 HEAD 基线复跑确认为既有问题，与本任务无关）；[x] 证据落盘 `artifacts/evidence/ROOT-20261104.txt`。

## 新任务 BUILD-20261104：发布 v0.3.1（用户 2026-11-04 授权"需要构建程序"）

范围：以 `scripts/publish.ps1 -Version 0.3.1` 产出 `dist/v0.3.1/win-x64` 单文件框架依赖包（含 ROOT-20261104 修复）；发布包冒烟 `--smoke`；CHANGELOG 增补 0.3.1 条目；记录 SHA-256 与证据。不改代码；不执行目标机实机 GUI 验收（属用户实机验收）。

验收：[x] publish exit 0 且产物含 EXE/README/LICENSE/THIRD-PARTY-NOTICES/SHA256SUMS；[x] 发布 EXE `--smoke` exit 0（SMOKE_RESULT=PASS，144 DPI 原生）；[x] 版本资源为 0.3.1（FileVersion 0.3.1，ProductVersion 0.3.1+f596263）；[x] 证据落盘 `artifacts/evidence/release-0.3.1.txt`。

## 新任务清单 SIMPLIFY-20261104：复杂度纪律审查整改（用户 2026-11-04 授权）

背景：按 AGENTS.md §4 复杂度纪律对 src 三层逐行审查，A 级（真实缺陷）发现形成以下任务；B 级简化与 C 级待决项未列入，须另行授权。S01–S05 相互无依赖，按编号执行（S01 为用户可见缺陷优先）。

### S01 修复 LastActivityAt 死过滤器

- 目标：日期过滤可选项与实际生产的数据一致，用户选择后不再静默清空列表。
- 实施内容：`LastActivityAt` 全仓唯一赋值点为 null，但 UI 暴露该过滤项且过滤器恒 false。端到端删除该死字段：不发明"活动时间"语义（现有"文件修改时间"过滤已覆盖 mtime）；如未来需要按活动过滤，另立任务定义数据来源。
- 源码映射：`InventoryService.cs:120`（唯一赋值 null）、`SessionSummary.cs:41`（字段）、`SessionFilter.cs:12,81`（枚举与恒 false 分支）、`SettingsDialog.cs:19`（UI 选项）、`SettingsStore.cs:275,283`（设置 token 映射）、`ReadableExport.cs:240`（导出行；如 `interfaces.md` 列有该字段同步更新）。
- 验收：[x] 编译零警告；[x] 相关测试（InventoryTests/FilterTests/SettingsTests）同步更新后全过；[x] "创建时间/文件修改时间"过滤行为不变；[x] 旧 settings.json 含 `lastActivityAt` token 时加载不报错（未知 token 按既有策略处理）。
- 最小验证：`dotnet test --no-restore`（单元测试项目）。

### S02 收敛路径守卫与根指纹（五份发散拷贝）

- 目标：路径规范化/包含检查/根指纹在 Application 层单一实现；同一根经所有规划器与审计器得到相同指纹。
- 实施内容：新建 internal 静态类（如 `RootGuard`）收敛五处 `NormalizeRoot`、四处 `TryContain/TryNormalizeRelative`、四处 `ComputeRootFingerprint`；统一语义（驱动器根检查、Trim、UNC 处理取一套并在注释写明理由）；暴露命名指纹方法，替换五处 `new CleanupPlanner().Build(root, [], [])` 空计划预言机调用。指纹仅在单次操作内比对，格式变化不跨运行持久化。
- 源码映射：`BackupManifest.cs:461-471`、`CleanupPlanner.cs:~275-331`、`CacheCleanupSelection.cs:167-217`、`DesktopCleanupSelection.cs:151-203`、`AttachmentCleanupSelection.cs:139-190`；预言机调用：`PreCleanupAuditor.cs:16`、`PostCleanupAuditor.cs:22`、`WorkspaceSyncCoordinator.cs:33`、`WorkspaceMetadataWriter.cs:187`、`WorkspaceRecoveryService.cs:109`。
- 验收：[x] 编译零警告；[x] 新增测试：同一根集合经各规划器/审计器所得 `RootFingerprint` 一致；[x] 单元测试全过；[x] 净删除重复行数在提交信息中记录。
- 最小验证：`dotnet test --no-restore`。

### S03 删除生产死代码与仅测试消费的平行实现

- 目标：src 内无生产零调用者的公开 API。
- 实施内容：删除 `JsonlLineSplitter`（生产零调用；`SessionEventReader.ParseLines:147-183` 为实际实现，其测试改测 `ParseLines` 或删除）；删除 `HeadersConsistent`（生产走 `header.IdentityMatches`）；删除 `ISessionInventory`（调用方持具体类型）；删除 `ScanAsync`（`Task.FromResult` 假异步，零生产调用）；`BackupManifestBuilder.Verify`（90 行仅测试用）降为 internal 或删除；删除 `SourceContractId`/`SourceContractCommit` 死常量。
- 源码映射：`Compression/JsonlLineSplitter.cs`、`SessionHeaderReader.cs:105`、`InventoryService.cs:15-18,76-81`、`BackupManifest.cs:124-214`、`WorkspaceMetadataPlan.cs:17-23`；对应测试 `CompressionTests.cs`、`HeaderTests.cs`、`InventoryTests.cs:241`、`ManifestTests.cs:160-236`。
- 验收：[x] 编译零警告；[x] 测试项目同步更新后全过；[x] grep 证实上述符号在 src 零引用；[x] 提交信息记录删除符号清单与行数。
- 最小验证：`dotnet test --no-restore`。

### S04 去除同一时刻对同一不可变值的重复校验

- 目标：每个不可变值在同一调用链只校验一次；reparse/越界/访问拒绝的负向错误码逐项不变。
- 实施内容：`RootLocator` 删除 `Add` 内或 `ProbeCandidate` 内的重复 `InputError`（同一调用链三次）；删除 `ProbeCandidate:96-102` 手写逐段 reparse 走查（保留其后的 `PathGuard.Check`，走查已由 PathGuard 覆盖）；`CleanupExecutor.DeleteOne` 删除 `PathGuard.Check` 之后的二次 reparse 属性判断（保留 mtime/length/hash 复验，属不同维度）；`WindowStateStore` 一次 Save 内三次 `RejectReparse` 收敛为构造/首次访问一次；`WorkspaceBackupService.Paths` 删除对 backupRoot 子目录的受保护根二次检查（ValidateDestination 已覆盖）。
- 源码映射：`RootLocator.cs:61,93,96-107`、`CleanupExecutor.cs:125-141`、`WindowStateStore.cs:21,43,51`、`WorkspaceBackupService.cs:110-114`。
- 验收：[x] 编译零警告；[x] 既有负向测试（reparse 拒绝、越界拒绝、访问拒绝、受保护根拒绝）逐项通过且错误码不变；[x] 单元测试全过 + WindowsTests `--roots-only` 全过；[x] 提交信息记录删除的校验点清单。
- 最小验证：`dotnet test --no-restore`（单元 + roots-only）。

### S05 统一会话文件名白名单与代际解析正则

- 目标：会话文件名白名单判定与代际解析共用同一正则，消除改一漏一漂移。
- 实施内容：`PathGuard.IsSessionFileName` 改为返回匹配结果（含代际捕获组）或提供单一 `MatchSessionFileName`；`SessionLogDiscovery` 用一次匹配同时完成白名单判定与代际解析，删除并存的 `GenerationPattern` 第二正则。
- 源码映射：`PathGuard.cs:42-43`、`SessionLogDiscovery.cs:109,123`。
- 验收：[x] 编译零警告；[x] DiscoveryTests/HeaderTests 全过；[x] 新增测试：白名单通过与代际解析结果对同一文件名恒一致（含畸形代际数字用例）；[x] 单元测试全过。
- 最小验证：`dotnet test --no-restore`。

## 新任务清单 SIMPLIFY-20261106：B 级简化（用户 2026-11-06 全授权）

背景：SIMPLIFY-20261104 三层审查的 B 级（有收益证据的简化）发现，经 2026-11-06 逐项源码复核确认缺口真实存在。B01–B08 按风险从低到高编号执行；B03 与 B06 均触及 PathGuard，须先后执行；B08 依赖 B03/B04 完成后的错误码现状。所有任务不改行为语义与错误码值，§5 安全底线机制只收敛副本、不可移除。

### B01 AttachmentIndex diskIds 去重 O(n²) 改 HashSet

- 目标：附件索引构建对大量附件不再二次方退化；去重与保序行为不变。
- 实施内容：`diskIds` 的 `List.Contains` 去重与 `:142` 的 `diskIds.Contains` 查找改用 `HashSet<string>(StringComparer.Ordinal)` 配合原 List 保序；不改首次出现顺序语义。
- 源码映射：`AttachmentIndex.cs:113-118,142`。
- 验收：[x] 编译零警告；[x] AttachmentIndexTests 全过；[x] 提交信息记录改动前后复杂度说明。
- 最小验证：`dotnet test --no-restore`（AttachmentIndexTests）。

### B02 WorkspaceMetadataWriter.Apply 拆阶段

- 目标：约 90 行的 `Apply` 拆为私有阶段方法，最危险写入路径可分段诊断；行为、错误码、fault seam 顺序完全不变。
- 实施内容：按现有注释边界拆私有方法（建议：锁获取、候选构建写入、候选校验+复验、提交窗口（安全描述符复制+双 rename）、收尾）；`_fault` 调用点顺序与 `WorkspaceWritePoint` 枚举不动；不新增抽象层。
- 源码映射：`WorkspaceMetadataWriter.cs:63-~150`（`WorkspaceWritePoint` 各调用点为阶段边界）。
- 验收：[x] 编译零警告；[x] 既有 fault-injection/崩溃点测试逐项通过且各 `WorkspaceWritePoint` 触发顺序不变；[x] 单元测试全过 + WindowsTests `--roots-only` 全过；[x] 提交信息记录拆出的方法清单与行数守恒说明。
- 最小验证：`dotnet test --no-restore`（单元 + roots-only）。

### B03 祖先 reparse 走查收敛为 PathGuard 单一 helper

- 目标：祖先目录逐段 reparse 走查一处实现、7 处复用；各处错误码不变。S04 已证明 `PathGuard.Check` 不覆盖祖先，故新增命名方法（如 `CheckAncestors`），不并入 Check。
- 实施内容：在 PathGuard 实现"自根向下逐段（或自路径向上至根）检查现存祖先的 ReparsePoint"；替换以下各处的手写走查：`PostCleanupAuditor.cs:27-28`、`RootLocator.cs:94-101`（前缀组件式）、`SessionResidueInventory.cs:30-34`、`WorkspaceReferenceReader.cs:29-30`、`AttachmentOpener.cs:88-93`（创建路径式）、`WindowStateStore.cs:82-86`、`WorkspaceBackupService.cs:143`（备份根子目录式）。各处的返回形态（异常/错误码/Unknown）保留，仅走查循环收敛。
- 源码映射：上列 7 处 + `PathGuard.cs`。
- 验收：[ ] 编译零警告；[ ] 既有 reparse 负向测试逐项通过且错误码不变；[ ] 新增测试：同一祖先 junction 场景下 7 个入口全部拒绝（可复用现有夹具）；[ ] 单元测试全过 + WindowsTests `--roots-only` 全过；[ ] 提交信息记录替换点清单。
- 最小验证：`dotnet test --no-restore`（单元 + roots-only）。

### B04 字节限额读取实现统一

- 目标："读到上限即停/超限判定"单一实现；各文件数值常量保持现值不变（改数值属行为变更，不在本任务）。
- 实施内容：以 `SessionHeaderReader.ReadAtMost:161` 为基础提取共享 bounded-read helper（internal，放 Infrastructure 公共处），替换各读取器中重复的"缓冲+1 判定/流式截断"实现：`SessionEventReader.ParseLines:147-156`、`SessionTitleReader`（MaxLineBytes/MaxInspectBytes 判定处）、`WorkspaceJson`（MaxBytes 快照加载）、`WorkspaceReleaseReader`（MaximumHeader/MaximumEntry）、`AttachmentInventoryService`（MaxReferenceReadBytes 预检）、`PreviewPresenter`（MaxPreviewReadBytes）。`CompressionLimits.MaxLineBytes` 先例不动。
- 源码映射：上列各文件限额执行点（常量定义处仅引用不改值）。
- 验收：[ ] 编译零警告；[ ] 各读取器既有超限/截断负向测试逐项通过且错误码不变；[ ] 单元测试全过；[ ] 提交信息记录替换的读取实现清单。
- 最小验证：`dotnet test --no-restore`。

### B05 JSON 重复键检测单一机制

- 目标：JSON 对象重复属性检测只保留一种机制；不同文件同一输入判定一致。
- 实施内容：现存两机制：`WorkspaceJson.DuplicateField`（walker，4 消费者）与 `WorkspaceProductionGate.cs:125` 的 `AllowDuplicateProperties=false` 解析器选项。统一为 walker（解析器选项语义依赖 STJ 版本行为，不可控）；ProductionGate 改为加载后跑 `DuplicateField`。zip 条目重复（`BackupManifest` DUPLICATE_ENTRY、`WorkspaceReleaseReader.cs:109`）与会话 ID 重复属不同语义，不在本任务。
- 源码映射：`WorkspaceJson.cs:35-53`、`WorkspaceProductionGate.cs:125`。
- 验收：[ ] 编译零警告；[ ] 既有重复键负向测试（含 ProductionGate 路径）通过且错误码不变；[ ] 新增测试：同一含重复键 JSON 经两入口判定一致；[ ] 单元测试全过。
- 最小验证：`dotnet test --no-restore`。

### B06 桌面缓存目录白名单删除平行副本

- 目标：桌面缓存目录白名单全仓一份。现状为两份名单且注释自认"同名单"（改一漏一已被代码承认）。
- 实施内容：删除 `DesktopCleanupSelection.cs:35` 的副本，Application 层引用 Infrastructure `PathGuard.DesktopCacheDirs:38`（或反向收敛，取依赖方向合规的一侧）；两处 `Contains/Any` 判定复用同一比较语义（OrdinalIgnoreCase）。
- 源码映射：`DesktopCleanupSelection.cs:34-35,98`、`PathGuard.cs:38,52`。
- 验收：[ ] 编译零警告；[ ] DesktopCleanupTests 全过（白名单五目录行为不变）；[ ] grep 证实名单仅一处定义；[ ] 单元测试全过。
- 最小验证：`dotnet test --no-restore`（DesktopCleanupTests）。

### B07 备份证据 DTO 收敛为能力对象

- 目标：`WorkspaceBackupEvidence` 不可由公开构造伪造（备份校验通过的事实只能由 WorkspaceBackupService 产生），对齐 `WorkspaceWritePreparation` 既有模式。
- 实施内容：`WorkspaceBackupEvidence` 记录类型保留，构造器降 internal（消费者均在 Infrastructure，测试经 InternalsVisibleTo 可达）；grep 确认 src 内无 Infrastructure 之外的构造调用；不改字段与消费逻辑。范围仅此一个 DTO：`ResolvedRoots` 等其余公开 DTO 是否同改待本任务实测后另行决定，不预先扩大。
- 源码映射：`WorkspaceBackupEvidence.cs:4-8`；消费者 `WorkspaceBackupService.cs:86,95`、`WorkspaceMetadataWriter.cs:35,175`、`WorkspaceRecoveryService.cs:41,55,104,113,211`、`WorkspaceSyncCoordinator.cs:64,117`、`WorkspaceWritePreparation.cs:8,12`。
- 验收：[ ] 编译零警告；[ ] 单元测试全过 + WindowsTests `--roots-only` 全过；[ ] 提交信息说明威胁模型（伪造备份证据进入写链的可构造路径）与证据等级（可构造）。
- 最小验证：`dotnet test --no-restore`（单元 + roots-only）。

### B08 错误码常量折叠

- 目标：同名错误码字符串全仓单一定义点；`OutcomeReport.AuditLogCodes` 白名单由常量组装而非重复字面量。
- 实施内容：新建 internal 静态错误码常量类（按族分组），折叠已确认重复：`ZSTD_TRUNCATED`（`SessionEventReader.cs:58` 与 `SessionHeaderReader.cs:53` 两处定义）；将 `OutcomeReport.cs:62-80` 白名单中的字面量替换为常量引用；各选择器 `*DuplicateReason` 等仅单处定义但被白名单重复字面量引用的码同步引用常量。错误码字符串值一律不改。
- 源码映射：`SessionEventReader.cs:58`、`SessionHeaderReader.cs:53`、`OutcomeReport.cs:62-80`、各 `*Reason`/`Code*` 常量定义处。
- 验收：[ ] 编译零警告；[ ] 单元测试全过；[ ] 新增测试：AuditLogCodes 覆盖全部已定义错误码常量（反射或清单断言）；[ ] 提交信息记录折叠的重复定义清单。
- 最小验证：`dotnet test --no-restore`。
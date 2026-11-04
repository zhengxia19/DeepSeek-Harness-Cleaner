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
- 验收：[ ] 编译零警告；[ ] DiscoveryTests/HeaderTests 全过；[ ] 新增测试：白名单通过与代际解析结果对同一文件名恒一致（含畸形代际数字用例）；[ ] 单元测试全过。
- 最小验证：`dotnet test --no-restore`。
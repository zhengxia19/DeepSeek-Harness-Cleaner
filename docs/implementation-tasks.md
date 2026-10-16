# 原子实施任务清单（Implementation Tasks）

状态：**全部待执行**。总任务数：34。方案 B 已确认。本清单可直接作为本地 Agent 的实施依据，但任务完成状态必须基于执行证据，不能由模型主观判断。

## A. 强制执行规则

1. 从 T-01 顺序读取任务，但只在明确满足依赖条件时执行；当前阶段允许对独立模块实现进行并行设计，实际修改同一文件不得并发。
2. **每次只领取一个最小任务**；严格限定“修改范围”和“任务边界”，不得顺手实施后续功能。
3. 执行前检查本任务依赖的全部前置任务在清单中已勾选、已写验收证据；缺一不可。
4. 完成改动后执行任务对应的自动测试与必须的负向测试，记录命令、退出码、commit hash、文件列表及证据路径到 `artifacts/evidence/T-XX.txt`。
5. **只有所有验收判据满足，复核人/Agent 确认证据真实后，才能把 `[ ]` 改为 `[x]`，并写入完成日期与证据路径**；否则维持 `[ ]`，并记 `BLOCKED` / `FAILED` 及原因；禁止跳过依赖任务。
6. 开发期对真实 `%USERPROFILE%\.dsh` 只能只读；除用户明确另行授权外，不得对真实历史执行自动化写入或删除。所有删除验证使用合成 fixture。
7. 危险操作验收不允许以“运行没有报错”代替；必须对**未选数据/根外哨兵的 hash 不变**给出断言。
8. 不要求多个相同的兼容性 PoC 重复完成：T-05 物理格式验证、T-13 语义映射验证、T-33 目标系统回归各司其职。

## B. Agent 每项任务输出模板

```text
任务：T-XX
前置任务：T-YY [x]；证据文件：...
变更文件：...
实施摘要：...
执行命令：...
退出码：0 / 非0
验收项：逐条 PASS / FAIL
负向测试：PASS / FAIL
提交 Hash：...
证据：artifacts/evidence/T-XX.txt
结论：ACCEPTED / FAILED / BLOCKED
```

## C. 原子任务

### T-01 — 初始化仓库骨架

- [x] **T-01：初始化仓库骨架** — 完成于 2026-10-16，证据 `artifacts/evidence/T-01.txt`，commit `6cc56f1`。
- **前置**：无。
- **修改范围**：`根目录 README/CHANGELOG/CONTRIBUTING、docs/、src/、tests/、scripts/、.gitignore`。
- **任务边界**：只创建设计约定的目录和 Git 忽略规则；不安装 SDK，不实现功能。
- **验收标准**：目录存在，`.dotnet/.nuget/.artifacts/dist` 被忽略，`git status` 无误添加构建物。
- **关联测试**：TC-17；验收证据保存为 `artifacts/evidence/T-01.txt`，通过后方可勾选。


### T-02 — 实现本地 SDK 初始化脚本

- [x] **T-02：实现本地 SDK 初始化脚本** — 完成于 2026-10-16，证据 `artifacts/evidence/T-02.txt`，commit `9815afc`。
- **前置**：T-01。
- **修改范围**：`scripts/bootstrap.ps1、scripts/enter-env.ps1、global.json`。
- **任务边界**：用官方安装脚本在项目 `.dotnet` 安装经核验稳定版 .NET 10 SDK；固定精确版本；当前会话设置 DOTNET_ROOT/PATH/NUGET_PACKAGES/DOTNET_CLI_HOME；不使用 setx。
- **验收标准**：在未安装全局 SDK 的用户模式 shell 运行 dotnet --info 成功；退出 shell 后系统 PATH 与 NuGet 全局目录未变；SDK 版本与 global.json 一致。
- **关联测试**：TC-17；验收证据保存为 `artifacts/evidence/T-02.txt`，通过后方可勾选。


### T-03 — 生成解决方案与依赖锁

- [x] **T-03：生成解决方案与依赖锁**（2026-10-16，证据 artifacts/evidence/T-03.txt，feat 55744e0）
- **前置**：T-02。
- **修改范围**：`DeepSeekHarnessCleaner.sln、Directory.Build.props、Directory.Packages.props、NuGet.Config、src/ 四项目、tests/ 项目`。
- **任务边界**：创建 Domain/Application/Infrastructure/UI 和 xUnit；添加 ZstdSharp.Port；集中锁定依赖，开启编译警告为错误。
- **验收标准**：dotnet restore 首次生成锁文件并提交；dotnet build 成功且 UI 项目 target=net10.0-windows；不引用不必要包。
- **关联测试**：TC-17；验收证据保存为 `artifacts/evidence/T-03.txt`，通过后方可勾选。


### T-04 — 构造隔离数据夹具

- [x] **T-04：构造隔离数据夹具**（2026-10-17，证据 artifacts/evidence/T-04.txt，test 4125dac）
- **前置**：T-03。
- **修改范围**：`tests/Fixtures/**（及相关 fixture 工厂）`。
- **任务边界**：构造日志多版本、多个 generation、无效 JSON、日期、附件与安全目录 fixtures；全部位于测试临时根；敏感信息仅合成。
- **验收标准**：测试运行能创建/清理 fixtures 且不访问真实 `%USERPROFILE%\.dsh`；覆盖清单在测试数据 README 中列出。
- **关联测试**：TC-02,TC-03,TC-04；验收证据保存为 `artifacts/evidence/T-04.txt`，通过后方可勾选。


### T-05 — 验证 Zstd 多帧物理解码

- [x] **T-05：验证 Zstd 多帧物理解码**（2026-10-16，证据 artifacts/evidence/T-05.txt，feat 26d5e13）
- **前置**：T-04。
- **修改范围**：`Infrastructure/Compression/**、tests/CompressionTests.cs`。
- **任务边界**：编写可复用流式解压适配器，验证连接帧、CRC、截断尾帧、空帧、超限的差异；无需语义投影。
- **验收标准**：多帧输出字节逐个一致；截断/损坏不可被当成正常成功；8MiB 行/256MiB 解压量保护在测试中触发。
- **关联测试**：TC-04,TC-20；验收证据保存为 `artifacts/evidence/T-05.txt`，通过后方可勾选。


### T-06 — 定位授权数据根

- [x] **T-06：定位授权数据根**（2026-10-16，证据 artifacts/evidence/T-06.txt，feat 00e6101）
- **前置**：T-03。
- **修改范围**：`Infrastructure/RootLocator.cs、Domain/ResolvedRoots.cs、tests/RootLocatorTests.cs`。
- **任务边界**：使用 USERPROFILE/APPDATA/可选 DSH_HOME 显式根，识别会话/存储/附件/桌面目录并返回状态。
- **验收标准**：默认、自定义、不存在、非 Harness 目录均按合同响应；禁止在扫描过程中创建源目录。
- **关联测试**：TC-01；验收证据保存为 `artifacts/evidence/T-06.txt`，通过后方可勾选。


### T-07 — 实现路径白名单验证

- [x] **T-07：实现路径白名单验证**（2026-10-16，证据 artifacts/evidence/T-07.txt，feat e3ac223）
- **前置**：T-06。
- **修改范围**：`Infrastructure/PathGuard.cs、tests/PathGuardTests.cs`。
- **任务边界**：建立根包含校验、白名单与 reparse 检查，拒绝 UNC、设备路径、ADS、项目目录及根目录。
- **验收标准**：..、junction、symbolic link、重命名路径均被拒绝；独立目录外 sentinel 的 SHA 绝不变化。
- **关联测试**：TC-15,TC-20；验收证据保存为 `artifacts/evidence/T-07.txt`，通过后方可勾选。


### T-08 — 发现会话物理候选

- [x] **T-08：发现会话物理候选**（2026-10-16，证据 artifacts/evidence/T-08.txt，feat 2a64660）
- **前置**：T-07。
- **修改范围**：`Infrastructure/SessionLogDiscovery.cs、tests/DiscoveryTests.cs`。
- **任务边界**：扫描规范命名的会话日志并提取 generation/编码，列出异常/冲突文件。
- **验收标准**：v0、vN、明文与 zstd 文件均识别；同代双编码冲突/不规则目录标记异常，且无写入。
- **关联测试**：TC-03；验收证据保存为 `artifacts/evidence/T-08.txt`，通过后方可勾选。


### T-09 — 解析受限日志头部

- [x] **T-09：解析受限日志头部**（2026-10-16，证据 artifacts/evidence/T-09.txt，feat fc4a3aa）
- **前置**：T-05,T-08。
- **修改范围**：`Infrastructure/SessionHeaderReader.cs、tests/HeaderTests.cs`。
- **任务边界**：只读取首条受限 header，验证 id、formatVersion、createdAt/cwd；不可验证标异常。
- **验收标准**：有效 v4/旧版 fixture 提取准确；畸形/缺失/冲突 header 不进入可删白名单；源字节不变。
- **关联测试**：TC-03,TC-04；验收证据保存为 `artifacts/evidence/T-09.txt`，通过后方可勾选。


### T-10 — 生成会话库存与磁盘统计

- [x] **T-10：生成会话库存与磁盘统计**（2026-10-16，证据 artifacts/evidence/T-10.txt，feat 3d9ec8e）
- **前置**：T-09。
- **修改范围**：`Application/InventoryService.cs、Domain/SessionSummary.cs、tests/InventoryTests.cs`。
- **任务边界**：按经验证身份把多 generation 归并一条会话，汇总文件大小和异常状态。
- **验收标准**：多代同会话只记一条逻辑记录；容量按所有物理文件求和且与 fixture 文件大小相符；不因单条损坏中断扫描。
- **关联测试**：TC-05；验收证据保存为 `artifacts/evidence/T-10.txt`，通过后方可勾选。


### T-11 — 实现会话日期与搜索筛选

- [x] **T-11：实现会话日期与搜索筛选**（2026-10-16，证据 artifacts/evidence/T-11.txt，feat e604257）
- **前置**：T-10。
- **修改范围**：`Application/SessionFilter.cs、tests/FilterTests.cs`。
- **任务边界**：实现 createdAt/lastActivityAt/文件修改日期、项目/ID/关键字过滤，包含式日期范围；缺失值标 Unknown。
- **验收标准**：跨月/跨年/不同偏移时区、start=end、日期为空时断言一致；不裁剪事件内容。
- **关联测试**：TC-07,TC-08；验收证据保存为 `artifacts/evidence/T-11.txt`，通过后方可勾选。


### T-12 — 实现安全 JSONL 事件流

- [x] **T-12：实现安全 JSONL 事件流**（2026-10-16，证据 artifacts/evidence/T-12.txt，feat 577e074）
- **前置**：T-05,T-09。
- **修改范围**：`Infrastructure/SessionEventReader.cs、tests/EventReaderTests.cs`。
- **任务边界**：消费解压字节逐行解析 JSON，限制总量、行长，返回行号/状态/未知事件，不修改日志。
- **验收标准**：有效 JSONL 行数与顺序一致；无效行明确报损坏/部分；取消与超限不泄漏句柄。
- **关联测试**：TC-04,TC-20；验收证据保存为 `artifacts/evidence/T-12.txt`，通过后方可勾选。


### T-13 — 实现 V4 可验证消息预览

- [x] **T-13：实现 V4 可验证消息预览**（2026-10-16，证据 artifacts/evidence/T-13.txt，feat 9fdb62e）
- **前置**：T-12。
- **修改范围**：`Application/TranscriptProjector.cs、tests/TranscriptV4Tests.cs`。
- **任务边界**：仅将已从 Harness 0.2.0.0 脱敏 fixture 确证的用户/助手文本事件转成 PreviewItem。
- **验收标准**：角色顺序/纯文本与 fixture 金样一致；缺字段、非文本、未知 schema 标记不完整；不展示推理/密钥。
- **关联测试**：TC-06；验收证据保存为 `artifacts/evidence/T-13.txt`，通过后方可勾选。


### T-14 — 实现旧版本预览安全退化

- [ ] **T-14：实现旧版本预览安全退化**
- **前置**：T-12。
- **修改范围**：`Application/LegacyPreview.cs、tests/LegacyPreviewTests.cs`。
- **任务边界**：为 v0-v3 提供元数据和已验证类型的保守摘要；不实现完整官方迁移；最高代际失败不隐式回退。
- **验收标准**：旧版未知结构显示“不完整/仅元数据”；无法解析时仍支持原始备份；不存在虚构的消息。
- **关联测试**：TC-06,TC-20；验收证据保存为 `artifacts/evidence/T-14.txt`，通过后方可勾选。


### T-15 — 建立附件引用证据索引

- [ ] **T-15：建立附件引用证据索引**
- **前置**：T-12。
- **修改范围**：`Application/AttachmentIndex.cs、tests/AttachmentIndexTests.cs`。
- **任务边界**：仅从已验证事件/路径建立引用；解析缺口时状态 Unknown，不自动标记孤儿。
- **验收标准**：多会话共享附件仍为 referenced；任一未解析会话使全局孤儿结论不可得；无自动删除。
- **关联测试**：TC-14；验收证据保存为 `artifacts/evidence/T-15.txt`，通过后方可勾选。


### T-16 — 识别单会话投影缓存

- [ ] **T-16：识别单会话投影缓存**
- **前置**：T-07,T-10。
- **修改范围**：`Infrastructure/SessionCacheIndex.cs、tests/CacheIndexTests.cs`。
- **任务边界**：按 <id>.json 命名和目录布局关联专属缓存，只建只读索引，不修改 workspace.json。
- **验收标准**：匹配与不匹配缓存分别标记；cache 文件保持不变；无法确认映射则 Unknown。
- **关联测试**：TC-12；验收证据保存为 `artifacts/evidence/T-16.txt`，通过后方可勾选。


### T-17 — 建立 WinForms 单窗口布局

- [ ] **T-17：建立 WinForms 单窗口布局**
- **前置**：T-03。
- **修改范围**：`UI/MainForm.*、UI/Program.cs`。
- **任务边界**：建立菜单/导航、列表区、预览区、状态栏、按钮与尺寸/DPI 行为（可无数据）。
- **验收标准**：Win10 窗口启动退出，125%/150% DPI 不遮挡主操作按钮；UI 可用键盘导航。
- **关联测试**：TC-17,TC-19；验收证据保存为 `artifacts/evidence/T-17.txt`，通过后方可勾选。


### T-18 — 绑定会话列表/搜索/日期选择

- [ ] **T-18：绑定会话列表/搜索/日期选择**
- **前置**：T-11,T-17。
- **修改范围**：`UI/InventoryPresenter.cs、UI/MainForm.*`。
- **任务边界**：扫描后台执行；绑定 DataGridView、日期/项目/文本过滤和多选；显示总量。
- **验收标准**：fixture 下列表/统计/筛选与服务结果一致；UI 不冻结；多选不因排序指向别的会话。
- **关联测试**：TC-05,TC-07,TC-08；验收证据保存为 `artifacts/evidence/T-18.txt`，通过后方可勾选。


### T-19 — 绑定会话预览与解析状态

- [ ] **T-19：绑定会话预览与解析状态**
- **前置**：T-13,T-14,T-18。
- **修改范围**：`UI/PreviewPresenter.cs、UI/MainForm.*`。
- **任务边界**：选中会话触发后台受限预览，显示 role、文本、来源与 warning。
- **验收标准**：V4 金样显示正确；未知旧版显示提示而非空白/崩溃；取消/重选不串会话。
- **关联测试**：TC-06,TC-20；验收证据保存为 `artifacts/evidence/T-19.txt`，通过后方可勾选。


### T-20 — 实现独立设置的保存与读取

- [ ] **T-20：实现独立设置的保存与读取**
- **前置**：T-17。
- **修改范围**：`Infrastructure/SettingsStore.cs、UI/SettingsDialog.cs、tests/SettingsTests.cs`。
- **任务边界**：schemaVersion=1、记忆路径布尔与窗口配置；损坏配置只读回退默认。
- **验收标准**：rememberRoots=false 时配置不含历史绝对路径；未知 schema 不覆写；读/写正常。
- **关联测试**：TC-19；验收证据保存为 `artifacts/evidence/T-20.txt`，通过后方可勾选。


### T-21 — 实现 ZIP manifest 与清单签名

- [ ] **T-21：实现 ZIP manifest 与清单签名**
- **前置**：T-07,T-10。
- **修改范围**：`Application/BackupManifest.cs、tests/ManifestTests.cs`。
- **任务边界**：从选中会话构建 ZIP 相对路径/长度/SHA 预期清单；覆盖所有已识别代际，去重并拒绝 zip-slip。
- **验收标准**：所有 generation 被计入；出现 ..、重复条目、跨根或未知文件时拒绝，manifest 校验可复算。
- **关联测试**：TC-09；验收证据保存为 `artifacts/evidence/T-21.txt`，通过后方可勾选。


### T-22 — 实现原始 ZIP 创建与校验

- [ ] **T-22：实现原始 ZIP 创建与校验**
- **前置**：T-21。
- **修改范围**：`Infrastructure/BackupService.cs、tests/BackupTests.cs`。
- **任务边界**：流式写临时 zip，校验 entry 字节 SHA/数量/长度后无覆盖发布；失败移除临时 zip，返回 VerifiedBackup。
- **验收标准**：损坏/磁盘满/目标已有文件/进程取消时不返回成功；完成 ZIP 解压后每份原文件与原 SHA 一致。
- **关联测试**：TC-09,TC-20；验收证据保存为 `artifacts/evidence/T-22.txt`，通过后方可勾选。


### T-23 — 实现 Markdown/JSON 可读导出

- [ ] **T-23：实现 Markdown/JSON 可读导出**
- **前置**：T-13,T-14。
- **修改范围**：`Application/ReadableExport.cs、tests/ReadableExportTests.cs`。
- **任务边界**：离线导出可读消息并标记 event/附件缺口；不将其作为恢复凭据。
- **验收标准**：UTF-8 中文、换行、代码块不破坏结构；未知内容有 `exportIncomplete` 标记；输出不联网。
- **关联测试**：TC-10；验收证据保存为 `artifacts/evidence/T-23.txt`，通过后方可勾选。


### T-24 — 生成会话精确清理计划

- [ ] **T-24：生成会话精确清理计划**
- **前置**：T-07,T-10,T-16,T-21。
- **修改范围**：`Application/CleanupPlanner.cs、tests/CleanupPlanTests.cs`。
- **任务边界**：由已验证会话 ID 与用户选择集合产生逐文件计划，包含所有该会话 generation、可验证的专属缓存。
- **验收标准**：未选会话、异常格式、身份冲突、未知文件和根外路径不进入计划；按日期仅整会话；计划带指纹。
- **关联测试**：TC-08,TC-11；验收证据保存为 `artifacts/evidence/T-24.txt`，通过后方可勾选。


### T-25 — 实现 Harness 退出状态检查

- [ ] **T-25：实现 Harness 退出状态检查**
- **前置**：T-03。
- **修改范围**：`Infrastructure/HarnessProcessProbe.cs、tests/ProcessProbeTests.cs`。
- **任务边界**：通过已知 exe 和可验证的进程路径检测 Harness/可能关联执行者；无法判定时返回 Unknown。
- **验收标准**：模拟 Active/Inactive/Unknown，后两类仅 Inactive 可进入写入；不得主动停止进程。
- **关联测试**：TC-15；验收证据保存为 `artifacts/evidence/T-25.txt`，通过后方可勾选。


### T-26 — 实现备份门禁与逐文件删除执行器

- [ ] **T-26：实现备份门禁与逐文件删除执行器**
- **前置**：T-22,T-24,T-25。
- **修改范围**：`Infrastructure/CleanupExecutor.cs、tests/CleanupExecutorTests.cs`。
- **任务边界**：在 verified backup + 用户确认后重新检查进程、路径、源文件 SHA/mtime，逐项删除并收集 ItemOutcome。
- **验收标准**：未备份、ZIP 无效、变化、活动进程、reparse、权限不足均零越界删除；部分成功报告真实；不改未选 fixture。
- **关联测试**：TC-11,TC-15,TC-16；验收证据保存为 `artifacts/evidence/T-26.txt`，通过后方可勾选。


### T-27 — 实现会话投影缓存的独立清理计划

- [ ] **T-27：实现会话投影缓存的独立清理计划**
- **前置**：T-07,T-16,T-26。
- **修改范围**：`Application/CacheCleanupSelection.cs、tests/CacheCleanupTests.cs`。
- **任务边界**：单会话仅精确匹配；全量必须显式选择；用同一安全 executor 通道，缓存删除不要求会话 ZIP。
- **验收标准**：workspace.json 不被列入；全量和单会话范围无误；运行中/路径不安全仍被阻断。
- **关联测试**：TC-12；验收证据保存为 `artifacts/evidence/T-27.txt`，通过后方可勾选。


### T-28 — 实现桌面缓存/日志清理计划

- [ ] **T-28：实现桌面缓存/日志清理计划**
- **前置**：T-07,T-26。
- **修改范围**：`Application/DesktopCleanupSelection.cs、tests/DesktopCleanupTests.cs`。
- **任务边界**：仅按固定 5 个 cache 目录与 logs，日志支持文件修改日期；不修改其他桌面数据。
- **验收标准**：Local Storage/Session Storage/Network 在所有组合下 hash 不变；误选根被拒绝。
- **关联测试**：TC-13；验收证据保存为 `artifacts/evidence/T-28.txt`，通过后方可勾选。


### T-29 — 实现显式附件备份与独立清理

- [ ] **T-29：实现显式附件备份与独立清理**
- **前置**：T-15,T-22,T-26。
- **修改范围**：`Application/AttachmentCleanupSelection.cs、tests/AttachmentCleanupTests.cs`。
- **任务边界**：列表中人工选择普通附件文件；强制源文件 ZIP 备份及确认，不随会话自动删除。
- **验收标准**：未知引用状态明确标 Unknown；共享附件不会自动加入；未备份不得删除；非普通文件拒绝。
- **关联测试**：TC-14；验收证据保存为 `artifacts/evidence/T-29.txt`，通过后方可勾选。


### T-30 — 集成 UI 备份/清理及二次确认

- [ ] **T-30：集成 UI 备份/清理及二次确认**
- **前置**：T-18,T-19,T-20,T-22,T-23,T-26,T-27,T-28,T-29。
- **修改范围**：`UI/ActionsPresenter.cs、UI/BackupDialog.cs、UI/CleanupDialog.cs`。
- **任务边界**：展示最终清单/备份状态/警告；用户确认后才调用 executor；后台进度可取消。
- **验收标准**：点击取消不删除，未备份的会话删除按钮禁用；完成刷新清单，过程未阻塞 UI。
- **关联测试**：TC-09,TC-11,TC-16；验收证据保存为 `artifacts/evidence/T-30.txt`，通过后方可勾选。


### T-31 — 实现不泄露正文的执行报告

- [ ] **T-31：实现不泄露正文的执行报告**
- **前置**：T-26,T-30。
- **修改范围**：`Application/OutcomeReport.cs、UI/ResultsDialog.cs、tests/OutcomeTests.cs`。
- **任务边界**：显示每项成功/跳过/失败、实际释放字节、备份位置和错误码；工具日志仅记录脱敏结构化信息。
- **验收标准**：模拟部分失败时 UI 和日志一致；检查日志中无 fixture 正文、secret、敏感 token。
- **关联测试**：TC-16,TC-18；验收证据保存为 `artifacts/evidence/T-31.txt`，通过后方可勾选。


### T-32 — 执行完整安全与功能回归

- [ ] **T-32：执行完整安全与功能回归**
- **前置**：T-31。
- **修改范围**：`tests/**、artifacts/evidence/T-32-*`。
- **任务边界**：运行所有可自动执行的 TC-01..20 子用例及 SEC-01..03；TC-17 中的实机发布部分与 TC-18 的运行时联网检查交由 T-33；核对 REQ→TC→任务映射，缺陷另列整改项。
- **验收标准**：自动化与安全用例全部通过，未完成的现场用例明确列为 T-33 待验证；保留命令/时间/commit/结果；危险行为无豁免，失败不得继续 T-33。
- **关联测试**：TC-01..TC-20；验收证据保存为 `artifacts/evidence/T-32.txt`，通过后方可勾选。


### T-33 — 执行性能与 Win10 兼容性验证

- [ ] **T-33：执行性能与 Win10 兼容性验证**
- **前置**：T-32。
- **修改范围**：`scripts/perf.ps1、scripts/smoke-win10.ps1、artifacts/evidence/T-33-*`。
- **任务边界**：1000 样本性能测试和真实 Win10 Edition/Build 自包含 UI 冒烟，以及 TC-18 运行时联网检查；仅只读使用脱敏 0.2.0.0 样本。
- **验收标准**：PERF-01/COMPAT-01/WIN10-01 有计时和系统信息；若不满足发布门槛则停工修正并重跑回归。
- **关联测试**：TC-17,TC-18；验收证据保存为 `artifacts/evidence/T-33.txt`，通过后方可勾选。


### T-34 — 制作发布包并最终复核

- [ ] **T-34：制作发布包并最终复核**
- **前置**：T-33。
- **修改范围**：`scripts/publish.ps1、docs/release.md、README.md、CHANGELOG.md、dist/v0.1.0/**`。
- **任务边界**：按 release 清单发布，自包含 win-x64、SHA256SUMS、依赖报告、已知限制；归档测试证据。
- **验收标准**：Release 放行项目全部通过、dist 无真实会话与开发缓存、干净 Win10 VM 可运行、版本与 SHA 记录准确。
- **关联测试**：TC-17,TC-20；验收证据保存为 `artifacts/evidence/T-34.txt`，通过后方可勾选。

## D. 阶段门禁

- M1（T-01～T-05）：隔离 SDK、测试夹具与 Zstd 物理解码已核验，才能声明“具备读取基础”。
- M2（T-06～T-16）：根验证、会话索引、时间与预览、缓存/附件关系的只读能力已验收，才能启动会话删除设计。
- M3（T-17～T-23）：交互界面、配置与**原始 ZIP 备份完整性**已通过，不能以 Markdown 导出代替备份。
- M4（T-24～T-31）：清理执行必须遵守路径/进程/备份三门禁且可报告部分失败。
- M5（T-32～T-34）：安全回归、Win10 实机兼容和发布门槛全通过方可宣称 0.1.0 可发布。

## E. 依赖与重复任务审查结果

- 无“既写清理引擎又再写第二清理引擎”：T-26 唯一执行器，T-27/T-28/T-29 只定义不同对象的计划与策略。
- T-09 只读受限 header，T-12 处理完整行流，T-13 仅处理语义投影；各自具有不同的输入和验收范围。
- T-21 只定义 manifest / 选中文件映射，T-22 负责 ZIP I/O 和归档检验；没有两份备份实现。
- T-32 是全套测试门禁，不重复重新设计各模块；T-33 验证性能/OS 环境，不重复 T-05 的压缩库 PoC。

## F. 最终验收闭环

任务状态记录与代码同步提交；`review-report.md` 记录设计审查，代码交付前必须新增实际测试结果。**没有测试证据的 `[x]` 为无效完成；任何关键测试失败即不能关闭对应任务或进入发布。**

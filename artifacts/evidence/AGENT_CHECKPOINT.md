# AGENT 检查点

更新时间：2026-10-17（T-24 轮）
Git HEAD：docs(T-24) 勾选提交（其前为 feat(T-24)；上上基线 3592948 docs(T-23)）

## 已完成（已验收 [x]）
- T-01 仓库基线（6cc56f1）
- T-02 本地 SDK 引导：.dotnet 10.0.402 + global.json + scripts/enter-env.ps1（9815afc）
- T-03 解决方案与依赖锁：sln + 5 项目 + CPM + ZstdSharp.Port 0.8.8 + packages.lock.json（55744e0）
- T-04 隔离数据夹具：tests/Fixtures/{FixtureRoot,FixtureFactory}（4125dac）
- T-05 Zstd 多帧物理解码：Infrastructure/Compression/*（26d5e13）
- T-06 定位授权数据根：Domain/ResolvedRoots.cs + Infrastructure/RootLocator.cs（00e6101）
- T-07 路径白名单验证：Infrastructure/PathGuard.cs（e3ac223）
- T-08 会话物理候选发现：Infrastructure/SessionLogDiscovery.cs（2a64660）
- T-09 受限日志头部读取：Infrastructure/SessionHeaderReader.cs（fc4a3aa，证据 T-09.txt）
- T-10 会话库存与磁盘统计：Domain/SessionSummary.cs + Application/InventoryService.cs + tests/InventoryTests.cs（10 用例）；全量 76/76（3d9ec8e，证据 T-10.txt）
- T-11 会话日期与搜索筛选：Application/SessionFilter.cs + tests/FilterTests.cs（11 用例）；全量 87/87（e604257，证据 T-11.txt）
- T-12 安全 JSONL 事件流：Infrastructure/SessionEventReader.cs + tests/EventReaderTests.cs（13 用例）；全量 100/100（577e074，证据 T-12.txt）
- T-13 V4 可验证消息预览：Application/TranscriptProjector.cs + tests/TranscriptV4Tests.cs（11 用例）；全量 111/111（9fdb62e，证据 T-13.txt）
- T-14 旧版本预览安全退化：Application/LegacyPreview.cs + tests/LegacyPreviewTests.cs（11 用例）；全量 122/122（4610b45，证据 T-14.txt）
- T-15 附件引用证据索引：Application/AttachmentIndex.cs + tests/AttachmentIndexTests.cs（10 用例）；全量 132/132（ddb5093，证据 T-15.txt）
- T-16 单会话投影缓存只读索引：Infrastructure/SessionCacheIndex.cs + tests/CacheIndexTests.cs（10 用例）；全量 142/142（eaeb8a6，证据 T-16.txt）
- T-17 WinForms 单窗口布局：UI/MainForm.cs（菜单/列表/预览/操作按钮/StatusStrip，Dock+AutoSize+AutoScaleMode.Font+PerMonitorV2，删除按钮初始禁用）、UI/MainForm.Smoke.cs（`--smoke <文件>` 自检）、UI/Program.cs（smoke 退出码 0/1/2）；冒烟全 PASS（12a644f，证据 T-17.txt）。坑：SplitContainer 默认宽 ~150，须先设 Size 再设 MinSize/SplitterDistance
- T-18 绑定会话列表/搜索/日期选择：UI/InventoryPresenter.cs（UiScanSource 适配器=测试侧 ScanSource 同构+LastScanThreadId；InventoryPresenter.ScanAsync=Task.Run 后台扫描、ApplyFilter=SessionFilter.Apply、BuildStatusText 总量文本）、MainForm.cs（ListView→DataGridView 7 列、行 Tag=SessionSummary、FullRowSelect/MultiSelect、过滤面板关键字/项目/会话ID/日期字段/启用日期/起止 DTP/应用筛选、扫描 async 后台+状态栏、RootLocator 或 --scan-root 扫描根）、MainForm.Smoke.cs（RunBindingChecks）、Program.cs（--scan-root）；冒烟 48 项全 PASS 退出码 0（含 T-17 回归）；build -m:1 exit 0；142/142（feat 见 git log，证据 T-18.txt）。fixture 根 .artifacts/tmp/dsh-t18-fixture（303 会话/1 异常，重建命令见 T-18.txt；perf-0001..0300 用于制造 656ms 扫描耗时证明 UI 不冻结）

- T-19 绑定会话预览与解析状态：UI/PreviewPresenter.cs（PreviewOutcome；PreviewAsync=Task.Run 后台；PathGuard 复验→受限读取/zstd 解压→LegacyPreview.Build；Render 纯文本含元数据/模式/说明/警告/事件正文/截断与不完整计数）、MainForm.cs（_previewCts 取消旧请求；_effectiveScanRoot 缓存；渲染前双重核对 cts 与选中行 Tag 同一对象→不串会话；0 选清空/多选提示）、MainForm.Smoke.cs（RunPreviewChecks+SelectRowBySessionId+PumpUntil；期望 303→305）；冒烟 54 项全 PASS 退出码 0；build -m:1 exit 0；142/142（feat 见 git log，证据 T-19.txt）。fixture 新增 projD/smoke-v4-0004/session.v4.jsonl（V4 金样）、projE/smoke-old-0005/session.v7.jsonl（未知代际），现 307 文件/305 会话/1 异常。坑：static lambda 不能捕获局部变量（CS8820）；PowerShell 函数名 H 与 Get-History 别名冲突；fixture 是根级布局（无 projects\ 层）

- T-20 独立设置保存/读取：Infrastructure/SettingsStore.cs（AppSettings schemaVersion=1 契约字段 camelCase；Load 缺失=Default/损坏=CorruptFallback/未知 schema=UnknownSchema 均回退默认且不改文件；previewMaxItems 钳制≤2000、未知 dateField 回退 fileModified、额外字段忽略；Save 既有损坏/未知 schema 拒绝覆写字节不变、原子=同目录 tmp+File.Move(overwrite)、rememberRoots=false 根一律 null、相对根丢弃、窗口非正回退默认钳制[320,8192]；默认路径 %LOCALAPPDATA%\DeepSeekHarnessCleaner\settings.json，测试注入夹具路径）、UI/SettingsDialog.cs（Load 回退提示、rememberRoots 禁用根输入/浏览、保存拒绝弹警告不关闭、FolderBrowserDialog；**未接线 MainForm**，归后续 UI 任务）、tests/SettingsTests.cs（10 用例）；build -m:1 exit 0；152/152；UI 冒烟 54 项回归 PASS 退出码 0（证据 T-20.txt）。坑：xunit Assert.Equal 对 string?[] 选 ReadOnlySpan 重载报 CS8631，Select 用 `Path.GetFileName(name)!` 投影非空

- T-21 ZIP manifest 与清单签名：Application/BackupManifest.cs（ManifestEntry/BackupManifest schemaVersion=1；Builder.Build=数据根下 sessions//attachments/ 类别限定 + 原始路径任何 .. 段先拒（PATH_TRAVERSAL，先于 GetFullPath 折叠）+ 跨根/跨盘 CROSS_ROOT + OrdinalIgnoreCase 去重 DUPLICATE_ENTRY + 未知类别 UNKNOWN_FILE，错误非空不产出部分清单，非 Valid 状态记警告；Verify=结构复核→可选签名→磁盘复算 length/SHA（MISSING/LENGTH_MISMATCH/SHA_MISMATCH）→可选 actualFiles 清单外=UNKNOWN_FILE；签名=规范化 JSON（紧凑、字段序固定、按 zipPath 排序）SHA-256 小写 hex；ToJson/FromJson 往返一致，FromJson 拒未知 schema/大写 SHA/../重复/未知类别；纯只读）、tests/ManifestTests.cs（14 用例）；build -m:1 exit 0；166/166；UI 冒烟 54 项回归 PASS 退出码 0（证据 T-21.txt）。坑：FileInfo.Length 对不存在文件抛 FileNotFoundException（测试须先落盘再构造 PhysicalLog）

- T-22 原始 ZIP 创建与校验：Infrastructure/BackupService.cs（CreateBackup=目标目录须存在+TARGET_EXISTS 不覆盖→同目录临时 zip（target+".tmp-"+guid）流式写 manifest.json+原字节条目→重开校验数量/逐条目解压 SHA/长度/manifest 签名→发布前复验+File.Move(overwrite:false)→VerifiedBackup(ZipPath,ManifestSignature,FileEntryCount,TotalBytes)；失败 CANCELED/IO_FAILURE/ZIP_VERIFY_FAILED 均删临时 zip 无副作用；VerifyZip 只读复验 SEC-03）、tests/BackupTests.cs（8 用例，附件夹具 4KB 伪随机合成字节）；build -m:1 exit 0；174/174；UI 冒烟 54 项回归 PASS 退出码 0（证据 T-22.txt）。坑：`using ZipArchive zip;`+try 内赋值=CS0210/CS1656，须普通声明+using(zip) 块；小 zip 单字节破坏偏移可能落 local header（.NET 以 central directory 为准）校验仍过，夹具数据须足够大使偏移落入条目数据流

- T-23 Markdown/JSON 可读导出：Application/ReadableExport.cs（Export(ReadableExportInput{Session,TranscriptText,ReferencedAttachmentIds,AttachmentIdsOnDisk})→ReadableExportResult{Markdown,Json,ExportIncomplete,IncompleteReasons}；复用 LegacyPreview.Build 选代际/元数据/MetadataOnly 退化；Markdown 固定声明“不是恢复备份”，消息正文代码块围栏=正文最长反引号串+1（≥3）保证 ```/````/~~~/中文/换行往返不破坏结构，不完整事件用引用块，附件节 存在/缺失/状态未知；JSON Utf8JsonWriter+UnsafeRelaxedJsonEscaping 保留 UTF-8 中文，schemaVersion=1，含 exportIncomplete/incompleteReasons/generations/attachments/projection；exportIncomplete 触发=未知事件/超限截断/附件缺失/磁盘清单不可判定/MetadataOnly/不可读；纯内存无 IO/网络）、tests/ReadableExportTests.cs（7 用例）；build -m:1 exit 0；181/181；UI 冒烟回归 PASS 退出码 0（证据 T-23.txt）。坑：Utf8JsonWriter 须持局部 `using var stream=new MemoryStream()` 再 `stream.ToArray()`（勿内联 new）


- T-24 会话精确清理计划：Application/CleanupPlanner.cs（Build(sessionRoot,selectedSessions,dedicatedCaches)→CleanupPlanResult{RootErrorCode,Plan,Exclusions}；CleanupTarget(AbsolutePath,Category{SessionLog,DedicatedCache},OwnerSessionId,Expected*)；CleanupPlan(PlanId,CreatedUtc,RootFingerprint=规范化根 SHA-256 小写 hex,Targets,RequiresBackup)；Application 不引用 Infrastructure→根内复验同 T-21 内联（.. 段先拒 PATH_TRAVERSAL、根外 CROSS_ROOT、根非法 ROOT_INVALID）；排除=UNVERIFIED_SESSION_ID/DUPLICATE_SESSION_ID/SHARED_FILE/ABNORMAL_FORMAT/ABNORMAL_GENERATION(整会话不拆分:staged 聚合全过才提交)/AMBIGUOUS/UNKNOWN_FILE(白名单 session(.vN≥1).jsonl(.zstd) 严格小写)/根外；专属缓存仅选择且已规划会话的 Matched 条目进入(DedicatedCacheEvidence 由调用方从 T-16 转换)；纯内存零写入）、tests/CleanupPlanTests.cs（16 用例）；build -m:1 exit 0；197/197；UI 冒烟回归 PASS 退出码 0（证据 T-24.txt）。坑：逐代际即时 Add 会在后段失败时残留拆分目标，须 staged+AddRange；xUnit2031 禁 Where+Single 用谓词重载


## 下一个任务
- T-25 实现 Harness 退出状态检查（**前置 T-03**，已验收）。范围：Infrastructure/HarnessProcessProbe.cs、tests/ProcessProbeTests.cs。要点：通过已知 exe 和可验证的进程路径检测 Harness/可能关联执行者；无法判定时返回 Unknown。验收：模拟 Active/Inactive/Unknown，后两类仅 Inactive 可进入写入；不得主动停止进程。关联 TC-15。定义见 docs/implementation-tasks.md:274-281（下轮先读原文）。注意：Get-CimInstance 可能被拒（用 Get-Process）；测试不得杀真实 Harness 进程，模拟用本项目自启动的合成子进程。
- 可复用 API：PathGuard（T-07）、CleanupPlanner/CleanupTarget/CleanupPlan（T-24）、BackupService/VerifiedBackup（T-22）、SessionCacheIndex（T-16）。

## 关键环境事实（勿重复排障）
- 运行方式：`. .\scripts\enter-env.ps1` 后用 `dotnet`（10.0.402）。**任何 build/test 前必须**设 `TEMP=$PWD\.artifacts\tmp`、`TMP` 同值（否则 MSBuild 崩溃 -532462766）。
- **文件策略随会话变化**：T-21 轮起为 workspace-write；该策略下 `dotnet test` 的 testhost OpenProcess 被拒（Win32Exception 5，测试运行中止）。解法：对确切命令一次性升级 `sandbox_permissions=danger-full-access`（T-21 已获批准）。build 在 workspace-write 下须 `-m:1`。full-access 下标准 `dotnet test .\DeepSeekHarnessCleaner.sln -c Release --no-build` 正常（166/166）。
- **Start-Process -Wait 在本环境会挂起**（即使目标进程已退出）。同步等待 GUI exe 用 `[System.Diagnostics.Process]::Start($psi)` + `WaitForExit(ms)`；异步用 `& $exe` + 轮询结果文件。
- UI 冒烟取证：`& exe --smoke <out.txt>`（结果文件 SMOKE_RESULT=PASS/FAIL，逐项 PASS/FAIL；退出码经 Process API 获取）。本机 DeviceDpi=96；真实 DPI/干净 Win10 VM 验证归 T-33，勿在此宣称。
- 命令：`dotnet build .\DeepSeekHarnessCleaner.sln -c Release --no-restore -warnaserror -m:1`；`dotnet test .\DeepSeekHarnessCleaner.sln -c Release --no-build`。build 退出码非 0 时勿跑 --no-build 测试。PowerShell 无 grep，用 Select-String。Get-CimInstance 可能被拒，用 Get-Process。
- 分层：Application 仅引用 Domain；跨层用 Domain 类型端口。Xunit 需显式 `using Xunit;`。Domain.ParseStatus={Valid,Partial,Unsupported,Corrupt,Unreadable}。
- Path 无 3 参 EndsWith；record 位置参数命名实参须用属性名；JsonDocument.Parse 无 ReadOnlySpan 重载。
- ZstdSharp 0.8.8：CompressFrame 带 checksumFlag；截断尾帧→TruncatedTail；损坏→CorruptData。CompressionLimits.MaxLineBytes=8MiB、MaxSessionDecompressedBytes=256MiB。
- PathGuard：`new PathGuard()`；Check(root,target,allowDirectory)；Check(root,root)=PATH_ESCAPE 为“根合法”哨兵。
- 夹具：`using var fx = FixtureRoot.Create(); fx.Factory.*`；junction 须在 Dispose 前 Directory.Delete(link,false)；NTFS 大小写不敏感。
- scripts/build.ps1、test.ps1、publish.ps1 尚不存在，勿假定。

## 未知工作树修改（不得覆盖、勿入库）
- AGENTS.md 已修改（用户改写）；docs/agent-execution.md 未跟踪。T-17 提交已排除二者。

## 收尾状态
- T-24 全部验收 PASS（build -m:1 exit 0；197/197 测试含 CleanupPlanTests 16 项；UI 冒烟回归 PASS 退出码 0），已勾选；本轮结束。下一轮从 T-25 开始。

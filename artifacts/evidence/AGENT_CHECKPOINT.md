# AGENT 检查点

更新时间：2026-10-16（T-16 轮）
Git HEAD：feat(T-16) eaeb8a6；docs(T-16) 勾选提交紧随其后

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
- T-10 会话库存与磁盘统计：Domain/SessionSummary.cs（ParseStatus 已从 Infrastructure 移入 Domain；DateOrigin/PhysicalLog/SessionSummary{TotalBytes,IsAmbiguous}/AnomalousFile/ScannedLogFile/ScannedSessionDirectory/SessionScanData）+ Application/InventoryService.cs（ISessionScanSource 端口、ISessionInventory、InventoryResult；归并/歧义阻断/状态取最严重/CreatedAt 取最高代际 header/排序日期降序→SessionId 升序）+ tests/InventoryTests.cs（10 用例，含测试侧 ScanSource 适配器组合 SessionLogDiscovery+SessionHeaderReader）；全量 76/76（3d9ec8e，证据 T-10.txt）
- T-11 会话日期与搜索筛选：Application/SessionFilter.cs（DateFilterField{CreatedAt,LastActivityAt,FileModified}/DateFilter/FilterRequest/SessionFilter.Apply(sessions,request,timeZone)；包含式本地日历日区间、未知日期排除、Project/SessionId 大小写不敏感精确、Keyword 包含、AND 组合、Start>End 抛 ArgumentException、整会话粒度不裁剪）+ tests/FilterTests.cs（11 用例）；全量 87/87（e604257，证据 T-11.txt）
- T-12 安全 JSONL 事件流：Infrastructure/SessionEventReader.cs（EventLineStatus{Valid,Unknown,MalformedJson,NotObject,TooLong}；EventLine{LineNumber 1 基含 TooLong,Status,EventType}；SessionEventReadResult{Status,Lines,ErrorCode}+UnknownCount/MalformedCount/IsReadComplete；Read(fullPath,maxLineBytes=8MiB,maxTotalBytes=256MiB,cancellationToken)；zstd→ZSTD_TRUNCATED/ZSTD_CORRUPT/EVENTS_EMPTY/TOTAL_LIMIT，明文同样限额；聚合 0 行→Corrupt/EVENTS_EMPTY、全畸形→Corrupt/EVENTS_ALL_MALFORMED、有畸形/超长→Partial；KnownEventTypes={header,message,attachment_ref} 由 fixture 绑定；每行检查取消，using FileStream 不泄漏句柄）+ tests/EventReaderTests.cs（13 用例，含读前后字节一致证明不修改日志）；全量 100/100（577e074，证据 T-12.txt）
- T-13 V4 可验证消息预览：Application/TranscriptProjector.cs（record PreviewItem(Role,Text,At,IsPartial)；record TranscriptProjection(Items,Truncated,IncompleteCount)+IsProjectionComplete；TranscriptProjector.Project(transcriptText,maxItems=2000,ct) 逐行投影，每行检查取消；仅 fixture 确证 message 事件（role∈{user,assistant}、text 为字符串）转 PreviewItem；header/attachment_ref 跳过；缺字段/非文本/未确证角色/未知类型/非对象/无效 JSON→IsPartial 摘要含 1 基行号+原因、正文不保留；附加字段如 reasoning/api_key 不进预览；Project 接收已解码 JSONL 文本，zstd 由调用方解码）+ tests/TranscriptV4Tests.cs（11 用例，zstd 金样经 ZstdFrameDecoder.Decode 后断言角色顺序/文本/时间戳精确匹配）；全量 111/111（9fdb62e，证据 T-13.txt）
- T-14 旧版本预览安全退化：Application/LegacyPreview.cs（enum LegacyPreviewMode{Projected,MetadataOnly}；record LegacyPreviewResult(Mode,PreviewGeneration,MetadataLines,Projection,SupportsRawBackup,Reason)；SelectPreviewGeneration=数值最高规范代际，无候选 null；Build(summary,已解码文本或 null)：无日志/最高代际不可读→MetadataOnly+不回退理由，代际 schema 未经 fixture 确证（FixtureVerifiedGenerations=[0,4]，v1-v3 无 fixture→仅元数据、不猜正文）→MetadataOnly，已确证→复用 TranscriptProjector 投影；SupportsRawBackup 恒 true；元数据行=会话/项目/创建时间 O 格式+DateOrigin/状态/各代际明细/预览代际）+ tests/LegacyPreviewTests.cs（11 用例：最高代际选择、v0 金样投影无虚构、v1-v3 Theory 仅元数据不含正文、v0+v4 损坏不回退、v0 未知事件标记不完整含行号、坏 JSON 仍可备份、空数据不崩溃、无代际、元数据行内容）；全量 122/122（4610b45，证据 T-14.txt）
- T-15 建立附件引用证据索引：Application/AttachmentIndex.cs（enum AttachmentEvidenceStatus{Referenced,Unknown,ConfirmedUnreferenced}；record SessionAttachmentSource(SessionId,TranscriptText?,ParseStatus)——调用方传已解码 JSONL 文本+解析状态，Application 不做文件 I/O；record AttachmentEvidence(AttachmentId,Status,ReferencingSessionIds)、DanglingAttachmentReference(AttachmentId,SessionIds)、AttachmentIndexResult(Attachments,DanglingReferences,GlobalOrphanConclusionAvailable,UnresolvedSessionIds)；Build(sessions,attachmentIdsOnDisk)：仅 JsonDocument 验证过的 attachment_ref（对象+type 精确+attachmentId 非空字符串）建立引用，每会话去重；覆盖规则 sessions 非空且全部 Valid+文本非空→GlobalOrphanConclusionAvailable，任一未解析（Corrupt/Partial/Unreadable/空文本）→false 且无引用附件=Unknown 绝不标孤儿；覆盖完整无引用→ConfirmedUnreferenced 仍不删除（删除属 T-29）；悬空引用仅记证据不虚构附件条目；排序确定性 Ordinal）+ tests/AttachmentIndexTests.cs（10 用例：共享附件 Referenced 含双会话、Corrupt/空列表/Partial/null 文本→结论不可得+Unknown、覆盖完整→ConfirmedUnreferenced、畸形 attachment_ref 不建引用、悬空引用、同会话重复只列一次、只读性夹具文件字节不变）；全量 132/132（ddb5093，证据 T-15.txt）
- T-16 识别单会话投影缓存：Infrastructure/SessionCacheIndex.cs（enum SessionCacheStatus{Matched,Unmatched,Unknown}；record SessionCacheEntry(FullPath,RelativePath,SessionId?,Status,Reason?)、WholeCacheEntry(FullPath,RelativePath,IsDirectory,Length)、CacheIndexResult(RootErrorCode,DedicatedCaches,WholeCacheCandidates,Anomalies)+RootValid；Build(storagesRoot,knownSessionIds)：根哨兵 Check(root,root)=PATH_ESCAPE；只扫 session_projcache/sessions/ 顶层；精确 Ordinal 匹配已知 ID→Matched，命名合规但不在清单→Unmatched(CACHE_ID_NOT_IN_INVENTORY)，目录条目/非 .json/空 ID→Unknown(CACHE_STRUCTURE_MISMATCH)、reparse→Unknown(PATH_REPARSE 不跟随)、同名冲突→Unknown(CACHE_NAME_COLLISION)、空清单→Unknown(CACHE_INVENTORY_MISSING)；全量候选只记录 session_projcache/ 目录+session_projcache.json 供 T-27；workspace.json 永不列出；全程只读；复用 DiscoveryAnomaly）+ tests/CacheIndexTests.cs（10 用例：匹配/不匹配分别标记、空清单 Unknown、目录条目/非 json/空 ID Unknown、junction Unknown、Build 前后缓存与 workspace.json 字节一致且 workspace.json 不在结果、全量候选列出含长度、sessions junction 异常但全量候选仍列、缺失布局空结果、根无效 RootErrorCode）；全量 142/142（eaeb8a6，证据 T-16.txt）

## 下一个任务
- T-17 建立 WinForms 单窗口布局（**前置 T-03**，已验收）。范围：UI/MainForm.*、UI/Program.cs。要点：菜单/导航、列表区、预览区、状态栏、按钮与尺寸/DPI 行为（可无数据）。验收：Win10 窗口启动退出，125%/150% DPI 不遮挡主操作按钮；UI 可用键盘导航。关联 TC-17,TC-19。定义见 docs/implementation-tasks.md:194-201（下轮先读原文，并查 testing.md TC-17/TC-19 与 design UI 章节；注意无头环境下 Win10/DPI 验收如何取证，勿伪造）。
- 可复用 API：SessionCacheIndex.Build(storagesRoot,knownSessionIds)（见上）；AttachmentIndex.Build；TranscriptProjector.Project、LegacyPreview.SelectPreviewGeneration/Build；SessionEventReader.Read；ZstdFrameDecoder.Decode(Stream)；SessionHeaderReader.Read；InventoryService.Scan；SessionFilter.Apply；PathGuard.Check；夹具 FixtureRoot（含 SessionsRoot/StoragesRoot/AttachmentsRoot/DesktopHome）、FixtureFactory（Utf8 为静态方法）.WriteFile(basePath,relativePath,bytes,modifiedUtc?) 等。

## 关键环境事实（勿重复排障）
- 运行方式：`. .\scripts\enter-env.ps1` 后用 `dotnet`（10.0.402）。**任何 build/test 前必须**设 `TEMP=$PWD\.artifacts\tmp`、`TMP` 同值（否则 MSBuild 崩溃 -532462766）。
- 命令：`dotnet build .\DeepSeekHarnessCleaner.sln -c Release --no-restore -warnaserror`；`dotnet test .\DeepSeekHarnessCleaner.sln -c Release --no-build`。build 退出码非 0 时勿跑 --no-build 测试（跑的是旧二进制）。PowerShell 无 grep，用 Select-String。
- 分层：Application 仅引用 Domain（不能 using Infrastructure）；跨层用 Domain 类型端口（如 ISessionScanSource），测试侧写适配器。Xunit 需显式 `using Xunit;`。Domain.ParseStatus={Valid,Partial,Unsupported,Corrupt,Unreadable}（无 Success 成员）。
- Path 类无 3 参 EndsWith（CS1501）；后缀判断用 string.EndsWith(…, StringComparison.Ordinal)。record 位置参数命名实参须用属性名（如 IsPartial:，CS1739）。JsonDocument.Parse 无 ReadOnlySpan<char> 重载（CS1503），传 string 或 UTF-8 字节。
- ZstdSharp 0.8.8：CompressFrame 带 checksumFlag；截断尾帧→TruncatedTail（Content 为空）；损坏→CorruptData。CompressionLimits.MaxLineBytes=8MiB、MaxSessionDecompressedBytes=256MiB。
- PathGuard：`new PathGuard()`；Check(root,target,allowDirectory)；Check(root,root)=PATH_ESCAPE 为“根合法”哨兵。
- 夹具：`using var fx = FixtureRoot.Create(); fx.Factory.*`；junction 须在 Dispose 前 Directory.Delete(link,false)；NTFS 大小写不敏感。
- scripts/build.ps1、test.ps1、publish.ps1 尚不存在，勿假定。

## 未知工作树修改（不得覆盖、勿入库）
- AGENTS.md 已修改（用户改写）；docs/agent-execution.md 未跟踪。T-10 提交已排除二者。

## 收尾状态
- T-16 全部验收 PASS（build exit 0；142/142 测试），已勾选；本轮结束。下一轮从 T-17 开始。

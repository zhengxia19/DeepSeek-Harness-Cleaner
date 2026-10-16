# AGENT 检查点

更新时间：2026-10-16（T-12 轮）
Git HEAD：feat(T-12) 577e074；docs(T-12) 勾选提交紧随其后

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

## 下一个任务
- T-13 实现 V4 可验证消息预览（前置 T-12 已验收）。范围：Application/TranscriptProjector.cs、tests/TranscriptV4Tests.cs。要点：仅将 Harness 0.2.0.0 脱敏 fixture 确证的用户/助手文本事件转成 PreviewItem；验收：角色顺序/纯文本与 fixture 金样一致，缺字段、非文本、未知 schema 标记不完整，不展示推理/密钥。关联 TC-06。定义见 docs/implementation-tasks.md:154-161。
- 可复用 API：SessionEventReader.Read→SessionEventReadResult（EventLine 行号/状态/类型，见上）；ZstdFrameDecoder；SessionHeaderReader.Read；夹具 FixtureFactory.HeaderLine/MessageLine/UnknownEventLine/WriteValidV0Session/WriteValidV4Session 等。库存/筛选：InventoryService.Scan；SessionFilter.Apply（事件正文关键字过滤属 T-12 之后任务）。

## 关键环境事实（勿重复排障）
- 运行方式：`. .\scripts\enter-env.ps1` 后用 `dotnet`（10.0.402）。**任何 build/test 前必须**设 `TEMP=$PWD\.artifacts\tmp`、`TMP` 同值（否则 MSBuild 崩溃 -532462766）。
- 命令：`dotnet build .\DeepSeekHarnessCleaner.sln -c Release --no-restore -warnaserror`；`dotnet test .\DeepSeekHarnessCleaner.sln -c Release --no-build`。build 退出码非 0 时勿跑 --no-build 测试（跑的是旧二进制）。PowerShell 无 grep，用 Select-String。
- 分层：Application 仅引用 Domain（不能 using Infrastructure）；跨层用 Domain 类型端口（如 ISessionScanSource），测试侧写适配器。Xunit 需显式 `using Xunit;`。Domain.ParseStatus={Valid,Partial,Unsupported,Corrupt,Unreadable}（无 Success 成员）。
- Path 类无 3 参 EndsWith（CS1501）；后缀判断用 string.EndsWith(…, StringComparison.Ordinal)。
- ZstdSharp 0.8.8：CompressFrame 带 checksumFlag；截断尾帧→TruncatedTail（Content 为空）；损坏→CorruptData。CompressionLimits.MaxLineBytes=8MiB、MaxSessionDecompressedBytes=256MiB。
- PathGuard：`new PathGuard()`；Check(root,target,allowDirectory)；Check(root,root)=PATH_ESCAPE 为“根合法”哨兵。
- 夹具：`using var fx = FixtureRoot.Create(); fx.Factory.*`；junction 须在 Dispose 前 Directory.Delete(link,false)；NTFS 大小写不敏感。
- scripts/build.ps1、test.ps1、publish.ps1 尚不存在，勿假定。

## 未知工作树修改（不得覆盖、勿入库）
- AGENTS.md 已修改（用户改写）；docs/agent-execution.md 未跟踪。T-10 提交已排除二者。

## 收尾状态
- T-12 全部验收 PASS（build exit 0；100/100 测试），已勾选；本轮结束。下一轮从 T-13 开始。

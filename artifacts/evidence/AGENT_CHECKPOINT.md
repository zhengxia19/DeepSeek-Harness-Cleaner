# AGENT 检查点

更新时间：2026-10-16（T-11 轮）
Git HEAD：feat(T-11) e604257；docs(T-11) 勾选提交紧随其后

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

## 下一个任务
- T-12 实现安全 JSONL 事件流（前置 T-05、T-09 均已验收）。范围：Infrastructure/SessionEventReader.cs、tests/EventReaderTests.cs。要点：消费解压字节逐行解析 JSON，限制总量（MaxSessionDecompressedBytes=256MiB）、行长（MaxLineBytes=8MiB），返回行号/状态/未知事件，不修改日志。验收：有效 JSONL 行数与顺序一致；无效行明确报损坏/部分；取消与超限不泄漏句柄。关联 TC-04、TC-20。
- 可复用 API：ZstdFrameDecoder（Infrastructure/Compression，多帧解码，TruncatedTail/CorruptData→Content 空）；SessionHeaderReader.Read；夹具 FixtureFactory.ZstdFrames/CorruptLine/UnknownEventLine/WriteCorruptJsonSession/WriteTruncatedZstdSession/WriteUnknownEventSession。库存/筛选 API：InventoryService.Scan→InventoryResult；SessionFilter.Apply（事件正文关键字过滤属 T-12 之后）。LastActivityAt 事件时间戳解析属 T-12 之后任务。

## 关键环境事实（勿重复排障）
- 运行方式：`. .\scripts\enter-env.ps1` 后用 `dotnet`（10.0.402）。**任何 build/test 前必须**设 `TEMP=$PWD\.artifacts\tmp`、`TMP` 同值（否则 MSBuild 崩溃 -532462766）。
- 命令：`dotnet build .\DeepSeekHarnessCleaner.sln -c Release --no-restore -warnaserror`；`dotnet test .\DeepSeekHarnessCleaner.sln -c Release --no-build`。build 退出码非 0 时勿跑 --no-build 测试（跑的是旧二进制）。PowerShell 无 grep，用 Select-String。
- 分层：Application 仅引用 Domain（不能 using Infrastructure）；跨层用 Domain 类型端口（如 ISessionScanSource），测试侧写适配器。Xunit 需显式 `using Xunit;`。
- Path 类无 3 参 EndsWith（CS1501）；后缀判断用 string.EndsWith(…, StringComparison.Ordinal)。
- ZstdSharp 0.8.8：CompressFrame 带 checksumFlag；截断尾帧→TruncatedTail（Content 为空）；损坏→CorruptData。CompressionLimits.MaxLineBytes=8MiB、MaxSessionDecompressedBytes=256MiB。
- PathGuard：`new PathGuard()`；Check(root,target,allowDirectory)；Check(root,root)=PATH_ESCAPE 为“根合法”哨兵。
- 夹具：`using var fx = FixtureRoot.Create(); fx.Factory.*`；junction 须在 Dispose 前 Directory.Delete(link,false)；NTFS 大小写不敏感。
- scripts/build.ps1、test.ps1、publish.ps1 尚不存在，勿假定。

## 未知工作树修改（不得覆盖、勿入库）
- AGENTS.md 已修改（用户改写）；docs/agent-execution.md 未跟踪。T-10 提交已排除二者。

## 收尾状态
- T-11 全部验收 PASS，已勾选；本轮结束。下一轮从 T-12 开始。

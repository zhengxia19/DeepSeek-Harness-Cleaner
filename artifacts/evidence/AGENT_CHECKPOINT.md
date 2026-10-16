# AGENT 检查点

更新时间：2026-10-16（T-09 轮）
Git HEAD：feat(T-09) fc4a3aa；docs(T-09) 勾选提交紧随其后

## 已完成（已验收 [x]）
- T-01 仓库基线（6cc56f1）
- T-02 本地 SDK 引导：.dotnet 10.0.402 + global.json + scripts/enter-env.ps1（9815afc）
- T-03 解决方案与依赖锁：sln + 5 项目 + CPM + ZstdSharp.Port 0.8.8 + packages.lock.json（55744e0）
- T-04 隔离数据夹具：tests/Fixtures/{FixtureRoot,FixtureFactory}（4125dac）
- T-05 Zstd 多帧物理解码：Infrastructure/Compression/*（26d5e13）
- T-06 定位授权数据根：Domain/ResolvedRoots.cs + Infrastructure/RootLocator.cs（00e6101）
- T-07 路径白名单验证：Infrastructure/PathGuard.cs（e3ac223）
- T-08 会话物理候选发现：Infrastructure/SessionLogDiscovery.cs（2a64660）
- T-09 受限日志头部读取：Infrastructure/SessionHeaderReader.cs（ParseStatus/SessionHeaderInfo/SessionHeaderReadResult+IdentityMatches+AllowsAutoCleanup；HeadersConsistent；错误码见 T-09 证据）+ tests/HeaderTests.cs（13 用例）；全量 66/66（fc4a3aa，证据 T-09.txt）

## 下一个任务
- T-10 生成会话库存与磁盘统计（前置 T-09 已验收）。范围：Application/InventoryService.cs、Domain/SessionSummary.cs、tests/InventoryTests.cs。要点：按经验证身份把多 generation 归并一条会话；容量按所有物理文件求和且与 fixture 大小相符；单条损坏不中断扫描。关联 TC-05。
- 可复用 API：SessionLogDiscovery.Discover→DiscoveryResult{RootErrorCode,Candidates(SessionFileEntry:FullPath/RelativePath/Generation/Encoding/Length),Anomalies}；SessionHeaderReader.Read(path,expectedSessionId)→SessionHeaderReadResult；HeadersConsistent。夹具 WriteMultiGenerationSession 等见 FixtureFactory。

## 关键环境事实（勿重复排障）
- 运行方式：`. .\scripts\enter-env.ps1` 后用 `dotnet`（10.0.402）。**任何 build/test 前必须**设 `TEMP=$PWD\.artifacts\tmp`、`TMP` 同值（否则 MSBuild 崩溃 -532462766）。
- 命令：`dotnet build .\DeepSeekHarnessCleaner.sln -c Release --no-restore -warnaserror`；`dotnet test .\DeepSeekHarnessCleaner.sln -c Release --no-build`。PowerShell 无 grep，用 Select-String。
- Path 类无 3 参 EndsWith（CS1501）；后缀判断用 string.EndsWith(…, StringComparison.Ordinal)。
- Infrastructure 需显式 `using System.Security;` 才能用 SecurityException。
- ZstdSharp 0.8.8：CompressFrame 带 checksumFlag；截断尾帧→TruncatedTail（Content 为空）；损坏→CorruptData。CompressionLimits.MaxLineBytes=8MiB、MaxSessionDecompressedBytes=256MiB。
- PathGuard：Check(root,target,allowDirectory)；IsSessionFileName 大小写敏感；Check(root,root)=PATH_ESCAPE 为“根合法”哨兵。
- 夹具：`using var fx = FixtureRoot.Create(); fx.Factory.*`；junction 须在 Dispose 前 Directory.Delete(link,false)；NTFS 大小写不敏感（大小写用例分目录）。
- 真实 .dsh 隔离断言：相对路径集合不得新增/删除。
- scripts/build.ps1、test.ps1、publish.ps1 尚不存在，勿假定。

## 未知工作树修改（不得覆盖、勿入库）
- AGENTS.md 已修改（用户改写）；docs/agent-execution.md 未跟踪。T-09 提交已排除二者。

## 收尾状态
- T-09 全部验收 PASS，已勾选；本轮结束。下一轮从 T-10 开始。

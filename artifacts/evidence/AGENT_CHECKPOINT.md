# AGENT 检查点

更新时间：2026-10-16（T-08 轮）
Git HEAD：feat(T-08) 2a64660；docs(T-08) 勾选提交紧随其后

## 已完成（已验收 [x]）
- T-01 仓库基线（6cc56f1）
- T-02 本地 SDK 引导：.dotnet 10.0.402 + global.json + scripts/enter-env.ps1（9815afc）
- T-03 解决方案与依赖锁：sln + 5 项目 + CPM + ZstdSharp.Port 0.8.8 + 5 个 packages.lock.json（55744e0，证据 T-03.txt）
- T-04 隔离数据夹具：tests/Fixtures/{FixtureRoot,FixtureFactory}（4125dac，证据 T-04.txt）
- T-05 Zstd 多帧物理解码：Infrastructure/Compression/*（26d5e13，证据 T-05.txt）
- T-06 定位授权数据根：Domain/ResolvedRoots.cs + Infrastructure/RootLocator.cs（00e6101，证据 T-06.txt）
- T-07 路径白名单验证：Infrastructure/PathGuard.cs（e3ac223，证据 T-07.txt）
- T-08 会话物理候选发现：Infrastructure/SessionLogDiscovery.cs（Discover→DiscoveryResult{RootErrorCode,Candidates,Anomalies}；SessionCandidate(RelativeDirectory,FullPath,Files,ConflictingGenerations)+IsAmbiguous；SessionFileEntry(FullPath,RelativePath,Generation,Encoding{Plain,Zstd},Length)；异常码 NOT_WHITELISTED/UNEXPECTED_LOCATION/PATH_REPARSE/FILE_ACCESS_DENIED/MISSING）+ tests/DiscoveryTests.cs（9 用例）；全量 53/53（2a64660，证据 T-08.txt）

## 下一个任务
- T-09 解析受限日志头部（前置 T-05、T-08 均已验收）。范围：Infrastructure/SessionHeaderReader.cs、tests/HeaderTests.cs。要点：只读首条受限 header，验证 id/formatVersion/createdAt/cwd；不可验证标异常；畸形/缺失/冲突 header 不进可删白名单；源字节不变。关联 TC-03、TC-04。夹具 HeaderLine 格式：{"type":"header","sessionId":...,"formatVersion":N,"createdAt":"..."}（FixtureFactory.HeaderLine）。

## 关键环境事实（勿重复排障）
- 运行方式：`. .\scripts\enter-env.ps1` 后用 `dotnet`（10.0.402）。**任何 build/test 前必须**设 `TEMP=$PWD\.artifacts\tmp`、`TMP` 同值（否则 MSBuild 崩溃 -532462766）。
- 命令：`dotnet build .\DeepSeekHarnessCleaner.sln -c Release --no-restore -warnaserror`；`dotnet test .\DeepSeekHarnessCleaner.sln -c Release --no-build`。
- ZstdSharp 0.8.8：CompressFrame 已启用 checksumFlag；截断→EndOfStreamException、损坏→ZstdException；解码器 leaveOpen。Compression API：ZstdFrameDecoder/CompressionLimits/JsonlLineSplitter（见 T-05 证据）。
- PathGuard：Check(root,target,allowDirectory)→PathCheckResult；IsSessionFileName 大小写敏感；错误码 ROOT_INVALID/PATH_REPARSE/PATH_ESCAPE/FILE_ACCESS_DENIED。Check(root,root) 返回 PATH_ESCAPE 可作“根合法”探测哨兵。
- 夹具用法：`using var fx = FixtureRoot.Create(); fx.Factory.*`；WriteSessionFile(project,sessionId,fileName,bytes)；ZstdFrames/Utf8/HeaderLine 为 public static。junction 须在 Dispose 前 Directory.Delete(link,false)。
- NTFS 大小写不敏感：同目录 "Session.jsonl" 与 "session.jsonl" 同一文件，大小写用例须分目录。
- Infrastructure 项目需显式 `using System.Security;` 才能用 SecurityException（`Security.` 简写 CS0103）。
- SDK 10.0.402 无 Directory.CreateJunction；junction 用 `cmd /c mklink /J`；本机无符号链接权限（非开发者模式）。
- 真实 .dsh 隔离断言：相对路径集合不得新增/删除（应用并发改 size/mtime）。
- scripts/build.ps1、test.ps1、publish.ps1 尚不存在，勿假定。

## 未知工作树修改（不得覆盖、勿入库）
- AGENTS.md 已修改（用户改写）；docs/agent-execution.md 未跟踪。T-08 提交已排除二者。

## 收尾状态
- T-08 全部验收 PASS，已勾选；本轮结束。下一轮从 T-09 开始。

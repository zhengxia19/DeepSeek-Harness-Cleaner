# AGENT 检查点

更新时间：2026-10-17（T-04 轮）
Git HEAD：docs(T-04) 勾选提交紧随 test(T-04) 4125dac 之后

## 已完成（已验收 [x]）
- T-01 仓库基线（6cc56f1）
- T-02 本地 SDK 引导：.dotnet 10.0.402 + global.json + scripts/enter-env.ps1（9815afc）
- T-03 解决方案与依赖锁：sln + 5 项目 + CPM + ZstdSharp.Port 0.8.8 + 5 个 packages.lock.json（55744e0，证据 T-03.txt）
- T-04 隔离数据夹具：tests/DeepSeekHarnessCleaner.Tests/Fixtures/{FixtureRoot,FixtureFactory,README}.cs/md + FixtureIsolationTests.cs；12 类合成夹具（v0/v4、多代际双帧 zstd、Ambiguous、CorruptLine、未知事件、截断尾帧、空文件、日期、附件引用、投影缓存、6 白名单目录+Network 哨兵）；test 6/6 通过；真实 .dsh 前后快照一致（4125dac，证据 T-04.txt）

## 下一个任务
- T-05 验证 Zstd 多帧物理解码（前置 T-04 已验收）。范围：Infrastructure/Compression/**、tests/CompressionTests.cs。验收：多帧逐字节一致；截断/损坏不当成功；8MiB 行 / 256MiB 解压量保护在测试中触发。关联 TC-04、TC-20。

## 关键环境事实（勿重复排障）
- 运行方式：`. .\scripts\enter-env.ps1` 后用 `dotnet`（10.0.402）。**任何 build/test/restore 前必须**设 `TEMP=$PWD\.artifacts\tmp`、`TMP` 同值（否则 MSBuild CreateTempSubdirectory 崩溃 -532462766）。
- restore 另需 `NUGET_HTTP_CACHE_PATH=$PWD\.nuget\http-cache`、`NUGET_SCRATCH=$PWD\.artifacts\nuget-scratch`；会话策略 danger-full-access、审批 never（勿再请求 escalation）。T-04 未新增包，lock 未变。
- 命令：`dotnet build .\DeepSeekHarnessCleaner.sln -c Release --no-restore -warnaserror`；`dotnet test .\DeepSeekHarnessCleaner.sln -c Release --no-build`。
- ZstdSharp 0.8.8：`Compressor.Wrap` 返回 `Span<byte>`（需 .ToArray()）；截断多帧文件仍含全部帧头，勿以帧头计数判完整性。
- 夹具用法：`using var fx = FixtureRoot.Create(); fx.Factory.*`；位于 %TEMP%\dshcleaner-fixtures\fx-<guid>，Dispose 幂等删整树。
- scripts/build.ps1、test.ps1、publish.ps1 尚不存在，勿假定。

## 未知工作树修改（不得覆盖、勿入库）
- AGENTS.md 已修改（用户改写）；docs/agent-execution.md 未跟踪。T-04 提交已排除二者。

## 收尾状态
- T-04 全部验收 PASS，已勾选；本轮结束。下一轮从 T-05 开始。

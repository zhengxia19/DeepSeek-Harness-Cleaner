# AGENT 检查点

更新时间：2026-10-16（T-05 轮）
Git HEAD：feat(T-05) 26d5e13；docs(T-05) 勾选提交紧随其后

## 已完成（已验收 [x]）
- T-01 仓库基线（6cc56f1）
- T-02 本地 SDK 引导：.dotnet 10.0.402 + global.json + scripts/enter-env.ps1（9815afc）
- T-03 解决方案与依赖锁：sln + 5 项目 + CPM + ZstdSharp.Port 0.8.8 + 5 个 packages.lock.json（55744e0，证据 T-03.txt）
- T-04 隔离数据夹具：tests/Fixtures/{FixtureRoot,FixtureFactory} + FixtureIsolationTests.cs；12 类合成夹具（4125dac，证据 T-04.txt）
- T-05 Zstd 多帧物理解码：src/DeepSeekHarnessCleaner.Infrastructure/Compression/{ZstdFrameDecoder,CompressionLimits,JsonlLineSplitter}.cs + tests/CompressionTests.cs（14 测试）；夹具 zstd 帧改为带内容校验和（CompressFrame 强制 ZSTD_c_checksumFlag=1）；全量 21/21 通过（26d5e13，证据 T-05.txt）

## 下一个任务
- T-06 定位授权数据根（前置 T-03 已验收）。范围：Infrastructure/RootLocator.cs、Domain/ResolvedRoots.cs、tests/RootLocatorTests.cs。要点：USERPROFILE/APPDATA/可选 DSH_HOME 显式根，识别会话/存储/附件/桌面目录并返回状态。

## 关键环境事实（勿重复排障）
- 运行方式：`. .\scripts\enter-env.ps1` 后用 `dotnet`（10.0.402）。**任何 build/test 前必须**设 `TEMP=$PWD\.artifacts\tmp`、`TMP` 同值（否则 MSBuild 崩溃 -532462766）。
- 命令：`dotnet build .\DeepSeekHarnessCleaner.sln -c Release --no-restore -warnaserror`；`dotnet test .\DeepSeekHarnessCleaner.sln -c Release --no-build`。
- ZstdSharp 0.8.8 实测：默认 Wrap 无内容校验和（中间损坏会静默解出错字节）；CompressFrame 已启用 checksumFlag；截断→EndOfStreamException、损坏→ZstdException；空帧/空输入均 0 字节不抛，适配器以 EmptyInput 状态区分。解码器 leaveOpen，调用方负责关闭 FileStream。
- 真实 .dsh 隔离断言：DSH 应用运行中会并发改既有文件 size/mtime，断言为“相对路径集合不得新增/删除”，勿恢复逐字节相等。
- 夹具用法：`using var fx = FixtureRoot.Create(); fx.Factory.*`；位于 %TEMP%\dshcleaner-fixtures\fx-<guid>，Dispose 幂等删整树。
- scripts/build.ps1、test.ps1、publish.ps1 尚不存在，勿假定。

## 未知工作树修改（不得覆盖、勿入库）
- AGENTS.md 已修改（用户改写）；docs/agent-execution.md 未跟踪。T-05 提交已排除二者。

## 收尾状态
- T-05 全部验收 PASS，已勾选；本轮结束。下一轮从 T-06 开始。

# AGENT 检查点

更新时间：2026-10-16（T-06 轮）
Git HEAD：feat(T-06) 00e6101；docs(T-06) 勾选提交紧随其后

## 已完成（已验收 [x]）
- T-01 仓库基线（6cc56f1）
- T-02 本地 SDK 引导：.dotnet 10.0.402 + global.json + scripts/enter-env.ps1（9815afc）
- T-03 解决方案与依赖锁：sln + 5 项目 + CPM + ZstdSharp.Port 0.8.8 + 5 个 packages.lock.json（55744e0，证据 T-03.txt）
- T-04 隔离数据夹具：tests/Fixtures/{FixtureRoot,FixtureFactory} + FixtureIsolationTests.cs；12 类合成夹具（4125dac，证据 T-04.txt）
- T-05 Zstd 多帧物理解码：Infrastructure/Compression/{ZstdFrameDecoder,CompressionLimits,JsonlLineSplitter}.cs + tests/CompressionTests.cs（14 测试）；zstd 帧强制 checksumFlag；全量 21/21（26d5e13，证据 T-05.txt）
- T-06 定位授权数据根：Domain/ResolvedRoots.cs（RootSource/RootProbeStatus/RootProbe/ResolvedRoots）+ Infrastructure/RootLocator.cs（选择>DSH_HOME>USERPROFILE\.dsh；桌面 APPDATA\@deepseek-ai\dsh-desktop；只读结构探测）+ tests/RootLocatorTests.cs（10 用例）；全量 31/31（00e6101，证据 T-06.txt）

## 下一个任务
- T-07 实现路径白名单验证（前置 T-06 已验收）。范围：Infrastructure/PathGuard.cs、tests/PathGuardTests.cs。要点：根包含校验、白名单、reparse 检查，拒绝 `..`/junction/symbolic link/UNC/设备路径/ADS/项目目录/根目录；sentinel SHA 不变。关联 TC-15、TC-20。

## 关键环境事实（勿重复排障）
- 运行方式：`. .\scripts\enter-env.ps1` 后用 `dotnet`（10.0.402）。**任何 build/test 前必须**设 `TEMP=$PWD\.artifacts\tmp`、`TMP` 同值（否则 MSBuild 崩溃 -532462766）。
- 命令：`dotnet build .\DeepSeekHarnessCleaner.sln -c Release --no-restore -warnaserror`；`dotnet test .\DeepSeekHarnessCleaner.sln -c Release --no-build`。
- ZstdSharp 0.8.8 实测：默认 Wrap 无内容校验和；CompressFrame 已启用 checksumFlag；截断→EndOfStreamException、损坏→ZstdException；解码器 leaveOpen，调用方关闭 FileStream。
- 真实 .dsh 隔离断言：DSH 应用并发改既有文件 size/mtime，断言为“相对路径集合不得新增/删除”。
- RootLocator 实测事实：`Directory.EnumerateDirectories` 返回完整路径（需 Path.GetFileName）；.NET Core `Path.GetFullPath` 对 `<>` 不抛异常（Missing），内嵌 NUL 才抛 ArgumentException；标记校验：.dsh 需 sessions+storages+attachments 全在，桌面需 Cache + 任一 {Code Cache,GPUCache,logs}（OrdinalIgnoreCase）。RootLocator 常量：DshHomeEnvironmentVariable="DSH_HOME"、RootInvalidErrorCode="ROOT_INVALID"。
- 夹具用法：`using var fx = FixtureRoot.Create(); fx.Factory.*`；位于 %TEMP%\dshcleaner-fixtures\fx-<guid>，Dispose 幂等删整树。
- scripts/build.ps1、test.ps1、publish.ps1 尚不存在，勿假定。

## 未知工作树修改（不得覆盖、勿入库）
- AGENTS.md 已修改（用户改写）；docs/agent-execution.md 未跟踪。T-06 提交已排除二者。

## 收尾状态
- T-06 全部验收 PASS，已勾选；本轮结束。下一轮从 T-07 开始。

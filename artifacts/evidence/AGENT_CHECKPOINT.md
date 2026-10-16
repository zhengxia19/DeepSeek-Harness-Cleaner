# AGENT 检查点

更新时间：2026-10-16（T-07 轮）
Git HEAD：feat(T-07) e3ac223；docs(T-07) 勾选提交紧随其后

## 已完成（已验收 [x]）
- T-01 仓库基线（6cc56f1）
- T-02 本地 SDK 引导：.dotnet 10.0.402 + global.json + scripts/enter-env.ps1（9815afc）
- T-03 解决方案与依赖锁：sln + 5 项目 + CPM + ZstdSharp.Port 0.8.8 + 5 个 packages.lock.json（55744e0，证据 T-03.txt）
- T-04 隔离数据夹具：tests/Fixtures/{FixtureRoot,FixtureFactory} + FixtureIsolationTests.cs（4125dac，证据 T-04.txt）
- T-05 Zstd 多帧物理解码：Infrastructure/Compression/*（14 测试）；全量 21/21（26d5e13，证据 T-05.txt）
- T-06 定位授权数据根：Domain/ResolvedRoots.cs + Infrastructure/RootLocator.cs（10 用例）；全量 31/31（00e6101，证据 T-06.txt）
- T-07 路径白名单验证：Infrastructure/PathGuard.cs（Check→PathCheckResult{Allowed|Missing|Rejected}；根包含 GetRelativePath、字面 `..` 拒绝、根→目标逐段 ReparsePoint、UNC/设备/ADS/驱动器根/用户根/项目目录拒绝；错误码 PATH_REPARSE/PATH_ESCAPE/ROOT_INVALID/FILE_ACCESS_DENIED；会话文件名与桌面缓存目录白名单）+ tests/PathGuardTests.cs（13 用例，junction 用 cmd /c mklink /J）；全量 44/44（e3ac223，证据 T-07.txt）

## 下一个任务
- T-08 发现会话物理候选（前置 T-07 已验收）。范围见 docs/implementation-tasks.md:104 起。要点：在 SessionRoot 下发现候选，使用 PathGuard 校验与 T-05 解码器；不猜测未知格式。

## 关键环境事实（勿重复排障）
- 运行方式：`. .\scripts\enter-env.ps1` 后用 `dotnet`（10.0.402）。**任何 build/test 前必须**设 `TEMP=$PWD\.artifacts\tmp`、`TMP` 同值（否则 MSBuild 崩溃 -532462766）。
- 命令：`dotnet build .\DeepSeekHarnessCleaner.sln -c Release --no-restore -warnaserror`；`dotnet test .\DeepSeekHarnessCleaner.sln -c Release --no-build`。
- ZstdSharp 0.8.8：CompressFrame 已启用 checksumFlag；截断→EndOfStreamException、损坏→ZstdException；解码器 leaveOpen。
- 真实 .dsh 隔离断言：相对路径集合不得新增/删除（应用并发改 size/mtime）。
- RootLocator：EnumerateDirectories 返回完整路径需 GetFileName；GetFullPath 对 `<>` 不抛、内嵌 NUL 抛；标记：.dsh 需 sessions+storages+attachments，桌面需 Cache+任一{Code Cache,GPUCache,logs}。
- PathGuard 实测事实：SDK 10.0.402 无 Directory.CreateJunction，junction 用 `cmd /c mklink /J`（免管理员）；`Directory.Delete(recursive)` 删不了 junction（Dispose 先枚举 reparse 目录 Delete(link,false)）；本机无符号链接权限（非开发者模式），symlink 用例优雅跳过、reparse 由 junction 实证；逐段检查必须覆盖 根→…→目标 全链（曾有 off-by-one 被测试捕获）。
- 夹具用法：`using var fx = FixtureRoot.Create(); fx.Factory.*`；Dispose 幂等删整树。
- scripts/build.ps1、test.ps1、publish.ps1 尚不存在，勿假定。

## 未知工作树修改（不得覆盖、勿入库）
- AGENTS.md 已修改（用户改写）；docs/agent-execution.md 未跟踪。T-07 提交已排除二者。

## 收尾状态
- T-07 全部验收 PASS，已勾选；本轮结束。下一轮从 T-08 开始。

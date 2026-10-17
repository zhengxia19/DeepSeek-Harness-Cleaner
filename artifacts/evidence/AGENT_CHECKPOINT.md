# AGENT 检查点

更新时间：2026-10-17（T-18 轮）
Git HEAD：feat(T-18)；docs(T-18) 勾选提交紧随其后（基线 a709411 docs(T-17)）

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

## 下一个任务
- T-19 绑定会话预览与解析状态（**前置 T-13,T-14,T-18**，均已验收）。范围：UI/PreviewPresenter.cs、UI/MainForm.*。要点：选中会话触发后台受限预览，显示 role、文本、来源与 warning。验收：V4 金样显示正确；未知旧版显示提示而非空白/崩溃；取消/重选不串会话。关联 TC-06,TC-20。定义见 docs/implementation-tasks.md:214-221（下轮先读原文）。
- 可复用 API：InventoryService.Scan（ISessionScanSource；UI 侧已有 UiScanSource）；SessionFilter.Apply；TranscriptProjector.Project；LegacyPreview.SelectPreviewGeneration/Build；SessionEventReader.Read；ZstdFrameDecoder.Decode；SessionCacheIndex.Build；AttachmentIndex.Build；PathGuard.Check；夹具 FixtureRoot/FixtureFactory。UI 冒烟数据绑定取证模式：exe `--smoke <res> --scan-root <fixture根>` + Process API；冒烟内 PerformClick+DoEvents 泵等待后台完成。

## 关键环境事实（勿重复排障）
- 运行方式：`. .\scripts\enter-env.ps1` 后用 `dotnet`（10.0.402）。**任何 build/test 前必须**设 `TEMP=$PWD\.artifacts\tmp`、`TMP` 同值（否则 MSBuild 崩溃 -532462766）。
- **本会话起 DSH 文件策略为 danger-full-access、审批关闭（不得请求 sandbox 升级）**。此前 workspace-write 下：MSBuild 并行子节点启动失败（“生成失败”但 0 错误、约 3 秒超时）→ build 须加 `-m:1`；dotnet test 的 testhost OpenProcess 被拒（Win32Exception 5）。full-access 下标准 `dotnet test .\DeepSeekHarnessCleaner.sln -c Release --no-build` 正常（142/142）。保守起见 build 仍带 `-m:1`。
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
- T-18 全部验收 PASS（build -m:1 exit 0；142/142 测试；冒烟 48 项 PASS 退出码 0），已勾选；本轮结束。下一轮从 T-19 开始。

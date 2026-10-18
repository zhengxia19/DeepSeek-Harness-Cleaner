# AGENT_CHECKPOINT — DeepSeek Harness Cleaner

更新时间：2026-10-18（Round 3 结束）
Git：HEAD 应为 `docs(T-33)` 提交（含 scripts/perf.ps1、scripts/smoke-win10.ps1、T-33 证据、勾选）；链：← `525b397 docs(T-32)` ← `a758cbe docs(T-31)`。
工作区常驻未提交项（永不提交）：`M AGENTS.md`、`?? docs/agent-execution.md`。

## 状态
- 已验收：T-01..T-33 全部 `[x]`（证据 artifacts/evidence/T-01..T-33.txt）。
- 本轮完成：T-33。PERF-01 PASS（1000 会话 18MB：初扫 2766ms≤15s、筛选 0.96ms≤200ms、峰值 WS 197MiB<512MiB；机器 Win10 Pro 22H2 19045.6466 / Intel F6M85 32核 / VMware SSD）。WIN10-01 PASS（自包含单文件 102.8MB，实机冒烟 72/72，退出 0）。TC-18 PASS（运行期 0 新增 TCP 连接）。COMPAT-01 分开结论：物理解码兼容（zstd magic/解码 Success）；语义不支持（本机 0.2.1-rc.2 头部 schema type=session/id/version ≠ 0.2.0.0 确证的 type=header/sessionId/formatVersion → 44/44 Corrupt、删除阻断；只读证明 changed=0 deleted=0）。
- 整改项 R-01：BackupService.cs 60 处 WHITESPACE，`dotnet format src\DeepSeekHarnessCleaner.Infrastructure\DeepSeekHarnessCleaner.Infrastructure.csproj --no-restore` 后重跑 build+test（发布放行前清零）。
- 整改项 R-02：0.2.1-rc.2 头部/事件 schema 适配未实现 → T-34 已知限制文档必须写明"不得对 0.2.1-rc.2 数据执行清理"。

## 下一个任务：T-34 — 制作发布包并最终复核（前置 T-33 ✓，最后一个任务）
- 修改范围：`scripts/publish.ps1、docs/release.md、README.md、CHANGELOG.md、dist/v0.1.0/**`。
- 内容：按 release 清单发布（自包含 win-x64、SHA256SUMS、依赖报告、已知限制含 R-02 与 WIN10-01 诚实标注）；归档测试证据；dist 无真实会话与开发缓存；干净 Win10 VM 可运行（TC-17 现场部分）；版本与 SHA 记录准确；R-01 format 清零（属发布放行门槛 testing.md §4，需在 T-34 内修 src 空白——若判定超修改范围，记录阻断而非越界）。
- 验收：Release 放行项目全部通过；`dotnet build -warnaserror`、`dotnet test`、`dotnet format --verify-no-changes` 全过；发布 SHA-256 记录；仅对 fixtures 删除。
- 参考：T-33 已验证 publish 命令（加 `-p:NuGetAudit=false` + `$env:NUGET_HTTP_CACHE_PATH` 项目内）；publish.ps1 尚不存在需本轮创建。

## 关键复用信息
- 环境：`. .\scripts\enter-env.ps1`（.dotnet 10.0.402）；`$env:TEMP=$env:TMP="$PWD\.artifacts\tmp"`。
- 构建/测试：`dotnet build .\DeepSeekHarnessCleaner.sln -c Release --no-restore -warnaserror -m:1`；`dotnet test … -c Release --no-build`（267 用例）。
- 冒烟：exe `--smoke <out> --scan-root <fixture>\sessions --log-dir <dir>`；fixture 源 `.artifacts/tmp/dsh-t18-fixture`（307 文件）复制到新目录后用。
- 发布：`dotnet publish src/DeepSeekHarnessCleaner.UI -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:NuGetAudit=false -o <dir>`（离线必须 NuGetAudit=false + NUGET_HTTP_CACHE_PATH 项目内）。
- 坑：PowerShell 无 grep；`-f` 复合格式与 JSON 花括号冲突（用 Replace）；`$i/27` 是 double（用 [int][Math]::Floor）；FileInfo 无 GetFileSystemInfo；ZstdDecodeResult 属性是 IsSuccess；Get-NetTCPConnection 无连接时抛异常（try/catch 计 0）；注册表正确键是 `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion`；dotnet format 需 `--no-restore`；真实 .dsh\sessions 被当前 Harness 会话实时写入（只读证明用 changed/deleted=0、允许 added）。
- 提交规范：`feat(T-XX)` + `docs(T-XX)`；排除 AGENTS.md 与 docs/agent-execution.md。

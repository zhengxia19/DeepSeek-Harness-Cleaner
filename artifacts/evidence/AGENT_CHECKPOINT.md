# AGENT_CHECKPOINT — DeepSeek Harness Cleaner 维护任务（M 系列）

更新：M-01～M-04 全部完成，v0.1.1 已发布。任务来源 docs/maintenance-tasks.md 已闭环。

## 状态
- 分支 docs/post-release-maintenance-20261019，HEAD 起点 9b5b3b3；工作树含全部 M 系列改动**未提交**（8 源码/测试 + 4 文档 + 3 脚本 + 证据；packages.lock.json ×4 已复原干净）。
- M-01 ✅ DIAG-event-format.txt；M-02 ✅ M-02.txt（header 契约）；M-03 ✅ M-03.txt（事件信封/角色/时间映射）；M-04 ✅ M-04.txt + M-04-win10.txt（发布 v0.1.1）。

## v0.1.1 发布要点
- 产物：dist/v0.1.1/win-x64/DeepSeekHarnessCleaner.UI.exe（102.8 MB，SHA-256 0b5f2e0f…9046，见 SHA256SUMS.txt 与 release.md §7）。
- 门禁：build 0 警告；tests 282/282；format 干净；vulnerability 两工程无漏洞包；smoke-win10.ps1 9/9 PASS（含新增“无 Corrupt”“全部带 createdAt”判定）；dist EXE 冒烟 72/0。
- 真实根只读核验：51 会话全 Valid（corrupt=0/ambiguous=0/anomalies=0），51/51 带 createdAt，快照 changed=0 deleted=0 added=0。
- 临时 harness（scan-check/event-check/preview-check/msg-probe）已删除；evidence 与 .artifacts/tmp 保留。

## 已固化的真实格式契约（勿再动摇）
- Header：{"type":"session","id":"session-<uuid>","version":0–4,"createdAt":<epoch-millis 数值>,…}；旧假设格式 → HEADER_MISSING。
- 事件信封：{"type":…,"seq":…,"time":<epoch-millis>,"data":…}；user/message 正文在 data；assistant/system/developer/message 与 tool/result 在 data.message；预览仅取 content[] 的 type=="text" 块；分块事件跳过；KnownEventTypes=48（47 实测 + attachment_ref）。

## 未验证 / 遗留（如实）
- freshly-provisioned Win10 干净 VM 未验证（release.md §7 已声明）。
- 工作树未提交；如需提交按 git-commit-push.cmd 或手工 commit（提交信息建议引用 M-01～M-04 证据）。
- 真实根为活动根：会话数随时间漂移（45→49→51），证据均为时点快照。

## 命令备忘
- 构建/测试/格式：$env:NUGET_PACKAGES='D:\Code\DeepSeek-Harness-Cleaner\.nuget'; .\.dotnet\dotnet.exe build DeepSeekHarnessCleaner.sln -c Release -warnaserror --nologo；dotnet test … --no-build；dotnet format --verify-no-changes（无 --nologo）。
- 冒烟：.\scripts\smoke-win10.ps1 -EvidencePath <file>（需 PATH 含 .dotnet）；或 Start-Process <exe> --smoke "<report>" --scan-root "<fixtureCopy>\sessions" --log-dir "<dir>" -Wait（72 PASS/0 FAIL 基线）。
- 发布：.\scripts\publish.ps1 -Version 0.1.1；发布后（且全部构建之后）git checkout -- src/*/packages.lock.json。
- fixture：.artifacts/tmp/dsh-t18-fixture（根即 sessions 根；备份 .bak-preM03/.bak-preM02）。

# AGENT 检查点（Round 4 结束 · 2026-10-19）

## 状态：T-01～T-34 全部已验收勾选，项目任务清单完成

- 本轮任务：T-34 制作发布包并最终复核 → 已验收，`docs/implementation-tasks.md` 已勾 [x]。
- 证据：`artifacts/evidence/T-34.txt`（门禁、发布包、SHA、冒烟 4×72/72+安装模拟、TC-18、真实数据只读、干净 VM 诚实标注）。

## Git

- HEAD：`docs(T-34)` 提交（含 README/CHANGELOG/release.md/T-34.txt/勾选/本检查点）← `8af8f70 feat(T-34)`（publish.ps1 + BackupService R-01 格式修复）← `f746e2e build(T-33)`。
- 永久不提交：`M AGENTS.md`、`?? docs/agent-execution.md`。
- `dist/` 被 .gitignore 排除；发布哈希在 `dist/.../SHA256SUMS.txt` 与 T-34.txt 存档。

## 发布产物（0.1.0）

- `dist/v0.1.0/win-x64/`：DeepSeekHarnessCleaner.UI.exe（102.8 MB，SHA-256 c5464a2df7fc7b04d99520c790b0706dffcd51abc670089adf68a7cf91cf6eea）+ LICENSE.txt/README.md/THIRD-PARTY-NOTICES.txt/SHA256SUMS.txt；未签名。
- 门禁：build -warnaserror 0；test 267/267；format verify 0；漏洞查询两工程均无易受攻击包。

## 关键注意事项（若需返工）

- 冒烟调用：exe `--smoke <out> --scan-root <fixture>\sessions --log-dir <dir>`；fixture 须把 dsh-t18-fixture 内容放入 `sessions` 子目录；冒烟含删除测试，每次须重建 fixture；文件在进程退出时才写。
- 同步 `&` 启动可靠；Start-Process 分离实例会被沙箱作业对象级联 kill。TC-18 轮询用 tasklist+netstat（原生），勿用 Get-NetTCPConnection（CIM 挂起）。
- publish.ps1 注入 -p:Version 会改写 packages.lock.json 为 [0.1.0,)，发布后须 `git checkout -- src/*/packages.lock.json`。
- 已知限制（CHANGELOG/release.md §6）：0.2.1-rc.2 只读（R-02）；未签名；freshly-provisioned 干净 VM 未验证（Windows Sandbox 不可用），以等效证据通过并诚实标注。

## 下一步

- 任务清单无未完成任务。如继续，仅为用户新需求或外部复核反馈；不自动开启新任务。

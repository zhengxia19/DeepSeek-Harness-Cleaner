## Unreleased (2026-10-20, not yet Windows-validated)

- Add prioritized repair checklist in `docs/2026-10-20-修复任务清单.md`.
- Guard null/invalid session grid row during preview and deletion; suppress selection callback during rebinding.
- Make project/session ID filters match substrings (case-insensitive).
- Debounce preview dispatch and cap UI-visible preview text; this is not a streaming preview implementation.
- Allow interactive backup folder selection and add open-folder action; generated ZIP names have unique suffixes.
- Add bounded durable `session/title` extraction (plain/zstd) and session-title search/display; expand standalone regression tests.
- Add checkboxes independent from selected rows; batch deletion uses checked sessions, otherwise selected rows.
- Rebuild filter area with aligned table layout, project ComboBox suggestions and reset action.
- Add session title to confirmation list, improve scrollability and long warning wrapping.
- Bound preview file reads and per-message rendered text; propagate cancellation to transcript projection.
- Keep the conservative process-based deletion guard; no unverified live-session deletion.
- Build and WinForms smoke tests have not been executed in this environment.

# CHANGELOG

## [0.1.0] — 2026-10-18（首个发布，win-x64 自包含单文件，未签名）

### Added
- 只读扫描 DeepSeek Harness 会话根（`%USERPROFILE%\.dsh\sessions` 及受控选择的其他根），按项目/日期/会话 ID/关键字筛选，展示容量、代际与解析状态。
- 多格式 Zstandard/JSONL 物理解码（`session.jsonl.zstd`、`session.v4.jsonl.zstd` 及明文 JSONL），多代际归并与异常文件标记。
- 可验证的消息预览（语义投影，仅对已确证的 0.2.0.0 V4 模式启用）。
- 原始 ZIP 备份：删除前强制生成，逐文件 SHA-256 校验，校验失败阻断删除；用户不可关闭（v1）。
- 会话删除：路径白名单、reparse/越界阻断、Harness 退出状态检查、删除前复验；部分失败可报告并可继续。
- 会话投影缓存（`storages/session_projcache*`）、桌面缓存/日志的受限清理；附件需人工确认关系后清理。
- WinForms 中文界面：列表/预览/扫描/删除/退出、状态栏统计、Tab 导航与助记键；扫描在后台线程执行不冻结 UI。
- 发布包 `dist/v0.1.0/win-x64/`：单文件 EXE、README 摘要、LICENSE、THIRD-PARTY-NOTICES、SHA256SUMS.txt。

### Known limitations（已知限制）
- 语义支持仅确证 DeepSeek Harness **0.2.0.0 V4** 样本模式；实测 **0.2.1-rc.2** 数据头部/事件 schema 未确证 → 只读识别、**禁止清理**（删除阻断，安全失败）。
- 备份 ZIP 是原始本地文件快照，非官方导入协议；删除后不随程序回滚自动恢复。
- 未签名 EXE；无自动更新；不提供云端历史删除。
- 兼容性实测于 Windows 10 Pro 22H2（19045.6466）；未在 freshly-provisioned 干净 VM 上验证（见 docs/release.md §6）。

## [Unreleased] — 2026-10-16

> 历史计划条目已于 0.1.0 全部实现并移入上方条目。本文件按 Added / Changed / Fixed / Removed 记录用户可见变化。

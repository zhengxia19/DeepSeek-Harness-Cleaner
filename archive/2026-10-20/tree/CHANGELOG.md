## [0.2.4] — 2026-10-20

- 修正执行结果报告的父窗体居中、自适应布局、可选择复制日志路径及实际横纵滚动。
- 附件右键按命中行打开系统默认应用；复验路径/摘要，已知无扩展名对象使用校验副本，源附件保持不变。
- 主窗关闭时在程序目录独立文件中记忆位置、大小及状态；恢复时校正屏幕工作区，写入失败不覆盖旧数据、不静默换目录。
- 会话“选择”表头切换当前筛选结果全选；显示隐藏勾选计数，删除计划与完整选择集一致。
- 主会话、附件、删除明细及结果报告四表标题加粗居中，正文与勾选行为不变。
- Release 零警告/错误；350 自动测试、91 Windows 窗口检查、5 默认应用实际打开检查、72 发布冒烟及两轮各 30 项同目录窗口记录检查通过。150% 实际 DPI 由用户验收，不计本轮通过。
- 逐任务证据见 `artifacts/evidence/2026-10-20/T-34.txt`～`T-38.txt`，T-33 证据保留。

## [0.2.3] — 2026-10-20

- 会话与附件删除备份改为可选；选择备份时仍须完成原始 ZIP 校验。无备份结果报告不再显示未创建的 ZIP 路径，保留进程、路径、指纹及文件复验。
- 删除与附件窗口在非模态打开时按直接父窗体居中，并限制在可用工作区内。
- 附件列表增加可核实名称、类型及关联会话；会话列表增加归档列，解析状态明确显示正常或具体异常。
- 修复备份窗口在任务启动前或执行中关闭时的取消处理，防止删除窗口停留在等待状态。
- 324 自动测试、38 Windows 窗口专项、72 发布 EXE 合成夹具冒烟通过；证据 `artifacts/evidence/2026-10-20/T-31.txt`。

## [0.2.2] — 2026-10-20

- 修复原生 v0/v4 会话只能查看、无法生成删除目标的问题；删除前重新核对身份、代际集合、JSONL 完整性及哈希，仍要求原始 ZIP 备份及 Harness 退出。
- 区分正常内部事件、工具文本、未知扩展和损坏 JSON，避免把未支持事件误报为会话内容损坏。
- 接入附件管理窗口，支持只读盘点、原生图片引用匹配、人工逐文件选择、独立 ZIP 备份和安全删除；Unknown 不判定孤儿。
- 新增合成数据上的 Windows 窗口回归程序，覆盖实际会话/附件选择、备份、确认、删除及未选文件/根外哨兵保护。
- 本轮实测命令、结果及限制见 `artifacts/evidence/2026-10-20/T-30.txt`；不代表完整 DPI/运行中删除验收。

## 2026-10-20 早期修复记录（当时尚未 Windows 功能验收）

- Default to a framework-dependent win-x64 single-file EXE; clients must install .NET 10 Windows Desktop Runtime x64 separately.

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
- Fix three nullable WinForms column accesses that blocked compilation (CS8602).
- Release solution build and win-x64 framework-dependent single-file publish passed on Windows 10 with SDK 10.0.402; unit tests, functional tests and WinForms smoke tests were not run for this build.

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

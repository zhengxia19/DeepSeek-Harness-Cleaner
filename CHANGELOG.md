# CHANGELOG

> 2026-10-19 文档维护：对齐 v0.1.0 已交付状态、Agent 后续维护规则与发布证据的表述；不构成新版本功能发布。

## [Unreleased]（v0.2.0 进行中）

### Added
- **归档会话只读识别与展示（N-01）**：扫描时只读读取 `storages/workspace.json`（与会话根同级）的 `global.archivedSessionIds`，会话列表新增“归档”列（是/—），状态栏显示“归档 N”。文件缺失/超限(32MiB)/不可读/JSON 非法/结构未知时扫描不失败，全部按未归档展示并提示“归档信息不可用”。归档 id 按 header `id` 精确匹配；该文件永不写入或删除（见 docs/design.md §2、docs/interfaces.md §3/§5、docs/testing.md §2.3）。
- **窗口位置/大小持久化（N-03）**：正常模式退出/关窗时保存窗口位置、大小与最大化状态到 `settings.json`（新增可选字段 `windowX/windowY/windowMaximized`，schemaVersion 保持 1，旧文件缺字段自动回退）；重启按保存值恢复，位置全出屏（如外接显示器已移除）时仅位置回退居中、尺寸仍恢复，最大化状态独立生效。冒烟与 `--scan-root` 覆盖模式不读写用户配置（见 docs/design.md §9、docs/interfaces.md §4）。

### Changed
- **会话列表结构与选择语义（N-02）**：列表列序改为 `[勾选][序号][会话 ID][项目][创建时间][日期来源][大小][格式][状态][归档]`。勾选框列（不可排序）为删除/备份的操作对象：删除按钮按“勾选行数>0”启用，删除计划仅取勾选行；行选中仅驱动预览。序号列为绑定顺序的 1 基序号（只读、不可排序），点列头排序后序号随行走、不重新编号。`项目/创建时间/大小` 三列按内容自适应宽度（含表头，绑定后全行测量兜底，覆盖虚拟化未显示行）（见 docs/requirements.md REQ-005/REQ-008、docs/testing.md §2.4）。

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

## [0.1.1] — 2026-10-19（真实格式修正维护版，win-x64 自包含单文件，未签名）

> 历史计划条目已于 0.1.0 全部实现并移入上方条目。本文件按 Added / Changed / Fixed / Removed 记录用户可见变化。发布记录与验证环境见 docs/release.md §7。

### Fixed
- **会话 header 解析对齐真实 DSH 格式（M-02）**：首行契约改为 `{"type":"session","id":…,"version":…,"createdAt":<epoch-millis 数值>}`（`version` 0–4；`createdAt` 仅接受 epoch-millis JSON 数值）。此前真实 `%USERPROFILE%\.dsh\sessions` 会话 100% 被判 `HEADER_MISSING` → Corrupt/Ambiguous、无法按日期筛选或进入可清理状态；现已可正常解析、显示创建时间。
- 旧假设格式（`type:"header"`/`sessionId`/`formatVersion`/ISO 字符串日期）不再接受，按 `HEADER_MISSING` 安全失败（不放宽门禁）；新增 header 负向测试（见 docs/testing.md §2.1）。
- **事件类型/角色/时间映射对齐真实 DSH 格式（M-03）**：事件信封契约确证为 `{"type":…,"seq":…,"time":<epoch-millis 数值>,"data":…}`；消息正文按真实位置读取——`user/message` 在 `data`，`assistant/system/developer/message` 与 `tool/result` 在 `data.message`；预览仅提取 `content[]` 中 `type:"text"` 块。此前真实根 v4 会话预览几乎全部显示为“未知事件/不完整”；现可显示 user/assistant/system/developer/tool 角色与文本正文，事件时间正确解析（epoch-millis）。已知事件类型表由真实根实测 47 类硬编码；未知事件仍按“类型/行号 + 不完整”摘要，v0–v3 仍仅提供元数据级最小保证（见 docs/design.md §2、docs/testing.md §2.2）。

### Known limitations（0.1.1 已知限制）
- 语义投影仅覆盖已确证的 0.2.x 事件类型；无文本块的消息（如仅 `tool-call`/`reasoning` 块）按“不完整”标记，不猜测正文；v0–v3 旧代际仅元数据级预览。
- 未签名 EXE；无自动更新；未在 freshly-provisioned 干净 VM 上验证（见 docs/release.md §7）。

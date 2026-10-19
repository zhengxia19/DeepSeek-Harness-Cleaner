# 维护任务清单（M 系列）— 真实 header/事件格式修正

> **归档状态（2026-10-19）：已闭环。** M-01～M-04 全部完成并通过验收（证据 `artifacts/evidence/M-01.txt`～`M-04-win10.txt`），v0.1.1 已发布（`dist/v0.1.1/win-x64/`）；提交 `953d9bc` 已推送。本清单归档，不再更新；§不做项中"归档标志的 UI 展示"已被 v0.2.0 N 系列取代。后续工作见 `docs/change-plan-v0.2.0.md`。

> 来源：`artifacts/evidence/DIAG-scan-coverage.txt`（2026-10-19 只读核验）。
> 已确证缺陷：真实 DSH 会话 header 为 `{"type":"session","version":<int>,"id":"session-<uuid>","createdAt":<epoch-millis 数值>,"cwd"?,…}`；
> `SessionHeaderReader` 却期望 `type:"header"` / `sessionId` / `formatVersion` / ISO 字符串日期，
> 导致真实机 46/46 文件 `HEADER_MISSING` → 全部 Corrupt/Ambiguous → 无一可自动清理。
> 发现层（目录/白名单/归档覆盖）已核验完整，不在本清单修复范围。
> 历史基线 T-01～T-34 不重开；M 系列为新增维护工作，按序执行，每轮一个。

## M-01 真实事件行格式核验（只读，无代码改动）

- **背景**：真实文件第 2 行起为 `{"type":"permission/preset","seq":…,"time":…,"data":…}` 等事件行；
  `SessionEventReader` 的事件类型/角色映射沿用 fixture 假设格式，未对真实数据验证过。预览是否同样失配未知。
- **边界**：只读解码真实 sessions 根中 v4 与 v0 样本各若干文件的**前若干行**；只记录事件 `type` 名、键名结构、
  `time` 字段编码（epoch/ISO）、角色与内容块结构假设是否成立；**不写会话正文/参数值到任何输出**。
  不修改 src/、tests/、docs/。
- **产出**：`artifacts/evidence/DIAG-event-format.txt`：逐项“假设 vs 真实”对照表 + 结论（匹配 / 不匹配清单）。
- **验收**：证据落盘；结论明确回答“预览事件映射是否需要修复”，若需修复列出全部差异点。
- **依赖**：无。

## M-02 header 解析修正（代码 + fixture + 测试 + 文档）

- **边界（改）**：
  - `SessionHeaderReader.ParseHeaderLine` 改为真实契约：`type=="session"`；`id`（非空字符串）；
    `version`（int，`<0` → `HEADER_VERSION_INVALID`，`>4` → `Unsupported/HEADER_VERSION_UNSUPPORTED`）；
    `createdAt` **仅接受 epoch-millis 数值**（JSON number），字符串/缺失/非法 → `HEADER_DATE_INVALID`；
    其余字段（cwd/isSeeded/delegationDepth/agentPreset）可选、不校验语义。
  - 旧假设格式（`type:"header"` 等）**不再接受**，按 `HEADER_MISSING` 处理（安全失败，不放宽门禁）。
  - 身份映射不变：`id` ↔ 会话目录名核对，`IdentityMatches=false` 仍禁自动清理。
  - 全部 fixture 金样（T-04/T-09/T-10/T-18 冒烟 fixture 等）header 行改为真实格式；
    T-18 的 305 会话/1 异常、关键字、projB、日期断言保持成立（createdAt epoch 值须换算自原日期）。
  - 文档同步：`docs/design.md` §2、`docs/interfaces.md`、`docs/testing.md` 受影响处；`CHANGELOG.md`。
- **边界（不改）**：`SessionEventReader`/预览、删除/备份流程、UI 列、扫描发现层、`MaxSupportedFormatVersion=4`。
- **验收**：
  1. `build -warnaserror` 0 警告；全部测试通过（含新增负向：type 旧值→HEADER_MISSING；createdAt 为字符串→HEADER_DATE_INVALID；
     version=5→Unsupported；id 缺失→HEADER_ID_MISSING；id≠目录名→IdentityMatches=false 不可删）。
  2. 只读 harness 扫真实根：46/46 `Valid`，`NO_SESSION_ID=0`、`NO_CREATED_AT=0`；
     45 会话中 Ambiguous=0（双代际目录 v0+v4 属不同代际，合法归并）。
  3. format verify 0；证据 `artifacts/evidence/M-02.txt`（命令、退出码、逐项结果）。
- **依赖**：无（可与 M-01 并行，但按序先 M-01）。

## M-03 事件/预览修正（条件任务，由 M-01 结论触发）

- **触发**：M-01 判定事件映射不匹配；否则记 `N/A` 关闭，不改代码。
- **边界（改）**：`SessionEventReader` 事件类型/角色/时间映射对齐真实格式；对应 fixture 与测试断言同步；
  未知事件仍按“类型/行号 + 不完整”摘要；预览限制（≤2000 行、单行 ≤8MiB、解压 ≤256MiB）不变。
  v0–v3 仍按 design.md §2 历史格式最低保证：只保证物理解码与元数据，不强行宣称完整语义。
- **边界（不改）**：header 契约（M-02 已定）、删除门禁。
- **验收**：真实根只读预览 harness：v4 会话预览出角色/文本且代际标注正确；v0 会话显示元数据+“不完整”而非空白/崩溃；
  全部测试通过；证据 `artifacts/evidence/M-03.txt`。
- **依赖**：M-01；与 M-02 无冲突（不同文件），但排在 M-02 后执行。

## M-04 全量回归 + 冒烟 + 发布 v0.1.1

- **边界**：重建合成 fixture → exe `--smoke`（含删除测试，仅合成根）；build/test/format/漏洞门禁；
  `scripts/publish.ps1` 出 `dist/v0.1.1/win-x64`（含 SHA256SUMS）；发布后 `git checkout -- src/*/packages.lock.json`；
  新 exe 对真实根**只读**扫描确认列表 45 会话、状态非 Corrupt、日期可筛选；更新 `docs/release.md`、`CHANGELOG.md`；
  删除临时 `artifacts/scan-check/`（或归档说明）。
- **验收**：冒烟逐项 PASS、ExitCode=0；测试全绿；真实根只读核验记录在 `artifacts/evidence/M-04.txt`；
  发布哈希落盘。Windows 10 干净 VM 仍未验证则如实标注，不宣称兼容通过。
- **依赖**：M-02；若 M-03 触发则亦依赖 M-03。

## 执行顺序与不做项

- 顺序：**M-01 → M-02 → M-03（条件）→ M-04**。M-01 先行是因为 M-04 冒烟含预览断言，须先知道事件格式结论以确定 M-03 是否触发。
- 不做：发现层改动（已核验完整）；归档标志的 UI 展示（DSH 归档是 workspace.json 标志，清理器按文件删除即可，无需识别归档态）；
  IL 标签问题（环境已修复，非代码缺陷；如复发按检查点记录的 icacls 命令处理）；不新增联网/遥测。

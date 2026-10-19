# 测试计划与发布门槛（Testing）

## 1. 测试隔离

- Unit / Integration 全部使用 `Path.GetTempPath()` 下自动创建的模拟 `.dsh` 根和模拟桌面根，每个测试完成后验证不触及外部路径。
- 脱敏真实数据仅用于**显式授权的只读兼容性测试**；任何带 `File.Delete` 的真实数据测试必须另行独立审批且不得由自动测试执行。
- 模拟文件可由项目内 fixtures 生成器写入，需覆盖有效 v0/v4、多个代际、多个 Zstd frame、截断尾帧、corrupt JSON、未知 event、附件引用、权限拒绝、reparse 点、并发变更。

## 2. 核心用例矩阵

| TC | 验证目标（对应 REQ） | 关键断言 |
|---|---|---|
| TC-01 | 根路径发现 | 默认/自定义/不存在/假目录必须准确归类；不触碰 workspace 外根 |
| TC-02 | 只读扫描 | 对 fixture 的 file list、长度、mtime、SHA 前后完全一致 |
| TC-03 | 代际聚合 | v0+v4 同一 ID 合并且保留两个物理文件，冲突时禁删 |
| TC-04 | Zstd/JSONL | 多帧按序、明文兼容；坏尾/不完整 JSON 标记 Partial/Corrupt |
| TC-05 | 列表 | 统计件数/大小/日期/项目/状态都可解释 |
| TC-06 | 预览 | 已验证 v4 角色文本正确；旧版未知事件标记不完整、不崩溃 |
| TC-07 | 日期/搜索 | inclusive 日期、跨时区、无日期、ID/项目/关键字都正确 |
| TC-08 | 勾选多选 | 勾选仅整会话；取消/重勾与过滤后的操作对象正确；列排序后勾选仍指向原会话、序号随行不重排 |
| TC-09 | ZIP 备份 | 原始代际全部入包；逐文件 SHA 相同；失败没有清理副作用 |
| TC-10 | 可读导出 | Markdown/JSON 字符串安全编码，未知事件有 incomplete 标记 |
| TC-11 | 精确删除 | 删除已选择目标；隔离树外和未选文件 hash 不变，失败逐项列明；无备份删除成功且逐项报告、结果报告如实标注无备份，主窗“创建备份”后删除报告引用该 ZIP，提供备份校验失败仍中止（N-04/N-05） |
| TC-12 | 会话缓存 | 单会话只删匹配 JSON；全量缓存需显式选择，workspace.json 不变 |
| TC-13 | 桌面缓存日志 | 仅允许 6 个白名单目录；浏览器状态和 Network 均不变 |
| TC-14 | 附件 | 未证明孤儿时标 Unknown；单文件经确认方可删除（备份可选，N-04） |
| TC-15 | 强制阻断 | 运行进程/无法检查/软链/出根/指纹变化必拒绝删除 |
| TC-16 | 结果 | 部分失败、取消、权限拒绝、容量统计与重扫状态准确 |
| TC-17 | 环境发布 | 本地 SDK 包缓存独立，干净 Windows 10 VM 上启动/退出 |
| TC-18 | 离线 | 运行 EXE 期间网络请求检查零新增主动请求；无自动更新 |
| TC-19 | 配置 | 默认/损坏/未知版本处理正确；记忆关闭时不写根路径；窗口几何往返、旧文件缺字段回退、全出屏回退（N-03） |
| TC-20 | 安全失败 | 未知格式、权限、空数据、坏压缩、异常目录均无误删/卡死 |

### 2.1 Header 契约负向用例（M-02，`tests/HeaderTests.cs`）

- 旧假设格式 `type:"header"`（含 `sessionId`/`formatVersion`/ISO 字符串日期）→ `HEADER_MISSING`（Corrupt，禁自动清理）。
- `createdAt` 为 ISO 字符串/缺失/非整数/超 `DateTimeOffset` 范围 → `HEADER_DATE_INVALID`；仅 epoch-millis JSON 数值可解析。
- `version=5`（>4）→ `Unsupported` + `HEADER_VERSION_UNSUPPORTED`；`version<0`/缺失/非整数 → `HEADER_VERSION_INVALID`。
- `id` 缺失/空 → `HEADER_ID_MISSING`；`id` ≠ 会话目录名 → `IdentityMatches=false`，禁自动清理（既有 `IdentityMismatch_Flagged_NotDeletable`）。

### 2.2 事件信封契约用例（M-03，`tests/TranscriptV4Tests.cs`、`tests/EventReaderTests.cs`）

- 消息事件按真实信封投影：`user/message` 正文在 `data`（`role`+`content[]` 文本块）；`assistant/system/developer/message` 正文嵌套在 `data.message`；`tool/result` 正文在 `data.message`（`role="tool"`）。
- 多文本块以换行拼接，非 `text` 块（`image`/`tool-call`/`reasoning` 等）不进入正文；仅含非文本块的 assistant 消息按不完整处理，不猜测正文。
- 缺 `content`/非文本/未确证角色 → 不完整标记；`tool/result` 缺 `data.message` → 不完整标记。
- 聚合分块事件（`reasoning-chunks`/`tool-call-chunks`/`text-chunks`）跳过且不标记不完整（正文由最终消息事件承载）。
- `time` 为 epoch-millis 数值 → 解析为事件时间；缺失/非法数值 → 时间为空但仍投影，不失败。
- 未列出的已知外类型（如 `step/start`）→ 不完整摘要含类型名与行号；`KnownEventTypes` 由真实根实测 47 类硬编码（`EventReaderTests` 词表断言）。

### 2.3 归档标志只读识别用例（N-01，`tests/ArchiveIndexTests.cs`）

- 正向：`storages/workspace.json` 的 `global.archivedSessionIds` 解析为归档集合；归档 id 按 header `id` 精确匹配 → `SessionSummary.IsArchived=true`；集合中的未知 id 容忍忽略，不影响其余会话。
- 负向（扫描均不失败、全部未归档并回报状态码）：文件缺失（`ARCHIVE_FILE_MISSING`）、超 32MiB（`ARCHIVE_FILE_TOO_LARGE`）、JSON 非法（`ARCHIVE_JSON_INVALID`）、根/`global` 非对象或 `archivedSessionIds` 非数组/含非字符串元素（`ARCHIVE_STRUCTURE_UNKNOWN`）、junction reparse（`PATH_REPARSE`）。
- `global` 或 `archivedSessionIds` 缺失 → 归档信息可用且集合为空（正常未归档），非错误。
- header 身份不匹配（`SessionId=null`）的会话即使目录名在归档集合中也不标记。
- 只读红线：读取前后 `workspace.json` 字节与 mtime 不变；真实根只读 harness 的归档命中集合与 `workspace.json` id∩目录名 ground truth 集合哈希一致（见 `artifacts/evidence/N-01.txt`）。

### 2.4 列表结构与勾选语义冒烟用例（N-02，`MainForm.Smoke.cs`）

- 列序断言：`[勾选][序号][会话 ID][项目][创建时间][日期来源][大小][格式][状态][归档]`；勾选列为复选框列、不可排序、可编辑；序号列只读、不可排序。
- 绑定断言（`GridMatches`）：勾选列初始未选；序号列按绑定顺序 1 基赋值。
- 宽度断言：`project/created/size` 三列宽度 ≥ 全行最长内容首选宽度（含表头；逐行 `TextRenderer` 测量，覆盖虚拟化未显示行）。
- 勾选语义断言：勾选两行启用删除按钮；列排序后勾选仍指向原会话、序号随行（绑定顺序不变，不重排）；取消勾选禁用删除按钮；删除计划仅含勾选会话（未勾选行不进入计划），确认后仅删除该 2 会话的文件。
- 预览仍由行选中驱动（不受勾选影响，既有 TC-06/TC-20 断言不变）。

## 3. 非功能测试

- PERF-01：Windows 10 x64 + SSD + ≥8GB RAM，在 1000 个小会话 ≤200MB 的 fixtures 上计时，完整元数据初扫 ≤15 秒；筛选响应 ≤200ms，工作集 <512MiB；记录机器 CPU/SSD/Build，失败必须修复或用户明确豁免。
- UX-01：125%/150% DPI、中文目录/Emoji 文本、键盘 Tab、窗口缩放、长路径、未勾选会话禁用删除，验证 UI。
- SEC-01：从目录外的文件通过 reparse point 或 `..` 逃逸到可控根时禁止清理；测试文件内容 hash 仍然相同。
- SEC-02：活动 Harness 进程、权限不可见的托管进程、占用文件、不一致清单均阻断清理。
- SEC-03：人为破坏 ZIP 的单字节后，校验必须失败；提供备份时删前文件变化要求重新备份（N-04 后未备份不触发该门禁）。
- COMPAT-01：实测 DeepSeek Harness 0.2.0.0 V4 与旧版日志（允许脱敏只读样本）；报告物理解码/语义预览分开结论。自 N-01 起含归档交叉核对：真实根扫描归档命中数与 `workspace.json` `archivedSessionIds`∩会话目录名一致、`ARCHIVE` 状态可用、`workspace.json` 前后指纹不变（`scripts/smoke-win10.ps1`）。
- WIN10-01：记录 `winver` 的 Edition/Version/Build，在该版本下测试 WinForms 单文件自包含、中文 UI、操作完成和退出。官方支持范围无法覆盖的 Windows 10 SKU 必须诚实标注实测而非官方支持。

## 4. 任务级与发布级检查

任务级：由每个 T 编号绑定具体 TC/命令；任务不得仅以“代码写完”通过。发布级必须同时满足：

- `dotnet build -warnaserror` 通过，`dotnet test` 全部通过，`dotnet format --verify-no-changes` 通过。
- TC-01～TC-20 全部已执行且有证据，安全必选 TC-02/09/11/15/20 不允许豁免。
- PERF-01、SEC-01～03、COMPAT-01、WIN10-01 完成；`dotnet package vulnerability` 同等查询结果存档。
- 在离线、干净的 Windows 10 x64 VM 上通过 Smoke；发布 SHA-256 已记录；仅对测试 fixtures 执行删除。

## 5. 测试证据格式

每项证据：`日期时间/提交 hash/SDK 版本/Windows Build/命令/退出码/通过断言/失败详情/证据文件路径/审核者或 Agent`。示例：`artifacts/evidence/T-09-backup-validation.txt`。勾选任务必须引用一次有效证据；不允许空证据或伪造截图。

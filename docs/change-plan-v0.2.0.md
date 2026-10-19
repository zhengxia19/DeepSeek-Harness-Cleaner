# v0.2.0 变更计划（N 系列）— UI 增强与删除规则调整

> 来源：用户 2026-10 本轮 9 项需求。基线：M 系列（v0.1.1）已完成但工作树未提交——**N 系列开始前须先提交 M 系列基线**，不覆盖未知改动。
> 历史基线 T-01～T-34 与 M-01～M-04 不重开。N 系列每项独立验收、独立勾选；**验收标准未确认前不得勾选**。
> 注：`docs/maintenance-tasks.md` §不做项曾裁定"归档标志的 UI 展示"不做；本轮用户明确要求，**该裁定被本计划取代**（决策记录见 N-01）。

## 0. 需求 → 代码现状核实（必要性结论）

| # | 需求 | 代码现状（已核实） | 必要性结论 |
|---|------|--------------------|------------|
| R1 | 表格标识归档会话 | `src/` 无任何 `archiv*` 代码；归档标志在 `storages\workspace.json → archivedSessionIds`（DIAG-scan-coverage 实测 16/16），程序从不读取；`SessionSummary` 无归档字段 | **需要新增**（读取器 + Domain 字段 + UI 列） |
| R2 | 关闭保存/启动加载窗口位置大小 | `AppSettings` 仅有 `WindowWidth/WindowHeight`（无 X/Y）；**MainForm 完全不读写 SettingsStore**（硬编码 `ClientSize(1100,680)` + `CenterScreen`）；`SettingsDialog` 存在但无菜单入口（孤儿） | **需要新增**（X/Y 字段 + MainForm 加载/保存接线） |
| R3 | 项目/创建时间/大小列自适应 | `_sessionGrid.AutoSizeColumnsMode = None`，全部列固定宽度（project=130/created=140/size=110） | **需要修改**（列级 `AllCells`） |
| R4 | 删除窗口在父窗口居中 | `CleanupDialog`/`BackupDialog`/`ResultsDialog`/`SettingsDialog` 均已 `CenterParent`，且 `Show(this)` 已设 owner | **疑似已满足**：需运行实证；已满足则只补冒烟断言，不改代码 |
| R5 | 删除前不强制备份 | `CleanupPlanner.cs:193` `RequiresBackup: targets.Count > 0`；`CleanupExecutor` `GATE_BACKUP_REQUIRED` 门禁；`CleanupDialog` 确认按钮备份完成前禁用；冒烟断言"未备份时确认删除按钮禁用" | **需要修改**；与 AGENTS.md §2 删除红线冲突，**须新增决策记录**（docs/decisions/） |
| R6 | 删除窗口控件随窗口缩放 | `CleanupDialog` 全部绝对坐标（`Location`/`Size` 硬编码，无 Anchor/Dock） | **需要修改**（改 Dock/TableLayoutPanel 布局） |
| R7 | 备份按钮移到主窗口 | "创建备份"仅在 `CleanupDialog` 内；主窗口操作区只有 扫描/删除/退出 | **需要修改**（主窗口按钮 + 复用 `BackupDialog`） |
| R8 | 表格前加勾选框列 + 序号列 | 网格无 checkbox 列；删除/预览由行选中（`SelectedRows`）驱动；冒烟按行选中驱动 | **需要修改**；须定义勾选与行选中的职责划分（决策点 D2） |
| R9 | 发布不带运行时框架 | `publish.ps1` 用 `--self-contained` 单文件捆绑 .NET；README 摘要写"无需安装 .NET"；REQ-017/TC-17 按自包含验收 | **需要修改**（framework-dependent + 文档/门禁同步） |
| R10（新增） | 附件要能列出（D1 附带） | 后端已具备：`AttachmentIndex`（附件 ID/引用状态/悬空引用）+ `RootLocator` 已解析 `attachments` 根；但 UI 层 `Attachment` **零引用**——主窗口从未列出附件，也无附件删除入口 | **需要新增**（只读附件列表 UI；后端仅调门禁） |

## 1. 决策点（已全部经用户确认，2026-10）

- **D1（R5 范围）✅**：备份门禁放宽适用于**会话删除与附件删除**（用户明确：附件删除也不强制备份）；缓存/桌面清理规则不变。用户同时新增需求：**附件要能在 UI 列出**（→ 新任务 N-06）。
- **D2（R8 语义）✅**：勾选框列 = 删除/备份的操作对象；行选中仍仅驱动预览；序号列 = 绑定顺序的 1 基序号（用户点列头排序后序号随行走，不重新编号）。删除/备份按钮按"勾选行数>0"启用。
- **D3（R2 范围）✅**：持久化 = 位置 + 大小 + 最大化状态；`schemaVersion` 保持 1（新字段可选、缺省回退，向后兼容）；恢复位置完全不在任何屏幕范围内时回退 `CenterScreen`；冒烟模式不读写用户配置。
- **D4（R5 保留项）✅**：只取消"必须先备份"。其余安全机制原样保留——① 删除窗必须点"确认删除"（明确确认）；② Harness 进程运行中阻断删除；③ 逐文件删除前复验路径白名单/重解析点/SHA-256 指纹（内容变化即跳过）。即"可以不备份，但不是盲删"。
- **D5（R1 容错）✅**：归档标志来自 `storages\workspace.json`；该文件缺失/损坏/异常大时扫描**不失败**，全部会话按"未归档"显示 + 状态栏提示"归档信息不可用"；该文件永远只读，本程序不写入。
- **D6（版本）✅**：删除规则属用户可见行为变更 → 版本号 **v0.2.0**（非补丁号）。

## 2. 任务序列（按序执行，每轮一个）

### N-01 归档会话识别与列表列（R1）

- **边界（改）**：
  - 新增 `SessionArchiveIndex`（Infrastructure，只读）：从会话根父目录推导 `storages\workspace.json`，解析 `archivedSessionIds`（JSON 字符串数组）；大小上限复用行/解压门禁思路（建议 ≤32MiB）；缺失/损坏/未知结构 → 空集 + 状态码（D5）。
  - `SessionScanData` 增加 `ArchivedSessionIds`；`UiScanSource.Scan` 填充；`InventoryService.MergeSession` 写入 `SessionSummary.IsArchived`（默认 false）。
  - `MainForm` 表格新增"归档"列（是/—）；`InventoryPresenter`/状态栏统计可选加"归档 N"。
  - 测试：workspace.json 正/负 fixture（缺失、非对象、非数组、含未知 id）；归档 id 与 header `id` 匹配断言。
  - 文档：`docs/design.md`、`docs/interfaces.md`、`docs/testing.md`、`CHANGELOG.md`；`docs/maintenance-tasks.md` §不做项加一行"已被 v0.2.0 N-01 取代"。
- **边界（不改）**：workspace.json 永不写/删；发现层、删除门禁、缓存清理对 storages 的处理不变。
- **验收**：`build -warnaserror` 0 警告；全部测试通过；只读 harness 扫真实根：归档列命中 16/16（与 workspace.json 交叉核对）；冒烟 PASS；证据 `artifacts/evidence/N-01.txt`。
- **依赖**：无（M 基线提交后）。

### N-02 表格重构：勾选框列 + 序号列 + 三列自适应（R8 + R3）

- **边界（改）**：
  - 列序：`[勾选(DataGridViewCheckBoxColumn)][序号][会话 ID][项目][创建时间][日期来源][大小][格式][状态][归档]`。
  - 勾选列不可排序；序号列只读、绑定顺序赋值；`project/created/size` 三列 `AutoSizeMode = AllCells`（绑定后 `AutoResizeColumn` 兜底，含表头宽度）。
  - 删除/备份按钮启用条件改为勾选行数；`OnDeleteClick`/备份取"勾选行"的 `Tag`；预览仍按行选中。
  - 冒烟：`GridMatches` 增加勾选/序号断言；删除流程改为设置勾选；新增"排序后序号随行"断言。
- **边界（不改）**：`SessionSummary`/服务层；筛选逻辑；行 `Tag` 绑定机制。
- **验收**：三列宽度 ≥ 最长内容（fixture 含长项目名/完整日期/大字节值）；勾选 2 行删除仅删该 2 会话（合成根冒烟）；全部测试 + 冒烟 PASS；证据 `artifacts/evidence/N-02.txt`。
- **依赖**：N-01（列集合定型后一次改齐，避免两次冒烟）。

### N-03 窗口位置/大小持久化（R2）

- **边界（改）**：
  - `AppSettings` 新增 `WindowX/WindowY/WindowMaximized`（可选，缺省=未保存）；`SettingsStore` 读写 + 钳制（负值/超界处理）；`schemaVersion` 保持 1。
  - `MainForm` 构造时经 `SettingsStore`（默认路径）加载：有保存值 → `StartPosition=Manual` + 位置/大小（跨屏校验，全出屏回退 `CenterScreen`）；`FormClosing` 保存（正常退出与关窗均保存）。
  - 冒烟/`--scan-root` 覆盖模式**不读写**用户配置（避免 CI 污染真实 `%LOCALAPPDATA%`）。
  - 测试：往返保存、旧文件（无新字段）加载回退、全出屏回退、损坏文件不覆写（既有语义保持）。
- **边界（不改）**：`SettingsDialog` 仍无入口（接线不在本轮）；`rememberRoots` 语义不变。
- **验收**：手工核验（启动→移动/缩放→关闭→重启恢复）记录于证据；自动化测试覆盖往返/回退分支；冒烟不写真实配置（证据含 settings.json mtime 对比）；证据 `artifacts/evidence/N-03.txt`。
- **依赖**：无；建议在 N-02 后（避免 MainForm 冲突）。

### N-04 删除门禁放宽：备份改为可选（R5，决策 D1/D4）

- **边界（改）**：
  - `CleanupPlanner.cs:193` → `RequiresBackup: false`（会话计划）；`AttachmentCleanupSelection.cs:134` → `RequiresBackup: false`（附件计划，D1 用户确认附件同样放宽）；`CleanupExecutor` 不变（提供备份仍逐项校验，防呆保留）。
  - `CleanupDialog`：确认按钮初始可用；备份状态标签改为"未备份（可选）/已备份并校验"；`OnConfirmClick` 允许 `manifest/backup` 为 null。
  - 冒烟：反转"未备份时确认删除按钮禁用" → "未备份可直接确认删除"；保留"已备份则校验通过才删"路径。
  - 文档：`docs/requirements.md` REQ-011（"强制先备份"→"备份可选、建议"）、REQ-014（"单附件删除必须单独备份及确认"→"必须确认，备份可选"）、`docs/design.md` 门禁节、`docs/interfaces.md`、`docs/testing.md` TC-11/TC-14、`CHANGELOG.md`、`README.md`；**新增 `docs/decisions/ADR-xxx-session-delete-backup-optional.md`**；同步修订 AGENTS.md §2 红线表述（用户文件，改动前征得同意）。
- **边界（不改）**：进程门禁、PathGuard、SHA-256 指纹复验、用户确认（D4：可以不备份，但不是盲删）、结果报告；缓存/桌面清理规则。
- **验收**：负向测试：无备份删除成功且逐项报告；有备份但 ZIP 校验失败 → `GATE_BACKUP_INVALID` 中止仍生效；附件计划测试改为"备份可选、确认必需"（`RequiresBackup=false` 断言 + 确认门禁仍生效）；全部测试 + 冒烟 PASS；证据 `artifacts/evidence/N-04.txt`。
- **依赖**：无强依赖；排在 UI 表格任务后，与 N-05 同文件（CleanupDialog）故先于 N-05。

### N-05 备份按钮移至主窗口（R7）

- **边界（改）**：
  - `MainForm` 操作区新增"创建备份(&B)"按钮：对**勾选**会话构建清单（`BuildSessionManifest`，数据根为根）→ 复用 `BackupDialog` 后台创建+校验；成功后主窗体持有 `LastBackup`（zip 路径 + manifest + `VerifiedBackup`），状态栏显示 ZIP 位置。
  - `CleanupDialog` 移除"创建备份"按钮与 `OnBackupClick`；构造入参改为可选的既有备份事实，仅展示状态；未备份时确认仍可用（N-04）。
  - 冒烟：备份步骤从对话框流改为主窗流；删除流断言"已备份→报告含 ZIP 位置"与"未备份→报告备份位置为空/占位"。
- **边界（不改）**：`BackupService`/`BackupManifestBuilder`/`BackupDialog` 内部逻辑；备份 ZIP 命名与临时目录规则。
- **验收**：勾选任意会话（未打开删除窗）可直接备份并校验（冒烟断言 Ready + SHA 校验）；备份后删除，结果报告引用该 ZIP；未备份删除，报告如实标注无备份；全部测试 + 冒烟 PASS；证据 `artifacts/evidence/N-05.txt`。
- **依赖**：N-02（勾选语义）、N-04（门禁语义）。

### N-06 附件列表（R10，只读展示）

- **背景核实**：后端能力已具备——`AttachmentIndex`（附件 ID、引用状态：被 N 会话引用/确认无引用/不可判定、悬空引用）与 `RootLocator` 的 `attachments` 根均已实现；UI 层零接线。
- **边界（改）**：
  - 主窗口新增"附件(&A)"入口（菜单或操作区按钮），打开 `AttachmentListDialog`：列 = 附件 ID、大小、引用状态、引用会话数（可展开引用会话 ID）；底部展示悬空引用与"不可判定"提示（存在未解析会话时明确标注 `GlobalOrphanConclusionAvailable=false`）。
  - 数据流：扫描根 → 枚举 `attachments` 根下附件 ID/大小（经 PathGuard 白名单）→ 读取全部会话转录文本 → `AttachmentIndex.Build`；打开时后台构建，可取消。
  - 冒烟：合成 fixture 含被引用/无引用/悬空三种附件，断言列表与状态正确。
  - 文档：`docs/interfaces.md`（UI 契约）、`docs/testing.md`、`CHANGELOG.md`。
- **边界（不改）**：附件**删除**本轮仍无 UI（仅列出；删除规则的门禁调整已在 N-04 完成于计划层）；`AttachmentIndex` 判定逻辑不变；附件文件只读，不写不删。
- **验收**：fixture 测试覆盖三种引用状态 + 不可判定场景；冒烟断言列表行数/状态与 `AttachmentIndex` 结果一致；真实根只读打开一次记录数量（附件数、悬空数）于证据；全部测试 + 冒烟 PASS；证据 `artifacts/evidence/N-06.txt`。
- **依赖**：N-02（复用勾选/列绑定模式，非强依赖）；与 N-03/N-04/N-05 无冲突。

### N-07 删除对话框自适应布局 + 居中实证（R6 + R4）

- **边界（改）**：
  - `CleanupDialog` 绝对坐标改 Dock/TableLayoutPanel：标签区顶部、清单 `Dock=Fill`（列宽含 `FillWeight`）、按钮行底部；`MinimumSize` 设定；缩放/最大化时清单与按钮不遮挡。
  - R4 实证：运行冒烟/手工核验 `CleanupDialog` 打开时矩形中心 = 父窗中心（`CenterParent` 已设，若实测不居中再定位原因修复）；冒烟新增居中断言（比较 ScreenToScreen 矩形）。
- **边界（不改）**：对话框数据流与事件；其余对话框（已 CenterParent）。
- **验收**：640×420 与 1280×800 两档缩放下截图/矩形断言：按钮完整可见、清单随窗伸缩；居中断言 PASS；证据 `artifacts/evidence/N-07.txt`（含 R4"已满足/已修复"结论）。
- **依赖**：N-05（同文件收尾）。

### N-08 framework-dependent 发布（R9）

- **边界（改）**：
  - `publish.ps1`：去掉 `--self-contained`（改 `-p:SelfContained=false`，保留 `-r win-x64 -p:PublishSingleFile=true`）；`THIRD-PARTY-NOTICES` 移除".NET 运行时捆绑"条目；README 摘要改"需预装 .NET 10 Windows Desktop Runtime（x64）"。
  - 文档：`docs/release.md` §1/§3、`docs/requirements.md` REQ-017/TC-17（"可独立运行"→"依赖已安装运行时"）、`CHANGELOG.md`。
- **边界（不改）**：SHA256SUMS/LICENSE/离线门禁（TC-18）；`enter-env.ps1` 开发环境不变。
- **验收**：`publish.ps1` 成功；EXE 体积显著下降（证据记录 MB 数，预期自包含 ~150MiB → 数 MiB 量级）；在装有 .NET 10 Desktop Runtime 的机器启动冒烟 PASS；未装运行时的负向说明如实记录（如无机可测则标注未验证）；证据 `artifacts/evidence/N-08.txt`。
- **依赖**：无强依赖；建议倒数第二，给 N-09 发布用。

### N-09 全量回归 + 冒烟 + 发布 v0.2.0（D6）

- **边界**：build/test/format/漏洞门禁；合成 fixture `--smoke` 全 PASS；`publish.ps1` 出 `dist/v0.2.0/win-x64`（framework-dependent）+ SHA256SUMS；真实根**只读**扫描核验（45 会话、归档列 16、无 Corrupt）；`docs/release.md`、`CHANGELOG.md` 定稿。
- **验收**：逐项 PASS、ExitCode=0；发布哈希落盘；真实根核验记录 `artifacts/evidence/N-09.txt`；Windows 10 干净 VM 未验证则如实标注。
- **依赖**：N-01～N-08 全部完成。

## 3. 执行顺序与理由

**N-01 → N-02 → N-03 → N-04 → N-05 → N-06 → N-07 → N-08 → N-09**

- N-01 先定列集合，N-02 一次改齐表格（勾选/序号/自适应），避免冒烟反复改。
- N-04（门禁）先于 N-05（按钮搬迁）：N-05 的 CleanupDialog 改造依赖"备份可选"语义。
- N-06（附件列表）复用 N-02 的列绑定模式，置于其后；N-07 与 N-05 同文件（CleanupDialog），顺序收尾。
- N-08 独立，置于发布前。
- 每轮结束跑：`build -warnaserror` + 全部测试 + `format --verify` + 相关冒烟；证据 `artifacts/evidence/N-xx.txt` 落盘后方可勾选。

## 4. 不做项（本轮边界外）

- `SettingsDialog` 菜单接线与根路径设置 UI（N-03 只接窗口字段）。
- 归档会话的筛选/差异化清理策略（仅展示标识）。
- 附件**删除** UI（本轮仅列出；附件删除门禁已在 N-04 放宽到计划层）；缓存/桌面清理规则不变。
- 不新增联网、遥测、自动更新；workspace.json 保持只读。

## 5. 任务勾选追踪

> 规则：任务全部验收项满足且证据 `artifacts/evidence/N-xx.txt` 落盘后，方可将对应项勾选为 `[x]`；任何一项未满足不得勾选，须标注失败点并停止，等待用户决定。勾选后提交并推送一次，再进入下一未勾选项。

- [x] N-01 归档会话识别与列表列（证据 `N-01.txt`）
- [x] N-02 表格重构：勾选框 + 序号 + 三列自适应（证据 `N-02.txt`）
- [x] N-03 窗口位置/大小持久化（证据 `N-03.txt`）
- [x] N-04 删除门禁放宽：备份改为可选 + ADR（证据 `N-04.txt`）
- [x] N-05 备份按钮移至主窗口（证据 `N-05.txt`）
- [x] N-06 附件列表（只读展示）（证据 `N-06.txt`）
- [ ] N-07 删除对话框自适应布局 + 居中实证（证据 `N-07.txt`）
- [ ] N-08 framework-dependent 发布（证据 `N-08.txt`）
- [ ] N-09 全量回归 + 冒烟 + 发布 v0.2.0（证据 `N-09.txt`）

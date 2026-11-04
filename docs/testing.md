# 测试计划与发布门槛（Testing）

## 1. 测试隔离

新计划 T15：WorkspaceSyncTests / WorkspaceSyncOutcomeTests 新增 44 项，验证部分失败、实际中断材料复查/恢复、
恢复自身中断、真实 ACL/共享租约、并发新目标保留、损坏记录拒绝、复查后新操作重试及脱敏独立结果。
全量 607 项通过；Windows --post-audit-only 44 项通过，检查未请求回调与两种结果窗口（截图已检查）。
证据：artifacts/evidence/change-plan-2026-10-20/T15.txt。请求/恢复/重试 UI 由 T16 单独验证，生产写入仍关闭。

新计划 T14：WorkspaceMetadataWriterTests 新增 55 项，验证严格门禁、原身份/哈希、精确语义变换、
真实重解析/硬链接/ACL/共享冲突、句柄租约、两次不覆盖重命名、中间中断和新目标保留。
全套 563 项通过；生产门禁仍关闭，恢复协调和 UI 未包含。证据：artifacts/evidence/change-plan-2026-10-20/T14.txt。

新计划 T13：WorkspaceBackupTests 新增 20 项，验证原始字节、哈希/路径/操作身份、幂等复用、
损坏/缺失/链接/锁定拒绝、源变更阻断及隔离恢复演练；全套 508 项通过。
没有生产恢复或替换测试结论，证据：artifacts/evidence/change-plan-2026-10-20/T13.txt。

新计划 T12：`WorkspaceMetadataProposalTests` 验证提议筛选、实际合成物理删除/跳过的依赖、
负向证明数据、只存在元数据而没有原日志目标、当前快照绑定、引用顺序及源 JSON/哨兵不变。
提议构造器不执行元数据写入；其测试不覆盖 T16 交互；T16 的独立 UI 证据见 T16.txt。
证据：`artifacts/evidence/change-plan-2026-10-20/T12.txt`。

新计划 T11：`SessionOutcomeTests` 覆盖文件/会话计数分离、未知计数、日志闭合错误码、序号化会话、
不泄露路径/正文/凭据形状内容、错计划复查降为未知及旧日志兼容。Windows `--post-audit-only` 已扩展结果窗口
三页与日志绑定检查，复用 `.artifacts/T10/ui-*` 合成夹具；本轮日志在 `.artifacts/T11/`，证据见 T11.txt。

新计划 T10：WindowsTests 的 `--post-audit-only` 在 `.artifacts/T10/ui-*` 执行真实异步删除完成回调专项。
仅对合成夹具注入 Inactive 进程并删除选中文件，验证物理成功后仍显示元数据残留；另在预审计后注入损坏
Workspace，验证复查未知。保留未选日志、Workspace（故障注入后恢复）和根外哨兵哈希。真实生产进程门禁不改。
命令和结果见 `artifacts/evidence/change-plan-2026-10-20/T10.txt`。

新计划 T09：WindowsTests 的 `--audit-only` 执行真实 MainForm 删除前审计/确认页专项。
合成数据、缓存、附件、Workspace、截图与哨兵保存在 `.artifacts/T09/ui-*`；检查审计不扩大原计划、
范围外/共享/未知显示、计划不匹配拒绝确认及取消后哈希不变。证据见 `artifacts/evidence/change-plan-2026-10-20/T09.txt`。

2026-10-20 新计划 T06：`dotnet run --project tests/DeepSeekHarnessCleaner.WindowsTests/DeepSeekHarnessCleaner.WindowsTests.csproj --no-build -- --roots-only`
运行根选择与跨根状态失效专项。夹具和设置均隔离于 `.artifacts/T06/ui-*`；不使用真实设置或真实会话根。
专项检查多候选、独立桌面确认、手选后的重新发现、记忆开关、取消、旧确认窗口阻断、跨根同 ID 和旧状态清空。
证据见 `artifacts/evidence/change-plan-2026-10-20/T06.txt`；FolderBrowser 系统弹窗本身未自动操作，测试从返回的手选路径继续执行相同发现流程。

- Unit / Integration 使用 `Path.GetTempPath()` 下自动创建的模拟 `.dsh` 根和模拟桌面根；Windows 窗口专项使用项目 `.artifacts/functional-ui-*` 下新建的合成根。均不对真实 Harness 数据执行删除。
- 脱敏真实数据仅用于**显式授权的只读兼容性测试**；任何带 `File.Delete` 的真实数据测试必须另行独立审批且不得由自动测试执行。
- 模拟文件可由项目内 fixtures 生成器写入，需覆盖有效 v0/v4、多个代际、多个 Zstd frame、截断尾帧、corrupt JSON、未知 event、附件引用、权限拒绝、reparse 点、并发变更。

当前交付验证结果与用户实机确认见 [验收记录](acceptance.md)。历史执行过程、早期未执行计划及逐任务证据见 [归档](../archive/2026-10-20/README.md)。下列矩阵保留为后续变更的测试要求，不表示本轮重跑。

## 2. 核心用例矩阵

| TC | 验证目标（对应 REQ） | 关键断言 |
|---|---|---|
| TC-01 | 根路径发现 | 默认/自定义/不存在/假目录必须准确归类；不触碰 workspace 外根 |
| TC-02 | 只读扫描 | 对 fixture 的 file list、长度、mtime、SHA 前后完全一致 |
| TC-03 | 代际聚合 | v0+v4 同一 ID 合并且保留两个物理文件，冲突时禁删 |
| TC-04 | Zstd/JSONL | 多帧按序、明文兼容；坏尾/不完整 JSON 标记 Partial/Corrupt |
| TC-05 | 列表 | 统计件数/大小/日期/项目可解释；归档独立显示已归档/未归档/未知，解析状态正常也有明确值 |
| TC-06 | 预览 | 已验证 v4 角色文本正确；旧版未知事件标记不完整、不崩溃 |
| TC-07 | 日期/搜索 | inclusive 日期、跨时区、无日期、ID/项目/关键字都正确 |
| TC-08 | 多选 | 选中仅整会话；取消/重选与过滤后的选择结果正确 |
| TC-09 | ZIP 备份 | 原始代际全部入包；逐文件 SHA 相同；失败没有清理副作用 |
| TC-10 | 可读导出 | Markdown/JSON 字符串安全编码，未知事件有 incomplete 标记 |
| TC-11 | 精确删除 | 删除已选择目标；隔离树外和未选文件 hash 不变，失败逐项列明 |
| TC-12 | 会话缓存 | 单会话只删匹配 JSON；全量缓存需显式选择，workspace.json 不变 |
| TC-13 | 桌面缓存日志 | 仅允许 6 个白名单目录；浏览器状态和 Network 均不变 |
| TC-14 | 附件 | 未证明孤儿时标 Unknown；名称/类型/关联会话有据可查；单文件独立确认删除，备份可选 |
| TC-15 | 强制阻断 | 运行进程/无法检查/软链/出根/指纹变化必拒绝删除 |
| TC-16 | 结果 | 部分失败、取消、权限拒绝、容量统计与重扫状态准确 |
| TC-17 | 环境发布 | 本地 SDK 包缓存独立；确认单文件包不捆绑 .NET 运行时，在安装 .NET 10 Windows Desktop Runtime x64 的干净 Windows 10 VM 上验证启动/退出，并检查缺少运行时的提示 |
| TC-18 | 离线 | 运行 EXE 期间网络请求检查零新增主动请求；无自动更新 |
| TC-19 | 配置 | 默认/损坏/未知版本处理正确；记忆关闭时不写根路径 |
| TC-20 | 安全失败 | 未知格式、权限、空数据、坏压缩、异常目录均无误删/卡死 |

## 3. 非功能测试

窗口交互变更另测父窗体居中、缩放布局、长文本和路径复制、真实横纵滚动；附件右键命中及默认应用失败路径；同目录窗口状态保存恢复与写入失败；筛选/隐藏勾选/全选；表头实际绘制。默认应用测试仅打开合成文件，状态文件仅写隔离应用目录。

- PERF-01：Windows 10 x64 + SSD + ≥8GB RAM，在 1000 个小会话 ≤200MB 的 fixtures 上计时，完整元数据初扫 ≤15 秒；筛选响应 ≤200ms，工作集 <512MiB；记录机器 CPU/SSD/Build，失败必须修复或用户明确豁免。
- UX-01：125%/150% DPI、中文目录/Emoji 文本、键盘 Tab、窗口缩放、长路径、未选择会话禁用删除，验证 UI。
- SEC-01：从目录外的文件通过 reparse point 或 `..` 逃逸到可控根时禁止清理；测试文件内容 hash 仍然相同。
- SEC-02：活动 Harness 进程、权限不可见的托管进程、占用文件、不一致清单均阻断清理。
- SEC-03：选择备份时，人为破坏 ZIP 的单字节后校验必须失败并阻断该备份路径；无备份路径须明确选择；删前文件变化仍须跳过并重新生成计划。
- COMPAT-01：实测 DeepSeek Harness 0.2.0.0 V4 与旧版日志（允许脱敏只读样本）；报告物理解码/语义预览分开结论。
- WIN10-01：记录 `winver` 的 Edition/Version/Build 及客户端安装的 .NET 10 Windows Desktop Runtime x64 版本；测试不捆绑运行时的 WinForms 单文件启动、中文 UI、操作完成和退出。缺少所需运行时时须检查依赖缺失提示，不得宣称无需安装 .NET。官方支持范围无法覆盖的 Windows 10 SKU 必须诚实标注实测而非官方支持。此处为验收要求，不代表当前包已通过测试。

## 4. 任务级与发布级检查

任务级：由每个 T 编号绑定具体 TC/命令；任务不得仅以“代码写完”通过。发布级必须同时满足：

- `dotnet build -warnaserror` 通过，`dotnet test` 全部通过，`dotnet format --verify-no-changes` 通过。
- TC-01～TC-20 全部已执行且有证据，安全必选 TC-02/09/11/15/20 不允许豁免。
- PERF-01、SEC-01～03、COMPAT-01、WIN10-01 完成；`dotnet package vulnerability` 同等查询结果存档。
- 在预装 .NET 10 Windows Desktop Runtime x64 的离线、干净 Windows 10 x64 VM 上通过 Smoke；发布 SHA-256 已记录；仅对测试 fixtures 执行删除。

## 5. 测试证据格式

每项证据：`日期时间/提交 hash/SDK 版本/Windows Build/命令/退出码/通过断言/失败详情/证据文件路径/审核者或 Agent`。示例：`artifacts/evidence/T-09-backup-validation.txt`。勾选任务必须引用一次有效证据；不允许空证据或伪造截图。

## T16 当前验证与总体缺项

WorkspaceSyncOptionTests：13 项只读可用性用例；全量 620 项通过。
Windows `--workspace-sync-only`：72 项检查（默认/取消/未勾选/已勾选/中断恢复重试/源与进程变化/不安全目录/禁止根切换）。
原功能 91 项、`--post-audit-only` 44 项、`--roots-only` 21 项通过；实际显示环境 DPI=96，默认和最小窗口截图已核对。
结果/恢复 UI 只使用生成数据；未勾选分支保持原元数据字节，勾选分支必须有原始备份，未选日志与哨兵哈希不变。
证据：artifacts/evidence/change-plan-2026-10-20/T16.txt，原始日志/截图在 .artifacts/T16/。

总体审计尚待完成。明确未实测：真正的进程强制终止/断电（已有为确定性进程内中断）、实际发行/后端/脱敏样本映射、
多 DPI/VM/发布安装验证。按各门禁范围处理，不能从合成测试推断生产可写或发布完成。

## A05 最终审计新增验证

`WindowsTests --workspace-crash-only` 在8个写入阶段等待明确的活跃子进程握手后强制终止该子进程，
不执行受测写入器的取消/finally路径。120项检查通过，实际退出码与磁盘恢复证明在 `.artifacts/FinalAcceptance/`。
新增收尾窗口：完成记录已落盘且锁已退役、但返回结果前退出；只有完整匹配证明才确认已应用，损坏记录仍拒绝。
新增观察脱敏样本只在合成根中解析，带来源哈希、独立文件时间/观察时间和明确替换规则；不在测试中读取真实路径。
全量624项、构建和格式检查通过；默认程序真实打开合成txt/png另有5项通过。
这补齐进程强制终止模拟，不宣称硬件断电/OS/VM崩溃；A04实际发行/后端/root映射仍未完成。
证据：artifacts/evidence/change-plan-2026-10-20/A05-process-crash.txt。

## A04 发行与配置门禁验证（2026-10-20）

全量681项通过，0失败/跳过；在原636项上增加30项配置语义、13项配置输入/包解析及2项写入绑定漂移测试。
公开协调器使用本机已核验发行资产、生成的隔离profile与临时进程级DSH_HOME执行11项端到端检查，使用模拟Inactive探针；未访问真实数据作为写入目标。
实际profile配置只读核验通过；Windows同步72项、原功能91项、根选择21项、后审计44项复测通过；8阶段强制进程终止120项复测通过。
构建、格式、NuGet漏洞审计与精确命令见 `artifacts/evidence/change-plan-2026-10-20/A04-production-gate.txt`。
这些结果取代之前阶段的缺项状态；硬件断电、未知插件副作用、多DPI/干净VM及正式发布仍未验证，不扩大验收结论。
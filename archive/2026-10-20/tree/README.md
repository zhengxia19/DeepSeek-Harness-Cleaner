# DeepSeek Harness Cleaner（设计与实施文档）

> 当前修复版：0.2.4，Windows 10 x64，C# / .NET 10 / WinForms。新增附件右键打开、主窗位置大小记忆、选择表头全选及统一表格标题；执行结果报告支持缩放与日志路径复制。原始任务历史保留，本次验收见 `artifacts/evidence/2026-10-20/T-38.txt`。

## 项目目标

一个**离线、只读优先、用户确认后才执行删除**的 Windows 桌面工具，扫描 Harness 本地会话历史，按项目/日期/会话筛选，预览可识别的消息、导出原始会话备份，并独立清理选定会话、投影缓存、桌面缓存、日志及经人工确认的附件。

## 当前发布方式

- Windows 10 x64，Release 单文件 EXE，不捆绑 .NET 运行时；客户端须自行安装 **.NET 10 Windows Desktop Runtime x64**（不是 .NET Framework 4.x）。
- 使用 `scripts/publish.ps1 -Version 0.2.4`，产物为 `dist/v0.2.4/win-x64/DeepSeekHarnessCleaner.UI.exe`；默认参数为 `--self-contained false -p:PublishSingleFile=true`。
- 本次有独立的自动回归及 Windows 窗口测试；以本次证据记录为准，不沿用 0.1.0 的验收结论。详见 [发布说明](docs/release.md)。

## 本次功能修复

- 执行结果报告按父窗体居中，随窗口缩放；日志路径可选择复制，表格可横纵滚动。
- 附件行右键“打开附件”交给系统默认应用。无扩展名对象仅按识别出的 PNG/JPEG/GIF/WebP/PDF 文件头生成独立校验副本；未知类型明确提示，源附件不改动。成功打开的副本保留在工具私有 opened-attachments 目录，不自动清理或联动删除。
- 主窗正常关闭时在 EXE 同目录保存 `DeepSeekHarnessCleaner.window-state.json`，下次恢复位置、正常尺寸及最大化状态。目录不可写时提示并保留旧记录；原通用设置文件不被迁移或覆盖。
- 点击会话“选择”表头全选/取消当前筛选结果，包括需滚动才能看到的行。既有隐藏勾选保留，状态栏明确显示隐藏及总勾选数；删除前核对完整清单。
- 四类表格列标题加粗居中，正文保持原样。实际 100% DPI 已测；150% 实际 DPI 按用户指示由用户验收。

- 会话扫描后可选择原生 v0/v4 会话进入删除确认；重新核对身份、代际集合、JSONL 完整性和文件哈希，可选备份全部代际。Harness 必须完全退出后才能执行删除。
- 预览跳过请求配置、待办等内部状态，展示工具调用名称与工具结果文本；尚未支持的扩展单独提示“未支持”，不再等同于原始数据损坏。预览仍按日志顺序展示，不声称重放完整历史替换。
- 扫描完成后，通过顶部“附件”查看名称、类型、关联会话、大小、引用状态及存储路径，勾选文件后独立确认删除，备份可选。无验证引用时保留 Unknown，不判断孤儿，不随会话自动删除附件。
- 会话“归档”列显示已归档/未归档/未知；“解析状态”列明确显示正常或具体异常。归档缺少可信元数据时保持未知，解析状态不代表运行状态。

## 安全默认值

- 启动与扫描不修改 Harness 目录；所有写入仅发生在用户明确启动的备份/清理操作中。
- 会话和附件删除可选择原始 ZIP 备份，也可不备份直接确认删除；进程退出、路径及文件复验仍为必要条件。不备份删除的文件无法由本工具恢复。
- Harness 未完全退出、运行状态不明、路径越界或文件扫描后已变化时，禁止清理。
- 保留 `.credentials.yaml`、`profiles/`、`dsh-runtimes/`、`workspace.json`、`Local Storage/`、`Session Storage/`、`Network/` 及匿名身份标识；这些均不属于默认可删除目标。
- 不调用模型 API、DeepSeek 云端、浏览器或联网遥测；不处理服务器端已上报历史。
- 旧格式/未知事件可安全原样备份，不假定一定能完整显示为聊天消息。

## 本机基线（来自用户提供的只读检查，非本项目现场复验）

- 会话：`%USERPROFILE%\.dsh\sessions\`；检查时 19 个压缩日志，约 3.16 MB，含 `session.jsonl.zstd` / `session.v4.jsonl.zstd`。
- 附件：`%USERPROFILE%\.dsh\attachments\`；检查时约 0.08 MB。
- 会话缓存：`%USERPROFILE%\.dsh\storages\session_projcache\` 与 `session_projcache.json`。
- 桌面数据：`%APPDATA%\@deepseek-ai\dsh-desktop\`；仅特定 Cache 目录和 `logs` 在清理范围内。
- 以上是目标路径模板，不硬编码用户名 `zx`；若用户通过 DSH_HOME 指向其他位置，由人工选择受控根目录并重新验证。

## 项目结构（实施时创建）

```text
DeepSeek-Harness-Cleaner/
├── README.md
├── CHANGELOG.md
├── CONTRIBUTING.md
├── global.json                       # 在 T-02 中写入确切 SDK 版本
├── Directory.Build.props
├── Directory.Packages.props          # NuGet 中央依赖版本
├── NuGet.Config
├── .gitignore
├── scripts/                          # 本地 bootstrap/build/test/publish 脚本
├── src/                              # WinForms / Application / Domain / Infrastructure
├── tests/                            # Unit / Integration / WindowsSmoke
└── docs/
    ├── requirements.md
    ├── architecture.md
    ├── design.md
    ├── development.md
    ├── coding-standard.md
    ├── dependencies.md
    ├── interfaces.md
    ├── testing.md
    ├── release.md
    ├── implementation-tasks.md
    ├── review-report.md
    └── decisions/
        ├── 0001-technology-selection.md
        └── 0002-safe-deletion-and-backup.md
```

## 开发入口

1. 阅读 [需求](docs/requirements.md) 与 [详细设计](docs/design.md)。
2. 严格按 [原子实施任务](docs/implementation-tasks.md) 的依赖链推进，不能越过未验收任务。
3. 按 [开发环境](docs/development.md) 创建项目本地 SDK 与隔离的 NuGet 缓存。
4. 根据 [测试](docs/testing.md) 生成脱敏/合成样本；**不得把真实用户数据作为自动清理测试对象**。
5. 发布前执行 [发布清单](docs/release.md)，含 Windows 10 版本兼容性验收。

## 文档阅读顺序

需求 → 架构 → 详细设计 → ADR → 环境与依赖 → 接口 → 测试 → 原子任务 → 发布；`review-report.md` 存放设计复审与遗留验证项。技术真相以相应权威文件为准，杜绝在多处复制可变配置/协议定义。

## 明确限制

- 不提供恢复、修改会话消息、云端历史删除、禁止 Harness 上报、管理 Harness 配置功能。
- 备份 ZIP 是**原始本地文件快照**，不是官方导入协议，也不保证可直接在 Harness UI 中恢复会话；恢复操作需人工依照备份清单完成，官方的 Web ZIP 导出亦不等于可直接导入。
- 目标 Windows 10 具体版本/版本生命周期尚未知。微软当前对 .NET 10 的 Windows 10 官方支持主要限定仍受支持的 LTSC/Enterprise 版本，普通已停止支持的版本仅允许作为兼容性实测、不宣称官方支持。

## 参考来源

- 用户上传的《软件开发项目文档集说明.md》：文档类型、职责和更新规则。
- 用户提供的 2026-10-16 本机只读核查文字：安装结构、数量、路径和风险边界。
- [官方会话持久化](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/session/session-persistence-jsonl/README.md)，[官方 Session 事件模型](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/core/session/README.md)，[官方投影缓存](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/session/session-projection-cache/README.md)。
- [Microsoft Windows 上安装 .NET](https://learn.microsoft.com/en-us/dotnet/core/install/windows)。


## 2026-10-20 修复任务与实施状态

详细追踪：[28 项修复清单](docs/2026-10-20-修复任务清单.md)。

本次迭代已提交源码：从已验证的 `session/title` 事件读取会话标题（明文及有界 zstd）、标题列表与筛选、可输入项目候选、对齐筛选面板、行与批量复选分离、预览防抖/大小限制/取消传递、删除确认标题及双向滚动、备份目录选择及资源管理器打开目录，以及长警告换行。

**验证状态：本地 Release 解决方案编译及不捆绑运行时的单文件发布已通过；未执行单元测试、客户端启动或 Windows 10 界面/性能验收，因此不是已验收发布版本。** 详细命令与产物记录见修复清单；标题提取和筛选测试仅参与编译，尚未运行。

**安全边界：** 当前官方 DSH SessionPersistence 与 Workspace 公开接口没有可证实的、覆盖 live Agent/内存状态/注册表/磁盘一致性的通用安全删除 RPC；停止进程以外的会话删除仍按现有机制阻断。社区 fork 中的 `workspace.deleteSession` 方案不能当成官方已发布能力。运行中已空闲或未加载的会话也不能仅根据文件时间判断安全可删。

相关上游：[持久化接口](https://github.com/deepseek-ai/deepseek-harness/blob/master/docs/subsystems/persistence.md) · [标题事件](https://github.com/deepseek-ai/deepseek-harness/blob/master/docs/subsystems/session-title.md) · [社区删除能力讨论](https://github.com/deepseek-ai/deepseek-harness/discussions/3075)。

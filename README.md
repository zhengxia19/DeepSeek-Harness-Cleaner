# DeepSeek Harness Cleaner（设计与实施文档）

> 状态：**已实施并通过 T-01～T-33 验收；0.1.0 发布包已构建（`dist/v0.1.0/win-x64/`），最终发布复核见 `artifacts/evidence/T-34.txt`**。文档日期：2026-10-18。目标：Windows 10 x64，DeepSeek Harness 0.2.0.0 本地数据（0.2.1-rc.2 只读识别、禁止清理）。方案 B：C# / .NET 10 / WinForms。

## 项目目标

一个**离线、只读优先、用户确认后才执行删除**的 Windows 桌面工具，扫描 Harness 本地会话历史，按项目/日期/会话筛选，预览可识别的消息、导出原始会话备份，并独立清理选定会话、投影缓存、桌面缓存、日志及经人工确认的附件。

## 安全默认值

- 启动与扫描不修改 Harness 目录；所有写入仅发生在用户明确启动的备份/清理操作中。
- 清理会话必须先完成 ZIP 原始备份及逐文件 SHA-256 校验；用户不能关闭该保护（v1）。
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

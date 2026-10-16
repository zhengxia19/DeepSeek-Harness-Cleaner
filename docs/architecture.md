# 架构设计（Architecture）

## 1. 决策与部署

- 方案：C# / `.NET 10` / `net10.0-windows` / WinForms，自包含 `win-x64`。使用三层逻辑分离，**同一进程**运行，无常驻服务、SQLite、WebView2、消息队列或远端 API。
- UI 线程只处理输入和绑定；所有文件扫描、解压、导出、删除通过异步任务与取消令牌驱动。
- 强制先验证并备份再清理；缓存与附件归属保守处理。

## 2. 模块关系

```text
WinForms.UI (Views/Presenters)
  -> Application (Scan/Filter/Preview/Backup/Cleanup orchestration)
     -> Domain (SessionId, StorageEntry, CleanupPlan, enums, policies)
     -> Infrastructure (FileScanner, ZstdJsonlReader, Exporter,
                        SafeFileOps, RuntimeProbe, ConfigStore)
  -> UI DTO / Progress callbacks
```

**禁止逆向依赖**：Domain 不引用 System.Windows.Forms；Infrastructure 不控制 UI；核心逻辑以接口替代控件耦合。

## 3. 模块边界

| 模块 | 输入/输出 | 核心职责 | 不负责 |
|---|---|---|---|
| RootLocator | 环境与用户选择→ResolvedRoots | 识别 `.dsh`/桌面数据路径并验证结构 | 自动删除 |
| SessionInventory | 会话目录→SessionSummary[] | 分组同 ID 多物理代际、头部和大小信息 | 自动迁移格式 |
| EventReader | 一份日志→受限事件流 | Zstd/JSONL、错误分类、流式上限 | 修改日志 |
| TranscriptProjector | 事件流→Preview | 仅按已验证映射展示角色/文本 | 执行 Harness 事件迁移 |
| AttachmentIndex | 会话已验证引用→附件状态 | 关联证据/不确定性标记 | 猜测孤儿文件 |
| SelectionService | 筛选条件→SessionSet | 日期/ID/项目检索与多选 | 删除会话内部分消息 |
| BackupService | 选定源文件→ZIP+manifest | 保全原始字节，hash 和归档复核 | 自动回导 Harness |
| CleanupPlanner | 选择与白名单→计划 | 列出每一目标及前置条件 | 执行 I/O |
| CleanupExecutor | 锁定计划→结果 | 停止使用检查、路径/指纹复核与逐项删除 | 修改未入选文件 |
| UiController | 命令↔DTO | 更新列表/预览/进度、确认危险操作 | 直接操作文件系统 |

## 4. 数据流

- **扫描**：校验根 → 遍历（不跟随 reparse point）→ 收集物理文件 → 尝试头部读取 → 按会话标识聚合 → 统计 → UI。
- **预览**：用户打开会话 → 选择格式代际最高的**已验证可读取**日志 → 流式解压 → 事件解析 → 支持的消息投影；解析不了显示原因，不用旧版本静默顶替当前代际。
- **备份**：用户选会话 → 构造有界、根内文件清单 → 复制到 ZIP 临时文件并逐项计算 hash → 重读 ZIP 校验 → 原子移动/发布正式备份；失败删临时产物，不触发删除。
- **清理**：仅 UI 显式按钮 → 清理计划预览 → 进程检查 → ZIP 完成 → 用户确认 → 每文件二次验证 → 执行 → 结果明细 → 重扫刷新。
- **日期**：createdAt 来自 header，lastActivityAt 仅来自可信事件时间戳；缺失时使用 `mtime` 并显式标注来源，不能把 `mtime` 伪装成活动日期。

## 5. 文件区域边界

- `SessionRoot`：`%USERPROFILE%\.dsh\sessions`，会话日志；仅删除已经列入计划的规范文件。
- `StorageRoot`：`%USERPROFILE%\.dsh\storages`，允许 `session_projcache/sessions/<ID>.json`（身份已验证时）和显式全量 `session_projcache/`、`session_projcache.json`；`workspace.json` 不在范围。
- `AttachmentRoot`：`%USERPROFILE%\.dsh\attachments`，仅显式选取的普通文件；不递归删除任意未知目录。
- `DesktopRoot`：`%APPDATA%\@deepseek-ai\dsh-desktop`，仅 `Cache`、`Code Cache`、`GPUCache`、`DawnGraphiteCache`、`DawnWebGPUCache`、`logs`。
- `BackupRoot`：默认 `%USERPROFILE%\Documents\DeepSeekHarnessCleaner\Backups`，必须与清理根分离；用户可另选。

## 6. 并发与故障模式

只允许 **1 个写入操作**；扫描/预览进行时禁止删除，或先取消读任务并等待退出。通过进程探测、全路径根验证、reparse 点拒绝、文件元数据与 SHA-256 双校验防止典型误删；对无法证明安全的目标 fail closed。并发修改无法跨目录获得完美事务锁，所以清理报告必须容纳部分完成、重扫并提示剩余项目。

## 7. 技术和限制

- WinForms 原生控件：TreeView/ListView 或 DataGridView + SplitContainer + RichTextBox，虚拟行/分页，后台扫描。
- `System.Text.Json`、`System.IO`、`System.IO.Compression`、`ZstdSharp.Port`；不用外部脚本清理真实数据。
- 不承诺官方 Harness 文件格式的永久兼容。格式变化优先退化为只读元数据/原始备份。
- .NET 10 官方 Windows 10 支持限定仍受支持的 LTSC/Enterprise；普通 Windows 10 版本兼容必须现场测试，不能标为官方支持。

详细状态机、数据模型、失败策略见 [design.md](design.md)，选型理由见 ADR。

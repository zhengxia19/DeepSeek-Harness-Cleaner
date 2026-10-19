# 接口与数据契约（Interfaces）

## 1. 对外接口

本工具**无网络 API、HTTP 监听端口、CLI 清理命令或注册表协议**。用户通过 WinForms GUI 操作；工具与 Harness 的唯一集成为受控目录只读扫描和用户确认后的文件清理。

## 2. 业务接口（设计契约）

```csharp
interface IRootLocator { ResolvedRoots Resolve(string? selectedDshHome, string? selectedDesktopHome); }
interface ISessionInventory { Task<IReadOnlyList<SessionSummary>> ScanAsync(ResolvedRoots roots, CancellationToken ct); }
interface IEventReader { IAsyncEnumerable<EventRow> ReadAsync(PhysicalLog log, CancellationToken ct); }
interface IBackupService { Task<BackupResult> CreateVerifiedAsync(CleanupPlan plan, string destination, CancellationToken ct); }
interface ICleanupPlanner { CleanupPlan Build(ResolvedRoots roots, Selection selection); }
interface ICleanupExecutor { Task<CleanupResult> ExecuteAsync(CleanupPlan plan, VerifiedBackup? backup, CancellationToken ct); }
```

接口签名为设计意图；具体源码名由 T-03/T-09 固化，不产生独立对外二进制 API 兼容承诺。

## 3. 磁盘契约

### 输入类型

- 会话目录：每个会话的规范代际日志文件 `session.jsonl[.zstd]` 或 `session.vN.jsonl[.zstd]`；遍历时只读读取 `SessionHeader` / 事件。首行 header 契约为 `{"type":"session","id":…,"version":0–4,"createdAt":<epoch-millis 数值>}`（M-02，详见 design.md §2）。事件行契约为 `{"type":…,"seq":…,"time":<epoch-millis 数值>,"data":…}`；消息正文在 `data`（`user/message`）或 `data.message`（`assistant/system/developer/message`、`tool/result`）（M-03，详见 design.md §2）。
- 投影缓存：`storages/session_projcache/sessions/<id>.json`、`storages/session_projcache.json`；不是权威会话日志。
- 归档标志输入（N-01）：`storages/workspace.json`（与会话根同级）`global.archivedSessionIds` 字符串数组；只读识别，永不写入/删除；解析失败按 §5 `ARCHIVE_*` 状态降级为全部未归档，扫描不失败。
- 桌面清理目录：`Cache`、`Code Cache`、`GPUCache`、`DawnGraphiteCache`、`DawnWebGPUCache`、`logs`。

### ZIP 导出契约（schemaVersion=1）

- 文件条目为原字节，使用 `sessions/<relative>` 或 `attachments/<relative>`；`manifest.json` 记录 root、relativePath、length、sha256、sessionId、警告。
- 对同一日志所有代际一并备份，避免把迁移前的历史文件丢失。
- SHA-256 小写 64 位十六进制；ZIP 内不得出现绝对路径、路径遍历、重复规范化路径。
- 可读导出为 UTF-8 Markdown 和机器可读 JSON 概览，必须带 `exportIncomplete=true`（若有未知事件、超限或附件缺失）。可读导出**不是恢复备份**。
- backup ZIP 的生成属于工具私有格式，不声明与官方 `dsh-session-<id>.zip` 导出完全兼容，也不宣称能用 Harness 官方 UI 导入。

## 4. 设置配置（schemaVersion=1）

位置 `%LOCALAPPDATA%\DeepSeekHarnessCleaner\settings.json`：

```json
{
  "schemaVersion": 1,
  "rememberRoots": false,
  "sessionRoot": null,
  "desktopRoot": null,
  "backupRoot": null,
  "dateField": "fileModified",
  "previewMaxItems": 2000,
  "windowWidth": 1100,
  "windowHeight": 700
}
```

- `rememberRoots=false` 时不得持久化所选绝对路径；默认扫描位置为系统环境推导。
- `previewMaxItems` 固定上限 2000；恶意大值按设计上限截断或报配置错误；不得扩展到未授权根。
- 任何未知 `schemaVersion`：只读提示/回退默认，不覆盖用户已有配置。

## 5. 错误码

| 错误码 | 意义 | 结果 |
|---|---|---|
| ROOT_INVALID | 不是受授权 Harness 目录 | 停止 |
| PATH_REPARSE | 存在符号链接/junction | 拒绝目标 |
| PATH_ESCAPE | 目标不在受控白名单根内 | 拒绝目标 |
| PROCESS_ACTIVE | Harness 仍运行 | 禁止清理 |
| PROCESS_UNKNOWN | 无法可靠确定退出状态 | 禁止清理 |
| FORMAT_UNSUPPORTED | 格式未验证 | 仅只读、原始备份 |
| FORMAT_CORRUPT | Zstd/JSON 破损 | 不进入自动清理 |
| BACKUP_INVALID | 归档未通过校验 | 不删除 |
| FILE_CHANGED | 计划/备份后文件变化 | 停止或跳过 |
| FILE_ACCESS_DENIED | 文件权限拒绝/被占用 | 报告，禁止提权 |
| OP_CANCELLED | 用户取消 | 停止于安全边界并报告部分结果 |
| ARCHIVE_FILE_MISSING | `storages/workspace.json` 不存在 | 扫描不失败；全部会话按未归档展示，状态栏提示“归档信息不可用” |
| ARCHIVE_FILE_TOO_LARGE | `workspace.json` 超过 32MiB 上限 | 同上 |
| ARCHIVE_FILE_UNREADABLE | `workspace.json` 权限拒绝/被占用 | 同上 |
| ARCHIVE_JSON_INVALID | `workspace.json` JSON 解析失败 | 同上 |
| ARCHIVE_STRUCTURE_UNKNOWN | 根/`global` 非对象、`archivedSessionIds` 非数组或含非字符串元素 | 同上 |

## 6. 兼容性与版本

- 文件格式只按受验证的样本适配，未知事件保守退化。
- 配置/备份 manifest 使用 `schemaVersion`；变更不兼容字段时新增版本并留迁移策略；不覆盖旧备份。
- 本工具版本遵循 semver：0.1.0 为首次合格发布，以实测支持矩阵写入 release notes。

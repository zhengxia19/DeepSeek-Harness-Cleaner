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

- 会话目录：每个会话的规范代际日志文件 `session.jsonl[.zstd]` 或 `session.vN.jsonl[.zstd]`；遍历时只读读取 `SessionHeader` / 事件。
- 投影缓存：`storages/session_projcache/sessions/<id>.json`、`storages/session_projcache.json`；不是权威会话日志。
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

### 4.1 主窗自动记忆（2026-10-20 用户指定，已实施）

位置为程序目录 `AppContext.BaseDirectory/DeepSeekHarnessCleaner.window-state.json`，不受进程当前工作目录影响，与上述通用设置文件分离：

```json
{
  "schemaVersion": 1,
  "normalBounds": { "x": 100, "y": 100, "width": 1120, "height": 720 },
  "windowState": "Normal",
  "savedDpi": 96
}
```

- `normalBounds` 为正常窗口外框的屏幕坐标/大小；`windowState` 仅保存 Normal 或 Maximized，最小化关闭按 Normal 恢复。启动时按当前工作区和 DPI 校正，不直接信任越界、极端或无效尺寸。
- 缺失文件使用默认；损坏或未知版本只读回退、不覆盖原文件。保存采用同目录唯一临时文件和原子替换；程序目录只读/文件占用时反馈失败，保留已有有效文件，不另找位置静默保存。
- 仅窗口字段落盘，不包含会话内容、扫描路径或凭据；不因关闭窗体写入真实 Harness 目录。测试必须注入独立应用目录/窗口状态路径，禁止自动测试污染发布目录或用户设置。
- 原 settings.json 的 windowWidth/windowHeight 保留旧 schema 兼容性，不再与自动窗口记录双向同步，防止双重来源覆盖。

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

## 6. 兼容性与版本

- 文件格式只按受验证的样本适配，未知事件保守退化。
- 配置/备份 manifest 使用 `schemaVersion`；变更不兼容字段时新增版本并留迁移策略；不覆盖旧备份。
- 本工具版本遵循 semver：0.1.0 为首次合格发布，以实测支持矩阵写入 release notes。

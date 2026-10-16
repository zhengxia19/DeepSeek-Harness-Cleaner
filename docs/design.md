# 详细设计（Design）

本设计是实现约束，不是对尚未验证的 Harness 0.2.0.0 解析能力的保证。开发按 `implementation-tasks.md` 的事实验证门槛实现。

## 1. 核心数据结构

```csharp
record ResolvedRoots(string SessionRoot, string StorageRoot, string AttachmentRoot, string DesktopRoot);
record PhysicalLog(string FullPath, int Generation, bool Compressed, long Bytes,
                   DateTimeOffset ModifiedAt, string? Sha256, ParseStatus Status);
record SessionSummary(string SessionId, string? Project, DateTimeOffset? CreatedAt,
                      DateTimeOffset? LastActivityAt, DateOrigin DateOrigin,
                      IReadOnlyList<PhysicalLog> Generations, ParseStatus Status);
record PreviewItem(string Role, string Text, DateTimeOffset? At, bool IsPartial);
record AttachmentRecord(string FullPath, long Bytes, ReferenceStatus Status,
                        IReadOnlyList<string> VerifiedRefSessionIds);
record CleanupTarget(string AbsolutePath, CleanupCategory Category, string? OwnerSessionId,
                     long ExpectedLength, DateTimeOffset ExpectedWriteUtc, string ExpectedSha256);
record CleanupPlan(Guid PlanId, DateTimeOffset CreatedUtc, string RootFingerprint,
                   IReadOnlyList<CleanupTarget> Targets, bool RequiresBackup);
record ItemOutcome(string FullPath, ActionStatus Status, long BytesRemoved, string? ErrorCode);
```

枚举：`ParseStatus={Valid, Partial, Unsupported, Corrupt, Unreadable}`；`DateOrigin={Header,Event,FileMtime,Unknown}`；`ReferenceStatus={VerifiedReferenced,VerifiedUnreferenced,Unknown}`；`ActionStatus={Succeeded,Failed,Skipped,Changed}`。上面是设计草案；具体 DTO 名称只在源码中维护。

## 2. 会话发现与物理格式

- 只进入由用户认可的 `SessionRoot` 下的普通目录，不跟随 NTFS junction / symbolic link / reparse point；禁止层级越界。
- 会话目录名称不能替代逻辑身份；读取 header `id` 后确认目录与头标识的映射；无有效 header 的日志列入“异常文件”，允许原始备份但不允许自动清理。
- 文件名白名单：`session.jsonl`、`session.jsonl.zstd`、`session.vN.jsonl`、`session.vN.jsonl.zstd`（N 为正整数）；大写/临时/备份/不匹配项不自动删除。
- 同一会话保留多个 format generation 是正常的；扫描归组，同一目录不应把多个代际计为多个会话。用于预览的代际为 numerically highest canonical generation；**最高代际无法读取时显示异常，不回退到旧代际假装当前内容**。
- 同一个会话目录同时存在相同代际 `.jsonl` 与 `.jsonl.zstd` 或 header 身份冲突，标记 Ambiguous，阻止清理。
- 物理读取：以 Zstandard 串接帧解码，分割 UTF-8 JSONL，每行含一个可解析对象。解压库对 *concatenated frames* 和 *truncated tail* 的处理先由 T-04 的 fixture 验证；不能把最后一帧截断当成正常 EOF。
- 限制：单行 ≤8MiB、单会话解压总量 ≤256MiB、单次预览 ≤2000 行；达到限制显示截断提示，不记录消息正文到日志。备份不限于能否完成语义解析，可按原始字节备份。
- **历史格式**：对于 v0-v3，物理解码与安全只读元数据为最低保证；不在 v1 强制重新实现官方的完整多阶段迁移。仅对经 fixture 验证的事件提供文本预览；对不支持的事件显示其类型/行号与“不完整”。V4 的消息角色和内容也只基于验证的事件 schema。

## 3. 时间规则

- `createdAt`：有效 header 值；不能从目录名猜测。
- `lastActivityAt`：仅使用经 schema 验证的事件时间戳，缺失则标记不确定；文件 `LastWriteTimeUtc` 仅可作为文件修改时间兜底筛选，UI 标注“文件修改时间（非会话活动）”。
- 筛选模式：创建日期/最近活动日期/文件修改日期，默认“文件修改日期”以实现所有记录可筛选，用户可切换；日期均以本机当前本地时区的 `DateOnly` 包含式区间 `[start 00:00, day-after-end 00:00)` 计算；不支持将未知日期硬填为今天。
- 排序稳定：日期降序 → 会话 ID 升序；没有日期的条目排末尾。
- 日期筛选**只控制要删除哪些完整会话**，绝不编辑日志内部事件。

## 4. 消息预览

- 标准显示用户/助手/工具角色和文本块；工具参数、隐藏推理内容等默认不展开，避免 UI 混淆与过度显示。
- 事件类型/角色映射由脱敏 fixture 的断言绑定，不以看到字段 `role` 为充分证明；对于删除无影响的未知事件输出简洁摘要。
- 文本控件使用纯文本赋值，禁止 Markdown/HTML 脚本执行或自动加载远程内容；不渲染外部图片 URL。对大型会话可取消/分页。

## 5. 附件引用策略

- 建立所有已成功解析会话中的显式附件 ID/路径引用证据；引用关系必须匹配本机附件布局，并经白名单验证。
- 如有旧格式无法解析或缺少全局会话，**绝不可声明任何附件为全局孤儿**；显示 `Unknown`。
- 会话删除不自动删除附件，无论是否被引用；用户可在附件页单独选择，先进行原始备份后再精确删除，且展示“引用不可判定”警告。
- 对目录级附件、外部软链和未知类型禁止自动删除。附件删除不能扩大到其他 Harness 配置/缓存。

## 6. 缓存策略

- **单会话缓存**：只有当 `session_projcache/sessions/<ID>.json` 与会话 ID 精确对应且未发生同名冲突时，才可随会话删除加入同一计划；存在缓存结构不一致时跳过并提示，不修改缓存内容。
- **全量会话缓存**：用户明确勾选可清理 `session_projcache/` 内经验证的普通文件，以及 `session_projcache.json`；包含其他存储域一律跳过；此动作需完全退出 Harness。
- **桌面缓存**：目录白名单：`Cache`、`Code Cache`、`GPUCache`、`DawnGraphiteCache`、`DawnWebGPUCache`。只删除其内部普通文件和清空后目录；不删 `Local Storage`、`Session Storage`、`Network`。
- **日志**：仅清理 `%APPDATA%\@deepseek-ai\dsh-desktop\logs` 的普通日志文件，支持按文件修改日期筛选。不得从日志文本抽取并对外输出会话内容。

## 7. 路径安全与预检

1. 使用 `Path.GetFullPath` 规范化；检测受控根是明确受授权的 Harness 布局；拒绝驱动器根/用户目录根/项目源目录。
2. 从受控根到目标的每一级路径检查是否带 `FileAttributes.ReparsePoint`，并在操作前重新检查；`GetFullPath` 本身不能消除软链风险。
3. 仅准许由扫描器在白名单内识别的目标；比较路径必须使用 `Path.GetRelativePath` 并确认不为 `..` 或根外，按 Windows 文件系统大小写规则比较；禁止 UNC/设备路径、ADS 流路径及未授权根。
4. 检查目标是普通文件，不允许写入/创建路径，也不自动提权或绕过 ACL。
5. 删除前再次读取长度、写入时间、SHA-256 并与计划值比对；若发生变化，跳过该项并报告。操作前再次检查 Harness 是否完全退出。
6. 进程探测至少覆盖已知 `DeepSeek Harness.exe`，明确记录无法可靠识别的托管/子进程风险；探测异常或无权限时禁止删除。**进程退出探测不是绝对文件锁**，无法取得稳定状态则 fail closed。
7. 同时只允许一个清理计划执行；点击删除时的确认对话框明确显示备份路径、选择数和预计容量。

## 8. 备份设计与状态机

会话删除和附件删除必须强制先备份。ZIP 保存“原始文件”而不是只保存解析出的 Markdown，记录：

```json
{
  "schemaVersion": 1,
  "createdUtc": "2026-10-16T00:00:00Z",
  "sourceRoots": ["sessions", "attachments"],
  "entries": [
    {"root":"sessions", "relativePath":"project/session-id/session.v4.jsonl.zstd", "length":123,
     "sha256":"lowercase-hex-64-chars", "sessionId":"..."}
  ],
  "warnings": ["attachment references unavailable: attachment bytes not included"]
}
```

- `sourceRoots` 仅包含类别名，**不把用户完整绝对路径写到清单**；zip entry 必须是 POSIX 风格相对路径，禁止 `..`、绝对路径和重名。
- ZIP 内路径：`sessions/<relative>`、`attachments/<relative>`、`manifest.json`；允许未来 schemaVersion 追加兼容字段。缓存不是会话备份必要组成，可单独选择备份缓存，不宣称完整可回导。
- 校验：生成临时 ZIP → 遍历 ZIP entries 对比 manifest 的数量/长度/SHA-256 → 校验 manifest 自身结构 → 用户指定最终路径下无覆盖发布。失败绝不开始删除。
- 备份完成与用户确认之间源文件若变化，**停止删除并要求重备份**；不能用陈旧备份覆盖变动的源文件。
- 若附件引用不明，必须在导出摘要写入“附件未完整保全”，并阻断“保证附件完整”的承诺；会话原始文件备份仍可执行。

```text
Idle → Scanning → Ready → Planning → BackupPending → BackupValidated
                                         └→ Error/Cancelled
BackupValidated → Recheck → AwaitConfirm → Deleting → Completed/PartialFailed
                       └→ Aborted/Changed
```

- 成功后显示真实释放字节数及 skipped/failed 明细，不声称所有删除原子完成；支持重新扫描核对残留。
- 常规缓存/日志清理没有强制 ZIP，但也必须展示计划并确认；提供手动导出备份选项。

## 9. 配置模型

- 工具配置独立存放 `%LOCALAPPDATA%\DeepSeekHarnessCleaner\settings.json`（非 Harness 目录）。初次启动不存在时使用默认值；损坏则报告并使用默认（不覆写损坏文件）。
- 仅保存 `schemaVersion`、最近选择的受控根、备份路径、日期筛选偏好、窗口布局；不保存会话正文、身份凭据或敏感模型配置；原始根路径显示须明确本地使用。
- 配置更新使用临时文件与替换，并可选择不记住路径。

## 10. 错误与取消

| 错误 | 行为 |
|---|---|
| root invalid / unauthorized | 拒绝扫描/删除，对用户提示如何更正 |
| invalid Zstd / JSON | 记录文件级错误，其他会话继续，只允许原始备份 |
| unsupported version | 只读列出/导出，不删除未验证日志 |
| unknown running state | 仅保留只读/备份操作，删除不可用 |
| backup verification failed | 不进入清理，保留源文件 |
| file changed since backup | 停止或跳过，需重建计划/重新备份 |
| file locked / access denied | 记录失败，不提权、不重试覆盖 |
| user cancels during delete | 在下一文件边界停止；展示已完成项，不回滚已删文件 |

## 11. 尚须实测的事实

- 在 Harness **0.2.0.0 本机**样本中验证旧版与 v4 header、时间字段、角色事件映射、同一会话多代路径和缓存命名规则。
- 验证 ZstdSharp.Port 对连接帧 / 校验错误 / 截断尾帧的行为，必要时实现受控 frame iterator；不得仅凭库说明假定全部符合。
- Windows 10 具体 SKU/Build、WinForms 自包含运行、长路径/reparse point 处理和中文 DPI。

依赖研究来源：<https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/session/session-persistence-jsonl/README.md>、<https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/session/session-projection-cache/README.md>、<https://github.com/deepseek-ai/deepseek-harness/blob/master/docs/persistence-changes/2026-09-16-session-format-v4.md>、<https://github.com/deepseek-ai/deepseek-harness/blob/master/.agents/notes/implemented/architecture/2026-08-31-released-session-format-migrations.md>。

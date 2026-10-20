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

### 2026-10-20 列表及窗口补充契约（修复清单 T-31）

- 会话列表增加独立“归档”列，展示已归档/未归档/未知；只从经上游 schema 核实且与会话 ID 对应的本地元数据读取，缺失、损坏、冲突或不支持时显示未知，不能将 ZIP、压缩代际或目录名当成归档标志。
- “解析状态”列明确展示正常、部分解析、格式未支持、损坏、无法读取及身份歧义。该状态不代表运行中/空闲，也不代表具备删除资格。
- 附件列表优先展示已验证引用元数据中的原始名称、类型及关联会话标题/ID，同时保留存储路径、附件 ID、大小和引用状态；没有证据时显示名称未知、类型未知，普通命名文件可显示存储文件名并注明来源。
- 删除窗体和附件窗体在首次显示时按直接父窗体的实际屏幕边界居中，适用于现有非模态打开方式；位置约束在可用工作区内。
- 归档来源：`storages/workspace.json` 的 `unit.name=workspace`、`unit.version=2`、`global.initialized=true` 及 `global.archivedSessionIds`。缺失字段、重复字段、非法 ID、未初始化和归档/置顶冲突返回未知；只读该文件，不修改 registry。
- 上游依据固定于 `687f282e8625c6b8d11ff58211a90524be2e4317`：[workspace schema](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/workspace/workspace/src/spec.ts)、[attachment types](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/attachment/attachment/src/types.ts)、[file object layout](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/attachment/attachment-local/src/file-store.ts)。附件元数据只沿已验证消息角色和 image/file 内容块读取；图片类型明确标为会话标注，不能冒称已检查文件魔数。摘要匹配的 v1/objects、v1/file-objects 与 v1/files 文件可关联同一附件 ID；仍逐文件显式选择，不自动扩大删除范围。

## 2. 会话发现与物理格式

- 只进入由用户认可的 `SessionRoot` 下的普通目录，不跟随 NTFS junction / symbolic link / reparse point；禁止层级越界。
- 会话目录名称不能替代逻辑身份；读取 header `id` 后确认目录与头标识的映射；无有效 header 的日志列入“异常文件”，允许原始备份但不允许自动清理。
- 文件名白名单：`session.jsonl`、`session.jsonl.zstd`、`session.vN.jsonl`、`session.vN.jsonl.zstd`（N 为正整数）；大写/临时/备份/不匹配项不自动删除。
- 同一会话保留多个 format generation 是正常的；扫描归组，同一目录不应把多个代际计为多个会话。用于预览的代际为 numerically highest canonical generation；**最高代际无法读取时显示异常，不回退到旧代际假装当前内容**。
- 同一个会话目录同时存在相同代际 `.jsonl` 与 `.jsonl.zstd` 或 header 身份冲突，标记 Ambiguous，阻止清理。
- 2026-10-20 本机修复：原生头部为 `type=session/id/version/createdAt`（Unix 毫秒），仅确证 v0/v4。文件名代际须与原生 version 一致；删除准备重新核对完整代际集合、有效 JSONL、长度/mtime/SHA-256。未知正文事件可以随原始文件整体备份，但未知版本、损坏或超限日志不得进入精确删除。仅 Harness 退出后处理完整文件，不修改日志或索引。
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
- 正常请求配置/待办/流式状态不作为损坏事件；工具调用只展示名称，工具结果展示已验证的文本块，内部参数/推理不展开。未知事件或内容块提示“未支持”，与无效 JSON/缺少必要字段分别统计。
- 文本控件使用纯文本赋值，禁止 Markdown/HTML 脚本执行或自动加载远程内容；不渲染外部图片 URL。对大型会话可取消/分页。

## 5. 附件引用策略

- 建立所有已成功解析会话中的显式附件 ID/路径引用证据；引用关系必须匹配本机附件布局，并经白名单验证。
- 如有旧格式无法解析或缺少全局会话，**绝不可声明任何附件为全局孤儿**；显示 `Unknown`。
- 会话删除不自动删除附件，无论是否被引用；用户可在附件页单独选择并确认精确删除，原始备份可选，且展示“引用不可判定”警告。
- 对目录级附件、外部软链和未知类型禁止自动删除。附件删除不能扩大到其他 Harness 配置/缓存。
- 附件页仅从受控 attachments 根枚举普通文件，不跟随 reparse；原生 `v1/objects/<prefix>/<sha256>` 映射须与内容摘要一致，按已验证 image/file 内容块的 attachmentId 确认正向引用。当前不能证明全局孤儿，未确认引用统一 Unknown；文件/别名等其他布局仍可查看并由用户逐文件显式选择，绝不自动合并目录或联动删除。

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

会话删除和附件删除均允许不备份直接确认删除；备份为独立可选操作（2026-10-20 用户确认的新契约）。选择备份时须完成校验；失败、取消或变更不能被显示为已备份，用户可明确取消备份选择后直接确认删除。两种路径均保留进程、路径、根指纹及文件长度/mtime/SHA-256 复验。ZIP 保存“原始文件”而不是只保存解析出的 Markdown，记录：

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
- 计划或备份完成与用户确认之间源文件若变化，停止或跳过删除，要求重新扫描并生成计划；选择备份时还需重备份。
- 若附件引用不明，必须在导出摘要写入“附件未完整保全”，并阻断“保证附件完整”的承诺；会话原始文件备份仍可执行。

```text
Idle → Scanning → Ready → Planning → AwaitConfirm（不备份）
                             └→ BackupPending → BackupValidated → AwaitConfirm
                                      └→ Error/Cancelled（可明确改选不备份）
AwaitConfirm → Recheck → Deleting → Completed/PartialFailed
                  └→ Aborted/Changed
```

- 成功后显示真实释放字节数及 skipped/failed 明细，不声称所有删除原子完成；支持重新扫描核对残留。
- 常规缓存/日志清理没有强制 ZIP，但也必须展示计划并确认；提供手动导出备份选项。

## 9. 配置模型

- 通用配置独立存放 `%LOCALAPPDATA%\DeepSeekHarnessCleaner\settings.json`（非 Harness 目录）。初次启动不存在时使用默认值；损坏则报告并使用默认（不覆写损坏文件）。
- 2026-10-20 用户要求的窗口自动记忆单独存放于 `AppContext.BaseDirectory/DeepSeekHarnessCleaner.window-state.json`，即程序文件所在目录，不使用当前工作目录。此规则替代通用配置中窗口尺寸字段作为主窗启动/关闭自动记忆来源的旧设计；旧字段兼容读取不等于已接入自动记忆，不自动迁移或改写原 settings.json。
- 主窗记录正常状态下的屏幕位置和外框大小；最大化/最小化关闭时保存 RestoreBounds，恢复最大化但不恢复最小化。关闭被取消时不写；实际关闭时保存一次。位置需适配当前屏幕工作区、最小尺寸及 DPI，显示器移除后仍可见。文件写入失败须提示，本次关闭仍可继续；不自动提权或悄悄写入其他目录。
- 仅保存 `schemaVersion`、最近选择的受控根、备份路径、日期筛选偏好及单独窗口记录；不保存会话正文、身份凭据或敏感模型配置；原始根路径显示须明确本地使用。
- 配置更新使用临时文件与替换，并可选择不记住路径。

### 2026-10-20 后续交互修正（已实施，修复清单 T-33～T-38）

- 结果报告复用 OwnedFormPosition，在非模态 Shown 后按直接父窗体居中；使用随窗体调整的布局容器或 Anchor/Dock。摘要、备份信息与底部日志路径允许长文本，关闭按钮不与文字重叠，设定可操作的最小尺寸。
- 日志说明与文件路径分开；路径用只读可选择控件呈现，保留 Ctrl+C，日志创建失败时不显示虚构路径。结果表采用可溢出的列宽或足够的 MinimumWidth，按需出现横纵滚动条；ScrollBars.Both 不是 Fill 模式下横向可滚动的充分条件。
- 附件右键命中具体行后才显示“打开附件”，不改变删除勾选；空白处不可误用旧行。启动前再次验证普通文件、受控根与 reparse；仅响应用户明确点击，以文件路径调用系统默认应用，不使用命令行字符串或 URL。
- 哈希命名且无扩展名的附件不能保证原路径有默认关联；有可信媒体类型时可复制为带确证扩展名的工具临时打开副本，再交给默认应用。没有类型依据时不猜后缀，提示无法确定应用。原附件不得改名/修改；打开副本不进入清理计划、不覆盖旧副本，成功启动后的副本不得立即删除；失败仅清理本次新建且未使用的副本。
- “选择”表头支持批量切换当前筛选后的全部显示行（包含需滚动才能看到的行）；全勾选时取消，否则全部勾选。结束当前复选框编辑后统一更新 HashSet 和行值，避免逐行预览/重复统计。不改变其他列排序或当前行回退规则。
- 保留既有跨筛选勾选规则；表头操作只影响显示集合，隐藏勾选不被偷偷清除或新增。状态栏同时显示当前显示已勾选/显示总数、总已勾选及隐藏已勾选数量，删除确认继续列出实际完整选择集。改变隐藏勾选保留策略需另行确定。
- 全部现有表格（主会话、附件、删除明细、执行结果）列标题均加粗并双向居中，正文/复选框/排序保持原行为。DataGridView 单独设置表头样式；ListView 首列表头需经验证的自绘或等效方案，不能用全控件 Font 把正文一并加粗。
- 实施状态与测试条件以修复清单为准；调研依据见 [交互修正调研](2026-10-20-交互修正调研.md)。

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
- Windows 10 具体 SKU/Build、安装 .NET 10 Windows Desktop Runtime x64 后的 WinForms 单文件运行、长路径/reparse point 处理和中文 DPI。

依赖研究来源：<https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/session/session-persistence-jsonl/README.md>、<https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/session/session-projection-cache/README.md>、<https://github.com/deepseek-ai/deepseek-harness/blob/master/docs/persistence-changes/2026-09-16-session-format-v4.md>、<https://github.com/deepseek-ai/deepseek-harness/blob/master/.agents/notes/implemented/architecture/2026-08-31-released-session-format-migrations.md>。


## 2026-10-20 UI 修复迭代补充（待运行验收）

- 标题来源：扫描已知会话代际中官方持久化的 `session/title` 事件，后发生的有效事件覆盖先前标题；没有确证标题时显示“未命名”，不通过消息正文猜测。为限制单次扫描资源，读取器检查至多 32 MiB 的文件/解码内容，不保证超长记录均能提供标题。
- 筛选：关键字搜索标题、项目和会话 ID；项目/标题/ID 独立包含匹配，按 AND 组合；项目候选来自扫描结果，WinForms 表格布局对齐，提供清空筛选。
- 选择：当前 `SelectedRows` 与 `checked` 列独立。存在勾选时删除使用勾选集合，否则使用选中的行；批量勾选不发起预览。筛选重绑维持同一次扫描的勾选身份，重新扫描失效对象会移除。
- 预览：选择变化延迟 140ms 后发起读取，旧请求取消；单次预览最多读取 8 MiB、每消息展示至多 16,000 字符、总显示约 240,000 字符；超过限制明确标记。压缩解码器在同步 `Decode` 阶段不具备真正可中断的 token 参数，因此不能声称所有 CPU 工作能即时取消。
- 删除确认：文件清单包含其逻辑会话标题；标题/路径/大小/类别均可随横向、纵向滚动查看；长进程警告使用只读多行可滚动文本框。
- 备份：手动选择 ZIP 目录，唯一 ZIP 名称、备份状态多行展示与资源管理器跳转；原始文件验证、备份复验、删除前文件属性与进程状态门禁保留。
- 运行中删除：根据当前上游 `SessionPersistence`、Workspace 注册表接口和官方讨论，不存在已验证可直接使用的会话级持久安全删除 API。不能仅依赖文件锁、mtime、前台选中状态证明会话可删；当前版本仍在进程 Active/Unknown 时拒绝写入，不能绕过运行保护。
- 变更清单及待验收内容见 `docs/2026-10-20-修复任务清单.md`。本节描述源码设计状态，不表示 Windows 编译/测试通过。

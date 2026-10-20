# Workspace 存储兼容性契约（T01）

> 核查日期：2026-10-20。上游锁定：`deepseek-ai/deepseek-harness@687f282e8625c6b8d11ff58211a90524be2e4317`（2026-10-17）。本文件是**源码契约核查**，不是 Windows 实机样本验证，更不是启用写入的授权。

## 1. 证据（固定 commit 的可复查路径）

- [Workspace schema](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/workspace/workspace/src/spec.ts)：`workspaceDomainSpec`、`workspaceDomainState`、`workspaceRecord`、`workspacePendingMutation`。
- [Workspace registry](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/workspace/workspace/src/index.ts)：`recoverPendingMutation`、`bootstrap`、`setState`、`archiveSession`、`pinSession`、`create/delete workspace` 路径。
- [Workspace entity](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/workspace/workspace/src/entity.ts)：`attachSession`、`detachSession`、`mutate` 对 `sessionIds` 的处理。
- [JSON medium format](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/storage/storage-json/src/format.ts)：`serialize`、`parse`、`serializeRecord`、`parseRecord`。
- [Single-file JSON backend](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/storage/storage-json/src/single-unit.ts)：`openSingleUnit`、`setGlobal`、`putRecord`、`publish`。
- [Storage backend selection](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/storage/storage-json/src/index.ts)：后端 root 显式配置，支持 single/per-record 形式；不能仅以文件名推断存储介质。
- [Atomic replace](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/storage/storage-json/src/atomic.ts)：写临时文件、sync、rename；不是跨多记录事务。
- [当前 Cleaner 读者](../src/DeepSeekHarnessCleaner.Infrastructure/WorkspaceArchiveReader.cs)（以仓库根路径 `src/DeepSeekHarnessCleaner.Infrastructure/WorkspaceArchiveReader.cs` 为准）：只读 `storages/workspace.json` 的归档集合，失败返回 null；现有方法不得被视为元数据写入器。

## 2. 已确认的 v2 schema 字段与语义

| 逻辑路径（single JSON） | 类型 / 条件 | 上游语义 | 安全操作边界 |
| --- | --- | --- | --- |
| `unit.name`, `unit.version` | `workspace`, `2` | KV domain 身份及版本；single medium 的 parse 严格拒绝版本不匹配 | 只读校验，禁止修改 |
| `global.initialized` | boolean | false 表示尚未完成历史 session header bootstrap；不等于“空工作区” | false 禁止外部写入 |
| `global.workspaceIds` | ordered string[] | 工作区 authoritative display order | 不在会话清理时改变 |
| `global.defaultWorkspaceId` | optional string | 首次使用工作区身份，删除注册后仍可保留 | 不在会话清理时改变 |
| `global.archivedSessionIds` | string[]，默认 [] | 全局归档标记；归档时仍保留工作区 sessionIds 位置 | 仅未来通过门禁的同步方案可按选中 ID 去引用 |
| `global.pinnedSessionIds` | string[]，默认 [] | 全局置顶，最近置顶在前；不能与归档集同时包含同一 ID | 同上 |
| `global.pendingMutation` | optional {operation: create/delete, workspaceId} | 两次写入之间的可恢复标记；启动时有恢复逻辑 | 存在时禁止外部写入 |
| `tables.workspaces[workspaceId].path` | string | 创建时的 realpath 规范路径 | 保持不变 |
| `tables.workspaces[workspaceId].title` | string | 用户工作区标题 | 保持不变 |
| `tables.workspaces[workspaceId].sessionIds` | ordered string[] | 工作区拥有的会话顺序；attach 校验 header cwd；detach 移除 ID | 仅未来通过门禁的同步方案可按选中 ID 去引用 |
| `tables.workspaces[workspaceId].createdAt/updatedAt` | string | ISO8601 时间戳；实体 mutate 会更新 updatedAt | 同步写入契约尚需确定时间戳策略 |

`workspaceDomainState` 对归档/置顶数组使用 Zod default([])：**字段缺失不等于损坏**，但存储格式不同仍不得写。Zod 对普通 object 的解析行为也不等于保留任意未知 JSON 字段；外部重写不能据此认定为无损。

## 3. 存储介质判别与未知情况

`storage-json` 存在 whole-unit `workspace.json` 和 per-record `workspace/` 布局；存储子系统也支持 SQLite。只有确认当前真实部署的后端、root、版本和布局之后才可将路径纳入读写范围。未找到 JSON 文件不能推断不存在归档、置顶或成员关系。

`single` 布局序列化形式为 `{unit:{name,version},global,tables:{workspaces:{[id]:record}}}`；`global:null` 表示尚未持久化 singleton，不能宣称引用为空。对于 `per-record` 与 SQLite，本项目尚未验证端到端实际样本及其数据位置，标记 Unsupported/Unknown，不能写入。

**所有可能影响兼容的未知项**：DSH Desktop 的实际发行构建是否等同此上游 commit；真实用户数据配置 root；per-record/SQLite 运行情况；未来 schema 改动；同一会话在多个工作区出现时的异常处理；文件外部改写与运行中的内存缓存冲突；session 删除与 workspace 持久化之间缺少跨资源原子事务。

## 4. 版本支持矩阵与写入允许清单

| 识别对象 | 源码契约 | 真实脱敏样本验证 | 读 | 外部写 |
| --- | --- | --- | --- | --- |
| 上游 commit `687f282...` / workspace v2 / JSON single | 已验证源码 | 未验证 | 严格校验后允许只读审计 | **禁止** |
| workspace v2 / JSON per-record | 源码显示支持 | 未验证 | 待实现识别；未知 | **禁止** |
| workspace v2 / SQLite | 源码显示可配置后端 | 未验证 | 待实现识别；未知 | **禁止** |
| 其他版本、缺失/无效状态、事务未完 | 未验证 | 未验证 | Unknown/Unsupported | **禁止** |

**外部元数据写入 allowlist = 空集**。T12–T16 不可因仅匹配 `unit.version==2` 就启用写入。后续必须获取明确发行版本与后端映射、真实脱敏样本、并发/崩溃恢复实测、强制备份与写前/写后身份校验，才可逐版本审议加入允许清单。不存在“默认兼容全部 v2”的许可。

## 5. 本任务代码影响及验收

T01 仅新增本文档及其证据记录；不修改现有 .NET 代码、UI 或删除行为。已与当前 `WorkspaceArchiveReader.Read(string dataHome)` 的严格 v2 single-file 读取逻辑核对：当前 Reader 还要求归档字段必须存在，且拒绝 pinned 与 archived 交集；是否放宽兼容由后续任务及测试决定，不在 T01 修改。

T01 只以“固定上游 commit、字段及事务语义有源码证据、保守空写入 allowlist”作为**文档契约验收**。编译、回归、桌面真实样本、Windows 实测均未执行，不能被计为通过。

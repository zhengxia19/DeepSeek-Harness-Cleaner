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
| Desktop0.2.1-rc.2 / 已核验安装资产 / 有效JSON single配置 | 字段及存储语义与打包代码核对，采用SHA256指纹，发行Git提交仍未知 | 观察脱敏样本及公开协调器合成根流程通过 | 严格校验后允许 | **有条件支持**：当前环境根、配置绑定及全部安全门禁通过；未知配置拒绝 |
| 本机观察样本（桌面包0.2.1-rc.2，数据生产者提交未知） | 字段结构匹配v2 single，发行语义/root映射未核实 | 1份严格脱敏样本，结构/71处引用索引通过 | 仅观察快照只读 | **禁止** |
| workspace v2 / JSON per-record | 源码显示支持 | 未验证 | T07 识别候选目录并返回 Unsupported；不读取内容 | **禁止** |
| workspace v2 / SQLite | 源码显示可配置后端 | 未验证 | 待实现识别；未知 | **禁止** |
| 其他版本、缺失/无效状态、事务未完 | 未验证 | 未验证 | Unknown/Unsupported | **禁止** |

**允许项：`dsh-desktop@0.2.1-rc.2/workspace/v2/json-single`**。不可因仅匹配 `unit.version==2` 就启用写入。发行资产、有效配置、当前环境根、强制备份、进程及文件身份必须逐次复验；不存在“默认兼容全部v2”的许可。细节见文末A04补充。

## 5. 本任务代码影响及验收

T01 仅新增本文档及其证据记录；不修改现有 .NET 代码、UI 或删除行为。已与当前 `WorkspaceArchiveReader.Read(string dataHome)` 的严格 v2 single-file 读取逻辑核对：当前 Reader 还要求归档字段必须存在，且拒绝 pinned 与 archived 交集；是否放宽兼容由后续任务及测试决定，不在 T01 修改。

T01 只以“固定上游 commit、字段及事务语义有源码证据、保守空写入 allowlist”作为**文档契约验收**。编译、回归、桌面真实样本、Windows 实测均未执行，不能被计为通过。

## 6. 后续 T07 实现状态（不改写 T01 历史验收）

已新增只读 `WorkspaceReferenceReader`，对 v2 single JSON 快照严格建立归档、置顶和成员索引，
返回 Known/Unknown/Unsupported、快照哈希和 JSON Pointer。重复字段/ID、未初始化、任何事务标记、
注册表与成员歧义均不判无引用；不存在单 JSON 文件不能推断没有其他存储后端。
具体规则与兼容边界见 interfaces.md，执行证据见 artifacts/evidence/change-plan-2026-10-20/T07.txt。
真实发行样本仍未验证，写入 allowlist 仍为空。

## 7. T12 契约决策

已按同一固定上游 commit 复核 schema、unarchive/unpin、detach/mutate 及事务恢复代码；
候选字段和状态机详见 [workspace-sync-design.md](workspace-sync-design.md)。
仅实际改变成员列表的记录拟在 Apply 时更新 updatedAt；不复制官方顺带裁剪其他成员的行为。
T12 只产生内存提议，不构造/写入新 JSON；原始字段和顺序保持不变。实际写入不变量测试留给 T14，
真实发行版写入允许清单仍为空，不因新增 DTO 或上述决策自动开放版本。

## 8. T16 开发状态补充

T12–T16 的提议、独立备份、句柄受控写入、恢复/重试及默认关闭交互已在合成数据上验证。
这不改变第4节的真实发行支持矩阵：真实发行/后端/脱敏样本映射尚未完成，生产写入允许清单仍为空。
界面会展示具体引用数和禁用原因，不提供强制绕过版本门禁的选项。真正的进程强制中断证据须另行补齐。

## 9. 最终审计补充

已增加观察脱敏样本及独立来源/替换规则说明，原数据只读且原JSON未落盘；不把采集时刻或替换日期冒充事件时间。已通过8阶段真实子进程强制终止模拟120项，区别于硬件断电。安装包元数据观察见 artifacts/evidence/change-plan-2026-10-20/installed-release-observation.json。
公开生产允许清单仍为空；下一步须核验打包代码语义、实际后端和根对应，不能从这些测试直接开放全部v2。完整状态见change-plan-2026-10-20/05-整体验收审计.md。

## A04 条件支持补充（2026-10-20）

当前支持的是安装资产指纹及有效配置共同确认的Desktop0.2.1-rc.2 JSON single布局。发行Git提交与观察文件最初生产者提交仍未知，不伪造两者的映射。
原始数据观察仅证明该快照结构；是否允许同步仍由当次发行/配置/根/格式/事务/进程/备份/身份/删除缺失复验决定。
版本号本身、观察样本本身、单个字段或调用者提供的DTO均不能授权写入。修改过的发行资产、未审阅兼容范围、SQLite/per-record、动态未知核心路由、include或未知核心作用域拒绝。
19项打包资产（含启动器/Workspace/存储模块/默认配置及关键包清单）以SHA256核验；只读配置组合在本机实际profile通过，公开服务在合成根通过成功及阻断路径。
其余发行、非当前环境根和配置形式仍列未支持/待验证；多DPI、干净VM、正式发布及硬件断电不由本轮推断通过。
上述条件取代此前阶段记录中的“允许清单为空”；证据及精确限制见 `artifacts/evidence/change-plan-2026-10-20/A04-production-gate.txt`。
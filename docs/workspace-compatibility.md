# Workspace 存储兼容性契约（T01 调查记录）

基线：Cleaner `main` commit `57b5806266cf2618deeeaee2690f824cbf34a1ab`（2026-10-20）。本文件仅记录已取得的源码证据，**不授权对任何生产 workspace.json 写入**。

## 源码来源与证据等级

- Cleaner 当前实现：[WorkspaceArchiveReader.cs](https://github.com/zhengxia19/DeepSeek-Harness-Cleaner/blob/57b5806266cf2618deeeaee2690f824cbf34a1ab/src/DeepSeekHarnessCleaner.Infrastructure/WorkspaceArchiveReader.cs)：仅解析 `unit.name=workspace`、`unit.version=2`、`global.initialized=true` 和归档 ID。此为本项目解析假设，不是上游格式证据。
- 公共镜像/衍生仓库 [112GT/deepseek-harness-vscode: spec.ts](https://github.com/112GT/deepseek-harness-vscode/blob/9d3b29af499445c035ecf3350c7514e25b8deda0/harness/packages/workspace/workspace/src/spec.ts)（内容已核对）：`defineDomain({name:'workspace',version:2})`，`global.initialized`、`workspaceIds`、`archivedSessionIds`、可选 `pendingMutation`；`workspaces` table 含 `path/title/sessionIds/createdAt/updatedAt`。该仓库不代表官方发布版本或当前用户安装版本。
- [112GT/deepseek-harness-vscode: index.ts](https://github.com/112GT/deepseek-harness-vscode/blob/9d3b29af499445c035ecf3350c7514e25b8deda0/harness/packages/workspace/workspace/src/index.ts)（内容已核对）：启动执行 `recoverPendingMutation()`、`validateStoredState()`；工作区 `sessionIds` 是所有者记账，归档保留原位置；`pendingMutation` 记录 create/delete 的两步中断恢复。

## 字段与保守判定

| 语义 | 镜像源码核实结果 | 清理器策略 |
| --- | --- | --- |
| Domain 身份与版本 | `name=workspace`，`version=2` | v2 仅说明类型，不自动授权写入 |
| `global.initialized` | boolean；false 表示历史引导未完成 | false / 缺失 / 非布尔：禁止写入 |
| `global.workspaceIds` | 有序工作区 ID 数组，需与 table 对应 | 缺失、重复、与 table 不一致：禁止写入 |
| `global.archivedSessionIds` | 归档全局列表；归档不解除 workspace 成员资格 | 仅在审计时读取；禁止盲删 |
| `global.pinnedSessionIds` | **上述镜像 v2 spec 未定义** | 未验证字段，不得按该版本强行写入 |
| `global.pendingMutation` | 可选 create/delete + workspaceId；启动恢复 | 出现时禁止离线写入，不自行恢复 |
| `tables.workspaces[*].sessionIds` | 记录内有序成员数组；一个 session 不能归属两个 workspace | 先核实实际磁盘序列化层路径及一致性，再考虑修改 |
| JSON `unit/global/tables` 外部包装 | Cleaner 当前只读器使用 `unit/global` | 尚缺官方 storage-domain 序列化源码与对应 release commit 的逐字段验证 |

注意：`z.object` 默认的额外字段处理不代表离线 JSON 写入可删除未知字段；未知字段、未知版本或未完成事务一律禁写。

## 版本支持矩阵

| 来源/版本 | 字段语义 | 实际落盘结构 | 是否获准写入 |
| --- | --- | --- | --- |
| 上述社区镜像的 workspace domain v2 | 部分核实 | 未证实与官方发行版相同 | **否** |
| Cleaner 所见 `unit.version=2` | 只读归档解析部分核实 | 没有官方对应 commit 证据 | **否** |
| 其他版本 / 未知版本 | 未验证 | 未验证 | **否** |

## 待完成的官方证据门禁

1. 定位 **DeepSeek Harness 官方上游仓库、具体发行 tag/commit** 的 workspace `spec.ts`、`entity.ts`、`index.ts` 和 storage-domain 序列化/事务实现；保留不可变 commit 链接。
2. 核对该官方 commit 的 `workspace.json` 实际字节结构（unit/global/table、各字段路径、文件写入事务与崩溃恢复）。
3. 证明当前安装的 DSH 版本对应上述 commit；无确证则保持 **可写版本清单为空**。
4. 复核置顶字段是否属于同一持久化域；不能从其他分支拼接字段写入契约。

当前结论：**T01 证据尚不满足验收；后续 T02–T16 未启动。** 没有修改任何运行代码，也没有对用户数据执行读写。

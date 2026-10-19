# ADR-0002：先原始备份校验、后精确删除；不改写源日志

- 状态：已接受（设计约束）；第 2 条“必须备份”自 v0.2.0 起由 ADR-0003 修订为“备份可选、确认与复验必需”，其余条款仍有效
- 日期：2026-10-16

## 背景

Harness 日志可能有 V0/V4 等多个代际、append-only 事件、压缩串帧；缓存/附件/会话不一定一一对应。直接修改日志行或清空整个 `.dsh` 易破坏状态。

## 决策

1. 只读扫描/预览，清理前创建具备源文件 SHA-256 的计划，进程与路径复核通过才能执行。
2. **会话和附件必须备份原始文件 ZIP 并完成校验**，然后用户再次确认；缓存/日志可单独清理并显式确认。
3. 只删除白名单中的普通文件；不处理 Harness 设置、凭据、运行时及浏览器状态；绝不在日志内部删除某一段时间的事件。
4. 不自动连带删除附件；无法验证关系则保留，并显示“未知”。
5. 保留所有会话物理 generation 到 ZIP；当前代际读取失败不使用较旧 generation 冒充当前。
6. 不实现自动导入/自动恢复；备份是安全留存和手工恢复基础，官方导出 ZIP 亦非已支持的导入协议。

## 替代方案

- 清空 `.dsh`：实现简单但影响配置、缓存和会话；拒绝。
- 改写 JSONL 删除部分消息：破坏事件依赖关系；拒绝。
- 全量回收站搬移：回收机制受盘符和 Windows Shell 语义影响，释放容量与可验证性较差；不作为 v1 核心。
- 自动删除“孤儿附件”：无法证明索引完整就会误判；拒绝。

## 影响

- 操作多一步备份且要求足够磁盘空间；用户可明确看到容量、文件与风险。
- 部分失败不能事务回滚，必须逐项报告并允许重扫。
- 用户侧导出仅表示本地原始文件保存，不保证 Harness 继续会话的可用性。

参考：<https://github.com/deepseek-ai/deepseek-harness/blob/master/.agents/notes/implemented/architecture/2026-08-31-released-session-format-migrations.md>、<https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/session-query/session-log-export/README.md>。

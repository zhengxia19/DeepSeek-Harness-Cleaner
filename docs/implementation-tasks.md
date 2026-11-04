# 任务与验收索引

当前 0.2.4 交付已由用户于 2026-10-20 确认实机验收完成，详见 [当前验收记录](acceptance.md)。用户已授权按 [2026-10-20 新计划](change-plan-2026-10-20/00-总调度与验收.md) 实施及验收；编号 T01–T16 与历史编号分开。T01 文档契约已完成；T02 合成夹具已通过实际验收，证据见 `artifacts/evidence/change-plan-2026-10-20/T02.txt`；T03 保守回归已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T03.txt；T04 候选发现模型已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T04.txt；T05 安全校验已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T05.txt；T06 根选择与状态失效已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T06.txt；T07 引用索引已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T07.txt；T08 会话关联清单已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T08.txt；T09 删除前审计已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T09.txt；T10 删除后复查已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T10.txt；T11 会话报告已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T11.txt；T12 契约与内存提议已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T12.txt；T13 强制原始备份已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T13.txt；T14 安全写入器已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T14.txt；T15 部分失败、恢复与重试已验收，证据见 artifacts/evidence/change-plan-2026-10-20/T15.txt；T16 可选同步/恢复交互已完成实施及合成验收，证据见 artifacts/evidence/change-plan-2026-10-20/T16.txt；[A01–A07 逐项审计](change-plan-2026-10-20/05-整体验收审计.md)已记录：8阶段强制终止模拟与观察脱敏样本已补齐；A04发行/配置门禁已通过独立验收，A01–A07技术复核已通过；用户于2026-10-21确认同意新增依赖，本次计划实施及验收关闭；未提交或发布新版。

历史唯一来源原样归档：

- [原始 T-01～T-34](../archive/2026-10-20/tree/docs/implementation-tasks.md)。
- [2026-10-20 修复清单](../archive/2026-10-20/tree/docs/2026-10-20-修复任务清单.md)：其编号仅在该文件内有效；最终 T-33～T-38 与旧版计划一并保留。
- [归档索引与校验清单](../archive/2026-10-20/README.md)。

历史任务的原始勾选与证据不重写；旧计划中未勾选条目不自动成为新待办。后续开发先根据用户新需求形成独立任务清单，明确范围、依赖、验收及证据，再按单任务规则实施。

本次维护任务 DOC-20261020：更新用户验收状态、归档历史变更/过程文件、校验哈希与文档引用，提交并推送。证据见 [维护记录](../artifacts/evidence/DOC-20261020.txt)。

## 新任务 ROOT-20261104：会话根标记契约调整（用户 2026-11-04 授权）

背景：目标机 `C:\Users\zx\.dsh` 含 `sessions`/`storages` 会话数据但从未使用附件，`attachments` 为 DSH 懒创建目录（开发机实测其创建时间晚于 sessions/storages 两个月），被现行为"三目录必备"整体判为 `ROOT_MARKERS_MISSING`，无法清理。

范围：`RootLocator` 会话根必备标记改为 `sessions`+`storages`；`attachments` 为可选标记——存在时仍经 `PathGuard` 复验（reparse/访问拒绝仍拒绝），缺失不影响有效性；同步更新选择对话框目录说明与 `interfaces.md` §2 标记契约。不改动桌面根标记、扫描/删除路径白名单与 `Resolve` 派生子根行为（缺失 `attachments` 由 `AttachmentInventoryService` 既有 `ATTACHMENT_ROOT_MISSING` 问题项降级处理）。

验收：[x] 编译通过；[x] 单元测试含"缺 attachments 仍有效且不创建该目录"与"attachments 为 reparse 仍拒绝"；[x] 原必备标记缺失（仅 sessions）仍 `ROOT_MARKERS_MISSING`；[x] 相关回归测试通过（单元测试 683 全过；WindowsTests `--roots-only` 全过；完整 UI 回归存在既有环境失败"报告窗口滚动条"，经未修改 HEAD 基线复跑确认为既有问题，与本任务无关）；[x] 证据落盘 `artifacts/evidence/ROOT-20261104.txt`。
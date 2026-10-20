# 2026-10-20 历史归档

归档基线：`299966b84ad35b17117c48984f0b9922f2430bcd`。用户于 2026-10-20 确认完成实机验收，并要求更新文档、归档历史及推送。

[清单与 SHA-256](manifest.csv) 共 70 项：53 项历史过程文档/证据迁移，17 项现行文档及检查点原文快照。`tree/` 保留原目录结构，全部文件按原始字节留存。Action=Move 表示迁移，Snapshot 表示归档前快照。历史状态、未勾选项、当时限制与矛盾均保留，不作为当前待办重新执行。

- [原始 T-01～T-34 任务与验收](tree/docs/implementation-tasks.md)
- [2026-10-20 修复任务与验收](tree/docs/2026-10-20-修复任务清单.md)
- [交互调研](tree/docs/2026-10-20-交互修正调研.md)
- [设计复审](tree/docs/review-report.md)
- [原始证据](tree/artifacts/evidence/)
- [归档前发布记录](tree/docs/release.md)、[变更记录](tree/CHANGELOG.md)

当前交付状态见 [验收记录](../../docs/acceptance.md)，当前任务入口见 [任务索引](../../docs/implementation-tasks.md)。历史文档中的相对路径按原目录解释；未纳入归档的源码和脚本可在上述 Git 基线查询。

本地 `.artifacts/` 原始运行日志、截图、合成夹具及恢复备份、`dist/` 历史发布包继续保留原位，不纳入 Git，不删除、不移动。SDK、NuGet 缓存和真实 Harness 数据不在归档范围。
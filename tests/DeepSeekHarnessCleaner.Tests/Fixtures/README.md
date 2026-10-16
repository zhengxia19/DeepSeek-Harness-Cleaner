# 测试夹具（tests/Fixtures）

隔离夹具工厂由 `FixtureRoot`（临时根生命周期）与 `FixtureFactory`（合成内容）组成。
所有夹具位于 `Path.GetTempPath()\dshcleaner-fixtures\fx-<guid>\` 下，测试结束整树删除；
**绝不读写真实 `%USERPROFILE%\.dsh` 或 `%APPDATA%\@deepseek-ai\dsh-desktop`**（`FixtureIsolationTests` 前后快照验证）。
全部文本/载荷为合成数据，不含真实会话正文或凭据。

## 目录布局

```
fx-<guid>/
  .dsh/
    sessions/<project>/<session-id>/session[.vN].jsonl[.zstd]
    storages/session_projcache/sessions/<id>.json
    storages/session_projcache.json
    attachments/<attachment-id>.bin
  @deepseek-ai/dsh-desktop/
    Cache | Code Cache | GPUCache | DawnGraphiteCache | DawnWebGPUCache | logs
    Network/sentinel.txt   ← 白名单外哨兵，永不删除
```

## 覆盖清单

| 夹具 | 工厂方法 | 特征 | 关联 TC |
|---|---|---|---|
| 有效 v0 明文 | `WriteValidV0Session` | `session.jsonl`，mtime 2026-01-05 | TC-02/03 |
| 有效 v4 单帧 zstd | `WriteValidV4Session` | `session.v4.jsonl.zstd` 1 frame | TC-04 |
| 多代际会话 | `WriteMultiGenerationSession` | 同目录 v0 明文 + v4 双帧 zstd | TC-03 |
| Ambiguous 会话 | `WriteAmbiguousSession` | 同代际 `.jsonl` 与 `.jsonl.zstd` 并存 | TC-03/15 |
| 无效 JSON | `WriteCorruptJsonSession` | 未闭合 JSON 行（`CorruptLine`） | TC-04 |
| 未知事件 | `WriteUnknownEventSession` | `quantum_event` 行 | TC-06/20 |
| 截断尾帧 | `WriteTruncatedZstdSession` | 双帧 zstd 尾部裁 8 字节，解压必须抛错 | TC-04/20 |
| 空会话 | `WriteEmptySession` | 0 字节 `session.jsonl` | TC-20 |
| 日期筛选 | `WriteDatedSessions` | 3 会话 mtime=2026-04-01/02/03 | TC-07 |
| 附件引用 | `WriteSessionWithAttachmentReference` | 日志含 `attachment_ref` 行 + 附件文件 | TC-14 |
| 投影缓存 | `WriteProjectionCache` | `session_projcache/sessions/<id>.json` + 索引 | TC-12 |
| 安全目录 | `CreateDesktopSafeDirectories` | 6 白名单目录 + logs 双日期日志 + `Network` 哨兵 | TC-13 |

## 隔离保证

- `FixtureRoot.Create()` 仅在 `%TEMP%\dshcleaner-fixtures` 下创建；`Dispose()` 幂等删除整树。
- `RealDshRoot_UnchangedByFullFixtureLifecycle` 对真实 `%USERPROFILE%\.dsh` 做前后只读快照（相对路径|长度|mtime）比对，必须完全一致。
- 权限拒绝、reparse 点、并发变更夹具由后续任务（T-15/T-20 相关）在工厂上扩展。

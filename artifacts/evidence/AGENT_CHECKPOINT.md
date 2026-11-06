# AGENT_CHECKPOINT — 2026-11-06 清单第 8 轮（B08，最后一项）

## 状态
- SIMPLIFY-20261106 清单 B01–B08 全部 `[x]`。
- 提交链：清单 `8b29ad5`；B01 `2e1edba`、B02 `eb67973`、B03 `780f20f`、B04 `0c435c1`、B05 `151f325`、B06 `dc40686`、B07 `4e41efb`；B08 本轮提交并推送（哈希见 git log）。
- 目标（按清单执行未完成任务）已达成：B08 为最后一项。

## 本轮（B08 错误码常量折叠）
- 新增 `src/DeepSeekHarnessCleaner.Application/ErrorCodes.cs`：internal 静态常量类，按族分组（Post/Workspace/Attachment-Reference/Session-Log/Path-Root/Cache-Enumeration），54 个白名单错误码唯一字面量定义点。Application IVT→Infrastructure/Tests 既有，无需改 csproj。
- `OutcomeReport.cs` AuditLogCodes 白名单 54 项全部改为 `ErrorCodes.*` 常量引用，无残留字面量。
- 折叠的重复定义（值一律未改）：
  - `ZSTD_TRUNCATED`/`ZSTD_CORRUPT`/`FILE_UNREADABLE`：SessionHeaderReader 三处字面量 → 别名指向 SessionEventReader 同名常量（单一定义点）。
  - `ROOT_INVALID`：原 3 处定义（PathGuard/RootLocator/CleanupPlanner）→ PathGuard 别名 ErrorCodes，RootLocator 别名 PathGuard，CleanupPlanner 别名 ErrorCodes。
  - `PATH_REPARSE`/`PATH_ESCAPE`/`FILE_ACCESS_DENIED`：PathGuard 字面量 → ErrorCodes 别名。
  - `CACHE_STRUCTURE_MISMATCH`：原 2 处定义（CacheCleanupSelection/SessionCacheIndex）→ 均别名 ErrorCodes。
  - `CACHE_NAME_COLLISION`/`CACHE_INVENTORY_MISSING`/`CACHE_ID_NOT_IN_INVENTORY`/`NOT_WHITELISTED`/`UNEXPECTED_LOCATION`/`MISSING`：SessionCacheIndex/SessionLogDiscovery 字面量 → ErrorCodes 别名（两文件补 using Application）。
- 新增 `tests/DeepSeekHarnessCleaner.Tests/ErrorCodesCoverageTests.cs`（3 测试）：反射断言 ErrorCodes 全部常量 ∈ AuditLogCodes；白名单无仅字面量项；别名常量历史字符串值逐一断言。
- 未折叠（非白名单、不同语义域，提交信息注明）：`CROSS_ROOT`（RootGuard/BackupManifest）、`UNSUPPORTED_SCHEMA`（BackupManifest/BackupService）、`CANCELED`（BackupService/CleanupExecutor）；throw 调用点字面量属使用而非定义，未改动。

## 验证（本轮实测）
- `.\.dotnet\dotnet.exe build .\DeepSeekHarnessCleaner.sln -c Release --no-restore -warnaserror`：0 警告 0 错误，EXIT=0。
- `.\.dotnet\dotnet.exe test ...Tests.csproj -c Release --no-build`：681/698 通过（= 基线 678 + 新增 3），17 失败为符号链接权限环境问题，唯一名单 13 项与基线一致（见 acceptance 已知环境问题）。
- WindowsTests `--roots-only`：EXIT=0 全过。

## 下一步
- 无未完成任务；如开启新工作需先形成获授权的新任务清单。

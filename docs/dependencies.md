# 依赖管理（Dependencies）

## 1. 最小依赖集

| 依赖 | 作用 | 决策 |
|---|---|---|
| Microsoft .NET 10 SDK / WindowsDesktop Runtime | C# 编译与 WinForms 运行 | SDK 仅安装在项目本地；发行包不捆绑运行时，客户端自行安装 .NET 10 Windows Desktop Runtime x64 |
| WinForms / System.IO / System.Text.Json / System.IO.Compression | UI、文件与 ZIP | BCL / Framework 引用，无 GUI 三方包 |
| ZstdSharp.Port | 解压 Harness `.zstd` 会话物理日志 | 选择纯托管兼容包，实际 API 和多帧行为必须 fixture 验证 |
| xUnit + Microsoft.NET.Test.Sdk | 单元与集成测试 | 仅开发依赖 |

参考 NuGet 当前可用 [ZstdSharp.Port 0.8.8](https://www.nuget.org/packages/zstdsharp.port)（2026-10-16 检索）。**项目实际使用版本**以 `Directory.Packages.props` + `packages.lock.json` 为权威；在 T-03 写定具体版本，不在此假定已经恢复/安装。

## 2. 管理制度

- 仅读取 NuGet 官方 HTTPS feed，初次 `restore` 可访问网络；发行后的应用不联网。依赖恢复缓存隔离在 `.nuget/packages`。
- NuGet 使用 central package management + lock file；升级必须一次变更包含代码、锁文件、兼容验证和变更说明。
- 新依赖必须写明：不可由 BCL 替代的必要性、许可证、可维护性、漏洞检查结果、运行平台、尺寸影响。
- 正式发布前执行 `dotnet list package --vulnerable --include-transitive`（或新版等价命令），结果归档到测试证据；无法访问漏洞源不得宣称“无漏洞”。
- 禁止 WebView2、Electron、Node runtime、网络遥测、运行时动态下载器、默认捆绑浏览器；不添加数据库、ORM 和系统服务框架。
- 所有生产依赖许可证经人工审核；如使用任何本机 native DLL，须列出来源、SHA 和 x64 架构；默认仅纯托管。
- 依赖问题可触发 ADR 追加决定，不直接在需求文档复制版本信息。

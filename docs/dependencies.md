# 依赖管理（Dependencies）

## 1. 最小依赖集

| 依赖 | 作用 | 决策 |
|---|---|---|
| Microsoft .NET 10 SDK / WindowsDesktop Runtime | C# 编译与 WinForms 运行 | SDK 仅安装在项目本地；发行包不捆绑运行时，客户端自行安装 .NET 10 Windows Desktop Runtime x64 |
| WinForms / System.IO / System.Text.Json / System.IO.Compression | UI、文件与 ZIP | BCL / Framework 引用，无 GUI 三方包 |
| ZstdSharp.Port | 解压 Harness `.zstd` 会话物理日志 | 选择纯托管兼容包，实际 API 和多帧行为必须 fixture 验证 |
| YamlDotNet 18.1.0 | 只读解析发行版 YAML 配置层，保留 `!!js` 文本 | BCL 不提供 YAML 解析器；使用有界事件扫描和节点模型，不反序列化为运行时类型、不执行表达式 |
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

## A04 新依赖核查（2026-10-20）

YamlDotNet 版本与哈希固定在中央版本文件及 lock 文件，仅从 NuGet 官方源恢复到项目 `.nuget/packages`。
[NuGet 元数据](https://www.nuget.org/packages/YamlDotNet/18.1.0)与包内 nuspec 声明 MIT，
[上游许可证文本](https://github.com/aaubry/YamlDotNet/blob/master/LICENSE.txt)已核对。Agent核查不替代人工决定；用户已于2026-10-21在本会话明确答复“同意”使用该依赖，见[人工审核记录](../artifacts/evidence/change-plan-2026-10-20/dependency-review.md)。
选用低层解析接口，避免自行拼凑 YAML 语法或引入 Node 运行时。纯托管 net10.0 DLL 为300032字节，新增生产传递依赖为0；最终发布包尺寸尚未测量。
本轮对解决方案执行 `dotnet list DeepSeekHarnessCleaner.sln package --vulnerable --include-transitive`，退出0，官方源未报告已知漏洞；这不代表保证不存在漏洞。证据为 `.artifacts/FinalAcceptance/dependency-audit.txt`。

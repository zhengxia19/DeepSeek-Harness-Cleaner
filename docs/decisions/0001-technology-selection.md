# ADR-0001：采用 C# / .NET 10 / WinForms

- 状态：已接受（用户于 2026-10-16 明确选择方案 B）
- 日期：2026-10-16

## 背景

目标为 Windows 10 单机清理工具，需要可靠目录操作、原生列表/预览和独立 EXE；无现代 Web UI 需求。

## 决策

C# + .NET 10 + Windows Forms；使用系统原生 `DataGridView`、`SplitContainer`、`RichTextBox` 等。分层设计，应用自包含发布。SDK 及 NuGet 依赖仅安装在项目目录。

## 评估替代

A：Python/Tkinter（实现快、GUI 复杂表格成本较高）；C：Python/PySide6（丰富 UI、依赖和打包更重）；B 在 Windows-only 长期维护与 UI 原生程度上综合更优。

## 影响和特殊风险

- Zstandard 依赖需通过真实多帧 fixture 复核。
- .NET 10 对 Win10 的微软官方支持主要覆盖仍受支持的 LTSC/Enterprise；不得把未验证的 Win10 普通版宣称为官方支持。要求发布时对用户目标 Win10 SKU/Build 完成启动、退出、路径安全和清理 fixture 冒烟验证，结果写入 release notes。
- 不在此 ADR 擅自降级目标框架；若兼容验收失败，新增 ADR 评估更改 TFM 后重新运行完整回归，并由用户决定路线。

参考：<https://learn.microsoft.com/en-us/dotnet/core/install/windows>。

## 2026-10-20 发布方式修订（用户明确指定）

本节替代原决策中“应用自包含发布”的部分，其余技术栈与隔离要求不变。默认发布为 `win-x64` Release 单文件 EXE，使用 `--self-contained false -p:PublishSingleFile=true`，不捆绑 .NET 运行时；客户端自行安装 .NET 10 Windows Desktop Runtime x64。

目的为减少发布包体积，运行时安装由客户端负责。缺少运行时的客户端无法直接启动；当前仅验证编译、发布配置与产物哈希，客户端启动及功能验收仍待执行。原 0.1.0 自包含包的验收记录保留为历史证据。

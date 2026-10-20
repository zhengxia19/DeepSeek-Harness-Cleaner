# DeepSeek Harness Cleaner

Windows 10 x64 本地历史清理工具，C# / .NET 10 / WinForms。当前交付 0.2.4，用户于 2026-10-20 确认完成实机验收，详见 [验收记录](docs/acceptance.md)。

- 扫描、筛选和预览会话，分别显示归档信息与解析状态。
- 按选中的整个会话生成精确文件计划；同一会话的多个物理代际分别列出，两个会话可以对应三个文件。
- 会话及附件删除可选择原始 ZIP 备份；未备份也可明确确认删除，仍检查 Harness 退出状态、受控路径、指纹与源文件变化。
- 结果报告在父窗体居中，支持调整大小、复制日志路径和表格双向滚动。
- 附件表格右键可用系统默认应用打开；已知无扩展名文件使用校验副本，未知类型不猜扩展名。
- 主窗关闭时将位置、大小与状态记录在程序同目录 `DeepSeekHarnessCleaner.window-state.json`，下次启动恢复。
- 会话表头“选择”切换当前筛选结果全选，隐藏勾选计数可见；四类表格标题加粗居中。

发布位于 `dist/v0.2.4/win-x64/`，单文件、不捆绑运行时、未签名，需要客户端安装 .NET 10 Windows Desktop Runtime x64。发布、哈希与回退说明见 [发布文档](docs/release.md)。

程序离线工作，不提供云端历史删除、消息改写或自动恢复。未知附件关系不自动连带删除；运行中或状态未知的 Harness 阻断删除。未备份的删除无法由本工具恢复。开发与自动测试仅对隔离合成数据执行写入和删除。

文档入口：[需求](docs/requirements.md) · [架构](docs/architecture.md) · [设计](docs/design.md) · [接口](docs/interfaces.md) · [开发](docs/development.md) · [测试](docs/testing.md) · [任务](docs/implementation-tasks.md)。

历史变更、计划、调研、复审、验收证据及归档前文档快照见 [历史归档](archive/2026-10-20/README.md)，含逐文件路径、长度与 SHA-256。原始本地日志、恢复备份和旧发布包保留原位。
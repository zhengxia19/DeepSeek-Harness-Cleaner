# 发布与回滚

当前发布为 0.2.4（2026-10-20），用户已报告完成实机验收，范围与来源见 [验收记录](acceptance.md)。

- 文件：`dist/v0.2.4/win-x64/DeepSeekHarnessCleaner.UI.exe`。
- 大小：970763 字节。
- SHA-256：`7c5c371b02a14e226abfb5822aaff2fe3e2938d9f84c3d91eb7aa025236ef3e4`。
- `win-x64`、Release、单文件、`--self-contained false -p:PublishSingleFile=true`，未签名。客户端安装 .NET 10 Windows Desktop Runtime x64。
- 既有验证与限制见 [T-38](../archive/2026-10-20/tree/artifacts/evidence/2026-10-20/T-38.txt)。本次文档维护不重新构建 EXE。

后续发布使用项目本地环境与 `scripts/publish.ps1`，按 [测试要求](testing.md) 执行编译、自动测试、Windows 交互与隔离合成数据冒烟，生成并验证包清单；实际结果落盘后才宣称通过。正式发布目录不混入测试产生的窗口状态文件。

回退时使用已保留的旧版本包，先保留当前用户设置和程序目录窗口状态文件。程序版本回退不恢复已经删除的数据；原始 ZIP 仅供人工核对恢复，不保证 Harness 可直接导入。禁止自动覆盖真实 Harness 根。

历次版本、发布命令、兼容性和当时限制见 [归档发布记录](../archive/2026-10-20/tree/docs/release.md) 与 [历史证据](../archive/2026-10-20/tree/artifacts/evidence/)。历史自包含包结论不适用于当前不捆绑运行时的发布包。
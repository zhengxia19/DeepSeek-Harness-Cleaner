# 发布与回滚

当前发布为 0.3.0（2026-11-04），对应 2026-10-20 新计划（T01–T16、A01–A07）验收关闭后的首个发布；验收记录见 [FINAL 证据](../artifacts/evidence/change-plan-2026-10-20/FINAL-acceptance.txt)。

- 文件：`dist/v0.3.0/win-x64/DeepSeekHarnessCleaner.UI.exe`。
- 大小：1492560 字节。
- SHA-256：`ec54fe29e7ab0aeda531c6fb803539596e373410dc8a39b9d5e774025278afda`。
- `win-x64`、Release、单文件、`--self-contained false -p:PublishSingleFile=true`，未签名。客户端安装 .NET 10 Windows Desktop Runtime x64。
- 新增依赖 YamlDotNet 18.1.0（MIT，用户 2026-10-21 批准）已随包捆绑，见 `THIRD-PARTY-NOTICES.txt`。
- 本轮编译、681 项自动测试、四套 Windows 交互门禁与发布 EXE 冒烟的实测命令、退出码与限制见 [发布证据](../artifacts/evidence/release-0.3.0.txt)。
- 已知限制：对话框固定像素布局不随显示缩放缩放，150% 缩放下最小尺寸布局检查失败（多 DPI 为验收已声明的未实测边界）；门禁按 96 DPI 等效环境（DpiUnaware 虚拟化）复跑全过。

上一版 0.2.4（2026-10-20，用户实机验收完成，范围见 [验收记录](acceptance.md)）：`dist/v0.2.4/win-x64/DeepSeekHarnessCleaner.UI.exe`，970763 字节，SHA-256 `7c5c371b02a14e226abfb5822aaff2fe3e2938d9f84c3d91eb7aa025236ef3e4`，同样为单文件、不捆绑运行时。既有验证与限制见 [T-38](../archive/2026-10-20/tree/artifacts/evidence/2026-10-20/T-38.txt)。

后续发布使用项目本地环境与 `scripts/publish.ps1`，按 [测试要求](testing.md) 执行编译、自动测试、Windows 交互与隔离合成数据冒烟，生成并验证包清单；实际结果落盘后才宣称通过。正式发布目录不混入测试产生的窗口状态文件。

回退时使用已保留的旧版本包，先保留当前用户设置和程序目录窗口状态文件。程序版本回退不恢复已经删除的数据；原始 ZIP 仅供人工核对恢复，不保证 Harness 可直接导入。禁止自动覆盖真实 Harness 根。

历次版本、发布命令、兼容性和当时限制见 [归档发布记录](../archive/2026-10-20/tree/docs/release.md) 与 [历史证据](../archive/2026-10-20/tree/artifacts/evidence/)。历史自包含包结论不适用于当前不捆绑运行时的发布包。
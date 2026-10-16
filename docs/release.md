# 发布与回滚（Release）

## 1. 发布版本和产物

- 首次发布候选：`0.1.0`，方案为 `win-x64` 自包含 WinForms EXE；单文件优先，但若 ZstdSharp 与 single-file 组合造成真实问题，保留自包含目录发布并追加 ADR，不能偷偷改路线。
- 发布目录 `dist/v0.1.0/win-x64/`，包含程序 EXE、README 摘要、LICENSE/THIRD-PARTY-NOTICES（如适用）、`SHA256SUMS.txt`。
- 不分发 NuGet 缓存、SDK、真实会话数据、配置或测试 fixtures。

## 2. 放行检查

- [ ] 任务 T-01～T-34 均验收并附证据；所有自动与手工测试按 `testing.md` 通过。
- [ ] 源码/依赖审计，无联网遥测或未核准包。
- [ ] 目标 Win10 Edition/Version/Build、.NET 10 官方支持状态及实测结果明确记录。
- [ ] 冷启动/退出/重复扫描、fixture 备份/删除/失败回退正常。
- [ ] 安全路径测试：UNC、软链/junction、未退出进程、文件变化均拒绝写入。
- [ ] CHANGELOG 的 Planned 已更新为真实 Added/Changed/Fixed，不含未实现功能。

## 3. 构建与校验

在干净的项目环境执行：

```powershell
.\scripts\bootstrap.ps1
.\scripts\test.ps1
.\scripts\publish.ps1
Get-FileHash .\dist\v0.1.0\win-x64\DeepSeekHarnessCleaner.exe -Algorithm SHA256
```

发布脚本中的实际文件名必须以 `*.csproj` 的 AssemblyName 为准。若不支持签名，明确标记“未签名”，禁止暗示已签名。

## 4. 验证及回滚

- 安装：解压/复制独立 EXE 到用户任意非系统目录；无管理员权限，首次运行不修改 Harness 数据。
- 更新：退出旧工具后替换 EXE；旧 `settings.json` 可继续使用或安全降级；不要自动迁移 Harness 原始会话日志。
- 回滚：关闭工具，保留上一个 EXE，覆盖新 EXE 后启动；只对工具自身设置采用版本化迁移/备份。
- **删除后的会话不随程序回滚而恢复**。需要恢复时参考原始 ZIP manifest 手工还原；本版本没有自动恢复能力，也不承诺能直接通过官方导入恢复。
- 发生根目录越界误删、无备份删除、ZIP 校验失效、Win10 启动失败时必须阻断发布。

## 5. 发布记录格式

版本、日期、commit、SDK、目标 OS Build、自动测试统计、手工检查、包 SHA-256、依赖风险扫描摘要、剩余限制、发布审批人。

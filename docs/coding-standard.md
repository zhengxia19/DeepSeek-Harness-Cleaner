# 编码规范（Coding Standard）

## 规则

- 使用 C# 标准命名：公开类型/方法 `PascalCase`，局部变量/参数 `camelCase`，私有字段 `_camelCase`，异步方法 `Async` 后缀；每文件一个主公共类型。
- 开启 `Nullable=enable`、`ImplicitUsings=enable`、`TreatWarningsAsErrors=true`、分析器；统一 UTF-8、CRLF/LF 均可但通过 `.editorconfig` 固定为 LF。
- 领域对象用不可变 record；后台服务以依赖注入构造，禁止在 UI 控件事件里实现路径拼接或删除逻辑。
- 所有路径来自用户授权根 + 明确白名单，不接受未经规范化的字符串直接传给 `File.Delete` / `Directory.Delete`。
- 事件解析使用 `System.Text.Json`；只读取需要的字段，未知字段不报错但不能以缺省值冒充有效数据；遇不支持 schema 必须显式标注。
- 异步文件操作使用 `CancellationToken`，并确保 WinForms UI 更新经 UI 线程调度。
- 不用 `async void`，除 WinForms event handler 外；event handler 必须 catch 并显示错误，不允许未观察的 task exception。
- 禁止静默吞异常、失败后继续执行危险操作；统一 `Result`/错误码报告。可重试操作必须明确幂等性；对删除默认不自动重试。
- 日志有 `Info/Warn/Error` 等级，但禁止包含聊天内容、密钥、认证 token、完整私密文件路径；日志文件写到工具独立目录，不与 Harness 数据目录混用。
- public API、危险边界代码需 XML 注释解释为何拒绝特殊路径、压缩损坏和文件变化；普通 getter/setter 不写重复注释。
- 模块静态检查使用编译器分析；格式化通过 `dotnet format --verify-no-changes`。新增 analyzer 先记录依赖再引入。
- 每个新增核心函数增加正向 + 负向测试；任何 `File.Delete` / `Directory.Delete` 的执行路径必须有根边界和 Harness 退出条件覆盖测试。
- 命令、依赖版本不在本文复制多个可变值：唯一来源 `scripts/` 与 `Directory.Packages.props`。

## 禁止项

- 不允许递归调用 `Directory.Delete(root, true)` 处理用户选择的任意路径。
- 不允许利用管理员权限、重设 ACL 或终止 Harness 进程来绕过保护。
- 不允许“为了解析方便”重写 Harness 原始日志或投影缓存。
- 不允许把运行时下载/联网、自动更新器、遥测库加入程序。

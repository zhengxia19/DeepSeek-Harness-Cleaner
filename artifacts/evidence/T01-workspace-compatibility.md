# T01 兼容性契约证据

状态：BLOCKED（未验收，任务不勾选）。调查日期：2026-10-20。

## 真实执行
- GitHub 连接器读取 Cleaner main `57b5806266cf2618deeeaee2690f824cbf34a1ab` 的 `WorkspaceArchiveReader.cs`、总调度及 T01 定义：成功。
- GitHub 连接器读取公共镜像 `112GT/deepseek-harness-vscode` 的 `harness/packages/workspace/workspace/src/spec.ts` 和 `index.ts`：成功；镜像不可当作官方原始发行证据。
- 实际确认镜像的 domain v2 定义了 `archivedSessionIds`、`workspaceIds`、`initialized`、`pendingMutation`，未定义 `pinnedSessionIds`。归档不会释放工作区会话成员位置。
- 官方具体发行 commit 与真实文件序列化层：未取得；版本写入白名单保持空。
- 编译/自动化测试/Windows 实测：未执行（本任务仅文档调查，不宣称通过）。

## 验收判断
T01 要求每个可写字段有对应官方上游源码证据。尚未满足，因此 BLOCKED，`[ ]` 保持不变。禁止继续 T02 或启用 workspace.json 同步写入。

详见 `docs/workspace-compatibility.md`。

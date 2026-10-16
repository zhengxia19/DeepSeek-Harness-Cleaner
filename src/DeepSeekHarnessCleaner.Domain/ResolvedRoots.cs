namespace DeepSeekHarnessCleaner.Domain;

/// <summary>根路径的来源：环境默认推导、DSH_HOME 环境变量或用户显式选择。</summary>
public enum RootSource
{
    Default,
    EnvironmentVariable,
    UserSelected,
}

/// <summary>数据根探测结果：结构有效、目录不存在、存在但不是 Harness 数据根。</summary>
public enum RootProbeStatus
{
    Valid,
    Missing,
    NotHarness,
}

/// <summary>单个数据根（.dsh 或桌面 userData）的探测合同。ROOT_INVALID 对应 NotHarness。</summary>
public sealed record RootProbe(
    string HomePath,
    RootSource Source,
    RootProbeStatus Status,
    string? ErrorCode)
{
    public bool IsValid => Status == RootProbeStatus.Valid;
}

/// <summary>
/// 已解析的授权数据根（architecture.md §5）。仅当对应探测 Valid 时路径才可进入后续扫描；
/// 定位过程绝不创建任何源目录。
/// </summary>
public sealed record ResolvedRoots(
    string DshHome,
    string SessionRoot,
    string StorageRoot,
    string AttachmentRoot,
    string DesktopHome,
    RootProbe DshProbe,
    RootProbe DesktopProbe);

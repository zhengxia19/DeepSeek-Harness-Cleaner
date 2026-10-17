using DeepSeekHarnessCleaner.Infrastructure;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>T-25：Harness 退出状态检查（TC-15）：Active/Inactive/Unknown 模拟，仅 Inactive 可写入；Unknown fail closed；只读探测。</summary>
public sealed class ProcessProbeTests
{
    private static HarnessProcessProbeResult ProbeWith(params ProcessSnapshot[] snapshots) =>
        new HarnessProcessProbe(() => snapshots).Probe();

    [Fact]
    public void Probe_KnownExeRunning_Active_AndBlocksWrite()
    {
        var result = ProbeWith(new ProcessSnapshot(1234, "DeepSeek Harness", @"C:\Harness\DeepSeek Harness.exe", true));

        Assert.Equal(HarnessProcessState.Active, result.State);
        Assert.False(result.AllowsWrite);
        Assert.Single(result.Matches);
        Assert.Equal(1234, result.Matches[0].ProcessId);
        Assert.Equal(@"C:\Harness\DeepSeek Harness.exe", result.Matches[0].ExecutablePath);
    }

    [Fact]
    public void Probe_NoMatch_Inactive_AllowsWrite()
    {
        var result = ProbeWith(
            new ProcessSnapshot(1, "chrome", @"C:\chrome\chrome.exe", true),
            new ProcessSnapshot(2, "SomeApp", null, false));

        Assert.Equal(HarnessProcessState.Inactive, result.State);
        Assert.True(result.AllowsWrite);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Probe_NameMatchButPathUnverifiable_Unknown_FailClosed()
    {
        var result = ProbeWith(new ProcessSnapshot(9, "DeepSeek Harness", null, false));

        Assert.Equal(HarnessProcessState.Unknown, result.State);
        Assert.False(result.AllowsWrite);
        Assert.Empty(result.Matches);
        Assert.Contains(result.RiskNotes, r => r.Contains("路径不可验证"));
    }

    [Fact]
    public void Probe_EnumerationThrows_Unknown_WithErrorCode()
    {
        var result = new HarnessProcessProbe(() => throw new UnauthorizedAccessException("denied")).Probe();

        Assert.Equal(HarnessProcessState.Unknown, result.State);
        Assert.False(result.AllowsWrite);
        Assert.Equal(HarnessProcessProbe.EnumerationFailedErrorCode, result.ErrorCode);
    }

    [Fact]
    public void Probe_NameMatchIsCaseInsensitive()
    {
        var result = ProbeWith(new ProcessSnapshot(5, "deepseek HARNESS", @"C:\x\deepseek harness.exe", true));

        Assert.Equal(HarnessProcessState.Active, result.State);
    }

    [Fact]
    public void Probe_PathMatchWithDifferentName_ActiveAndRiskRecorded()
    {
        var result = ProbeWith(new ProcessSnapshot(7, "dsh-runner", @"C:\x\DeepSeek Harness.exe", true));

        Assert.Equal(HarnessProcessState.Active, result.State);
        Assert.Contains(result.RiskNotes, r => r.Contains("主模块匹配"));
    }

    [Fact]
    public void Probe_MultipleMatches_AllRecorded()
    {
        var result = ProbeWith(
            new ProcessSnapshot(11, "DeepSeek Harness", @"C:\a\DeepSeek Harness.exe", true),
            new ProcessSnapshot(12, "DeepSeek Harness", @"C:\b\DeepSeek Harness.exe", true),
            new ProcessSnapshot(13, "DeepSeek Harness", null, false));

        Assert.Equal(HarnessProcessState.Active, result.State);
        Assert.Equal(2, result.Matches.Count);
        // 存在不可验证同名进程：即使已 Active，也记录风险（Unknown 判定被 Active 覆盖但风险留痕）。
        Assert.Contains(result.RiskNotes, r => r.Contains("路径不可验证"));
    }

    [Fact]
    public void Probe_ManagedProcessRiskNoteAlwaysRecorded()
    {
        var inactive = ProbeWith(new ProcessSnapshot(1, "notepad", @"C:\windows\notepad.exe", true));

        Assert.Contains(inactive.RiskNotes, r => r.Contains("托管/子进程"));
    }

    [Fact]
    public void Probe_RealSystem_ReturnsStableStateWithoutThrowing()
    {
        var result = new HarnessProcessProbe().Probe();

        Assert.True(
            result.State is HarnessProcessState.Active or HarnessProcessState.Inactive or HarnessProcessState.Unknown);
        Assert.Equal(result.State == HarnessProcessState.Inactive, result.AllowsWrite);
        Assert.All(result.Matches, m => Assert.NotNull(m.ExecutablePath));
    }
}

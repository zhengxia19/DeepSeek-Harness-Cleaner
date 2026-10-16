using Xunit;
using ZstdSharp;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>
/// 测试基础设施冒烟用例（T-03）。业务用例由后续任务补充。
/// </summary>
public class InfrastructureSmokeTests
{
    [Fact]
    public void Solution_IsBuildableAndTestRunnable()
    {
        Assert.True(true);
    }

    [Fact]
    public void ZstdRoundtrip_Works()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("dsh-cleaner-zstd-smoke");
        using var compressor = new Compressor(3);
        var compressed = compressor.Wrap(data);
        using var decompressor = new Decompressor();
        var restored = decompressor.Unwrap(compressed).ToArray();
        Assert.Equal(data, restored);
    }
}

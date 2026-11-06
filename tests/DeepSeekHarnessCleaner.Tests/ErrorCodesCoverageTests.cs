using System.Reflection;
using DeepSeekHarnessCleaner.Application;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>B08：同名错误码字符串全仓单一定义点；AuditLogCodes 白名单由 ErrorCodes 常量组装。</summary>
public sealed class ErrorCodesCoverageTests
{
    private static List<string> DefinedCodes() =>
        typeof(ErrorCodes)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    private static HashSet<string> AuditLogCodes()
    {
        var field = typeof(OutcomeReport).GetField("AuditLogCodes", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (HashSet<string>)field!.GetValue(null)!;
    }

    [Fact]
    public void AuditLogCodesCoverAllDefinedErrorCodes()
    {
        var defined = DefinedCodes();
        Assert.NotEmpty(defined);
        Assert.Equal(defined.Count, defined.Distinct(StringComparer.Ordinal).Count());
        var whitelist = AuditLogCodes();
        Assert.All(defined, code => Assert.Contains(code, whitelist));
    }

    [Fact]
    public void AuditLogCodesContainNoLiteralOnlyEntries()
    {
        var defined = DefinedCodes();
        var whitelist = AuditLogCodes();
        Assert.All(whitelist, code => Assert.Contains(code, defined));
    }

    [Fact]
    public void FoldedAliasesKeepHistoricalStringValues()
    {
        Assert.Equal("ZSTD_TRUNCATED", DeepSeekHarnessCleaner.Infrastructure.SessionHeaderReader.ZstdTruncatedErrorCode);
        Assert.Equal("ZSTD_CORRUPT", DeepSeekHarnessCleaner.Infrastructure.SessionHeaderReader.ZstdCorruptErrorCode);
        Assert.Equal("FILE_UNREADABLE", DeepSeekHarnessCleaner.Infrastructure.SessionHeaderReader.FileUnreadableErrorCode);
        Assert.Equal("PATH_REPARSE", DeepSeekHarnessCleaner.Infrastructure.PathGuard.PathReparseErrorCode);
        Assert.Equal("PATH_ESCAPE", DeepSeekHarnessCleaner.Infrastructure.PathGuard.PathEscapeErrorCode);
        Assert.Equal("ROOT_INVALID", DeepSeekHarnessCleaner.Infrastructure.PathGuard.RootInvalidErrorCode);
        Assert.Equal("FILE_ACCESS_DENIED", DeepSeekHarnessCleaner.Infrastructure.PathGuard.FileAccessDeniedErrorCode);
        Assert.Equal("CACHE_STRUCTURE_MISMATCH", CacheCleanupSelection.StructureMismatchReason);
        Assert.Equal("CACHE_NAME_COLLISION", DeepSeekHarnessCleaner.Infrastructure.SessionCacheIndex.NameCollisionReason);
        Assert.Equal("NOT_WHITELISTED", DeepSeekHarnessCleaner.Infrastructure.SessionLogDiscovery.NotWhitelistedReason);
        Assert.Equal("MISSING", DeepSeekHarnessCleaner.Infrastructure.SessionLogDiscovery.MissingReason);
    }
}

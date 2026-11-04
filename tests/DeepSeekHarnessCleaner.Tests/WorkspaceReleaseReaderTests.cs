using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DeepSeekHarnessCleaner.Infrastructure;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

public sealed class WorkspaceReleaseReaderTests
{
    private static readonly byte[] Code = Encoding.UTF8.GetBytes("synthetic reviewed module");
    private static IReadOnlyDictionary<string, string> Expected => new Dictionary<string, string>
    {
        ["lib/index.js"] = Convert.ToHexString(SHA256.HashData(Code)),
    };

    private static MemoryStream Archive(string? member = null, byte[]? content = null)
    {
        member ??= "{\"size\":" + Code.Length + ",\"offset\":\"0\"}";
        var json = Encoding.UTF8.GetBytes("{\"files\":{\"lib\":{\"files\":{\"index.js\":" + member + "}}}}");
        var headerSize = 8 + json.Length;
        var bytes = new byte[8 + headerSize + (content ?? Code).Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)(headerSize - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)json.Length);
        json.CopyTo(bytes, 16);
        (content ?? Code).CopyTo(bytes, 8 + headerSize);
        return new MemoryStream(bytes);
    }

    [Fact]
    public void ReadsExactBoundedNestedMember_AndDoesNotAcceptChangedCode()
    {
        using var original = Archive();
        Assert.True(WorkspaceReleaseReader.Matches(original, Expected));
        using var changed = Archive(content: Enumerable.Repeat((byte)'x', Code.Length).ToArray());
        Assert.False(WorkspaceReleaseReader.Matches(changed, Expected));
        Assert.False(WorkspaceReleaseReader.Matches(original, new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData("{\"size\":1,\"offset\":\"0\",\"link\":\"outside.js\"}")]
    [InlineData("{\"size\":1,\"offset\":\"0\",\"unpacked\":true}")]
    [InlineData("{\"size\":1,\"offset\":\"-1\"}")]
    [InlineData("{\"size\":1,\"offset\":\"9223372036854775807\"}")]
    [InlineData("{\"size\":8388609,\"offset\":\"0\"}")]
    [InlineData("{\"size\":-1,\"offset\":\"0\"}")]
    [InlineData("{\"size\":1,\"offset\":\"0\",\"files\":{}}")]
    [InlineData("{\"size\":1,\"size\":1,\"offset\":\"0\"}")]
    public void RejectsLinksUnpackedDirectoryAndOutOfBoundsMembers(string member)
    {
        using var archive = Archive(member);
        Assert.Throws<InvalidDataException>(() => WorkspaceReleaseReader.Matches(archive, Expected));
    }

    [Fact]
    public void RejectsUnboundedHeaderBeforeAllocation()
    {
        var prefix = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), uint.MaxValue);
        using var stream = new MemoryStream(prefix);
        Assert.Throws<InvalidDataException>(() => WorkspaceReleaseReader.Matches(stream, Expected));
    }

    [Fact]
    public void RejectsDuplicateArchiveMembers()
    {
        const string duplicate = "{\"files\":{\"lib\":{\"files\":{\"index.js\":{},\"index.js\":{}}}}}";
        var json = Encoding.UTF8.GetBytes(duplicate);
        var bytes = new byte[16 + json.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(8 + json.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)json.Length);
        json.CopyTo(bytes, 16);
        using var stream = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => WorkspaceReleaseReader.Matches(stream, Expected));
    }

    [Fact]
    public void MissingInstallationIsNotRecognized()
    {
        Assert.False(WorkspaceReleaseReader.MatchesReviewedRelease(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".asar")));
    }
}

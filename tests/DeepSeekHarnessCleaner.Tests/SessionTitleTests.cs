using System.Text;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

public sealed class SessionTitleTests
{
    [Fact]
    public void LastDurableTitleWins_AndUnknownEventsAreIgnored()
    {
        using var fx = FixtureRoot.Create();
        var log = fx.Factory.WriteSessionFile("proj-title", "title-001", "session.v4.jsonl",
            Encoding.UTF8.GetBytes(
                "{\"type\":\"header\",\"sessionId\":\"title-001\",\"formatVersion\":4,\"createdAt\":\"2026-01-01T00:00:00Z\"}\n" +
                "{\"type\":\"session/title\",\"data\":{\"title\":\"Previous title\"}}\n" +
                "{\"type\":\"message\",\"role\":\"user\",\"text\":\"not a title\"}\n" +
                "{\"type\":\"session/title\",\"data\":{\"title\":\"Final title\"}}\n"));
        Assert.Equal("Final title", new SessionTitleReader().Read(log, compressed: false));
    }

    [Fact]
    public void MissingTitleAndMalformedEvents_DoNotCreateGuess()
    {
        using var fx = FixtureRoot.Create();
        var log = fx.Factory.WriteSessionFile("proj-title", "title-002", "session.v4.jsonl",
            Encoding.UTF8.GetBytes("{bad json}\n{\"type\":\"message\",\"text\":\"hello\"}\n"));
        Assert.Null(new SessionTitleReader().Read(log, compressed: false));
    }

    [Fact]
    public void CompressedLog_ReadsLatestDurableTitle()
    {
        using var fx = FixtureRoot.Create();
        var events = "{\"type\":\"header\",\"sessionId\":\"title-zstd\",\"formatVersion\":4,\"createdAt\":\"2026-01-01T00:00:00Z\"}" + "\n"
            + "{\"type\":\"session/title\",\"data\":{\"title\":\"Compressed title\"}}" + "\n";
        var path = fx.Factory.WriteSessionFile("proj-title", "title-zstd", "session.v4.jsonl.zstd",
            FixtureFactory.ZstdFrames(events));
        Assert.Equal("Compressed title", new SessionTitleReader().Read(path, compressed: true));
    }

    [Fact]
    public void MissingFileReturnsNull()
    {
        Assert.Null(new SessionTitleReader().Read(Path.Combine(Path.GetTempPath(),
            "missing-" + Guid.NewGuid().ToString("N") + ".jsonl"), compressed: false));
    }
}

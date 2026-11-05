using Clipstems.Core.Engine;
using Clipstems.Core.Settings;

namespace Clipstems.Tests;

/// <summary>
/// The engine's lifecycle on real code: with no game running (as on a CI
/// runner) it starts, idles without recording, and stops cleanly. Recording
/// itself needs audio hardware and a Steam game, so it is covered by the
/// capture and buffer tests and by live runs.
/// </summary>
public class EngineTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private static EngineOptions Options(TempDir dir) => new()
    {
        BufferRoot = Path.Combine(dir.Path, "buffer"),
        ProcessedLog = Path.Combine(dir.Path, "processed.log"),
        AutoAttach = false,
        CompressBuffer = false,
    };

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what}.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task An_engine_never_started_disposes_cleanly_and_twice()
    {
        using var dir = new TempDir();
        var engine = new ClipstemsEngine(new ClipstemsSettings(), Options(dir));

        await engine.DisposeAsync();
        await engine.DisposeAsync();

        Assert.Equal(EngineStatus.Stopped, engine.Status);
    }

    [Fact]
    public async Task Without_a_game_it_waits_runs_its_loop_and_stops_on_dispose()
    {
        using var dir = new TempDir();
        var engine = new ClipstemsEngine(new ClipstemsSettings(), Options(dir));
        int changes = 0;
        engine.Changed += (_, _) => Interlocked.Increment(ref changes);

        engine.Start();
        await Until(() => Volatile.Read(ref changes) >= 2, "two passes of the engine loop");

        Assert.Contains(engine.Status, new[] { EngineStatus.WaitingForGame, EngineStatus.Recording }); // Recording only if a Steam game runs here
        Assert.DoesNotContain(engine.Issues, i => i.Message == ClipstemsEngine.FaultMessage);
        await engine.DisposeAsync();
        await engine.DisposeAsync();
        Assert.Equal(EngineStatus.Stopped, engine.Status);
    }

    [Fact]
    public async Task A_second_copy_waits_for_the_buffer_and_takes_over_when_the_first_stops()
    {
        using var dir = new TempDir();
        var first = new ClipstemsEngine(new ClipstemsSettings(), Options(dir));
        var second = new ClipstemsEngine(new ClipstemsSettings(), Options(dir));
        try
        {
            first.Start();
            await Until(() => first.Status != EngineStatus.Starting, "the first copy to take the buffer");
            second.Start();
            await Until(() => second.Status == EngineStatus.Blocked, "the second copy to report the buffer in use");
            Assert.Contains(second.Issues, i => i.Message == ClipstemsEngine.BlockedMessage);

            await first.DisposeAsync();
            await Until(() => second.Status != EngineStatus.Blocked, "the second copy to take over");
            Assert.DoesNotContain(second.Issues, i => i.Message == ClipstemsEngine.BlockedMessage);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }
}

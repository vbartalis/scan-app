using ScanApp.Core.Cleanup;
using ScanApp.Core.Simulation;
using ScanApp.Core.Storage;

namespace ScanApp.Core.Tests.Cleanup;

public class MotionAnalyzerTests
{
    private const int Points = 256;

    // Features along travel, with a sideways ripple whose phase drifts with y. With height depending on y alone, every
    // profile is one number plus a fixed shape, so any earlier profile of equal height "matches" and a forward push
    // is indistinguishable from a backward one. The drifting ripple gives each position a distinct profile.
    private static double Height(double x, double y) =>
        2 * Math.Sin(y / 3) + 0.8 * Math.Sin(x / 5 + y / 1.7) + 0.5 * Math.Sin(x / 11 - y / 2.3);

    private static SimulationOptions Options(Func<double, double> speed, double deadTimeS = 0) => new(
        Height,
        PointsPerProfile: Points, ProfileRateHz: 1000, SpeedMmPerSAt: speed,
        DeadTimeStartS: deadTimeS, NoiseMm: 0.002);

    private static PassReader MakePass(SimulationOptions options, int count)
    {
        var ms = new MemoryStream();
        using (var w = new PassWriter(new NonClosingStream(ms), Points, false))
            foreach (var p in SimulatedProfileSource.Generate(options, count)) w.Write(p);
        ms.Position = 0;
        return new PassReader(ms);
    }

    private static MotionAnalysis Analyze(PassReader reader, CancellationToken ct = default) =>
        MotionAnalyzer.Analyze(reader, new MotionOptions(), null, ct);

    [Fact]
    public void Generate_MatchesStreamTimestampsAndCount()
    {
        var profiles = SimulatedProfileSource.Generate(Options(_ => 20), 100).ToArray();

        Assert.Equal(100, profiles.Length);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(i, profiles[i].Sequence);
            Assert.Equal(i * 1_000_000L, profiles[i].TimestampNs);
        }
    }

    [Fact]
    public void Analyze_DeadTimeAtBothEnds_TrimsThem()
    {
        using var reader = MakePass(Options(t => t < 4 ? 20 : 0, deadTimeS: 1.0), 5500);

        var result = Analyze(reader);

        Assert.True(result.HasMovement);
        Assert.InRange(result.AutoTrim.Start, 940, 1060);
        Assert.InRange(result.AutoTrim.End, 3939, 4059);
        Assert.False(result.NoiseFloorCapped);
    }

    [Fact]
    public void Analyze_PauseInside_IsReported()
    {
        using var reader = MakePass(Options(t => t < 1 ? 20 : t < 1.5 ? 0 : 20), 2500);

        var result = Analyze(reader);

        var pause = Assert.Single(result.Pauses);
        Assert.InRange(pause.Start, 940, 1060);
        Assert.InRange(pause.End, 1440, 1560);
        Assert.Empty(result.BackwardRuns);
    }

    [Fact]
    public void Analyze_BackwardSlip_IsFlagged()
    {
        using var reader = MakePass(Options(t => t < 1.5 ? 20 : t < 1.8 ? -20 : t < 2.8 ? 20 : 0), 3300);

        var result = Analyze(reader);

        var run = Assert.Single(result.BackwardRuns);
        Assert.True(run.Start <= 1800 && run.End >= 1500, $"run {run.Start}..{run.End}");
    }

    [Fact]
    public void Analyze_SteadyPush_NoPausesNoBackward()
    {
        using var reader = MakePass(Options(t => t < 4 ? 20 : 0, deadTimeS: 1.0), 5000);

        var result = Analyze(reader);

        Assert.True(result.HasMovement);
        Assert.Empty(result.Pauses);
        Assert.Empty(result.BackwardRuns);
    }

    [Fact]
    public void Analyze_NoStillTime_CapsTheNoiseFloor()
    {
        using var reader = MakePass(Options(_ => 20), 3000);

        var result = Analyze(reader);

        Assert.True(result.NoiseFloorCapped);
        Assert.Equal(0.004f, result.NoiseFloor);
        Assert.True(result.StillThreshold <= 3 * 0.004f + 1e-6f, $"threshold {result.StillThreshold}");
        Assert.True(result.HasMovement);
        Assert.Empty(result.Pauses);
    }

    [Fact]
    public void Analyze_AllStill_FallsBackToWholePass()
    {
        using var reader = MakePass(Options(_ => 0, deadTimeS: 1.0), 3000);

        var result = Analyze(reader);

        Assert.False(result.HasMovement);
        Assert.Equal(new ProfileRange(0, 2999), result.AutoTrim);
        Assert.Empty(result.Pauses);
        Assert.Empty(result.BackwardRuns);
    }

    [Fact]
    public void Analyze_ShorterThanLag_KeepsWholePass()
    {
        using var reader = MakePass(Options(_ => 20), 30);

        var result = Analyze(reader);

        Assert.Equal(new ProfileRange(0, 29), result.AutoTrim);
        Assert.False(result.HasMovement);
        Assert.Equal(30, result.Signal.Length);
        Assert.Equal(30, result.TimeS.Length);
    }

    [Fact]
    public void Analyze_HonoursCancellation()
    {
        using var reader = MakePass(Options(_ => 20), 500);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Analyze(reader, cts.Token));
    }

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}

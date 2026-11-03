using ScanApp.Core.Cleanup;
using ScanApp.Core.Combine;
using ScanApp.Core.Simulation;
using ScanApp.Core.Storage;
using ScanApp.Core.Tests.Combine;
using ScanApp.Core.Viewing;

namespace ScanApp.Core.Tests.Viewing;

public class RawCutTests
{
    private static readonly CutLimits Limits = new(10, 90, -50, 50, 1, 20, true);

    private static bool Keeps(int profile = 50, double x = 0, float height = 5, bool paused = false, bool reflection = false) =>
        RawCut.Keeps(Limits, profile, x, height, paused, reflection);

    [Fact]
    public void Keeps_InsideEverything_IsTrue()
    {
        Assert.True(Keeps());
        Assert.True(Keeps(profile: 10, x: -50, height: 1));
        Assert.True(Keeps(profile: 90, x: 50, height: 20));
    }

    [Fact]
    public void Keeps_OutsideTheTrim_IsFalse()
    {
        Assert.False(Keeps(profile: 9));
        Assert.False(Keeps(profile: 91));
    }

    [Fact]
    public void Keeps_Paused_IsFalse() => Assert.False(Keeps(paused: true));

    [Fact]
    public void Keeps_OutsideX_IsFalse()
    {
        Assert.False(Keeps(x: -50.1));
        Assert.False(Keeps(x: 50.1));
    }

    [Fact]
    public void Keeps_BelowTheBand_IsFalse() => Assert.False(Keeps(height: 0.9f));

    [Fact]
    public void Keeps_AboveTheBand_IsFalse() => Assert.False(Keeps(height: 20.1f));

    [Fact]
    public void Keeps_ReflectionWithFilterOn_IsFalse() => Assert.False(Keeps(reflection: true));

    [Fact]
    public void Keeps_ReflectionWithFilterOff_IsTrue() =>
        Assert.True(RawCut.Keeps(Limits with { FilterReflections = false }, 50, 0, 5, false, true));

    [Fact]
    public void Keeps_NaNHeightWithBand_IsTrue() => Assert.True(Keeps(height: float.NaN));

    [Fact]
    public void CutLimits_From_NullsAreInfinite()
    {
        var none = CutLimits.From(new ProfileRange(3, 7), PassZone.None, false);
        Assert.Equal(new CutLimits(3, 7, double.NegativeInfinity, double.PositiveInfinity,
                                   double.NegativeInfinity, double.PositiveInfinity, false), none);

        var some = CutLimits.From(new ProfileRange(3, 7), new PassZone(1, null, null, 4), true);
        Assert.Equal(new CutLimits(3, 7, 1, double.PositiveInfinity, double.NegativeInfinity, 4, true), some);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Keeps_AgreesWithTheWalk(bool reverse)
    {
        using var reader = RawCutFixture.MakePass();
        var (analysis, cleanup) = RawCutFixture.Cleanup(reader, reverse);
        var all = new PreviewOptions(MaxPoints: int.MaxValue);

        // The cleaned preview samples every point the walk keeps (zone applied, table set), by raw profile and point.
        var cleaned = PassPreviewBuilder.Build(reader, all, cleanup, null, CancellationToken.None).Points;
        var walked = new HashSet<long>();
        for (int i = 0; i < cleaned.Count; i++) walked.Add(Key(cleaned.Profile[i], cleaned.Point[i]));

        var raw = PassPreviewBuilder.Build(reader, all, null, null, CancellationToken.None);
        var geometry = RawGeometry.Build(raw, analysis, cleanup, null);
        var limits = CutLimits.From(cleanup.Trim, cleanup.Zone, filterReflections: true);
        var kept = new HashSet<long>();
        int cutByBand = 0, cutByReflection = 0, cutByPause = 0;
        for (int i = 0; i < raw.Points.Count; i++)
        {
            var p = raw.Points;
            bool paused = (geometry.PointFlags[i] & RawGeometry.Paused) != 0;
            bool reflection = (geometry.PointFlags[i] & RawGeometry.Reflection) != 0;
            if (RawCut.Keeps(limits, p.Profile[i], p.X[i], geometry.PointHeight[i], paused, reflection))
                kept.Add(Key(p.Profile[i], p.Point[i]));
            else if (!cleanup.Zone.InBand(geometry.PointHeight[i])) cutByBand++;
            else if (reflection) cutByReflection++;
            else if (paused) cutByPause++;
        }

        Assert.NotEmpty(walked);
        Assert.True(cutByBand > 1000, $"{cutByBand} cut by the band");
        Assert.True(cutByReflection > 10, $"{cutByReflection} cut as reflections");
        Assert.True(cutByPause > 1000, $"{cutByPause} cut as paused");
        Assert.True(walked.SetEquals(kept),
            $"walk {walked.Count}, raw {kept.Count}, only walk {walked.Except(kept).Count()}, only raw {kept.Except(walked).Count()}");
    }

    private static long Key(int profile, int point) => (long)profile << 32 | (uint)point;
}

/// <summary>
/// A tilted table with a 10 mm box and three dim spike blobs, pushed at 20 mm/s with a half-second pause, with
/// reflectance; its saved cleanup trims, keeps the pause, limits X and the height band and drops the reflections.
/// </summary>
internal static class RawCutFixture
{
    public const int Points = 401;
    public const double RateHz = 200;
    public const int Count = 1100;
    /// <summary>The profiles the pusher stands still (t 2.0..2.5 s).</summary>
    public static readonly ProfileRange Pause = new(400, 500);
    /// <summary>A pause before the trim: unclipped in the analysis, gone from the cleanup.</summary>
    public static readonly ProfileRange EarlyPause = new(0, 15);

    public static PassReader MakePass() => SimulatedPasses.Make(new SimulationOptions((x, y) =>
    {
        double z = 0.5 * (x + 100) / 200 + 0.02 * y;
        return Math.Abs(x) <= 20 && y is >= 30 and <= 50 ? z + 10 : z;
    }, PointsPerProfile: Points, ProfileRateHz: RateHz, SpeedMmPerSAt: t => t is >= 2.0 and < 2.5 ? 0 : 20,
       DeadTimeStartS: 0, WithReflectance: true,
       Spikes: [new SpikeBlob(-50, 20, 2, 11), new SpikeBlob(50, 70, 2, 11), new SpikeBlob(-10, 90, 2, 11)]), Count);

    public static MotionAnalysis Analysis(PassReader reader)
    {
        var timeS = new double[reader.ProfileCount];
        long first = 0;
        int i = 0;
        foreach (var p in reader.ReadAll())
        {
            if (i == 0) first = p.TimestampNs;
            timeS[i++] = (p.TimestampNs - first) / 1e9;
        }
        return new MotionAnalysis(new float[timeS.Length], timeS, 0, 0, false, new ProfileRange(0, timeS.Length - 1), true,
                                  [EarlyPause, Pause], []);
    }

    /// <summary>The saved cleanup as the app builds it: reflections found, then the table fitted on the masked pass, ignoring the zone.</summary>
    public static (MotionAnalysis Analysis, PassCleanup Cleanup) Cleanup(PassReader reader, bool reverse)
    {
        var analysis = Analysis(reader);
        var cleanup = PassCleanup.From(analysis, new PassEdits(30, 1050, -60, 60, 0, 15), reverse);
        var mask = ReflectionFilter.Find(reader, cleanup, null, CancellationToken.None);
        var table = AlignmentMapBuilder.Build(reader, cleanup with { Zone = PassZone.None, Reflections = mask }, null,
                                              CancellationToken.None).Table;
        Assert.NotNull(table);
        Assert.True(mask.Count > 0);
        return (analysis, cleanup with { Table = table, Reflections = mask });
    }
}

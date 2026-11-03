using ScanApp.Core.Cleanup;
using ScanApp.Core.Simulation;
using ScanApp.Core.Storage;
using ScanApp.Core.Tests.Combine;

namespace ScanApp.Core.Tests.Cleanup;

public class ReflectionFilterTests
{
    private const int Points = 801;      // 0.25 mm apart over the 200 mm line
    private const double RateHz = 200;

    private static PassCleanup Whole(PassReader reader) => new(new ProfileRange(0, reader.ProfileCount - 1), [], false);

    private static ReflectionMask Find(PassReader reader, CancellationToken ct = default) =>
        ReflectionFilter.Find(reader, Whole(reader), null, ct);

    /// <summary>Counts the dim spike points (reflectance 31) and the others, and how many of each the mask removes.</summary>
    private static (int Spikes, int SpikesRemoved, int Others, int OthersRemoved) Tally(PassReader reader, ReflectionMask mask)
    {
        int spikes = 0, spikesRemoved = 0, others = 0, othersRemoved = 0;
        for (int i = 0; i < reader.ProfileCount; i++)
        {
            var p = reader.ReadAt(i);
            for (int j = 0; j < p.PointCount; j++)
            {
                if (float.IsNaN(p.Z[j])) continue;
                bool removed = mask.Contains(i, j);
                if (p.Reflectance![j] == 31) { spikes++; if (removed) spikesRemoved++; }
                else { others++; if (removed) othersRemoved++; }
            }
        }
        return (spikes, spikesRemoved, others, othersRemoved);
    }

    [Fact]
    public void Find_RemovesDimSpikeBlobs()
    {
        // The demo plate (5 mm, x -80..80, y 10..160, with bumps) and six dim blobs on the table at its base.
        SpikeBlob[] blobs =
        [
            new(-84, 40, 2, 11), new(84, 70, 2, 11), new(-84, 120, 2, 11),
            new(84, 150, 2, 11), new(0, 6, 2, 11), new(-40, 164, 2, 11),
        ];
        var options = SimulatedProfileSource.DemoPlate() with
        {
            PointsPerProfile = Points, ProfileRateHz = RateHz, DeadTimeStartS = 0, WithReflectance = true, Spikes = blobs,
        };
        using var reader = SimulatedPasses.Make(options, (int)(170 / 20.0 * RateHz));

        var mask = Find(reader);

        var (spikes, spikesRemoved, others, othersRemoved) = Tally(reader, mask);
        Assert.True(spikes > 400, $"{spikes} spike points");
        Assert.True(spikesRemoved >= 0.95 * spikes, $"{spikesRemoved} of {spikes} spike points removed");
        Assert.True(othersRemoved <= 0.0004 * others, $"{othersRemoved} of {others} other points removed");
        Assert.Equal(spikesRemoved + othersRemoved, mask.Count);
    }

    [Fact]
    public void Find_LeavesBrightEdgesAlone()
    {
        // The combine fixture's L-block (10 mm, 18 mm on its round top), all bright: its edges deviate but are no seed.
        var options = new SimulationOptions(CombinerFixture.World, PointsPerProfile: Points, ProfileRateHz: RateHz,
            SpeedMmPerSAt: _ => 34, DeadTimeStartS: 0, WithReflectance: true);
        using var reader = SimulatedPasses.Make(options, (int)(125 / 34.0 * RateHz));

        var mask = Find(reader);

        var (_, _, others, othersRemoved) = Tally(reader, mask);
        Assert.True(othersRemoved <= 0.0004 * others, $"{othersRemoved} of {others} points removed");
    }

    [Fact]
    public void Find_NoReflectance_RemovesNothing()
    {
        var options = new SimulationOptions((_, _) => 0, PointsPerProfile: Points, ProfileRateHz: RateHz, DeadTimeStartS: 0,
            Spikes: [new SpikeBlob(0, 20, 2, 11)]);
        using var reader = SimulatedPasses.Make(options, 400);
        Assert.False(reader.HasReflectance);

        var mask = Find(reader);

        Assert.Same(ReflectionMask.None, mask);
        Assert.Equal(0, mask.Count);
    }

    [Fact]
    public void Find_HonoursCancellation()
    {
        var options = new SimulationOptions((_, _) => 0, PointsPerProfile: Points, ProfileRateHz: RateHz, DeadTimeStartS: 0,
            WithReflectance: true);
        using var reader = SimulatedPasses.Make(options, 400);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Find(reader, cts.Token));
    }
}

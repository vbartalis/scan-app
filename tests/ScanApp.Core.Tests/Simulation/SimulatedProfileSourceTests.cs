using ScanApp.Core.Model;
using ScanApp.Core.Simulation;

namespace ScanApp.Core.Tests.Simulation;

public class SimulatedProfileSourceTests
{
    private static async Task<List<Profile>> TakeAsync(SimulationOptions options, int count)
    {
        await using var source = new SimulatedProfileSource(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await source.ConnectAsync(cts.Token);
        var profiles = new List<Profile>(count);
        await foreach (var p in source.StreamAsync(cts.Token))
        {
            profiles.Add(p);
            if (profiles.Count == count) break;
        }
        return profiles;
    }

    private static SimulationOptions Fast(Func<double, double, double> height, double noise = 0.02, int seed = 1) =>
        new(height, PointsPerProfile: 64, ProfileRateHz: 100_000, NoiseMm: noise, Seed: seed);

    [Fact]
    public async Task Simulated_EmitsConfiguredPointCountAndMonotonicTimestamps()
    {
        var profiles = await TakeAsync(Fast((_, _) => 0), 200);

        Assert.All(profiles, p => Assert.Equal(64, p.PointCount));
        for (int i = 1; i < profiles.Count; i++)
        {
            Assert.True(profiles[i].TimestampNs > profiles[i - 1].TimestampNs);
            Assert.Equal(profiles[i - 1].Sequence + 1, profiles[i].Sequence);
        }
        Assert.Equal(10_000, profiles[1].TimestampNs - profiles[0].TimestampNs);
    }

    [Fact]
    public async Task Simulated_DeadTime_ProducesIdenticalNoiselessProfilesAtStart()
    {
        var options = new SimulationOptions((x, y) => y, PointsPerProfile: 64, ProfileRateHz: 1000, NoiseMm: 0);

        var profiles = await TakeAsync(options, 1100);

        Assert.All(profiles.Take(1000), p => Assert.Equal(profiles[0].Z, p.Z));
        Assert.NotEqual(profiles[0].Z, profiles[1099].Z);
    }

    [Fact]
    public async Task Simulated_SameSeed_SameOutput()
    {
        var a = await TakeAsync(Fast((x, _) => x * 0.01, seed: 7), 50);
        var b = await TakeAsync(Fast((x, _) => x * 0.01, seed: 7), 50);
        var c = await TakeAsync(Fast((x, _) => x * 0.01, seed: 8), 50);

        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(a[i].TimestampNs, b[i].TimestampNs);
            Assert.Equal(a[i].Z, b[i].Z);
        }
        Assert.NotEqual(a[0].Z, c[0].Z);
    }

    [Fact]
    public async Task Simulated_HeightMatchesFunction()
    {
        var profiles = await TakeAsync(Fast((x, _) => x > 0 ? 3 : 0, noise: 0), 5);

        foreach (var p in profiles)
            for (int i = 0; i < p.PointCount; i++)
                Assert.Equal(p.X[i] > 0 ? 3f : 0f, p.Z[i]);
        Assert.Equal(-100f, profiles[0].X[0]);
        Assert.Equal(100f, profiles[0].X[^1]);
    }

    [Fact]
    public async Task Simulated_NoReturn_IsNaNInXAndZ()
    {
        var profiles = await TakeAsync(Fast((x, _) => x < 0 ? double.NaN : 1, noise: 0), 1);

        Assert.True(float.IsNaN(profiles[0].Z[0]));
        Assert.True(float.IsNaN(profiles[0].X[0]));
        Assert.Equal(1f, profiles[0].Z[^1]);
    }
    [Fact]
    public async Task Generate_WithReflectance_SurfaceBrightSpikesDim()
    {
        // A 2 mm table; one blob r 3 mm, 11 mm high, at (0, 0) — the scanner sits still over it.
        var options = new SimulationOptions((_, _) => 2, PointsPerProfile: 401, NoiseMm: 0, WithReflectance: true,
                                            Spikes: [new SpikeBlob(0, 0, 3, 11)]);
        var profiles = SimulatedProfileSource.Generate(options, 20).ToList();
        await using var source = new SimulatedProfileSource(options);
        Assert.True(source.HasReflectance);

        int inside = 0, spikes = 0;
        foreach (var p in profiles)
        {
            Assert.NotNull(p.Reflectance);
            for (int i = 0; i < p.PointCount; i++)
            {
                bool inBlob = Math.Abs(p.X[i]) <= 3 - 1e-3;
                if (!inBlob && Math.Abs(p.X[i]) < 3 + 1e-3) continue;
                if (inBlob) inside++;
                if (p.Reflectance![i] == 31)
                {
                    Assert.True(inBlob, $"spike at x {p.X[i]}");
                    Assert.Equal(13f, p.Z[i]);
                    spikes++;
                }
                else
                {
                    Assert.Equal((ushort)216, p.Reflectance[i]);
                    Assert.Equal(2f, p.Z[i]);
                }
            }
        }
        // Some, not all, of the blob's points are spikes, so a cell's median stays on the surface.
        Assert.InRange(spikes / (double)inside, 0.1, 0.4);

        // The default options still have none.
        var plain = options with { WithReflectance = false, Spikes = null };
        await using var plainSource = new SimulatedProfileSource(plain);
        Assert.False(plainSource.HasReflectance);
        Assert.All(SimulatedProfileSource.Generate(plain, 3), p => Assert.Null(p.Reflectance));
    }
}

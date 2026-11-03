using System.Diagnostics;
using ScanApp.Core.Cleanup;
using ScanApp.Core.Combine;
using ScanApp.Core.Model;
using ScanApp.Core.Simulation;
using ScanApp.Core.Storage;
using Xunit.Abstractions;

namespace ScanApp.Core.Tests.Combine;

/// <summary>Simulated scans shared by the combine tests; each scenario is combined once.</summary>
public sealed class CombinerFixture : IDisposable
{
    public const double LineWidthMm = 200, RateHz = 200;
    // Four points per map column (see SpeedProfile_RecoversVaryingSpeed).
    public const int Points = 801;
    public const double RefSpeed = 34, RotatedSpeed = 26, ShiftedSpeed = 30;
    public static readonly double Theta45 = 45 * Math.PI / 180;
    public const double Tx = 30, Ty = -20, ShiftTx = 15, ShiftTy = -10;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"combiner-{Guid.NewGuid():N}");

    public CombinerFixture()
    {
        Directory.CreateDirectory(_root);
        Rotated = new(() =>
        {
            var repo = ScanRepository.Create(_root, "rotated");
            int a = Record(repo, World, RefSpeed, 125), b = Record(repo, Seen(Theta45, Tx, Ty), RotatedSpeed, 150);
            int c = Record(repo, Seen(0, ShiftTx, ShiftTy), ShiftedSpeed, 125);
            return (repo, [a, b, c]);
        });
        TwoRotated = new(() => Combine(Rotated.Value.Repo, Rotated.Value.Ids[..2]));
        ThreePasses = new(() => Combine(Rotated.Value.Repo, Rotated.Value.Ids));
        SameOrientationSecond = new(() =>
        {
            var ids = Rotated.Value.Ids;
            return Combine(Rotated.Value.Repo, [ids[0], ids[2], ids[1]]);
        });
        SameRotation = new(() =>
        {
            var repo = ScanRepository.Create(_root, "same");
            int a = Record(repo, World, RefSpeed, 125), b = Record(repo, Seen(0, ShiftTx, ShiftTy), RotatedSpeed, 125);
            return Combine(repo, [a, b]);
        });
        Tilted = new(() =>
        {
            // Each pass's table is tilted in its own scanner frame, about -0.85 mm/100 mm in X like scan 6's.
            var repo = ScanRepository.Create(_root, "tilted");
            int a = Record(repo, Tilt(World, -0.0085, 0.3), RefSpeed, 125);
            int b = Record(repo, Tilt(Seen(Theta45, Tx, Ty), -0.0085, -0.4), RotatedSpeed, 150);
            return Combine(repo, [a, b]);
        });
        MixedReflectance = new(() =>
        {
            // The reference recorded reflectance and has four dim spike blobs at the L's base; pass B has none.
            var repo = ScanRepository.Create(_root, "mixed");
            int a = Record(repo, World, RefSpeed, 125, withReflectance: true, spikes: BaseSpikes);
            int b = Record(repo, Seen(Theta45, Tx, Ty), RotatedSpeed, 150);
            return Combine(repo, [a, b]);
        });
        SpikesOnBoth = new(() =>
        {
            // Both passes recorded reflectance and carry dim spike blobs on their tables.
            var repo = ScanRepository.Create(_root, "spikes-both");
            int a = Record(repo, World, RefSpeed, 125, withReflectance: true, spikes: BaseSpikes);
            int b = Record(repo, Seen(Theta45, Tx, Ty), RotatedSpeed, 150, withReflectance: true, spikes: RotatedSpikes);
            return (repo, new[] { a, b });
        });
        FilterOn = new(() => Combine(SpikesOnBoth.Value.Repo, SpikesOnBoth.Value.Ids));
        FilterOffOnB = new(() =>
        {
            var (repo, ids) = SpikesOnBoth.Value;
            _ = FilterOn.Value;
            repo.SaveEdits(ids[1], repo.LoadEdits(ids[1]) with { ReflectionFilter = false });
            return Combine(repo, ids);
        });
        FilterBackOn = new(() =>
        {
            var (repo, ids) = SpikesOnBoth.Value;
            _ = FilterOffOnB.Value;
            repo.SaveEdits(ids[1], repo.LoadEdits(ids[1]) with { ReflectionFilter = null });
            return Combine(repo, ids);
        });
        SlopedAlongTravel = new(() =>
        {
            // Pass B's whole view rises 0.04 mm per mm of travel, about 6 mm over its 150 mm: a table fit gone wrong.
            var repo = ScanRepository.Create(_root, "sloped");
            var seen = Seen(Theta45, Tx, Ty);
            int a = Record(repo, World, RefSpeed, 125);
            int b = Record(repo, (x, y) => seen(x, y) + 0.04 * y, RotatedSpeed, 150);
            return Combine(repo, [a, b]);
        });
        SymmetricBox = new(() =>
        {
            // A plain 60 x 40 x 10 mm box at (0, 60), the second pass at 90°: 90° and 270° fit it equally well.
            var repo = ScanRepository.Create(_root, "symmetric");
            int a = Record(repo, Box, RefSpeed, 125);
            int b = Record(repo, Rotate(Box, Math.PI / 2, 60, 0), RotatedSpeed, 125);
            return Combine(repo, [a, b]);
        });
    }

    private static double Box(double x, double y) => Math.Abs(x) <= 30 && Math.Abs(y - 60) <= 20 ? 10 : 0;

    private static Func<double, double, double> Rotate(Func<double, double, double> world, double theta, double tx, double ty) =>
        (x, y) => world(Math.Cos(theta) * x - Math.Sin(theta) * y + tx, Math.Sin(theta) * x + Math.Cos(theta) * y + ty);

    private static Func<double, double, double> Tilt(Func<double, double, double> height, double perMm, double offsetMm) =>
        (x, y) => height(x, y) + perMm * x + offsetMm;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    public string NewScanParent()
    {
        string parent = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        return parent;
    }

    public Lazy<(ScanRepository Repo, int[] Ids)> Rotated { get; }
    public Lazy<Run> TwoRotated { get; }
    public Lazy<Run> ThreePasses { get; }
    public Lazy<Run> SameOrientationSecond { get; }
    public Lazy<Run> SameRotation { get; }
    public Lazy<Run> Tilted { get; }
    public Lazy<Run> SlopedAlongTravel { get; }
    public Lazy<Run> MixedReflectance { get; }
    public Lazy<(ScanRepository Repo, int[] Ids)> SpikesOnBoth { get; }
    public Lazy<Run> FilterOn { get; }
    public Lazy<Run> FilterOffOnB { get; }
    public Lazy<Run> FilterBackOn { get; }
    public Lazy<Run> SymmetricBox { get; }

    /// <summary>Four blobs r 2 mm, 11 mm high, on the table just beside the L (reference frame).</summary>
    public static readonly SpikeBlob[] BaseSpikes =
        [new(-43, 60, 2, 11), new(-17, 70, 2, 11), new(23, 40, 2, 11), new(0, 27, 2, 11)];

    /// <summary>Four blobs on pass B's own table, in its scanner frame (clear of the L, which sits near (28, 85)).</summary>
    public static readonly SpikeBlob[] RotatedSpikes =
        [new(-60, 40, 2, 11), new(-40, 120, 2, 11), new(70, 30, 2, 11), new(-80, 100, 2, 11)];

    public sealed record Run(ScanRepository Repo, CombineResult Result, MergedCloud Cloud, long Milliseconds);

    private static Run Combine(ScanRepository repo, int[] ids)
    {
        var sw = Stopwatch.StartNew();
        var result = Combiner.CombineAsync(repo, ids, null, CancellationToken.None).GetAwaiter().GetResult();
        long ms = sw.ElapsedMilliseconds;
        return new Run(repo, result, repo.OpenResultCloud(result.Id), ms);
    }

    // The L-shaped block of the pair-aligner tests, at (-10, 60) in the reference frame: x -40..20, y 30..90.
    public static double World(double x, double y) => Object(x + 10, y - 60);

    private static double Object(double u, double v)
    {
        bool bar = u is >= -30 and <= 30 && v is >= -30 and <= -10;
        bool upright = u is >= -30 and <= -10 && v is >= -30 and <= 30;
        if (!bar && !upright) return 0;
        double du = u + 20, dv = v - 15;
        return du * du + dv * dv <= 49 ? 18 : 10;
    }

    // What a pass at pose (theta, tx, ty) sees: its point p lies at R(theta) p + (tx, ty) in the reference frame.
    public static Func<double, double, double> Seen(double theta, double tx, double ty) =>
        (x, y) => World(Math.Cos(theta) * x - Math.Sin(theta) * y + tx, Math.Sin(theta) * x + Math.Cos(theta) * y + ty);

    /// <summary>
    /// Saves a constant-speed pass over <paramref name="lengthMm"/>, trimmed by an edit to the whole pass; with
    /// reflectance (and any <paramref name="spikes"/>) when <paramref name="withReflectance"/> is set.
    /// </summary>
    public static int Record(ScanRepository repo, Func<double, double, double> height, double speed, double lengthMm,
                             bool withReflectance = false, IReadOnlyList<SpikeBlob>? spikes = null)
    {
        int count = (int)Math.Round(lengthMm / speed * RateHz) + 1;
        var options = new SimulationOptions(height, LineWidthMm: LineWidthMm, PointsPerProfile: Points,
            ProfileRateHz: RateHz, SpeedMmPerSAt: _ => speed, DeadTimeStartS: 0, WithReflectance: withReflectance, Spikes: spikes);
        int id;
        using (var pending = repo.BeginPass(PassDirection.CameraFront, Points, options.WithReflectance, "Simulated",
                   new Dictionary<string, string>()))
        {
            foreach (var p in SimulatedProfileSource.Generate(options, count)) pending.Writer.Write(p);
            id = pending.Commit(PassStatus.Complete).Id;
        }
        repo.SaveEdits(id, new PassEdits(0, count - 1));
        return id;
    }
}

[Trait("Category", "Slow")]
[Collection(SlowCombineCollection.Name)]
public class CombinerTests(CombinerFixture fixture, ITestOutputHelper output) : IClassFixture<CombinerFixture>
{
    private const double RefSpeed = CombinerFixture.RefSpeed;

    private static double AngleError(double a, double b) => Math.Abs(Math.IEEERemainder(a - b, 2 * Math.PI)) * 180 / Math.PI;

    private void Show(string name, CombinerFixture.Run run)
    {
        output.WriteLine($"{name}: {run.Milliseconds} ms, {run.Cloud.X.Length} points, uncertain {run.Result.UncertainAlignment}, scale unknown {run.Result.ScaleUnknown}");
        foreach (var p in run.Result.Passes)
            output.WriteLine($"  pass {p.PassId}: theta {p.Pose.ThetaRad * 180 / Math.PI:F2} deg, T ({p.Pose.Tx:F2}, {p.Pose.Ty:F2}), " +
                             $"{p.MeanSpeedMmPerS:F2} mm/s +-{p.SpeedRangeFraction:P0}, overlap {p.OverlapMadMm:F3} mm, table uncertain {p.TableUncertain}");
    }

    /// <summary>
    /// Checks a pass against its truth: rotation ±1°, mean speed ±3 %, and where its points land (the offset)
    /// within 2 mm, judged in the world, so the reference frame's own travel origin does not matter.
    /// </summary>
    private static void AssertPass(CombineResult result, int index, double theta, double speed, double tx, double ty)
    {
        var reference = result.Passes[0];
        var pass = result.Passes[index];
        Assert.InRange(AngleError(pass.Pose.ThetaRad, theta), 0, 1);
        Assert.InRange(pass.MeanSpeedMmPerS / speed, 0.97, 1.03);

        // World y of a reference-frame point = its y plus the reference's travel lag behind the truth.
        double lag = RefSpeed * 1.5 - reference.Speed.TravelAt(1.5);
        foreach (double t in new[] { 1.0, 2.0, 3.0, 4.0 })
            foreach (double x in new[] { -30.0, 0, 30 })
            {
                double y = pass.Speed.TravelAt(t);
                double c = Math.Cos(pass.Pose.ThetaRad), s = Math.Sin(pass.Pose.ThetaRad);
                double qx = c * x - s * y + pass.Pose.Tx, qy = s * x + c * y + pass.Pose.Ty + lag;
                double ct = Math.Cos(theta), st = Math.Sin(theta);
                double wx = ct * x - st * speed * t + tx, wy = st * x + ct * speed * t + ty;
                Assert.True(Math.Abs(qx - wx) <= 2 && Math.Abs(qy - wy) <= 2,
                    $"pass {pass.PassId} at t {t}, x {x}: lands at ({qx:F2}, {qy:F2}), truth ({wx:F2}, {wy:F2})");
            }
    }

    // Four points per map column: with one, every point sits on its column's left edge, half a cell from the centre
    // the map assigns it, which biases a rotated pair's travel by about 0.4 mm.
    private const int DensePoints = 801;

    [Fact]
    public void SpeedProfile_RecoversVaryingSpeed()
    {
        // A textured plate (no section is uniform along the travel), seen at 45° by a pass whose speed swings ±30 %.
        double theta = Math.PI / 4;
        double tx = 50 * Math.Sqrt(2), ty = 80 - 50 * Math.Sqrt(2);  // the plate's centre lands at moving (0, 100)
        Func<double, double> speedAt = t => 30 * (1 + 0.3 * Math.Sin(2 * Math.PI * t / 4));
        var travel = SimulatedPasses.Travel(speedAt, 4000, CombinerFixture.RateHz);
        int count = Array.FindIndex(travel, y => y >= 200) + 1;

        Func<double, double, double> seen = (x, y) => Plate(Math.Cos(theta) * x - Math.Sin(theta) * y + tx,
                                                           Math.Sin(theta) * x + Math.Cos(theta) * y + ty);
        var reference = SimulatedPasses.Map(Plate, 34, 170, CombinerFixture.LineWidthMm, DensePoints, CombinerFixture.RateHz);
        var moving = SimulatedPasses.Map(seen, speedAt, count, CombinerFixture.LineWidthMm, DensePoints, CombinerFixture.RateHz);
        var pose = new PairAlignment(theta, 34, 30, tx, ty, 0, 0);

        var sw = Stopwatch.StartNew();
        var profile = SpeedProfileSolver.Solve(reference, moving, pose, CancellationToken.None);
        output.WriteLine($"solved in {sw.ElapsedMilliseconds} ms, {profile.TimeS.Length} samples");

        var truthProfile = new SpeedProfile([.. Enumerable.Range(0, count).Select(n => n / CombinerFixture.RateHz)], travel[..count]);
        double worst = 0;
        int checkedRows = 0;
        string worstRow = "";
        for (int i = 0; i < profile.TimeS.Length; i++)
        {
            double t = profile.TimeS[i];
            double truth = truthProfile.TravelAt(t);
            output.WriteLine($"t {t:F2} s: travel {profile.TravelMm[i]:F2} mm, truth {truth:F2} mm");
            if (truth is < 25 or > 175) continue;   // only rows over the plate
            double error = Math.Abs(profile.TravelMm[i] - truth) / truth;
            if (error > worst) (worst, worstRow) = (error, $"t {t:F2} s: travel {profile.TravelMm[i]:F2} mm, truth {truth:F2} mm");
            checkedRows++;
        }
        output.WriteLine($"worst relative travel error {worst:P2} over {checkedRows} rows ({worstRow})");
        Assert.True(worst <= 0.02, worstRow);
        Assert.True(checkedRows > 50);
        for (int i = 1; i < profile.TravelMm.Length; i++) Assert.True(profile.TravelMm[i] >= profile.TravelMm[i - 1]);
    }

    // A 6 mm plate (x -50..50, y 20..140) with 40 seeded round bumps (r 3-6 mm, 2-6 mm higher).
    private static readonly (double X, double Y, double R, double H)[] Bumps = MakeBumps();

    private static (double, double, double, double)[] MakeBumps()
    {
        var random = new Random(3);
        return [.. Enumerable.Range(0, 40).Select(_ => (-45 + 90 * random.NextDouble(), 25 + 110 * random.NextDouble(),
            3 + 3 * random.NextDouble(), 2 + 4 * random.NextDouble()))];
    }

    private static double Plate(double x, double y)
    {
        if (Math.Abs(x) > 50 || y < 20 || y > 140) return 0;
        foreach (var (bx, by, r, h) in Bumps)
            if ((x - bx) * (x - bx) + (y - by) * (y - by) <= r * r) return 6 + h;
        return 6;
    }

    [Fact]
    public void Combine_TwoRotatedPasses_SavesConsistentResult()
    {
        var run = fixture.TwoRotated.Value;
        Show("two rotated", run);
        var result = run.Result;
        var ids = fixture.Rotated.Value.Ids;

        Assert.Equal(ids[0], result.ReferencePassId);
        Assert.Equal(ids[..2], result.Passes.Select(p => p.PassId));
        Assert.Equal(new PassPose(0, 0, 0), result.Passes[0].Pose);
        Assert.InRange(result.Passes[0].MeanSpeedMmPerS / RefSpeed, 0.97, 1.03);
        AssertPass(result, 1, CombinerFixture.Theta45, CombinerFixture.RotatedSpeed, CombinerFixture.Tx, CombinerFixture.Ty);
        Assert.False(result.ScaleUnknown);
        Assert.False(result.UncertainAlignment);
        Assert.All(result.Passes, p => Assert.InRange(p.OverlapMadMm, 0, 0.5));
        // Two passes have no cycle to check: null, as in an older result, not empty.
        Assert.Null(result.OpenCycles);
        Assert.Null(Assert.Single(ScanRepository.Open(run.Repo.Directory).Results, r => r.Id == result.Id).OpenCycles);

        // Saved, with the cloud tagged by pass.
        Assert.Contains(result.Id, ScanRepository.Open(run.Repo.Directory).Results.Select(r => r.Id));
        Assert.Equal([0, 1], run.Cloud.PassIndex.Distinct().Order().Select(i => (int)i));

        // The L's upright spans y 30..90 in the world, 60 mm; its top is the only part above 5 mm.
        var ys = Enumerable.Range(0, run.Cloud.Z.Length).Where(i => run.Cloud.Z[i] > 5).Select(i => (double)run.Cloud.Y[i]).ToArray();
        double length = ys.Max() - ys.Min();
        foreach (byte pass in new byte[] { 0, 1 })
        {
            var own = Enumerable.Range(0, run.Cloud.Z.Length).Where(i => run.Cloud.Z[i] > 5 && run.Cloud.PassIndex[i] == pass)
                .Select(i => (double)run.Cloud.Y[i]).ToArray();
            output.WriteLine($"pass index {pass}: object y {own.Min():F2}..{own.Max():F2}");
        }
        output.WriteLine($"object length in the cloud {length:F2} mm");
        Assert.InRange(length / 60, 0.99, 1.01);
    }

    [Fact]
    public void Combine_ThreePasses_AllToReference()
    {
        var run = fixture.ThreePasses.Value;
        Show("three passes", run);
        var result = run.Result;

        Assert.Equal(fixture.Rotated.Value.Ids, result.Passes.Select(p => p.PassId));
        Assert.InRange(result.Passes[0].MeanSpeedMmPerS / RefSpeed, 0.97, 1.03);
        AssertPass(result, 1, CombinerFixture.Theta45, CombinerFixture.RotatedSpeed, CombinerFixture.Tx, CombinerFixture.Ty);
        AssertPass(result, 2, 0, CombinerFixture.ShiftedSpeed, CombinerFixture.ShiftTx, CombinerFixture.ShiftTy);
        Assert.False(result.ScaleUnknown);
        Assert.Equal([0, 1, 2], run.Cloud.PassIndex.Distinct().Order().Select(i => (int)i));
    }

    [Fact]
    public void Combine_ThreePasses_CyclesClose()
    {
        var result = fixture.ThreePasses.Value.Result;

        Assert.NotNull(result.OpenCycles);
        Assert.Empty(result.OpenCycles);
        Assert.False(result.UncertainAlignment);
    }

    [Fact]
    public void Combine_SymmetricObject_FlagsUncertain()
    {
        var run = fixture.SymmetricBox.Value;
        Show("symmetric box", run);

        Assert.True(run.Result.UncertainAlignment);
    }

    [Fact]
    public void Combine_SameRotation_FlagsScaleUnknown()
    {
        var run = fixture.SameRotation.Value;
        Show("same rotation", run);
        var result = run.Result;

        Assert.True(result.ScaleUnknown);
        Assert.Equal(20, result.Passes[0].MeanSpeedMmPerS, 6);
        double ratio = result.Passes[1].MeanSpeedMmPerS / result.Passes[0].MeanSpeedMmPerS;
        Assert.InRange(ratio / (CombinerFixture.RotatedSpeed / RefSpeed), 0.97, 1.03);
        Assert.InRange(AngleError(result.Passes[1].Pose.ThetaRad, 0), 0, 1);
    }

    [Fact]
    public void Combine_TiltedTables_MergesHeightAboveEachTable()
    {
        var run = fixture.Tilted.Value;
        Show("tilted tables", run);
        var cloud = run.Cloud;

        // Table points sit at 0 in every pass.
        var tableP99 = new List<double>();
        foreach (byte pass in new byte[] { 0, 1 })
        {
            var table = Enumerable.Range(0, cloud.Z.Length).Where(i => cloud.PassIndex[i] == pass && cloud.Z[i] < 5)
                .Select(i => Math.Abs((double)cloud.Z[i])).Order().ToArray();
            double p99 = table[(int)(0.99 * (table.Length - 1))];
            output.WriteLine($"pass index {pass}: table |z| p99 {p99:F3} mm over {table.Length} points");
            tableP99.Add(p99);
        }

        // On 2 mm cells of the 10 mm top seen by both passes, the passes agree.
        var cells = new Dictionary<(int, int), (double Sum0, int N0, double Sum1, int N1, bool Edge)>();
        for (int i = 0; i < cloud.Z.Length; i++)
        {
            var key = ((int)Math.Floor(cloud.X[i] / 2), (int)Math.Floor(cloud.Y[i] / 2));
            var cell = cells.GetValueOrDefault(key);
            if (cloud.Z[i] is < 5 or > 14) cell.Edge = true;
            else if (cloud.PassIndex[i] == 0) (cell.Sum0, cell.N0) = (cell.Sum0 + cloud.Z[i], cell.N0 + 1);
            else (cell.Sum1, cell.N1) = (cell.Sum1 + cloud.Z[i], cell.N1 + 1);
            cells[key] = cell;
        }
        var dz = cells.Values.Where(c => !c.Edge && c.N0 >= 3 && c.N1 >= 3).Select(c => c.Sum1 / c.N1 - c.Sum0 / c.N0).ToArray();
        output.WriteLine($"{dz.Length} shared top cells, dz {dz.Min():F3}..{dz.Max():F3} mm");
        Assert.True(dz.Length > 100);
        Assert.All(dz, d => Assert.InRange(d, -0.2, 0.2));
        Assert.All(tableP99, p => Assert.InRange(p, 0, 0.15));
    }

    [Fact]
    public void Combine_TableSlopingAlongTravel_FlagsTableUncertain()
    {
        var run = fixture.SlopedAlongTravel.Value;
        Show("sloped along travel", run);
        var result = run.Result;

        Assert.False(result.Passes[0].TableUncertain);
        Assert.True(result.Passes[1].TableUncertain);
        Assert.True(result.UncertainAlignment);
    }

    [Fact]
    public void Combine_MixedReflectance_FiltersOnlyPassesThatHaveIt()
    {
        var run = fixture.MixedReflectance.Value;
        Show("mixed reflectance", run);
        var result = run.Result;
        output.WriteLine($"reflections removed: {string.Join(", ", result.Passes.Select(p => p.ReflectionsRemoved?.ToString() ?? "null"))}");

        Assert.True(result.Passes[0].ReflectionsRemoved > 0);
        Assert.Null(result.Passes[1].ReflectionsRemoved);
        Assert.InRange(AngleError(result.Passes[1].Pose.ThetaRad, CombinerFixture.Theta45), 0, 1);

        // The spikes are gone from the merged cloud too: the reference's points over each blob are table. A blob's
        // world y is reached at y / RefSpeed seconds, where the reference frame puts the reference's own travel.
        var travel = result.Passes[0].Speed;
        var centres = CombinerFixture.BaseSpikes.Select(b => (b.X, Y: travel.TravelAt(b.Y / RefSpeed))).ToArray();
        var spikeArea = Enumerable.Range(0, run.Cloud.Z.Length).Where(i => run.Cloud.PassIndex[i] == 0
            && centres.Any(b => Math.Pow(run.Cloud.X[i] - b.X, 2) + Math.Pow(run.Cloud.Y[i] - b.Y, 2) < 1)).ToArray();
        output.WriteLine($"{spikeArea.Length} reference points over the blobs");
        Assert.NotEmpty(spikeArea);
        Assert.All(spikeArea, i => Assert.InRange(run.Cloud.Z[i], -0.5f, 0.5f));
    }

    [Fact]
    public void Combine_FilterOffOnOnePass_KeepsItsSpikes()
    {
        var on = fixture.FilterOn.Value;
        var off = fixture.FilterOffOnB.Value;
        Show("filter off on B", off);
        int PointsOfB(CombinerFixture.Run r) => r.Cloud.PassIndex.Count(i => i == 1);
        output.WriteLine($"B points: on {PointsOfB(on)}, off {PointsOfB(off)}");

        Assert.True(on.Result.Passes[1].ReflectionsRemoved > 0);
        Assert.False(on.Result.Passes[1].ReflectionFilterOff);
        Assert.Null(off.Result.Passes[1].ReflectionsRemoved);
        Assert.True(off.Result.Passes[1].ReflectionFilterOff);
        Assert.False(off.Result.Passes[0].ReflectionFilterOff);
        Assert.Equal(on.Result.Passes[0].ReflectionsRemoved, off.Result.Passes[0].ReflectionsRemoved);
        Assert.True(PointsOfB(off) > PointsOfB(on));
    }

    [Fact]
    public void Combine_FilterBackOn_MatchesFilterOn()
    {
        var on = fixture.FilterOn.Value;
        var back = fixture.FilterBackOn.Value;

        Assert.Equal(on.Result.Passes.Select(p => p.Pose), back.Result.Passes.Select(p => p.Pose));
        Assert.Equal(on.Result.Passes.Select(p => p.ReflectionsRemoved), back.Result.Passes.Select(p => p.ReflectionsRemoved));
        Assert.All(back.Result.Passes, p => Assert.False(p.ReflectionFilterOff));
        Assert.Equal(on.Cloud.X, back.Cloud.X);
        Assert.Equal(on.Cloud.Y, back.Cloud.Y);
        Assert.Equal(on.Cloud.Z, back.Cloud.Z);
        Assert.Equal(on.Cloud.PassIndex, back.Cloud.PassIndex);
    }

    private static double ObjectLength(MergedCloud cloud)
    {
        var ys = Enumerable.Range(0, cloud.Z.Length).Where(i => cloud.Z[i] > 5).Select(i => (double)cloud.Y[i]).ToArray();
        return ys.Max() - ys.Min();
    }

    [Fact]
    public void Combine_SelectionOrder_DoesNotChangeTheScale()
    {
        // The reference is solved against the first rotated pass, wherever it sits in the selection.
        var rotatedSecond = fixture.ThreePasses.Value;
        var sameSecond = fixture.SameOrientationSecond.Value;
        Show("same orientation second", sameSecond);

        var a = rotatedSecond.Result.Passes[0].Speed;
        var b = sameSecond.Result.Passes[0].Speed;
        double worst = 0;
        for (double t = 0.5; t <= 3.5; t += 0.05) worst = Math.Max(worst, Math.Abs(a.TravelAt(t) - b.TravelAt(t)));
        output.WriteLine($"reference travel differs by at most {worst:F3} mm; lengths {ObjectLength(rotatedSecond.Cloud):F3} / {ObjectLength(sameSecond.Cloud):F3} mm");
        Assert.InRange(worst, 0, 0.3);
        Assert.InRange(Math.Abs(ObjectLength(rotatedSecond.Cloud) - ObjectLength(sameSecond.Cloud)), 0, 0.2);
        Assert.Equal(rotatedSecond.Result.Passes[0].OverlapMadMm, sameSecond.Result.Passes[0].OverlapMadMm, 3);
    }

    [Fact]
    public void SpeedProfile_NoOverlap_KeepsAMinimumSpeed()
    {
        // Nothing to match anywhere: every travel costs the same, and the solver must not park the pass at 0 mm/s.
        var reference = SimulatedPasses.Map(Plate, 34, 170, CombinerFixture.LineWidthMm, CombinerFixture.Points, CombinerFixture.RateHz);
        var moving = SimulatedPasses.Map(Plate, 30, 170, CombinerFixture.LineWidthMm, CombinerFixture.Points, CombinerFixture.RateHz);
        var profile = SpeedProfileSolver.Solve(reference, moving, new PairAlignment(0, 34, 30, 5000, 5000, 0, 0), CancellationToken.None);

        for (int i = 1; i < profile.TimeS.Length; i++)
        {
            double speed = (profile.TravelMm[i] - profile.TravelMm[i - 1]) / (profile.TimeS[i] - profile.TimeS[i - 1]);
            Assert.True(speed >= PairAligner.MinSpeed - 1e-9, $"speed {speed:F2} mm/s at {profile.TimeS[i]:F2} s");
        }
    }

    [Theory]
    [InlineData(30, 0.1, false)]
    [InlineData(4, 0.1, true)]
    [InlineData(61, 0.1, true)]
    [InlineData(30, 1.9, true)]
    [InlineData(30, 2.0, true)]
    public void Implausible_FlagsSpeedOutsideRangeOrNoOverlap(double meanSpeed, double overlapMad, bool expected)
    {
        var outcome = new PassOutcome(1, new PassPose(0, 0, 0), new SpeedProfile([0, 1], [0, meanSpeed]), meanSpeed, 0, overlapMad);
        Assert.Equal(expected, Combiner.IsImplausible(outcome));
    }

    [Fact]
    public async Task Combine_PassWithoutObject_FailsClearly()
    {
        var repo = ScanRepository.Create(fixture.NewScanParent(), "empty");
        int a = CombinerFixture.Record(repo, CombinerFixture.World, RefSpeed, 125);
        int b = CombinerFixture.Record(repo, (_, _) => 0, RefSpeed, 125);

        var e = await Assert.ThrowsAsync<InvalidDataException>(() => Combiner.CombineAsync(repo, [a, b], null, CancellationToken.None));
        Assert.Equal($"No object found in pass {b}.", e.Message);
        Assert.Empty(repo.Results);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(repo.Directory, "results")));
    }

    [Fact]
    public async Task Combine_Cancelled_WritesNothing()
    {
        var repo = ScanRepository.Create(fixture.NewScanParent(), "cancel");
        int a = CombinerFixture.Record(repo, CombinerFixture.World, RefSpeed, 125);
        int b = CombinerFixture.Record(repo, CombinerFixture.Seen(CombinerFixture.Theta45, CombinerFixture.Tx, CombinerFixture.Ty),
            CombinerFixture.RotatedSpeed, 150);
        using var cts = new CancellationTokenSource();
        var stages = new List<string>();
        var progress = new InlineProgress(r =>
        {
            if (stages.Count == 0 || stages[^1] != r.Stage) stages.Add(r.Stage);
            if (r.Stage != "Reading") cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Combiner.CombineAsync(repo, [a, b], progress, cts.Token));
        Assert.Equal(["Reading", "Aligning"], stages);
        Assert.Empty(repo.Results);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(repo.Directory, "results")));
    }

    /// <summary>The two rotated passes in a fresh scan, so saving their zones leaves the shared fixture alone.</summary>
    private (ScanRepository Repo, int A, int B) NewRotatedPair(string name)
    {
        var repo = ScanRepository.Create(fixture.NewScanParent(), name);
        int a = CombinerFixture.Record(repo, CombinerFixture.World, RefSpeed, 125);
        int b = CombinerFixture.Record(repo, CombinerFixture.Seen(CombinerFixture.Theta45, CombinerFixture.Tx, CombinerFixture.Ty),
            CombinerFixture.RotatedSpeed, 150);
        return (repo, a, b);
    }

    private static void SetZone(ScanRepository repo, int id, PassZone zone) => repo.SaveEdits(id, repo.LoadEdits(id).WithZone(zone));

    [Fact]
    public async Task Combine_LowerBand_DropsTheTableFromTheCloud()
    {
        var (repo, a, b) = NewRotatedPair("band");
        SetZone(repo, a, new PassZone(null, null, 1, null));
        SetZone(repo, b, new PassZone(null, null, 1, null));

        var result = await Combiner.CombineAsync(repo, [a, b], null, CancellationToken.None);
        var cloud = repo.OpenResultCloud(result.Id);
        output.WriteLine($"lower band: {cloud.Z.Length} points, z min {cloud.Z.Min():F4} mm");

        Assert.Equal([0, 1], cloud.PassIndex.Distinct().Order().Select(i => (int)i));
        Assert.All(cloud.Z, z => Assert.True(z >= 1 - 1e-4, $"z {z}"));
        Assert.InRange(AngleError(result.Passes[1].Pose.ThetaRad, CombinerFixture.Theta45), 0, 1);
    }

    [Fact]
    public async Task Combine_ZoneRemovingTheObject_FailsClearly()
    {
        var (repo, a, b) = NewRotatedPair("no object");
        SetZone(repo, b, new PassZone(null, null, 50, null));

        var e = await Assert.ThrowsAsync<InvalidDataException>(() => Combiner.CombineAsync(repo, [a, b], null, CancellationToken.None));
        Assert.Equal($"No object found in pass {b}.", e.Message);
        Assert.Empty(repo.Results);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(repo.Directory, "results")));
    }

    // The L plus a 40 mm block beside it (x 30..55, y 40..70, its corner x > 45, y > 60 cut so it has one way up).
    private static double TwoHeights(double x, double y)
    {
        bool block = x is >= 30 and <= 55 && y is >= 40 and <= 70 && !(x > 45 && y > 60);
        return block ? 40 : CombinerFixture.World(x, y);
    }

    /// <summary>
    /// Three passes whose chained pair cannot align: pass B's zone keeps only what lies under 25 mm (the L), pass C's
    /// only what lies above 35 mm (the block), so B and C share no edge of similar height and the coarse search of
    /// B←C finds no candidate; each still aligns to the reference, which sees both.
    /// </summary>
    private (ScanRepository Repo, int[] Ids) NewDisjointZones(string name)
    {
        var repo = ScanRepository.Create(fixture.NewScanParent(), name);
        int a = CombinerFixture.Record(repo, TwoHeights, RefSpeed, 125);
        int b = CombinerFixture.Record(repo, Seen(TwoHeights, CombinerFixture.Theta45, CombinerFixture.Tx, CombinerFixture.Ty),
            CombinerFixture.RotatedSpeed, 150);
        int c = CombinerFixture.Record(repo, Seen(TwoHeights, 0, CombinerFixture.ShiftTx, CombinerFixture.ShiftTy),
            CombinerFixture.ShiftedSpeed, 125);
        SetZone(repo, b, new PassZone(null, null, null, 25));
        SetZone(repo, c, new PassZone(null, null, 35, null));
        return (repo, [a, b, c]);
    }

    private static Func<double, double, double> Seen(Func<double, double, double> world, double theta, double tx, double ty) =>
        (x, y) => world(Math.Cos(theta) * x - Math.Sin(theta) * y + tx, Math.Sin(theta) * x + Math.Cos(theta) * y + ty);

    [Fact]
    public async Task Combine_ChainedPairFailsToAlign_RecordsAnOpenCycle()
    {
        var (repo, ids) = NewDisjointZones("disjoint");
        var chainFails = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => AlignChain(repo, ids[1], ids[2])));
        output.WriteLine($"chained pair: {chainFails.Message}");

        var result = await Combiner.CombineAsync(repo, ids, null, CancellationToken.None);

        Assert.NotNull(result.OpenCycles);
        Assert.Equal([ids], result.OpenCycles);
        Assert.True(result.UncertainAlignment);
        Assert.Single(repo.Results);
    }

    [Fact]
    public async Task Combine_CancelledDuringAFailingChain_IsCancelled()
    {
        var (repo, ids) = NewDisjointZones("disjoint cancel");
        using var cts = new CancellationTokenSource();
        // The chained alignment is the last of three in "Aligning": its share starts at 2/3.
        var progress = new InlineProgress(r =>
        {
            if (r.Stage == "Aligning" && r.Fraction > 2.0 / 3 + 1e-9) cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Combiner.CombineAsync(repo, ids, progress, cts.Token));
        Assert.Empty(repo.Results);
    }

    /// <summary>The chained pair's maps as the combine builds them (cleanup, filter, zone), aligned on their own.</summary>
    private static PairAlignment AlignChain(ScanRepository repo, int from, int to)
    {
        AlignmentMap Map(int id)
        {
            using var reader = repo.OpenPass(id);
            var analysis = MotionAnalyzer.Analyze(reader, new MotionOptions(), null, CancellationToken.None);
            var cleanup = PassCleanup.From(analysis, repo.LoadEdits(id), false);
            cleanup = cleanup with { Reflections = ReflectionFilter.Find(reader, cleanup, null, CancellationToken.None) };
            return AlignmentMapBuilder.Build(reader, cleanup, null, CancellationToken.None);
        }
        return PairAligner.Align(Map(from), Map(to), null, CancellationToken.None);
    }

    private sealed class InlineProgress(Action<(string Stage, double Fraction)> report) : IProgress<(string stage, double fraction)>
    {
        public void Report((string stage, double fraction) value) => report(value);
    }
}

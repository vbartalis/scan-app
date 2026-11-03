using ScanApp.Core.Cleanup;
using ScanApp.Core.Combine;
using ScanApp.Core.Simulation;
using ScanApp.Core.Storage;

namespace ScanApp.Core.Tests.Combine;

public class AlignmentMapTests
{
    private const int Points = 400;
    private const int Profiles = 4000;   // 4 s at 1 kHz and 20 mm/s: y runs 0..80 mm

    // Table tilted 0.5 mm over the 200 mm line, plus a 40 (x) x 20 (y) x 10 (z) mm box centred on x = 0.
    private static Func<double, double, double> Surface(double boxY0, double shakeMm = 0, double offsetMm = 0) => (x, y) =>
    {
        double z = 0.5 * (x + 100) / 200 + shakeMm * Math.Sin(2 * Math.PI * 10.5 * y / 20) + offsetMm;
        return Math.Abs(x) <= 20 && y >= boxY0 && y <= boxY0 + 20 ? z + 10 : z;
    };

    private static PassReader MakePass(Func<double, double, double> height, int count = Profiles) =>
        SimulatedPasses.Make(new SimulationOptions(height, PointsPerProfile: Points, DeadTimeStartS: 0), count);

    private static PassCleanup Whole(int count = Profiles, bool reverse = false) =>
        new(new ProfileRange(0, count - 1), [], reverse);

    private static AlignmentMap Build(PassReader r, PassCleanup c, CancellationToken ct = default) =>
        AlignmentMapBuilder.Build(r, c, null, ct);

    private static double XOf(AlignmentMap m, int column) => m.XMinMm + (column + 0.5) * m.ColumnMm;

    // Rows are 10 ms = 0.2 mm of travel at 20 mm/s, so y = row * 0.2 + 0.1.
    private static int RowOf(double yMm) => (int)(yMm / 0.2);

    [Fact]
    public void Build_RemovesTiltedTable()
    {
        using var reader = MakePass(Surface(30));

        var map = Build(reader, Whole());

        int checkedCells = 0;
        for (int r = 0; r < map.Rows; r++)
            for (int c = 0; c < map.Columns; c++)
            {
                if (IsBoxArea(map, c, r) || float.IsNaN(map.At(c, r))) continue;
                Assert.InRange(map.At(c, r), -0.1f, 0.1f);
                checkedCells++;
            }
        Assert.True(checkedCells > map.Rows * map.Columns / 2);
    }

    [Fact]
    public void Build_KeepsTheTablePlane()
    {
        using var reader = MakePass(Surface(30, offsetMm: 2));

        var map = Build(reader, Whole());

        Assert.NotNull(map.Table);
        foreach (double x in new[] { -90.0, 0, 90 })
            foreach (double t in new[] { 0.5, 3.5 })
                Assert.InRange(map.Table.At(x, t) - (0.5 * (x + 100) / 200 + 2), -0.02, 0.02);
    }

    [Fact]
    public void Build_MarksObjectAboveThreeMm()
    {
        using var reader = MakePass(Surface(30));

        var map = Build(reader, Whole());

        int inside = 0;
        for (int r = RowOf(32); r < RowOf(48); r++)
            for (int c = 0; c < map.Columns; c++)
            {
                if (Math.Abs(XOf(map, c)) > 18) continue;
                Assert.True(map.IsObject[r * map.Columns + c]);
                Assert.InRange(map.At(c, r), 9.8f, 10.2f);
                inside++;
            }
        Assert.True(inside > 0);
        Assert.False(map.IsObject[RowOf(10) * map.Columns + map.Columns / 2]);
    }

    [Fact]
    public void Build_RemovesPerRowOffsets()
    {
        using var reader = MakePass(Surface(30, shakeMm: 0.15));

        var map = Build(reader, Whole());

        for (int r = 0; r < map.Rows; r++)
            for (int c = 0; c < map.Columns; c++)
            {
                if (IsBoxArea(map, c, r) || float.IsNaN(map.At(c, r))) continue;
                Assert.InRange(map.At(c, r), -0.05f, 0.05f);
            }
    }

    [Fact]
    public void Build_RowsFollowMovingTime_FromCleanup()
    {
        using var reader = MakePass(Surface(30));
        var whole = Build(reader, Whole());

        var paused = Build(reader, new PassCleanup(new ProfileRange(0, Profiles - 1), [new ProfileRange(1000, 1499)], false));

        Assert.Equal(whole.Rows - 50, paused.Rows);
        // The box starts at profile 1500, just after the pause, so it moves up by exactly the 50 removed rows.
        Assert.Equal(FirstObjectRow(whole) - 50, FirstObjectRow(paused));
        Assert.Equal(0.01, paused.RowS);
        Assert.Equal(1.0, paused.ColumnMm);
    }

    [Fact]
    public void Build_Reverse_PutsTheCanonicalStartAtRowZero()
    {
        using var reader = MakePass(Surface(10));   // box at y 10..30, rows 50..150

        var forward = Build(reader, Whole());
        var back = Build(reader, Whole(reverse: true));

        int c = forward.Columns / 2;
        Assert.True(forward.IsObject[RowOf(20) * forward.Columns + c]);
        Assert.False(back.IsObject[RowOf(20) * back.Columns + c]);
        Assert.True(back.IsObject[(back.Rows - 1 - RowOf(20)) * back.Columns + c]);
    }

    [Fact]
    public void Build_MissingDataStaysNaN()
    {
        var surface = Surface(30);
        using var reader = MakePass((x, y) => x is > 60 and < 80 || y is > 60 and < 64 ? double.NaN : surface(x, y));

        var map = Build(reader, Whole());

        for (int r = 0; r < map.Rows; r++)
            for (int c = 0; c < map.Columns; c++)
            {
                double x = XOf(map, c), y = r * 0.2 + 0.1;
                bool hole = x is > 61 and < 79 || y is > 60.5 and < 63.5;
                if (!hole) continue;
                Assert.True(float.IsNaN(map.At(c, r)));
                Assert.False(map.IsObject[r * map.Columns + c]);
            }
        Assert.False(float.IsNaN(map.At(map.Columns / 4, 10)));
    }

    [Fact]
    public void Build_ReportsProgress_AndHonoursCancellation()
    {
        using var reader = MakePass(Surface(30));
        var seen = new List<double>();

        AlignmentMapBuilder.Build(reader, Whole(), new InlineProgress(seen.Add), CancellationToken.None);

        Assert.Equal(1.0, seen[^1]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Build(reader, Whole(), cts.Token));
    }

    [Fact]
    public void Build_NothingKept_GivesEmptyMapAndCompletesProgress()
    {
        using var reader = MakePass(Surface(30), 100);
        var seen = new List<double>();

        var map = AlignmentMapBuilder.Build(reader, new PassCleanup(new ProfileRange(500, 600), [], false),
            new InlineProgress(seen.Add), CancellationToken.None);

        Assert.Equal(0, map.Rows);
        Assert.Equal(1.0, seen[^1]);
    }

    [Fact]
    public void Build_RaisedBoard_FindsTheTableBelowIt()
    {
        var board = SimulatedProfileSource.RaisedBoard().HeightAt;
        // Bare board: 10 mm at the cell and 3 mm around it, clear of the components, holes and board edges.
        bool IsBare(double x, double y) => Math.Abs(x) < 98 && y is > 22 and < 188
            && new[] { (0, 0), (-3, -3), (-3, 3), (3, -3), (3, 3) }.All(d => board(x + d.Item1, y + d.Item2) == 10);

        var map = SimulatedPasses.Map(board, 20, 200, 200, Points, 1000);

        int bare = 0;
        for (int r = 0; r < map.Rows; r++)
            for (int c = 0; c < map.Columns; c++)
            {
                double x = XOf(map, c), y = r * 0.2 + 0.1;
                if (float.IsNaN(map.At(c, r))) continue;
                if (y < 15) Assert.Equal(0f, map.At(c, r));
                if (!IsBare(x, y)) continue;
                Assert.True(map.IsObject[r * map.Columns + c]);
                Assert.InRange(map.At(c, r), 9.8f, 10.2f);
                bare++;
            }
        Assert.True(bare > map.Rows * map.Columns / 2);
        foreach (var (hx, hy) in new[] { (-80.0, 40.0), (80.0, 40.0), (-80.0, 170.0), (80.0, 170.0) })
        {
            Assert.Equal(0, board(hx, hy));
            Assert.Equal(0f, map.At((int)((hx - map.XMinMm) / map.ColumnMm), RowOf(hy)));
        }
        Assert.NotNull(map.Table);
        Assert.InRange(Math.Abs(map.Table.C * map.DurationS), 0, 0.2);
    }

    private static PassCleanup Zoned(PassZone zone) => Whole() with { Zone = zone };

    [Fact]
    public void Build_XRange_MakesOutsideColumnsMissing()
    {
        using var reader = MakePass(Surface(30));

        var map = Build(reader, Zoned(new PassZone(-50, 40, null, null)));

        int inside = 0;
        for (int r = 0; r < map.Rows; r++)
            for (int c = 0; c < map.Columns; c++)
            {
                double x = XOf(map, c);
                if (x is >= -50 and <= 40) { inside += float.IsNaN(map.At(c, r)) ? 0 : 1; continue; }
                Assert.True(float.IsNaN(map.At(c, r)));
                Assert.False(map.IsObject[r * map.Columns + c]);
            }
        Assert.True(inside > map.Rows * 80);
        Assert.True(map.IsObject[RowOf(40) * map.Columns + (int)((0 - map.XMinMm) / map.ColumnMm)]);
    }

    [Fact]
    public void Build_HeightMax_MakesHigherCellsMissing()
    {
        using var reader = MakePass(Surface(30));

        var map = Build(reader, Zoned(new PassZone(null, null, null, 5)));

        int box = 0;
        for (int r = 0; r < map.Rows; r++)
            for (int c = 0; c < map.Columns; c++)
            {
                if (Math.Abs(XOf(map, c)) <= 18 && r >= RowOf(32) && r < RowOf(48))
                {
                    Assert.True(float.IsNaN(map.At(c, r)));
                    Assert.False(map.IsObject[r * map.Columns + c]);
                    box++;
                }
                else if (!IsBoxArea(map, c, r)) Assert.Equal(0f, map.At(c, r));
            }
        Assert.True(box > 0);
    }

    [Fact]
    public void Build_HeightMin_KeepsLowerCellsAsTable()
    {
        using var reader = MakePass(Surface(30));

        var map = Build(reader, Zoned(new PassZone(null, null, 12, null)));

        for (int r = RowOf(32); r < RowOf(48); r++)
            for (int c = 0; c < map.Columns; c++)
                if (Math.Abs(XOf(map, c)) <= 18) Assert.Equal(0f, map.At(c, r));
        Assert.DoesNotContain(true, map.IsObject);
    }

    [Fact]
    public void Build_Zone_DoesNotMoveTheTable()
    {
        using var reader = MakePass(Surface(30, offsetMm: 2));

        var plain = Build(reader, Whole());
        var zoned = Build(reader, Zoned(new PassZone(-30, 30, 1, 5)));

        Assert.NotNull(plain.Table);
        Assert.NotNull(zoned.Table);
        Assert.Equal(plain.Table.A, zoned.Table.A);
        Assert.Equal(plain.Table.B, zoned.Table.B);
        Assert.Equal(plain.Table.C, zoned.Table.C);
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private static int FirstObjectRow(AlignmentMap m)
    {
        int c = m.Columns / 2;
        for (int r = 0; r < m.Rows; r++)
            if (m.IsObject[r * m.Columns + c]) return r;
        return -1;
    }

    private static bool IsBoxArea(AlignmentMap m, int c, int r) =>
        Math.Abs(XOf(m, c)) <= 22 && r >= RowOf(28) && r <= RowOf(52);
    [Fact]
    public void Build_Reflections_SpikesGoneFromTheMap()
    {
        // The box (10 mm, x -20..20, y 30..50) with two dim blobs on its top and one on the table.
        SpikeBlob[] blobs = [new(-8, 36, 2, 11), new(8, 44, 2, 11), new(50, 60, 2, 11)];
        var options = new SimulationOptions(Surface(30), PointsPerProfile: Points, DeadTimeStartS: 0, WithReflectance: true,
            Spikes: blobs);
        using var reader = SimulatedPasses.Make(options, Profiles);
        var mask = ReflectionFilter.Find(reader, Whole(), null, CancellationToken.None);

        var unfiltered = Build(reader, Whole());
        var map = Build(reader, Whole() with { Reflections = mask });

        int spikeCells = 0, raised = 0;
        foreach (var b in blobs)
        {
            float surface = Math.Abs(b.X) <= 20 && b.Y is >= 30 and <= 50 ? 10f : 0f;
            for (int r = 0; r < map.Rows; r++)
                for (int c = 0; c < map.Columns; c++)
                {
                    double dx = XOf(map, c) - b.X, dy = (r + 0.5) * 0.2 - b.Y;
                    if (dx * dx + dy * dy > b.RadiusMm * b.RadiusMm) continue;
                    if (float.IsNaN(map.At(c, r))) continue;
                    Assert.InRange(map.At(c, r), surface - 0.2f, surface + 0.2f);
                    if (unfiltered.At(c, r) > surface + 0.5f) raised++;
                    spikeCells++;
                }
        }
        Assert.True(spikeCells > 100, $"{spikeCells} spike cells");
        Assert.True(raised > spikeCells / 4, $"only {raised} of {spikeCells} cells raised without the filter");
    }
}

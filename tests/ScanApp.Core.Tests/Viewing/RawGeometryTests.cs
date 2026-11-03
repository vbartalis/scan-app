using ScanApp.Core.Cleanup;
using ScanApp.Core.Combine;
using ScanApp.Core.Simulation;
using ScanApp.Core.Tests.Combine;
using ScanApp.Core.Viewing;

namespace ScanApp.Core.Tests.Viewing;

public class RawGeometryTests
{
    private const double Dt = 0.001;
    private const int Profiles = 100, Ppr = 10, Rows = 10, Columns = 4;

    // A table rising 100 mm per second of map time, flat in X, so a wrong map time moves a cell across the band limit.
    private static readonly TablePlane Rising = new(0, 0, 100, 0, 0, 0.01, []);

    private static MotionAnalysis Analysis(params ProfileRange[] pauses) =>
        new(new float[Profiles], [.. Enumerable.Range(0, Profiles).Select(i => i * Dt)], 0, 0, false,
            new ProfileRange(0, Profiles - 1), true, pauses, []);

    /// <summary>A raw preview whose grid is 10 rows of 10 profiles over X 0..4; cell (c, r) = <paramref name="z"/>(c, r).</summary>
    private static PassPreview Preview(Func<int, int, float> z)
    {
        var cells = new float[Rows * Columns];
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Columns; c++)
                cells[r * Columns + c] = z(c, r);
        var grid = new HeightGrid(Columns, Rows, 0, 4, Ppr, [.. Enumerable.Range(0, Rows).Select(r => r * Ppr * Dt)], cells);
        return new PassPreview(grid, new PointSample([], [], [], [], []), Profiles, (Profiles - 1) * Dt, 0, 1);
    }

    private static PassCleanup Cleanup(bool reverse, params ProfileRange[] pauses) =>
        new(new ProfileRange(20, 79), pauses, reverse) { Zone = new PassZone(null, null, 1, null), Table = Rising };

    /// <summary>Per cell: true where the raw view cuts it (no reflections), as <see cref="RawCut.Keeps"/> decides on the cell height.</summary>
    private static bool[] Cut(RawGeometry geometry, HeightGrid grid, PassCleanup cleanup)
    {
        var limits = CutLimits.From(cleanup.Trim, cleanup.Zone, false);
        var cut = new bool[grid.Z.Length];
        for (int r = 0; r < grid.Rows; r++)
            for (int c = 0; c < grid.Columns; c++)
            {
                int i = r * grid.Columns + c;
                bool paused = (geometry.CellFlags[i] & RawGeometry.Paused) != 0;
                cut[i] = !RawCut.Keeps(limits, r * grid.ProfilesPerRow, grid.ColumnToX(c), geometry.CellHeight[i], paused, false);
            }
        return cut;
    }

    // --- Map times ---

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MapTimes_MatchTheCleanedWalk(bool reverse)
    {
        using var reader = RawCutFixture.MakePass();
        var (analysis, cleanup) = RawCutFixture.Cleanup(reader, reverse);
        // The cleaned preview gives every kept point the walk's map time, by raw profile.
        var cleaned = PassPreviewBuilder.Build(reader, new PreviewOptions(MaxPoints: int.MaxValue), cleanup, null,
                                               CancellationToken.None).Points;

        var times = RawGeometry.MapTimes(analysis, cleanup);

        Assert.Equal(reader.ProfileCount, times.Length);
        Assert.Contains(cleaned.Profile, p => p > RawCutFixture.Pause.End);
        for (int i = 0; i < cleaned.Count; i++)
            Assert.Equal(cleaned.TimeS[i], times[cleaned.Profile[i]], 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MapTimes_OutsideTheTrims_ExtendsByRawSteps(bool reverse)
    {
        // Trim 20..79 with a pause 40..49: moving time loses the pause's 10 ms, so the last kept profile is at 49 ms.
        var cleanup = new PassCleanup(new ProfileRange(20, 79), [new ProfileRange(40, 49)], reverse);
        double Moving(double t) => reverse ? 0.049 - t : t;

        var times = RawGeometry.MapTimes(Analysis(new ProfileRange(40, 49)), cleanup);

        Assert.Equal(Moving(0), times[20], 9);
        Assert.Equal(Moving(-20 * Dt), times[0], 9);
        Assert.Equal(Moving(-1 * Dt), times[19], 9);
        Assert.Equal(Moving(20 * Dt), times[50], 9);
        Assert.Equal(Moving(49 * Dt), times[79], 9);
        Assert.Equal(Moving(50 * Dt), times[80], 9);
        Assert.Equal(Moving(69 * Dt), times[99], 9);
    }

    // --- Heights and flags ---

    [Fact]
    public void Build_PointHeightsAreZMinusTable()
    {
        const double rate = 250;
        var board = SimulatedProfileSource.RaisedBoard().HeightAt;
        int count = (int)(200 / 20.0 * rate) + 1;
        using var reader = SimulatedPasses.Make(SimulatedProfileSource.RaisedBoard() with
        {
            PointsPerProfile = 400, ProfileRateHz = rate, DeadTimeStartS = 0,
        }, count);
        var analysis = new MotionAnalysis(new float[count], [.. Enumerable.Range(0, count).Select(i => i / rate)], 0, 0, false,
                                          new ProfileRange(0, count - 1), true, [], []);
        var whole = new PassCleanup(new ProfileRange(0, count - 1), [], false);
        var table = AlignmentMapBuilder.Build(reader, whole, null, CancellationToken.None).Table;
        Assert.NotNull(table);
        var raw = PassPreviewBuilder.Build(reader, new PreviewOptions(), null, null, CancellationToken.None);

        var geometry = RawGeometry.Build(raw, analysis, whole with { Table = table }, null);

        // Clear of every step by 1.5 mm, where a small travel error cannot change the height.
        bool Flat(double x, double y) =>
            new[] { (0.0, 0.0), (-1.5, -1.5), (-1.5, 1.5), (1.5, -1.5), (1.5, 1.5) }
                .All(d => board(x + d.Item1, y + d.Item2) == board(x, y));
        Assert.Equal(raw.Points.Count, geometry.PointHeight.Length);
        int checkedPoints = 0;
        for (int i = 0; i < raw.Points.Count; i++)
        {
            double x = raw.Points.X[i], y = 20 * raw.Points.Profile[i] / rate;
            if (!Flat(x, y)) continue;
            Assert.InRange(geometry.PointHeight[i], board(x, y) - 0.2, board(x, y) + 0.2);
            checkedPoints++;
        }
        Assert.True(checkedPoints > raw.Points.Count / 2, $"{checkedPoints} of {raw.Points.Count} checked");
    }

    [Fact]
    public void Build_NoTable_HeightsAreNaN()
    {
        using var reader = RawCutFixture.MakePass();
        var (analysis, cleanup) = RawCutFixture.Cleanup(reader, false);
        var raw = PassPreviewBuilder.Build(reader, new PreviewOptions(GridColumns: 50, MaxGridRows: 200, MaxPoints: 20_000),
                                           null, null, CancellationToken.None);

        var geometry = RawGeometry.Build(raw, analysis, cleanup with { Table = null }, null);

        Assert.Equal(raw.Points.Count, geometry.PointHeight.Length);
        Assert.Equal(raw.Grid.Z.Length, geometry.CellHeight.Length);
        Assert.All(geometry.PointHeight, h => Assert.True(float.IsNaN(h)));
        Assert.All(geometry.CellHeight, h => Assert.True(float.IsNaN(h)));
    }

    [Fact]
    public void Build_FlagsPausesAndReflections()
    {
        using var reader = RawCutFixture.MakePass();
        var (analysis, cleanup) = RawCutFixture.Cleanup(reader, false);
        var raw = PassPreviewBuilder.Build(reader, new PreviewOptions(GridColumns: 200, MaxGridRows: 1100, MaxPoints: int.MaxValue),
                                           null, null, CancellationToken.None);
        var cells = ReflectionCells.Find(reader, raw.Grid, cleanup.Reflections);

        var geometry = RawGeometry.Build(raw, analysis, cleanup, cells);

        static bool InAny(IEnumerable<ProfileRange> ranges, int i) => ranges.Any(r => i >= r.Start && i <= r.End);
        int paused = 0, reflections = 0;
        for (int i = 0; i < raw.Points.Count; i++)
        {
            byte flags = geometry.PointFlags[i];
            bool isPaused = InAny(analysis.Pauses, raw.Points.Profile[i]);
            bool isReflection = cleanup.Reflections.Contains(raw.Points.Profile[i], raw.Points.Point[i]);
            Assert.Equal(isPaused, (flags & RawGeometry.Paused) != 0);
            Assert.Equal(isReflection, (flags & RawGeometry.Reflection) != 0);
            if (isPaused) paused++;
            if (isReflection) reflections++;
        }
        Assert.Contains(raw.Points.Profile, p => InAny([RawCutFixture.EarlyPause], p));
        Assert.True(paused > 0 && reflections == cleanup.Reflections.Count, $"{paused} paused, {reflections} reflections");

        for (int i = 0; i < geometry.CellFlags.Length; i++)
        {
            int row = i / raw.Grid.Columns;
            Assert.Equal(InAny(analysis.Pauses, row * raw.Grid.ProfilesPerRow), (geometry.CellFlags[i] & RawGeometry.Paused) != 0);
            Assert.Equal(cells[i], (geometry.CellFlags[i] & RawGeometry.Reflection) != 0);
        }
        var none = RawGeometry.Build(raw, analysis, cleanup, null);
        Assert.All(none.CellFlags, f => Assert.Equal(0, f & RawGeometry.Reflection));
    }

    // --- Cell heights, ported from the old zone shading ---

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_CellHeightsMatchOldZoneShading(bool reverse)
    {
        // Kept rows 2..7; map time of a row's first profile counts from profile 20, and runs backwards when reversed.
        double MapTime(int row) => reverse ? 0.059 - (row * Ppr - 20) * Dt : (row * Ppr - 20) * Dt;
        float[] above = [0.5f, 5f, float.NaN, 0.5f];
        var preview = Preview((c, r) => above[c] + (float)(100 * MapTime(r)));
        var cleanup = Cleanup(reverse);

        var geometry = RawGeometry.Build(preview, Analysis(), cleanup, null);

        Assert.Equal(preview.Grid.Z.Length, geometry.CellHeight.Length);
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Columns; c++)
            {
                float h = geometry.CellHeight[r * Columns + c];
                if (float.IsNaN(above[c])) Assert.True(float.IsNaN(h));
                else Assert.Equal(above[c], h, 3);
            }
        var cut = Cut(geometry, preview.Grid, cleanup);
        for (int r = 0; r < Rows; r++)
            Assert.Equal(r is >= 2 and <= 7 ? [true, false, false, true] : [true, true, true, true],
                         cut.Skip(r * Columns).Take(Columns));
    }

    [Fact]
    public void Build_CellHeights_PausedRowsAreCutAndLoseThePause()
    {
        // Trim 20..79 with a pause 40..49: rows 0, 1, 8, 9 are outside the trim and row 4 starts in the pause.
        // After the pause, map time has lost the pause's 10 ms.
        double MapTime(int row) => row < 4 ? (row * Ppr - 20) * Dt : (row * Ppr - 30) * Dt;
        var preview = Preview((_, r) => r is >= 2 and <= 7 and not 4 ? 0.5f + (float)(100 * MapTime(r)) : 0f);
        var pause = new ProfileRange(40, 49);
        var cleanup = Cleanup(false, pause);

        var geometry = RawGeometry.Build(preview, Analysis(pause), cleanup, null);

        for (int r = 2; r <= 7; r++)
        {
            if (r == 4) continue;
            Assert.All(geometry.CellHeight.Skip(r * Columns).Take(Columns), h => Assert.Equal(0.5f, h, 3));
        }
        Assert.All(geometry.CellFlags.Skip(4 * Columns).Take(Columns), f => Assert.Equal(RawGeometry.Paused, f));
        Assert.All(Cut(geometry, preview.Grid, cleanup), Assert.True);
    }

    [Fact]
    public void Build_CellHeights_NoBandOrNoTable_CutOnlyByTrim()
    {
        var preview = Preview((_, _) => -50f);
        var noBand = Cleanup(false) with { Zone = new PassZone(1, 2, null, null) };
        var noTable = Cleanup(false) with { Table = null };

        foreach (var cleanup in new[] { noBand, noTable })
        {
            var geometry = RawGeometry.Build(preview, Analysis(), cleanup, null);
            Assert.Equal(preview.Grid.Z.Length, geometry.CellHeight.Length);
            if (cleanup.Table is null) Assert.All(geometry.CellHeight, h => Assert.True(float.IsNaN(h)));
            var cut = Cut(geometry, preview.Grid, cleanup with { Zone = cleanup.Zone with { XMin = null, XMax = null } });
            for (int r = 0; r < Rows; r++)
                Assert.All(cut.Skip(r * Columns).Take(Columns), c => Assert.Equal(r is < 2 or > 7, c));
        }
    }

    // --- Reflection cells ---

    [Fact]
    public void ReflectionCells_MarksCellsOfMaskedPoints()
    {
        using var reader = RawCutFixture.MakePass();
        var (_, cleanup) = RawCutFixture.Cleanup(reader, false);
        var raw = PassPreviewBuilder.Build(reader, new PreviewOptions(GridColumns: 200, MaxGridRows: 300, MaxPoints: int.MaxValue),
                                           null, null, CancellationToken.None);
        var grid = raw.Grid;

        var cells = ReflectionCells.Find(reader, grid, cleanup.Reflections);

        var expected = new bool[grid.Z.Length];
        double span = grid.XMax - grid.XMin;
        for (int i = 0; i < raw.Points.Count; i++)
        {
            if (!cleanup.Reflections.Contains(raw.Points.Profile[i], raw.Points.Point[i])) continue;
            int column = (int)Math.Clamp((raw.Points.X[i] - grid.XMin) / span * grid.Columns, 0, grid.Columns - 1);
            expected[raw.Points.Profile[i] / grid.ProfilesPerRow * grid.Columns + column] = true;
        }
        Assert.Contains(true, expected);
        Assert.Equal(expected, cells);
        Assert.All(ReflectionCells.Find(reader, grid, ReflectionMask.None), Assert.False);
    }
}

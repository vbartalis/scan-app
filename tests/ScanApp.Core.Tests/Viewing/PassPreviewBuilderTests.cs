using ScanApp.Core.Cleanup;
using ScanApp.Core.Model;
using ScanApp.Core.Storage;
using ScanApp.Core.Viewing;

namespace ScanApp.Core.Tests.Viewing;

public class PassPreviewBuilderTests
{
    private static PassReader MakePass(IEnumerable<Profile> profiles, int points)
    {
        var ms = new MemoryStream();
        using (var w = new PassWriter(new NonClosingStream(ms), points, false))
            foreach (var p in profiles) w.Write(p);
        ms.Position = 0;
        return new PassReader(ms);
    }

    private static Profile Make(long seq, long ns, float[] x, float[] z) => new(seq, ns, x, z, null);

    private static PassPreview Build(PassReader reader, PreviewOptions? options = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
        => PassPreviewBuilder.Build(reader, options ?? new PreviewOptions(), null, progress, ct);

    [Fact]
    public void Build_GridCellIsMeanOfItsPoints()
    {
        // X = 0..3 over [0,3] with 2 columns: x 0,1 -> column 0; x 2,3 -> column 1.
        var profiles = Enumerable.Range(0, 4).Select(s => Make(s, s * 1_000_000_000L,
            [0, 1, 2, 3], [s * 10 + 0, s * 10 + 1, s * 10 + 2, s * 10 + 3])).ToArray();
        using var reader = MakePass(profiles, 4);

        var grid = Build(reader, new PreviewOptions(GridColumns: 2, MaxGridRows: 2)).Grid;

        Assert.Equal(2, grid.Columns);
        Assert.Equal(2, grid.Rows);
        Assert.Equal(2, grid.ProfilesPerRow);
        Assert.Equal((0 + 1 + 10 + 11) / 4f, grid.CellAt(0, 0));
        Assert.Equal((2 + 3 + 12 + 13) / 4f, grid.CellAt(1, 0));
        Assert.Equal((20 + 21 + 30 + 31) / 4f, grid.CellAt(0, 1));
        Assert.Equal((22 + 23 + 32 + 33) / 4f, grid.CellAt(1, 1));
    }

    [Fact]
    public void PassPreviewBuilder_RawSample_CarriesProfileAndPoint()
    {
        // Profile s, point j: X = j, Z = 100 s + j, with a hole at point 2 of profile 3.
        var nan = float.NaN;
        var profiles = Enumerable.Range(0, 20).Select(s => Make(s, s * 1_000_000L, [0, 1, 2, 3, 4],
            [.. Enumerable.Range(0, 5).Select(j => s == 3 && j == 2 ? nan : 100f * s + j)])).ToArray();
        using var reader = MakePass(profiles, 5);

        foreach (var options in new[] { new PreviewOptions(), new PreviewOptions(MaxPoints: 12) })
        {
            var sample = Build(reader, options).Points;
            Assert.Equal(sample.Count, sample.Profile.Length);
            Assert.Equal(sample.Count, sample.Point.Length);
            Assert.NotEmpty(sample.Profile);
            for (int i = 0; i < sample.Count; i++)
            {
                Assert.Equal(sample.Point[i], sample.X[i]);
                Assert.Equal(100f * sample.Profile[i] + sample.Point[i], sample.Z[i]);
            }
        }

        // With a cleanup, the index is still the raw profile's.
        var cleaned = PassPreviewBuilder.Build(reader, new PreviewOptions(),
            new PassCleanup(new ProfileRange(5, 14), [], true), null, CancellationToken.None).Points;
        Assert.Equal(5, cleaned.Profile.Min());
        Assert.Equal(14, cleaned.Profile.Max());
        for (int i = 0; i < cleaned.Count; i++)
            Assert.Equal(100f * cleaned.Profile[i] + cleaned.Point[i], cleaned.Z[i]);
    }

    [Fact]
    public void Build_HolesStayNaN()
    {
        var nan = float.NaN;
        var profiles = Enumerable.Range(0, 3).Select(s => Make(s, s,
            [0, 1, 2, 3, 4, 5], [1, 1, nan, nan, 1, 1])).ToArray();
        using var reader = MakePass(profiles, 6);

        var preview = Build(reader, new PreviewOptions(GridColumns: 6, MaxGridRows: 1));

        // X is valid everywhere, so the range is 0..5; Z is NaN at x=2 and 3, so columns 2 and 3 get nothing.
        Assert.False(float.IsNaN(preview.Grid.CellAt(0, 0)));
        Assert.False(float.IsNaN(preview.Grid.CellAt(1, 0)));
        Assert.True(float.IsNaN(preview.Grid.CellAt(2, 0)));
        Assert.True(float.IsNaN(preview.Grid.CellAt(3, 0)));
        Assert.False(float.IsNaN(preview.Grid.CellAt(4, 0)));
        Assert.False(float.IsNaN(preview.Grid.CellAt(5, 0)));
        Assert.Equal(12, preview.Points.Count);
    }

    [Fact]
    public void Build_RowTimesFollowTimestamps()
    {
        var profiles = Enumerable.Range(0, 4).Select(s => Make(s, s * 1_000_000_000L, [0, 1], [1, 1])).ToArray();
        using var reader = MakePass(profiles, 2);

        var preview = Build(reader, new PreviewOptions(GridColumns: 2, MaxGridRows: 2));

        Assert.Equal([0.0, 2.0], preview.Grid.RowStartTimeS);
        Assert.Equal(3.0, preview.DurationS);
    }

    [Fact]
    public void Build_RespectsCaps()
    {
        const int points = 256;
        var x = Enumerable.Range(0, points).Select(i => (float)i).ToArray();
        var profiles = Enumerable.Range(0, 3000).Select(s =>
            Make(s, s * 1_000_000L, x, Enumerable.Repeat((float)s, points).ToArray()));
        using var reader = MakePass(profiles, points);

        var preview = Build(reader, new PreviewOptions(MaxGridRows: 100, MaxPoints: 10_000));

        Assert.True(preview.Points.Count <= 10_000);
        Assert.True(preview.Grid.Rows <= 100);
        Assert.Equal(0f, preview.Points.TimeS.Min());
        Assert.Equal((float)preview.DurationS, preview.Points.TimeS.Max());
        Assert.Equal(0f, preview.ZMin);
        Assert.Equal(2999f, preview.ZMax);
    }

    [Fact]
    public void Build_AllMissing_GivesEmptyPreview()
    {
        var nan = float.NaN;
        var profiles = Enumerable.Range(0, 5).Select(s => Make(s, s, [nan, nan, nan], [nan, nan, nan]));
        using var reader = MakePass(profiles, 3);

        var preview = Build(reader, new PreviewOptions(GridColumns: 4, MaxGridRows: 2));

        Assert.True(preview.IsEmpty);
        Assert.True(float.IsNaN(preview.ZMin));
        Assert.True(float.IsNaN(preview.ZMax));
        Assert.All(preview.Grid.Z, z => Assert.True(float.IsNaN(z)));
        Assert.Equal(5, preview.ProfileCount);
    }

    [Fact]
    public void Build_ValidPointsOffTheSamplingLattice_IsNotEmpty()
    {
        var nan = float.NaN;
        // MaxPoints 1 gives stride 4, which samples only profile 0/3 and point 0; the one valid point is at (1, 2).
        var profiles = Enumerable.Range(0, 4).Select(s => Make(s, s,
            [0, 1, 2, 3], s == 1 ? [nan, nan, 7, nan] : [nan, nan, nan, nan])).ToArray();
        using var reader = MakePass(profiles, 4);

        var preview = Build(reader, new PreviewOptions(GridColumns: 4, MaxGridRows: 4, MaxPoints: 1));

        Assert.Equal(0, preview.Points.Count);
        Assert.False(preview.IsEmpty);
        Assert.Equal(7f, preview.ZMin);
        Assert.Contains(preview.Grid.Z, z => float.IsFinite(z));
    }

    [Fact]
    public void Build_SingleProfile_HasOneRowAndZeroSpan()
    {
        using var reader = MakePass([Make(0, 5_000, [0, 1, 2], [1, 2, 3])], 3);

        var preview = Build(reader);

        Assert.Equal(1, preview.Grid.Rows);
        Assert.Equal(0.0, preview.DurationS);
        Assert.Equal(3, preview.Points.Count);
    }

    [Fact]
    public void Build_XOutsideSampledRange_GoesToEdgeColumns()
    {
        // Profile 1 is not among the ~300 evenly spaced X-range samples, and is far wider than the rest.
        var profiles = Enumerable.Range(0, 1000).Select(s => s == 1
            ? Make(s, s, [-100, 0, 1, 2, 100], [7, 7, 7, 7, 7])
            : Make(s, s, [0, 0.5f, 1, 1.5f, 2], [1, 1, 1, 1, 1]));
        using var reader = MakePass(profiles, 5);

        var grid = Build(reader, new PreviewOptions(GridColumns: 10, MaxGridRows: 1000)).Grid;

        Assert.Equal(0f, grid.XMin);
        Assert.Equal(2f, grid.XMax);
        Assert.Equal(7f, grid.CellAt(0, 1));
        Assert.Equal(7f, grid.CellAt(9, 1));
    }

    [Fact]
    public void Build_ReportsProgressAndHonoursCancellation()
    {
        var profiles = Enumerable.Range(0, 500).Select(s => Make(s, s, [0, 1], [1, 1]));
        using var reader = MakePass(profiles, 2);
        var reports = new List<double>();

        Build(reader, progress: new SyncProgress(reports.Add));

        Assert.Equal(1.0, reports[^1]);
        Assert.True(reports.Count <= 102);
        Assert.Equal(reports.OrderBy(v => v), reports);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Build(reader, ct: cts.Token));
    }

    private sealed class SyncProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
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
        protected override void Dispose(bool disposing) => inner.Flush();
    }
}

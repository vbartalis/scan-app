using ScanApp.Core.Cleanup;
using ScanApp.Core.Combine;
using ScanApp.Core.Model;
using ScanApp.Core.Simulation;
using ScanApp.Core.Storage;
using ScanApp.Core.Tests.Combine;
using ScanApp.Core.Viewing;

namespace ScanApp.Core.Tests.Viewing;

public class PassPreviewCleanupTests
{
    private const long DtNs = 1_000_000;
    private const double Dt = 0.001;

    // Profile i: X = 0..3, Z = i everywhere (a ramp over the pass).
    private static PassReader MakeRamp(int count)
    {
        var ms = new MemoryStream();
        using (var w = new PassWriter(new NonClosingStream(ms), 4, false))
            for (int i = 0; i < count; i++)
                w.Write(new Profile(i, i * DtNs, [0, 1, 2, 3], [i, i, i, i], null));
        ms.Position = 0;
        return new PassReader(ms);
    }

    private static PassPreview Build(PassReader r, PassCleanup? c, PreviewOptions? o = null)
        => PassPreviewBuilder.Build(r, o ?? new PreviewOptions(GridColumns: 4, MaxGridRows: 1000), c, null, CancellationToken.None);

    [Fact]
    public void Build_WithTrim_UsesOnlyKeptProfiles()
    {
        using var reader = MakeRamp(100);

        var preview = Build(reader, new PassCleanup(new ProfileRange(20, 79), [], false));

        Assert.Equal(60, preview.ProfileCount);
        Assert.Equal(59 * Dt, preview.DurationS, 9);
        Assert.Equal(20f, preview.ZMin);
        Assert.Equal(79f, preview.ZMax);
    }

    [Fact]
    public void Build_WithPause_DropsPauseTimeFromTravel()
    {
        using var reader = MakeRamp(100);

        var preview = Build(reader, new PassCleanup(new ProfileRange(0, 99), [new ProfileRange(40, 59)], false));

        Assert.Equal(80, preview.ProfileCount);
        Assert.Equal(79 * Dt, preview.DurationS, 9);
        // Z 39 and Z 60 are neighbours in time.
        var t39 = preview.Points.TimeS[Array.IndexOf(preview.Points.Z, 39f)];
        var t60 = preview.Points.TimeS[Array.IndexOf(preview.Points.Z, 60f)];
        Assert.Equal(Dt, t60 - t39, 5);
        Assert.DoesNotContain(preview.Points.Z, z => z is >= 40f and <= 59f);
    }

    [Fact]
    public void Build_Reverse_FlipsTravel()
    {
        using var reader = MakeRamp(100);

        var preview = Build(reader, new PassCleanup(new ProfileRange(0, 99), [], true));

        Assert.Equal(99f, preview.Grid.CellAt(0, 0));
        Assert.Equal(0f, preview.Grid.CellAt(0, 99));
        Assert.Equal(0.0, preview.Grid.RowStartTimeS[0], 9);
        Assert.True(preview.Grid.RowStartTimeS[1] > preview.Grid.RowStartTimeS[0]);
        int lowest = Array.IndexOf(preview.Points.Z, 0f);
        Assert.Equal(preview.DurationS, preview.Points.TimeS[lowest], 5);
    }

    [Fact]
    public void Build_NullCleanup_IsUnchanged()
    {
        using var reader = MakeRamp(100);
        var whole = Build(reader, new PassCleanup(new ProfileRange(0, 99), [], false));
        var raw = Build(reader, null);

        Assert.Equal(raw.ProfileCount, whole.ProfileCount);
        Assert.Equal(raw.DurationS, whole.DurationS);
        Assert.Equal(raw.Grid.Z, whole.Grid.Z);
        Assert.Equal(raw.Grid.RowStartTimeS, whole.Grid.RowStartTimeS);
        Assert.Equal(raw.Points.Z, whole.Points.Z);
        Assert.Equal(100, raw.ProfileCount);
        Assert.Equal(99 * Dt, raw.DurationS, 9);
    }

    [Fact]
    public void From_EditsOverrideAuto_AndPausesAreClipped()
    {
        var analysis = new MotionAnalysis([], [], 0f, 0f, false, new ProfileRange(10, 90), true,
            [new ProfileRange(0, 5), new ProfileRange(20, 30), new ProfileRange(45, 60), new ProfileRange(80, 95)], []);

        var auto = PassCleanup.From(analysis, PassEdits.None, false);
        Assert.Equal(new ProfileRange(10, 90), auto.Trim);

        var edited = PassCleanup.From(analysis, new PassEdits(25, 50), true);
        Assert.Equal(new ProfileRange(25, 50), edited.Trim);
        Assert.True(edited.Reverse);
        Assert.Equal([new ProfileRange(25, 30), new ProfileRange(45, 50)], edited.Pauses);
    }

    // A table tilted 0.5 mm over the 200 mm line and 1.6 mm over the 80 mm of travel (20 mm/s, 1 kHz), with a
    // 40 x 20 x 10 mm box at x -20..20, y 30..50.
    private static PassReader MakeBox() => SimulatedPasses.Make(new SimulationOptions((x, y) =>
    {
        double z = 0.5 * (x + 100) / 200 + 0.02 * y;
        return Math.Abs(x) <= 20 && y is >= 30 and <= 50 ? z + 10 : z;
    }, PointsPerProfile: 400, DeadTimeStartS: 0), 4000);

    private static PassCleanup WholeBox(bool reverse = false) => new(new ProfileRange(0, 3999), [], reverse);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_Zone_DropsOutOfZonePoints(bool reverse)
    {
        using var ramp = MakeRamp(100);
        var xRange = Build(ramp, new PassCleanup(new ProfileRange(0, 99), [], reverse) { Zone = new PassZone(1, 2, null, null) });
        Assert.NotEmpty(xRange.Points.X);
        Assert.All(xRange.Points.X, x => Assert.InRange(x, 1f, 2f));

        using var box = MakeBox();
        var table = AlignmentMapBuilder.Build(box, WholeBox(reverse), null, CancellationToken.None).Table;
        Assert.NotNull(table);
        var options = new PreviewOptions(GridColumns: 50, MaxGridRows: 200);
        var all = Build(box, WholeBox(reverse), options);
        var banded = Build(box, WholeBox(reverse) with { Zone = new PassZone(null, null, 1, null), Table = table }, options);

        Assert.NotEmpty(banded.Points.Z);
        Assert.True(banded.Points.Z.Length < all.Points.Z.Length / 4);
        Assert.All(banded.Points.Z, z => Assert.InRange(z, 10f, 12f));
        Assert.All(banded.Points.X, x => Assert.InRange(x, -20.5f, 20.5f));
    }

    [Fact]
    public void Build_BandWithoutTable_IsNotApplied()
    {
        using var box = MakeBox();
        var options = new PreviewOptions(GridColumns: 50, MaxGridRows: 200);

        var all = Build(box, WholeBox(), options);
        var banded = Build(box, WholeBox() with { Zone = new PassZone(null, null, 1, null) }, options);

        Assert.Equal(all.Points.Z, banded.Points.Z);
        Assert.Equal(all.Grid.Z, banded.Grid.Z);
    }

    [Fact]
    public void Build_Reflections_DropsMaskedPoints()
    {
        // A flat 1 mm table with two dim blobs 11 mm high.
        var options = new SimulationOptions((_, _) => 1, PointsPerProfile: 400, DeadTimeStartS: 0, WithReflectance: true,
            Spikes: [new SpikeBlob(-30, 20, 2, 11), new SpikeBlob(40, 50, 2, 11)]);
        using var reader = SimulatedPasses.Make(options, 4000);
        var whole = new PassCleanup(new ProfileRange(0, 3999), [], false);
        var mask = ReflectionFilter.Find(reader, whole, null, CancellationToken.None);
        Assert.True(mask.Count > 0);
        var opts = new PreviewOptions(GridColumns: 200, MaxGridRows: 400);

        var raw = Build(reader, whole, opts);
        var cleaned = Build(reader, whole with { Reflections = mask }, opts);

        Assert.True(raw.ZMax > 10, $"raw z max {raw.ZMax}");
        Assert.True(cleaned.ZMax < 2, $"cleaned z max {cleaned.ZMax}");
        Assert.All(cleaned.Points.Z, z => Assert.InRange(z, 0.5f, 1.5f));
        Assert.All(cleaned.Grid.Z.Where(z => !float.IsNaN(z)), z => Assert.InRange(z, 0.5f, 1.5f));
        Assert.Equal(raw.ProfileCount, cleaned.ProfileCount);
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

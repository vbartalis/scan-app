using ScanApp.Core.Cleanup;
using ScanApp.Core.Combine;
using ScanApp.Core.Simulation;
using ScanApp.Core.Storage;

namespace ScanApp.Core.Tests.Combine;

/// <summary>Simulated passes, saved to memory and read back, for the combine tests.</summary>
internal static class SimulatedPasses
{
    /// <summary>The pass as saved: with reflectance when the options have it.</summary>
    public static PassReader Make(SimulationOptions options, int count)
    {
        var ms = new MemoryStream();
        using (var w = new PassWriter(new NonClosingStream(ms), options.PointsPerProfile, options.WithReflectance))
            foreach (var p in SimulatedProfileSource.Generate(options, count)) w.Write(p);
        ms.Position = 0;
        return new PassReader(ms);
    }

    /// <summary>The alignment map of a whole pass pushed at a constant speed over <paramref name="lengthMm"/>.</summary>
    public static AlignmentMap Map(Func<double, double, double> height, double speedMmPerS, double lengthMm,
                                   double lineWidthMm, int points, double rateHz)
    {
        int count = (int)Math.Round(lengthMm / speedMmPerS * rateHz) + 1;
        var options = new SimulationOptions(height, LineWidthMm: lineWidthMm, PointsPerProfile: points,
            ProfileRateHz: rateHz, SpeedMmPerSAt: _ => speedMmPerS, DeadTimeStartS: 0);
        using var reader = Make(options, count);
        return AlignmentMapBuilder.Build(reader, new PassCleanup(new ProfileRange(0, count - 1), [], false),
            null, CancellationToken.None);
    }

    /// <summary>The alignment map of a whole pass of <paramref name="count"/> profiles pushed at <paramref name="speedAt"/>(t).</summary>
    public static AlignmentMap Map(Func<double, double, double> height, Func<double, double> speedAt, int count,
                                   double lineWidthMm, int points, double rateHz)
    {
        var options = new SimulationOptions(height, LineWidthMm: lineWidthMm, PointsPerProfile: points,
            ProfileRateHz: rateHz, SpeedMmPerSAt: speedAt, DeadTimeStartS: 0);
        using var reader = Make(options, count);
        return AlignmentMapBuilder.Build(reader, new PassCleanup(new ProfileRange(0, count - 1), [], false),
            null, CancellationToken.None);
    }

    /// <summary>
    /// The simulator's travel at each profile (it adds speed(t)·dt from the second profile on, with no dead time);
    /// index i is profile i, at time i / rate.
    /// </summary>
    public static double[] Travel(Func<double, double> speedAt, int count, double rateHz)
    {
        var y = new double[count];
        for (int i = 1; i < count; i++) y[i] = y[i - 1] + speedAt(i / rateHz) / rateHz;
        return y;
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

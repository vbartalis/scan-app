using System.Text;
using ScanApp.Core.Combine;

namespace ScanApp.Core.Storage;

/// <summary>
/// Merged cloud file layout, little-endian: magic "SCNCLD01", int32 count, then per point
/// float32 x, float32 y, float32 z, byte pass index.
/// </summary>
public static class ResultFormat
{
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("SCNCLD01");
    public const int HeaderSize = 8 + 4;
    public const int PointSize = 4 + 4 + 4 + 1;

    internal static void Write(Stream stream, MergedCloud cloud)
    {
        PassFormat.EnsureLittleEndian();
        int n = cloud.X.Length;
        var buffer = new byte[HeaderSize + (long)n * PointSize];
        Magic.CopyTo(buffer, 0);
        BitConverter.TryWriteBytes(buffer.AsSpan(8), n);
        int o = HeaderSize;
        for (int i = 0; i < n; i++)
        {
            BitConverter.TryWriteBytes(buffer.AsSpan(o), cloud.X[i]);
            BitConverter.TryWriteBytes(buffer.AsSpan(o + 4), cloud.Y[i]);
            BitConverter.TryWriteBytes(buffer.AsSpan(o + 8), cloud.Z[i]);
            buffer[o + 12] = cloud.PassIndex[i];
            o += PointSize;
        }
        stream.Write(buffer);
    }

    internal static MergedCloud Read(Stream stream)
    {
        PassFormat.EnsureLittleEndian();
        var header = new byte[HeaderSize];
        if (stream.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) < HeaderSize)
            throw new InvalidDataException("The result cloud file is truncated.");
        if (!header.AsSpan(0, 8).SequenceEqual(Magic))
            throw new InvalidDataException("The file is not a result cloud: bad magic.");
        int n = BitConverter.ToInt32(header, 8);
        if (n < 0) throw new InvalidDataException("The result cloud file has a negative point count.");
        var body = new byte[(long)n * PointSize];
        if (stream.ReadAtLeast(body, body.Length, throwOnEndOfStream: false) < body.Length)
            throw new InvalidDataException("The result cloud file is truncated.");
        var (x, y, z, pass) = (new float[n], new float[n], new float[n], new byte[n]);
        int o = 0;
        for (int i = 0; i < n; i++)
        {
            x[i] = BitConverter.ToSingle(body, o);
            y[i] = BitConverter.ToSingle(body, o + 4);
            z[i] = BitConverter.ToSingle(body, o + 8);
            pass[i] = body[o + 12];
            o += PointSize;
        }
        return new MergedCloud(x, y, z, pass);
    }
}

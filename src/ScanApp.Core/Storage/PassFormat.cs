using System.Text;

namespace ScanApp.Core.Storage;

/// <summary>
/// Raw pass file layout, little-endian. Header: magic "SCNPASS1", int32 version, int32 pointsPerProfile,
/// byte hasReflectance. Then profiles until end of file: int64 sequence, int64 timestampNs,
/// float32[n] X, float32[n] Z, uint16[n] reflectance if flagged. No profile count, so writing never seeks.
/// </summary>
public static class PassFormat
{
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("SCNPASS1");
    public const int Version = 1;
    public const int HeaderSize = 8 + 4 + 4 + 1;

    public static long ProfileSize(int pointsPerProfile, bool hasReflectance) =>
        8 + 8 + pointsPerProfile * (4L + 4 + (hasReflectance ? 2 : 0));

    internal static void EnsureLittleEndian()
    {
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("The pass format is written with little-endian bulk copies.");
    }
}

namespace ScanApp.Core.Model;

/// <summary>
/// One scanner profile. X along the laser line, Z height, both mm. A missing point is
/// <see cref="float.NaN"/> in Z (and X). Reflectance is null when the source does not provide it.
/// All arrays have equal length.
/// </summary>
public sealed record Profile(long Sequence, long TimestampNs, float[] X, float[] Z, ushort[]? Reflectance)
{
    public float[] X { get; } = X;
    public float[] Z { get; } = Z.Length == X.Length
        ? Z
        : throw new ArgumentException($"Z has {Z.Length} points, X has {X.Length}.", nameof(Z));
    public ushort[]? Reflectance { get; } = Reflectance is null || Reflectance.Length == X.Length
        ? Reflectance
        : throw new ArgumentException($"Reflectance has {Reflectance.Length} points, X has {X.Length}.", nameof(Reflectance));

    public int PointCount => X.Length;
}

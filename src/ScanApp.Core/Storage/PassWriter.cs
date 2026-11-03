using System.Runtime.InteropServices;
using ScanApp.Core.Model;

namespace ScanApp.Core.Storage;

/// <summary>Streams profiles to a pass file one at a time. Owns and disposes the stream.</summary>
public sealed class PassWriter : IDisposable
{
    private readonly BinaryWriter _writer;
    private readonly int _pointsPerProfile;
    private readonly bool _hasReflectance;

    public PassWriter(Stream stream, int pointsPerProfile, bool hasReflectance)
    {
        PassFormat.EnsureLittleEndian();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pointsPerProfile);
        _writer = new BinaryWriter(stream);
        _pointsPerProfile = pointsPerProfile;
        _hasReflectance = hasReflectance;
        _writer.Write(PassFormat.Magic);
        _writer.Write(PassFormat.Version);
        _writer.Write(pointsPerProfile);
        _writer.Write(hasReflectance ? (byte)1 : (byte)0);
    }

    public int ProfilesWritten { get; private set; }

    public void Write(Profile profile)
    {
        if (profile.PointCount != _pointsPerProfile)
            throw new ArgumentException($"Profile has {profile.PointCount} points, the pass has {_pointsPerProfile}.", nameof(profile));
        if (profile.Reflectance is null == _hasReflectance)
            throw new ArgumentException(_hasReflectance ? "The pass expects reflectance." : "The pass has no reflectance.", nameof(profile));

        _writer.Write(profile.Sequence);
        _writer.Write(profile.TimestampNs);
        _writer.Write(MemoryMarshal.AsBytes(profile.X.AsSpan()));
        _writer.Write(MemoryMarshal.AsBytes(profile.Z.AsSpan()));
        if (profile.Reflectance is { } reflectance)
            _writer.Write(MemoryMarshal.AsBytes(reflectance.AsSpan()));
        ProfilesWritten++;
    }

    public void Flush() => _writer.Flush();

    public void Dispose() => _writer.Dispose();
}

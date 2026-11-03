using System.Runtime.InteropServices;
using ScanApp.Core.Model;

namespace ScanApp.Core.Storage;

/// <summary>Reads a pass file. Owns and disposes the stream, which must be seekable.</summary>
public sealed class PassReader : IDisposable
{
    private readonly Stream _stream;
    private readonly BinaryReader _reader;
    private readonly long _profileSize;

    public PassReader(Stream stream)
    {
        PassFormat.EnsureLittleEndian();
        _stream = stream;
        _reader = new BinaryReader(stream);
        try
        {
            if (!_reader.ReadBytes(PassFormat.Magic.Length).AsSpan().SequenceEqual(PassFormat.Magic))
                throw new InvalidDataException("Not a pass file (bad magic).");
            int version = _reader.ReadInt32();
            if (version != PassFormat.Version)
                throw new InvalidDataException($"Unsupported pass format version {version}.");
            PointsPerProfile = _reader.ReadInt32();
            if (PointsPerProfile <= 0)
                throw new InvalidDataException($"Invalid points per profile {PointsPerProfile}.");
            HasReflectance = _reader.ReadByte() != 0;
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("Pass file header is truncated.", e);
        }
        _profileSize = PassFormat.ProfileSize(PointsPerProfile, HasReflectance);
    }

    public int PointsPerProfile { get; }
    public bool HasReflectance { get; }

    /// <summary>Whole profiles in the file. A trailing partial profile (an interrupted write) is ignored.</summary>
    public int ProfileCount => (int)((_stream.Length - PassFormat.HeaderSize) / _profileSize);

    public IEnumerable<Profile> ReadAll()
    {
        int count = ProfileCount;
        for (int i = 0; i < count; i++)
            yield return ReadAt(i);
    }

    public Profile ReadAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, ProfileCount);
        _stream.Position = PassFormat.HeaderSize + index * _profileSize;

        long sequence = _reader.ReadInt64();
        long timestampNs = _reader.ReadInt64();
        var x = new float[PointsPerProfile];
        var z = new float[PointsPerProfile];
        _stream.ReadExactly(MemoryMarshal.AsBytes(x.AsSpan()));
        _stream.ReadExactly(MemoryMarshal.AsBytes(z.AsSpan()));
        ushort[]? reflectance = null;
        if (HasReflectance)
        {
            reflectance = new ushort[PointsPerProfile];
            _stream.ReadExactly(MemoryMarshal.AsBytes(reflectance.AsSpan()));
        }
        return new Profile(sequence, timestampNs, x, z, reflectance);
    }

    public void Dispose() => _reader.Dispose();
}

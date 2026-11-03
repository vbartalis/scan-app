using ScanApp.Core.Model;
using ScanApp.Core.Storage;

namespace ScanApp.Core.Tests.Storage;

public class PassFormatTests
{
    private static Profile MakeProfile(long seq, int points, bool reflectance) => new(
        seq,
        1_000_000L * seq,
        Enumerable.Range(0, points).Select(i => i * 0.5f + seq).ToArray(),
        Enumerable.Range(0, points).Select(i => i * 0.25f - seq).ToArray(),
        reflectance ? Enumerable.Range(0, points).Select(i => (ushort)(i + seq)).ToArray() : null);

    private static MemoryStream WriteAll(IEnumerable<Profile> profiles, int points, bool reflectance)
    {
        var ms = new MemoryStream();
        using (var w = new PassWriter(new NonClosingStream(ms), points, reflectance))
            foreach (var p in profiles) w.Write(p);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void RoundTrip_PreservesAllFieldsIncludingNaN()
    {
        var profiles = Enumerable.Range(0, 3).Select(s => MakeProfile(s, 4, true)).ToArray();
        profiles[1].Z[2] = float.NaN;

        using var reader = new PassReader(WriteAll(profiles, 4, true));
        var read = reader.ReadAll().ToArray();

        Assert.Equal(4, reader.PointsPerProfile);
        Assert.True(reader.HasReflectance);
        Assert.Equal(3, read.Length);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(profiles[i].Sequence, read[i].Sequence);
            Assert.Equal(profiles[i].TimestampNs, read[i].TimestampNs);
            Assert.Equal(profiles[i].X, read[i].X);
            Assert.Equal(profiles[i].Z, read[i].Z);
            Assert.Equal(profiles[i].Reflectance, read[i].Reflectance);
        }
        Assert.True(float.IsNaN(read[1].Z[2]));
    }

    [Fact]
    public void RoundTrip_WithoutReflectance_ReturnsNullReflectance()
    {
        using var reader = new PassReader(WriteAll([MakeProfile(0, 4, false)], 4, false));

        Assert.False(reader.HasReflectance);
        Assert.Null(Assert.Single(reader.ReadAll()).Reflectance);
    }

    [Fact]
    public void Writer_RejectsProfileWithWrongPointCount()
    {
        using var w = new PassWriter(new MemoryStream(), 4, false);

        Assert.Throws<ArgumentException>(() => w.Write(MakeProfile(0, 3, false)));
    }

    [Fact]
    public void Writer_RejectsReflectanceMismatch()
    {
        using var withRefl = new PassWriter(new MemoryStream(), 4, true);
        using var withoutRefl = new PassWriter(new MemoryStream(), 4, false);

        Assert.Throws<ArgumentException>(() => withRefl.Write(MakeProfile(0, 4, false)));
        Assert.Throws<ArgumentException>(() => withoutRefl.Write(MakeProfile(0, 4, true)));
    }

    [Fact]
    public void Reader_RejectsBadMagic()
    {
        var ms = new MemoryStream("NOTAPASS\u0001\0\0\0\u0004\0\0\0\0"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => new PassReader(ms));
    }

    [Fact]
    public void ReadAt_ReturnsSameAsSequentialRead()
    {
        var profiles = Enumerable.Range(0, 5).Select(s => MakeProfile(s, 4, true)).ToArray();
        using var reader = new PassReader(WriteAll(profiles, 4, true));

        Assert.Equal(5, reader.ProfileCount);
        var sequential = reader.ReadAll().ToArray()[2];
        var random = reader.ReadAt(2);

        Assert.Equal(sequential.Sequence, random.Sequence);
        Assert.Equal(sequential.X, random.X);
        Assert.Equal(sequential.Z, random.Z);
        Assert.Equal(sequential.Reflectance, random.Reflectance);
    }

    [Fact]
    public void Writer_StreamsWithoutBufferingAllProfiles()
    {
        const int points = 2560;
        string path = Path.Combine(Path.GetTempPath(), $"passformat-{Guid.NewGuid():N}.pass");
        try
        {
            using var w = new PassWriter(new FileStream(path, FileMode.CreateNew), points, true);
            for (int i = 0; i < 1000; i++)
            {
                w.Write(MakeProfile(i, points, true));
                if ((i + 1) % 100 == 0) w.Flush();
                if (i + 1 == 500)
                    Assert.Equal(PassFormat.HeaderSize + 500L * PassFormat.ProfileSize(points, true), new FileInfo(path).Length);
            }
            Assert.Equal(1000, w.ProfilesWritten);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Lets a test read a MemoryStream back after the writer has disposed its stream.</summary>
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

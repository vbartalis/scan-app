using ScanApp.Core.Model;
using ScanApp.Core.Storage;

namespace ScanApp.Core.Tests.Storage;

public sealed class ScanRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"scanrepo-{Guid.NewGuid():N}");

    public ScanRepositoryTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly IReadOnlyDictionary<string, string> Settings =
        new Dictionary<string, string> { ["AcquisitionLineRate"] = "1000" };

    private static Profile MakeProfile(long seq) =>
        new(seq, seq * 1_000_000L, [0f, 1f, 2f], [5f, float.NaN, 7f], [10, 20, 30]);

    private static PassMetadata RecordPass(ScanRepository repo, PassDirection direction, int profiles)
    {
        using var pending = repo.BeginPass(direction, 3, true, "Simulated: test", Settings);
        for (int i = 0; i < profiles; i++) pending.Writer.Write(MakeProfile(i));
        return pending.Commit(PassStatus.Complete, "note");
    }

    [Fact]
    public void Create_WritesScanJsonAndFolders()
    {
        var repo = ScanRepository.Create(_root, "remote");

        Assert.Equal(Path.Combine(_root, "remote"), repo.Directory);
        Assert.True(File.Exists(Path.Combine(repo.Directory, "scan.json")));
        Assert.True(Directory.Exists(Path.Combine(repo.Directory, "passes")));
        Assert.True(Directory.Exists(Path.Combine(repo.Directory, "results")));
        Assert.Equal("remote", repo.Info.Name);
        Assert.Equal(1, repo.Info.FormatVersion);
        Assert.Empty(repo.Passes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("con")]
    [InlineData("LPT1.txt")]
    [InlineData("trailing.")]
    public void Create_RejectsInvalidName(string name)
    {
        Assert.Throws<ArgumentException>(() => ScanRepository.Create(_root, name));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void Create_RejectsNamesWithInvalidFileNameChars()
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            Assert.Throws<ArgumentException>(() => ScanRepository.Create(_root, $"bad{c}name"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void Create_RejectsExistingScan()
    {
        var first = ScanRepository.Create(_root, "remote");
        RecordPass(first, PassDirection.CameraFront, 2);
        string scanJson = Path.Combine(first.Directory, "scan.json");
        string before = File.ReadAllText(scanJson);
        var filesBefore = Directory.GetFiles(first.Directory, "*", SearchOption.AllDirectories).Order().ToArray();

        Assert.Throws<IOException>(() => ScanRepository.Create(_root, "remote"));

        Assert.Equal(before, File.ReadAllText(scanJson));
        Assert.Equal(filesBefore, Directory.GetFiles(first.Directory, "*", SearchOption.AllDirectories).Order().ToArray());
    }

    [Fact]
    public void BeginPass_Commit_ThenOpenFromDisk_ListsPassWithMetadata()
    {
        var created = ScanRepository.Create(_root, "remote");
        var committed = RecordPass(created, PassDirection.CameraBack, 4);

        Assert.Equal(1, committed.Id);
        Assert.Single(created.Passes);

        var repo = ScanRepository.Open(created.Directory);
        var pass = Assert.Single(repo.Passes);
        Assert.Equal(1, pass.Id);
        Assert.Equal(PassDirection.CameraBack, pass.Direction);
        Assert.Equal(4, pass.ProfileCount);
        Assert.Equal(3, pass.PointsPerProfile);
        Assert.Equal(PassStatus.Complete, pass.Status);
        Assert.Equal("Simulated: test", pass.SourceDescription);
        Assert.Equal("1000", pass.ScannerSettings["AcquisitionLineRate"]);
        Assert.Equal("note", pass.Notes);

        using var reader = repo.OpenPass(1);
        var profiles = reader.ReadAll().ToArray();
        Assert.Equal(4, profiles.Length);
        Assert.Equal(MakeProfile(3).Z, profiles[3].Z);
    }

    [Fact]
    public void PassIds_AreSequential_AfterReopen()
    {
        var created = ScanRepository.Create(_root, "remote");
        RecordPass(created, PassDirection.CameraFront, 1);
        RecordPass(created, PassDirection.CameraBack, 1);

        var repo = ScanRepository.Open(created.Directory);
        Assert.Equal([1, 2], repo.Passes.Select(p => p.Id));
        using var next = repo.BeginPass(PassDirection.CameraFront, 3, true, "Simulated: test", Settings);
        Assert.Equal(3, next.Id);
    }

    [Fact]
    public void Discard_LeavesNoFiles()
    {
        var repo = ScanRepository.Create(_root, "remote");
        var pending = repo.BeginPass(PassDirection.CameraFront, 3, true, "Simulated: test", Settings);
        pending.Writer.Write(MakeProfile(0));

        pending.Discard();

        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(repo.Directory, "passes")));
        Assert.Empty(repo.Passes);
    }

    [Fact]
    public void Dispose_WithoutCommit_Discards()
    {
        var repo = ScanRepository.Create(_root, "remote");
        using (var pending = repo.BeginPass(PassDirection.CameraFront, 3, true, "Simulated: test", Settings))
            pending.Writer.Write(MakeProfile(0));

        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(repo.Directory, "passes")));
    }

    [Fact]
    public void Open_IgnoresLeftoverTempPassFiles()
    {
        var created = ScanRepository.Create(_root, "remote");
        RecordPass(created, PassDirection.CameraFront, 2);
        File.WriteAllBytes(Path.Combine(created.Directory, "passes", "pass-002.bin.tmp"), [1, 2, 3, 4, 5]);

        var repo = ScanRepository.Open(created.Directory);

        Assert.Equal(1, Assert.Single(repo.Passes).Id);
        using (var next = repo.BeginPass(PassDirection.CameraFront, 3, true, "Simulated: test", Settings))
            Assert.Equal(3, next.Id);
        using var reader = repo.OpenPass(1);
        Assert.Equal(2, reader.ProfileCount);
    }

    [Fact]
    public void Open_RejectsFolderWithoutScanJson()
    {
        string dir = Path.Combine(_root, "not-a-scan");
        Directory.CreateDirectory(dir);

        Assert.Throws<InvalidDataException>(() => ScanRepository.Open(dir));
    }

    [Fact]
    public void DeletePass_RemovesItsFilesAndListing()
    {
        var created = ScanRepository.Create(_root, "remote");
        RecordPass(created, PassDirection.CameraFront, 2);
        RecordPass(created, PassDirection.CameraBack, 2);

        created.DeletePass(1);

        Assert.Equal([2], created.Passes.Select(p => p.Id));
        Assert.False(File.Exists(Path.Combine(created.Directory, "passes", "pass-001.bin")));
        Assert.False(File.Exists(Path.Combine(created.Directory, "passes", "pass-001.json")));
        Assert.Equal([2], ScanRepository.Open(created.Directory).Passes.Select(p => p.Id));
    }

    [Fact]
    public void DeletePass_NumberIsNeverReused()
    {
        var created = ScanRepository.Create(_root, "remote");
        RecordPass(created, PassDirection.CameraFront, 1);
        RecordPass(created, PassDirection.CameraFront, 1);

        created.DeletePass(2);

        using (var next = created.BeginPass(PassDirection.CameraFront, 3, true, "Simulated: test", Settings))
            Assert.Equal(3, next.Id);
        using (var afterReopen = ScanRepository.Open(created.Directory).BeginPass(PassDirection.CameraFront, 3, true, "Simulated: test", Settings))
            Assert.Equal(3, afterReopen.Id);
    }

    [Fact]
    public void DeletePass_UnknownId_Throws()
    {
        var repo = ScanRepository.Create(_root, "remote");

        Assert.Throws<ArgumentException>(() => repo.DeletePass(7));
    }

    [Fact]
    public void DeleteScan_RemovesTheScanFolder()
    {
        var repo = ScanRepository.Create(_root, "remote");
        RecordPass(repo, PassDirection.CameraFront, 2);

        repo.DeleteScan();

        Assert.False(Directory.Exists(repo.Directory));
    }

    [Fact]
    public void Delete_ByDirectory_RemovesAScanThatCannotBeOpened()
    {
        var repo = ScanRepository.Create(_root, "old");
        File.WriteAllText(Path.Combine(repo.Directory, "passes", "pass-001.json"), "{ \"Direction\": \"Away\" }");
        Assert.Throws<InvalidDataException>(() => ScanRepository.Open(repo.Directory));

        ScanRepository.Delete(repo.Directory);

        Assert.False(Directory.Exists(repo.Directory));
    }

    [Fact]
    public void DeleteScan_RefusesAFolderThatIsNoLongerAScan()
    {
        var repo = ScanRepository.Create(_root, "remote");
        File.Delete(Path.Combine(repo.Directory, "scan.json"));

        Assert.Throws<InvalidDataException>(() => repo.DeleteScan());
        Assert.True(Directory.Exists(repo.Directory));
    }
}

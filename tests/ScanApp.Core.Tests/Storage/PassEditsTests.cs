using ScanApp.Core.Cleanup;
using ScanApp.Core.Model;
using ScanApp.Core.Storage;

namespace ScanApp.Core.Tests.Storage;

public sealed class PassEditsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"passedits-{Guid.NewGuid():N}");

    public PassEditsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly IReadOnlyDictionary<string, string> Settings =
        new Dictionary<string, string> { ["AcquisitionLineRate"] = "1000" };

    private static void RecordPass(ScanRepository repo)
    {
        using var pending = repo.BeginPass(PassDirection.CameraFront, 3, true, "Simulated: test", Settings);
        pending.Writer.Write(new Profile(0, 0, [0f, 1f, 2f], [5f, float.NaN, 7f], [10, 20, 30]));
        pending.Commit(PassStatus.Complete);
    }

    private ScanRepository NewScanWithPass()
    {
        var repo = ScanRepository.Create(_root, "remote");
        RecordPass(repo);
        return repo;
    }

    private static string EditsPath(ScanRepository repo, int id) =>
        Path.Combine(repo.Directory, "passes", $"pass-{id:000}.edit.json");

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var repo = NewScanWithPass();

        repo.SaveEdits(1, new PassEdits(120, 9000));

        Assert.Equal(new PassEdits(120, 9000), repo.LoadEdits(1));
        Assert.Equal(new PassEdits(120, 9000), ScanRepository.Open(repo.Directory).LoadEdits(1));
        Assert.False(File.Exists(EditsPath(repo, 1) + ".tmp"));
    }

    [Fact]
    public void LoadEdits_NoFile_IsNone()
    {
        var repo = NewScanWithPass();

        Assert.Equal(PassEdits.None, repo.LoadEdits(1));
    }

    [Fact]
    public void SaveEdits_None_DeletesTheFile()
    {
        var repo = NewScanWithPass();
        repo.SaveEdits(1, new PassEdits(120, null));
        Assert.True(File.Exists(EditsPath(repo, 1)));

        repo.SaveEdits(1, PassEdits.None);

        Assert.False(File.Exists(EditsPath(repo, 1)));
        Assert.Equal(PassEdits.None, repo.LoadEdits(1));
    }

    [Fact]
    public void LoadEdits_Corrupt_Throws()
    {
        var repo = NewScanWithPass();
        File.WriteAllText(EditsPath(repo, 1), "{ not json");

        Assert.Throws<InvalidDataException>(() => repo.LoadEdits(1));
    }

    [Fact]
    public void DeletePass_RemovesEditsFile()
    {
        var repo = NewScanWithPass();
        repo.SaveEdits(1, new PassEdits(1, 2));

        repo.DeletePass(1);

        Assert.False(File.Exists(EditsPath(repo, 1)));
        using var next = repo.BeginPass(PassDirection.CameraFront, 3, true, "Simulated: test", Settings);
        Assert.Equal(2, next.Id);
    }

    [Fact]
    public void Open_IgnoresEditsFiles()
    {
        var repo = NewScanWithPass();
        RecordPass(repo);
        repo.SaveEdits(1, new PassEdits(1, 2));
        File.WriteAllText(EditsPath(repo, 2) + ".tmp", "{}");

        var reopened = ScanRepository.Open(repo.Directory);

        Assert.Equal([1, 2], reopened.Passes.Select(p => p.Id));
    }

    [Fact]
    public void SaveEdits_UnknownPass_Throws()
    {
        var repo = NewScanWithPass();

        Assert.Throws<ArgumentException>(() => repo.SaveEdits(7, new PassEdits(1, 2)));
        Assert.False(File.Exists(EditsPath(repo, 7)));
    }

    [Fact]
    public void LoadEdits_OldFile_HasNoZone()
    {
        var repo = NewScanWithPass();
        File.WriteAllText(EditsPath(repo, 1), """{"TrimStart":5,"TrimEnd":90}""");

        var edits = repo.LoadEdits(1);

        Assert.Equal(new PassEdits(5, 90), edits);
        Assert.Equal(PassZone.None, edits.Zone);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsZone()
    {
        var repo = NewScanWithPass();
        var edits = new PassEdits(120, null).WithZone(new PassZone(-40.5, 60, 1, null));

        repo.SaveEdits(1, edits);

        var loaded = ScanRepository.Open(repo.Directory).LoadEdits(1);
        Assert.Equal(edits, loaded);
        Assert.Equal(new PassZone(-40.5, 60, 1, null), loaded.Zone);
        string json = File.ReadAllText(EditsPath(repo, 1));
        Assert.DoesNotContain("Zone", json);
        Assert.Contains("HeightMax", json);
    }

    [Fact]
    public void WithoutTrim_KeepsTheZone()
    {
        var zone = new PassZone(null, 50, null, 30);
        var edits = new PassEdits(10, 20).WithZone(zone);

        var auto = edits.WithoutTrim();

        Assert.Null(auto.TrimStart);
        Assert.Null(auto.TrimEnd);
        Assert.Equal(zone, auto.Zone);
        Assert.Equal(new PassEdits(10, 20), edits.WithZone(PassZone.None));
    }

    [Fact]
    public void LoadEdits_OldFiles_FilterOn()
    {
        var repo = NewScanWithPass();
        File.WriteAllText(EditsPath(repo, 1), """{"TrimStart":5,"TrimEnd":90}""");
        var milestone3 = repo.LoadEdits(1);
        File.WriteAllText(EditsPath(repo, 1), """{"TrimStart":5,"TrimEnd":90,"XMin":-10,"XMax":null,"HeightMin":1,"HeightMax":null}""");
        var milestone5a = repo.LoadEdits(1);

        Assert.All(new[] { milestone3, milestone5a }, e =>
        {
            Assert.Null(e.ReflectionFilter);
            Assert.True(e.FiltersReflections);
        });
    }

    [Fact]
    public void SaveEdits_FilterOff_RoundTrips()
    {
        var repo = NewScanWithPass();
        var edits = new PassEdits(120, null, ReflectionFilter: false);

        repo.SaveEdits(1, edits);

        var loaded = ScanRepository.Open(repo.Directory).LoadEdits(1);
        Assert.Equal(edits, loaded);
        Assert.False(loaded.ReflectionFilter);
        Assert.False(loaded.FiltersReflections);
        Assert.Contains("ReflectionFilter", File.ReadAllText(EditsPath(repo, 1)));
        Assert.True((loaded with { ReflectionFilter = true }).FiltersReflections);
    }

    [Theory]
    [InlineData(null, null, null, null, true)]
    [InlineData(-10.0, 10.0, 1.0, 20.0, true)]
    [InlineData(10.0, -10.0, null, null, false)]
    [InlineData(5.0, 5.0, null, null, false)]
    [InlineData(null, null, 20.0, 1.0, false)]
    [InlineData(null, 10.0, 20.0, null, true)]
    public void PassZone_IsValid_RejectsCrossedLimits(double? xMin, double? xMax, double? hMin, double? hMax, bool valid)
    {
        Assert.Equal(valid, new PassZone(xMin, xMax, hMin, hMax).IsValid);
    }
}

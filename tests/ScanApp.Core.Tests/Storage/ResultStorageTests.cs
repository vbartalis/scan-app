using ScanApp.Core.Combine;
using ScanApp.Core.Model;
using ScanApp.Core.Storage;

namespace ScanApp.Core.Tests.Storage;

public sealed class ResultStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"resultstore-{Guid.NewGuid():N}");

    public ResultStorageTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly IReadOnlyDictionary<string, string> Settings = new Dictionary<string, string>();

    private static int RecordPass(ScanRepository repo)
    {
        using var pending = repo.BeginPass(PassDirection.CameraFront, 2, false, "test", Settings);
        pending.Writer.Write(new Profile(0, 0, [0f, 1f], [1f, 2f], null));
        return pending.Commit(PassStatus.Complete).Id;
    }

    private static CombineResult Make(int id, params int[] passIds) =>
        new(id, new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.FromHours(1)), passIds[0],
            passIds.Select(p => new PassOutcome(p, new PassPose(0.25, 1.5, -2.5),
                new SpeedProfile([0, 1, 2], [0, 10, 21]), 10.5, 0.1, 0.003)).ToList(),
            UncertainAlignment: true, ScaleUnknown: false);

    private static MergedCloud Cloud(byte max = 0) =>
        new([1f, 2f, 3f], [4f, 5f, 6f], [7f, float.NaN, 9f], [0, max, max]);

    [Fact]
    public void SaveThenReopen_RoundTripsResultAndCloud()
    {
        var repo = ScanRepository.Create(_root, "scan");
        int p1 = RecordPass(repo), p2 = RecordPass(repo);

        var saved = repo.SaveResult(id => Make(id, p1, p2), Cloud(1));
        Assert.Equal(1, saved.Id);
        Assert.Single(repo.Results);

        var reopened = ScanRepository.Open(repo.Directory);
        var result = Assert.Single(reopened.Results);
        Assert.Equal(saved.Id, result.Id);
        Assert.Equal(saved.CreatedAt, result.CreatedAt);
        Assert.Equal(p1, result.ReferencePassId);
        Assert.True(result.UncertainAlignment);
        Assert.False(result.ScaleUnknown);
        Assert.Equal([p1, p2], result.Passes.Select(p => p.PassId));
        var o = result.Passes[1];
        Assert.Equal(new PassPose(0.25, 1.5, -2.5), o.Pose);
        Assert.Equal([0, 1, 2], o.Speed.TimeS);
        Assert.Equal([0, 10, 21], o.Speed.TravelMm);
        Assert.Equal(10.5, o.MeanSpeedMmPerS);
        Assert.Equal(0.1, o.SpeedRangeFraction);
        Assert.Equal(0.003, o.OverlapMadMm);

        var cloud = reopened.OpenResultCloud(1);
        Assert.Equal([1f, 2f, 3f], cloud.X);
        Assert.Equal([4f, 5f, 6f], cloud.Y);
        Assert.True(float.IsNaN(cloud.Z[1]));
        Assert.Equal(7f, cloud.Z[0]);
        Assert.Equal([0, 1, 1], cloud.PassIndex);
        Assert.False(Directory.Exists(Path.Combine(repo.Directory, "results", "result-001.tmp")));
    }

    [Fact]
    public void SaveResult_RejectsMismatchedCloudLengths()
    {
        var repo = ScanRepository.Create(_root, "scan");
        int p1 = RecordPass(repo);
        var bad = new MergedCloud([1f, 2f], [1f], [1f, 2f], [0, 0]);

        Assert.Throws<ArgumentException>(() => repo.SaveResult(id => Make(id, p1), bad));
        Assert.Empty(repo.Results);
    }

    [Fact]
    public void ResultIds_NeverReused_AfterDelete()
    {
        var repo = ScanRepository.Create(_root, "scan");
        int p1 = RecordPass(repo);
        repo.SaveResult(id => Make(id, p1), Cloud());
        repo.SaveResult(id => Make(id, p1), Cloud());

        repo.DeleteResult(2);
        Assert.Equal([1], repo.Results.Select(r => r.Id));
        Assert.True(File.Exists(Path.Combine(repo.Directory, "results", "result-002.deleted")));
        Assert.False(Directory.Exists(Path.Combine(repo.Directory, "results", "result-002")));

        Assert.Equal(3, repo.SaveResult(id => Make(id, p1), Cloud()).Id);
        var reopened = ScanRepository.Open(repo.Directory);
        Assert.Equal(4, reopened.SaveResult(id => Make(id, p1), Cloud()).Id);
        Assert.Throws<ArgumentException>(() => reopened.DeleteResult(2));
        Assert.Throws<ArgumentException>(() => reopened.OpenResultCloud(2));
    }

    [Fact]
    public void Result_OpensAfterItsPassIsDeleted()
    {
        var repo = ScanRepository.Create(_root, "scan");
        int p1 = RecordPass(repo), p2 = RecordPass(repo);
        repo.SaveResult(id => Make(id, p1, p2), Cloud(1));

        repo.DeletePass(p2);

        var reopened = ScanRepository.Open(repo.Directory);
        var result = Assert.Single(reopened.Results);
        Assert.Contains(result.Passes, p => p.PassId == p2);
        Assert.Equal(3, reopened.OpenResultCloud(1).X.Length);
    }

    [Fact]
    public void SaveResult_FailedWrite_LeavesNoTmp()
    {
        var repo = ScanRepository.Create(_root, "scan");
        int p1 = RecordPass(repo);
        // JSON has no NaN, so writing result.json fails after the .tmp folder exists.
        CombineResult Bad(int id) => Make(id, p1) with
        {
            Passes = [Make(id, p1).Passes[0] with { OverlapMadMm = double.NaN }],
        };

        Assert.ThrowsAny<Exception>(() => repo.SaveResult(Bad, Cloud()));
        Assert.Empty(repo.Results);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(repo.Directory, "results")));
    }

    [Fact]
    public void SaveResult_FailedAfterFilesWritten_LeavesNoTmp()
    {
        var repo = ScanRepository.Create(_root, "scan");
        int p1 = RecordPass(repo);
        string blocker = "";
        // A file where the folder must go makes the final rename fail once result.json and points.bin exist.
        CombineResult Blocked(int id)
        {
            blocker = Path.Combine(repo.Directory, "results", $"result-{id:000}");
            File.WriteAllBytes(blocker, []);
            return Make(id, p1);
        }

        Assert.ThrowsAny<IOException>(() => repo.SaveResult(Blocked, Cloud()));
        Assert.Empty(repo.Results);
        Assert.Equal([blocker], Directory.EnumerateFileSystemEntries(Path.Combine(repo.Directory, "results")));
    }

    [Fact]
    public void Open_IgnoresLeftoverTmpResultFolder()
    {
        var repo = ScanRepository.Create(_root, "scan");
        int p1 = RecordPass(repo);
        repo.SaveResult(id => Make(id, p1), Cloud());
        string tmp = Path.Combine(repo.Directory, "results", "result-002.tmp");
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "result.json"), "{ half written");

        var reopened = ScanRepository.Open(repo.Directory);

        Assert.Equal([1], reopened.Results.Select(r => r.Id));
        Assert.Equal(3, reopened.SaveResult(id => Make(id, p1), Cloud()).Id);
        Assert.Equal([1, 3], reopened.Results.Select(r => r.Id));
    }

    [Fact]
    public void OldResultJson_LoadsWithNewFieldsEmpty()
    {
        var repo = ScanRepository.Create(_root, "scan");
        string folder = Path.Combine(repo.Directory, "results", "result-001");
        Directory.CreateDirectory(folder);
        // A milestone-4 result.json, written before the table check existed.
        File.WriteAllText(Path.Combine(folder, "result.json"), """
            {
              "Id": 1,
              "CreatedAt": "2026-10-31T05:33:23.4002712+02:00",
              "ReferencePassId": 1,
              "Passes": [
                {
                  "PassId": 1,
                  "Pose": { "ThetaRad": 0, "Tx": 0, "Ty": 0 },
                  "Speed": { "TimeS": [ 0, 1 ], "TravelMm": [ 0, 34.5 ] },
                  "MeanSpeedMmPerS": 34.5,
                  "SpeedRangeFraction": 0.25,
                  "OverlapMadMm": 0.09
                },
                {
                  "PassId": 4,
                  "Pose": { "ThetaRad": 5.5, "Tx": 12.5, "Ty": -3 },
                  "Speed": { "TimeS": [ 0, 1 ], "TravelMm": [ 0, 29 ] },
                  "MeanSpeedMmPerS": 29,
                  "SpeedRangeFraction": 0.2,
                  "OverlapMadMm": 0.08
                }
              ],
              "UncertainAlignment": false,
              "ScaleUnknown": false
            }
            """);

        var result = Assert.Single(ScanRepository.Open(repo.Directory).Results);

        Assert.Equal([1, 4], result.Passes.Select(p => p.PassId));
        Assert.Equal(new PassPose(5.5, 12.5, -3), result.Passes[1].Pose);
        Assert.All(result.Passes, p => Assert.False(p.TableUncertain));
        Assert.All(result.Passes, p => Assert.Null(p.ReflectionsRemoved));
        Assert.All(result.Passes, p => Assert.False(p.ReflectionFilterOff));
        Assert.Null(result.OpenCycles);
    }

    [Fact]
    public void DeleteScan_RemovesResultsToo()
    {
        var repo = ScanRepository.Create(_root, "scan");
        int p1 = RecordPass(repo);
        repo.SaveResult(id => Make(id, p1), Cloud());

        repo.DeleteScan();

        Assert.False(Directory.Exists(repo.Directory));
    }

    [Fact]
    public void Open_IgnoresHalfDeletedResultFolders()
    {
        var repo = ScanRepository.Create(_root, "scan");
        int p1 = RecordPass(repo);
        repo.SaveResult(id => Make(id, p1), Cloud());
        repo.SaveResult(id => Make(id, p1), Cloud());
        repo.SaveResult(id => Make(id, p1), Cloud());
        string results = Path.Combine(repo.Directory, "results");
        // Stopped after the marker: the folder is still whole.
        File.WriteAllBytes(Path.Combine(results, "result-001.deleted"), []);
        // Stopped after result.json went: only points.bin is left.
        File.Delete(Path.Combine(results, "result-002", "result.json"));

        var reopened = ScanRepository.Open(repo.Directory);

        Assert.Equal([3], reopened.Results.Select(r => r.Id));
        Assert.Equal(4, reopened.SaveResult(id => Make(id, p1), Cloud()).Id);
    }

    [Fact]
    public void DeleteResult_InterruptedFolderDelete_LeavesNoResult()
    {
        var repo = ScanRepository.Create(_root, "scan");
        int p1 = RecordPass(repo);
        repo.SaveResult(id => Make(id, p1), Cloud());

        // An open points.bin stops the folder delete on Windows.
        using (new FileStream(Path.Combine(repo.Directory, "results", "result-001", "points.bin"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => repo.DeleteResult(1));

        Assert.Empty(repo.Results);
        Assert.Empty(ScanRepository.Open(repo.Directory).Results);
    }

    [Fact]
    public void DeleteScan_ClearsResults()
    {
        var repo = ScanRepository.Create(_root, "scan");
        int p1 = RecordPass(repo);
        repo.SaveResult(id => Make(id, p1), Cloud());

        repo.DeleteScan();

        Assert.Empty(repo.Results);
    }

    [Fact]
    public void Results_IsASnapshot()
    {
        var repo = ScanRepository.Create(_root, "scan");
        int p1 = RecordPass(repo);
        var before = repo.Results;

        repo.SaveResult(id => Make(id, p1), Cloud());

        Assert.Empty(before);
        Assert.Single(repo.Results);
    }
}

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ScanApp.Core.Acquisition;
using ScanApp.Core.Model;
using ScanApp.Core.Storage;

namespace ScanApp.Core.Tests.Acquisition;

public sealed class PassRecorderTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"recorder-{Guid.NewGuid():N}");
    private readonly ScanRepository _repo;
    private readonly FakeProfileSource _source = new();

    public PassRecorderTests()
    {
        Directory.CreateDirectory(_root);
        _repo = ScanRepository.Create(_root, "scan");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Profile MakeProfile(long seq) => new(seq, seq * 1_000_000L, [0f, 1f], [2f, 3f], null);

    /// <summary>Starts recording and returns a task that completes once <paramref name="count"/> profiles were received.</summary>
    private (PassRecorder recorder, Task received) StartAndExpect(int count)
    {
        var recorder = new PassRecorder(_repo, _source);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int seen = 0;
        recorder.ProfileReceived += _ => { if (Interlocked.Increment(ref seen) == count) done.SetResult(); };
        recorder.Start(PassDirection.CameraFront);
        return (recorder, done.Task);
    }

    [Fact]
    public async Task Recorder_RecordsProfilesUntilStop_CommitsCompletePass()
    {
        var (recorder, received) = StartAndExpect(3);
        for (int i = 0; i < 3; i++) _source.Push(MakeProfile(i));
        await received.WaitAsync(Timeout);

        var result = await recorder.StopAsync().WaitAsync(Timeout);

        Assert.Null(result.Error);
        Assert.NotNull(result.Pass);
        Assert.Equal(PassStatus.Complete, result.Pass.Status);
        Assert.Equal(3, result.Pass.ProfileCount);
        Assert.Equal(PassDirection.CameraFront, result.Pass.Direction);
        Assert.False(recorder.IsRecording);
        using var reader = _repo.OpenPass(result.Pass.Id);
        Assert.Equal([0L, 1L, 2L], reader.ReadAll().Select(p => p.Sequence));
    }

    [Fact]
    public async Task Recorder_RaisesProfileReceivedForEachProfile()
    {
        var recorder = new PassRecorder(_repo, _source);
        var sequences = new List<long>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.ProfileReceived += p => { lock (sequences) { sequences.Add(p.Sequence); if (sequences.Count == 4) done.SetResult(); } };
        recorder.Start(PassDirection.CameraBack);
        for (int i = 0; i < 4; i++) _source.Push(MakeProfile(i));

        await done.Task.WaitAsync(Timeout);
        await recorder.StopAsync().WaitAsync(Timeout);

        Assert.Equal([0L, 1L, 2L, 3L], sequences);
    }

    [Fact]
    public async Task Recorder_NoProfiles_ReturnsNullAndWritesNothing()
    {
        var recorder = new PassRecorder(_repo, _source);
        recorder.Start(PassDirection.CameraFront);

        var result = await recorder.StopAsync().WaitAsync(Timeout);

        Assert.Null(result.Pass);
        Assert.Null(result.Error);
        Assert.Empty(_repo.Passes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_repo.Directory, "passes")));
    }

    [Fact]
    public async Task Recorder_SourceFaults_KeepsPartialPassMarkedInterrupted()
    {
        var recorder = new PassRecorder(_repo, _source);
        recorder.Start(PassDirection.CameraFront);
        for (int i = 0; i < 5; i++) _source.Push(MakeProfile(i));
        _source.Fault(new IOException("cable pulled"));

        var result = await recorder.Completion.WaitAsync(Timeout);

        Assert.NotNull(result.Pass);
        Assert.Equal(PassStatus.Interrupted, result.Pass.Status);
        Assert.Equal(5, result.Pass.ProfileCount);
        Assert.IsType<IOException>(result.Error);
        Assert.False(recorder.IsRecording);
        Assert.Same(result, await recorder.StopAsync().WaitAsync(Timeout));
    }

    [Fact]
    public async Task Recorder_DiscardFailure_StillCompletesWithNothingSaved()
    {
        var recorder = new PassRecorder(_repo, _source);
        recorder.Start(PassDirection.CameraFront);
        string tmp = Path.Combine(_repo.Directory, "passes", "pass-001.bin.tmp");
        // Another handle without delete sharing makes File.Delete fail, as antivirus or an indexer can.
        using (new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var result = await recorder.StopAsync().WaitAsync(Timeout);

            Assert.Null(result.Pass);
            Assert.False(recorder.IsRecording);
        }
    }

    [Fact]
    public async Task Recorder_StartWhileRecording_Throws()
    {
        var recorder = new PassRecorder(_repo, _source);
        recorder.Start(PassDirection.CameraFront);

        Assert.Throws<InvalidOperationException>(() => recorder.Start(PassDirection.CameraBack));

        await recorder.StopAsync().WaitAsync(Timeout);
    }

    private sealed class FakeProfileSource : IProfileSource
    {
        private readonly Channel<Profile> _channel = Channel.CreateUnbounded<Profile>();

        public string Description => "Fake";
        public int PointsPerProfile => 2;
        public bool HasReflectance => false;
        public IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>();

        public void Push(Profile profile) => _channel.Writer.TryWrite(profile);
        public void Fault(Exception error) => _channel.Writer.TryComplete(error);

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

        public async IAsyncEnumerable<Profile> StreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var profile in _channel.Reader.ReadAllAsync(ct))
                yield return profile;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

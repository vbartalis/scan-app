using ScanApp.Core.Model;
using ScanApp.Core.Storage;

namespace ScanApp.Core.Acquisition;

/// <summary>The outcome of one recording. <see cref="Pass"/> is null when nothing was saved.</summary>
public sealed record RecordingResult(PassMetadata? Pass, Exception? Error);

/// <summary>Streams profiles from a source into a new pass of a scan, one recording at a time.</summary>
public sealed class PassRecorder
{
    private const int FlushEvery = 100;

    private readonly ScanRepository _repository;
    private readonly IProfileSource _source;
    private CancellationTokenSource? _stop;
    private volatile bool _isRecording;

    public PassRecorder(ScanRepository repository, IProfileSource source)
    {
        _repository = repository;
        _source = source;
    }

    /// <summary>Raised on the acquisition thread; the UI must marshal and throttle.</summary>
    public event Action<Profile>? ProfileReceived;

    public bool IsRecording => _isRecording;

    /// <summary>Completes when the current (or last) recording ends, by Stop or by a source fault.</summary>
    public Task<RecordingResult> Completion { get; private set; } = Task.FromResult(new RecordingResult(null, null));

    public void Start(PassDirection direction)
    {
        if (_isRecording)
            throw new InvalidOperationException("A pass is already being recorded.");

        var pending = _repository.BeginPass(direction, _source.PointsPerProfile, _source.HasReflectance,
                                            _source.Description, _source.Settings);
        _stop = new CancellationTokenSource();
        _isRecording = true;
        var token = _stop.Token;
        Completion = Task.Run(() => RecordAsync(pending, token));
    }

    public async Task<RecordingResult> StopAsync()
    {
        _stop?.Cancel();
        return await Completion.ConfigureAwait(false);
    }

    private async Task<RecordingResult> RecordAsync(PendingPass pending, CancellationToken ct)
    {
        Exception? error = null;
        try
        {
            await foreach (var profile in _source.StreamAsync(ct).WithCancellation(ct).ConfigureAwait(false))
            {
                pending.Writer.Write(profile);
                if (pending.Writer.ProfilesWritten % FlushEvery == 0)
                    pending.Writer.Flush();
                ProfileReceived?.Invoke(profile);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stop was requested: a normal end.
        }
        catch (Exception e)
        {
            error = e;
        }

        try
        {
            if (pending.Writer.ProfilesWritten == 0)
            {
                pending.Discard();
                return new RecordingResult(null, error);
            }
            var pass = pending.Commit(error is null ? PassStatus.Complete : PassStatus.Interrupted);
            return new RecordingResult(pass, error);
        }
        catch (Exception e)
        {
            pending.Discard();
            return new RecordingResult(null, error is null ? e : new AggregateException(error, e));
        }
        finally
        {
            _isRecording = false;
        }
    }
}

namespace ScanApp.App.Views;

/// <summary>
/// Keeps only the newest posted value and applies it at most once per interval. <see cref="Post"/> may be called
/// from any thread; <see cref="Tick"/> runs on the UI thread, driven by a timer.
/// </summary>
public sealed class LatestValueThrottle<T>
{
    private readonly TimeSpan _interval;
    private readonly Action<T> _apply;
    private readonly Func<TimeSpan> _clock;
    private readonly Lock _lock = new();
    private T? _latest;
    private bool _pending;
    private TimeSpan? _lastApplied;

    public LatestValueThrottle(TimeSpan interval, Action<T> apply, Func<TimeSpan> clock)
    {
        _interval = interval;
        _apply = apply;
        _clock = clock;
    }

    public void Post(T value)
    {
        lock (_lock)
        {
            _latest = value;
            _pending = true;
        }
    }

    public void Tick()
    {
        T value;
        lock (_lock)
        {
            var now = _clock();
            if (!_pending || (_lastApplied is { } last && now - last < _interval))
                return;
            value = _latest!;
            _latest = default;
            _pending = false;
            _lastApplied = now;
        }
        _apply(value);
    }
}

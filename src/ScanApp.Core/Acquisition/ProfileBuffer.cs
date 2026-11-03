using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ScanApp.Core.Model;

namespace ScanApp.Core.Acquisition;

/// <summary>
/// Hands profiles from a device thread to one <see cref="StreamAsync"/> reader at a time. Profiles published with no
/// reader are dropped. The buffer is bounded: a reader that falls behind gets an <see cref="IOException"/> instead of
/// unbounded memory growth. A terminal fault is kept, so a reader that arrives later still sees it.
/// </summary>
public sealed class ProfileBuffer
{
    private readonly int _capacity;
    private readonly Lock _lock = new();
    private Channel<Profile>? _reader;
    private Exception? _terminal;

    public ProfileBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    /// <summary>Called by the device thread for every profile.</summary>
    public void Publish(Profile profile)
    {
        Channel<Profile>? reader;
        lock (_lock) reader = _reader;
        if (reader is not null && !reader.Writer.TryWrite(profile))
            reader.Writer.TryComplete(new IOException(
                $"Profiles arrived faster than they could be saved (more than {_capacity} waiting); recording stopped."));
    }

    /// <summary>The source has failed for good: the current and any later reader get <paramref name="error"/>.</summary>
    public void Fail(Exception error)
    {
        Channel<Profile>? reader;
        lock (_lock)
        {
            _terminal ??= error;
            reader = _reader;
        }
        reader?.Writer.TryComplete(_terminal);
    }

    /// <summary>The source has ended normally; no more profiles will come.</summary>
    public void Finish() => Fail(new IOException("The scanner connection has ended."));

    public async IAsyncEnumerable<Profile> StreamAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<Profile>(new BoundedChannelOptions(_capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        lock (_lock)
        {
            if (_terminal is not null) throw _terminal;
            if (_reader is not null) throw new InvalidOperationException("Already streaming.");
            _reader = channel;
        }
        try
        {
            await foreach (var profile in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return profile;
        }
        finally
        {
            lock (_lock)
                if (_reader == channel) _reader = null;
        }
    }
}

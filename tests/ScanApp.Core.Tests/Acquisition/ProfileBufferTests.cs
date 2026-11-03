using ScanApp.Core.Acquisition;
using ScanApp.Core.Model;

namespace ScanApp.Core.Tests.Acquisition;

public class ProfileBufferTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static Profile MakeProfile(long seq) => new(seq, seq, [0f], [0f], null);

    private static async Task<(List<long> sequences, Exception? error)> DrainAsync(IAsyncEnumerable<Profile> stream, int stopAfter = int.MaxValue)
    {
        var sequences = new List<long>();
        try
        {
            await foreach (var p in stream)
            {
                sequences.Add(p.Sequence);
                if (sequences.Count == stopAfter) break;
            }
            return (sequences, null);
        }
        catch (Exception e)
        {
            return (sequences, e);
        }
    }

    [Fact]
    public async Task Publish_WithReader_DeliversInOrder()
    {
        var buffer = new ProfileBuffer(capacity: 10);
        var reading = DrainAsync(buffer.StreamAsync(CancellationToken.None), stopAfter: 3);
        await Task.Delay(20);

        for (int i = 0; i < 3; i++) buffer.Publish(MakeProfile(i));

        var (sequences, error) = await reading.WaitAsync(Timeout);
        Assert.Null(error);
        Assert.Equal([0L, 1L, 2L], sequences);
    }

    [Fact]
    public async Task Publish_WithoutReader_IsDropped()
    {
        var buffer = new ProfileBuffer(capacity: 10);
        buffer.Publish(MakeProfile(0));

        var reading = DrainAsync(buffer.StreamAsync(CancellationToken.None), stopAfter: 1);
        await Task.Delay(20);
        buffer.Publish(MakeProfile(1));

        Assert.Equal([1L], (await reading.WaitAsync(Timeout)).sequences);
    }

    [Fact]
    public async Task Fail_BeforeStreaming_IsThrownByLaterStream()
    {
        var buffer = new ProfileBuffer(capacity: 10);
        buffer.Fail(new IOException("scanner aborted"));

        var (_, error) = await DrainAsync(buffer.StreamAsync(CancellationToken.None)).WaitAsync(Timeout);

        Assert.IsType<IOException>(error);
        Assert.Equal("scanner aborted", error.Message);
    }

    [Fact]
    public async Task Fail_WhileStreaming_DeliversBufferedThenThrows()
    {
        var buffer = new ProfileBuffer(capacity: 10);
        var reading = DrainAsync(buffer.StreamAsync(CancellationToken.None));
        await Task.Delay(20);

        buffer.Publish(MakeProfile(0));
        buffer.Fail(new IOException("scanner aborted"));

        var (sequences, error) = await reading.WaitAsync(Timeout);
        Assert.Equal([0L], sequences);
        Assert.IsType<IOException>(error);
    }

    [Fact]
    public async Task Finish_EndsLaterStreamWithError()
    {
        var buffer = new ProfileBuffer(capacity: 10);
        buffer.Finish();

        var (_, error) = await DrainAsync(buffer.StreamAsync(CancellationToken.None)).WaitAsync(Timeout);

        Assert.IsType<IOException>(error);
    }

    [Fact]
    public async Task Overflow_StopsStreamWithErrorInsteadOfGrowing()
    {
        var buffer = new ProfileBuffer(capacity: 3);
        var gate = new TaskCompletionSource();
        var sequences = new List<long>();
        Exception? error = null;
        var reading = Task.Run(async () =>
        {
            try
            {
                await foreach (var p in buffer.StreamAsync(CancellationToken.None))
                {
                    await gate.Task;              // a reader slower than the producer
                    sequences.Add(p.Sequence);
                }
            }
            catch (Exception e) { error = e; }
        });
        await Task.Delay(50);

        for (int i = 0; i < 10; i++) buffer.Publish(MakeProfile(i));
        gate.SetResult();
        await reading.WaitAsync(Timeout);

        Assert.IsType<IOException>(error);
        Assert.Contains("faster than they could be saved", error!.Message);
        Assert.True(sequences.Count <= 4, $"read {sequences.Count} profiles from a buffer of 3");
        Assert.Equal(sequences.Count, sequences.Distinct().Count());
    }
}

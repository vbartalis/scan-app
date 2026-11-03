using ScanApp.Core.Model;

namespace ScanApp.Core.Acquisition;

/// <summary>Any scanner, real or simulated.</summary>
public interface IProfileSource : IAsyncDisposable
{
    string Description { get; }
    int PointsPerProfile { get; }      // valid after ConnectAsync
    bool HasReflectance { get; }
    IReadOnlyDictionary<string, string> Settings { get; }
    Task ConnectAsync(CancellationToken ct);

    /// <summary>Yields profiles until <paramref name="ct"/> is cancelled. Throws (e.g. IOException) if the device is lost.</summary>
    IAsyncEnumerable<Profile> StreamAsync(CancellationToken ct);
}

namespace ScanApp.Core.Model;

public enum PassStatus { Complete, Interrupted }

/// <summary>Per-pass metadata, stored as JSON beside the pass's raw profiles.</summary>
public sealed record PassMetadata(
    int Id,                                   // 1-based, unique within a scan
    PassDirection Direction,
    DateTimeOffset RecordedAt,
    int ProfileCount,
    int PointsPerProfile,
    PassStatus Status,
    string SourceDescription,                 // e.g. "Ruler3000 SN 1234" or "Simulated: plate"
    IReadOnlyDictionary<string, string> ScannerSettings,
    string Notes);

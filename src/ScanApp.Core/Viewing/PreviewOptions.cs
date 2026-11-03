namespace ScanApp.Core.Viewing;

/// <summary>Size caps for a pass preview.</summary>
public sealed record PreviewOptions(int GridColumns = 1000, int MaxGridRows = 2000, int MaxPoints = 2_000_000);

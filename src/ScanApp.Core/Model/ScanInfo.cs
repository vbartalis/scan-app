namespace ScanApp.Core.Model;

/// <summary>The contents of a scan folder's scan.json.</summary>
public sealed record ScanInfo(int FormatVersion, string Name, DateTimeOffset CreatedAt);

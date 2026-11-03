namespace ScanApp.Core.Cleanup;

/// <summary>
/// A pass's zone of interest: an X range in scanner mm along the laser line and a height band in mm above the pass's
/// table. Null = no limit; the limits themselves are inside. Derived like the trims; the raw pass is never touched.
/// </summary>
public sealed record PassZone(double? XMin, double? XMax, double? HeightMin, double? HeightMax)
{
    public static readonly PassZone None = new(null, null, null, null);

    /// <summary>True when a height limit is set; the band needs the pass's table.</summary>
    public bool HasBand => HeightMin is not null || HeightMax is not null;

    /// <summary>True when an X limit is set.</summary>
    public bool HasXRange => XMin is not null || XMax is not null;

    /// <summary>False when a pair of limits is crossed or equal (XMin ≥ XMax, HeightMin ≥ HeightMax).</summary>
    public bool IsValid => !(XMin >= XMax) && !(HeightMin >= HeightMax);

    public bool InX(double x) => !(x < XMin) && !(x > XMax);

    public bool InBand(double heightMm) => !(heightMm < HeightMin) && !(heightMm > HeightMax);
}

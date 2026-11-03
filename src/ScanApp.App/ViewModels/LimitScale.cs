namespace ScanApp.App.ViewModels;

/// <summary>The six limits of a pass, in panel order.</summary>
public enum LimitKind { TrimStart, TrimEnd, XMin, XMax, HeightMin, HeightMax }

/// <summary>
/// Slider position (0..1) ↔ value for one limit over <c>Min..Max</c>. The range's end on the limit's own side means
/// automatic (trims) or no limit (zone), which is null: position 0 for TrimStart, XMin and HeightMin, position 1 for
/// TrimEnd, XMax and HeightMax.
/// </summary>
public sealed record LimitScale(LimitKind Kind, double Min, double Max)
{
    /// <summary>The wheel step: 0.05 s for the trims, 1 mm for X, 0.5 mm for height.</summary>
    public double Step => Kind switch
    {
        LimitKind.TrimStart or LimitKind.TrimEnd => 0.05,
        LimitKind.XMin or LimitKind.XMax => 1,
        _ => 0.5,
    };

    /// <summary>True for the lower limits, whose own end is <see cref="Min"/> (position 0).</summary>
    private bool AtStart => Kind is LimitKind.TrimStart or LimitKind.XMin or LimitKind.HeightMin;

    private double Span => Max - Min;

    /// <summary>The value at a slider position (clamped to 0..1); null at the limit's own end.</summary>
    public double? ValueAt(double position)
    {
        double p = Math.Clamp(position, 0, 1);
        if (AtStart ? p <= 0 : p >= 1) return null;
        return Min + p * Span;
    }

    /// <summary>The slider position of a value (clamped to 0..1); null sits at the limit's own end.</summary>
    public double PositionOf(double? value)
    {
        if (value is not { } v) return AtStart ? 0 : 1;
        if (!(Span > 0)) return 0.5;
        return Math.Clamp((v - Min) / Span, 0, 1);
    }

    /// <summary>
    /// The value <paramref name="wheelSteps"/> steps of <see cref="Step"/> on, clamped to the range; null (automatic or no
    /// limit) starts from the limit's own end, and landing on that end gives null.
    /// </summary>
    public double? Nudge(double? value, int wheelSteps)
    {
        double from = value ?? (AtStart ? Min : Max);
        double v = Math.Clamp(from + wheelSteps * Step, Min, Max);
        double tolerance = Step * 1e-9;
        return (AtStart ? v <= Min + tolerance : v >= Max - tolerance) ? null : v;
    }
}

namespace ScanApp.Core.Viewing;

/// <summary>
/// Valid points only. TimeS is seconds since the pass's first profile. Profile is the raw profile index of each point
/// (also with a cleanup) and Point its index within that profile, so a point can be matched to the raw pass.
/// </summary>
public sealed record PointSample(float[] X, float[] Z, float[] TimeS, int[] Profile, int[] Point)
{
    public int Count => X.Length;
}

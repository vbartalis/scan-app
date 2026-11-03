namespace ScanApp.Core.Cleanup;

/// <summary>An inclusive range of profile indices.</summary>
public readonly record struct ProfileRange(int Start, int End);

/// <summary>
/// What <see cref="MotionAnalyzer"/> found in one pass. <c>Signal</c> is the smoothed motion signal in mm, per profile,
/// NaN where undefined; <c>TimeS</c> is seconds since the first profile.
/// </summary>
public sealed record MotionAnalysis(float[] Signal, double[] TimeS,
                                    float NoiseFloor, float StillThreshold, bool NoiseFloorCapped,
                                    ProfileRange AutoTrim, bool HasMovement,
                                    IReadOnlyList<ProfileRange> Pauses,
                                    IReadOnlyList<ProfileRange> BackwardRuns);

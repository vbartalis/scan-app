namespace ScanApp.Core.Cleanup;

/// <summary>Tuning for <see cref="MotionAnalyzer"/>. Times are seconds; <c>Lag</c> is in profiles.</summary>
public sealed record MotionOptions(int Lag = 50, double SmoothingS = 0.1, double FloorPercentile = 0.05,
                                   double FloorFactor = 3, float MinStillMm = 0.005f, float MaxNoiseFloorMm = 0.004f,
                                   double MinMovingS = 0.25, double MinPauseS = 0.25, double MinBackwardS = 0.1);

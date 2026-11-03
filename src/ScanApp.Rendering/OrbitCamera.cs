using System.Numerics;

namespace ScanApp.Rendering;

/// <summary>
/// Orbit camera around a target point. World is z-up (Z is height); yaw turns about +Z, pitch lifts the eye above the XY plane.
/// Matrices use System.Numerics' row-vector convention: clip = v × View × Projection. Projection maps depth to [-w, w] (OpenGL).
/// </summary>
public sealed class OrbitCamera
{
    private const float FovRad = MathF.PI / 4;
    private const float MaxPitchRad = 89f * MathF.PI / 180f;
    private const float RadiansPerPixel = 0.01f;

    public Vector3 Target { get; private set; }
    public float Distance { get; private set; } = 1000f;
    public float YawRad { get; private set; } = -MathF.PI / 4;
    public float PitchRad { get; private set; } = MathF.PI / 6;

    /// <summary>Targets the box centre and backs off until its bounding sphere fits the view (exact for aspect at least 1).</summary>
    public void Fit(Vector3 boundsMin, Vector3 boundsMax)
    {
        Target = (boundsMin + boundsMax) / 2;
        float radius = MathF.Max((boundsMax - boundsMin).Length() / 2, 1e-3f);
        Distance = radius / MathF.Sin(FovRad / 2) * 1.05f;
    }

    public void Orbit(float dxPixels, float dyPixels)
    {
        YawRad -= dxPixels * RadiansPerPixel;
        PitchRad = Math.Clamp(PitchRad + dyPixels * RadiansPerPixel, -MaxPitchRad, MaxPitchRad);
    }

    /// <summary>Slides the target in the screen plane so the scene follows the cursor.</summary>
    public void Pan(float dxPixels, float dyPixels, float viewportHeightPixels)
    {
        float perPixel = 2 * Distance * MathF.Tan(FovRad / 2) / MathF.Max(viewportHeightPixels, 1);
        var forward = Vector3.Normalize(-EyeOffset());
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        var up = Vector3.Cross(right, forward);
        Target += (up * dyPixels - right * dxPixels) * perPixel;
    }

    /// <summary>Positive delta zooms in: distance × 0.9^delta, never below a tiny positive floor.</summary>
    public void Zoom(float wheelDelta) =>
        Distance = MathF.Max(Distance * MathF.Pow(0.9f, wheelDelta), 1e-3f);

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Target + EyeOffset(), Target, Vector3.UnitZ);

    public Matrix4x4 Projection(float aspect)
    {
        float near = Distance * 0.01f, far = Distance * 100f;
        float f = 1f / MathF.Tan(FovRad / 2);
        return new Matrix4x4(
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, (far + near) / (near - far), -1,
            0, 0, 2 * far * near / (near - far), 0);
    }

    private Vector3 EyeOffset() => Distance * new Vector3(
        MathF.Cos(PitchRad) * MathF.Cos(YawRad),
        MathF.Cos(PitchRad) * MathF.Sin(YawRad),
        MathF.Sin(PitchRad));
}

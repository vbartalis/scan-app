using System.Numerics;
using ScanApp.Rendering;

namespace ScanApp.Rendering.Tests;

public class OrbitCameraTests
{
    private static Vector3 Eye(OrbitCamera c) =>
        c.Target + c.Distance * new Vector3(
            MathF.Cos(c.PitchRad) * MathF.Cos(c.YawRad),
            MathF.Cos(c.PitchRad) * MathF.Sin(c.YawRad),
            MathF.Sin(c.PitchRad));

    [Fact]
    public void Fit_TargetsCentreAndSeesTheBox()
    {
        var min = new Vector3(-50, 0, -5);
        var max = new Vector3(150, 800, 40);
        var cam = new OrbitCamera();
        cam.Fit(min, max);

        Assert.Equal((min + max) / 2, cam.Target);
        var vp = cam.View * cam.Projection(1.5f);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? min.X : max.X,
                (i & 2) == 0 ? min.Y : max.Y,
                (i & 4) == 0 ? min.Z : max.Z);
            var clip = Vector4.Transform(new Vector4(corner, 1), vp);
            Assert.InRange(clip.X, -clip.W, clip.W);
            Assert.InRange(clip.Y, -clip.W, clip.W);
            Assert.InRange(clip.Z, -clip.W, clip.W);
            Assert.True(clip.W > 0);
        }
    }

    [Fact]
    public void Orbit_ClampsPitch()
    {
        var cam = new OrbitCamera();
        cam.Orbit(0, 1_000_000);
        Assert.True(MathF.Abs(cam.PitchRad) <= 89f * MathF.PI / 180f + 1e-6f);
        cam.Orbit(0, -2_000_000);
        Assert.True(MathF.Abs(cam.PitchRad) <= 89f * MathF.PI / 180f + 1e-6f);
    }

    [Fact]
    public void Zoom_InReducesDistance_NeverReachesZero()
    {
        var cam = new OrbitCamera();
        float before = cam.Distance;
        cam.Zoom(1);
        Assert.True(cam.Distance < before);
        for (int i = 0; i < 10_000; i++) cam.Zoom(50);
        Assert.True(cam.Distance > 0);
    }

    [Fact]
    public void Pan_MovesTargetPerpendicularToViewDirection()
    {
        var cam = new OrbitCamera();
        cam.Fit(new Vector3(0, 0, 0), new Vector3(100, 100, 10));
        var before = cam.Target;
        var viewDir = before - Eye(cam);

        cam.Pan(40, -25, 600);

        var move = cam.Target - before;
        Assert.True(move.Length() > 0);
        Assert.Equal(0f, Vector3.Dot(Vector3.Normalize(move), Vector3.Normalize(viewDir)), 4);
    }
}

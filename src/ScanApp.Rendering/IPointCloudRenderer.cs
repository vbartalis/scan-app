using System.Numerics;

namespace ScanApp.Rendering;

public enum PointColorMode { Height, Pass }

/// <summary>
/// Per point of the raw cloud, in the order of <see cref="IPointCloudRenderer.SetPoints"/>: its raw profile index, its height
/// above the table (NaN without one) and its flags (<see cref="Paused"/>, <see cref="Reflection"/>).
/// </summary>
public sealed record RawPointAttributes(float[] Profile, float[] HeightAboveTable, byte[] Flags)
{
    public const byte Paused = 1, Reflection = 2;
}

/// <summary>
/// The raw cut as the shader tests it: trims in raw profile indices (inclusive), X in mm, the band in mm above the table.
/// No limit is ±<see cref="float.MaxValue"/>. <paramref name="ShowReflections"/> paints the reflections magenta.
/// </summary>
public sealed record RawCutUniforms(float TrimStart, float TrimEnd, float XMin, float XMax, float HeightMin, float HeightMax,
                                    bool FilterReflections, bool ShowReflections);

/// <summary>The kept region in world coordinates, drawn as 12 lines.</summary>
public sealed record CutBox(Vector3 Min, Vector3 Max);

/// <summary>
/// Draws a point cloud in a z-up world: x = X mm, y = travel mm, z = Z mm.
/// The renderer draws positions exactly as given. For a flipped view the caller negates z before
/// <see cref="SetPoints"/> (and so the camera fits the negated bounds); the flip in <see cref="SetColorMode"/> only swaps the colour ramp.
/// </summary>
public interface IPointCloudRenderer
{
    /// <summary>
    /// xyz interleaved, mm; value per point drives the colour (height, or the pass index in Pass mode); NaN never passed.
    /// Refits the camera unless <paramref name="refit"/> is false, which keeps the view (the bounds are still kept for <see cref="ResetView"/>).
    /// </summary>
    void SetPoints(float[] xyz, float[] value, float valueMin, float valueMax, bool refit = true);

    /// <summary>Height ramps the value (flip swaps the ramp ends, matching HeightColorMap flip); Pass maps the value, a pass index, to PassPalette.</summary>
    void SetColorMode(PointColorMode mode, bool flip);

    /// <summary>
    /// Raw mode: one entry per point, uploaded once into a second buffer; the points outside the cut turn grey. Null is
    /// plain mode (cleaned pass, result). Attributes whose count differs from the points' are drawn as plain.
    /// </summary>
    void SetRawAttributes(RawPointAttributes? attributes);

    /// <summary>Per limit change: only the uniforms and the box change, nothing is uploaded. Null clears either.</summary>
    void SetCut(RawCutUniforms? cut, CutBox? box);

    OrbitCamera Camera { get; }

    /// <summary>Refits the camera to the current points.</summary>
    void ResetView();
}

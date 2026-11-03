namespace ScanApp.Core.Viewing;

/// <summary>Maps a height to an ARGB colour on a blue (low) to yellow (high) ramp.</summary>
public static class HeightColorMap
{
    public const uint Hole = 0xFF202020;

    // Dark blue, blue, teal, green, yellow (RGB), evenly spaced.
    private static readonly (byte R, byte G, byte B)[] Stops =
    [
        (0x10, 0x18, 0x60),
        (0x20, 0x50, 0xD0),
        (0x10, 0xA0, 0xA8),
        (0x40, 0xC0, 0x40),
        (0xF8, 0xE0, 0x20),
    ];

    /// <summary>ARGB, blue (low) to yellow (high); flip swaps the ends; NaN gives <see cref="Hole"/>; z outside [min, max] clamps.</summary>
    public static uint ToArgb(float z, float zMin, float zMax, bool flip)
    {
        if (float.IsNaN(z)) return Hole;
        double t = zMax > zMin ? Math.Clamp((z - zMin) / (double)(zMax - zMin), 0, 1) : 0;
        if (flip) t = 1 - t;

        double pos = t * (Stops.Length - 1);
        int i = Math.Min((int)pos, Stops.Length - 2);
        double f = pos - i;
        var a = Stops[i];
        var b = Stops[i + 1];
        uint r = Lerp(a.R, b.R, f), g = Lerp(a.G, b.G, f), bl = Lerp(a.B, b.B, f);
        return 0xFF000000 | r << 16 | g << 8 | bl;
    }

    private static uint Lerp(byte a, byte b, double f) => (uint)Math.Round(a + (b - a) * f);
}

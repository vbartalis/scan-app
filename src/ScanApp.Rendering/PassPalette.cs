using System.Globalization;
using System.Text;

namespace ScanApp.Rendering;

/// <summary>The fixed categorical colours of the by-pass mode. Pass index n gets colour n modulo <see cref="Size"/>.</summary>
public static class PassPalette
{
    public const int Size = 8;

    // Bright, well-separated hues that read on the dark 3D background.
    private static readonly (byte R, byte G, byte B)[] Colors =
    [
        (230, 159, 0),
        (86, 180, 233),
        (0, 192, 140),
        (240, 228, 66),
        (110, 150, 255),
        (240, 100, 60),
        (230, 130, 190),
        (200, 200, 200),
    ];

    public static (byte R, byte G, byte B) Color(int index) => Colors[((index % Size) + Size) % Size];

    /// <summary>The GLSL constant array for the vertex shader, generated so the shader cannot drift from <see cref="Color"/>.</summary>
    public static string GlslArray()
    {
        var sb = new StringBuilder($"const vec3 PassColors[{Size}] = vec3[{Size}](");
        for (int i = 0; i < Size; i++)
        {
            var (r, g, b) = Color(i);
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $"\n            vec3({r}.0, {g}.0, {b}.0) / 255.0");
        }
        return sb.Append(");").ToString();
    }
}

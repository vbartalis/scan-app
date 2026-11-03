using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ScanApp.Core.Model;

namespace ScanApp.App.Views;

/// <summary>Draws one profile as a polyline of Z against X, auto-scaled, broken at missing (NaN) points.</summary>
public sealed class ProfileView : Control
{
    public static readonly StyledProperty<Profile?> ProfileProperty =
        AvaloniaProperty.Register<ProfileView, Profile?>(nameof(Profile));

    private static readonly IPen LinePen = new Pen(Brushes.LimeGreen, 1.5);
    private const double Inset = 12;

    static ProfileView() => AffectsRender<ProfileView>(ProfileProperty);

    public Profile? Profile
    {
        get => GetValue(ProfileProperty);
        set => SetValue(ProfileProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Black, bounds);
        if (Profile is not { } profile || !TryGetExtent(profile, out var extent))
            return;

        double scaleX = (bounds.Width - 2 * Inset) / Math.Max(extent.Width, 1e-6);
        double scaleZ = (bounds.Height - 2 * Inset) / Math.Max(extent.Height, 1e-6);
        Point ToScreen(int i) => new(
            Inset + (profile.X[i] - extent.X) * scaleX,
            bounds.Height - Inset - (profile.Z[i] - extent.Y) * scaleZ);

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            bool inFigure = false;
            for (int i = 0; i < profile.PointCount; i++)
            {
                if (float.IsNaN(profile.X[i]) || float.IsNaN(profile.Z[i]))
                {
                    if (inFigure) g.EndFigure(false);
                    inFigure = false;
                    continue;
                }
                if (!inFigure) g.BeginFigure(ToScreen(i), false);
                else g.LineTo(ToScreen(i));
                inFigure = true;
            }
            if (inFigure) g.EndFigure(false);
        }
        context.DrawGeometry(null, LinePen, geometry);
    }

    /// <summary>X/Z bounding box of the valid points, as a rect with X along the line and Y as height.</summary>
    private static bool TryGetExtent(Profile profile, out Rect extent)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        for (int i = 0; i < profile.PointCount; i++)
        {
            float x = profile.X[i], z = profile.Z[i];
            if (float.IsNaN(x) || float.IsNaN(z)) continue;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
        }
        extent = minX <= maxX ? new Rect(minX, minZ, maxX - minX, maxZ - minZ) : default;
        return minX <= maxX;
    }
}

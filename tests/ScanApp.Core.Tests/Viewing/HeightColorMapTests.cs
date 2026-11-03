using ScanApp.Core.Viewing;

namespace ScanApp.Core.Tests.Viewing;

public class HeightColorMapTests
{
    private static (int R, int G, int B) Rgb(uint argb) =>
        ((int)(argb >> 16 & 0xFF), (int)(argb >> 8 & 0xFF), (int)(argb & 0xFF));

    [Fact]
    public void ToArgb_LowIsBlueHighIsYellow()
    {
        var low = Rgb(HeightColorMap.ToArgb(0f, 0f, 10f, false));
        var high = Rgb(HeightColorMap.ToArgb(10f, 0f, 10f, false));

        Assert.True(low.B > low.R);
        Assert.True(high.R > high.B);
        Assert.True(high.G > high.B);
    }

    [Fact]
    public void ToArgb_FlipSwapsEnds()
    {
        Assert.Equal(HeightColorMap.ToArgb(10f, 0f, 10f, false), HeightColorMap.ToArgb(0f, 0f, 10f, true));
        Assert.Equal(HeightColorMap.ToArgb(0f, 0f, 10f, false), HeightColorMap.ToArgb(10f, 0f, 10f, true));
    }

    [Fact]
    public void ToArgb_NaN_IsHole() =>
        Assert.Equal(HeightColorMap.Hole, HeightColorMap.ToArgb(float.NaN, 0f, 10f, false));

    [Fact]
    public void ToArgb_ZeroRange_DoesNotThrow() =>
        Assert.Equal(HeightColorMap.ToArgb(0f, 0f, 10f, false), HeightColorMap.ToArgb(5f, 5f, 5f, false));

    [Fact]
    public void ToArgb_OutsideRange_Clamps()
    {
        Assert.Equal(HeightColorMap.ToArgb(0f, 0f, 10f, false), HeightColorMap.ToArgb(-5f, 0f, 10f, false));
        Assert.Equal(HeightColorMap.ToArgb(10f, 0f, 10f, false), HeightColorMap.ToArgb(50f, 0f, 10f, false));
    }
}

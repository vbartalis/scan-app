using ScanApp.Rendering;

namespace ScanApp.Rendering.Tests;

public class PassPaletteTests
{
    [Fact]
    public void FirstEightColoursAreDistinct()
    {
        var colours = Enumerable.Range(0, PassPalette.Size).Select(PassPalette.Color).ToList();
        Assert.Equal(8, colours.Distinct().Count());
    }

    [Theory]
    [InlineData(8, 0)]
    [InlineData(11, 3)]
    [InlineData(-1, 7)]
    public void IndexWrapsModuloSize(int index, int same) =>
        Assert.Equal(PassPalette.Color(same), PassPalette.Color(index));

    [Fact]
    public void EveryColourIsBrightEnoughForTheDarkBackground()
    {
        for (int i = 0; i < PassPalette.Size; i++)
        {
            var (r, g, b) = PassPalette.Color(i);
            double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            Assert.True(luma >= 80, $"colour {i} is too dark ({luma:F0})");
        }
    }

    [Fact]
    public void GlslArrayListsEveryColourInOrder()
    {
        string glsl = PassPalette.GlslArray();
        Assert.Contains("vec3[8]", glsl);
        for (int i = 0; i < PassPalette.Size; i++)
        {
            var (r, g, b) = PassPalette.Color(i);
            Assert.Contains($"vec3({r}.0, {g}.0, {b}.0) / 255.0", glsl);
        }
    }
}

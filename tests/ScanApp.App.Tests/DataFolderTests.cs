namespace ScanApp.App.Tests;

public sealed class DataFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"datafolder-{Guid.NewGuid():N}");

    public DataFolderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Locate_FindsDataFolderBesideTheSolution()
    {
        File.WriteAllText(Path.Combine(_root, "ScanApp.sln"), "");
        string exeDir = Path.Combine(_root, "src", "ScanApp.App", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(exeDir);

        Assert.Equal(Path.Combine(_root, "data"), DataFolder.Locate(exeDir));
    }

    [Fact]
    public void Locate_WithoutSolution_FallsBackBesideTheExe()
    {
        string exeDir = Path.Combine(_root, "app");
        Directory.CreateDirectory(exeDir);

        Assert.Equal(Path.Combine(exeDir, "data"), DataFolder.Locate(exeDir));
    }
}

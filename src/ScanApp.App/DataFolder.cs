namespace ScanApp.App;

/// <summary>Where scans live: <c>data/</c> beside the solution when run from the repo, else beside the executable.</summary>
public static class DataFolder
{
    private const string SolutionFile = "ScanApp.sln";

    public static string Locate(string exeDirectory)
    {
        for (var dir = new DirectoryInfo(exeDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, SolutionFile)))
                return Path.Combine(dir.FullName, "data");
        return Path.Combine(exeDirectory, "data");
    }
}

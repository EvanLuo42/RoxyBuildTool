namespace RoxyBuildTool.FakeMsBuild;

public sealed class FakeMsBuildMarker;

public static class Program
{
    public static int Main(string[] args)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".roxy");
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, "fake-msbuild.args"), args);
        return 0;
    }
}

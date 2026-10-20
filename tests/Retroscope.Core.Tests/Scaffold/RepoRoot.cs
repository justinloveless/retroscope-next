namespace Retroscope.Core.Tests.Scaffold;

internal static class RepoRoot {
    public static string Path { get; } = Find();

    private static string Find() {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent) {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Retroscope.slnx"))) {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Retroscope.slnx not found above the test output directory");
    }
}

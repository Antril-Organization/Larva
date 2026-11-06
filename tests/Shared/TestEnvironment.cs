using System.Runtime.CompilerServices;

namespace Larva.Testing;

/// <summary>
/// What every test assembly sets before its first test: larva's settings file is the tests' own (never this machine's
/// defaults; child processes inherit it), emptied at the start of the run.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        string config = Path.Combine(Path.GetTempPath(), "larva-tests", AppDomain.CurrentDomain.FriendlyName, "defaults.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        if (File.Exists(config))
            File.Delete(config);
        Environment.SetEnvironmentVariable("LARVA_CONFIG", config);
    }

    /// <summary>A new empty folder for one test, under the system's temporary folder.</summary>
    public static string TempFolder(string name)
    {
        string folder = Path.Combine(Path.GetTempPath(), "larva-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }
}

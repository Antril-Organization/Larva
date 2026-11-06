using System.Runtime.CompilerServices;

// The tests share temporary folders and process-wide settings (QueenProject.SourceExtensions, HIVE_CACHE): one test at a time.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Larva.Build.Tests;

internal static class BuildTestSetup
{
    /// <summary>
    /// A project's sources are the files of an installed language: larva sets these from the language packs, the tests
    /// name one extension. Hive's cache is a temporary folder of the run, never this machine's.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        QueenProject.SourceExtensions = [".ant"];
        Environment.SetEnvironmentVariable("HIVE_CACHE", TestEnvironment.TempFolder("hive-cache"));
    }
}

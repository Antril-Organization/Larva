namespace Larva.Tests;

/// <summary>
/// Tests that run compiled code in this process (LLVM's JIT, the x64 back end, the in-process runtime): that runtime raises
/// exceptions with SEH and allocates through kernel32, and the code follows the Win64 ABI. On Linux and macOS they are
/// skipped; programs built for those systems are tested as executables (larva build --target linux-x64).
/// </summary>
internal static class InProcess
{
    public static void RequireWindows() =>
        Assert.SkipUnless(OperatingSystem.IsWindows(), "runs Windows code in this process (the in-process runtime and the JIT are Windows only)");
}

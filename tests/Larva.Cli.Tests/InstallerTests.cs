using System.Runtime.Versioning;
using Larva.Setup;

namespace Larva.Cli.Tests;

/// <summary>
/// The Windows setup's helpers that need no registry: what an installation leaves of an earlier one, what is in use
/// before anything is removed, and the script that removes the uninstaller once it has ended.
/// </summary>
[SupportedOSPlatform("windows")] // the setup is: here the tests that need no registry still run anywhere (InUse skips elsewhere)
public sealed class InstallerTests
{
    [Fact]
    public void Stale_IsWhatThePreviousInstallationHadAndTheNewOneDoesNotBring()
    {
        string[] previous = ["larva.exe", @"Library\System.Core\List.ant", "Library/System.Core/String.ant", @".\README.txt", "", "uninstall.exe"];
        string[] installed = ["LARVA.EXE", "System.Core.dll", "README.txt", "uninstall.exe"];
        Assert.Equal([@"Library\System.Core\List.ant", "Library/System.Core/String.ant"], Installer.Stale(previous, installed));
        Assert.Empty(Installer.Stale(installed, installed));
        Assert.Empty(Installer.Stale([], installed));
    }

    [Fact]
    public void InUse_NamesTheFirstFileAnotherProgramHoldsAndNothingWhenAllAreFree()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the setup is Windows-only");
        string folder = Path.Combine(Path.GetTempPath(), "larva-tests", "installer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "Languages"));
        string free = Path.Combine(folder, "larva.exe"), held = Path.Combine(folder, "Languages", "pack.dll");
        File.WriteAllText(free, "x");
        File.WriteAllText(held, "y");
        try
        {
            string[] files = ["larva.exe", "missing.dll", "Languages/pack.dll", @"..\outside.txt", ""];
            Assert.Null(Installer.InUse(folder, files, keep: null));
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Equal(held, Installer.InUse(folder, files, keep: null));
                // The running uninstaller is never asked: it cannot be deleted now, and it is removed after it ends.
                Assert.Null(Installer.InUse(folder, files, keep: held));
            }
            Assert.Null(Installer.InUse(folder, files, keep: null));
            // Nothing was changed by asking.
            Assert.Equal("x", File.ReadAllText(free));
            Assert.Equal("y", File.ReadAllText(held));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void DeleteAfterExitScript_WaitsForTheProcessThenDeletesTheUninstallerTheEmptyFolderAndItself()
    {
        string script = Installer.DeleteAfterExitScript(4242, @"C:\Programs\Larva\uninstall.exe", @"C:\Programs\Larva");
        string[] lines = script.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("tasklist /fi \"PID eq 4242\" 2>nul | find \"4242\" >nul", lines);
        Assert.Contains("if errorlevel 1 goto gone", lines);
        Assert.Contains("goto wait", lines);
        int gone = Array.IndexOf(lines, ":gone");
        Assert.True(gone > Array.IndexOf(lines, ":wait"), "the wait comes first");
        Assert.Equal(["del /f /q \"C:\\Programs\\Larva\\uninstall.exe\"", "rmdir \"C:\\Programs\\Larva\"", "del \"%~f0\""], lines[(gone + 1)..]);
        Assert.DoesNotContain("/s", script); // the folder goes only when nothing else is left in it
    }
}

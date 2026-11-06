namespace Larva.Setup;

/// <summary>What the wizard says (in English, as every Larva tool).</summary>
internal sealed class Texts
{
    public static readonly Texts Current = English();

    public required char Decimal;
    public required string Window, Brand, Back, Next, Install, Cancel, Finish, Browse;
    public required string WelcomeTitle, WelcomeBody;
    public required string OptionsTitle, OptionsSubtitle, Folder, AddToPath, AddToApps, AddHive, AddTypes, Space, Free;
    public required string ReadyTitle, ReadySubtitle, ReadyFolder, ReadyOptions, ReadyPath, ReadyApps, ReadyHive, ReadyTypes, ReadyNoOptions;
    public required string Upgrade, Reinstall, Replace, ReadyNeeds, ReadyNeedsNote, ReadyGo;
    public required string Msvc, MsvcMissing, Clang, ClangMissing, Node, NodeMissing;
    public required string InstallingTitle, InstallingSubtitle, Copying, StepPath, StepApps, StepHive, StepTypes, StepCheck;
    public required string DoneTitle, DoneSubtitle, DoneBody, DoneOnPath, DoneNotOnPath, OpenFolder;
    public required string Failed, Running, FolderEmpty, ConfirmCancel, BrowseTitle;
    public required string UninstallConfirm, UninstallDone, UninstallFailed;

    private static Texts English() => new()
    {
        Decimal = '.',
        Window = "Larva {0} Setup",
        Brand = "Larva {0}",
        Back = "< Back",
        Next = "Next >",
        Install = "Install",
        Cancel = "Cancel",
        Finish = "Finish",
        Browse = "Browse...",
        WelcomeTitle = "Welcome to the Larva {0} Setup Wizard",
        WelcomeBody =
            "This wizard will install Larva {0} on your computer.\n\n" +
            "Larva is a set of build tools:\n\n" +
            "    •  larva build: compiles to native code and WebAssembly.\n" +
            "    •  larva lsp: a language server for code editors.\n" +
            "    •  larva bind: generates bindings for C libraries.\n\n" +
            "Larva is installed for your user only and needs no administrator rights.\n\n" +
            "Click Next to continue, or Cancel to exit Setup.",
        OptionsTitle = "Destination Folder and Options",
        OptionsSubtitle = "Choose where Larva will be installed and how it integrates with Windows.",
        Folder = "Destination folder:",
        AddToPath = "Add Larva to the user PATH variable (run larva from any terminal)",
        AddToApps = "Register Larva in Installed apps, with its uninstaller",
        AddHive = "Use the www.antril.org/hive-api package registry by default (larva's settings)",
        AddTypes = "Show Larva's files with their icons (.lva, .lvo, .queen, .hive, source files)",
        Space = "Space required: {0}",
        Free = "Space available: {0}",
        ReadyTitle = "Ready to Install",
        ReadySubtitle = "Review the settings before starting the installation.",
        ReadyFolder = "Destination folder:",
        ReadyOptions = "Options:",
        ReadyPath = "Add Larva to the user PATH variable",
        ReadyApps = "Register Larva in Installed apps",
        ReadyHive = "Use www.antril.org/hive-api as the default package registry",
        ReadyTypes = "Show Larva's files with their icons",
        ReadyNoOptions = "None",
        Upgrade = "Larva {0} will be upgraded to version {1}.",
        Reinstall = "Larva {0} will be reinstalled over the existing installation.",
        Replace = "The Larva installation already in this folder will be replaced.",
        ReadyNeeds = "Optional system components:",
        ReadyNeedsNote = "Missing components can be installed later.",
        ReadyGo = "Click Install to begin.",
        Msvc = "MSVC build tools (native executables)",
        MsvcMissing = "MSVC build tools: not found (needed for native executables)",
        Clang = "clang (C header preprocessing in larva bind)",
        ClangMissing = "clang: not found (larva bind will read headers without preprocessing)",
        Node = "Node.js (running WebAssembly programs)",
        NodeMissing = "Node.js: not found (only to run WebAssembly outside a browser)",
        InstallingTitle = "Installing",
        InstallingSubtitle = "Please wait while Larva {0} is being installed.",
        Copying = "Extracting: {0}",
        StepPath = "Configuring the PATH variable...",
        StepApps = "Registering the application...",
        StepHive = "Configuring the package registry...",
        StepTypes = "Registering file types...",
        StepCheck = "Verifying the installation...",
        DoneTitle = "Installation Complete",
        DoneSubtitle = "Larva {0} was installed successfully.",
        DoneBody = "Larva was installed in:\n{0}\n\n{1}\n\nClick Finish to close the wizard.",
        DoneOnPath = "To verify the installation, open a new terminal and run:\n    larva --version",
        DoneNotOnPath = "Larva was not added to the PATH variable: run it by its full path (larva.exe in the installation folder).",
        OpenFolder = "Open the installation folder",
        Failed = "The installation could not be completed.\n\n{0}\n\nMake sure Larva is not in use and try again.",
        Running = "Larva is in use from the destination folder ({0} process(es)).\n\nClose the programs using it and click Retry.",
        FolderEmpty = "Enter a valid destination folder.",
        ConfirmCancel = "Are you sure you want to cancel the Larva installation?",
        BrowseTitle = "Select the folder where Larva will be installed:",
        UninstallConfirm = "Do you want to uninstall Larva {0} from\n{1}?",
        UninstallDone = "Larva was uninstalled successfully.",
        UninstallFailed = "Larva could not be uninstalled.\n\n{0}",
    };
}

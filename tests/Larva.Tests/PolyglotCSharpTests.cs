using System.Diagnostics;
using Larva.Native;

namespace Larva.Tests;

/// <summary>
/// <c>larva polyglot build --lang c,csharp</c> end to end: the C# face of System.Bindings and of System.Core, built by
/// dotnet into a library, and a C# program that uses it from several threads (classes with their constructors,
/// properties and methods, static classes, overloads, enums, lists as IList, structs, optionals, arrays, exceptions,
/// objects disposed and finalized). Needs MSVC, the dotnet SDK this test runs on, and compiled System.Core and
/// System.Bindings (the installer's stage, or an installed larva).
/// </summary>
public sealed class PolyglotCSharpTests : CheckedTest
{
    private static readonly Lazy<string> Folder = new(() => TestEnvironment.TempFolder("polyglot-cs"));

    private static string Framework => $"net{Environment.Version.Major}.0";

    private static string? Libraries()
    {
        string[] candidates =
        [
            Path.Combine(Repository.Root, "artifacts", "installer", "stage"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Larva"),
        ];
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "System.Core.dll")) && File.Exists(Path.Combine(c, "System.Bindings.dll")));
    }

    private const string BindingsProgram = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using System.Bindings;

        int failures = 0;
        void Check(string what, bool ok) { if (!ok) { Console.WriteLine("FAILED: " + what); System.Threading.Interlocked.Increment(ref failures); } }

        // A class: its constructor, properties, methods; disposed.
        using (var type = new CType("u8*", ""))
        {
            Check("a CType made in C#", type.IsPointer());
            Check("its Text", type.Text == "u8*");
            type.Text = "i32";
            Check("Text set and read again", type.Text == "i32" && type.IsNumber());
        }
        // A static class, overloads, an enum, a list given as a C# list (copied) and as the library's (not copied).
        Check("Names.Pascal", Names.Pascal("window_flags") == "WindowFlags");
        Check("Names.Shape with NameCase.Snake", Names.Shape("GPUDevice", NameCase.Snake) == "gpu_device");
        Check("Names.Strip(string, string)", Names.Strip("SDL_Window", "SDL_") == "Window");
        Check("Names.Strip(string, IList<string>) from a C# list", Names.Strip("SDL_Window", new List<string> { "X_", "SDL_" }) == "Window");
        var prefixes = new StringList { "SDL_" };
        prefixes.Insert(0, "GL_");
        Check("a StringList is an IList<string>", prefixes.Count == 2 && prefixes[0] == "GL_" && prefixes.Contains("SDL_") && string.Join(",", prefixes) == "GL_,SDL_");
        Check("Names.Strip with the library's list", Names.Strip("GL_Texture", prefixes) == "Texture");
        // Errors: C#'s for a null argument, the library's as LarvaException.
        try { Names.Pascal(null!); Check("null for a string throws", false); } catch (ArgumentNullException) { }
        try { _ = new StringList()[3]; Check("past the end throws", false); }
        catch (IndexOutOfRangeException e) { Check("System.Core's IndexError is .NET's, the library's type inside", e.InnerException is LarvaException { TypeName: "System.IndexError" }); }
        // An interface: a class of the library is one (explicitly), and what the library gives as one is the class it is.
        IBindingProfile profile = new BindingProfile();
        var settings = new BindingSettings();
        profile.Configure(settings);
        Check("a class is the interface it implements", profile is BindingProfile);
        Check("an interface method through the class", profile.Name(NameKind.Type, "SDL_Window", "Window") == "Window");
        // An interface implemented in C#: the library's BindingRunner calls a C# profile's methods (and its exception).
        var model = new BindingModel(new BindingSettings());
        model.AddFunction("SDL_Init", "SDL.h", "i32", "").Add("flags", "u32", "");
        model.AddFunction("SDL_Quit", "SDL.h", "void", "");
        var mine = new CSharpProfile();
        string description = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"larva-profile-{Environment.ProcessId}.txt");
        BindingRunner.Run(mine, model, description);
        string written = System.IO.File.ReadAllText(description);
        Check("the C# profile was called: Start, each function, Finish", mine.Calls.Contains("Start") && mine.Calls.Contains("Function SDL_Init")
            && mine.Calls.Contains("Function SDL_Quit") && mine.Calls[^1] == "Finish");
        Check("its Name renamed (text back to the library)", written.Contains("InitEverything"));
        Check("its Function left one out (a bool back)", !written.Contains("SDL_Quit"));
        System.IO.File.Delete(description);
        try { BindingRunner.Run(new FailingProfile(), model, description); Check("the profile's exception reaches C#", false); }
        catch (LarvaException e) { Check("as the library's exception, with the profile's message", e.Message == "no start"); }

        // Several threads at once; objects left to the finalizer.
        Parallel.For(0, 4, n =>
        {
            for (int round = 0; round < 200; round++)
            {
                var type = new CType($"u{n * 1000 + round}*", "");
                if (!type.IsPointer() || type.Text != $"u{n * 1000 + round}*")
                    Check("a thread's CType", false);
                if (Names.Shape("WindowFlags", NameCase.Upper) != "WINDOW_FLAGS")
                    Check("a thread's Names.Shape", false);
            }
        });
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Console.WriteLine(failures == 0 ? "ok 0" : $"failed {failures}");
        return failures;

        sealed class CSharpProfile : IBindingProfile
        {
            public readonly List<string> Calls = [];
            public void Configure(BindingSettings settings) => Calls.Add("Configure");
            public void Start(BindingModel model) => Calls.Add("Start");
            public bool Enum(CEnum e) => true;
            public bool Struct(CStruct s) => true;
            public bool Constant(CConstant c) => true;
            public bool Function(CFunction f) { Calls.Add("Function " + f.Entry); return f.Entry != "SDL_Quit"; }
            public string Name(NameKind kind, string cName, string proposed) => cName == "SDL_Init" ? "InitEverything" : proposed;
            public void Finish(BindingModel model) => Calls.Add("Finish");
        }

        sealed class FailingProfile : IBindingProfile
        {
            public void Configure(BindingSettings settings) { }
            public void Start(BindingModel model) => throw new InvalidOperationException("no start");
            public bool Enum(CEnum e) => true;
            public bool Struct(CStruct s) => true;
            public bool Constant(CConstant c) => true;
            public bool Function(CFunction f) => true;
            public string Name(NameKind kind, string cName, string proposed) => proposed;
            public void Finish(BindingModel model) { }
        }
        """;

    private const string CoreProgram = """
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Threading.Tasks;
        using L = LarvaCore.System;

        int failures = 0;
        void Check(string what, bool ok) { if (!ok) { Console.WriteLine("FAILED: " + what); System.Threading.Interlocked.Increment(ref failures); } }

        // Callbacks: lambdas the library calls (on its own threads too), their exceptions, calls back into the library.
        int sum = 0;
        L.Threading.Parallel.For(0, 1000, i => System.Threading.Interlocked.Add(ref sum, i));
        Check("Parallel.For calls the lambda on the library's threads", sum == 499500);
        var mutex = new L.Threading.Mutex();
        int calls = 0;
        for (int i = 0; i < 100; i++)
            mutex.Run(() => calls++);
        Check("Mutex.Run calls the lambda", calls == 100);
        try { mutex.Run(() => throw new InvalidOperationException("boom")); Check("an exception in a callback reaches the caller", false); }
        catch (LarvaCore.System.Core.LarvaException e) { Check("the callback's exception, as the library's", e.Message == "boom"); }
        int inner = 0;
        mutex.Run(() => inner = L.TimeSpan.FromSeconds(42).Seconds);
        Check("a callback calls the library again", inner == 42);
        var thread = new L.Threading.Thread(() => System.Threading.Interlocked.Increment(ref calls));
        thread.Start();
        thread.Join();
        Check("a Thread of the library runs the lambda", calls == 101);
        L.GC.Collect();

        Parallel.For(0, 4, n =>
        {
            for (int round = 0; round < 50; round++)
            {
                // Structs: methods, properties, static constructors of values.
                var sum = L.TimeSpan.FromSeconds(90 + n).Add(L.TimeSpan.FromSeconds(30));
                Check("TimeSpan arithmetic", sum.TotalSeconds == 120 + n && sum.Seconds == n);
                // An optional result.
                Check("DateTime.TryParse", L.DateTime.TryParse("2026-11-24") is L.DateTime d && d.Year == 2026);
                Check("DateTime.TryParse of nonsense", L.DateTime.TryParse("not a date") is null);
                // Arrays: out as an array, in as a span.
                ushort[] units = L.Unicode.ToUtf16("año");
                Check("Unicode.ToUtf16", units.Length == 3 && units[1] == 0xF1);
                Check("Array.Equals", L.Array.Equals(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 }) && !L.Array.Equals(new byte[] { 1 }, new byte[] { 2 }));
                // Lists both ways, through the library's files.
                string path = Path.Combine(Path.GetTempPath(), $"larva-cs-{Environment.ProcessId}-{n}-{round}.txt");
                L.IO.File.WriteAllLines(path, new List<string> { "uno", "dos" });
                IList<string> read = L.IO.File.ReadAllLines(path);
                Check("ReadAllLines gives the lines", read.Count == 2 && read[0] == "uno" && read[1] == "dos");
                read.Clear();
                Check("cleared", read.Count == 0);
                File.Delete(path);
                // A result is the class the object is: a Stream property gives back the MemoryStream it holds.
                using var memory = new L.IO.MemoryStream();
                using var writer = new L.IO.BinaryWriter(memory);
                Check("BaseStream is the MemoryStream", writer.BaseStream is L.IO.MemoryStream);
                // System.Core's errors are .NET's.
                try { L.TimeSpan.Parse("not a span"); Check("TimeSpan.Parse of nonsense throws", false); }
                catch (FormatException e) { Check("a FormatError is a FormatException", e.InnerException is LarvaCore.System.Core.LarvaException { TypeName: "System.FormatError" }); }
            }
        });
        Console.WriteLine(failures == 0 ? "ok 0" : $"failed {failures}");
        return failures;
        """;

    [Fact]
    public void System_Bindings_from_CSharp() => Run("bindings", "System.Bindings.dll", "SystemBindings", BindingsProgram, []);

    [Fact]
    public void System_Core_from_CSharp() =>
        Run("core", "System.Core.dll", "SystemCore", CoreProgram, ["--csharp-root", "LarvaCore", "--exclude", "System.Threading.Tasks.Task.GetResult"]);

    private void Run(string name, string library, string assembly, string program, string[] options)
    {
        Assert.SkipWhen(Toolchain.Find() is null, Toolchain.Missing);
        string? libraries = Libraries();
        Assert.SkipWhen(libraries is null, "no compiled System.Core and System.Bindings (the installer's stage, or an installed larva)");
        string folder = Path.Combine(Folder.Value, name);
        var (code, log) = Repository.Cli(null, 900_000, ["polyglot", "build", Path.Combine(libraries!, library), "--core", Path.Combine(libraries!, "System.Core.dll"),
            "--lang", "c,csharp", "--csharp-framework", Framework, "-o", folder, .. options]);
        Check($"larva polyglot build {library} --lang c,csharp", code == 0, log.Trim().Length > 2000 ? log.Trim()[^2000..] : log.Trim());
        if (code != 0)
            return;
        Check("a folder per language, each with its own native library",
            Directory.GetFiles(Path.Combine(folder, "c"), "*.h").Length == 1
            && Directory.GetFiles(Path.Combine(folder, "csharp"), "*.dll").Length == 1);
        // The C# folder, moved elsewhere: it stands by itself.
        string moved = Path.Combine(folder, "moved");
        if (Directory.Exists(moved))
            Directory.Delete(moved, recursive: true);
        Directory.Move(Path.Combine(folder, "csharp"), moved);
        folder = moved;
        // What larva writes, not the library's own documentation (copied as its author wrote it).
        var written = File.ReadAllLines(Path.Combine(folder, assembly + ".cs")).Where(l => !l.TrimStart().StartsWith("///")).ToList();
        Check("the C# code does not say what language the library was written in", !written.Any(l => l.Contains("Antril", StringComparison.OrdinalIgnoreCase)));
        var (built, buildLog) = Dotnet(folder, "build", Path.Combine(folder, assembly + ".csproj"), "-warnaserror");
        Check($"the C# library builds without warnings", built == 0, buildLog.Length > 3000 ? buildLog[^3000..] : buildLog);

        // A program that references the generated project.
        string app = Path.Combine(folder, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "App.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{Framework}</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>disable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\{assembly}.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(app, "Program.cs"), program);
        var (exit, output) = Dotnet(app, "run", "--project", Path.Combine(app, "App.csproj"));
        Check($"a C# program uses {library}: every check holds", exit == 0 && output.TrimEnd().EndsWith("ok 0"), $"exit {exit}: {(output.Length > 3000 ? output[^3000..] : output)}");
    }

    private static (int Exit, string Output) Dotnet(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(600_000))
        {
            process.Kill(entireProcessTree: true);
            return (-1, output.Result + errors.Result + " [killed]");
        }
        return (process.ExitCode, output.Result + errors.Result);
    }
}

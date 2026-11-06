using System.Globalization;
using Larva.Driver;
using Larva.IR;
using Larva.IR.Linking;
using Larva.IR.Lowering;
using Larva.Llvm;
using Larva.Native;
using Larva.Runtime.Vm;
using Larva.X64;

// larva run  <module.lvm> [--gc-stress] [args...]                    runs the module's entry function
// larva dis  <module.lvm>                                            prints the module in assembly form
// larva link -o <out.lvm> [--entry name] [--library] <a.lvo>...      links objects into a program
// larva native -o <out> [--exe] [--backend llvm|x64] [--emit obj|asm|ll] [-O0..3] [--entry name] [--library] [--no-bce]
//              [--polly [--polly-opt <opt>] [--polly-plugin <LLVMPolly>]] <a.lvo>...  native code via LLVM

const string Usage = """
    usage: larva new  [<template> [<Name>] [--lang <language>] [-o <folder>]]   a project from a template (none: the list)
           larva run  [<project.queen|folder>] [--rebuild] [build options] [-- <arguments>...]   the project you are in, built if it changed, run
           larva build [-o <app.exe|lib.dll|app.js>] [--check] [--dll] [--target native|wasm|wasm-llvm] [--backend llvm|x64] [-O0..3] [--borrowcheck enforce|warn|off] [--cpu generic|native|<name>] [--core <dir>] [--timings] [--no-cache] [<source file|project.queen>...]   (none: the project you are in)
           larva lib  -o <Name.dll> [--borrowcheck enforce|warn|off] [--core <dir>] <source file>...   (against System.Core)
           larva config [<key> [<value>] | --unset <key>]    this machine's defaults (BUILD keys, hive.provider, languages.provider, languages.trust)
           larva hive <key|pack|publish|install|add|restore|verify|info|list|trust> ...   packages (larva hive: the list)
           larva bind <lib.dll|lib.so> <api.bind> [-o <Name.dll>] [--source Name.ext] [--core <dir>]   (--list <lib>: its exports)
           larva types  [--lang <language>|hir] [--lib X.dll]... [source file]... [namespace]
           larva show   [--lang <language>|hir] [--no-bodies] [--public] [--lib X.dll]... [source file]... <Type>...
           larva search [--lang <language>|hir] [--lib X.dll]... [source file]... <query>
           larva languages                                    the language packs installed, checked
           larva languages search [<text>] | install <id>[@<version>] | update [<id>] | remove <id>   [--provider <url>]   languages from a registry
           larva languages publish <folder> [--key <author.pem>] | status <request-id>   [--provider <url>]   publish a language (larva languages: details)
           larva languages pack <folder> [--key <key.pem>] | key <key.pem>   a pack's manifest completed and signed; a signing key
           larva languages approve <request|pack> --registry-key <registry.pem> --into <folder-registry>   (administrators)
           larva lsp    [--core <dir>]                         a language server (stdio) for editors
           larva run  <module.lvm> [--gc-stress] [args...]
           larva dis  <module.lvm>
           larva link -o <out.lvm> [--entry name] [--library] <object.lvo>...
           larva native -o <out> [--exe] [--backend llvm|x64] [--emit obj|asm|ll] [-O0..3] [--entry name] [--library] [--no-bce]
                        [--polly [--polly-opt <opt>] [--polly-plugin <LLVMPolly>]] <object.lvo>...
    """;

if (args is ["--version"] or ["version"])
{
    // `larva --version`: the toolchain's version (what the installer installed, what the editor checks).
    Console.WriteLine($"larva {LarvaVersion.Current}");
    return 0;
}
// A project's sources are the files of the languages installed (their manifests say which extensions).
Larva.Build.QueenProject.SourceExtensions = LanguageRegistry.Extensions;
if (args.Length >= 1 && args[0] == "new")
    return NewCommand.Run(args[1..]);
if (args.Length >= 1 && args[0] == "run" && RunCommand.Handles(args))
    return RunCommand.Run(args[1..]);
if (args.Length >= 1 && args[0] == "languages")
    return LanguagesCommand.Run(args[1..]);
if (args.Length >= 1 && args[0] == "lsp")
    return LspServer.Run(args[1..]);
if (args.Length >= 1 && args[0] is "types" or "show" or "search")
    return TypeCommands.Run(args[0], args[1..]);
if (args.Length >= 1 && args[0] == "bind")
    return BindCommand.Run(args[1..]);
if (args.Length >= 1 && args[0] == "hive")
    return HiveCommand.Run(args[1..]);
if (args.Length >= 1 && args[0] == "config")
    return ConfigCommand.Run(args[1..]);
if (args is ["build", ..])
    return SourceBuild.Build(args[1..]);

if (args.Length < 2 || args[0] is not ("run" or "dis" or "link" or "native" or "build" or "lib"))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

if (args[0] == "build")
    return SourceBuild.Build(args[1..]);
if (args[0] == "lib")
    return SourceBuild.Lib(args[1..]);

if (args[0] is "link" or "native")
    return Link(args[1..], native: args[0] == "native");

IlModule module;
try
{
    module = ModuleFile.Load(args[1]);
}
catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"larva: cannot load {args[1]}: {e.Message}");
    return 1;
}

if (args[0] == "dis")
{
    Console.Write(module);
    return 0;
}

if (module.Entry < 0)
{
    Console.Error.WriteLine("larva: the module has no entry function");
    return 1;
}

bool stress = args.Contains("--gc-stress");
object[] arguments = [.. args.Skip(2).Where(a => a != "--gc-stress").Select(Parse)];
var vm = new Interpreter(module, options: new InterpreterOptions { GcStress = stress });
try
{
    var result = vm.Run(module.Functions[module.Entry].Name, arguments);
    if (result is not null)
        Console.WriteLine(result is byte[] bytes ? Convert.ToHexString(bytes) : Convert.ToString(result, CultureInfo.InvariantCulture));
    if (Environment.GetEnvironmentVariable("LARVA_STATS") is "1")
        Console.Error.WriteLine(vm.Stats);
    return 0;
}
catch (VmException e)
{
    Console.Error.WriteLine($"larva: {e.Message}");
    return 1;
}

static int Link(string[] args, bool native)
{
    string? output = null;
    string entry = "main";
    string emit = "obj";
    int optimization = 2;
    bool library = false;
    bool boundsChecks = true;
    string backend = "llvm";
    bool executable = false;
    bool polly = false;
    string pollyTool = "opt";
    string? pollyPlugin = null;
    List<string> inputs = [];
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "-o" when i + 1 < args.Length:
                output = args[++i];
                break;
            case "--entry" when i + 1 < args.Length:
                entry = args[++i];
                break;
            case "--library":
                library = true;
                break;
            case "--emit" when i + 1 < args.Length:
                emit = args[++i];
                break;
            case "--exe":
                executable = true;
                break;
            case "--backend" when i + 1 < args.Length:
                backend = args[++i];
                break;
            case "--no-bce":
                boundsChecks = false;
                break;
            case "--polly":
                polly = true;
                break;
            case "--polly-opt" when i + 1 < args.Length:
                (polly, pollyTool) = (true, args[++i]);
                break;
            case "--polly-plugin" when i + 1 < args.Length:
                (polly, pollyPlugin) = (true, args[++i]);
                break;
            case ['-', 'O', >= '0' and <= '3']:
                optimization = args[i][2] - '0';
                break;
            default:
                inputs.Add(args[i]);
                break;
        }
    }
    if (output is null || inputs.Count == 0)
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }

    List<LarvaObject> objects = [];
    foreach (var input in inputs)
    {
        try
        {
            objects.Add(ObjectFile.Load(input));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"larva: cannot load {input}: {e.Message}");
            return 1;
        }
    }

    var options = new LinkOptions
    {
        Entry = entry,
        Library = library,
        Compilation = CompilationOptions.Default with { Gc = objects[0].Gc },
    };
    var result = Linker.Link(objects, options);
    if (!result.Success)
    {
        foreach (var error in result.Errors)
            Console.Error.WriteLine(error);
        return 1;
    }
    if (!native)
    {
        ModuleFile.Save(result.Il!, output);
        Console.WriteLine($"linked {objects.Count} object(s) into {output} ({result.Il!.Functions.Length} functions)");
        return 0;
    }

    if (executable)
        return Executable(result, options.Compilation, backend, optimization, boundsChecks, polly ? new PollyOptions(pollyTool, pollyPlugin) : null, output, objects.Count);

    if (backend == "x64")
    {
        // The direct back end writes COFF objects only (no LLVM involved).
        if (emit != "obj" || polly)
        {
            Console.Error.WriteLine("larva: the x64 back end emits objects only (--emit obj, no Polly)");
            return 2;
        }
        var direct = X64Backend.Compile(result, new X64Options { Compilation = options.Compilation, BoundsCheckElimination = boundsChecks });
        if (!direct.Success)
        {
            Console.Error.WriteLine($"larva: {direct}");
            return 1;
        }
        direct.Program!.EmitObject(output);
        Console.WriteLine($"compiled {objects.Count} object(s) to {output} (obj, direct x86-64 back end, {direct.Program.Functions.Count} functions; "
            + $"bounds checks: {direct.Program.BoundsChecks} kept, {direct.Program.BoundsChecksEliminated} eliminated)");
        return 0;
    }
    if (backend != "llvm")
    {
        Console.Error.WriteLine($"larva: unknown --backend {backend} (llvm or x64)");
        return 2;
    }

    var compiled = LlvmBackend.Compile(result, new LlvmOptions
    {
        Compilation = options.Compilation,
        OptimizationLevel = optimization,
        BoundsCheckElimination = boundsChecks,
        Polly = polly ? new PollyOptions(pollyTool, pollyPlugin) : null,
    });
    if (!compiled.Success)
    {
        Console.Error.WriteLine($"larva: {compiled}");
        return 1;
    }
    using var program = compiled.Program!;
    switch (emit)
    {
        case "obj": program.EmitObject(output); break;
        case "asm": program.EmitAssembly(output); break;
        case "ll": program.EmitIr(output); break;
        default:
            Console.Error.WriteLine($"larva: unknown --emit {emit} (obj, asm or ll)");
            return 2;
    }
    Console.WriteLine($"compiled {objects.Count} object(s) to {output} ({emit}, {program.Triple}, O{optimization}{(program.Polly ? " + Polly" : "")}; "
        + $"bounds checks: {program.BoundsChecks} kept, {program.BoundsChecksEliminated} eliminated)");
    return 0;
}

/// Program + C runtime → .exe (Larva.Driver: entry wrappers, prefixed symbols, MSVC).
static int Executable(LinkResult linked, CompilationOptions compilation, string backend, int optimization, bool boundsChecks,
    PollyOptions? polly, string output, int objectCount)
{
    if (backend is not ("llvm" or "x64"))
    {
        Console.Error.WriteLine($"larva: unknown --backend {backend} (llvm or x64)");
        return 2;
    }
    var (success, message) = ExecutableBuilder.Build(linked, output, new ExecutableOptions
    {
        Compilation = compilation,
        Backend = backend == "x64" ? Backend.X64 : Backend.Llvm,
        OptimizationLevel = optimization,
        BoundsCheckElimination = boundsChecks,
        Polly = polly,
    });
    if (!success)
    {
        Console.Error.WriteLine($"larva: {message}");
        return 1;
    }
    Console.WriteLine($"linked {objectCount} object(s) into {message}");
    return 0;
}

static object Parse(string text) =>
    bool.TryParse(text, out bool b) ? b
    : long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l
    : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d
    : throw new ArgumentException($"cannot parse argument `{text}` (expected an integer, a float or true/false)");

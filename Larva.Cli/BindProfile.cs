using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Larva.Bindings;
using Larva.Hir.Encoding;
using Larva.Hir.Linking;
using Larva.Hir.Lowering;
using Larva.Hir.Services;
using Larva.Runtime.Vm;
using H = Larva.Hir;

/// <summary>
/// <c>larva bind --profile Profile.ant [libraries] [headers] [-I dir] [-o Name.dll] [--source folder] [--class Name] [--dry-run]</c>:
/// a binding decided by a profile, a class that implements <c>System.Bindings.IBindingProfile</c> (usually deriving from
/// <c>BindingProfile</c>). The profile is compiled with System.Bindings and run on the VM, inside larva: first its
/// <c>Configure</c> (what to read and where it goes), then, with what the headers declare, every hook; what it leaves is
/// a description larva turns into the Larva library and its source.
/// </summary>
internal static class BindProfile
{
    /// <summary>What a profile's <c>Configure</c> said, with the command line on top (paths from the profile's folder).</summary>
    private sealed class Settings
    {
        public List<string> Libraries = [], Headers = [], Includes = [];
        public string Namespace = "Bindings", Class = "", Assembly = "", Output = "", Source = "", Split = "none";
        public bool CheckExports, Long64, AllHeaders, EnumsFromC = true, AutoFlags = true;
    }

    /// <summary>With <c>--timings</c>: how long each step took, printed as it ends.</summary>
    private static System.Diagnostics.Stopwatch? clock;

    private static void Mark(string step)
    {
        if (clock is null)
            return;
        Console.Error.WriteLine($"  {step}: {clock.ElapsedMilliseconds} ms");
        clock.Restart();
    }

    public static int Run(string profilePath, List<string> inputs, List<string> includes, string? output, string? source, string? className, string? core, bool dryRun, bool timings = false)
    {
        clock = timings ? System.Diagnostics.Stopwatch.StartNew() : null;
        profilePath = Path.GetFullPath(profilePath);
        if (!File.Exists(profilePath) && !Directory.Exists(profilePath))
        {
            Console.Error.WriteLine($"larva: {profilePath} not found");
            return 1;
        }
        // A profile is a source file, or a folder of them (a profile with helpers of its own).
        string folder = File.Exists(profilePath) ? Path.GetDirectoryName(profilePath)! : profilePath;
        var profileSources = File.Exists(profilePath) ? [profilePath] : Directory.GetFiles(profilePath).Where(LanguageRegistry.IsSource).Order(StringComparer.Ordinal).ToList();
        if (SourceBuild.Language(profileSources) is not { } language)
            return 1;
        if (SourceBuild.CoreLibrary(core, noCache: false, out _, out string? corePath) is not { } coreBytes)
            return 1;
        var coreReference = SourceBuild.CoreReference(coreBytes, corePath);
        if (BindingsLibrary(coreBytes, out string? bindingsPath) is not { } bindingsBytes)
            return 1;
        var bindingsReference = bindingsPath is not null ? LanguagePack.Reference(bindingsPath) : LanguagePack.Reference(bindingsBytes);

        // The profile's class: the one type of its sources that is an IBindingProfile (or --class).
        var alone = SourceBuild.Compile(language, profileSources, "BindingProfile", [coreReference, bindingsReference], library: true);
        if (alone?.Assembly is not { } profileBytes)
            return 1;
        var catalog = HirCatalog.Create(H.Encoding.LvaFormat.Read(profileBytes), H.Encoding.LvaFormat.Read(coreBytes), H.Encoding.LvaFormat.Read(bindingsBytes));
        var profileReference = LanguagePack.Reference(profileBytes);
        var profiles = catalog.AllTypes.Where(t => t.Assembly.Index == 0 && !t.IsInterface && !t.IsHidden && (t.Definition.Flags & H.TypeFlags.Abstract) == 0
            && t.AllInterfaces().Any(i => i.FullName == "System.Bindings.IBindingProfile")).ToList();
        if (className is not null)
            profiles = profiles.Where(t => t.Name == className || t.FullName == className).ToList();
        if (profiles.Count != 1)
        {
            Console.Error.WriteLine(profiles.Count == 0
                ? $"larva: {Path.GetFileName(profilePath)} has no class that is an IBindingProfile{(className is null ? "" : $" named {className}")} (derive one from System.Bindings.BindingProfile)"
                : $"larva: {Path.GetFileName(profilePath)} has {profiles.Count} profiles ({string.Join(", ", profiles.Select(p => p.FullName))}): name one with --class");
            return 1;
        }
        var profile = profiles[0];
        if (profile.Access != H.Access.Public)
        {
            Console.Error.WriteLine($"larva: the profile {profile.FullName} is not public: larva makes it from outside its library");
            return 1;
        }
        string work = Path.Combine(Path.GetTempPath(), $"larva-bind-{Guid.NewGuid():N}");
        // The profile is compiled once (a library); each step is a small program against it, run on the VM.
        bool RunStep(Func<string, string> glue, string name) =>
            RunProgram(language, glue(Path.Combine(work, name + ".failed")), Path.Combine(work, name + Extension(language)), Path.Combine(work, name + ".failed"),
                [coreBytes, bindingsBytes, profileBytes], [coreReference, bindingsReference, profileReference]);
        Directory.CreateDirectory(work);
        try
        {
            // 1. Configure: what to read, where it goes.
            string settingsFile = Path.Combine(work, "settings.txt");
            if (!RunStep(failure => Glue(profile, $"BindingRunner.WriteSettings(s, {Literal(settingsFile)});", failure), "configure"))
                return 1;
            var settings = ReadSettings(File.ReadAllText(settingsFile), folder);
            // The command line on top: libraries and headers by extension, -I folders.
            var libraries = inputs.Where(i => !i.EndsWith(".h", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFullPath).ToList();
            var headers = inputs.Where(i => i.EndsWith(".h", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFullPath).ToList();
            if (libraries.Count > 0)
                settings.Libraries = libraries;
            if (headers.Count > 0)
                settings.Headers = headers;
            settings.Includes.AddRange(includes.Select(Path.GetFullPath));
            if (settings.Libraries.Count == 0)
            {
                Console.Error.WriteLine("larva: no native library (the profile's `Libraries`, or a .dll/.so on the command line)");
                return 1;
            }
            string library = Resolve(settings.Libraries[0]);
            string libraryName = Path.GetFileName(settings.Libraries[0]);
            if (settings.Class.Length == 0)
                settings.Class = Writable(Path.GetFileNameWithoutExtension(libraryName));
            if (settings.Assembly.Length == 0)
                settings.Assembly = settings.Class + "Bindings";

            // 2. The headers, as the profile's model.
            Mark("configure");
            var model = HeaderModel.Read(settings.Headers, libraryName, File.Exists(library) ? library : null, settings.Includes,
                settings.Long64, settings.EnumsFromC, settings.AutoFlags, settings.AllHeaders, settings.CheckExports);
            // What the binding lacks: functions the library exports that could not be bound (variadic, a struct by
            // value…). The rest of what the headers declare (the C runtime's, other platforms') is only counted.
            var exported = File.Exists(library) ? new HashSet<string>(Exports.Read(library), StringComparer.Ordinal) : null;
            int others = 0;
            foreach (var (name, why) in model.Reader.Skipped.Where(s => !s.Name.StartsWith('_')))
            {
                if (exported is null || exported.Contains(name))
                    Console.Error.WriteLine($"larva: {name} is left out: {why}");
                else
                    others++;
            }
            if (model.NotExported.Count + others > 0)
                Console.Error.WriteLine($"larva: {model.NotExported.Count + others} declaration(s) of the headers that {libraryName} does not export, left out");

            // 3. The profile over the model: every hook, then the description it leaves.
            string described = Path.Combine(work, "binding.bind");
            var overrides = new StringBuilder();
            overrides.Append("s.Libraries.Clear();\n");
            foreach (string l in settings.Libraries)
                overrides.Append($"s.Libraries.Add({Literal(l)});\n");
            overrides.Append($"s.Class = {Literal(settings.Class)};\ns.Assembly = {Literal(settings.Assembly)};\n");
            Mark("headers read");
            string modelFile = Path.Combine(work, "model.txt");
            File.WriteAllText(modelFile, ModelText(model));
            string body = overrides + $"BindingModel m = BindingModel.Read(s, {Literal(modelFile)});\n" + $"BindingRunner.Run(p, m, {Literal(described)});";
            if (!RunStep(failure => Glue(profile, body, failure), "binding"))
                return 1;
            string descriptionText = File.ReadAllText(described);
            Mark("profile run");
            var description = BindingDescription.Parse(descriptionText);

            // The files are stems: the language of the source gives their extension.
            string extension = language.Extensions.FirstOrDefault() ?? "";
            BindingGenerator.SourceExtension = extension;
            Place(description, extension);
            var problems = Clashes(description);
            foreach (string problem in problems)
                Console.Error.WriteLine($"{profilePath}: error: {problem}");
            if (problems.Count > 0)
                return 1;
            if (dryRun)
            {
                Console.Write(descriptionText);
                Console.WriteLine($"// {description.Functions.Count} function(s), {description.Structs.Count} struct(s), {description.Enums.Count} enum(s), {description.Constants.Count} constant(s); nothing written (--dry-run)");
                return 0;
            }

            // 4. The library and its source.
            BindCommand.Reserve(language);
            var assembly = BindingGenerator.Build(description);
            output ??= settings.Output.Length > 0 ? settings.Output : BindCommand.DefaultOutput(folder, description.Assembly, description.Library);
            if (BindCommand.Refused(output, description.Library) is { } refusal)
            {
                Console.Error.WriteLine($"larva: {refusal}");
                return 1;
            }
            LvaFormat.Save(assembly, output);
            Mark("library written");
            Console.WriteLine($"{output}: {description.Functions.Count} function(s), {description.Structs.Count} struct(s), {description.Enums.Count} enum(s), {description.Constants.Count} constant(s)");
            // A library of the profile's (not the system's): programs find it next to them.
            if (File.Exists(library) && !Path.GetFullPath(library).StartsWith(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"programs that use it load {libraryName}: a project copies it next to its program with `lib: {Path.GetRelativePath(Directory.GetCurrentDirectory(), library).Replace('\\', '/')}` in its DEPS chunk");
            string? sourceFolder = source is not null ? Path.GetFullPath(source) : settings.Source.Length > 0 ? settings.Source : null;
            if (sourceFolder is not null)
            {
                Directory.CreateDirectory(sourceFolder);
                var texts = BindCommand.Write(language, assembly, [coreReference], BindingGenerator.Files(description), description.Class + extension);
                if (texts is null)
                    return 1;
                foreach (var (file, text) in texts)
                    File.WriteAllText(Path.Combine(sourceFolder, file), text);
                Console.WriteLine($"{sourceFolder}: {texts.Count} {language.Name} file(s)");
            }
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ── System.Bindings ────────────────────────────────

    /// <summary>
    /// System.Bindings: the compiled library next to larva (an installation), or the sources of
    /// <c>Library/System.Bindings</c> above it (a working copy), compiled once against System.Core and cached.
    /// </summary>
    internal static byte[]? BindingsLibrary(byte[] coreBytes, out string? path, bool quiet = false)
    {
        path = null;
        string installed = Path.Combine(AppContext.BaseDirectory, "System.Bindings.dll");
        if (File.Exists(installed))
        {
            path = installed;
            return LvaContainer.Unwrap(File.ReadAllBytes(installed));
        }
        string? folder = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && folder is null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Library", "System.Bindings");
            if (Directory.Exists(candidate))
                folder = candidate;
        }
        if (folder is null)
        {
            if (!quiet)
                Console.Error.WriteLine("larva: System.Bindings not found (System.Bindings.dll next to larva); reinstall Larva");
            return null;
        }
        var files = Directory.GetFiles(folder).Where(LanguageRegistry.IsSource).Order(StringComparer.Ordinal).ToArray();
        if (LanguageRegistry.For(files, out var problem) is not { } language)
        {
            if (!quiet)
                Console.Error.WriteLine($"larva: {problem}");
            return null;
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{language.Id}|{language.Identity}|{LvaFormat.Version}"));
        hash.AppendData(SHA256.HashData(coreBytes));
        foreach (var file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file)));
            hash.AppendData(File.ReadAllBytes(file));
        }
        string key = Convert.ToHexString(hash.GetHashAndReset())[..16];
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Larva", "cache", $"System.Bindings-{key}.lva");
        if (File.Exists(cache))
        {
            path = cache;
            return File.ReadAllBytes(cache);
        }
        var compiled = SourceBuild.Compile(language, files, "System.Bindings", [SourceBuild.CoreReference(coreBytes, null)], library: true, quiet: quiet);
        if (compiled?.Assembly is not { } bytes)
            return null;
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllBytes(cache, bytes);
        path = cache;
        return bytes;
    }

    // ── Running the profile ────────────────────────────

    private static string Extension(LanguagePack language) => language.Extensions.FirstOrDefault() ?? ".ant";

    /// <summary>
    /// <paramref name="glue"/> compiled against <paramref name="references"/>, linked with <paramref name="libraries"/>, and
    /// run: as native code in memory (x64 on Windows: no linker, no file written), else on the VM. What the profile threw
    /// comes back in <paramref name="failure"/> (the glue catches it and writes it there).
    /// </summary>
    private static unsafe bool RunProgram(LanguagePack language, string glue, string gluePath, string failure, List<byte[]> libraries, List<JsonObject> references)
    {
        var compiled = SourceBuild.Compile(language, [gluePath], "BindingRun", references, texts: new Dictionary<string, string> { [gluePath] = glue });
        if (compiled?.Assembly is not { } programBytes)
            return false;
        Mark("  compiled");
        var linked = HirLinker.Link("BindingRun", [.. libraries, programBytes]);
        if (!linked.Success || linked.Assembly!.EntryPoint is not { } entry)
        {
            foreach (var error in linked.Errors)
                Console.Error.WriteLine($"larva: {error}");
            return false;
        }
        var lowered = HirLowerer.Lower(linked.Assembly, new LoweringOptions { StaticConstructorsFromEntry = true });
        if (!lowered.Success)
        {
            foreach (var error in lowered.Errors)
                Console.Error.WriteLine($"larva: {error}");
            return false;
        }
        Mark("  linked and lowered");
        string function = lowered.FunctionName(entry);
        // The libraries were checked when they were built; the program is a few lines.
        var options = Larva.IR.CompilationOptions.Default with { Entry = function, BorrowCheck = Larva.IR.BorrowCheckMode.Off };
        if (OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64)
        {
            var verified = Larva.IR.Compiler.CompileObject(lowered.Module!, options);
            var program = verified.Success ? Larva.IR.Linking.Linker.Link([verified.Object!], new Larva.IR.Linking.LinkOptions { Entry = function, Compilation = options }) : null;
            if (program is not { Success: true })
            {
                foreach (var error in verified.TypeErrors)
                    Console.Error.WriteLine($"larva: {error}");
                foreach (var error in program?.Errors ?? [])
                    Console.Error.WriteLine($"larva: {error}");
                return false;
            }
            Larva.Native.NativeRuntime.Reset();
            var native = Larva.X64.X64Backend.Compile(program, new Larva.X64.X64Options { Compilation = options });
            if (!native.Success)
            {
                Console.Error.WriteLine($"larva: {native}");
                return false;
            }
            Mark("  compiled to x64");
            int code;
            using (var image = native.Program!.Load())
                code = ((delegate* unmanaged<int>)image.Address(program.Entry!))();
            Larva.Native.NativeRuntime.Reset();
            Mark("  run");
            return Succeeded(code, failure);
        }
        var vm = Larva.IR.Compiler.Compile(lowered.Module!, options with { Optimize = false });
        if (!vm.Success)
        {
            foreach (var error in vm.TypeErrors)
                Console.Error.WriteLine($"larva: {error}");
            return false;
        }
        try
        {
            Mark("  compiled for the VM");
            object? result = new Interpreter(vm.Il!, options: new InterpreterOptions { StepLimit = long.MaxValue }).Run(function);
            Mark("  run on the VM");
            return Succeeded(Convert.ToInt32(result ?? 0, CultureInfo.InvariantCulture), failure);
        }
        catch (Exception e) when (e is UnhandledException or VmException)
        {
            Console.Error.WriteLine($"larva: the profile failed: {e.Message}");
            return false;
        }
    }

    /// <summary>The glue's answer: 0, or what the profile threw (in <paramref name="failure"/>).</summary>
    private static bool Succeeded(int code, string failure)
    {
        if (code == 0)
            return true;
        Console.Error.WriteLine($"larva: the profile failed: {(File.Exists(failure) ? File.ReadAllText(failure).Trim() : $"it ended with {code}")}");
        return false;
    }

    /// <summary>
    /// A program that makes the profile and the settings, then does <paramref name="body"/>: 0 when it ends, 1 with what
    /// was thrown written to <paramref name="failure"/>.
    /// </summary>
    private static string Glue(CatalogType profile, string body, string failure)
    {
        var text = new StringBuilder();
        text.Append("import System;\nimport System.IO;\nimport System.Bindings;\n");
        if (profile.Namespace.Length > 0)
            text.Append("import ").Append(profile.Namespace).Append(";\n");
        text.Append("\ninto namespace LarvaBindRun\n{\n\tMain() : i32\n\t{\n\t\ttry\n\t\t{\n");
        text.Append("\t\t\tBindingSettings s = new BindingSettings();\n");
        text.Append("\t\t\tIBindingProfile p = new ").Append(profile.Name).Append("();\n");
        text.Append("\t\t\tp.Configure(s);\n");
        foreach (string line in body.Split('\n'))
            text.Append("\t\t\t").Append(line).Append('\n');
        text.Append("\t\t}\n\t\tcatch (Exception e)\n\t\t{\n\t\t\tFile.WriteAllText(").Append(Literal(failure)).Append(", e.ToString());\n\t\t\treturn 1;\n\t\t}\n");
        text.Append("\t\treturn 0;\n\t}\n}\n");
        return text.ToString();
    }

    /// <summary>
    /// The model as System.Bindings reads it (<c>BindingModel.Read</c>): a declaration a line, its fields between tabs
    /// (`E` enum, `M` member, `T` typedef; `S` struct, `F` field; `C` constant; `U` function, `P` parameter).
    /// </summary>
    private static string ModelText(HeaderModel model)
    {
        var text = new StringBuilder();
        void Line(params string[] fields) => text.AppendJoin('\t', fields.Select(f => f.Replace('\t', ' ').Replace('\n', ' '))).Append('\n');
        string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
        var description = model.Description;
        foreach (var e in model.Enums)
        {
            Line("E", e.Name, e.File ?? "", e.Underlying, e.IsFlags ? "1" : "0");
            foreach (var (name, value) in e.Members)
                Line("M", name, Number(value));
            foreach (string typedef in e.Typedefs)
                Line("T", typedef);
        }
        foreach (var s in description.Structs)
        {
            Line("S", s.Name, model.Origin(s.Name) ?? "", s.IsUnion ? "1" : "0", s.Pack.ToString(CultureInfo.InvariantCulture));
            foreach (var (name, type) in s.Fields)
                Line("F", name, type, model.Typedef($"{s.Name}.{name}") ?? "");
        }
        foreach (var c in description.Constants)
            Line("C", c.Name, model.Origin(c.Name) ?? "", Number(c.Value));
        foreach (var f in description.Functions)
        {
            Line("U", f.Entry, model.Origin(f.Entry) ?? "", f.Result, model.Typedef($"{f.Entry}#result") ?? "");
            for (int i = 0; i < f.Parameters.Count; i++)
                Line("P", f.Parameters[i].Name, f.Parameters[i].Type, model.Typedef($"{f.Entry}#{i}") ?? "");
        }
        return text.ToString();
    }

    /// <summary>A string literal: quotes and backslashes escaped.</summary>
    private static string Literal(string text)
    {
        char backslash = (char)92;
        var result = new StringBuilder("\"");
        foreach (char c in text)
        {
            if (c == '"' || c == backslash)
                result.Append(backslash);
            result.Append(c);
        }
        return result.Append('"').ToString();
    }

    private static Settings ReadSettings(string text, string folder)
    {
        var settings = new Settings();
        string Full(string path) => Path.GetFullPath(Path.Combine(folder, path));
        foreach (string raw in text.Split('\n'))
        {
            int eq = raw.IndexOf('=');
            if (eq < 0)
                continue;
            string key = raw[..eq], value = raw[(eq + 1)..].TrimEnd('\r');
            switch (key)
            {
                case "library":
                    // A name alone (`kernel32.dll`) is looked up in the system's folder; a path is the profile's.
                    settings.Libraries.Add(value.Contains('/') || value.Contains('\\') || File.Exists(Full(value)) ? Full(value) : value);
                    break;
                case "header": settings.Headers.Add(Full(value)); break;
                case "include": settings.Includes.Add(Full(value)); break;
                case "namespace": settings.Namespace = value; break;
                case "class": settings.Class = value; break;
                case "assembly": settings.Assembly = value; break;
                case "output": settings.Output = value.Length > 0 ? Full(value) : ""; break;
                case "source": settings.Source = value.Length > 0 ? Full(value) : ""; break;
                case "split": settings.Split = value; break;
                case "exports": settings.CheckExports = value == "check"; break;
                case "long": settings.Long64 = value == "64"; break;
                case "all": settings.AllHeaders = value == "true"; break;
                case "enums": settings.EnumsFromC = value == "true"; break;
                case "flags": settings.AutoFlags = value == "true"; break;
            }
        }
        return settings;
    }

    /// <summary>The description's file stems with the language's extension.</summary>
    private static void Place(BindingDescription description, string extension)
    {
        string? File(string? stem) => stem is null or "" ? null : stem.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? stem : stem + extension;
        for (int i = 0; i < description.Functions.Count; i++)
            description.Functions[i] = description.Functions[i] with { File = File(description.Functions[i].File) };
        for (int i = 0; i < description.Constants.Count; i++)
            description.Constants[i] = description.Constants[i] with { File = File(description.Constants[i].File) };
        for (int i = 0; i < description.Structs.Count; i++)
            description.Structs[i] = description.Structs[i] with { File = File(description.Structs[i].File) };
        for (int i = 0; i < description.Enums.Count; i++)
            description.Enums[i] = description.Enums[i] with { File = File(description.Enums[i].File) };
    }

    /// <summary>Names the profile gave twice where one is needed: two types of a namespace, two members of a class.</summary>
    private static List<string> Clashes(BindingDescription d)
    {
        var problems = new List<string>();
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        void Type(string ns, string name, string what)
        {
            string full = $"{ns}.{name}";
            if (types.TryGetValue(full, out var other))
                problems.Add($"{what} and {other} are both `{full}`");
            else
                types[full] = what;
        }
        foreach (var e in d.Enums)
            Type(e.Namespace ?? d.Namespace, e.Name, $"the enum `{e.Name}`");
        foreach (var s in d.Structs)
            Type(s.Namespace ?? d.Namespace, s.Name, $"the struct `{s.Name}`");
        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        void Member(string? ns, string? cls, string name, string what)
        {
            string full = $"{ns ?? d.Namespace}.{cls ?? d.Class}.{name}";
            if (members.TryGetValue(full, out var other))
                problems.Add($"{what} and {other} are both `{full}`");
            else
                members[full] = what;
        }
        foreach (var c in d.Constants)
            Member(c.Namespace, c.Class, c.Name, $"a constant (`{c.Name}`)");
        foreach (var f in d.Functions)
            Member(f.Namespace, f.Class, f.Name, $"`{f.Entry}`");
        return problems;
    }

    /// <summary>A library name as a class name: what is not a letter or digit becomes `_`.</summary>
    private static string Writable(string name)
    {
        var text = new string([.. name.Select(c => char.IsLetterOrDigit(c) ? c : '_')]);
        return text.Length > 0 && char.IsDigit(text[0]) ? "_" + text : text;
    }

    private static string Resolve(string library)
    {
        if (File.Exists(library))
            return Path.GetFullPath(library);
        if (OperatingSystem.IsWindows() && File.Exists(Path.Combine(Environment.SystemDirectory, library)))
            return Path.Combine(Environment.SystemDirectory, library);
        return library;
    }
}

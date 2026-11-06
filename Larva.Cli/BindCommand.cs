using System.Text.Json.Nodes;
using Larva.Bindings;
using Larva.Hir.Encoding;

/// <summary>
/// <c>larva bind &lt;library&gt; &lt;api.bind&gt; [-o &lt;Name.dll&gt;] [--source Name.ext] [--core &lt;dir&gt;]</c>: a library's exports
/// checked against a description, written as a Larva library of [Import] declarations (a <c>.dll</c> carrying the assembly,
/// or a bare <c>.lva</c>; and, with <c>--source</c>, as source in the language of the file's extension, by its pack).
/// <c>larva bind --list &lt;library&gt;</c> prints what the library exports.
/// </summary>
internal static class BindCommand
{
    public const string Usage = """
               larva bind <lib.dll|lib.so> <api.bind|api.h> [-o <Name.dll>] [--source Name.ext] [--describe api.bind] [--no-pp] [-I <dir>]... [--class Name] [--core <dir>]
               larva bind --list <lib.dll|lib.so>
               larva bind --profile <Profile.ant|folder> [<lib.dll|lib.so>...] [<api.h>...] [-I <dir>]... [-o <Name.dll>] [--source <folder>] [--class Name] [--dry-run] [--timings] [--core <dir>]
                          (a binding decided by a class that implements System.Bindings.IBindingProfile)
        """;

    public static int Run(string[] args)
    {
        string? output = null, source = null, core = null, describe = null, className = null, profile = null;
        bool list = false, preprocess = true, dryRun = false, timings = false;
        var includes = new List<string>();
        var inputs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" when i + 1 < args.Length: output = args[++i]; break;
                case "--source" or "--ant" when i + 1 < args.Length: source = args[++i]; break;
                case "--profile" when i + 1 < args.Length: profile = args[++i]; break;
                case "--dry-run": dryRun = true; break;
                case "--timings": timings = true; break;
                case "--core" when i + 1 < args.Length: core = args[++i]; break;
                case "--list": list = true; break;
                case "--describe" when i + 1 < args.Length: describe = args[++i]; break;
                case "--no-pp": preprocess = false; break;
                case "-I" when i + 1 < args.Length: includes.Add(Path.GetFullPath(args[++i])); break;
                case "--class" when i + 1 < args.Length: className = args[++i]; break;
                default: inputs.Add(args[i]); break;
            }
        }
        try
        {
            if (list)
            {
                if (inputs.Count != 1)
                {
                    Console.Error.WriteLine(Usage);
                    return 2;
                }
                foreach (string name in Exports.Read(Resolve(inputs[0])))
                    Console.WriteLine(name);
                return 0;
            }
            if (profile is not null)
                return BindProfile.Run(profile, inputs, includes, output, source, className, core, dryRun, timings);
            if (inputs.Count != 2)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }
            string library = Resolve(inputs[0]);
            string descriptionText = File.ReadAllText(inputs[1]);
            if (inputs[1].EndsWith(".h", StringComparison.OrdinalIgnoreCase))
            {
                // A C header: its prototypes, typedefs, enums and #defines, preprocessed by clang when it is installed.
                var reader = new HeaderReader();
                descriptionText = reader.ReadFile(inputs[1], Path.GetFileName(inputs[0]), className, preprocess, includes);
                foreach (var (name, why) in reader.Skipped)
                    Console.Error.WriteLine($"larva: skipped {name}: {why}");
                foreach (var (name, why) in reader.Opaque)
                    Console.Error.WriteLine($"larva: opaque {name}: {why}");
            }
            var description = BindingDescription.Parse(descriptionText);
            var exports = File.Exists(library) ? Exports.Read(library) : null;
            if (exports is null)
                Console.Error.WriteLine($"larva: {inputs[0]} not found: the entries are not checked against its exports");
            else if (inputs[1].EndsWith(".h", StringComparison.OrdinalIgnoreCase))
            {
                // A header declares more than the library may export (other platforms, inline helpers): those are left out.
                var missing = description.KeepExported(exports);
                if (missing.Count > 0)
                    Console.Error.WriteLine($"larva: {missing.Count} declared function(s) not exported by {Path.GetFileName(library)}, left out: {string.Join(", ", missing.Take(8))}{(missing.Count > 8 ? ", …" : "")}");
            }
            if (describe is not null)
                File.WriteAllText(describe, descriptionText);
            // Names that are a keyword of the language the source is written in get a `_` (the C symbol keeps its own).
            LanguagePack? sourceLanguage = null;
            if (source is not null && (sourceLanguage = LanguageRegistry.ForFile(source)) is null)
            {
                Console.Error.WriteLine($"larva: {source}: no installed language writes {Path.GetExtension(source)} files ({LanguageRegistry.Installed()})");
                return 1;
            }
            Reserve(sourceLanguage);
            var assembly = BindingGenerator.Build(description, exports);
            output ??= DefaultOutput(Directory.GetCurrentDirectory(), description.Assembly, library);
            if (Refused(output, library) is { } refusal)
            {
                Console.Error.WriteLine($"larva: {refusal}");
                return 1;
            }
            LvaFormat.Save(assembly, output);
            Console.WriteLine($"{output}: {description.Class} with {description.Functions.Count} function(s) of {description.Library}{(description.Structs.Count > 0 ? $", {description.Structs.Count} struct(s)" : "")}");
            if (source is not null)
            {
                var references = new List<JsonObject>();
                if (assembly.References.Count > 0)
                {
                    if (SourceBuild.CoreLibrary(core, noCache: false, out _, out string? corePath) is not { } coreBytes)
                        return 1;
                    references.Add(SourceBuild.CoreReference(coreBytes, corePath));
                }
                if (Write(sourceLanguage!, assembly, references, null, "") is not { } texts)
                    return 1;
                File.WriteAllText(source, texts[""]);
                Console.WriteLine($"{source}: {sourceLanguage!.Name} source");
            }
            return 0;
        }
        catch (LanguagePackException e)
        {
            Console.Error.WriteLine($"larva: {e.Message}");
            return 1;
        }
        catch (BindingException e)
        {
            Console.Error.WriteLine($"larva: {e.Message}");
            return 1;
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"larva: {e.Message}");
            return 1;
        }
    }

    /// <summary>The bindings keep clear of every installed language's keywords, or of <paramref name="language"/>'s.</summary>
    internal static void Reserve(LanguagePack? language)
    {
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pack in language is null ? LanguageRegistry.All : [language])
        {
            try
            {
                reserved.UnionWith(pack.Keywords);
            }
            catch (LanguagePackException e) when (language is null)
            {
                Console.Error.WriteLine($"larva: {e.Message}");
            }
        }
        BindingGenerator.Reserved = reserved;
    }

    /// <summary>
    /// The assembly written as <paramref name="language"/>'s source by its pack, one text per file (<paramref name="fileOf"/>
    /// places types by full name; the rest go to <paramref name="defaultFile"/>): null when the pack could not.
    /// </summary>
    internal static Dictionary<string, string>? Write(LanguagePack language, Larva.Hir.HirAssembly assembly, IEnumerable<JsonObject> references,
        IReadOnlyDictionary<string, string>? fileOf, string defaultFile, bool nonPublic = false, bool header = true)
    {
        var files = new JsonObject();
        foreach (var (type, file) in fileOf ?? new Dictionary<string, string>())
            files[type] = file;
        JsonNode? result;
        try
        {
            result = language.Call("source", new JsonObject
            {
                ["assembly"] = LanguagePack.Reference(LvaFormat.Write(assembly)),
                ["references"] = new JsonArray([.. references.Select(r => (JsonNode)(r.Parent is null ? r : r.DeepClone()))]),
                ["files"] = files,
                ["defaultFile"] = defaultFile,
                ["nonPublic"] = nonPublic,
                ["header"] = header,
            });
        }
        catch (LanguagePackException e)
        {
            Console.Error.WriteLine($"larva: {e.Message}");
            return null;
        }
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, text) in result?["files"] as JsonObject ?? [])
            texts[file] = text!.GetValue<string>();
        return texts;
    }

    /// <summary>
    /// Where the bindings go when no <c>-o</c> says: <c>Name.dll</c> in <paramref name="folder"/>, or
    /// <c>Name.Bindings.dll</c> when that is the native library's own name (see <see cref="Refused"/>).
    /// </summary>
    internal static string DefaultOutput(string folder, string assembly, string? native)
    {
        string name = assembly + ".dll";
        if (native is not null && string.Equals(name, NativeFileName(native), StringComparison.OrdinalIgnoreCase))
            name = assembly + ".Bindings.dll";
        return Path.Combine(folder, name);
    }

    /// <summary>The native library's file name as Windows looks it up (<c>SDL3</c> is <c>SDL3.dll</c>).</summary>
    private static string NativeFileName(string native)
    {
        string file = Path.GetFileName(native);
        return Path.HasExtension(file) ? file : file + ".dll";
    }

    /// <summary>
    /// Why the bindings cannot be written to <paramref name="output"/>, or null. A Larva library is a DLL with no code:
    /// with the native library's file name, Windows would load it in the native one's place (next to a program, or in
    /// the folder it runs in); and a DLL that is not a Larva library already there is someone's code, not overwritten.
    /// </summary>
    internal static string? Refused(string output, string? native)
    {
        string file = Path.GetFileName(output);
        if (!file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return null;
        if (native is not null && string.Equals(file, NativeFileName(native), StringComparison.OrdinalIgnoreCase))
            return $"{output}: the bindings would have the native library's name, and Windows would load them in its place; name them otherwise (such as {Path.GetFileNameWithoutExtension(file)}.Bindings.dll)";
        if (File.Exists(output) && !Larva.Build.QueenProject.IsAssembly(output))
            return $"{output} is a DLL that is not a Larva library: it is not overwritten";
        return null;
    }

    /// <summary>A library by path, or by name in the system's directory (<c>kernel32.dll</c>).</summary>
    private static string Resolve(string library)
    {
        if (File.Exists(library))
            return Path.GetFullPath(library);
        if (OperatingSystem.IsWindows())
        {
            string system = Path.Combine(Environment.SystemDirectory, library);
            if (File.Exists(system))
                return system;
        }
        else
        {
            foreach (string folder in new[] { "/lib/x86_64-linux-gnu", "/usr/lib/x86_64-linux-gnu", "/lib64", "/usr/lib", "/lib" })
            {
                string candidate = Path.Combine(folder, library);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return library;
    }
}

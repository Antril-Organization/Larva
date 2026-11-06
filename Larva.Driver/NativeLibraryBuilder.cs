using Larva.IR;
using Larva.IR.Linking;
using Larva.IR.Lowering;
using Larva.Llvm;
using Larva.Native;
using Larva.X64;

namespace Larva.Driver
{
    /// <summary>A Larva DLL a program can load: its module (namespace), file and the ids it needs (from its <c>.lvo</c>).</summary>
    public sealed record NativeLibrary(LarvaObject Object, string Dll)
    {
        public string Module => Object.Module.Name;
    }

    /// <summary>
    /// Native libraries: one DLL per library module (a namespace), sharing <see cref="Toolchain.RuntimeDll"/>, and
    /// programs that load them (see <see cref="NativeImages"/> for what each image contains).
    /// </summary>
    public static class NativeLibraryBuilder
    {
        /// <summary>
        /// Builds the DLL of each module of <paramref name="own"/> into <paramref name="folder"/> (<c>NAME.dll</c>), with
        /// <paramref name="others"/> (libraries they use, already built or not) as the DLLs they call. Returns the objects
        /// with their id manifests filled in: save them as the <c>.lvo</c> files that go with the DLLs.
        /// </summary>
        public static (IReadOnlyList<LarvaObject>? Objects, string Message) BuildLibraries(IReadOnlyList<LarvaObject> own, IReadOnlyList<LarvaObject> others,
            string folder, ExecutableOptions? options = null)
        {
            options ??= new ExecutableOptions();
            var toolchain = Toolchain.Find();
            if (toolchain is null)
                return (null, Toolchain.Missing);
            var linked = Linker.Link([.. own, .. others], new LinkOptions { Library = true, Compilation = options.Compilation });
            if (!linked.Success)
                return (null, string.Join("\n", linked.Errors));
            Directory.CreateDirectory(folder);
            string work = Scratch();
            try
            {
                var (runtimeOk, runtimeLog) = toolchain.BuildRuntime(work);
                if (!runtimeOk)
                    return (null, $"building the runtime failed\n{runtimeLog}");
                var all = own.Concat(others).ToList();
                var imports = ImportLibraries(toolchain, all, work, out string? importError);
                if (imports is null)
                    return (null, importError!);
                var result = new List<LarvaObject>();
                var sizes = new List<string>();
                foreach (var library in own)
                {
                    string module = library.Module.Name;
                    var foreign = all.Select(o => o.Module.Name).Where(m => m != module).ToHashSet();
                    var prepared = NativeImages.Prepare(linked, foreign, module);
                    var slots = new IdSlots();
                    var concrete = Monomorphizer.Run(prepared.Merged!, GenericsMode.Specialize, prepared.Roots, slots);
                    (concrete, var manifest) = NativeImages.AddIdSetter(concrete, slots, prepared.Merged!, module);
                    string obj = Path.Combine(work, $"{module}.obj");
                    if (Compile(concrete, obj, options, module) is { } error)
                        return (null, $"{module}: {error}");
                    var defined = concrete.Functions.Where(f => !f.IsDeclaration).Select(f => f.Name).ToHashSet();
                    var exports = NativeImages.Exports(library.Module).Where(defined.Contains).Select(Symbol).ToList();
                    string dll = Path.Combine(folder, $"{module}.dll");
                    var (linkOk, linkLog) = toolchain.LinkLibrary([obj], dll, exports, Path.Combine(work, "larva_rt.lib"),
                        [.. foreign.Where(imports.ContainsKey).Select(m => imports[m])]);
                    if (!linkOk)
                        return (null, $"{module}: linking the DLL failed\n{linkLog}");
                    sizes.Add($"{module}.dll {new FileInfo(dll).Length} bytes");
                    result.Add(library with { NativeIds = manifest });
                }
                File.Copy(Path.Combine(work, Toolchain.RuntimeDll), Path.Combine(folder, Toolchain.RuntimeDll), overwrite: true);
                return (result, string.Join(", ", sizes));
            }
            finally
            {
                TryDelete(work);
            }
        }

        /// <summary>
        /// A program that loads <paramref name="libraries"/> (their DLLs, copied next to it with the runtime) instead of
        /// containing their code. <paramref name="linked"/> is the program linked with every library object.
        /// </summary>
        public static (bool Success, string Message) BuildProgram(LinkResult linked, IReadOnlyList<NativeLibrary> libraries, string output,
            ExecutableOptions? options = null)
        {
            options ??= new ExecutableOptions();
            var toolchain = Toolchain.Find();
            if (toolchain is null)
                return (false, Toolchain.Missing);
            foreach (var library in libraries.Where(l => l.Object.NativeIds is null || !File.Exists(l.Dll)))
                return (false, $"{library.Module}: no DLL built from this .lvo (build the library for a native back end)");
            var foreign = libraries.Select(l => l.Module).ToHashSet();
            LinkResult program;
            try
            {
                var prepared = NativeImages.Prepare(linked, foreign, own: null);
                (prepared, int init) = NativeImages.AddIdInit(prepared, libraries.ToDictionary(l => l.Module, l => l.Object.NativeIds!));
                program = ExecutableEntry.AddWrappers(prepared, init);
            }
            catch (InvalidOperationException e)
            {
                return (false, e.Message);
            }
            string work = Scratch();
            try
            {
                var (runtimeOk, runtimeLog) = toolchain.BuildRuntime(work);
                if (!runtimeOk)
                    return (false, $"building the runtime failed\n{runtimeLog}");
                var imports = ImportLibraries(toolchain, [.. libraries.Select(l => l.Object)], work, out string? importError);
                if (imports is null)
                    return (false, importError!);
                var concrete = Monomorphizer.Run(program.Merged!, GenericsMode.Specialize, program.Roots);
                string obj = Path.Combine(work, "program.obj");
                if (Compile(concrete, obj, options, "program") is { } error)
                    return (false, error);
                var (ok, log) = toolchain.LinkProgram([obj], output, Path.Combine(work, "larva_rt.lib"), [.. imports.Values]);
                if (!ok)
                    return (false, $"linking failed\n{log}");
                // What the program loads goes next to it.
                string folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
                File.Copy(Path.Combine(work, Toolchain.RuntimeDll), Path.Combine(folder, Toolchain.RuntimeDll), overwrite: true);
                foreach (var library in libraries)
                {
                    string target = Path.Combine(folder, Path.GetFileName(library.Dll));
                    if (!string.Equals(Path.GetFullPath(library.Dll), target, StringComparison.OrdinalIgnoreCase))
                        File.Copy(library.Dll, target, overwrite: true);
                }
                string backend = options.Backend == Backend.X64 ? "x64 back end" : $"llvm back end, O{options.OptimizationLevel}";
                return (true, $"{output} ({backend}, entry {linked.Entry}; loads {string.Join(", ", libraries.Select(l => Path.GetFileName(l.Dll)).Prepend(Toolchain.RuntimeDll))})");
            }
            finally
            {
                TryDelete(work);
            }
        }

        /// <summary>The object-file symbol of a Larva function (what executables and DLLs link by).</summary>
        public static string Symbol(string name) => name.StartsWith("larva_", StringComparison.Ordinal) ? name : ExecutableBuilder.SymbolPrefix + name;

        private static Dictionary<string, string>? ImportLibraries(Toolchain toolchain, IReadOnlyList<LarvaObject> libraries, string work, out string? error)
        {
            error = null;
            var result = new Dictionary<string, string>();
            foreach (var library in libraries)
            {
                string lib = Path.Combine(work, $"{library.Module.Name}.lib");
                var (ok, log) = toolchain.ImportLibrary($"{library.Module.Name}.dll", [.. NativeImages.Exports(library.Module).Select(Symbol)], lib);
                if (!ok)
                {
                    error = $"{library.Module.Name}: making its import library failed\n{log}";
                    return null;
                }
                result[library.Module.Name] = lib;
            }
            return result;
        }

        private static string? Compile(IrModule concrete, string obj, ExecutableOptions options, string name)
        {
            if (options.Backend == Backend.X64)
            {
                var direct = X64Backend.CompileConcrete(concrete, new X64Options
                {
                    Compilation = options.Compilation,
                    BoundsCheckElimination = options.BoundsCheckElimination,
                    SymbolPrefix = ExecutableBuilder.SymbolPrefix,
                });
                if (!direct.Success)
                    return direct.ToString();
                direct.Program!.EmitObject(obj);
                return null;
            }
            var compiled = LlvmBackend.CompileConcrete(concrete, new LlvmOptions
            {
                Compilation = options.Compilation,
                OptimizationLevel = options.OptimizationLevel,
                BoundsCheckElimination = options.BoundsCheckElimination,
                Polly = options.Polly,
                SymbolPrefix = ExecutableBuilder.SymbolPrefix,
            }, name);
            if (!compiled.Success)
                return compiled.ToString();
            using var llvm = compiled.Program!;
            llvm.EmitObject(obj);
            return null;
        }

        private static string Scratch()
        {
            string work = Path.Combine(Path.GetTempPath(), $"larva-dll-{Guid.NewGuid():N}");
            Directory.CreateDirectory(work);
            return work;
        }

        private static void TryDelete(string folder)
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

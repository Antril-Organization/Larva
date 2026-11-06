using Larva.IR;
using Larva.IR.Linking;
using Larva.IR.Lowering;
using Larva.Llvm;
using Larva.Native;

namespace Larva.Driver
{
    /// <summary>A target for native code: the operating system and processor (LLVM triples).</summary>
    public sealed record NativeTarget(string Name, string Triple, PosixSystem? Posix)
    {
        public static readonly NativeTarget Windows = new("windows-x64", "x86_64-pc-windows-msvc", null);
        public static readonly NativeTarget LinuxX64 = new("linux-x64", LlvmBackend.LinuxX64, PosixSystem.Linux);
        public static readonly NativeTarget LinuxArm64 = new("linux-arm64", LlvmBackend.LinuxArm64, PosixSystem.Linux);
        public static readonly NativeTarget MacX64 = new("macos-x64", LlvmBackend.MacX64, PosixSystem.MacOS);
        public static readonly NativeTarget MacArm64 = new("macos-arm64", LlvmBackend.MacArm64, PosixSystem.MacOS);

        public static readonly NativeTarget[] All = [Windows, LinuxX64, LinuxArm64, MacX64, MacArm64];

        public static NativeTarget? Named(string name) => All.FirstOrDefault(t => t.Name == name);

        /// <summary>The executable's extension (<c>.exe</c> on Windows, none elsewhere).</summary>
        public string ExecutableExtension => Posix is null ? ".exe" : "";

        /// <summary>A Larva library's file name for a module: <c>NAME.dll</c>, <c>libNAME.so</c>, <c>libNAME.dylib</c>.</summary>
        public string LibraryFile(string module) => Posix switch
        {
            null => module + ".dll",
            PosixSystem.MacOS => $"lib{module}.dylib",
            _ => $"lib{module}.so",
        };
    }

    /// <summary>
    /// Native code for Linux and macOS, through LLVM (the direct x86-64 back end speaks the Windows ABI only): programs
    /// with the runtime compiled in, and shared libraries per namespace with <c>liblarva_rt</c>, like the Windows DLLs
    /// (see <see cref="NativeImages"/>). Linked by the system C compiler (on Windows, inside WSL).
    /// </summary>
    public static class PosixBuilder
    {
        public static (bool Success, string Message) BuildExecutable(LinkResult linked, string output, NativeTarget target, ExecutableOptions? options = null)
        {
            options ??= new ExecutableOptions();
            var (toolchain, problem) = PosixToolchain.Find(target.Posix!.Value);
            if (toolchain is null)
                return (false, problem!);
            LinkResult program;
            try
            {
                program = ExecutableEntry.AddWrappers(linked, gc: options.Compilation.Gc);
            }
            catch (InvalidOperationException e)
            {
                return (false, e.Message);
            }
            string work = Scratch();
            try
            {
                string obj = Path.Combine(work, "program.o");
                if (Compile(Monomorphizer.Run(program.Merged!, GenericsMode.Specialize, program.Roots), obj, target, options) is { } error)
                    return (false, error);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                var (ok, log) = toolchain.LinkExecutable([obj], output, work);
                return ok ? (true, $"{output} (llvm back end, O{options.OptimizationLevel}, {target.Name}, entry {linked.Entry})") : (false, $"linking failed\n{log}");
            }
            finally
            {
                TryDelete(work);
            }
        }

        /// <summary>One shared library per module of <paramref name="own"/> (and <c>liblarva_rt</c>) in <paramref name="folder"/>.</summary>
        public static (IReadOnlyList<LarvaObject>? Objects, string Message) BuildLibraries(IReadOnlyList<LarvaObject> own, IReadOnlyList<LarvaObject> others,
            string folder, NativeTarget target, ExecutableOptions? options = null)
        {
            options ??= new ExecutableOptions();
            var (toolchain, problem) = PosixToolchain.Find(target.Posix!.Value);
            if (toolchain is null)
                return (null, problem!);
            var linked = Linker.Link([.. own, .. others], new LinkOptions { Library = true, Compilation = options.Compilation, LowerToIl = false });
            if (!linked.Success)
                return (null, string.Join("\n", linked.Errors));
            Directory.CreateDirectory(folder);
            string work = Scratch();
            try
            {
                var (runtimeOk, runtimeLog) = toolchain.BuildRuntime(folder, work);
                if (!runtimeOk)
                    return (null, $"building the runtime failed\n{runtimeLog}");
                var all = own.Concat(others).ToList();
                var result = new List<LarvaObject>();
                var sizes = new List<string>();
                // Libraries link against those they call: build in dependency order (others are already built, next to them).
                foreach (var library in Ordered(own))
                {
                    string module = library.Module.Name;
                    var foreign = all.Select(o => o.Module.Name).Where(m => m != module).ToHashSet();
                    var prepared = NativeImages.Prepare(linked, foreign, module);
                    var slots = new IdSlots();
                    var concrete = Monomorphizer.Run(prepared.Merged!, GenericsMode.Specialize, prepared.Roots, slots);
                    (concrete, var manifest) = NativeImages.AddIdSetter(concrete, slots, prepared.Merged!, module);
                    string obj = Path.Combine(work, $"{module}.o");
                    if (Compile(concrete, obj, target, options) is { } error)
                        return (null, $"{module}: {error}");
                    string file = Path.Combine(folder, target.LibraryFile(module));
                    var uses = library.Dependencies.Select(d => Path.Combine(folder, target.LibraryFile(d))).Where(File.Exists)
                        .Append(Path.Combine(folder, "liblarva_rt" + toolchain.LibraryExtension)).ToList();
                    var (linkOk, linkLog) = toolchain.LinkLibrary([obj], file, uses);
                    if (!linkOk)
                        return (null, $"{module}: linking failed\n{linkLog}");
                    sizes.Add($"{Path.GetFileName(file)} {new FileInfo(file).Length} bytes");
                    result.Add(library with { NativeIds = manifest });
                }
                return (result, string.Join(", ", sizes));
            }
            finally
            {
                TryDelete(work);
            }
        }

        /// <summary>A program that loads <paramref name="libraries"/> (copied next to it with the runtime).</summary>
        public static (bool Success, string Message) BuildProgram(LinkResult linked, IReadOnlyList<NativeLibrary> libraries, string output, NativeTarget target,
            ExecutableOptions? options = null)
        {
            options ??= new ExecutableOptions();
            var (toolchain, problem) = PosixToolchain.Find(target.Posix!.Value);
            if (toolchain is null)
                return (false, problem!);
            LinkResult program;
            try
            {
                var prepared = NativeImages.Prepare(linked, libraries.Select(l => l.Module).ToHashSet(), own: null);
                (prepared, int init) = NativeImages.AddIdInit(prepared, libraries.ToDictionary(l => l.Module, l => l.Object.NativeIds!));
                program = ExecutableEntry.AddWrappers(prepared, init, options.Compilation.Gc);
            }
            catch (InvalidOperationException e)
            {
                return (false, e.Message);
            }
            string work = Scratch();
            try
            {
                string obj = Path.Combine(work, "program.o");
                if (Compile(Monomorphizer.Run(program.Merged!, GenericsMode.Specialize, program.Roots), obj, target, options) is { } error)
                    return (false, error);
                string folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
                Directory.CreateDirectory(folder);
                // What the program loads goes next to it (found there through its rpath).
                string runtime = "liblarva_rt" + toolchain.LibraryExtension;
                var files = libraries.Select(l => l.Dll).Append(Path.Combine(Path.GetDirectoryName(libraries[0].Dll)!, runtime)).ToList();
                foreach (string file in files)
                {
                    string copy = Path.Combine(folder, Path.GetFileName(file));
                    if (!string.Equals(Path.GetFullPath(file), copy, StringComparison.OrdinalIgnoreCase))
                        File.Copy(file, copy, overwrite: true);
                }
                var (ok, log) = toolchain.LinkProgram([obj], output, [.. files.Select(f => Path.Combine(folder, Path.GetFileName(f)))], work);
                return ok ? (true, $"{output} ({target.Name}; loads {string.Join(", ", files.Select(Path.GetFileName))})") : (false, $"linking failed\n{log}");
            }
            finally
            {
                TryDelete(work);
            }
        }

        private static IEnumerable<LarvaObject> Ordered(IReadOnlyList<LarvaObject> libraries)
        {
            var done = new HashSet<string>();
            var names = libraries.ToDictionary(l => l.Module.Name);
            var order = new List<LarvaObject>();
            void Visit(LarvaObject library)
            {
                if (!done.Add(library.Module.Name))
                    return;
                foreach (string dependency in library.Dependencies)
                {
                    if (names.TryGetValue(dependency, out var used))
                        Visit(used);
                }
                order.Add(library);
            }
            foreach (var library in libraries)
                Visit(library);
            return order;
        }

        private static string? Compile(IrModule concrete, string obj, NativeTarget target, ExecutableOptions options)
        {
            var compiled = LlvmBackend.CompileConcrete(concrete, new LlvmOptions
            {
                Compilation = options.Compilation,
                OptimizationLevel = options.OptimizationLevel,
                BoundsCheckElimination = options.BoundsCheckElimination,
                SymbolPrefix = ExecutableBuilder.SymbolPrefix,
                PreciseStackRoots = false, // the C runtime finds handles on the stack and in the registers itself
                Triple = target.Triple,
            });
            if (!compiled.Success)
                return compiled.ToString();
            using var program = compiled.Program!;
            program.EmitObject(obj);
            return null;
        }

        private static string Scratch()
        {
            string work = Path.Combine(Path.GetTempPath(), $"larva-posix-{Guid.NewGuid():N}");
            Directory.CreateDirectory(work);
            return work;
        }

        private static void TryDelete(string folder)
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

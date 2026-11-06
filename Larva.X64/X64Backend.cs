using Larva.IR;
using Larva.IR.Analysis;
using Larva.IR.Linking;
using Larva.IR.Lowering;
using Larva.Native;

namespace Larva.X64
{
    public sealed record X64Options
    {
        public static readonly X64Options Default = new();

        /// <summary>GC mode and checks. Generics are always specialized for native code.</summary>
        public CompilationOptions Compilation { get; init; } = CompilationOptions.Default;

        /// <summary>Drop the bounds checks <see cref="BoundsAnalysis"/> proves unnecessary.</summary>
        public bool BoundsCheckElimination { get; init; } = true;

        /// <summary>
        /// Prefix for the symbols of Larva functions (not for <c>larva_*</c> names, reserved for the runtime), so objects
        /// linked into executables cannot clash with C runtime symbols such as <c>main</c>.
        /// </summary>
        public string? SymbolPrefix { get; init; }
    }

    public sealed record X64Result(X64Program? Program, IReadOnlyList<TypeError> TypeErrors, IReadOnlyList<BorrowError> Diagnostics, string? Error)
    {
        public bool Success => Program is not null;

        /// <summary>Functions left out because they use a type this back end lacks (and what reaches them), with why.</summary>
        public IReadOnlyDictionary<string, string> Skipped { get; init; } = new Dictionary<string, string>();


        public override string ToString() => Success ? "ok" : string.Join("\n",
            TypeErrors.Select(e => e.ToString()).Concat(Diagnostics.Select(d => d.ToString())).Append(Error ?? ""));
    }

    /// <summary>
    /// The direct x86-64 back end: IR → monomorphization → <see cref="X64Codegen"/> → machine code with Win64 unwind info,
    /// written as a COFF object (<see cref="X64Program.ToObject"/>) or loaded into this process (<see cref="X64Program.Load"/>).
    /// No LLVM involved.
    /// </summary>
    public static class X64Backend
    {
        public static X64Result Compile(IrModule module, X64Options? options = null)
        {
            options ??= X64Options.Default;
            if (module.HasImports)
                return new X64Result(null, [], [], "the module has imports: link it first");
            var compilation = options.Compilation with { Generics = GenericsMode.Specialize };
            var (typeErrors, diagnostics, ok) = Compiler.Verify(module, compilation);
            if (!ok)
                return new X64Result(null, typeErrors, diagnostics, null);
            return Generate(Monomorphizer.Run(module, GenericsMode.Specialize), options);
        }

        public static X64Result Compile(LinkResult linked, X64Options? options = null)
        {
            options ??= X64Options.Default;
            if (!linked.Success || linked.Merged is null)
                return new X64Result(null, [], [], "the link failed");
            return Generate(Monomorphizer.Run(linked.Merged, GenericsMode.Specialize, linked.Roots), options);
        }

        /// <summary>Compiles a module already monomorphized (native images build theirs: see <c>NativeImages</c>).</summary>
        /// <summary>Whether this back end compiles <paramref name="function"/>: every primitive is supported (f16 through F16C,
        /// f128 and f256 through the runtime's soft-float routines).</summary>
        public static bool Supports(IrFunction function, IrModule module) => !Compilable.Uses(function, module, _ => false);

        public static X64Result CompileConcrete(IrModule concrete, X64Options? options = null) => Generate(concrete, options ?? X64Options.Default);

        private static X64Result Generate(IrModule concrete, X64Options options)
        {
            concrete = Compilable.Stub(concrete, Supports);
            try
            {
                var codegen = new X64Codegen(concrete, options.Compilation.Gc, options.BoundsCheckElimination, options.SymbolPrefix);
                codegen.Generate();
                return new X64Result(new X64Program(codegen.Asm.ToArray(), codegen.Functions, codegen.Asm.Relocations)
                {
                    Data = codegen.Data,
                    BoundsChecks = codegen.BoundsChecks,
                    BoundsChecksEliminated = codegen.BoundsChecksEliminated,
                }, [], [], null) { Skipped = codegen.Skipped };
            }
            catch (X64BackendException e)
            {
                return new X64Result(null, [], [], e.Message);
            }
        }
    }

    /// <summary>A static array's bytes (length header, then elements), aligned to 16.</summary>
    public sealed record X64Data(string Name, byte[] Bytes, bool Writable = false);

    /// <summary>Machine code for a module: functions, their unwind info and GC roots, and the symbols they call.</summary>
    public sealed class X64Program(byte[] code, IReadOnlyList<X64Function> functions, IReadOnlyList<Relocation> relocations)
    {
        public byte[] Code { get; } = code;
        public IReadOnlyList<X64Function> Functions { get; } = functions;
        public IReadOnlyList<Relocation> Relocations { get; } = relocations;
        public int BoundsChecks { get; init; }
        public int BoundsChecksEliminated { get; init; }

        /// <summary>Read-only static arrays (<c>.rdata</c>), referenced rip-relative by name.</summary>
        public IReadOnlyList<X64Data> Data { get; init; } = [];

        /// <summary>Symbols called but not defined here (the <c>larva_*</c> runtime).</summary>
        public IReadOnlyList<string> Externals =>
            [.. Relocations.Select(r => r.Symbol)
                .Concat(Functions.SelectMany(f => f.UnwindFixups).Where(x => x.Symbol is not null).Select(x => x.Symbol!))
                .Distinct().Where(s => Functions.All(f => f.Name != s) && Data.All(d => d.Name != s)).Order(StringComparer.Ordinal)];

        public X64Function Function(string name) => Functions.FirstOrDefault(f => f.Name == name)
            ?? throw new KeyNotFoundException($"no function `{name}`");

        /// <summary>A COFF object for x86-64 (<c>.text</c>, <c>.xdata</c>, <c>.pdata</c>, and the stack maps of tracing modes).</summary>
        public byte[] ToObject() => CoffWriter.Write(this);

        public void EmitObject(string path) => File.WriteAllBytes(path, ToObject());

        /// <summary>Loads the code into this process, binding <c>larva_*</c> to <see cref="NativeRuntime"/>.</summary>
        public X64Image Load() => new(this);
    }
}

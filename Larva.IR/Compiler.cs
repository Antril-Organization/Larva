using Larva.IR.Analysis;
using Larva.IR.Lowering;

namespace Larva.IR
{
    public enum GenericsMode : byte
    {
        /// <summary>
        /// Compile generic functions once and pass witness tables, specializing only those whose bodies need the
        /// concrete type (arithmetic, comparisons, casts on <c>T</c>). Allows instantiating with types known only at run time.
        /// </summary>
        Shared,
        /// <summary>Specialize every instantiation (fastest code, larger output, no run-time instantiation).</summary>
        Specialize,
    }

    public enum BorrowCheckMode : byte
    {
        /// <summary>Skip the borrow checker entirely.</summary>
        Off,
        /// <summary>Run it and report findings as warnings; IL is still produced.</summary>
        Warn,
        /// <summary>Run it and refuse to produce IL when it finds errors.</summary>
        Enforce,
    }

    public sealed record CompilationOptions
    {
        public static readonly CompilationOptions Default = new();

        public BorrowCheckMode BorrowCheck { get; init; } = BorrowCheckMode.Enforce;

        /// <summary>The program's GC mode; decides whether <c>gc T</c> is allowed.</summary>
        public GcConfig Gc { get; init; } = GcConfig.Default;

        public GenericsMode Generics { get; init; } = GenericsMode.Shared;

        /// <summary>
        /// Reflection level of structs without <c>#[reflect]</c> (types owning <c>gc</c> handles never drop below
        /// <see cref="ReflectionLevel.Layout"/>). Functions are only reflected with <c>#[reflect(full)]</c>.
        /// </summary>
        public ReflectionLevel Reflection { get; init; } = ReflectionLevel.Layout;

        /// <summary>Run IL peephole optimizations after lowering.</summary>
        public bool Optimize { get; init; } = true;

        /// <summary>Name of the function the module starts at when run as a program (ignored if absent).</summary>
        public string Entry { get; init; } = "main";
    }

    /// <param name="Il">Produced IL, or <c>null</c> when type or (enforced) borrow errors stopped compilation.</param>
    /// <param name="TypeErrors">Type checker errors (always fatal).</param>
    /// <param name="Diagnostics">Borrow checker findings (errors or warnings depending on the mode).</param>
    public sealed record CompilationResult(IlModule? Il, IReadOnlyList<TypeError> TypeErrors, IReadOnlyList<BorrowError> Diagnostics)
    {
        /// <summary>The module after monomorphization (what was lowered), when compilation got that far.</summary>
        public IrModule? Concrete { get; init; }

        public bool Success => Il is not null;
    }

    /// <param name="Object">The verified module, ready to be written as a <c>.lvo</c> and linked; <c>null</c> on errors.</param>
    public sealed record ObjectResult(Linking.LarvaObject? Object, IReadOnlyList<TypeError> TypeErrors, IReadOnlyList<BorrowError> Diagnostics)
    {
        public bool Success => Object is not null;
    }

    /// <summary>IR → type check → region check → (optional) borrow check → shared/specialized generics → IL.</summary>
    public static class Compiler
    {
        /// <summary>
        /// Verifies one module of a multi-module program. Imports are checked against their declared signatures; the linker
        /// later checks those declarations against the exporting modules.
        /// </summary>
        public static ObjectResult CompileObject(IrModule module, CompilationOptions? options = null)
        {
            options ??= CompilationOptions.Default;
            var (typeErrors, diagnostics, ok) = Verify(module, options);
            return new ObjectResult(ok ? new Linking.LarvaObject(module, options.Gc) : null, typeErrors, diagnostics);
        }

        public static CompilationResult Compile(IrModule module, CompilationOptions? options = null)
        {
            options ??= CompilationOptions.Default;
            if (module.HasImports)
                return new CompilationResult(null,
                    [new TypeError(module.Name, null, TypeErrorKind.LinkageViolation, "the module has imports: compile it with CompileObject and link it")], []);

            var (typeErrors, diagnostics, ok) = Verify(module, options);
            if (!ok)
                return new CompilationResult(null, typeErrors, diagnostics);
            return Lower(module, options, diagnostics, ProgramRoots(module, options));
        }

        /// <summary>
        /// With an entry, only what it reaches is lowered (plus what reflection can call at run time), like a linked program;
        /// without one, every function.
        /// </summary>
        private static HashSet<int>? ProgramRoots(IrModule module, CompilationOptions options)
        {
            int entry = options.Entry.Length == 0 ? -1 : Array.FindIndex(module.Functions, f => f.Name == options.Entry);
            if (entry < 0)
                return null;
            var roots = new HashSet<int> { entry };
            for (int f = 0; f < module.Functions.Length; f++)
            {
                if (!module.Functions[f].IsGeneric && module.Functions[f].Reflect == ReflectionLevel.Full)
                    roots.Add(f);
            }
            return roots;
        }

        /// <summary>Type, region and (optional) borrow checks; generic bodies are checked once, before instantiation.</summary>
        internal static (IReadOnlyList<TypeError> TypeErrors, IReadOnlyList<BorrowError> Diagnostics, bool Ok) Verify(IrModule module, CompilationOptions options)
        {
            IReadOnlyList<TypeError> typeErrors;
            using (PhaseTimer.Start("type check"))
                typeErrors = TypeChecker.Check(module, options.Gc);
            if (typeErrors.Count == 0)
            {
                using (PhaseTimer.Start("region check"))
                    typeErrors = RegionChecker.Check(module, options.Gc);
            }
            if (typeErrors.Count > 0)
                return (typeErrors, [], false);

            IReadOnlyList<BorrowError> diagnostics = [];
            if (options.BorrowCheck != BorrowCheckMode.Off)
            {
                using (PhaseTimer.Start("borrow check"))
                    diagnostics = BorrowChecker.Check(module);
                if (options.BorrowCheck == BorrowCheckMode.Enforce && diagnostics.Count > 0)
                    return ([], diagnostics, false);
                diagnostics = [.. diagnostics.Select(d => d with { Severity = Severity.Warning })];
            }
            return ([], diagnostics, true);
        }

        /// <summary>Monomorphization and lowering of an already verified, import-free module.</summary>
        internal static CompilationResult Lower(IrModule module, CompilationOptions options, IReadOnlyList<BorrowError> diagnostics,
            IReadOnlySet<int>? roots = null)
        {
            IrModule concrete;
            using (PhaseTimer.Start("monomorphize"))
                concrete = Monomorphizer.Run(module, options.Generics, roots);
            IlModule il;
            using (PhaseTimer.Start($"lower to IL ({concrete.Functions.Length} functions, {module.Functions.Length} before)"))
                il = IlLowering.Lower(concrete, options.Optimize, options.Gc, module, options.Reflection);
            il = il with { Entry = Array.FindIndex(il.Functions, f => f.Name == options.Entry) };
            return new CompilationResult(il, [], diagnostics) { Concrete = concrete };
        }
    }
}

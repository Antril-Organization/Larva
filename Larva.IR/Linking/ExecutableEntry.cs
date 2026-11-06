namespace Larva.IR.Linking
{
    /// <summary>
    /// What a native executable needs from the program: the functions the C runtime's <c>main</c> calls.
    /// <list type="bullet">
    /// <item><c>larva_entry(a0..a3: i64) -&gt; i64</c>: converts up to four command-line integers to the entry's parameter
    /// types, calls the entry and widens its result (0 for unit or float results).</item>
    /// <item><c>larva_entry_f64(a0..a3: i64) -&gt; f64</c>: the same for float results (0.0 otherwise).</item>
    /// <item><c>larva_entry_info() -&gt; i64</c>: <c>arity | kind &lt;&lt; 8 | text &lt;&lt; 16</c>, kind 0 unit, 1 signed,
    /// 2 unsigned, 3 bool, 4 float, 5 exit code (an integer the process exits with, not printed); <c>text</c> is 1 when the
    /// entry reads its command line itself (<see cref="Intrinsics.ProgramAttribute"/>): any number of words, not parsed;
    /// bit 17 is 1 when nothing in the program traces (reference counting alone): the runtime makes no young objects;
    /// bit 18 is 1 when some code counts references (a thread's environment is retained for it, released when it ends).</item>
    /// </list>
    /// Names starting with <c>larva_</c> are reserved for the runtime and these wrappers; native back ends keep them
    /// unprefixed when they prefix Larva's own symbols.
    /// </summary>
    public static class ExecutableEntry
    {
        public const int MaxArguments = 4;

        /// <summary>Adds the wrappers for the linked program's entry; they become roots.</summary>
        /// <param name="init">A function to run before the entry (the ids native libraries need), or none.</param>
        /// <param name="gc">The program's GC, when known: whether anything traces goes into the entry's info.</param>
        public static LinkResult AddWrappers(LinkResult linked, int? init = null, GcConfig? gc = null)
        {
            if (!linked.Success || linked.Merged is null || linked.Entry is null)
                throw new InvalidOperationException("an executable needs a linked program with an entry function");
            var module = linked.Merged;
            int entry = Array.FindIndex(module.Functions, f => f.Name == linked.Entry);
            if (entry < 0)
                throw new InvalidOperationException($"entry `{linked.Entry}` not found");
            var function = module.Functions[entry];
            if (function.IsGeneric)
                throw new InvalidOperationException($"entry `{function.Name}` is generic");
            if (function.ArgumentCount > MaxArguments)
                throw new InvalidOperationException($"entry `{function.Name}` takes {function.ArgumentCount} arguments; executables pass at most {MaxArguments}");
            for (int a = 1; a <= function.ArgumentCount; a++)
            {
                var type = function.Locals[a].Type;
                if (!type.IsInteger && type != IrType.Bool && !type.IsFloat)
                    throw new InvalidOperationException($"entry `{function.Name}` takes a `{type}`; executables pass integers, bools or floats");
            }
            int kind = Kind(function.ReturnType)
                ?? throw new InvalidOperationException($"entry `{function.Name}` returns `{function.ReturnType}`; executables print integers, bools, floats or nothing");

            bool program = function.Attributes.Any(a => a.Name == Intrinsics.ProgramAttribute);
            IrFunction[] wrappers =
            [
                Wrapper("larva_entry", IrType.I64, entry, function, kind != 4, init),
                Wrapper("larva_entry_f64", IrType.F64, entry, function, kind == 4, init),
                Info(function.ArgumentCount | (program && kind is 1 or 2 ? 5 : kind) << 8 | (program ? 1 : 0) << 16
                    | (gc is not null && !module.Traces(gc) ? 1 : 0) << 17
                    | (gc is not null && module.Any(GcCapabilities.RefCounting, gc) ? 1 : 0) << 18),
            ];
            var merged = module with { Functions = [.. module.Functions, .. wrappers] };
            var roots = new HashSet<int>(linked.Roots);
            for (int w = 0; w < wrappers.Length; w++)
                roots.Add(module.Functions.Length + w);
            return linked with { Merged = merged, Roots = roots };
        }

        /// <summary>
        /// A native library has no entry: the runtime's <c>main</c> (unused there) still names the wrappers, which return 0.
        /// </summary>
        public static LinkResult AddLibraryStubs(LinkResult linked)
        {
            if (!linked.Success || linked.Merged is null)
                throw new InvalidOperationException("a native library needs a linked program");
            var module = linked.Merged;
            IrFunction Zero(string name, IrType result)
            {
                var f = new FunctionBuilder(name, result);
                for (int a = 0; a < MaxArguments; a++)
                    f.Argument(IrType.I64, $"a{a}");
                f.Assign(FunctionBuilder.ReturnPlace, result.IsFloat ? Operand.Float(0) : Operand.Int(0, IrType.I64)).Return();
                return f.Build();
            }
            IrFunction[] stubs = [Zero("larva_entry", IrType.I64), Zero("larva_entry_f64", IrType.F64), Info(0)];
            var roots = new HashSet<int>(linked.Roots);
            for (int s = 0; s < stubs.Length; s++)
                roots.Add(module.Functions.Length + s);
            return linked with { Merged = module with { Functions = [.. module.Functions, .. stubs] }, Roots = roots };
        }

        private static int? Kind(IrType type) =>
            type.IsUnit ? 0 : type == IrType.Bool ? 3 : type.IsFloat ? 4 : type.IsSigned ? 1 : type.IsUnsigned ? 2 : null;

        private static IrFunction Wrapper(string name, IrType result, int entry, IrFunction target, bool real, int? init)
        {
            var f = new FunctionBuilder(name, result);
            var raw = Enumerable.Range(0, MaxArguments).Select(i => f.Argument(IrType.I64, $"a{i}")).ToArray();
            if (!real)
            {
                f.Assign(FunctionBuilder.ReturnPlace, result.IsFloat ? Operand.Float(0) : Operand.Int(0, IrType.I64)).Return();
                return f.Build();
            }
            if (init is int initFunction)
            {
                int done = f.Local(IrType.Unit, "init");
                int next = f.NewBlock();
                f.Call(initFunction, [], new Place(done), next);
                f.At(next);
            }
            var arguments = new List<Operand>();
            for (int a = 1; a <= target.ArgumentCount; a++)
            {
                int converted = f.Local(target.Locals[a].Type, $"x{a}");
                f.Assign(converted, Rvalue.Cast(Operand.Copy(new Place(raw[a - 1])), target.Locals[a].Type));
                arguments.Add(Operand.Copy(new Place(converted)));
            }
            int value = f.Local(target.ReturnType, "result");
            int after = f.NewBlock();
            f.Call(entry, [.. arguments], value, after);
            f.At(after);
            if (target.ReturnType.IsUnit)
                f.Assign(FunctionBuilder.ReturnPlace, Operand.Int(0, IrType.I64));
            else
                f.Assign(FunctionBuilder.ReturnPlace, Rvalue.Cast(Operand.Copy(new Place(value)), result));
            f.Return();
            return f.Build();
        }

        private static IrFunction Info(long value)
        {
            var f = new FunctionBuilder("larva_entry_info", IrType.I64);
            f.Assign(FunctionBuilder.ReturnPlace, Operand.Int(value, IrType.I64)).Return();
            return f.Build();
        }
    }
}

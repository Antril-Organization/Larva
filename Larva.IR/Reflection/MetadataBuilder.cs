using Larva.IL;
using Larva.IR.Lowering;

namespace Larva.IR.Reflection
{
    /// <summary>
    /// Builds <see cref="ModuleMetadata"/> for a lowered module and the IL invoke stubs it needs.
    /// <list type="bullet">
    /// <item>Structs: level = <c>#[reflect]</c> or the module default; types owning <c>gc</c> handles never go below
    /// <see cref="ReflectionLevel.Layout"/> because the GC reads their pointer maps.</item>
    /// <item>Methods: only <c>#[reflect(full)]</c> functions, grouped by source function.</item>
    /// <item>Stubs: one per distinct <see cref="InvokeAbi"/> shape, appended after the module's functions.</item>
    /// </list>
    /// </summary>
    internal sealed class MetadataBuilder(IrModule source, IrModule concrete, int functionCount, TokenTable tokens, ReflectionLevel defaultLevel)
    {
        private readonly Dictionary<string, int> stubs = [];
        private readonly List<IlFunction> stubFunctions = [];

        public (ModuleMetadata Metadata, IlFunction[] Stubs) Build()
        {
            var structs = concrete.Structs.Select(Describe).ToArray();
            var methods = Enumerable.Range(0, source.Functions.Length)
                .Where(f => source.Functions[f].Reflect == ReflectionLevel.Full)
                .Select(Describe)
                .ToArray();
            return (new ModuleMetadata(structs, methods), [.. stubFunctions]);
        }

        // ── Structs ────────────────────────────────────────────────

        private StructMetadata Describe(StructDef def, int id)
        {
            var level = def.Reflect ?? defaultLevel;
            var self = new StructType(id);
            bool generic = def.Generics.Length > 0;
            if (!generic && level == ReflectionLevel.None && Constraints.Satisfies(self, TypeConstraint.Managed, new TypeContext(concrete, [])))
                level = ReflectionLevel.Layout;

            bool visible = level >= ReflectionLevel.Layout;
            return new StructMetadata(visible ? def.Name : "", level, def.Fields)
            {
                Generics = def.Generics,
                FieldNames = visible ? def.FieldNames : [],
                InstanceOf = visible ? def.InstanceOf : null,
                Layout = visible && !generic ? Layouts.Of(self, concrete) : null,
                FieldOffsets = visible && !generic ? Layouts.FieldOffsets(self, concrete) ?? [] : [],
            };
        }

        // ── Methods ────────────────────────────────────────────────

        private MethodMetadata Describe(int sourceIndex)
        {
            var function = source.Functions[sourceIndex];
            var instances = Enumerable.Range(0, concrete.Functions.Length)
                .Where(i => concrete.Functions[i].InstanceOf?.Function == sourceIndex)
                .ToList();

            var metadata = new MethodMetadata(function.Name, Parameters(function), function.ReturnType) { Generics = function.Generics };

            // Non-generic, or shared: one compiled body serves every call.
            int single = instances.FindIndex(i => concrete.Functions[i].InstanceOf!.Arguments.Length == 0);
            if (single >= 0)
            {
                int index = instances[single];
                string shape = InvokeAbi.Shape(function.Generics.Length, metadata.Parameters, metadata.Return);
                return metadata with { Shared = function.IsGeneric, Function = index, Stub = StubFor(shape), Shape = shape };
            }

            // Specialized only: run-time instantiation can pick among the compiled ones.
            return metadata with
            {
                Instances = [.. instances.Select(i =>
                {
                    var instance = concrete.Functions[i];
                    string shape = InvokeAbi.Shape(0, Parameters(instance), instance.ReturnType);
                    return new MethodInstance(instance.InstanceOf!.Arguments, i, StubFor(shape), shape);
                })],
            };
        }

        private static IrType[] Parameters(IrFunction function) =>
            [.. Enumerable.Range(1, function.ArgumentCount).Select(a => function.Locals[a].Type)];

        // ── Stubs ──────────────────────────────────────────────────

        /// <summary>
        /// <c>$invoke(shape)(target, slots, result)</c>: pushes each slot (witnesses first) decoded by its class, calls
        /// <c>target</c> indirectly (callee on top of the arguments) and writes the result into <c>result</c>.
        /// </summary>
        private int StubFor(string shape)
        {
            if (stubs.TryGetValue(shape, out int existing))
                return existing;

            int open = shape.IndexOf('('), close = shape.IndexOf(')');
            string slots = shape[..open] + shape[(open + 1)..close];
            char result = shape[^1];

            List<Operation> ops = [];
            if (result != 'v')
            {
                ops.Add(new Operation(OpCode.LoadArgument, 2));
                if (result == 'x')
                    ops.Add(new Operation(OpCode.Stack, StackOp.LoadMem)); // the slot holds the result buffer's address
            }
            for (int s = 0; s < slots.Length; s++)
            {
                ops.Add(new Operation(OpCode.LoadArgument, 1));
                if (s > 0)
                {
                    ops.Add(new Operation(OpCode.Const, 8 * s));
                    ops.Add(new Operation(OpCode.Add));
                }
                ops.Add(new Operation(OpCode.Stack, StackOp.LoadMem));
                switch (slots[s])
                {
                    case 'd' or 's' or 'h':
                        ops.Add(new Operation(OpCode.Convert, ConvertOp.BitCast, tokens.Type(FloatOf(slots[s]))));
                        break;
                    case 'x':
                        ops.Add(new Operation(OpCode.Stack, StackOp.LoadMem));
                        break;
                }
            }
            ops.Add(new Operation(OpCode.LoadArgument, 0));
            ops.Add(new Operation(OpCode.CallExtended, CallOp.Indirect));
            if (result != 'v')
            {
                if (result is 'd' or 's' or 'h')
                    ops.Add(new Operation(OpCode.Convert, ConvertOp.BitCast, tokens.Type(IrType.U64)));
                ops.Add(new Operation(OpCode.Stack, StackOp.StoreMem));
            }
            ops.Add(new Operation(OpCode.Return));

            string name = $"$invoke{shape}";
            int index = functionCount + stubFunctions.Count;
            var slot = IrType.PtrMut(IrType.U64);
            stubFunctions.Add(new IlFunction(name, 3, [tokens.Type(IrType.Unit)], new OperationBlock(name, [.. ops]))
            {
                ArgumentTypes = [tokens.Type(IrType.U64), tokens.Type(slot), tokens.Type(slot)],
            });
            stubs.Add(shape, index);
            return index;
        }

        private static IrType FloatOf(char @class) => @class switch
        {
            'd' => IrType.F64,
            's' => IrType.F32,
            _ => IrType.F16,
        };
    }
}

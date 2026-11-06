using Larva.IR;
using Larva.IR.Reflection;
using Larva.IR.Text;

namespace Larva.Runtime
{
    /// <summary>
    /// An invocable function. Generic definitions must be instantiated with <see cref="MakeGeneric"/> first: shared methods
    /// accept any type that satisfies the constraints (the compiled body is reused with fresh witnesses), specialized ones
    /// only the instantiations compiled into the image.
    /// </summary>
    public sealed class MethodInfo
    {
        private readonly RuntimeModule module;
        private readonly MethodMetadata metadata;

        public string Name => metadata.Name;
        public GenericParam[] Generics => TypeArguments.Length == 0 ? metadata.Generics : [];
        public bool IsGenericDefinition => Generics.Length > 0;

        /// <summary>Instantiated type arguments (empty for non-generic methods and definitions).</summary>
        public TypeInfo[] TypeArguments { get; }

        public IrType[] Parameters { get; }
        public IrType Return { get; }

        /// <summary>IL function and invoke stub that run this method, and the stub's <see cref="InvokeAbi"/> shape.</summary>
        public int Function { get; }
        public int Stub { get; }
        public string Shape { get; }

        /// <summary>Witness ids passed before the arguments when calling shared generic code.</summary>
        public int[] Witnesses { get; }

        internal MethodInfo(RuntimeModule module, MethodMetadata metadata)
            : this(module, metadata, [], metadata.Parameters, metadata.Return, metadata.Function, metadata.Stub, metadata.Shape, [])
        {
        }

        private MethodInfo(RuntimeModule module, MethodMetadata metadata, TypeInfo[] typeArguments, IrType[] parameters, IrType result,
            int function, int stub, string shape, int[] witnesses)
        {
            this.module = module;
            this.metadata = metadata;
            TypeArguments = typeArguments;
            Parameters = parameters;
            Return = result;
            Function = function;
            Stub = stub;
            Shape = shape;
            Witnesses = witnesses;
        }

        /// <summary>Instantiates a generic method; see the class summary for what an AOT image can provide.</summary>
        public MethodInfo MakeGeneric(params TypeInfo[] arguments)
        {
            if (!IsGenericDefinition)
                throw new ReflectionException($"`{Name}` is not a generic definition");
            TypeInfo.CheckArguments(module, Name, metadata.Generics, arguments);

            IrType[] types = [.. arguments.Select(a => a.Type)];
            IrType[] parameters = [.. metadata.Parameters.Select(p => p.Substitute(types))];
            var result = metadata.Return.Substitute(types);

            if (metadata.Shared)
                return new MethodInfo(module, metadata, arguments, parameters, result, metadata.Function, metadata.Stub, metadata.Shape,
                    [.. arguments.Select(module.WitnessFor)]);

            var compiled = metadata.Instances.FirstOrDefault(i => i.Arguments.SequenceEqual(types))
                ?? throw new AotInstantiationException(
                    $"`{Name}<{string.Join(", ", arguments.Select(a => a.Name))}>` was not compiled into this image " +
                    $"(available: {(metadata.Instances.Length == 0 ? "none" : string.Join(", ", metadata.Instances.Select(i => $"<{string.Join(", ", i.Arguments.Select(a => IrPrinter.FormatType(a, module.Shapes)))}>")))})");
            return new MethodInfo(module, metadata, arguments, parameters, result, compiled.Function, compiled.Stub, compiled.Shape, []);
        }

        /// <summary>
        /// Type-checks and marshals <paramref name="arguments"/> into the slots of this method's invoke stub. The VM runs it as
        /// <c>call stub(fn.Function, slots, result)</c>.
        /// </summary>
        public InvokePlan PrepareInvoke(params object[] arguments)
        {
            if (IsGenericDefinition)
                throw new ReflectionException($"`{Name}` is generic; call MakeGeneric first");
            if (arguments.Length != Parameters.Length)
                throw new ReflectionException($"`{Name}` takes {Parameters.Length} argument(s), got {arguments.Length}");

            List<ulong> slots = [.. Witnesses.Select(w => (ulong)w)];
            List<byte[]> buffers = [];
            for (int i = 0; i < Parameters.Length; i++)
            {
                var type = Parameters[i];
                string what = $"{Name} argument {i}";
                if (InvokeAbi.Class(type) == 'x')
                {
                    var layout = module.TypeOf(type).Layout ?? throw new ReflectionException($"{what}: `{type}` has no layout");
                    buffers.Add(Values.Indirect(type, layout.Size, arguments[i], what));
                    slots.Add((ulong)(buffers.Count - 1));
                }
                else
                {
                    slots.Add(Values.Slot(type, arguments[i], what));
                }
            }

            int? resultBuffer = null;
            if (InvokeAbi.Class(Return) == 'x')
            {
                buffers.Add(new byte[module.TypeOf(Return).Layout?.Size ?? 0]);
                resultBuffer = buffers.Count - 1;
            }
            return new InvokePlan(Function, Stub, Shape, [.. slots], [.. buffers], resultBuffer);
        }

        public override string ToString()
        {
            string generics = TypeArguments.Length > 0 ? $"<{string.Join(", ", TypeArguments.Select(t => t.Name))}>" : IrPrinter.FormatGenerics(Generics);
            return $"{Name}{generics}({string.Join(", ", Parameters.Select(p => IrPrinter.FormatType(p, module.Shapes, metadata.Generics)))}) -> {IrPrinter.FormatType(Return, module.Shapes, metadata.Generics)}";
        }
    }

    /// <summary>
    /// A ready-to-run reflective call: invoke stub <see cref="Stub"/> with target <see cref="Function"/>. Each slot is an
    /// 8-byte value per <see cref="Shape"/>; slots of class <c>x</c> hold an index into <see cref="Buffers"/>, which the VM
    /// replaces with that buffer's address. A by-pointer result is written to <see cref="ResultBuffer"/>.
    /// </summary>
    public sealed record InvokePlan(int Function, int Stub, string Shape, ulong[] Slots, byte[][] Buffers, int? ResultBuffer)
    {
        public override string ToString() =>
            $"call fn.{Stub} [{Shape}] target fn.{Function} slots [{string.Join(", ", Slots.Select(s => $"0x{s:X}"))}]" +
            (Buffers.Length > 0 ? $" buffers [{string.Join(", ", Buffers.Select(b => $"{b.Length}B"))}]" : "") +
            (ResultBuffer is int r ? $" result → buffer {r}" : "");
    }
}

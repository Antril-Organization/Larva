using Larva.IR.Lowering;
using Larva.IR.Text;

namespace Larva.IR.Reflection
{
    /// <summary>
    /// What a compiled module tells its runtime about itself. Struct ids inside <see cref="IrType"/>s index
    /// <see cref="Structs"/>; function indices point at the IL module's functions (invoke stubs included).
    /// </summary>
    public sealed record ModuleMetadata(StructMetadata[] Structs, MethodMetadata[] Methods)
    {
        public override string ToString()
        {
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < Structs.Length; i++)
                builder.AppendLine($".reflect struct#{i} {Structs[i]}");
            foreach (var method in Methods)
                builder.AppendLine($".reflect {method}");
            return builder.ToString();
        }
    }

    /// <summary>
    /// A struct as reflection sees it. Every struct is listed (so layouts can be recomputed for generic instantiations),
    /// but below <see cref="ReflectionLevel.Layout"/> its name and field names are trimmed. Concrete structs at
    /// <see cref="ReflectionLevel.Layout"/> or above carry their <see cref="Layout"/> and <see cref="FieldOffsets"/>.
    /// </summary>
    public sealed record StructMetadata(string Name, ReflectionLevel Level, IrType[] Fields)
    {
        public GenericParam[] Generics { get; init; } = [];
        public string[] FieldNames { get; init; } = [];
        public StructType? InstanceOf { get; init; }
        public TypeLayout? Layout { get; init; }
        public int[] FieldOffsets { get; init; } = [];

        public bool IsGenericDefinition => Generics.Length > 0;

        public override string ToString()
        {
            string name = Name.Length == 0 ? "<trimmed>" : Name;
            string generics = IrPrinter.FormatGenerics(Generics);
            var fields = Fields.Select((f, i) =>
                $"{(i < FieldNames.Length ? FieldNames[i] : $"_{i}")}{(i < FieldOffsets.Length ? $" @{FieldOffsets[i]}" : "")}: {IrPrinter.FormatType(f, null, Generics)}");
            return $"{name}{generics} ({Level.ToString().ToLowerInvariant()}) {{ {string.Join(", ", fields)} }}{(Layout is null ? "" : $" [{Layout}]")}";
        }
    }

    /// <summary>
    /// An invocable function (<c>#[reflect(full)]</c>). Non-generic and shared generic methods have a <see cref="Function"/>
    /// and a <see cref="Stub"/>; generics that could only be specialized list their compiled <see cref="Instances"/>.
    /// </summary>
    public sealed record MethodMetadata(string Name, IrType[] Parameters, IrType Return)
    {
        public GenericParam[] Generics { get; init; } = [];

        /// <summary>Compiled once for every type argument (called with witnesses); see <see cref="Monomorphizer.Shareable"/>.</summary>
        public bool Shared { get; init; }

        /// <summary>IL function index, or -1 when only specialized instances exist.</summary>
        public int Function { get; init; } = -1;

        /// <summary>IL index of the invoke stub for <see cref="Shape"/>, or -1.</summary>
        public int Stub { get; init; } = -1;

        public string Shape { get; init; } = "";
        public MethodInstance[] Instances { get; init; } = [];

        public override string ToString()
        {
            string signature = $"{Name}{IrPrinter.FormatGenerics(Generics)}({string.Join(", ", Parameters.Select(p => IrPrinter.FormatType(p, null, Generics)))}) -> {IrPrinter.FormatType(Return, null, Generics)}";
            string target = Function >= 0 ? $" = fn.{Function} via fn.{Stub} [{Shape}]{(Shared ? " shared" : "")}" : "";
            string instances = Instances.Length == 0 ? "" : $" instances: {string.Join(", ", Instances.Select(i => i.ToString()))}";
            return $"method {signature}{target}{instances}";
        }
    }

    /// <summary>A pre-compiled specialization of a generic method.</summary>
    public sealed record MethodInstance(IrType[] Arguments, int Function, int Stub, string Shape)
    {
        public override string ToString() =>
            $"<{string.Join(", ", Arguments.Select(a => IrPrinter.FormatType(a)))}> = fn.{Function} via fn.{Stub} [{Shape}]";
    }

    /// <summary>
    /// Calling convention of reflection invoke stubs. Arguments travel in an array of 8-byte slots, one per witness and
    /// argument, classified as: <c>w</c> witness, <c>i</c> integer / bool / pointer up to 64 bits, <c>d</c> / <c>s</c> / <c>h</c>
    /// f64 / f32 / f16 bit pattern, <c>x</c> pointer to the value (wide numbers, structs, generic values). The return slot
    /// uses the same classes, <c>v</c> for unit. A shape reads like <c>w(ix)d</c>.
    /// </summary>
    public static class InvokeAbi
    {
        public static char Class(IrType type) => type switch
        {
            PrimitiveType { Kind: Primitive.Unit } => 'v',
            PrimitiveType { Kind: Primitive.F64 } => 'd',
            PrimitiveType { Kind: Primitive.F32 } => 's',
            PrimitiveType { Kind: Primitive.F16 } => 'h',
            PrimitiveType { Kind: Primitive.Bool } => 'i',
            PrimitiveType p when p.BitWidth <= 64 => 'i',
            ReferenceType or GcType or BoxType or PointerType or OptionalType => 'i',
            _ => 'x',
        };

        public static string Shape(int witnesses, IEnumerable<IrType> parameters, IrType result) =>
            $"{new string('w', witnesses)}({string.Concat(parameters.Select(Class))}){Class(result)}";
    }
}

namespace Larva.IR
{
    /// <summary>
    /// Built-in scalar types. The first 12 (<see cref="Unit"/>–<see cref="F64"/>) encode in one nibble
    /// in the binary IR; the wide ones after them need an extended tag (see <c>IrFormat</c>).
    /// </summary>
    public enum Primitive : byte
    {
        Unit, Bool,
        I8, I16, I32, I64,
        U8, U16, U32, U64,
        F32, F64,

        // Wide / less common: extended encoding.
        F16, F128, F256,
        I128, I256,
        U128, U256,
    }

    /// <summary>Type of an IR local or value. Compared structurally.</summary>
    public abstract record IrType
    {
        public static readonly IrType Unit = new PrimitiveType(Primitive.Unit);
        public static readonly IrType Bool = new PrimitiveType(Primitive.Bool);
        public static readonly IrType I8 = new PrimitiveType(Primitive.I8);
        public static readonly IrType I16 = new PrimitiveType(Primitive.I16);
        public static readonly IrType I32 = new PrimitiveType(Primitive.I32);
        public static readonly IrType I64 = new PrimitiveType(Primitive.I64);
        public static readonly IrType I128 = new PrimitiveType(Primitive.I128);
        public static readonly IrType I256 = new PrimitiveType(Primitive.I256);
        public static readonly IrType U8 = new PrimitiveType(Primitive.U8);
        public static readonly IrType U16 = new PrimitiveType(Primitive.U16);
        public static readonly IrType U32 = new PrimitiveType(Primitive.U32);
        public static readonly IrType U64 = new PrimitiveType(Primitive.U64);
        public static readonly IrType U128 = new PrimitiveType(Primitive.U128);
        public static readonly IrType U256 = new PrimitiveType(Primitive.U256);
        public static readonly IrType F16 = new PrimitiveType(Primitive.F16);
        public static readonly IrType F32 = new PrimitiveType(Primitive.F32);
        public static readonly IrType F64 = new PrimitiveType(Primitive.F64);
        public static readonly IrType F128 = new PrimitiveType(Primitive.F128);
        public static readonly IrType F256 = new PrimitiveType(Primitive.F256);

        public static IrType Ref(IrType pointee) => new ReferenceType(pointee, Mutable: false);
        public static IrType RefMut(IrType pointee) => new ReferenceType(pointee, Mutable: true);
        public static IrType Gc(IrType pointee) => new GcType(pointee);
        public static IrType Box(IrType pointee) => new BoxType(pointee);
        public static IrType Ptr(IrType pointee) => new PointerType(pointee, Mutable: false);
        public static IrType PtrMut(IrType pointee) => new PointerType(pointee, Mutable: true);
        public static IrType ArrayOf(IrType element) => new ArrayType(element);
        public static IrType Struct(int id, params IrType[] arguments) => new StructType(id, arguments);
        public static IrType Param(int index) => new GenericParamType(index);

        public bool IsUnit => this is PrimitiveType { Kind: Primitive.Unit };
        public bool IsFloat => this is PrimitiveType { Kind: Primitive.F16 or Primitive.F32 or Primitive.F64 or Primitive.F128 or Primitive.F256 };
        public bool IsSigned => this is PrimitiveType { Kind: Primitive.I8 or Primitive.I16 or Primitive.I32 or Primitive.I64 or Primitive.I128 or Primitive.I256 };
        public bool IsUnsigned => this is PrimitiveType { Kind: Primitive.U8 or Primitive.U16 or Primitive.U32 or Primitive.U64 or Primitive.U128 or Primitive.U256 };
        public bool IsInteger => IsSigned || IsUnsigned;

        /// <summary>Size in bits of a primitive value (bool = 1, unit = 0); <c>null</c> for non-primitive types.</summary>
        public int? BitWidth => this is PrimitiveType primitive ? primitive.Kind switch
        {
            Primitive.Unit => 0,
            Primitive.Bool => 1,
            Primitive.I8 or Primitive.U8 => 8,
            Primitive.I16 or Primitive.U16 or Primitive.F16 => 16,
            Primitive.I32 or Primitive.U32 or Primitive.F32 => 32,
            Primitive.I64 or Primitive.U64 or Primitive.F64 => 64,
            Primitive.I128 or Primitive.U128 or Primitive.F128 => 128,
            Primitive.I256 or Primitive.U256 or Primitive.F256 => 256,
            _ => throw new ArgumentOutOfRangeException(nameof(primitive.Kind)),
        } : null;
    }

    public sealed record PrimitiveType(Primitive Kind) : IrType
    {
        public override string ToString() => Kind switch
        {
            Primitive.Unit => "()",
            _ => Kind.ToString().ToLowerInvariant(),
        };
    }

    /// <summary><c>&amp;T</c> or <c>&amp;mut T</c>: a borrow, tracked by the borrow checker.</summary>
    public sealed record ReferenceType(IrType Pointee, bool Mutable) : IrType
    {
        public override string ToString() => Mutable ? $"&mut {Pointee}" : $"&{Pointee}";
    }

    /// <summary>
    /// <c>gc T</c>: handle to an object owned by the program's garbage collector. Handles copy freely;
    /// the object is immutable through them except inside <c>unsafe</c> functions. Forbidden when the
    /// GC mode cannot manage objects (see <see cref="GcConfig"/>).
    /// </summary>
    public sealed record GcType(IrType Pointee) : IrType
    {
        public override string ToString() => $"gc {Pointee}";
    }

    /// <summary><c>box T</c>: unique owner of a heap value, freed deterministically when dropped. Works in every GC mode.</summary>
    public sealed record BoxType(IrType Pointee) : IrType
    {
        public override string ToString() => $"box {Pointee}";
    }

    /// <summary><c>*T</c> / <c>*mut T</c>: raw pointer. Not tracked; dereferencing requires <c>unsafe</c>.</summary>
    public sealed record PointerType(IrType Pointee, bool Mutable) : IrType
    {
        public override string ToString() => Mutable ? $"*mut {Pointee}" : $"*{Pointee}";
    }

    /// <summary>Dynamically sized array <c>[T]</c> (not <c>fixedsize</c>; use it behind a pointer).</summary>
    public sealed record ArrayType(IrType Element) : IrType
    {
        public override string ToString() => $"[{Element}]";
    }

    /// <summary>
    /// Inline array <c>[T; N]</c>: <see cref="Count"/> elements stored in place (no header), or <c>Count × sizeof(SizeOf)</c>
    /// when <see cref="SizeOf"/> is set, so the length can follow a type parameter (<c>fixed u8[sizeof(TSize)]</c>).
    /// Indexing is bounds-checked against the length.
    /// </summary>
    public sealed record FixedArrayType(IrType Element, int Count, IrType? SizeOf = null) : IrType
    {
        public override string ToString() => SizeOf is null ? $"[{Element}; {Count}]" : $"[{Element}; {(Count == 1 ? "" : $"{Count} * ")}sizeof({SizeOf})]";

        /// <summary>The number of elements of a concrete inline array; null while it depends on a type parameter.</summary>
        public int? Length(IrModule module) => SizeOf is null ? Count
            : Lowering.Layouts.Of(SizeOf, module) is { } layout ? Count * layout.Size : null;
    }

    /// <summary>
    /// Reference to <see cref="IrModule.Structs"/> by index, with type arguments when the struct is generic.
    /// Compared structurally (including <see cref="Arguments"/>).
    /// </summary>
    public sealed record StructType(int Id, params IrType[] Arguments) : IrType
    {
        public bool Equals(StructType? other) =>
            other is not null && Id == other.Id && Arguments.AsSpan().SequenceEqual(other.Arguments);

        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(Id);
            foreach (var argument in Arguments)
                hash.Add(argument);
            return hash.ToHashCode();
        }

        public override string ToString() =>
            Arguments.Length == 0 ? $"struct#{Id}" : $"struct#{Id}<{string.Join(", ", Arguments.Select(a => a.ToString()))}>";
    }

    /// <summary>
    /// <c>fn(A, B) -> R</c>: the address of a function with that signature, called with an indirect call. A plain code
    /// address: copied freely, owns nothing, never null (so it has no zero value).
    /// </summary>
    public sealed record FunctionType(IrType[] Parameters, IrType Result) : IrType
    {
        public bool Equals(FunctionType? other) =>
            other is not null && Result == other.Result && Parameters.AsSpan().SequenceEqual(other.Parameters);

        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(Result);
            foreach (var parameter in Parameters)
                hash.Add(parameter);
            return hash.ToHashCode();
        }

        public override string ToString() => $"fn({string.Join(", ", Parameters.Select(p => p.ToString()))}) -> {Result}";
    }

    /// <summary>
    /// <c>?T</c> for a handle or reference <c>T</c> (<c>gc</c>, <c>box</c>, <c>&amp;</c>, <c>*</c>): the same pointer, which may be
    /// null (zero). Dereferencing it checks for null (a panic, never undefined behavior); <see cref="WrapRvalue"/>,
    /// <see cref="IsNullRvalue"/> and <see cref="UnwrapRvalue"/> convert and test it.
    /// </summary>
    public sealed record OptionalType(IrType Inner) : IrType
    {
        public override string ToString() => $"?{Inner}";
    }

    /// <summary>Generic parameter <see cref="Index"/> of the enclosing function or struct (<c>T0</c>, <c>T1</c>...).</summary>
    public sealed record GenericParamType(int Index) : IrType
    {
        public override string ToString() => $"T{Index}";
    }

    /// <summary>How much a type or function exposes to run-time reflection.</summary>
    public enum ReflectionLevel : byte
    {
        /// <summary>Nothing beyond what the GC itself needs.</summary>
        None,
        /// <summary>Name, field names, offsets and layout (enough to serialize).</summary>
        Layout,
        /// <summary>Everything, including invocable methods and run-time generic instantiation.</summary>
        Full,
    }

    /// <summary>How a function or struct relates to other modules at link time.</summary>
    public enum Linkage : byte
    {
        /// <summary>Visible only inside its module.</summary>
        Internal,
        /// <summary>Defined here and visible to other modules as <c>module::name</c>.</summary>
        Export,
        /// <summary>A declaration of something another module exports (<see cref="StructDef.Symbol"/> / <see cref="IrFunction.Symbol"/>).</summary>
        Import,
        /// <summary>
        /// Defined in every module that needs it (what a compiler writes itself: the root object, nullable, boxes); the
        /// linker keeps one per name, all of which must be identical. Visible to other modules like an export.
        /// </summary>
        LinkOnce,
    }

    /// <summary>A struct; fields may mention its <see cref="Generics"/> as <see cref="GenericParamType"/>.</summary>
    public sealed record StructDef(string Name, params IrType[] Fields)
    {
        public Linkage Linkage { get; init; }

        /// <summary>For imports: the exported struct this declaration stands for, as <c>module::Name</c>.</summary>
        public string? Symbol { get; init; }

        /// <summary>The GC domain of the module that defines the struct (see <see cref="IrFunction.Domain"/>); 0 before linking.</summary>
        public int Domain { get; init; }

        public GenericParam[] Generics { get; init; } = [];

        /// <summary>Source names of <see cref="Fields"/> (empty when unnamed); exposed by reflection at <see cref="ReflectionLevel.Layout"/>.</summary>
        public string[] FieldNames { get; init; } = [];

        /// <summary><c>#[reflect(...)]</c>: explicit level, or <c>null</c> for the module default.</summary>
        public ReflectionLevel? Reflect { get; init; }

        /// <summary>Set by monomorphization: the generic struct and arguments this concrete struct instantiates.</summary>
        public StructType? InstanceOf { get; init; }

        /// <summary>
        /// An object type (a <c>class</c>): its fields may be assigned through <c>gc</c> handles outside <c>unsafe</c>,
        /// by value only — nothing may borrow into the object, since other handles can reach it at any time.
        /// </summary>
        public bool Mutable { get; init; }

        /// <summary>
        /// The struct this one extends (a base class): its fields are the first fields of this one, in order and
        /// with the same types, so a handle to this struct can be used as a handle to the base (<see cref="HandleCastRvalue"/>).
        /// </summary>
        public StructType? Base { get; init; }

        /// <summary>Attributes of the type (see <see cref="IrAttribute"/>).</summary>
        public IrAttribute[] Attributes { get; init; } = [];

        /// <summary>Attributes of each field (by field index; empty when no field has any).</summary>
        public IrAttribute[][] FieldAttributes { get; init; } = [];
    }

    public static class IrTypeExtensions
    {
        /// <summary>Replaces <see cref="GenericParamType"/>s with <paramref name="arguments"/>.</summary>
        public static IrType Substitute(this IrType type, IReadOnlyList<IrType> arguments)
        {
            if (arguments.Count == 0)
                return type;
            return type switch
            {
                GenericParamType p => p.Index < arguments.Count
                    ? arguments[p.Index]
                    : throw new InvalidOperationException($"Missing type argument for T{p.Index}."),
                ReferenceType r => r with { Pointee = r.Pointee.Substitute(arguments) },
                GcType g => new GcType(g.Pointee.Substitute(arguments)),
                BoxType b => new BoxType(b.Pointee.Substitute(arguments)),
                PointerType ptr => ptr with { Pointee = ptr.Pointee.Substitute(arguments) },
                ArrayType a => new ArrayType(a.Element.Substitute(arguments)),
                FixedArrayType f => f with { Element = f.Element.Substitute(arguments), SizeOf = f.SizeOf?.Substitute(arguments) },
                StructType st => new StructType(st.Id, [.. st.Arguments.Select(x => x.Substitute(arguments))]),
                FunctionType f => new FunctionType([.. f.Parameters.Select(x => x.Substitute(arguments))], f.Result.Substitute(arguments)),
                OptionalType o => new OptionalType(o.Inner.Substitute(arguments)),
                _ => type,
            };
        }

        /// <summary>The handle inside <c>?T</c>; the type itself otherwise.</summary>
        public static IrType Unoptional(this IrType type) => type is OptionalType o ? o.Inner : type;

        /// <summary>The pointee when <paramref name="type"/> can be dereferenced (<c>&amp;</c>, <c>gc</c>, <c>box</c>, <c>*</c>).</summary>
        public static IrType? Pointee(this IrType type) => type switch
        {
            ReferenceType r => r.Pointee,
            GcType g => g.Pointee,
            BoxType b => b.Pointee,
            PointerType p => p.Pointee,
            OptionalType o => o.Inner.Pointee(),
            _ => null,
        };

        /// <summary>True when the type mentions any generic parameter.</summary>
        public static bool IsGeneric(this IrType type) => type.Walk().Any(t => t is GenericParamType);

        /// <summary>
        /// Whether values of <paramref name="type"/> hold no handles or pointers (numbers, inline arrays and structs of
        /// them): the collector need not look inside memory that holds only these.
        /// </summary>
        public static bool HoldsNoReferences(this IrType type, IrModule module) => type switch
        {
            PrimitiveType => true,
            FixedArrayType f => f.Element.HoldsNoReferences(module),
            StructType s => s.Id < module.Structs.Length && module.Structs[s.Id].Fields.All(f => f.Substitute(s.Arguments).HoldsNoReferences(module)),
            _ => false,
        };

        /// <summary>
        /// The last argument of the runtime's <c>larva_array_new</c>: 1 for a gc array, plus 2 when its elements hold no
        /// references (the collector does not scan it).
        /// </summary>
        public static int ArrayFlags(this ArrayRvalue array, IrModule module) =>
            array.Kind == AllocKind.Gc ? 1 | (array.Element.HoldsNoReferences(module) ? 2 : 0) : 0;

        /// <summary>Every type nested in <paramref name="type"/>, itself included.</summary>
        public static IEnumerable<IrType> Walk(this IrType type)
        {
            yield return type;
            IrType[] children = type switch
            {
                StructType st => st.Arguments,
                ArrayType a => [a.Element],
                FixedArrayType f => f.SizeOf is null ? [f.Element] : [f.Element, f.SizeOf],
                FunctionType f => [.. f.Parameters, f.Result],
                OptionalType o => [o.Inner],
                _ => type.Pointee() is { } pointee ? [pointee] : [],
            };
            foreach (var child in children)
            {
                foreach (var nested in child.Walk())
                    yield return nested;
            }
        }
    }
}

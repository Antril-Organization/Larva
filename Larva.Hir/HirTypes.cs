namespace Larva.Hir
{
    /// <summary>
    /// The built-in value types. <c>str</c> and <c>any</c> are not here: they are classes of the core library
    /// (<c>[Core("string")]</c>, <c>[Core("object")]</c>), named like any other type.
    /// </summary>
    public enum Primitive : byte
    {
        Void, Bool, Char,
        I8, I16, I32, I64, I128, I256,
        U8, U16, U32, U64, U128, U256,
        F16, F32, F64, F128, F256,
    }

    public static class Primitives
    {
        public static bool IsInteger(this Primitive p) => p is >= Primitive.I8 and <= Primitive.U256;
        public static bool IsSigned(this Primitive p) => p is >= Primitive.I8 and <= Primitive.I256;
        public static bool IsFloat(this Primitive p) => p >= Primitive.F16;

        /// <summary>Width in bits (0 for void; 1 for bool; 32 for char, a Unicode scalar).</summary>
        public static int Bits(this Primitive p) => p switch
        {
            Primitive.Void => 0,
            Primitive.Bool => 1,
            Primitive.Char => 32,
            Primitive.I8 or Primitive.U8 => 8,
            Primitive.I16 or Primitive.U16 or Primitive.F16 => 16,
            Primitive.I32 or Primitive.U32 or Primitive.F32 => 32,
            Primitive.I64 or Primitive.U64 or Primitive.F64 => 64,
            Primitive.I128 or Primitive.U128 or Primitive.F128 => 128,
            _ => 256,
        };

        /// <summary>The keyword the HIR text format uses.</summary>
        public static string Keyword(this Primitive p) => p == Primitive.Void ? "void" : p.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// A type definition: <see cref="IsDefinition"/> means one of this assembly's <see cref="HirAssembly.Types"/>, else an
    /// entry of its <see cref="HirAssembly.TypeRefs"/> (a type of another assembly, by name).
    /// </summary>
    public readonly record struct TypeToken(int Value)
    {
        public static TypeToken Def(int index) => new(index);
        public static TypeToken Ref(int index) => new(~index);
        public bool IsDefinition => Value >= 0;
        public int Index => Value >= 0 ? Value : ~Value;
        public override string ToString() => IsDefinition ? $"T{Index}" : $"TR{Index}";
    }

    /// <summary>
    /// A member (field, method, property, event): <c>Type</c> and <c>Member</c> index a definition of this assembly
    /// (<c>Types[Type].Methods[Member]</c>…); with <c>Type == -1</c>, <c>Member</c> indexes <see cref="HirAssembly.MemberRefs"/>.
    /// </summary>
    public readonly record struct MemberToken(int Type, int Member)
    {
        public static MemberToken Def(int type, int member) => new(type, member);
        public static MemberToken Ref(int index) => new(-1, index);
        public bool IsDefinition => Type >= 0;
        public override string ToString() => IsDefinition ? $"T{Type}.{Member}" : $"MR{Member}";
    }

    /// <summary>A HIR type. Equality is structural (arrays compared element by element).</summary>
    public abstract record HirType
    {
        public static readonly HirType Void = new PrimitiveType(Primitive.Void);
        public static readonly HirType Bool = new PrimitiveType(Primitive.Bool);
        public static readonly HirType Char = new PrimitiveType(Primitive.Char);
        public static readonly HirType I32 = new PrimitiveType(Primitive.I32);
        public static readonly HirType I64 = new PrimitiveType(Primitive.I64);
        public static readonly HirType U8 = new PrimitiveType(Primitive.U8);
        public static readonly HirType F64 = new PrimitiveType(Primitive.F64);

        public static HirType Of(Primitive p) => p switch
        {
            Primitive.Void => Void,
            Primitive.Bool => Bool,
            Primitive.I32 => I32,
            Primitive.I64 => I64,
            _ => new PrimitiveType(p),
        };

        public static NamedType Named(TypeToken type, params HirType[] arguments) => new(type, arguments);
    }

    public sealed record PrimitiveType(Primitive Kind) : HirType;

    /// <summary>A class, struct, interface, enum or named func type, with its type arguments.</summary>
    public sealed record NamedType(TypeToken Type, HirType[] Arguments) : HirType
    {
        public bool Equals(NamedType? other) => other is not null && Type == other.Type && Arguments.AsSpan().SequenceEqual(other.Arguments);
        public override int GetHashCode() => HashCode.Combine(Type, Hash.Of(Arguments));
    }

    /// <summary><c>T[]</c> (rank 1) or <c>T[,]</c>… up to rank 8. Jagged arrays are arrays of arrays.</summary>
    public sealed record ArrayType(HirType Element, int Rank = 1) : HirType;

    /// <summary><c>fixed T[N]</c>: N elements stored inline (in a struct or a field).</summary>
    /// <summary>
    /// An inline array of <c>Count</c> elements (<c>fixed u8[4]</c>), or of <c>Count * sizeof(SizeOf)</c> elements when
    /// <c>SizeOf</c> is given (<c>fixed u8[sizeof(T)]</c>: the length is known once <c>T</c> is).
    /// </summary>
    public sealed record InlineArrayType(HirType Element, int Count, HirType? SizeOf = null) : HirType;

    /// <summary><c>T?</c>.</summary>
    public sealed record NullableType(HirType Inner) : HirType;

    /// <summary><c>&amp;T</c> / <c>&amp;mut T</c>: a borrow, checked by the borrow checker.</summary>
    public sealed record ReferenceType(HirType Target, bool Mutable) : HirType;

    /// <summary><c>T*</c>: a raw pointer (unsafe code).</summary>
    public sealed record PointerType(HirType Target, bool Mutable = true) : HirType;

    /// <summary><c>func&lt;A, B&gt; : R</c>: a function value (a closure, a method of an object, a static function).</summary>
    public sealed record FuncType(HirType[] Parameters, HirType Result) : HirType
    {
        public bool Equals(FuncType? other) => other is not null && Result == other.Result && Parameters.AsSpan().SequenceEqual(other.Parameters);
        public override int GetHashCode() => HashCode.Combine(Result, Hash.Of(Parameters));
    }

    /// <summary><c>func*&lt;A, B&gt; : R</c>: the address of a C function.</summary>
    public sealed record NativeFuncType(HirType[] Parameters, HirType Result) : HirType
    {
        public bool Equals(NativeFuncType? other) => other is not null && Result == other.Result && Parameters.AsSpan().SequenceEqual(other.Parameters);
        public override int GetHashCode() => HashCode.Combine(Result, Hash.Of(Parameters));
    }

    public enum GenericOwner : byte { Type, Method }

    /// <summary>Type parameter <c>Index</c> of the enclosing type (outer types' first) or of the method.</summary>
    public sealed record GenericParamType(GenericOwner Owner, int Index) : HirType;

    /// <summary><c>T...</c>: a variadic parameter's pack, expanded once per type argument it stands for.</summary>
    public sealed record PackExpansionType(HirType Pattern) : HirType;

    public static class HirTypes
    {
        /// <summary>
        /// <paramref name="type"/> with the type's parameters replaced by <paramref name="typeArguments"/> and the method's by
        /// <paramref name="methodArguments"/> (a parameter past the arguments given stays).
        /// </summary>
        public static HirType Substitute(this HirType type, HirType[] typeArguments, HirType[]? methodArguments = null)
        {
            if (typeArguments.Length == 0 && (methodArguments is null || methodArguments.Length == 0))
                return type;
            return Replace(type, typeArguments, methodArguments ?? []);
        }

        private static HirType Replace(HirType type, HirType[] types, HirType[] methods) => type switch
        {
            GenericParamType { Owner: GenericOwner.Type } g when g.Index < types.Length => types[g.Index],
            GenericParamType { Owner: GenericOwner.Method } g when g.Index < methods.Length => methods[g.Index],
            NamedType n when n.Arguments.Length > 0 => new NamedType(n.Type, [.. n.Arguments.Select(a => Replace(a, types, methods))]),
            ArrayType a => a with { Element = Replace(a.Element, types, methods) },
            NullableType n => new NullableType(Replace(n.Inner, types, methods)),
            ReferenceType r => r with { Target = Replace(r.Target, types, methods) },
            PointerType p => p with { Target = Replace(p.Target, types, methods) },
            InlineArrayType i => i with { Element = Replace(i.Element, types, methods), SizeOf = i.SizeOf is null ? null : Replace(i.SizeOf, types, methods) },
            FuncType f => new FuncType([.. f.Parameters.Select(p => Replace(p, types, methods))], Replace(f.Result, types, methods)),
            _ => type,
        };

        /// <summary>Whether <paramref name="type"/> mentions a type parameter.</summary>
        public static bool IsGeneric(this HirType type) => type switch
        {
            GenericParamType => true,
            NamedType n => n.Arguments.Any(IsGeneric),
            ArrayType a => a.Element.IsGeneric(),
            NullableType n => n.Inner.IsGeneric(),
            ReferenceType r => r.Target.IsGeneric(),
            PointerType p => p.Target.IsGeneric(),
            InlineArrayType i => i.Element.IsGeneric() || i.SizeOf?.IsGeneric() == true,
            FuncType f => f.Parameters.Any(IsGeneric) || f.Result.IsGeneric(),
            _ => false,
        };
    }

    internal static class Hash
    {
        public static int Of(HirType[] items)
        {
            var hash = new HashCode();
            foreach (var item in items)
                hash.Add(item);
            return hash.ToHashCode();
        }
    }
}

namespace Larva.IR
{
    /// <summary>Built-in type groups usable in constraints.</summary>
    public enum TypeGroup : byte
    {
        /// <summary>i8 … i256</summary>
        Int,
        /// <summary>u8 … u256</summary>
        UInt,
        /// <summary><c>int | uint</c></summary>
        Integer,
        /// <summary>f16 … f256</summary>
        Float,
        /// <summary><c>integer | float</c></summary>
        Numeric,
        /// <summary>Owns no <c>gc</c> handle, at any depth (borrows and raw pointers do not own).</summary>
        Unmanaged,
        /// <summary>Owns at least one <c>gc</c> handle.</summary>
        Managed,
        /// <summary>Size known at compile time (everything except <c>[T]</c> and structs containing it).</summary>
        FixedSize,
        /// <summary>Duplicable with <c>copy</c>: primitives, <c>&amp;T</c>, <c>*T</c>, <c>gc T</c>.</summary>
        Copy,
        /// <summary>Plain data: all-zero bytes are a valid value (no references, boxes or handles at any depth). <c>zeroed T</c> needs it.</summary>
        Plain,
        /// <summary>An object: a <c>gc</c> handle, or a raw pointer to a struct (objects of the manual heap).</summary>
        Object,
        /// <summary>A value: a primitive or a struct held by value.</summary>
        Value,
        /// <summary>Holds no <c>&amp;</c> / <c>&amp;mut</c> reference at any depth (handles and raw pointers are fine).</summary>
        Unborrowed,
    }

    /// <summary>
    /// Predicate over types, built from groups, exact types and <c>size &lt;= N</c> with <c>&amp;</c>, <c>|</c>, <c>!</c>:
    /// <code>numeric</code> <code>i32 | i64 | f32</code> <code>int &amp; !i256</code> <code>unmanaged &amp; size &lt;= 64</code>
    /// </summary>
    public abstract record TypeConstraint
    {
        public static readonly TypeConstraint Any = new AnyConstraint();
        public static readonly TypeConstraint Int = new GroupConstraint(TypeGroup.Int);
        public static readonly TypeConstraint UInt = new GroupConstraint(TypeGroup.UInt);
        public static readonly TypeConstraint Integer = new GroupConstraint(TypeGroup.Integer);
        public static readonly TypeConstraint Float = new GroupConstraint(TypeGroup.Float);
        public static readonly TypeConstraint Numeric = new GroupConstraint(TypeGroup.Numeric);
        public static readonly TypeConstraint Unmanaged = new GroupConstraint(TypeGroup.Unmanaged);
        public static readonly TypeConstraint Managed = new GroupConstraint(TypeGroup.Managed);
        public static readonly TypeConstraint FixedSize = new GroupConstraint(TypeGroup.FixedSize);
        public static readonly TypeConstraint Copy = new GroupConstraint(TypeGroup.Copy);
        public static readonly TypeConstraint Plain = new GroupConstraint(TypeGroup.Plain);
        public static readonly TypeConstraint Unborrowed = new GroupConstraint(TypeGroup.Unborrowed);
        public static readonly TypeConstraint Object = new GroupConstraint(TypeGroup.Object);

        public static TypeConstraint Exact(IrType type) => new ExactConstraint(type);
        public static TypeConstraint OneOf(params IrType[] types) => new OrConstraint([.. types.Select(Exact)]);
        public static TypeConstraint SizeAtMost(int bits) => new SizeAtMostConstraint(bits);

        public static TypeConstraint operator &(TypeConstraint a, TypeConstraint b) =>
            new AndConstraint([.. Parts<AndConstraint>(a), .. Parts<AndConstraint>(b)]);

        public static TypeConstraint operator |(TypeConstraint a, TypeConstraint b) =>
            new OrConstraint([.. Parts<OrConstraint>(a), .. Parts<OrConstraint>(b)]);

        public static TypeConstraint operator !(TypeConstraint a) => a is NotConstraint neg ? neg.Inner : new NotConstraint(a);

        private static IEnumerable<TypeConstraint> Parts<T>(TypeConstraint c) where T : TypeConstraint => c switch
        {
            AndConstraint conj when typeof(T) == typeof(AndConstraint) => conj.Parts,
            OrConstraint disj when typeof(T) == typeof(OrConstraint) => disj.Parts,
            _ => [c],
        };

        public sealed override string ToString() => Text.IrPrinter.FormatConstraint(this);
    }

    /// <summary>No restriction.</summary>
    public sealed record AnyConstraint : TypeConstraint;

    public sealed record GroupConstraint(TypeGroup Group) : TypeConstraint;

    public sealed record ExactConstraint(IrType Type) : TypeConstraint;

    public sealed record AndConstraint(TypeConstraint[] Parts) : TypeConstraint;

    public sealed record OrConstraint(TypeConstraint[] Parts) : TypeConstraint;

    public sealed record NotConstraint(TypeConstraint Inner) : TypeConstraint;

    /// <summary>Value size in bits is known and at most <see cref="Bits"/>.</summary>
    public sealed record SizeAtMostConstraint(int Bits) : TypeConstraint;

    /// <summary>A type parameter of a function or struct.</summary>
    public sealed record GenericParam(string Name, TypeConstraint Constraint)
    {
        public GenericParam(string name) : this(name, TypeConstraint.Any) { }

        /// <summary>Operations every type argument must provide (operators, conversions, static members); see <see cref="Requirement"/>.</summary>
        public Requirement[] Requirements { get; init; } = [];
    }

    /// <summary>What a constraint check needs: the module (struct layouts) and the generic parameters in scope.</summary>
    public sealed record TypeContext(IrModule Module, IReadOnlyList<GenericParam> Generics)
    {
        public static TypeContext For(IrFunction function, IrModule module) => new(module, function.Generics);
    }

    /// <summary>
    /// Evaluates constraints. <see cref="Satisfies"/> answers "does this type meet the constraint"; for a generic
    /// parameter it asks whether the parameter's own constraint <see cref="Implies"/> the target. Implication is
    /// exact for finite constraints (groups and lists of primitives: every member is checked) and sound but
    /// conservative for open groups.
    /// </summary>
    public static class Constraints
    {
        private static readonly IrType[] Primitives = [.. Enum.GetValues<Primitive>().Select(p => (IrType)new PrimitiveType(p))];

        public static bool Satisfies(IrType type, TypeConstraint constraint, TypeContext context)
        {
            if (type is GenericParamType p)
                return Implies(ParamConstraint(p, context), constraint, context);

            return constraint switch
            {
                AnyConstraint => true,
                GroupConstraint g => InGroup(type, g.Group, context, []),
                ExactConstraint e => type == e.Type,
                AndConstraint a => a.Parts.All(x => Satisfies(type, x, context)),
                OrConstraint o => o.Parts.Any(x => Satisfies(type, x, context)),
                NotConstraint n => !type.IsGeneric() && !Satisfies(type, n.Inner, context),
                SizeAtMostConstraint s => SizeInBits(type, context) is int bits && bits <= s.Bits,
                _ => throw new ArgumentOutOfRangeException(nameof(constraint)),
            };
        }

        /// <summary>True when every type satisfying <paramref name="a"/> also satisfies <paramref name="b"/>.</summary>
        public static bool Implies(TypeConstraint a, TypeConstraint b, TypeContext context)
        {
            if (Members(a, context) is { } members)
                return members.All(m => Satisfies(m, b, context));

            switch (b)
            {
                case AnyConstraint:
                    return true;
                case AndConstraint conj:
                    return conj.Parts.All(part => Implies(a, part, context));
                case OrConstraint disj when disj.Parts.Any(part => Implies(a, part, context)):
                    return true;
            }

            switch (a)
            {
                case AndConstraint conj:
                    return conj.Parts.Any(part => Implies(part, b, context));
                case OrConstraint disj:
                    return disj.Parts.All(part => Implies(part, b, context));
            }

            return (a, b) switch
            {
                (GroupConstraint ga, GroupConstraint gb) => GroupImplies(ga.Group, gb.Group),
                (GroupConstraint ga, NotConstraint { Inner: GroupConstraint gb }) => Disjoint(ga.Group, gb.Group),
                (SizeAtMostConstraint sa, SizeAtMostConstraint sb) => sa.Bits <= sb.Bits,
                (SizeAtMostConstraint, GroupConstraint { Group: TypeGroup.FixedSize }) => true,
                _ => false,
            };
        }

        /// <summary>The finite set of primitives a constraint allows, or <c>null</c> when it is open-ended.</summary>
        public static IReadOnlyList<IrType>? Members(TypeConstraint constraint, TypeContext context)
        {
            switch (constraint)
            {
                case GroupConstraint { Group: TypeGroup.Int or TypeGroup.UInt or TypeGroup.Integer or TypeGroup.Float or TypeGroup.Numeric } g:
                    return [.. Primitives.Where(p => InGroup(p, g.Group, context, []))];
                case ExactConstraint e when !e.Type.IsGeneric():
                    return [e.Type];
                case OrConstraint disj:
                    var parts = disj.Parts.Select(p => Members(p, context)).ToList();
                    return parts.Any(p => p is null) ? null : [.. parts.SelectMany(p => p!).Distinct()];
                case AndConstraint conj:
                    foreach (var part in conj.Parts)
                    {
                        if (Members(part, context) is { } finite)
                            return [.. finite.Where(m => Satisfies(m, conj, context))];
                    }
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>Size of a value in bits, or <c>null</c> when unknown or unsized.</summary>
        public static int? SizeInBits(IrType type, TypeContext context) => type switch
        {
            PrimitiveType { Kind: Primitive.Bool } => 8,
            PrimitiveType p => p.BitWidth,
            ReferenceType or GcType or BoxType or PointerType or FunctionType or OptionalType => 64,
            StructType st => SumFields(st, context),
            FixedArrayType array => array.Length(context.Module) is int length && SizeInBits(array.Element, context) is int bits ? length * bits : null,
            _ => null,
        };

        private static int? SumFields(StructType st, TypeContext context)
        {
            int total = 0;
            foreach (var field in context.Module.Structs[st.Id].Fields)
            {
                if (SizeInBits(field.Substitute(st.Arguments), context) is not int bits)
                    return null;
                total += bits;
            }
            return total;
        }

        private static TypeConstraint ParamConstraint(GenericParamType p, TypeContext context) =>
            p.Index < context.Generics.Count
                ? context.Generics[p.Index].Constraint
                : throw new InvalidOperationException($"T{p.Index} is not in scope.");

        private static bool InGroup(IrType type, TypeGroup group, TypeContext context, HashSet<StructType> visiting)
        {
            if (type is GenericParamType p)
                return Implies(ParamConstraint(p, context), new GroupConstraint(group), context);

            switch (group)
            {
                case TypeGroup.Int: return type.IsSigned;
                case TypeGroup.UInt: return type.IsUnsigned;
                case TypeGroup.Integer: return type.IsInteger;
                case TypeGroup.Float: return type.IsFloat;
                case TypeGroup.Numeric: return type.IsInteger || type.IsFloat;
                // A struct is copied freely when every field is (an enum, a func value, a value of numbers): see below.
                case TypeGroup.Copy when type is not StructType:
                    return type is PrimitiveType or ReferenceType { Mutable: false } or PointerType or GcType or FunctionType
                        || type is FixedArrayType inline && InGroup(inline.Element, group, context, visiting)
                        || type is OptionalType optional && InGroup(optional.Inner, group, context, visiting);
                case TypeGroup.Plain when type is not StructType:
                    return type is PrimitiveType || type is FixedArrayType plain && InGroup(plain.Element, group, context, visiting);
                case TypeGroup.Object:
                    return type is GcType || type is PointerType { Mutable: true, Pointee: StructType };
                case TypeGroup.Value:
                    return type is PrimitiveType { IsUnit: false } || type is StructType;
                // What a handle, a box or a raw pointer leads to is not borrowed by the value itself.
                case TypeGroup.Unborrowed when type is not StructType:
                    return type is not ReferenceType
                        && (type is not FixedArrayType fixedArray || InGroup(fixedArray.Element, group, context, visiting))
                        && (type is not ArrayType array || InGroup(array.Element, group, context, visiting))
                        && (type is not OptionalType maybe || InGroup(maybe.Inner, group, context, visiting));
            }

            if (type is StructType st)
            {
                if (!visiting.Add(st))
                    return group != TypeGroup.Managed; // a cycle adds nothing new
                var fields = context.Module.Structs[st.Id].Fields.Select(f => f.Substitute(st.Arguments));
                bool result = group == TypeGroup.Managed
                    ? fields.Any(f => InGroup(f, group, context, visiting))
                    : fields.All(f => InGroup(f, group, context, visiting));
                // A struct holding objects is not copied bit for bit: reference counting would not see the new owners.
                if (group == TypeGroup.Copy && result)
                    result = !fields.Any(f => InGroup(f, TypeGroup.Managed, context, visiting));
                visiting.Remove(st);
                return result;
            }

            return (group, type) switch
            {
                (TypeGroup.Managed, GcType) => true,
                (TypeGroup.Unmanaged, GcType) => false,
                // Owned contents count; borrowed / raw targets do not.
                (TypeGroup.Managed or TypeGroup.Unmanaged, BoxType box) => InGroup(box.Pointee, group, context, visiting),
                (TypeGroup.Managed or TypeGroup.Unmanaged, ArrayType array) => InGroup(array.Element, group, context, visiting),
                (TypeGroup.Managed or TypeGroup.Unmanaged or TypeGroup.FixedSize, FixedArrayType inline) => InGroup(inline.Element, group, context, visiting),
                (TypeGroup.Managed or TypeGroup.Unmanaged, OptionalType optional) => InGroup(optional.Inner, group, context, visiting),
                (TypeGroup.Managed, _) => false,
                (TypeGroup.Unmanaged, _) => true,
                (TypeGroup.FixedSize, ArrayType) => false,
                (TypeGroup.FixedSize, _) => true,
                _ => false,
            };
        }

        private static bool GroupImplies(TypeGroup a, TypeGroup b) => a == b || (a, b) switch
        {
            (TypeGroup.Copy, TypeGroup.FixedSize) => true,
            // A `T: plain` may be copied: its arguments own nothing, so duplicating their bytes is safe. Concrete structs
            // stay affine (moved) by choice, which is why this is stated for parameters rather than derived per type.
            (TypeGroup.Plain, TypeGroup.Copy or TypeGroup.FixedSize or TypeGroup.Unmanaged or TypeGroup.Unborrowed) => true,
            // Objects are handles: copied freely, of a fixed size.
            (TypeGroup.Object, TypeGroup.Copy or TypeGroup.FixedSize) => true,
            (TypeGroup.Value, TypeGroup.FixedSize) => true,
            _ => false,
        };

        private static bool Disjoint(TypeGroup a, TypeGroup b) =>
            (a, b) is (TypeGroup.Managed, TypeGroup.Unmanaged) or (TypeGroup.Unmanaged, TypeGroup.Managed);
    }
}

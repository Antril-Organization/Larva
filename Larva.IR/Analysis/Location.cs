namespace Larva.IR.Analysis
{
    /// <summary>A point in a function: statement <see cref="Index"/> of block <see cref="Block"/>; the terminator sits at <c>Statements.Length</c>.</summary>
    public readonly record struct Location(int Block, int Index)
    {
        public override string ToString() => $"bb{Block}[{Index}]";
    }

    internal static class PlaceExtensions
    {
        /// <summary>True when <paramref name="prefix"/> is <paramref name="place"/> or one of its parents.</summary>
        public static bool IsPrefixOf(this Place prefix, Place place) =>
            prefix.Local == place.Local
            && prefix.Projections.Length <= place.Projections.Length
            && prefix.Projections.AsSpan().SequenceEqual(place.Projections.AsSpan(0, prefix.Projections.Length));

        /// <summary>
        /// True when both places may name the same memory: one is a prefix of the other,
        /// treating any two index projections as potentially equal.
        /// </summary>
        public static bool Overlaps(this Place a, Place b)
        {
            if (a.Local != b.Local)
                return false;
            int common = Math.Min(a.Projections.Length, b.Projections.Length);
            for (int i = 0; i < common; i++)
            {
                var (p, q) = (a.Projections[i], b.Projections[i]);
                if (p is IndexProjection && q is IndexProjection)
                    continue;
                if (p != q)
                    return false;
            }
            return true;
        }

        public static bool HasDeref(this Place place) => place.Projections.Any(p => p is DerefProjection);

        /// <summary>
        /// True when the place is reached through a pointer the base local does not own (<c>&amp;</c>, <c>*</c>, <c>gc</c>).
        /// Data behind a <c>box</c> still belongs to the local and dies with it.
        /// </summary>
        public static bool IsBehindNonOwningPointer(this Place place, IrFunction function, IrModule module)
        {
            for (int j = 0; j < place.Projections.Length; j++)
            {
                if (place.Projections[j] is DerefProjection
                    && function.TypeOf(new Place(place.Local, place.Projections[..j]), module) is not BoxType)
                    return true;
            }
            return false;
        }

        public static bool HasIndex(this Place place) => place.Projections.Any(p => p is IndexProjection);

        /// <summary>Locals read to evaluate the place itself: the base local when dereferenced, and index locals.</summary>
        public static IEnumerable<int> IndexLocals(this Place place) =>
            place.Projections.OfType<IndexProjection>().Select(i => i.Local);
    }

    internal static class TypeExtensions
    {
        /// <summary>Values duplicated by <c>copy</c>: the <see cref="TypeGroup.Copy"/> group (generic parameters by constraint).</summary>
        public static bool IsCopy(this IrType type, TypeContext context) => Constraints.Satisfies(type, TypeConstraint.Copy, context);

        /// <summary>
        /// Whether a value of this type may carry borrows. Conservative for generic parameters: only a finite set of
        /// primitives is known to hold none.
        /// </summary>
        public static bool ContainsReference(this IrType type, TypeContext context) => ContainsReference(type, context, []);

        private static bool ContainsReference(IrType type, TypeContext context, HashSet<StructType> visiting) => type switch
        {
            ReferenceType => true,
            // A parameter holds references unless its constraint rules them out (`plain`, or a closed set of types without them).
            GenericParamType p => !Constraints.Satisfies(p, TypeConstraint.Plain, context) && !Constraints.Satisfies(p, TypeConstraint.Unborrowed, context)
                && (Constraints.Members(context.Generics[p.Index].Constraint, context) is not { } members || members.Any(m => ContainsReference(m, context, visiting))),
            ArrayType array => ContainsReference(array.Element, context, visiting),
            FixedArrayType inline => ContainsReference(inline.Element, context, visiting),
            BoxType box => ContainsReference(box.Pointee, context, visiting),
            OptionalType optional => ContainsReference(optional.Inner, context, visiting),
            GcType gc => ContainsReference(gc.Pointee, context, visiting),
            StructType st when visiting.Add(st) => context.Module.Structs[st.Id].Fields
                .Any(f => ContainsReference(f.Substitute(st.Arguments), context, visiting)),
            _ => false,
        };
    }
}

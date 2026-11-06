namespace Larva.IR.Lowering
{
    /// <summary>Which stack slots the tracing collector must see: those that may hold a <c>gc</c> handle or own heap memory.</summary>
    public static class StackRoots
    {
        /// <summary>
        /// Whether a value of <paramref name="type"/> may hold the address of something the collector tracks: a <c>gc</c>
        /// object, or a heap cell it owns (a <c>box</c>: arrays and boxed values, freed by the collector once unreachable).
        /// </summary>
        public static bool MayHoldTracked(IrType type, TypeContext context) =>
            !type.IsUnit && (!Constraints.Satisfies(type, TypeConstraint.Unmanaged, context) || OwnsCell(type, context, []));

        private static bool OwnsCell(IrType type, TypeContext context, HashSet<StructType> visiting)
        {
            switch (type)
            {
                case BoxType:
                    return true;
                case OptionalType optional:
                    return OwnsCell(optional.Inner, context, visiting);
                case FixedArrayType inline:
                    return OwnsCell(inline.Element, context, visiting);
                case GenericParamType:
                    return !Constraints.Satisfies(type, TypeConstraint.Plain, context);
                case StructType st when st.Id >= 0 && st.Id < context.Module.Structs.Length:
                {
                    if (!visiting.Add(st))
                        return false;
                    bool owns = context.Module.Structs[st.Id].Fields.Any(f => OwnsCell(f.Substitute(st.Arguments), context, visiting));
                    visiting.Remove(st);
                    return owns;
                }
                default:
                    return false;
            }
        }
    }
}

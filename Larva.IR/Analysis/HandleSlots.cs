namespace Larva.IR.Analysis
{
    /// <summary>
    /// The places inside a value that hold <c>gc</c> handles: its fields and, through nested structs, theirs. What a
    /// release has to visit when memory holding such a value goes away without the ownership analysis seeing it (a
    /// <c>free</c> of raw memory under reference counting).
    /// </summary>
    public static class HandleSlots
    {
        public static IEnumerable<Place> Of(Place root, IrType type, IrModule module, int depth = 0)
        {
            switch (type)
            {
                case GcType:
                case OptionalType { Inner: GcType }:
                    yield return root;
                    break;
                case StructType { Arguments.Length: 0 } st when depth < 8:
                    var def = module.Structs[st.Id];
                    for (int f = 0; f < def.Fields.Length; f++)
                    {
                        foreach (var place in Of(root.Field(f), def.Fields[f], module, depth + 1))
                            yield return place;
                    }
                    break;
            }
        }
    }
}

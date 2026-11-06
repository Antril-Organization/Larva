using Larva.IR.Text;

namespace Larva.IR.Linking
{
    /// <summary>
    /// Types as text that another link of the same modules reads back: structs by their linked name (<c>module::Name</c>,
    /// or <c>*::Name</c> for link-once ones) with their arguments, and the usual type syntax around them. Function types
    /// and inline buffers are not written.
    /// </summary>
    public static class TypeNames
    {
        public static string Format(IrType type, IrModule module) => type switch
        {
            FunctionType or FixedArrayType or GenericParamType => throw new NotSupportedException($"`{IrPrinter.FormatType(type, module)}` has no portable name"),
            _ => IrPrinter.FormatType(type, module),
        };

        /// <summary>The type <paramref name="text"/> names in <paramref name="module"/>; null when a struct is missing.</summary>
        public static IrType? Parse(string text, IrModule module)
        {
            var byName = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int s = 0; s < module.Structs.Length; s++)
                byName.TryAdd(module.Structs[s].Name, s);
            int at = 0;
            var type = Parse(text, ref at, byName);
            return at == text.Length ? type : null;
        }

        private static readonly Dictionary<string, IrType> Primitives = Enum.GetValues<Primitive>()
            .Select(p => (IrType)new PrimitiveType(p)).ToDictionary(t => t.ToString()!, StringComparer.Ordinal);

        private static IrType? Parse(string text, ref int at, Dictionary<string, int> structs)
        {
            bool Take(string prefix, ref int position)
            {
                if (string.CompareOrdinal(text, position, prefix, 0, prefix.Length) != 0)
                    return false;
                position += prefix.Length;
                return true;
            }
            if (Take("&mut ", ref at))
                return Parse(text, ref at, structs) is { } a ? IrType.RefMut(a) : null;
            if (Take("&", ref at))
                return Parse(text, ref at, structs) is { } b ? IrType.Ref(b) : null;
            bool linkOnce = string.CompareOrdinal(text, at, "*::", 0, 3) == 0 || string.CompareOrdinal(text, at, "*@", 0, 2) == 0; // a link-once struct's name (`*::`, or `*@N::` in a GC domain), not a pointer
            if (!linkOnce && Take("*mut ", ref at))
                return Parse(text, ref at, structs) is { } c ? IrType.PtrMut(c) : null;
            if (!linkOnce && Take("*", ref at))
                return Parse(text, ref at, structs) is { } d ? IrType.Ptr(d) : null;
            if (Take("gc ", ref at))
                return Parse(text, ref at, structs) is { } e ? IrType.Gc(e) : null;
            if (Take("box ", ref at))
                return Parse(text, ref at, structs) is { } f ? IrType.Box(f) : null;
            if (Take("?", ref at))
                return Parse(text, ref at, structs) is { } g ? new OptionalType(g) : null;
            if (Take("[", ref at))
            {
                var element = Parse(text, ref at, structs);
                return element is not null && Take("]", ref at) ? IrType.ArrayOf(element) : null;
            }
            int start = at;
            while (at < text.Length && text[at] is not ('<' or '>' or ',' or ' '))
                at++;
            string name = text[start..at];
            if (Primitives.TryGetValue(name, out var primitive))
                return primitive;
            if (!structs.TryGetValue(name, out int id))
                return null;
            var arguments = new List<IrType>();
            if (Take("<", ref at))
            {
                do
                {
                    Take(" ", ref at);
                    if (Parse(text, ref at, structs) is not { } argument)
                        return null;
                    arguments.Add(argument);
                } while (Take(",", ref at));
                if (!Take(">", ref at))
                    return null;
            }
            return new StructType(id, [.. arguments]);
        }
    }
}

namespace Larva.IR.Lowering
{
    /// <summary>Properties a witness reports about its type.</summary>
    [Flags]
    public enum WitnessFlags : byte
    {
        None = 0,
        Copy = 1 << 0,
        Unmanaged = 1 << 1,
        Managed = 1 << 2,
        FixedSize = 1 << 3,
    }

    /// <summary>
    /// Memory layout of a concrete type, in bytes. <see cref="GcOffsets"/> lists where <c>gc</c> handles live inside
    /// the value (the tracing GC's pointer map); handles behind a <c>box</c> live in the box's own allocation.
    /// </summary>
    public sealed record TypeLayout(int Size, int Align, WitnessFlags Flags, int[] GcOffsets)
    {
        public override string ToString() =>
            $"size {Size}, align {Align}, {(Flags == WitnessFlags.None ? "-" : Flags.ToString().ToLowerInvariant().Replace(", ", "|"))}" +
            $", gc [{string.Join(", ", GcOffsets)}]";
    }

    /// <summary>
    /// C-like sequential layout: primitives aligned to their size (at most 16 bytes), pointers 8 bytes,
    /// struct fields in order with padding, size rounded up to the alignment.
    /// </summary>
    public static class Layouts
    {
        /// <summary>
        /// The bytes of a static array as every backend lays out arrays: the length as a u64 in a header of
        /// max(8, align) bytes, then the elements little-endian at their aligned stride.
        /// </summary>
        public static byte[] StaticBytes(StaticData data, IrModule module, out int header)
        {
            var layout = Of(data.Element, module) ?? throw new InvalidOperationException($"static data of unsized `{data.Element}`");
            int stride = Math.Max(1, (layout.Size + layout.Align - 1) / layout.Align * layout.Align);
            header = Math.Max(8, layout.Align);
            var bytes = new byte[header + data.Values.Length * stride];
            BitConverter.TryWriteBytes(bytes.AsSpan(0, 8), (ulong)data.Values.Length);
            for (int i = 0; i < data.Values.Length; i++)
            {
                var value = data.Values[i];
                if (value.Sign < 0)
                    value += System.Numerics.BigInteger.One << (layout.Size * 8);
                var raw = value.ToByteArray(isUnsigned: true, isBigEndian: false);
                raw.AsSpan(0, Math.Min(raw.Length, layout.Size)).CopyTo(bytes.AsSpan(header + i * stride));
            }
            return bytes;
        }

        /// <summary>Layout of a concrete type, or <c>null</c> for generic or dynamically sized types.</summary>
        public static TypeLayout? Of(IrType type, IrModule module)
        {
            if (type.IsGeneric())
                return null;

            List<int> gcOffsets = [];
            if (Measure(type, module, 0, gcOffsets, []) is not var (size, align))
                return null;

            TypeContext context = new(module, []);
            var flags = WitnessFlags.None;
            if (Constraints.Satisfies(type, TypeConstraint.Copy, context)) flags |= WitnessFlags.Copy;
            if (Constraints.Satisfies(type, TypeConstraint.Unmanaged, context)) flags |= WitnessFlags.Unmanaged;
            if (Constraints.Satisfies(type, TypeConstraint.Managed, context)) flags |= WitnessFlags.Managed;
            if (Constraints.Satisfies(type, TypeConstraint.FixedSize, context)) flags |= WitnessFlags.FixedSize;
            return new TypeLayout(size, align, flags, [.. gcOffsets]);
        }

        /// <summary>
        /// The collector's map of a concrete type: where <c>gc</c> handles are (<paramref name="Handles"/>), where owned
        /// heap cells (<c>box</c>) are (<paramref name="Cells"/>), and whether it holds borrows (<c>&amp;T</c>, which may point
        /// into objects: such a value is scanned word by word, its object never moved). Null for unsized or generic types.
        /// </summary>
        public static (int[] Handles, int[] Cells, bool Ambiguous)? PointerMap(IrType type, IrModule module)
        {
            if (type.IsGeneric())
                return null;
            List<int> handles = [], cells = [];
            bool ambiguous = false;
            bool Walk(IrType t, int offset, HashSet<StructType> visiting)
            {
                switch (t)
                {
                    case GcType:
                        handles.Add(offset);
                        return true;
                    case OptionalType optional:
                        return Walk(optional.Inner, offset, visiting);
                    case BoxType:
                        cells.Add(offset);
                        return true;
                    case ReferenceType:
                        ambiguous = true;
                        return true;
                    case FixedArrayType array:
                    {
                        if (Measure(array.Element, module, 0, [], visiting) is not var (elementSize, elementAlign))
                            return false;
                        int length = array.Count;
                        if (array.SizeOf is not null)
                        {
                            if (Measure(array.SizeOf, module, 0, [], visiting) is not var (unitSize, _))
                                return false;
                            length *= unitSize;
                        }
                        int stride = AlignUp(Math.Max(elementSize, 0), elementAlign);
                        for (int i = 0; i < length; i++)
                            if (!Walk(array.Element, offset + i * stride, visiting))
                                return false;
                        return true;
                    }
                    case StructType st when visiting.Add(st):
                    {
                        int size = 0;
                        var walked = module.Structs[st.Id].Fields;
                        for (int f = 0; f < walked.Length; f++)
                        {
                            var fieldType = walked[f].Substitute(st.Arguments);
                            bool last = f == walked.Length - 1;
                            if (FieldMeasure(fieldType, last, module, visiting) is not var (fieldSize, fieldAlign))
                                return false;
                            size = AlignUp(size, fieldAlign);
                            // A trailing array's elements hold no references (Trailing), so only its length is there.
                            if (!(last && fieldType is ArrayType) && !Walk(fieldType, offset + size, visiting))
                                return false;
                            size += fieldSize;
                        }
                        visiting.Remove(st);
                        return true;
                    }
                    case StructType:
                        return false;
                    default:
                        return Measure(t, module, 0, [], visiting) is not null;
                }
            }
            return Walk(type, 0, []) ? ([.. handles], [.. cells], ambiguous) : null;
        }

        /// <summary>Byte offset of every field of a concrete struct (same rules as <see cref="Of"/>), or <c>null</c> if unsized.</summary>
        public static int[]? FieldOffsets(StructType type, IrModule module)
        {
            if (type.IsGeneric())
                return null;
            var fields = module.Structs[type.Id].Fields;
            var offsets = new int[fields.Length];
            int size = 0;
            for (int i = 0; i < fields.Length; i++)
            {
                if (FieldMeasure(fields[i].Substitute(type.Arguments), i == fields.Length - 1, module, [type]) is not var (fieldSize, fieldAlign))
                    return null;
                size = AlignUp(size, fieldAlign);
                offsets[i] = size;
                size += fieldSize;
            }
            return offsets;
        }

        private static (int Size, int Align)? Measure(IrType type, IrModule module, int offset, List<int> gcOffsets, HashSet<StructType> visiting)
        {
            switch (type)
            {
                case PrimitiveType { Kind: Primitive.Unit }:
                    return (0, 1);
                case PrimitiveType { Kind: Primitive.Bool }:
                    return (1, 1);
                case PrimitiveType primitive:
                    int bytes = primitive.BitWidth!.Value / 8;
                    return (bytes, Math.Min(bytes, 16));
                case GcType:
                    gcOffsets.Add(offset);
                    return (8, 8);
                case OptionalType optional:
                    return Measure(optional.Inner, module, offset, gcOffsets, visiting);
                case ReferenceType or BoxType or PointerType or FunctionType:
                    return (8, 8);
                case FixedArrayType array:
                {
                    if (Measure(array.Element, module, 0, [], visiting) is not var (elementSize, elementAlign))
                        return null;
                    int length = array.Count;
                    if (array.SizeOf is not null)
                    {
                        if (Measure(array.SizeOf, module, 0, [], visiting) is not var (unitSize, _))
                            return null;
                        length *= unitSize;
                    }
                    int stride = AlignUp(Math.Max(elementSize, 0), elementAlign);
                    for (int i = 0; i < length; i++)
                        Measure(array.Element, module, offset + i * stride, gcOffsets, visiting);
                    return (length * stride, elementAlign);
                }
                case StructType st when visiting.Add(st):
                    int size = 0, align = 1;
                    var measured = module.Structs[st.Id].Fields;
                    for (int f = 0; f < measured.Length; f++)
                    {
                        var fieldType = measured[f].Substitute(st.Arguments);
                        // Measure once without recording to learn the alignment, then place the field.
                        if (FieldMeasure(fieldType, f == measured.Length - 1, module, visiting) is not var (fieldSize, fieldAlign))
                            return null;
                        size = AlignUp(size, fieldAlign);
                        Measure(fieldType, module, offset + size, gcOffsets, visiting);
                        size += fieldSize;
                        align = Math.Max(align, fieldAlign);
                    }
                    visiting.Remove(st);
                    return (AlignUp(size, align), align);
                default:
                    return null; // [T], generic parameters, recursive-by-value structs
            }
        }

        private static int AlignUp(int value, int align) => (value + align - 1) / align * align;

        /// <summary>
        /// A field's size and alignment: a struct's last field may be an array <c>[T]</c> kept in place (its length word,
        /// 8 bytes; the elements follow the struct), when its elements have a layout, hold no references and align to at
        /// most 8 bytes.
        /// </summary>
        private static (int Size, int Align)? FieldMeasure(IrType field, bool last, IrModule module, HashSet<StructType> visiting)
        {
            if (field is ArrayType array)
            {
                if (!last || !array.Element.HoldsNoReferences(module) || Measure(array.Element, module, 0, [], visiting) is not var (_, elementAlign) || elementAlign > 8)
                    return null;
                return (8, 8);
            }
            return Measure(field, module, 0, [], visiting);
        }

        /// <summary>
        /// The layout of a struct whose last field is an array kept in place: where its length is, where the elements
        /// start (the struct's size as the collector sees it: the length is the 8 bytes before), their stride, and their
        /// type. Null for any other struct, or a generic one.
        /// </summary>
        public static (int LengthOffset, int Header, int Stride, IrType Element)? Trailing(StructType type, IrModule module)
        {
            var fields = module.Structs[type.Id].Fields;
            if (fields.Length == 0 || fields[^1].Substitute(type.Arguments) is not ArrayType array || FieldOffsets(type, module) is not { } offsets
                || Of(array.Element, module) is not { } element)
                return null;
            int stride = Math.Max(1, AlignUp(element.Size, element.Align));
            return (offsets[^1], offsets[^1] + 8, stride, array.Element);
        }
    }
}

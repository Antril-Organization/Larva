using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

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
    /// <remarks>
    /// Each struct and inline array is measured once per module (its size, alignment and where its handles are, from
    /// offset 0) and remembered: a field is placed from what its type measured, an inline array is its element times its
    /// length. Lowering and the back ends ask from several threads, so the memory is concurrent.
    /// </remarks>
    public static class Layouts
    {
        /// <summary>Size, alignment and <c>gc</c> handle offsets of a sized type, from offset 0.</summary>
        private sealed record Shape(int Size, int Align, int[] GcOffsets);

        /// <summary>The collector's map of a type, from offset 0 (see <see cref="PointerMap"/>).</summary>
        private sealed record Map(int[] Handles, int[] Cells, bool Ambiguous);

        private sealed class Memory
        {
            public readonly ConcurrentDictionary<IrType, Shape?> Shapes = new();
            public readonly ConcurrentDictionary<IrType, Map?> Maps = new();
            public readonly ConcurrentDictionary<IrType, TypeLayout?> Layouts = new();
        }

        private static readonly ConditionalWeakTable<IrModule, Memory> memories = [];

        private static Memory MemoryOf(IrModule module) => memories.GetValue(module, _ => new Memory());

        private static readonly Shape Empty = new(0, 1, []), Byte = new(1, 1, []), Pointer = new(8, 8, []), Handle = new(8, 8, [0]);

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
            var memory = MemoryOf(module);
            if (memory.Layouts.TryGetValue(type, out var known))
                return known;

            TypeLayout? layout = null;
            if (Measure(type, module, memory, []) is { } shape)
            {
                TypeContext context = new(module, []);
                var flags = WitnessFlags.None;
                if (Constraints.Satisfies(type, TypeConstraint.Copy, context)) flags |= WitnessFlags.Copy;
                if (Constraints.Satisfies(type, TypeConstraint.Unmanaged, context)) flags |= WitnessFlags.Unmanaged;
                if (Constraints.Satisfies(type, TypeConstraint.Managed, context)) flags |= WitnessFlags.Managed;
                if (Constraints.Satisfies(type, TypeConstraint.FixedSize, context)) flags |= WitnessFlags.FixedSize;
                layout = new TypeLayout(shape.Size, shape.Align, flags, shape.GcOffsets);
            }
            return memory.Layouts.GetOrAdd(type, layout);
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
            return MapOf(type, module, MemoryOf(module), []) is { } map ? ([.. map.Handles], [.. map.Cells], map.Ambiguous) : null;
        }

        private static readonly Map NoPointers = new([], [], false), OneHandle = new([0], [], false), OneCell = new([], [0], false),
            Borrow = new([], [], true);

        private static Map? MapOf(IrType type, IrModule module, Memory memory, HashSet<StructType> visiting)
        {
            switch (type)
            {
                case GcType:
                    return OneHandle;
                case OptionalType optional:
                    return MapOf(optional.Inner, module, memory, visiting);
                case BoxType:
                    return OneCell;
                case ReferenceType:
                    return Borrow;
                case FixedArrayType or StructType:
                    break;
                default:
                    return Measure(type, module, memory, visiting) is null ? null : NoPointers;
            }
            if (memory.Maps.TryGetValue(type, out var known))
                return known;

            Map? map;
            if (type is FixedArrayType array)
            {
                if (Measure(array.Element, module, memory, visiting) is not { } element || Length(array, module, memory, visiting) is not int length
                    || MapOf(array.Element, module, memory, visiting) is not { } inner)
                    return null;
                int stride = AlignUp(Math.Max(element.Size, 0), element.Align);
                map = length == 0 || inner.Handles.Length == 0 && inner.Cells.Length == 0
                    ? new Map([], [], length > 0 && inner.Ambiguous)
                    : new Map(Repeat(inner.Handles, length, stride), Repeat(inner.Cells, length, stride), inner.Ambiguous);
            }
            else
            {
                var st = (StructType)type;
                if (!visiting.Add(st))
                    return null;
                map = MapOfStruct(st, module, memory, visiting);
                visiting.Remove(st);
            }
            return memory.Maps.GetOrAdd(type, map);
        }

        private static Map? MapOfStruct(StructType st, IrModule module, Memory memory, HashSet<StructType> visiting)
        {
            List<int> handles = [], cells = [];
            bool ambiguous = false;
            int size = 0;
            var fields = module.Structs[st.Id].Fields;
            for (int f = 0; f < fields.Length; f++)
            {
                var fieldType = fields[f].Substitute(st.Arguments);
                bool last = f == fields.Length - 1;
                if (FieldMeasure(fieldType, last, module, memory, visiting) is not var (fieldSize, fieldAlign))
                    return null;
                size = AlignUp(size, fieldAlign);
                // A trailing array's elements hold no references (Trailing), so only its length is there.
                if (!(last && fieldType is ArrayType))
                {
                    if (MapOf(fieldType, module, memory, visiting) is not { } inner)
                        return null;
                    foreach (int handle in inner.Handles)
                        handles.Add(size + handle);
                    foreach (int cell in inner.Cells)
                        cells.Add(size + cell);
                    ambiguous |= inner.Ambiguous;
                }
                size += fieldSize;
            }
            return new Map([.. handles], [.. cells], ambiguous);
        }

        /// <summary>Byte offset of every field of a concrete struct (same rules as <see cref="Of"/>), or <c>null</c> if unsized.</summary>
        public static int[]? FieldOffsets(StructType type, IrModule module)
        {
            if (type.IsGeneric())
                return null;
            var memory = MemoryOf(module);
            var fields = module.Structs[type.Id].Fields;
            var offsets = new int[fields.Length];
            int size = 0;
            for (int i = 0; i < fields.Length; i++)
            {
                if (FieldMeasure(fields[i].Substitute(type.Arguments), i == fields.Length - 1, module, memory, [type]) is not var (fieldSize, fieldAlign))
                    return null;
                size = AlignUp(size, fieldAlign);
                offsets[i] = size;
                size += fieldSize;
            }
            return offsets;
        }

        /// <summary>
        /// A sized type's shape; <c>null</c> for <c>[T]</c>, generic parameters and structs that hold themselves by value
        /// (<paramref name="visiting"/>: the structs being measured). Whether a type is sized does not depend on where it
        /// is asked from (a struct met again while measuring it is in a cycle, unsized from anywhere), so it is remembered.
        /// </summary>
        private static Shape? Measure(IrType type, IrModule module, Memory memory, HashSet<StructType> visiting)
        {
            switch (type)
            {
                case PrimitiveType { Kind: Primitive.Unit }:
                    return Empty;
                case PrimitiveType { Kind: Primitive.Bool }:
                    return Byte;
                case PrimitiveType primitive:
                    int bytes = primitive.BitWidth!.Value / 8;
                    return new Shape(bytes, Math.Min(bytes, 16), []);
                case GcType:
                    return Handle;
                case OptionalType optional:
                    return Measure(optional.Inner, module, memory, visiting);
                case ReferenceType or BoxType or PointerType or FunctionType:
                    return Pointer;
                case FixedArrayType or StructType:
                    break;
                default:
                    return null; // [T], generic parameters
            }
            if (memory.Shapes.TryGetValue(type, out var known))
                return known;

            Shape? shape;
            if (type is FixedArrayType array)
            {
                if (Measure(array.Element, module, memory, visiting) is not { } element || Length(array, module, memory, visiting) is not int length)
                    return null;
                int stride = AlignUp(Math.Max(element.Size, 0), element.Align);
                shape = new Shape(length * stride, element.Align, Repeat(element.GcOffsets, length, stride));
            }
            else
            {
                var st = (StructType)type;
                if (!visiting.Add(st))
                    return null; // recursive by value
                shape = MeasureStruct(st, module, memory, visiting);
                visiting.Remove(st);
            }
            return memory.Shapes.GetOrAdd(type, shape);
        }

        private static Shape? MeasureStruct(StructType st, IrModule module, Memory memory, HashSet<StructType> visiting)
        {
            int size = 0, align = 1;
            List<int> gcOffsets = [];
            var fields = module.Structs[st.Id].Fields;
            for (int f = 0; f < fields.Length; f++)
            {
                var fieldType = fields[f].Substitute(st.Arguments);
                if (FieldMeasure(fieldType, f == fields.Length - 1, module, memory, visiting) is not var (fieldSize, fieldAlign))
                    return null;
                size = AlignUp(size, fieldAlign);
                // A trailing array (only its length is here) holds no handles in the struct.
                if (fieldType is not ArrayType && Measure(fieldType, module, memory, visiting) is { GcOffsets: { Length: > 0 } inner })
                {
                    foreach (int offset in inner)
                        gcOffsets.Add(size + offset);
                }
                size += fieldSize;
                align = Math.Max(align, fieldAlign);
            }
            return new Shape(AlignUp(size, align), align, [.. gcOffsets]);
        }

        /// <summary>The element count of an inline array: <c>Count</c>, times the size of <c>SizeOf</c> when it has one.</summary>
        private static int? Length(FixedArrayType array, IrModule module, Memory memory, HashSet<StructType> visiting)
        {
            if (array.SizeOf is null)
                return array.Count;
            return Measure(array.SizeOf, module, memory, visiting) is { } unit ? array.Count * unit.Size : null;
        }

        /// <summary><paramref name="offsets"/> once per element of <paramref name="length"/> elements at <paramref name="stride"/>.</summary>
        private static int[] Repeat(int[] offsets, int length, int stride)
        {
            if (offsets.Length == 0 || length <= 0)
                return [];
            var all = new int[offsets.Length * length];
            for (int i = 0, at = 0; i < length; i++)
            {
                foreach (int offset in offsets)
                    all[at++] = i * stride + offset;
            }
            return all;
        }

        private static int AlignUp(int value, int align) => (value + align - 1) / align * align;

        /// <summary>
        /// A field's size and alignment: a struct's last field may be an array <c>[T]</c> kept in place (its length word,
        /// 8 bytes; the elements follow the struct), when its elements have a layout, hold no references and align to at
        /// most 8 bytes.
        /// </summary>
        private static (int Size, int Align)? FieldMeasure(IrType field, bool last, IrModule module, Memory memory, HashSet<StructType> visiting)
        {
            if (field is ArrayType array)
            {
                if (!last || !array.Element.HoldsNoReferences(module) || Measure(array.Element, module, memory, visiting) is not { } element || element.Align > 8)
                    return null;
                return (8, 8);
            }
            return Measure(field, module, memory, visiting) is { } shape ? (shape.Size, shape.Align) : null;
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

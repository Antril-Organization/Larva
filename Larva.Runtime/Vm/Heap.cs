using System.Numerics;
using Larva.IR;
using Larva.IR.Lowering;
using Larva.IR.Text;

namespace Larva.Runtime.Vm
{
    public enum AllocationKind : byte
    {
        /// <summary>A local or argument of a running call (freed when it returns).</summary>
        Frame,
        /// <summary><c>gc.new</c>: reclaimed by the GC mode.</summary>
        Gc,
        /// <summary><c>gc.alloc</c>: <c>box T</c> or <c>*mut T</c>, freed with <c>gc.free</c>.</summary>
        Heap,
        /// <summary>Argument / result buffers of a reflective invoke.</summary>
        Buffer,
        /// <summary>A module's read-only static array (<c>lit.data</c>): never freed, never written.</summary>
        Static,
        /// <summary>
        /// Memory outside the interpreter, at <see cref="Allocation.NativeBase"/>: native memory the program allocated
        /// (<c>mem.alloc</c>, C code can use it), or memory a C function gave (its size unknown).
        /// </summary>
        Native,
    }

    public sealed class Allocation(int id, AllocationKind kind, IrType type, int size)
    {
        public int Id { get; } = id;
        public AllocationKind Kind { get; } = kind;
        public IrType Type { get; } = type;
        public byte[] Data { get; } = new byte[size];
        public bool Freed { get; internal set; }
        public int RefCount { get; internal set; } = 1;
        public int Pins { get; internal set; }
        public int? Region { get; internal set; }
        /// <summary>The GC domain of the function that made it (gc objects; 0 in the program's own domain).</summary>
        public int Domain { get; internal set; }
        /// <summary>Whether retain and release count it (false for objects of a domain without counting in a mixed program).</summary>
        public bool Counted { get; internal set; } = true;
        /// <summary>The function the object's death calls (<c>gc.newfin</c>), until it has run.</summary>
        public int? Finalizer { get; internal set; }
        internal bool Marked;
        internal bool Sealed;

        /// <summary>Where a <see cref="AllocationKind.Native"/> allocation's bytes are.</summary>
        public nint NativeBase { get; init; }

        /// <summary>Its size (<see cref="int.MaxValue"/> when C gave it and did not say).</summary>
        public int NativeSize { get; init; }

        /// <summary>Allocated by the program (freed by it), not memory C owns.</summary>
        public bool OwnsNative { get; init; }

        /// <summary>The bytes that can be read and written.</summary>
        public int Length => Kind == AllocationKind.Native ? NativeSize : Data.Length;

        /// <summary><paramref name="size"/> bytes at <paramref name="offset"/>, wherever they live.</summary>
        internal unsafe Span<byte> Bytes(int offset, int size) =>
            Kind == AllocationKind.Native ? new Span<byte>((byte*)NativeBase + offset, size) : Data.AsSpan(offset, size);

        public override string ToString() => $"#{Id} {Kind} {IrPrinter.FormatType(Type)} ({Data.Length}B){(Freed ? " freed" : "")}";
    }

    /// <summary>
    /// The interpreter's memory: every local, argument and heap cell is an allocation, so addresses (<c>ldloca</c>,
    /// <c>ldflda</c>, pointers in memory) work uniformly. Reads and writes are typed; touching freed memory is an error.
    /// </summary>
    public sealed class Heap(IrModule shapes)
    {
        private readonly Dictionary<int, Allocation> allocations = [];
        private readonly Dictionary<IrType, TypeLayout> layouts = [];
        private int nextId = 1;

        public int TotalAllocations { get; private set; }
        public int TotalFrees { get; private set; }

        public IEnumerable<Allocation> Live => allocations.Values.Where(a => !a.Freed);

        public Allocation Allocate(AllocationKind kind, IrType type, int? size = null)
        {
            var allocation = new Allocation(nextId++, kind, type, size ?? LayoutOf(type).Size);
            allocations[allocation.Id] = allocation;
            if (kind is AllocationKind.Gc or AllocationKind.Heap)
                TotalAllocations++;
            return allocation;
        }

        public void Free(Address address, string why)
        {
            if (address.IsNull)
                return;
            var allocation = Get(address, why);
            if (address.Offset != 0)
                throw new VmException($"{why}: {address} is not the start of an allocation");
            allocation.Freed = true;
            if (allocation.Kind is AllocationKind.Gc or AllocationKind.Heap)
                TotalFrees++;
            if (allocation.Kind == AllocationKind.Native)
            {
                natives.Remove(allocation.NativeBase);
                if (allocation.OwnsNative)
                    unsafe { System.Runtime.InteropServices.NativeMemory.Free((void*)allocation.NativeBase); }
            }
        }

        // ── Native memory (interop) ────────────────────────────────

        private readonly Dictionary<nint, Allocation> natives = [];
        private readonly List<(nint Start, int Length, int Allocation)> pinned = [];

        /// <summary><c>mem.alloc</c>: zeroed native memory C can use, freed with <see cref="Free"/>.</summary>
        public unsafe Address AllocateNative(long size)
        {
            int bytes = (int)Math.Clamp(size, 1, int.MaxValue);
            var block = (nint)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)bytes);
            var allocation = new Allocation(nextId++, AllocationKind.Native, IrType.U8, 0) { NativeBase = block, NativeSize = bytes, OwnsNative = true };
            allocations[allocation.Id] = allocation;
            natives[block] = allocation;
            return new Address(allocation.Id, 0);
        }

        /// <summary>
        /// A native address as the interpreter sees it: inside memory it allocated or lent to C (pinned), or memory C gave
        /// (seen from there on, its size unknown).
        /// </summary>
        public Address FromNative(nint address)
        {
            if (address == 0)
                return default;
            foreach (var (start, length, id) in pinned)
                if (address >= start && address < start + length)
                    return new Address(id, (int)(address - start));
            foreach (var allocation in natives.Values)
                if (address >= allocation.NativeBase && address < allocation.NativeBase + allocation.NativeSize)
                    return new Address(allocation.Id, (int)(address - allocation.NativeBase));
            var foreign = new Allocation(nextId++, AllocationKind.Native, IrType.U8, 0) { NativeBase = address, NativeSize = int.MaxValue };
            allocations[foreign.Id] = foreign;
            natives[address] = foreign;
            return new Address(foreign.Id, 0);
        }

        /// <summary>
        /// The native address of an interpreter address, for C: native memory as it is; the interpreter's own memory
        /// pinned until <paramref name="pins"/> are released (after the call).
        /// </summary>
        public nint ToNative(Address address, List<System.Runtime.InteropServices.GCHandle> pins)
        {
            if (address.IsNull)
                return 0;
            var allocation = Get(address, "native call");
            if (allocation.Kind == AllocationKind.Native)
                return allocation.NativeBase + address.Offset;
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(allocation.Data, System.Runtime.InteropServices.GCHandleType.Pinned);
            pins.Add(handle);
            nint start = handle.AddrOfPinnedObject();
            pinned.Add((start, allocation.Data.Length, allocation.Id));
            return start + address.Offset;
        }

        /// <summary>Releases the pins of a call.</summary>
        public void Unpin(List<System.Runtime.InteropServices.GCHandle> pins)
        {
            foreach (var handle in pins)
            {
                nint start = handle.AddrOfPinnedObject();
                pinned.RemoveAll(p => p.Start == start);
                handle.Free();
            }
            pins.Clear();
        }

        // ── Native functions ───────────────────────────────────────

        private static readonly List<nint> nativeFunctions = [];

        /// <summary>
        /// A C function's address as a function value: a negative index into a table (the interpreter's own functions
        /// are 0 and up).
        /// </summary>
        public static Value NativeFunction(nint address)
        {
            lock (nativeFunctions)
            {
                int index = nativeFunctions.IndexOf(address);
                if (index < 0)
                {
                    index = nativeFunctions.Count;
                    nativeFunctions.Add(address);
                }
                return Value.FromFunction(-1 - index);
            }
        }

        /// <summary>The C address of a function value from <see cref="NativeFunction"/>.</summary>
        public static nint NativeAddressOf(int function)
        {
            lock (nativeFunctions)
                return nativeFunctions[-1 - function];
        }

        public Allocation Get(Address address, string why = "access")
        {
            if (address.IsNull)
                throw new VmException($"{why}: null dereference");
            if (!allocations.TryGetValue(address.Allocation, out var allocation))
                throw new VmException($"{why}: invalid address {address}");
            if (allocation.Freed)
                throw new VmException($"{why}: use after free of {allocation}");
            return allocation;
        }

        // ── Arrays: [length:u64][padding][elements...] ─────────────

        /// <summary>Where elements start and how far apart they are.</summary>
        public (int Header, int Stride) ArrayLayout(IrType element)
        {
            var layout = LayoutOf(element);
            int stride = Math.Max(1, (layout.Size + layout.Align - 1) / layout.Align * layout.Align);
            return (Math.Max(8, layout.Align), stride);
        }

        public Allocation AllocateArray(AllocationKind kind, IrType element, BigInteger length)
        {
            if (length < 0 || length > int.MaxValue)
                throw new VmException($"invalid array length {length}");
            var (header, stride) = ArrayLayout(element);
            var allocation = Allocate(kind, new ArrayType(element), checked(header + (int)length * stride));
            BitConverter.TryWriteBytes(allocation.Data, (ulong)length);
            return allocation;
        }

        public ulong ArrayLength(Address array)
        {
            var allocation = Get(array, "array length");
            if (allocation.Type is ArrayType)
                return BitConverter.ToUInt64(allocation.Data, 0);
            if (TrailingAt(allocation, array) is not null)
                return BitConverter.ToUInt64(allocation.Data, array.Offset);
            throw new VmException($"{allocation} is not an array");
        }

        /// <summary>An object of <paramref name="owner"/> whose last field is an array of <paramref name="length"/> elements, in place.</summary>
        public Allocation AllocateTrailing(AllocationKind kind, StructType owner, BigInteger length)
        {
            if (length < 0 || length > int.MaxValue)
                throw new VmException($"invalid array length {length}");
            var (lengthOffset, header, stride, _) = Layouts.Trailing(owner, shapes) ?? throw new VmException($"`{IrPrinter.FormatType(owner, shapes)}` does not end with an array");
            var allocation = Allocate(kind, owner, checked(header + (int)length * stride));
            BitConverter.TryWriteBytes(allocation.Data.AsSpan(lengthOffset), (ulong)length);
            return allocation;
        }

        /// <summary>The trailing array's layout when <paramref name="array"/> is the last field of an object that has one.</summary>
        private (int LengthOffset, int Header, int Stride, IrType Element)? TrailingAt(Allocation allocation, Address array) =>
            allocation.Type is StructType owner && Layouts.Trailing(owner, shapes) is { } trailing && trailing.LengthOffset == array.Offset ? trailing : null;

        /// <summary>Address of element <paramref name="index"/>, bounds-checked.</summary>
        public Address Element(Address array, BigInteger index, out IrType element)
        {
            var allocation = Get(array, "array access");
            if (allocation.Type is not ArrayType && TrailingAt(allocation, array) is var (lengthOffset, start, step, inPlace))
            {
                element = inPlace;
                ulong count = BitConverter.ToUInt64(allocation.Data, lengthOffset);
                if (index < 0 || index >= count)
                    throw new VmException($"index {index} out of bounds for array of length {count}");
                return new Address(array.Allocation, start + (int)index * step);
            }
            element = allocation.Type is ArrayType a ? a.Element : throw new VmException($"{allocation} is not an array");
            ulong length = BitConverter.ToUInt64(allocation.Data, 0);
            if (index < 0 || index >= length)
                throw new VmException($"index {index} out of bounds for array of length {length}");
            var (header, stride) = ArrayLayout(element);
            return new Address(array.Allocation, header + (int)index * stride);
        }

        /// <summary>Address of element <paramref name="index"/> of an inline array <c>[T; N]</c> at <paramref name="array"/>.</summary>
        public Address InlineElement(Address array, FixedArrayType type, BigInteger index, out IrType element)
        {
            element = type.Element;
            int length = type.Length(shapes) ?? throw new VmException($"`{IrPrinter.FormatType(type, shapes)}` has no length");
            if (index < 0 || index >= length)
                throw new VmException($"index {index} out of bounds for inline array of length {length}");
            var layout = LayoutOf(element);
            int stride = (layout.Size + layout.Align - 1) / layout.Align * layout.Align;
            return array.Plus((long)index * stride);
        }

        public TypeLayout LayoutOf(IrType type)
        {
            if (!layouts.TryGetValue(type, out var layout))
                layouts[type] = layout = Layouts.Of(type, shapes) ?? throw new VmException($"`{IrPrinter.FormatType(type, shapes)}` has no layout");
            return layout;
        }

        public int[] FieldOffsets(StructType type) =>
            Layouts.FieldOffsets(type, shapes) ?? throw new VmException($"`{IrPrinter.FormatType(type, shapes)}` has no layout");

        public IrType FieldType(StructType type, int field) => shapes.Structs[type.Id].Fields[field].Substitute(type.Arguments);

        // ── Typed access ───────────────────────────────────────────

        public Value Read(IrType type, Address address)
        {
            var allocation = Get(address, "read");
            int size = LayoutOf(type).Size;
            if (address.Offset + size > allocation.Length)
                throw new VmException($"read of {size}B at {address} overruns {allocation}");
            // A pointer in C's memory is a real address (C wrote it): seen as the interpreter sees native addresses, also
            // inside a struct or an inline array read from there.
            if (allocation.Kind == AllocationKind.Native && type is PointerType)
                return Value.FromPointer(FromNative((nint)BitConverter.ToInt64(allocation.Bytes(address.Offset, 8))), type);
            if (allocation.Kind == AllocationKind.Native && PointerOffsets(type) is { Length: > 0 } offsets)
            {
                var copy = allocation.Bytes(address.Offset, size).ToArray();
                foreach (int at in offsets)
                {
                    long raw = BitConverter.ToInt64(copy, at);
                    if (raw != 0)
                        BitConverter.TryWriteBytes(copy.AsSpan(at, 8), FromNative((nint)raw).Packed);
                }
                return Decode(type, copy);
            }
            return Decode(type, allocation.Bytes(address.Offset, size));
        }

        private readonly Dictionary<IrType, int[]> pointerOffsets = [];

        /// <summary>Where a value of <paramref name="type"/> holds raw pointers (`T*`): in itself, its struct fields, its inline arrays.</summary>
        private int[] PointerOffsets(IrType type)
        {
            if (pointerOffsets.TryGetValue(type, out var known))
                return known;
            var found = new List<int>();
            switch (type)
            {
                case PointerType:
                    found.Add(0);
                    break;
                case StructType structType when Layouts.FieldOffsets(structType, shapes) is { } fieldOffsets:
                    for (int f = 0; f < fieldOffsets.Length; f++)
                        found.AddRange(PointerOffsets(FieldType(structType, f)).Select(o => o + fieldOffsets[f]));
                    break;
                case FixedArrayType array when PointerOffsets(array.Element) is { Length: > 0 } inner:
                {
                    int stride = LayoutOf(array.Element).Size;
                    for (int i = 0; i < array.Count; i++)
                        found.AddRange(inner.Select(o => o + i * stride));
                    break;
                }
            }
            return pointerOffsets[type] = [.. found];
        }

        public void Write(IrType type, Address address, Value value)
        {
            var allocation = Get(address, "write");
            if (allocation.Kind == AllocationKind.Static && allocation.Sealed)
                throw new VmException($"write: {address} is read-only static data");
            int size = LayoutOf(type).Size;
            if (address.Offset + size > allocation.Length)
                throw new VmException($"write of {size}B at {address} overruns {allocation}");
            Encode(type, Coerce(value, type), allocation.Bytes(address.Offset, size));
            // Into C's memory, a pointer to C's memory goes as its real address (C reads it), also inside a struct.
            if (allocation.Kind == AllocationKind.Native && PointerOffsets(type) is { Length: > 0 } offsets)
            {
                var bytes = allocation.Bytes(address.Offset, size);
                foreach (int at in offsets)
                {
                    var inner = Address.Unpack(BitConverter.ToUInt64(bytes.Slice(at, 8)));
                    if (!inner.IsNull && allocations.TryGetValue(inner.Allocation, out var target) && target.Kind == AllocationKind.Native)
                        BitConverter.TryWriteBytes(bytes.Slice(at, 8), (long)(target.NativeBase + inner.Offset));
                }
            }
        }

        /// <summary>Converts a value to the representation of <paramref name="type"/> (what storing it into such a slot does).</summary>
        public Value Coerce(Value value, IrType type)
        {
            if (value.Kind == ValueKind.Deferred)
                return Read(type, value.Address);

            switch (type)
            {
                case PrimitiveType { Kind: Primitive.Unit }:
                    return Value.UnitValue;
                case PrimitiveType { Kind: Primitive.Bool }:
                    return Value.FromBool(value.IsTrue);
                case PrimitiveType { IsInteger: true }:
                    return Value.FromInt(AsInteger(value), type);
                case PrimitiveType { IsFloat: true }:
                    return CoerceFloat(value, type);
                case ReferenceType or GcType or BoxType or PointerType or OptionalType:
                    return Value.FromPointer(AsAddress(value), type);
                case FunctionType when value.Kind == ValueKind.Function:
                    return value;
                case FunctionType:
                {
                    // An address as a function: one of the interpreter's (tagged) or C code.
                    ulong raw = (ulong)(AsInteger(value) & ulong.MaxValue);
                    return (raw & Address.FunctionTag) != 0 ? Value.FromFunction((int)(raw & ~Address.FunctionTag))
                        : raw == 0 ? Value.FromFunction(int.MinValue) : NativeFunction((nint)raw);
                }
                default:
                    if (value.Kind != ValueKind.Struct)
                        throw new VmException($"cannot use {value} as `{IrPrinter.FormatType(type, shapes)}`");
                    // An object seen as a base class (or the root `Object`): its fields start the same way.
                    if (value.Bytes!.Length > LayoutOf(type).Size && type is StructType)
                        return Value.FromStruct(value.Bytes[..LayoutOf(type).Size], type);
                    if (value.Bytes!.Length != LayoutOf(type).Size)
                        throw new VmException($"{value} does not have the size of `{IrPrinter.FormatType(type, shapes)}`");
                    return Value.FromStruct(value.Bytes, type);
            }
        }

        /// <summary>A number as a float: wide floats (f128, f256) convert exactly, through their bits.</summary>
        private static Value CoerceFloat(Value value, IrType type)
        {
            bool wideSource = value.Kind == ValueKind.Float && Numbers.IsWide(value.Type);
            if (!Numbers.IsWide(type) && !wideSource)
                return Value.FromFloat(value.Kind == ValueKind.Float ? value.Float : (double)AsInteger(value), type);
            return value.Kind == ValueKind.Float
                ? Value.FromFloatBits(value.FloatBitsAs(type), type)
                : Value.FromFloatBits(SoftFloat.FromInteger(AsInteger(value), Numbers.FormatOf(type)), type);
        }

        public static BigInteger AsInteger(Value value) => value.Kind switch
        {
            ValueKind.Int or ValueKind.Bool => value.Int,
            ValueKind.Float when Numbers.IsWide(value.Type) => SoftFloat.ToInteger(value.Int, Numbers.FormatOf(value.Type!)),
            ValueKind.Float => double.IsFinite(value.Float) ? new BigInteger(Math.Truncate(value.Float)) : BigInteger.Zero,
            ValueKind.Pointer => value.Address.Packed,
            ValueKind.Function => value.Function == int.MinValue ? 0 : value.Function < 0 ? (ulong)NativeAddressOf(value.Function) : Address.FunctionTag | (uint)value.Function,
            _ => throw new VmException($"{value} is not an integer"),
        };

        public static Address AsAddress(Value value) => value.Kind switch
        {
            ValueKind.Pointer or ValueKind.Deferred => value.Address,
            ValueKind.Int => Address.Unpack((ulong)(value.Int & ulong.MaxValue)),
            _ => throw new VmException($"{value} is not an address"),
        };

        /// <summary>Decodes a value of <paramref name="type"/> from raw bytes (e.g. a field inside a struct value).</summary>
        public Value DecodeBytes(IrType type, ReadOnlySpan<byte> bytes) => Decode(type, bytes);

        private Value Decode(IrType type, ReadOnlySpan<byte> bytes) => type switch
        {
            PrimitiveType { Kind: Primitive.Unit } => Value.UnitValue,
            PrimitiveType { Kind: Primitive.Bool } => Value.FromBool(bytes[0] != 0),
            PrimitiveType { IsInteger: true } p => Value.FromInt(new BigInteger(bytes, isUnsigned: !p.IsSigned), type),
            PrimitiveType { IsFloat: true } => Value.FromFloatBits(new BigInteger(bytes, isUnsigned: true), type),
            ReferenceType or GcType or BoxType or PointerType or OptionalType => Value.FromPointer(Address.Unpack(BitConverter.ToUInt64(bytes)), type),
            FunctionType => Value.FromFunction((int)BitConverter.ToUInt64(bytes)),
            _ => Value.FromStruct(bytes.ToArray(), type),
        };

        private static void Encode(IrType type, Value value, Span<byte> target)
        {
            switch (value.Kind)
            {
                case ValueKind.Unit:
                    break;
                case ValueKind.Bool:
                    target[0] = (byte)(value.IsTrue ? 1 : 0);
                    break;
                case ValueKind.Int:
                    WriteBits(target, value.Int);
                    break;
                case ValueKind.Float:
                    WriteBits(target, value.FloatBits);
                    break;
                case ValueKind.Pointer:
                    BitConverter.TryWriteBytes(target, value.Address.Packed);
                    break;
                case ValueKind.Function:
                    BitConverter.TryWriteBytes(target, (ulong)value.Function);
                    break;
                case ValueKind.Struct:
                    value.Bytes.CopyTo(target);
                    break;
                default:
                    throw new VmException($"cannot store {value} in memory");
            }
        }

        private static void WriteBits(Span<byte> target, BigInteger value)
        {
            target.Fill(value.Sign < 0 ? (byte)0xFF : (byte)0);
            var source = value.ToByteArray(); // two's complement, little-endian
            source.AsSpan(0, Math.Min(source.Length, target.Length)).CopyTo(target);
        }
    }
}

using System.Numerics;

namespace Larva.IR
{
    // ── Operands ───────────────────────────────────────────────────

    /// <summary>A value read by a statement or terminator.</summary>
    public abstract record Operand
    {
        public static Operand Copy(Place place) => new CopyOperand(place);
        public static Operand Move(Place place) => new MoveOperand(place);
        /// <summary>Integer/bool value, or raw IEEE bit pattern for float types (see <see cref="ConstOperand"/>).</summary>
        public static Operand Const(BigInteger value, IrType type) => new ConstOperand(value, type);
        public static Operand Int(int value) => new ConstOperand(value, IrType.I32);
        public static Operand Int(BigInteger value, IrType type) => new ConstOperand(value, type);
        public static Operand Bool(bool value) => new ConstOperand(value ? 1 : 0, IrType.Bool);
        public static Operand Half(Half value) => new ConstOperand(BitConverter.HalfToUInt16Bits(value), IrType.F16);
        public static Operand Float32(float value) => new ConstOperand(BitConverter.SingleToUInt32Bits(value), IrType.F32);
        public static Operand Float(double value) => new ConstOperand(BitConverter.DoubleToUInt64Bits(value), IrType.F64);

        /// <summary>Float from its raw bit pattern; the only way to build <c>f128</c>/<c>f256</c> constants (no .NET type).</summary>
        public static Operand FloatBits(BigInteger bits, IrType type) => new ConstOperand(bits, type);

        public sealed override string ToString() => Text.IrPrinter.Format(this);
    }

    /// <summary>Reads the place; the place stays usable afterwards.</summary>
    public sealed record CopyOperand(Place Place) : Operand;

    /// <summary>Reads the place and invalidates it (ownership transfer; checked by the borrow checker).</summary>
    public sealed record MoveOperand(Place Place) : Operand;

    /// <summary>
    /// Constant of a primitive type. <see cref="Bits"/> is the value for integers (signed for <c>iN</c>) and bool,
    /// and the raw non-negative IEEE bit pattern for floats. The constructor rejects values that do not fit the type.
    /// </summary>
    public sealed record ConstOperand : Operand
    {
        public BigInteger Bits { get; }
        public IrType Type { get; }

        public ConstOperand(BigInteger bits, IrType type)
        {
            int width = type.BitWidth ?? throw new ArgumentException($"Constants must have a primitive type, got {type}.", nameof(type));
            var (min, max) = type.IsSigned
                ? (-(BigInteger.One << (width - 1)), (BigInteger.One << (width - 1)) - 1)
                : (BigInteger.Zero, (BigInteger.One << width) - 1);
            if (bits < min || bits > max)
                throw new ArgumentOutOfRangeException(nameof(bits), $"{bits} does not fit in {type}.");
            Bits = bits;
            Type = type;
        }

        /// <summary>True when the value fits a signed 64-bit immediate (IL <c>ldc</c>).</summary>
        public bool FitsInt64 => Bits >= long.MinValue && Bits <= long.MaxValue;
    }

    // ── Rvalues ────────────────────────────────────────────────────

    public enum BinaryOp : byte
    {
        Add, Sub, Mul, Div, Rem,
        And, Or, Xor, Shl, Shr,
        Eq, Ne, Lt, Le, Gt, Ge,
    }

    public enum UnaryOp : byte
    {
        Neg, Not,
    }

    /// <summary>Right-hand side of an assignment.</summary>
    public abstract record Rvalue
    {
        public static implicit operator Rvalue(Operand operand) => new UseRvalue(operand);

        public static Rvalue Use(Operand operand) => new UseRvalue(operand);
        public static Rvalue Binary(BinaryOp op, Operand left, Operand right) => new BinaryRvalue(op, left, right);
        public static Rvalue Unary(UnaryOp op, Operand operand) => new UnaryRvalue(op, operand);
        public static Rvalue Ref(Place place) => new RefRvalue(place, Mutable: false);
        public static Rvalue RefMut(Place place) => new RefRvalue(place, Mutable: true);
        public static Rvalue Cast(Operand operand, IrType type) => new CastRvalue(operand, type);
        public static Rvalue SizeOf(IrType type) => new SizeOfRvalue(type);
        public static Rvalue Box(Operand value) => new AllocRvalue(AllocKind.Box, value);
        public static Rvalue Gc(Operand value, Operand? finalizer = null) => new AllocRvalue(AllocKind.Gc, value, finalizer);
        public static Rvalue Alloc(Operand value) => new AllocRvalue(AllocKind.Raw, value);
        public static Rvalue NewArray(AllocKind kind, IrType element, Operand length) => new ArrayRvalue(kind, element, length);

        /// <summary>A new array whose elements are all assigned next (see <see cref="ArrayRvalue.Filled"/>).</summary>
        public static Rvalue NewFilledArray(AllocKind kind, IrType element, Operand length) => new ArrayRvalue(kind, element, length) { Filled = true };
        public static Rvalue Len(Place array) => new LenRvalue(array);
        public static Rvalue Static(int data) => new StaticRvalue(data);
        public static Rvalue Zero(IrType type) => new ZeroRvalue(type);
        /// <summary><c>filler T</c>: zero bytes as the unread payload of an empty optional (see <see cref="ZeroRvalue"/>).</summary>
        public static Rvalue ZeroFiller(IrType type) => new ZeroRvalue(type, Filler: true);
        public static Rvalue ReadGlobal(int global) => new GlobalRvalue(global);
        public static Rvalue ReadGlobal(int global, IrType[] typeArguments) => new GlobalRvalue(global) { TypeArguments = typeArguments };
        public static Rvalue TypeId(IrType type) => new TypeIdRvalue(type);
        public static Rvalue Implementation(Requirement requirement) => new ImplementationRvalue(requirement);
        public static Rvalue ClassId(IrType @class, bool end = false) => new ClassIdRvalue(@class, end);
        public static Rvalue FunctionRef(int function, params IrType[] typeArguments) => new FunctionRefRvalue(function, typeArguments);

        public static Rvalue Instances(int function) => new InstancesRvalue(function);
        public static Rvalue HandleCast(Operand handle, IrType target) => new HandleCastRvalue(handle, target);
        public static Rvalue Wrap(Operand value) => new WrapRvalue(value);
        public static Rvalue IsNull(Place value) => new IsNullRvalue(value);
        public static Rvalue Unwrap(Operand value) => new UnwrapRvalue(value);

        /// <summary>The exception being handled; only as the first statement of a typed handler block, into a whole local.</summary>
        public static readonly Rvalue Caught = new CaughtRvalue();

        public sealed override string ToString() => Text.IrPrinter.Format(this);
    }

    public sealed record UseRvalue(Operand Operand) : Rvalue;

    public sealed record BinaryRvalue(BinaryOp Op, Operand Left, Operand Right) : Rvalue;

    public sealed record UnaryRvalue(UnaryOp Op, Operand Operand) : Rvalue;

    /// <summary><c>&amp;place</c> or <c>&amp;mut place</c>: creates a borrow.</summary>
    public sealed record RefRvalue(Place Place, bool Mutable) : Rvalue;

    public sealed record CastRvalue(Operand Operand, IrType Type) : Rvalue;

    public enum AllocKind : byte
    {
        /// <summary><c>box v</c> → <c>box T</c>, freed automatically when its owner goes away.</summary>
        Box,
        /// <summary><c>gc v</c> → <c>gc T</c>, reclaimed by the GC mode.</summary>
        Gc,
        /// <summary><c>alloc v</c> → <c>*mut T</c>, released with <see cref="FreeStatement"/>.</summary>
        Raw,
    }

    /// <summary>Moves (or copies) <see cref="Value"/> into a new heap allocation.</summary>
    /// <remarks>
    /// <see cref="Finalizer"/> (only for <see cref="AllocKind.Gc"/>): a <c>fn(gc T)</c> the runtime calls once when the
    /// object dies (its count drops to zero, a collection finds it unreachable, its arena region closes) before it is
    /// freed. Under tracing the object outlives that call until the next collection that finds it unreachable again.
    /// </remarks>
    public sealed record AllocRvalue(AllocKind Kind, Operand Value, Operand? Finalizer = null) : Rvalue
    {
        /// <summary>Type produced when allocating a value of <paramref name="valueType"/>.</summary>
        public IrType ResultType(IrType valueType) => Kind switch
        {
            AllocKind.Box => new BoxType(valueType),
            AllocKind.Gc => new GcType(valueType),
            _ => new PointerType(valueType, Mutable: true),
        };
    }

    /// <summary>
    /// <c>box [T; n]</c> / <c>gc [T; n]</c> / <c>alloc [T; n]</c>: a new array of <see cref="Length"/> zeroed elements, giving
    /// <c>box [T]</c>, <c>gc [T]</c> or <c>*mut [T]</c>. <see cref="Element"/> must be Copy, so zero is a valid value.
    /// </summary>
    public sealed record ArrayRvalue(AllocKind Kind, IrType Element, Operand Length) : Rvalue
    {
        /// <summary>
        /// Every element is assigned right after (an array literal): the elements may be of a type whose all-zero bytes
        /// are not a value (arrays of arrays, of objects), as nothing reads them before.
        /// </summary>
        public bool Filled { get; init; }

        /// <summary>
        /// An object whose last field is the array (<c>gc Owner [T; n]</c>): one allocation of the struct's fields, then the
        /// array's length and elements in place (see <see cref="Lowering.Layouts.Trailing"/>). It gives a handle to the
        /// struct; the elements are reached through its last field. Null for a plain array.
        /// </summary>
        public StructType? Owner { get; init; }

        public IrType ResultType => Owner is { } owner
            ? Kind switch
            {
                AllocKind.Box => new BoxType(owner),
                AllocKind.Gc => new GcType(owner),
                _ => new PointerType(owner, Mutable: true),
            }
            : Kind switch
            {
                AllocKind.Box => new BoxType(new ArrayType(Element)),
                AllocKind.Gc => new GcType(new ArrayType(Element)),
                _ => new PointerType(new ArrayType(Element), Mutable: true),
            };
    }

    /// <summary><c>Len(place)</c>: number of elements of the array at <see cref="Array"/>, as a <c>u64</c>.</summary>
    public sealed record LenRvalue(Place Array) : Rvalue;

    /// <summary><c>static#N</c>: a <c>&amp;[T]</c> to <see cref="IrModule.Data"/>[N], read-only and alive for the whole program.</summary>
    public sealed record StaticRvalue(int Data) : Rvalue;

    /// <summary><c>caught</c>: the exception that entered the current handler block.</summary>
    public sealed record CaughtRvalue : Rvalue;

    /// <summary>Size of <see cref="Type"/> in bytes, as a <c>u64</c>. Requires <c>fixedsize</c>; read from the witness in shared code.</summary>
    public sealed record SizeOfRvalue(IrType Type) : Rvalue;

    /// <summary>
    /// <c>zeroed T</c>: every byte zero, for types whose all-zero bytes are a valid value (numbers, bool, inline arrays and
    /// structs of those). Initializes the whole place at once, which element-by-element stores into an inline array cannot.
    /// </summary>
    /// <remarks>
    /// <see cref="Filler"/>: zero bytes for any type, even one whose all-zero bytes are not a valid value (a null handle,
    /// a null function). It fills a slot nothing reads as a <c>T</c>, such as the payload of an empty nullable struct;
    /// runtimes ignore null handles when they trace or release.
    /// </remarks>
    public sealed record ZeroRvalue(IrType Type, bool Filler = false) : Rvalue;

    /// <summary><c>global#N</c>: a copy of the value of <see cref="IrModule.Globals"/>[N].</summary>
    public sealed record GlobalRvalue(int Global) : Rvalue
    {
        /// <summary>The instantiation of a generic global (<see cref="GlobalDef.GenericCount"/>).</summary>
        public IrType[] TypeArguments { get; init; } = [];
    }

    /// <summary>
    /// <c>typeid T</c>: an <c>i32</c> that differs for every concrete type, fixed when generics are resolved (functions
    /// that use it on a type parameter are specialized, never shared).
    /// </summary>
    public sealed record TypeIdRvalue(IrType Type) : Rvalue;

    /// <summary>
    /// <c>impl name(params) -&gt; result</c>: the address (a <see cref="FunctionType"/> value) of the function that meets
    /// <see cref="Requirement"/> for the concrete types it names, found the way calls through constrained type parameters
    /// find theirs (<see cref="Requirements.Resolve"/>, among <see cref="IrModule.Implementations"/>) when generics are
    /// resolved: how generic code asks for something every type provides in its own way (its run-time type, boxing)
    /// without declaring a requirement on its type parameters. Functions that use it are specialized, never shared.
    /// </summary>
    public sealed record ImplementationRvalue(Requirement Requirement) : Rvalue
    {
        public FunctionType Type => new(Requirement.Parameters, Requirement.Result);
    }

    /// <summary>
    /// <c>classid C</c> (or <c>classid.end C</c>): an <c>i32</c> numbering the classes of the whole program in preorder
    /// over their bases, so a class and everything derived from it are the ids <c>[classid C, classid.end C)</c>, whatever
    /// module each comes from. Classes are the <see cref="StructDef.Mutable"/> structs; fixed when generics are resolved.
    /// </summary>
    public sealed record ClassIdRvalue(IrType Class, bool End = false) : Rvalue;

    /// <summary>
    /// <c>instances f</c>: a new array (<c>box [fn(…) -&gt; R]</c>) with the address of every instantiation of the generic
    /// function <paramref name="Function"/> the program has once specialized, in no particular order: how code finds,
    /// at run time, what was compiled ahead of time (<c>MakeGeneric</c>). The function's signature mentions none of its
    /// type parameters, so every instance has the same type. Filled in when generics are resolved.
    /// </summary>
    public sealed record InstancesRvalue(int Function) : Rvalue;

    /// <summary><c>fn f::&lt;args&gt;</c>: the address of a function (instantiated), a <see cref="FunctionType"/> value.</summary>
    public sealed record FunctionRefRvalue(int Function, IrType[] TypeArguments) : Rvalue
    {
        public bool Equals(FunctionRefRvalue? other) => other is not null && Function == other.Function && TypeArguments.AsSpan().SequenceEqual(other.TypeArguments);
        public override int GetHashCode() => HashCode.Combine(Function, TypeArguments.Length);
    }

    /// <summary>
    /// A handle (<c>gc T</c> or <c>*mut T</c>) seen as a handle to another struct of the same object: to a base
    /// (<see cref="StructDef.Base"/>, always allowed) or back down to a derived struct (only in <c>unsafe</c> code, which
    /// must know the object's real type).
    /// </summary>
    public sealed record HandleCastRvalue(Operand Handle, IrType Target) : Rvalue;

    /// <summary><c>T</c> → <c>?T</c>: the same pointer, now allowed to be null.</summary>
    public sealed record WrapRvalue(Operand Value) : Rvalue;

    /// <summary>Whether a <c>?T</c> is null (reads the place, moves nothing).</summary>
    public sealed record IsNullRvalue(Place Value) : Rvalue;

    /// <summary><c>?T</c> → <c>T</c>: the pointer, checked (a panic when null).</summary>
    public sealed record UnwrapRvalue(Operand Value) : Rvalue;
}

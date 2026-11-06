namespace Larva.IR.Encoding
{
    /// <summary>
    /// Nibble tags of the binary IR. Every tag is one nibble; integers are nibble-varints
    /// (see <see cref="Larva.IL.Encoding.NibbleWriter"/>).
    /// <code>
    /// module    = name structs:uv {name generics meta:4 [names] linkage:4 [symbol] fields:uv type*}* functions:uv function*
    /// function  = name flags:4 linkage:4 [symbol] generics regions:uv name* args:uv locals:uv {flags:4 type [name] [region:uv]}* blocks:uv {stmts:uv stmt* term}*
    /// generics  = count:uv {name constraint}*
    /// name      = bytes:uv byte*
    /// type      = 0..11 common primitive (unit..f64) | 12 id:uv args:uv type* (struct) | 13 type (&amp;) | 14 type (&amp;mut)
    ///           | 15 0 type ([]) | 15 1..7 wide primitive (f16 f128 f256 i128 i256 u128 u256)
    ///           | 15 8 type (gc) | 15 9 type (box) | 15 10 type (*) | 15 11 type (*mut) | 15 12 index:uv (generic param)
    /// constraint= 0 (any) | 1 group:4 | 2 type (exact) | 3 n:uv constraint* (and) | 4 n:uv constraint* (or)
    ///           | 5 constraint (not) | 6 bits:uv (size &lt;=)
    /// place     = local:uv projections:uv {0 | 1 field:uv | 2 local:uv}*
    /// operand   = 0 place (copy) | 1 place (move) | 2 type bits:sv
    /// stmt      = tag place? payload          (tag: one nibble 0-14, or 15 + nibble for the rest)
    /// term      = TerminatorTag payload        (call / throw / resume end with handlers = n:uv {hasType:4 [type] block:uv}*)
    /// </code>
    /// Assignments fold the rvalue kind into the statement tag, so the common <c>_1 = copy _2</c> costs 3 nibbles.
    /// </summary>
    internal static class IrFormat
    {
        public const int TypeStruct = 12;
        public const int TypeRef = 13;
        public const int TypeRefMut = 14;
        public const int TypeExtended = 15;

        // Second nibble after TypeExtended.
        public const int ExtendedArray = 0;

        /// <summary>Wide primitives follow the array tag: <c>Primitive.F16</c> → 1 … <c>Primitive.U256</c> → 7.</summary>
        public const int ExtendedPrimitiveBase = (int)Primitive.F16 - 1;
        public const int ExtendedGc = 8;
        public const int ExtendedBox = 9;
        public const int ExtendedPointer = 10;
        public const int ExtendedPointerMut = 11;
        public const int ExtendedGenericParam = 12;
        /// <summary><c>[T; N]</c>: element type, count, then 0 or 1 + the type whose size multiplies the count.</summary>
        public const int ExtendedFixedArray = 13;
        public const int ExtendedFunction = 14;
        public const int ExtendedOptional = 15;

        public const int ConstraintAny = 0;
        public const int ConstraintGroup = 1;
        public const int ConstraintExact = 2;
        public const int ConstraintAnd = 3;
        public const int ConstraintOr = 4;
        public const int ConstraintNot = 5;
        public const int ConstraintSize = 6;

        public const int FunctionUnsafe = 0b0001;
        public const int FunctionMangled = 0b1000;

        /// <summary>Statement tags from 15 on are written as 15 followed by a second nibble.</summary>
        public const int StatementTagEscape = 15;

        /// <summary>Reflection level in two bits: 0 = module default, 1 + <see cref="ReflectionLevel"/> otherwise.</summary>
        public const int ReflectShift = 1;
        public const int ReflectMask = 0b0110;

        /// <summary>Struct metadata nibble: reflection bits as for functions, plus whether field names follow.</summary>
        public const int StructFieldNames = 0b1000;

        /// <summary>Struct metadata nibble: an object type whose fields may be written through <c>gc</c> handles.</summary>
        public const int StructMutable = 0b0001;

        public static int EncodeReflect(ReflectionLevel? level) => (level is { } l ? (int)l + 1 : 0) << ReflectShift;

        public static ReflectionLevel? DecodeReflect(int nibble) =>
            ((nibble & ReflectMask) >> ReflectShift) is int code and > 0 ? (ReflectionLevel)(code - 1) : null;

        public const int ProjectionDeref = 0;
        public const int ProjectionField = 1;
        public const int ProjectionIndex = 2;

        public const int OperandCopy = 0;
        public const int OperandMove = 1;
        public const int OperandConst = 2;

        public const int LocalMutable = 0b0001;
        public const int LocalNamed = 0b0010;
        public const int LocalRegion = 0b0100;
    }

    internal enum StatementTag : byte
    {
        AssignUse,
        AssignBinary,
        AssignUnary,
        AssignRef,
        AssignRefMut,
        AssignCast,
        StorageLive,
        StorageDead,
        Nop,
        AssignSizeOf,
        AssignAlloc,
        Free,
        RegionEnter,
        RegionExit,
        AssignArray,
        AssignLen,
        AssignCaught,
        AssignStatic,
        AssignZero,
        AssignGlobal,
        StoreGlobal,
        AssignFunctionRef,
        AssignHandleCast,
        AssignWrap,
        AssignIsNull,
        AssignUnwrap,
        AssignFiller,
        AssignGenericGlobal,
        StoreGenericGlobal,
        AssignTypeId,
        AssignIntrinsic,
        AssignClassId,
        AssignClassIdEnd,
        AssignInstances,
        AssignImplementation,
    }

    internal enum TerminatorTag : byte
    {
        Goto,
        If,
        Switch,
        Return,
        Call,
        Drop,
        Unreachable,
        Throw,
        Resume,
    }
}

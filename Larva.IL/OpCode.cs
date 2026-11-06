namespace Larva.IL
{
    /// <summary>
    /// Larva IL opcodes. The numeric value is the exact bit pattern written to the stream:
    /// <list type="bullet">
    /// <item><b>Short</b> (4 bits): <c>0x0</c>–<c>0xE</c>, the 15 most frequent operations.</item>
    /// <item><b>Extended</b> (8 bits): when the first nibble is <c>1111</c>, the next nibble selects
    /// one of 16 opcodes, i.e. <c>0xF0</c>–<c>0xFF</c>.</item>
    /// </list>
    /// Less frequent operations are grouped into families (<see cref="Alu"/>, <see cref="Float"/>, …)
    /// whose first operand is a one-nibble sub-operation. Operands and metadata live in <see cref="OpCodes"/>.
    /// </summary>
    public enum OpCode : byte
    {
        // ── Short opcodes: 4 bits ──────────────────────────────────
        LoadLocal = 0x0,
        StoreLocal = 0x1,
        LoadArgument = 0x2,
        Const = 0x3,
        Add = 0x4,
        Sub = 0x5,
        Mul = 0x6,
        Call = 0x7,
        Return = 0x8,
        Jump = 0x9,
        BranchTrue = 0xA,
        BranchFalse = 0xB,
        LoadField = 0xC,
        StoreField = 0xD,
        Duplicate = 0xE,
        // 0xF: extended prefix, see OpCodeEncoding.ExtendedPrefix.

        // ── Extended opcodes: 1111 + 4 bits ────────────────────────
        Alu = 0xF0,         // AluOp
        Float = 0xF1,       // FloatOp
        Compare = 0xF2,     // Condition
        Branch = 0xF3,      // Condition, label
        Convert = 0xF4,     // ConvertOp
        Object = 0xF5,      // ObjectOp
        Array = 0xF6,       // ArrayOp
        Literal = 0xF7,     // LiteralOp
        Stack = 0xF8,       // StackOp
        CallExtended = 0xF9,// CallOp
        Switch = 0xFA,      // label table
        Exception = 0xFB,   // ExceptionOp
        Atomic = 0xFC,      // AtomicOp
        System = 0xFD,      // SystemOp
        Generic = 0xFE,     // GenericOp: shared generic code (witness tables)
        Gc = 0xFF,          // GcOp: allocation and GC cooperation
    }
}

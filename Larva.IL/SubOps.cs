namespace Larva.IL
{
    // Sub-operations of the extended opcode families. Each is written as a single nibble
    // right after its opcode, so every family holds at most 16 entries.

    public enum AluOp : byte
    {
        Div, Rem, Neg, Inc, Dec,
        And, Or, Xor, Not,
        Shl, Shr, UShr, Rol, Ror,
    }

    public enum FloatOp : byte
    {
        Add, Sub, Mul, Div, Rem, Neg,
        Sqrt, Abs, Min, Max,
        Floor, Ceil, Round, Trunc,
    }

    /// <summary>Used by <see cref="OpCode.Compare"/> and <see cref="OpCode.Branch"/> (which rejects <see cref="Ord"/>).</summary>
    public enum Condition : byte
    {
        Eq, Ne, Lt, Le, Gt, Ge,
        /// <summary>Three-way compare: pushes -1, 0 or 1.</summary>
        Ord,
    }

    public enum ConvertOp : byte
    {
        Conv, Cast, BitCast, Box, Unbox,
        SignExtend, ZeroExtend, Truncate,
    }

    public enum ObjectOp : byte
    {
        New, NewValue, Init, Copy,
        IsInstance, CastClass,
        LoadType, SizeOf, TypeOf,
        LoadVirtualTable, LoadFieldInfo,
        LoadFieldAddress,
    }

    public enum ArrayOp : byte
    {
        New, Length, Load, Store, LoadElement, StoreElement, Copy,
        LoadElementAddress,
        /// <summary>[address, index] → [element address] in an inline array <c>[T; N]</c> (type token), bounds-checked against N.</summary>
        FixedElementAddress,
    }

    public enum LiteralOp : byte
    {
        Null, True, False,
        /// <summary>Loads a constant-pool entry (128/256-bit integers, f128/f256).</summary>
        Wide,
        /// <summary>Pushes a <c>&amp;[T]</c> to the module's static array N (read-only).</summary>
        Data,
        /// <summary>Pushes the all-zero value of a type (type token).</summary>
        Zero,
        /// <summary>Pushes the value of global N.</summary>
        Global,
        /// <summary>Pops a value into global N.</summary>
        SetGlobal,
    }

    public enum StackOp : byte
    {
        Pop, Swap, LoadMem, StoreMem, StoreArgument, Move,
        LoadLocalAddress, LoadArgumentAddress,
        /// <summary>Traps when the pointer on top of the stack is null (it stays there).</summary>
        CheckNull,
        /// <summary>[pointer] → [bool]: whether it is null.</summary>
        IsNull,
    }

    public enum CallOp : byte
    {
        Virtual, Indirect, Native, Tail, LoadFunction,
    }

    public enum ExceptionOp : byte
    {
        Throw, Rethrow, Leave, Try, EndTry, Catch, Finally, EndFinally,
    }

    public enum AtomicOp : byte
    {
        Load, Store, Exchange, Add, CompareExchange, Fence, Lock, Unlock,
    }

    /// <summary>
    /// Operations for generic code compiled once and shared by every instantiation. A shared function receives one
    /// witness (size, alignment, flags, drop, GC pointer map) per type parameter in a separate witness area; type
    /// tokens inside it may be open (mention <c>T0</c>, <c>T1</c>…) and are resolved against those witnesses.
    /// </summary>
    public enum GenericOp : byte
    {
        /// <summary>Push the witness of a type (open types resolved in the current frame). Precedes calls to shared functions.</summary>
        Witness,
        /// <summary>Push the witness received for type parameter N (shortcut for <see cref="Witness"/> of exactly <c>TN</c>).</summary>
        TypeArgument,
        /// <summary>Push the size in bytes of a type, read from its witness.</summary>
        SizeOf,
        /// <summary>Drop the value on top of the stack using its type's witness.</summary>
        Drop,
    }

    /// <summary>
    /// Heap allocation and cooperation with the program's GC mode. Which of these appear depends on the mode's
    /// capabilities: a module compiled for one mode only contains the operations that mode needs.
    /// </summary>
    public enum GcOp : byte
    {
        /// <summary>[value] → [gc handle]: allocate a GC-managed object initialized with the value.</summary>
        New,
        /// <summary>[value] → [pointer]: allocate an unmanaged heap cell (for <c>box T</c> and <c>*mut T</c>).</summary>
        Alloc,
        /// <summary>[pointer] → []: release a heap cell from <see cref="Alloc"/>.</summary>
        Free,
        /// <summary>[address, value] → []: store with a write barrier (generational / incremental GCs).</summary>
        WriteBarrier,
        /// <summary>[gc handle] → []: keep the object in place while interior borrows exist (moving GCs).</summary>
        Pin,
        /// <summary>[gc handle] → []: end a <see cref="Pin"/>.</summary>
        Unpin,
        /// <summary>[gc handle] → [gc handle]: add a reference (reference counting).</summary>
        Retain,
        /// <summary>[gc handle] → []: drop a reference (reference counting).</summary>
        Release,
        /// <summary>Open arena region N: <see cref="New"/> allocates there until it is exited.</summary>
        RegionEnter,
        /// <summary>Close arena region N, freeing everything allocated in it.</summary>
        RegionExit,
        /// <summary>[length] → [gc handle]: allocate a GC-managed array of zeroed elements of the given type.</summary>
        NewArray,
        /// <summary>[value, fn] → [gc handle]: <see cref="New"/>, with a finalizer the runtime calls once when the object dies.</summary>
        NewFinalized,
        /// <summary>
        /// [length] → [gc handle]: allocate a GC-managed object of the given struct, zeroed, whose last field is an array of
        /// that many elements kept in place.
        /// </summary>
        NewTrailing,
        /// <summary>[length] → [pointer]: <see cref="NewTrailing"/> in a heap cell the program frees (box, raw).</summary>
        AllocTrailing,
    }

    public enum SystemOp : byte
    {
        Nop, Break, Trap, Debug, Safepoint, Poll, Intrinsic,
    }
}

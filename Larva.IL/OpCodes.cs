using System.Collections.Frozen;
using Larva.IL.Encoding;
using L = Larva.IL.OperandKind;

namespace Larva.IL
{
    /// <summary>A sub-operation of a family opcode: nibble code, name and operand layout.</summary>
    public sealed record SubOpInfo(byte Code, string Name, params OperandKind[] Operands);

    /// <summary>
    /// Static description of an opcode. Plain opcodes carry their operand layout in <see cref="Operands"/>;
    /// family opcodes carry it per sub-operation in <see cref="SubOps"/>.
    /// </summary>
    public sealed record OpCodeInfo(OpCode OpCode, string Mnemonic, OperandKind[] Operands, Type? SubOpType, SubOpInfo?[]? SubOps)
    {
        public bool IsShort => OpCodeEncoding.IsShort(OpCode);

        /// <summary>Bits used by the opcode itself (4 or 8), excluding sub-op and operands.</summary>
        public int OpCodeBits => IsShort ? 4 : 8;

        public bool IsFamily => SubOps is not null;

        public SubOpInfo GetSubOp(byte code) =>
            SubOps is not null && code < SubOps.Length && SubOps[code] is { } sub
                ? sub
                : throw new ArgumentOutOfRangeException(nameof(code), $"'{Mnemonic}' has no sub-operation {code}.");

        public bool TryGetSubOp(byte code, out SubOpInfo sub)
        {
            sub = SubOps is not null && code < SubOps.Length ? SubOps[code]! : null!;
            return sub is not null;
        }
    }

    /// <summary>Single source of truth for opcode metadata.</summary>
    public static class OpCodes
    {
        private static readonly OpCodeInfo[] All =
        [
            // Short: 4 bits
            Op(OpCode.LoadLocal, "ldloc", L.Local),
            Op(OpCode.StoreLocal, "stloc", L.Local),
            Op(OpCode.LoadArgument, "ldarg", L.Argument),
            Op(OpCode.Const, "ldc", L.Immediate),
            Op(OpCode.Add, "add"),
            Op(OpCode.Sub, "sub"),
            Op(OpCode.Mul, "mul"),
            Op(OpCode.Call, "call", L.Function),
            Op(OpCode.Return, "ret"),
            Op(OpCode.Jump, "jmp", L.Label),
            Op(OpCode.BranchTrue, "brtrue", L.Label),
            Op(OpCode.BranchFalse, "brfalse", L.Label),
            Op(OpCode.LoadField, "ldfld", L.Field),
            Op(OpCode.StoreField, "stfld", L.Field),
            Op(OpCode.Duplicate, "dup"),

            // Extended: 1111 + 4 bits
            Family<AluOp>(OpCode.Alu, "alu",
                Sub(AluOp.Div, "div"), Sub(AluOp.Rem, "rem"), Sub(AluOp.Neg, "neg"),
                Sub(AluOp.Inc, "inc"), Sub(AluOp.Dec, "dec"),
                Sub(AluOp.And, "and"), Sub(AluOp.Or, "or"), Sub(AluOp.Xor, "xor"), Sub(AluOp.Not, "not"),
                Sub(AluOp.Shl, "shl"), Sub(AluOp.Shr, "shr"), Sub(AluOp.UShr, "ushr"),
                Sub(AluOp.Rol, "rol"), Sub(AluOp.Ror, "ror")),

            Family<FloatOp>(OpCode.Float, "fpu",
                Sub(FloatOp.Add, "add"), Sub(FloatOp.Sub, "sub"), Sub(FloatOp.Mul, "mul"),
                Sub(FloatOp.Div, "div"), Sub(FloatOp.Rem, "rem"), Sub(FloatOp.Neg, "neg"),
                Sub(FloatOp.Sqrt, "sqrt"), Sub(FloatOp.Abs, "abs"), Sub(FloatOp.Min, "min"), Sub(FloatOp.Max, "max"),
                Sub(FloatOp.Floor, "floor"), Sub(FloatOp.Ceil, "ceil"), Sub(FloatOp.Round, "round"), Sub(FloatOp.Trunc, "trunc")),

            Family<Condition>(OpCode.Compare, "cmp",
                Sub(Condition.Eq, "eq"), Sub(Condition.Ne, "ne"), Sub(Condition.Lt, "lt"),
                Sub(Condition.Le, "le"), Sub(Condition.Gt, "gt"), Sub(Condition.Ge, "ge"),
                Sub(Condition.Ord, "ord")),

            Family<Condition>(OpCode.Branch, "br",
                Sub(Condition.Eq, "eq", L.Label), Sub(Condition.Ne, "ne", L.Label), Sub(Condition.Lt, "lt", L.Label),
                Sub(Condition.Le, "le", L.Label), Sub(Condition.Gt, "gt", L.Label), Sub(Condition.Ge, "ge", L.Label)),

            Family<ConvertOp>(OpCode.Convert, "conv",
                Sub(ConvertOp.Conv, "to", L.Type), Sub(ConvertOp.Cast, "cast", L.Type), Sub(ConvertOp.BitCast, "bitcast", L.Type),
                Sub(ConvertOp.Box, "box", L.Type), Sub(ConvertOp.Unbox, "unbox", L.Type),
                Sub(ConvertOp.SignExtend, "sext", L.Unsigned), Sub(ConvertOp.ZeroExtend, "zext", L.Unsigned),
                Sub(ConvertOp.Truncate, "trunc", L.Unsigned)),

            Family<ObjectOp>(OpCode.Object, "obj",
                Sub(ObjectOp.New, "new", L.Type), Sub(ObjectOp.NewValue, "newval", L.Type),
                Sub(ObjectOp.Init, "init", L.Type), Sub(ObjectOp.Copy, "copy", L.Type),
                Sub(ObjectOp.IsInstance, "isinst", L.Type), Sub(ObjectOp.CastClass, "castclass", L.Type),
                Sub(ObjectOp.LoadType, "ldtype", L.Type), Sub(ObjectOp.SizeOf, "sizeof", L.Type),
                Sub(ObjectOp.TypeOf, "typeof"), Sub(ObjectOp.LoadVirtualTable, "ldvtbl"),
                Sub(ObjectOp.LoadFieldInfo, "ldfldinfo", L.Field),
                Sub(ObjectOp.LoadFieldAddress, "ldflda", L.Field)),

            Family<ArrayOp>(OpCode.Array, "arr",
                Sub(ArrayOp.New, "new", L.Type), Sub(ArrayOp.Length, "len"),
                Sub(ArrayOp.Load, "ld"), Sub(ArrayOp.Store, "st"),
                Sub(ArrayOp.LoadElement, "ldelem", L.Type), Sub(ArrayOp.StoreElement, "stelem", L.Type),
                Sub(ArrayOp.Copy, "copy"), Sub(ArrayOp.LoadElementAddress, "ldelema"), Sub(ArrayOp.FixedElementAddress, "ldfixa", L.Type)),

            Family<LiteralOp>(OpCode.Literal, "ldk",
                Sub(LiteralOp.Null, "null"), Sub(LiteralOp.True, "true"), Sub(LiteralOp.False, "false"),
                Sub(LiteralOp.Wide, "wide", L.Constant), Sub(LiteralOp.Data, "data", L.Unsigned), Sub(LiteralOp.Zero, "zero", L.Type), Sub(LiteralOp.Global, "global", L.Unsigned), Sub(LiteralOp.SetGlobal, "setglobal", L.Unsigned)),

            Family<StackOp>(OpCode.Stack, "stk",
                Sub(StackOp.Pop, "pop"), Sub(StackOp.Swap, "swap"),
                Sub(StackOp.LoadMem, "ldmem"), Sub(StackOp.StoreMem, "stmem"),
                Sub(StackOp.StoreArgument, "starg", L.Argument), Sub(StackOp.Move, "mov", L.Local, L.Local),
                Sub(StackOp.LoadLocalAddress, "ldloca", L.Local), Sub(StackOp.LoadArgumentAddress, "ldarga", L.Argument),
                Sub(StackOp.CheckNull, "chknull"), Sub(StackOp.IsNull, "isnull")),

            Family<CallOp>(OpCode.CallExtended, "callx",
                Sub(CallOp.Virtual, "virt", L.Function), Sub(CallOp.Indirect, "ind"),
                Sub(CallOp.Native, "native", L.Function), Sub(CallOp.Tail, "tail", L.Function),
                Sub(CallOp.LoadFunction, "ldfn", L.Function)),

            Op(OpCode.Switch, "switch", L.LabelTable),

            Family<ExceptionOp>(OpCode.Exception, "eh",
                Sub(ExceptionOp.Throw, "throw"), Sub(ExceptionOp.Rethrow, "rethrow"),
                Sub(ExceptionOp.Leave, "leave", L.Label),
                Sub(ExceptionOp.Try, "try"), Sub(ExceptionOp.EndTry, "endtry"),
                Sub(ExceptionOp.Catch, "catch", L.Type),
                Sub(ExceptionOp.Finally, "finally"), Sub(ExceptionOp.EndFinally, "endfinally")),

            Family<AtomicOp>(OpCode.Atomic, "atom",
                Sub(AtomicOp.Load, "ld"), Sub(AtomicOp.Store, "st"), Sub(AtomicOp.Exchange, "xchg"),
                Sub(AtomicOp.Add, "add"), Sub(AtomicOp.CompareExchange, "cmpxchg"),
                Sub(AtomicOp.Fence, "fence"), Sub(AtomicOp.Lock, "lock"), Sub(AtomicOp.Unlock, "unlock")),

            Family<GenericOp>(OpCode.Generic, "gen",
                Sub(GenericOp.Witness, "wit", L.Type), Sub(GenericOp.TypeArgument, "ldtarg", L.Unsigned),
                Sub(GenericOp.SizeOf, "sizeof", L.Type), Sub(GenericOp.Drop, "drop", L.Type)),

            Family<GcOp>(OpCode.Gc, "gc",
                Sub(GcOp.New, "new", L.Type), Sub(GcOp.Alloc, "alloc", L.Type), Sub(GcOp.Free, "free", L.Type),
                Sub(GcOp.WriteBarrier, "wb", L.Type), Sub(GcOp.Pin, "pin"), Sub(GcOp.Unpin, "unpin"),
                Sub(GcOp.Retain, "retain"), Sub(GcOp.Release, "release"),
                Sub(GcOp.RegionEnter, "renter", L.Unsigned), Sub(GcOp.RegionExit, "rexit", L.Unsigned),
                Sub(GcOp.NewArray, "newarr", L.Type), Sub(GcOp.NewFinalized, "newfin", L.Type), Sub(GcOp.NewTrailing, "newtrail", L.Type), Sub(GcOp.AllocTrailing, "alloctrail", L.Type)),

            Family<SystemOp>(OpCode.System, "sys",
                Sub(SystemOp.Nop, "nop"), Sub(SystemOp.Break, "break"),
                Sub(SystemOp.Trap, "trap", L.Unsigned), Sub(SystemOp.Debug, "debug", L.Unsigned),
                Sub(SystemOp.Safepoint, "safepoint"), Sub(SystemOp.Poll, "poll"),
                Sub(SystemOp.Intrinsic, "intrinsic", L.Unsigned)),
        ];

        private static readonly FrozenDictionary<OpCode, OpCodeInfo> ByOpCode = All.ToFrozenDictionary(i => i.OpCode);

        public static IReadOnlyList<OpCodeInfo> Table => All;

        public static OpCodeInfo Get(OpCode opCode) =>
            ByOpCode.TryGetValue(opCode, out var info)
                ? info
                : throw new ArgumentOutOfRangeException(nameof(opCode), $"Unknown opcode 0x{(byte)opCode:X}.");

        public static bool TryGet(OpCode opCode, out OpCodeInfo info) => ByOpCode.TryGetValue(opCode, out info!);

        public static OpCodeInfo Info(this OpCode opCode) => Get(opCode);

        public static string Mnemonic(this OpCode opCode) => Get(opCode).Mnemonic;

        private static OpCodeInfo Op(OpCode opCode, string mnemonic, params OperandKind[] operands) =>
            new(opCode, mnemonic, operands, null, null);

        private static OpCodeInfo Family<TSubOp>(OpCode opCode, string mnemonic, params SubOpInfo[] subOps)
            where TSubOp : struct, Enum
        {
            var table = new SubOpInfo?[subOps.Max(s => s.Code) + 1];
            if (table.Length > 16)
                throw new InvalidOperationException($"'{mnemonic}' has more than 16 sub-operations.");
            foreach (var sub in subOps)
                table[sub.Code] = sub;
            return new(opCode, mnemonic, [], typeof(TSubOp), table);
        }

        private static SubOpInfo Sub<TSubOp>(TSubOp subOp, string name, params OperandKind[] operands)
            where TSubOp : struct, Enum => new(System.Convert.ToByte(subOp), name, operands);
    }
}

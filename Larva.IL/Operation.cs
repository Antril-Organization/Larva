namespace Larva.IL
{
    /// <summary>
    /// One IL operation. Operand layout is dictated by <see cref="OpCodes"/>; label operands hold the
    /// absolute index of the target operation inside its <see cref="OperationBlock"/>.
    /// </summary>
    public readonly record struct Operation
    {
        public OpCode OpCode { get; }

        /// <summary>Sub-operation nibble for family opcodes (<see cref="OpCodeInfo.IsFamily"/>); 0 otherwise.</summary>
        public byte SubOp { get; }

        public long[] Operands { get; }

        /// <summary>Plain opcode, e.g. <c>new Operation(OpCode.LoadLocal, 1)</c>.</summary>
        public Operation(OpCode opCode, params long[] operands)
        {
            var info = OpCodes.Get(opCode);
            if (info.IsFamily)
                throw new ArgumentException($"'{info.Mnemonic}' needs a {info.SubOpType!.Name} sub-operation.", nameof(opCode));

            Validate(info.Mnemonic, info.Operands, operands);
            OpCode = opCode;
            Operands = operands;
        }

        /// <summary>Family opcode, e.g. <c>new Operation(OpCode.Compare, Condition.Lt)</c>.</summary>
        public Operation(OpCode opCode, Enum subOp, params long[] operands)
        {
            var info = OpCodes.Get(opCode);
            if (info.SubOpType != subOp.GetType())
                throw new ArgumentException(
                    $"'{info.Mnemonic}' expects {(info.IsFamily ? info.SubOpType!.Name : "no sub-operation")}, got {subOp.GetType().Name}.",
                    nameof(subOp));

            this = FromCode(opCode, System.Convert.ToByte(subOp), operands);
        }

        private Operation(OpCode opCode, byte subOp, long[] operands, bool validated)
        {
            OpCode = opCode;
            SubOp = subOp;
            Operands = operands;
        }

        /// <summary>Builds an operation from a raw sub-operation code (ignored for plain opcodes); used by the decoder.</summary>
        public static Operation FromCode(OpCode opCode, byte subOp, params long[] operands)
        {
            var info = OpCodes.Get(opCode);
            if (!info.IsFamily)
                return new Operation(opCode, operands);

            var sub = info.GetSubOp(subOp);
            Validate($"{info.Mnemonic}.{sub.Name}", sub.Operands, operands);
            return new Operation(opCode, subOp, operands, validated: true);
        }

        public OpCodeInfo Info => OpCodes.Get(OpCode);

        public SubOpInfo? SubOpInfo => Info.IsFamily ? Info.GetSubOp(SubOp) : null;

        /// <summary>Assembly mnemonic, e.g. <c>ldloc</c> or <c>cmp.lt</c>.</summary>
        public string Mnemonic => SubOpInfo is { } sub ? $"{Info.Mnemonic}.{sub.Name}" : Info.Mnemonic;

        /// <summary>Declared operand layout (from the sub-operation for family opcodes).</summary>
        public OperandKind[] Shape => SubOpInfo?.Operands ?? Info.Operands;

        public bool HasLabelTable => HasTable(Shape);

        /// <summary>Kind of the operand at <paramref name="index"/>, expanding a trailing label table.</summary>
        public OperandKind OperandKindAt(int index) => KindAt(Shape, index);

        private static bool HasTable(OperandKind[] shape) => shape.Length > 0 && shape[^1] == OperandKind.LabelTable;

        private static OperandKind KindAt(OperandKind[] shape, int index) =>
            HasTable(shape) && index >= shape.Length - 1 ? OperandKind.Label : shape[index];

        private static void Validate(string mnemonic, OperandKind[] shape, long[] operands)
        {
            bool table = HasTable(shape);
            int fixedCount = table ? shape.Length - 1 : shape.Length;
            bool countOk = table ? operands.Length >= fixedCount : operands.Length == fixedCount;
            if (!countOk)
                throw new ArgumentException(
                    $"'{mnemonic}' expects {(table ? "at least " : "")}{fixedCount} operand(s), got {operands.Length}.",
                    nameof(operands));

            for (int i = 0; i < operands.Length; i++)
            {
                if (operands[i] < 0 && !KindAt(shape, i).IsSigned())
                    throw new ArgumentOutOfRangeException(nameof(operands), $"Operand {i} of '{mnemonic}' must be non-negative.");
            }
        }

        public bool Equals(Operation other) =>
            OpCode == other.OpCode && SubOp == other.SubOp && Operands.AsSpan().SequenceEqual(other.Operands);

        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(OpCode);
            hash.Add(SubOp);
            foreach (var operand in Operands)
                hash.Add(operand);
            return hash.ToHashCode();
        }

        /// <summary>Assembly-style text without block context (labels shown as <c>@index</c>).</summary>
        public override string ToString() => Text.Disassembler.Format(this, label => $"@{label}");
    }
}

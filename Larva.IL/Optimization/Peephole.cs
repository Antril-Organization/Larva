namespace Larva.IL.Optimization
{
    /// <summary>Local IL clean-ups that keep stack behavior identical.</summary>
    public static class Peephole
    {
        /// <summary>
        /// Removes <c>stloc X; ldloc X</c> when that is the only read of <c>X</c>, its address is never taken
        /// and nothing jumps to the <c>ldloc</c>. The value simply stays on the stack. Branch targets are remapped.
        /// </summary>
        public static OperationBlock RemoveStoreLoadPairs(OperationBlock block) => RemoveStoreLoadPairs(block, out _);

        public static OperationBlock RemoveStoreLoadPairs(OperationBlock block, out long[] newIndex) =>
            RemoveStoreLoadPairs(block, out newIndex, []);

        /// <param name="newIndex">
        /// For each original operation index (plus one past the end), its index in the result; a removed operation maps
        /// to the next kept one. Lets callers keep side tables (e.g. safepoints) in sync.
        /// </param>
        /// <param name="externalTargets">Operations entered from outside the code (e.g. exception handler starts).</param>
        public static OperationBlock RemoveStoreLoadPairs(OperationBlock block, out long[] newIndex, IEnumerable<long> externalTargets)
        {
            var ops = block.Operations;
            var targets = new HashSet<long>(externalTargets);
            var loads = new Dictionary<long, int>();
            var addressTaken = new HashSet<long>();
            foreach (var op in ops)
            {
                for (int i = 0; i < op.Operands.Length; i++)
                {
                    if (op.OperandKindAt(i) == OperandKind.Label)
                        targets.Add(op.Operands[i]);
                }
                if (op.OpCode == OpCode.LoadLocal)
                    loads[op.Operands[0]] = loads.GetValueOrDefault(op.Operands[0]) + 1;
                if (op.OpCode == OpCode.Stack && op.SubOp == (byte)StackOp.LoadLocalAddress)
                    addressTaken.Add(op.Operands[0]);
            }

            var removed = new bool[ops.Length];
            for (int i = 0; i + 1 < ops.Length; i++)
            {
                if (ops[i].OpCode != OpCode.StoreLocal || ops[i + 1].OpCode != OpCode.LoadLocal)
                    continue;
                long local = ops[i].Operands[0];
                if (ops[i + 1].Operands[0] != local || loads[local] != 1 || addressTaken.Contains(local) || targets.Contains(i + 1))
                    continue;
                removed[i] = removed[i + 1] = true;
                i++;
            }

            // newIndex[old] = number of kept operations before old, so a removed target falls through to the next kept one.
            newIndex = new long[ops.Length + 1];
            for (int i = 0; i < ops.Length; i++)
                newIndex[i + 1] = newIndex[i] + (removed[i] ? 0 : 1);

            var result = new List<Operation>(ops.Length);
            for (int i = 0; i < ops.Length; i++)
            {
                if (removed[i])
                    continue;
                var op = ops[i];
                long[] operands = [.. op.Operands];
                for (int o = 0; o < operands.Length; o++)
                {
                    if (op.OperandKindAt(o) == OperandKind.Label)
                        operands[o] = newIndex[operands[o]];
                }
                result.Add(Operation.FromCode(op.OpCode, op.SubOp, operands));
            }
            return block with { Operations = [.. result] };
        }
    }
}

namespace Larva.IR.Analysis
{
    /// <summary>
    /// Reference counting: the locals that hold a handle read from memory (<c>t = copy (*r).f</c>, an element, a field of a
    /// field) only to be read at once, which may borrow it instead of taking a reference. Nothing between the read and
    /// the local's last use can make the object lose that memory's reference: the uses are in the same block, none is a
    /// call (the callee could write the field), and no statement in between stores into memory, replaces or drops a local
    /// that owns something, frees, stores a global or runs an intrinsic. The memory's owner stays alive meanwhile: its
    /// local is not touched either, and the field still holds the object.
    /// </summary>
    internal static class FieldBorrows
    {
        public static HashSet<int> Of(IrFunction function, IrModule module, IReadOnlySet<int> addressTaken, Liveness liveness)
        {
            var result = new HashSet<int>();
            var assignments = new int[function.Locals.Length];
            foreach (var block in function.Blocks)
            {
                foreach (var statement in block.Statements)
                    if (statement is AssignStatement { Target: { IsLocal: true } target })
                        assignments[target.Local]++;
                if (block.Terminator is CallTerminator { Destination: { IsLocal: true } destination })
                    assignments[destination.Local]++;
            }

            for (int b = 0; b < function.Blocks.Length; b++)
            {
                var block = function.Blocks[b];
                for (int s = 0; s < block.Statements.Length; s++)
                {
                    if (block.Statements[s] is not AssignStatement { Target: { IsLocal: true } target, Value: UseRvalue { Operand: CopyOperand { Place: var source } } })
                        continue;
                    int t = target.Local;
                    if (source.IsLocal || source.Projections[0] is not DerefProjection || function.IsArgument(t) || addressTaken.Contains(t)
                        || assignments[t] != 1 || function.Locals[t].Type.Unoptional() is not GcType)
                        continue;
                    int root = source.Local;
                    if (addressTaken.Contains(root) || assignments[root] > (function.IsArgument(root) ? 0 : 1))
                        continue;
                    if (Safe(function, module, block, s, t, liveness, b))
                        result.Add(t);
                }
            }
            return result;
        }

        private static bool Safe(IrFunction function, IrModule module, BasicBlock block, int def, int t, Liveness liveness, int b)
        {
            bool unsafeSeen = false;
            for (int i = def + 1; i < block.Statements.Length; i++)
            {
                var statement = block.Statements[i];
                if (statement is StorageDeadStatement { Local: var dead } && dead == t)
                    return true;
                if (Liveness.Uses(statement).Contains(t))
                {
                    if (unsafeSeen)
                        return false;
                }
                if (MayRelease(function, module, statement, t))
                    unsafeSeen = true;
            }
            // Still live at the end of the block: only a terminator that reads it without calling may.
            if (liveness.LiveAfter(new Location(b, block.Statements.Length)).Contains(t))
                return false;
            return !Liveness.Uses(block.Terminator).Contains(t)
                || !unsafeSeen && block.Terminator is IfTerminator or SwitchTerminator;
        }

        /// <summary>Whether a statement can make some object lose a reference (and maybe die) or write memory.</summary>
        private static bool MayRelease(IrFunction function, IrModule module, Statement statement, int t) => statement switch
        {
            AssignStatement { Value: IntrinsicRvalue } => true,
            AssignStatement { Target: var target } when !target.IsLocal => target.Projections.Any(p => p is DerefProjection),
            AssignStatement { Target: var target } => target.Local != t && !function.Locals[target.Local].Type.HoldsNoReferences(module),
            StorageDeadStatement dead => dead.Local != t && !function.Locals[dead.Local].Type.HoldsNoReferences(module),
            FreeStatement or StoreGlobalStatement => true,
            _ => false,
        };
    }
}

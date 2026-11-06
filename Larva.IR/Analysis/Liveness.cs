namespace Larva.IR.Analysis
{
    /// <summary>
    /// Backward liveness of locals: a local is live at a point if its current value may be read later.
    /// Used by the borrow checker to decide which references (and therefore which loans) are still alive.
    /// </summary>
    internal sealed class Liveness
    {
        private readonly HashSet<int>[][] liveBefore;
        private readonly HashSet<int>[] liveOut;

        private Liveness(HashSet<int>[][] liveBefore, HashSet<int>[] liveOut)
        {
            this.liveBefore = liveBefore;
            this.liveOut = liveOut;
        }

        /// <summary>Locals live immediately before the statement or terminator at <paramref name="location"/>.</summary>
        public IReadOnlySet<int> LiveBefore(Location location) => liveBefore[location.Block][location.Index];

        /// <summary>Locals live when control leaves <paramref name="block"/> (before its successors run).</summary>
        public IReadOnlySet<int> LiveOut(int block) => liveOut[block];

        /// <summary>Locals live right after the statement or terminator at <paramref name="location"/>.</summary>
        public IReadOnlySet<int> LiveAfter(Location location) =>
            location.Index + 1 < liveBefore[location.Block].Length
                ? liveBefore[location.Block][location.Index + 1]
                : liveOut[location.Block];

        public static Liveness Compute(IrFunction function)
        {
            int count = function.Blocks.Length;
            var liveIn = new HashSet<int>[count];
            for (int b = 0; b < count; b++)
                liveIn[b] = [];

            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int b = count - 1; b >= 0; b--)
                {
                    var state = LiveOut(function, b, liveIn);
                    ApplyBlock(function.Blocks[b], state, perPoint: null);
                    if (!state.SetEquals(liveIn[b]))
                    {
                        liveIn[b] = state;
                        changed = true;
                    }
                }
            }

            var result = new HashSet<int>[count][];
            var outs = new HashSet<int>[count];
            for (int b = 0; b < count; b++)
            {
                result[b] = new HashSet<int>[function.Blocks[b].Statements.Length + 1];
                outs[b] = LiveOut(function, b, liveIn);
                ApplyBlock(function.Blocks[b], [.. outs[b]], result[b]);
            }
            return new Liveness(result, outs);
        }

        private static HashSet<int> LiveOut(IrFunction function, int block, HashSet<int>[] liveIn)
        {
            HashSet<int> state = [];
            foreach (int successor in function.Blocks[block].Terminator.Successors)
                state.UnionWith(liveIn[successor]);
            return state;
        }

        /// <summary>Walks the block backwards; when <paramref name="perPoint"/> is given, records the state before each point.</summary>
        private static void ApplyBlock(BasicBlock block, HashSet<int> state, HashSet<int>[]? perPoint)
        {
            Apply(state, TerminatorEffects(block.Terminator));
            if (perPoint is not null)
                perPoint[block.Statements.Length] = [.. state];

            for (int i = block.Statements.Length - 1; i >= 0; i--)
            {
                Apply(state, StatementEffects(block.Statements[i]));
                if (perPoint is not null)
                    perPoint[i] = [.. state];
            }
        }

        private static void Apply(HashSet<int> state, (IEnumerable<int> Uses, int? Def) effects)
        {
            if (effects.Def is int def)
                state.Remove(def);
            state.UnionWith(effects.Uses);
        }

        /// <summary>The locals a statement reads (an index, a place it reads through, an operand).</summary>
        public static IEnumerable<int> Uses(Statement statement) => StatementEffects(statement).Uses;

        /// <summary>The locals a terminator reads.</summary>
        public static IEnumerable<int> Uses(Terminator terminator) => TerminatorEffects(terminator).Uses;

        private static (IEnumerable<int> Uses, int? Def) StatementEffects(Statement statement) => statement switch
        {
            AssignStatement assign => (RvalueUses(assign.Value).Concat(TargetUses(assign.Target)), TargetDef(assign.Target)),
            StorageDeadStatement dead => ([], dead.Local),
            FreeStatement free => (PlaceUses(free.Pointer), null),
            StoreGlobalStatement store => (OperandUses(store.Value), null),
            _ => ([], null),
        };

        private static (IEnumerable<int> Uses, int? Def) TerminatorEffects(Terminator terminator) => terminator switch
        {
            IfTerminator branch => (OperandUses(branch.Condition), null),
            SwitchTerminator @switch => (OperandUses(@switch.Value), null),
            CallTerminator call => (call.Operands.SelectMany(OperandUses).Concat(TargetUses(call.Destination)), TargetDef(call.Destination)),
            DropTerminator drop => (PlaceUses(drop.Place), null),
            ReturnTerminator => ([0], null),
            ThrowTerminator @throw => (OperandUses(@throw.Value), null),
            _ => ([], null),
        };

        /// <summary>A whole-local assignment kills the old value; a partial one keeps the local alive.</summary>
        private static int? TargetDef(Place target) => target.IsLocal ? target.Local : null;

        private static IEnumerable<int> TargetUses(Place target) => target.IsLocal ? [] : PlaceUses(target);

        private static IEnumerable<int> RvalueUses(Rvalue rvalue) => rvalue switch
        {
            UseRvalue use => OperandUses(use.Operand),
            BinaryRvalue binary => OperandUses(binary.Left).Concat(OperandUses(binary.Right)),
            UnaryRvalue unary => OperandUses(unary.Operand),
            CastRvalue cast => OperandUses(cast.Operand),
            RefRvalue reference => PlaceUses(reference.Place),
            AllocRvalue alloc => alloc.Finalizer is { } finalizer ? OperandUses(alloc.Value).Concat(OperandUses(finalizer)) : OperandUses(alloc.Value),
            IntrinsicRvalue intrinsic => intrinsic.Arguments.SelectMany(OperandUses),
            ArrayRvalue array => OperandUses(array.Length),
            HandleCastRvalue cast => OperandUses(cast.Handle),
            WrapRvalue wrap => OperandUses(wrap.Value),
            UnwrapRvalue unwrap => OperandUses(unwrap.Value),
            IsNullRvalue test => PlaceUses(test.Value),
            LenRvalue len => PlaceUses(len.Array),
            _ => [],
        };

        private static IEnumerable<int> OperandUses(Operand operand) => operand switch
        {
            CopyOperand copy => PlaceUses(copy.Place),
            MoveOperand move => PlaceUses(move.Place),
            _ => [],
        };

        private static IEnumerable<int> PlaceUses(Place place) => place.IndexLocals().Prepend(place.Local);
    }
}

using System.Numerics;

namespace Larva.IR.Analysis
{
    /// <summary>
    /// Backward liveness of locals: a local is live at a point if its current value may be read later.
    /// Used by the borrow checker to decide which references (and therefore which loans) are still alive.
    /// </summary>
    /// <remarks>
    /// Solved once per block, as bitsets (live-in, live-out); the state at a point inside a block is computed when asked,
    /// by walking that block backwards from its live-out set (all its points at once, kept for a few recent blocks).
    /// </remarks>
    internal sealed class Liveness
    {
        private readonly IrFunction function;
        private readonly int words;
        private readonly ulong[][] liveIn;
        private readonly ulong[][] liveOut;

        /// <summary>
        /// The states before every point (<c>Statements.Length + 1</c> bitsets, flat) of the blocks asked last. Callers
        /// walk blocks in order, so a handful keeps each block computed about once; entries are replaced whole.
        /// </summary>
        private readonly BlockPoints?[] recent = new BlockPoints?[4];
        private int nextRecent;

        private sealed record BlockPoints(int Block, ulong[] Bits);

        private Liveness(IrFunction function, int words, ulong[][] liveIn, ulong[][] liveOut)
        {
            this.function = function;
            this.words = words;
            this.liveIn = liveIn;
            this.liveOut = liveOut;
        }

        /// <summary>Locals live immediately before the statement or terminator at <paramref name="location"/>.</summary>
        public IReadOnlySet<int> LiveBefore(Location location) =>
            location.Index == 0
                ? new LiveSet(liveIn[location.Block], 0, words)
                : new LiveSet(PointsOf(location.Block), location.Index * words, words);

        /// <summary>Locals live when control leaves <paramref name="block"/> (before its successors run).</summary>
        public IReadOnlySet<int> LiveOut(int block) => new LiveSet(liveOut[block], 0, words);

        /// <summary>Locals live right after the statement or terminator at <paramref name="location"/>.</summary>
        public IReadOnlySet<int> LiveAfter(Location location) =>
            location.Index < function.Blocks[location.Block].Statements.Length
                ? new LiveSet(PointsOf(location.Block), (location.Index + 1) * words, words)
                : LiveOut(location.Block);

        public static Liveness Compute(IrFunction function)
        {
            int count = function.Blocks.Length;
            int words = Math.Max(1, (function.Locals.Length + 63) >> 6);

            // Per block, walking it backwards once: what it reads before writing it (gen) and what it writes whole (kill).
            var gen = new ulong[count][];
            var kill = new ulong[count][];
            var liveIn = new ulong[count][];
            var liveOut = new ulong[count][];
            for (int b = 0; b < count; b++)
            {
                gen[b] = new ulong[words];
                kill[b] = new ulong[words];
                liveIn[b] = new ulong[words];
                liveOut[b] = new ulong[words];
                var block = function.Blocks[b];
                Compose(gen[b], kill[b], TerminatorEffects(block.Terminator));
                for (int i = block.Statements.Length - 1; i >= 0; i--)
                    Compose(gen[b], kill[b], StatementEffects(block.Statements[i]));
            }

            // The sets only grow, so a block's live-in changes exactly when a bit is added.
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int b = count - 1; b >= 0; b--)
                {
                    var outs = liveOut[b];
                    foreach (int successor in function.Blocks[b].Terminator.Successors)
                    {
                        var into = liveIn[successor];
                        for (int w = 0; w < words; w++)
                            outs[w] |= into[w];
                    }
                    var ins = liveIn[b];
                    var g = gen[b];
                    var k = kill[b];
                    for (int w = 0; w < words; w++)
                    {
                        ulong value = g[w] | (outs[w] & ~k[w]);
                        if (value != ins[w])
                        {
                            ins[w] = value;
                            changed = true;
                        }
                    }
                }
            }
            return new Liveness(function, words, liveIn, liveOut);
        }

        /// <summary>Puts a point in front of what is composed so far, walking backwards: in = gen | (out &amp; ~kill).</summary>
        private static void Compose(ulong[] gen, ulong[] kill, (IEnumerable<int> Uses, int? Def) effects)
        {
            if (effects.Def is int def)
            {
                kill[def >> 6] |= 1UL << def;
                gen[def >> 6] &= ~(1UL << def);
            }
            foreach (int use in effects.Uses)
                gen[use >> 6] |= 1UL << use;
        }

        /// <summary>The state before a point from the state after it, in the bitset at <paramref name="at"/>.</summary>
        private static void Apply(ulong[] state, int at, (IEnumerable<int> Uses, int? Def) effects)
        {
            if (effects.Def is int def)
                state[at + (def >> 6)] &= ~(1UL << def);
            foreach (int use in effects.Uses)
                state[at + (use >> 6)] |= 1UL << use;
        }

        /// <summary>The state before each point of block <paramref name="b"/>, walking it backwards from its live-out set.</summary>
        private ulong[] PointsOf(int b)
        {
            foreach (var entry in recent)
            {
                if (entry is not null && entry.Block == b)
                    return entry.Bits;
            }
            var block = function.Blocks[b];
            int last = block.Statements.Length;
            var bits = new ulong[(last + 1) * words];
            Array.Copy(liveOut[b], 0, bits, last * words, words);
            Apply(bits, last * words, TerminatorEffects(block.Terminator));
            for (int i = last - 1; i >= 0; i--)
            {
                Array.Copy(bits, (i + 1) * words, bits, i * words, words);
                Apply(bits, i * words, StatementEffects(block.Statements[i]));
            }
            lock (recent)
            {
                recent[nextRecent] = new BlockPoints(b, bits);
                nextRecent = (nextRecent + 1) % recent.Length;
            }
            return bits;
        }

        /// <summary>A read-only view of one bitset of locals; enumerates in ascending order.</summary>
        private sealed class LiveSet(ulong[] bits, int start, int words) : IReadOnlySet<int>
        {
            public int Count
            {
                get
                {
                    int count = 0;
                    for (int w = 0; w < words; w++)
                        count += BitOperations.PopCount(bits[start + w]);
                    return count;
                }
            }

            public bool Contains(int item) => item >= 0 && (item >> 6) < words && (bits[start + (item >> 6)] & (1UL << item)) != 0;

            public IEnumerator<int> GetEnumerator()
            {
                for (int w = 0; w < words; w++)
                {
                    ulong word = bits[start + w];
                    while (word != 0)
                    {
                        yield return (w << 6) + BitOperations.TrailingZeroCount(word);
                        word &= word - 1;
                    }
                }
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

            private HashSet<int> Copy() => [.. this];
            public bool IsProperSubsetOf(IEnumerable<int> other) => Copy().IsProperSubsetOf(other);
            public bool IsProperSupersetOf(IEnumerable<int> other) => Copy().IsProperSupersetOf(other);
            public bool IsSubsetOf(IEnumerable<int> other) => Copy().IsSubsetOf(other);
            public bool IsSupersetOf(IEnumerable<int> other) => other.All(Contains);
            public bool Overlaps(IEnumerable<int> other) => other.Any(Contains);
            public bool SetEquals(IEnumerable<int> other) => Copy().SetEquals(other);
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

namespace Larva.IR.Analysis
{
    /// <summary>A forward dataflow analysis whose state is mutated in place by the transfer functions.</summary>
    internal interface IForwardAnalysis<TState> where TState : class
    {
        TState Entry();
        TState Clone(TState state);

        /// <summary>Merges <paramref name="from"/> into <paramref name="into"/>; returns whether <paramref name="into"/> changed.</summary>
        bool Join(TState into, TState from);

        void Statement(TState state, Statement statement, Location location);
        void Terminator(TState state, Terminator terminator, Location location);

        /// <summary>
        /// Effect of a terminator on its exception edges (to <see cref="IUnwinding.Handlers"/>). By default the same as
        /// <see cref="Terminator"/>; analyses override it when, e.g., a call's destination is not written when it throws.
        /// </summary>
        void Unwind(TState state, Terminator terminator, Location location) => Terminator(state, terminator, location);

        /// <summary>
        /// What is known on the edge to <paramref name="successor"/> only (e.g. the condition of an <c>if</c> on its then
        /// edge). Returns <paramref name="state"/> itself when the edge adds nothing, or a refined copy; never mutates it.
        /// </summary>
        TState Refine(TState state, Terminator terminator, int successor) => state;
    }

    internal static class Dataflow
    {
        /// <summary>
        /// Solves <paramref name="analysis"/> to a fixed point. Returns the state at entry of each block,
        /// or <c>null</c> for blocks unreachable from <c>bb0</c>.
        /// </summary>
        public static TState?[] Forward<TState>(IrFunction function, IForwardAnalysis<TState> analysis) where TState : class
        {
            var entries = new TState?[function.Blocks.Length];
            entries[0] = analysis.Entry();
            // Blocks in reverse postorder: each runs after its predecessors (but for back edges), so a block is redone only
            // when a loop brings it something new. (First-in-first-out redid every block after a join once per incoming
            // edge: cubic in a chain of hundreds of `&&`.)
            var order = ReversePostorder(function);
            PriorityQueue<int, int> worklist = new();
            worklist.Enqueue(0, order[0]);
            var queued = new bool[function.Blocks.Length];
            queued[0] = true;

            while (worklist.TryDequeue(out int b, out _))
            {
                queued[b] = false;
                var state = analysis.Clone(entries[b]!);
                var block = function.Blocks[b];
                for (int i = 0; i < block.Statements.Length; i++)
                    analysis.Statement(state, block.Statements[i], new Location(b, i));

                Location end = new(b, block.Statements.Length);
                var handlers = block.Terminator is IUnwinding unwinding ? unwinding.Handlers.Select(h => h.Block).ToHashSet() : [];
                TState? unwound = null;
                if (handlers.Count > 0)
                {
                    unwound = analysis.Clone(state);
                    analysis.Unwind(unwound, block.Terminator, end);
                }
                analysis.Terminator(state, block.Terminator, end);

                foreach (int successor in block.Terminator.Successors.Distinct())
                {
                    var edge = handlers.Contains(successor) ? unwound! : analysis.Refine(state, block.Terminator, successor);
                    bool changed;
                    if (entries[successor] is null)
                    {
                        entries[successor] = analysis.Clone(edge);
                        changed = true;
                    }
                    else
                    {
                        changed = analysis.Join(entries[successor]!, edge);
                    }

                    if (changed && !queued[successor])
                    {
                        queued[successor] = true;
                        worklist.Enqueue(successor, order[successor]);
                    }
                }
            }
            return entries;
        }

        /// <summary>
        /// Each block's position in reverse postorder from <c>bb0</c> (a block's first successor comes before its other
        /// ones); unreachable blocks get <see cref="int.MaxValue"/>.
        /// </summary>
        internal static int[] ReversePostorder(IrFunction function)
        {
            int count = function.Blocks.Length;
            var order = new int[count];
            Array.Fill(order, int.MaxValue);
            var visited = new bool[count];
            var successors = new int[count][];
            var stack = new Stack<(int Block, int Next)>();
            int position = count;
            visited[0] = true;
            stack.Push((0, 0));
            while (stack.TryPop(out var frame))
            {
                // Successors are visited last to first, so the first one finishes last and comes first in the order.
                var next = successors[frame.Block] ??= [.. function.Blocks[frame.Block].Terminator.Successors.Distinct().Reverse()];
                if (frame.Next < next.Length)
                {
                    stack.Push((frame.Block, frame.Next + 1));
                    int successor = next[frame.Next];
                    if (!visited[successor])
                    {
                        visited[successor] = true;
                        stack.Push((successor, 0));
                    }
                    continue;
                }
                order[frame.Block] = --position;
            }
            return order;
        }
    }
}

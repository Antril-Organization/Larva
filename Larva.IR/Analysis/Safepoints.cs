namespace Larva.IR.Analysis
{
    /// <summary>
    /// Where native code polls for a collection: at the header of every loop that allocates (the target of a back edge of
    /// a depth-first walk, so such a loop polls once per iteration), and at the entry of the functions that allocate (or
    /// ask for a collection) or can be re-entered without a loop: those in a cycle of direct calls
    /// (recursion) and those that call through a function value or a vtable. Every other function runs a bounded amount
    /// of code, allocating nothing, before it returns or reaches a poll, so it does not poll itself. A loop that only
    /// calls needs no poll of its own: a callee that allocates polls at its entry, each time round.
    /// </summary>
    public static class Safepoints
    {
        /// <summary>The successors of a terminator on the path that does not throw.</summary>
        public static IEnumerable<int> NormalSuccessors(Terminator terminator) => terminator switch
        {
            CallTerminator call => [call.Target],
            ThrowTerminator or ResumeTerminator => [],
            _ => terminator.Successors,
        };

        /// <summary>
        /// The headers of the loops of <paramref name="function"/> that poll: those whose body allocates (or asks for a
        /// collection: the runtime's other services never allocate on the collected heap). The runtime is single-threaded and only an allocation makes a collection due; the functions a loop
        /// calls poll at their entry when they allocate, so a loop that does not allocate itself needs no poll.
        /// </summary>
        public static HashSet<int> PollingLoopHeaders(IrFunction function) => PollingLoopHeaders(function, everyLoop: false);

        /// <summary>
        /// The polling loop headers of a function of <paramref name="module"/>: in a program that starts threads, every loop
        /// polls (a collection stops every thread at a safepoint, and a loop that allocates nothing must reach one too).
        /// </summary>
        public static HashSet<int> PollingLoopHeaders(IrFunction function, IrModule module) => PollingLoopHeaders(function, Threaded(module));

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IrModule, object> threaded = new();

        /// <summary>Whether the program starts threads (it calls <c>thread.start</c>).</summary>
        public static bool Threaded(IrModule module) => (bool)threaded.GetValue(module, m => m.Functions.Any(f => f.Blocks.Any(b =>
            b.Statements.Any(s => s is AssignStatement { Value: IntrinsicRvalue { Name: Intrinsics.ThreadStart } }))));

        public static HashSet<int> PollingLoopHeaders(IrFunction function, bool everyLoop)
        {
            var headers = new HashSet<int>();
            var edges = BackEdges(function);
            if (edges.Count == 0)
                return headers;
            int count = function.Blocks.Length;
            var predecessors = new List<int>[count];
            for (int i = 0; i < count; i++)
                predecessors[i] = [];
            for (int i = 0; i < count; i++)
            {
                foreach (int successor in NormalSuccessors(function.Blocks[i].Terminator))
                {
                    if (successor >= 0 && successor < count)
                        predecessors[successor].Add(i);
                }
            }
            foreach (var (tail, header) in edges)
            {
                if (headers.Contains(header))
                    continue;
                // The natural loop: the header and every block that reaches the tail without passing through it.
                var body = new HashSet<int> { header };
                var work = new Stack<int>();
                if (body.Add(tail))
                    work.Push(tail);
                while (work.Count > 0)
                {
                    foreach (int p in predecessors[work.Pop()])
                    {
                        if (body.Add(p))
                            work.Push(p);
                    }
                }
                if (everyLoop || body.Any(b => Allocates(function.Blocks[b])))
                    headers.Add(header);
            }
            return headers;
        }

        private static bool Allocates(BasicBlock block)
        {
            foreach (var statement in block.Statements)
            {
                if (statement is AssignStatement { Value: AllocRvalue or ArrayRvalue or IntrinsicRvalue { Name: "gc.request" or "gc.custom" } })
                    return true;
            }
            return false;
        }

        /// <summary>The back edges (tail → header) of a depth-first walk from the entry (exception edges ignored).</summary>
        private static List<(int Tail, int Header)> BackEdges(IrFunction function)
        {
            var edges = new List<(int, int)>();
            int count = function.Blocks.Length;
            if (count == 0)
                return edges;
            var state = new byte[count];
            var stack = new Stack<(int Block, IEnumerator<int> Next)>();
            state[0] = 1;
            stack.Push((0, NormalSuccessors(function.Blocks[0].Terminator).GetEnumerator()));
            while (stack.Count > 0)
            {
                var (block, next) = stack.Peek();
                if (next.MoveNext())
                {
                    int successor = next.Current;
                    if (successor < 0 || successor >= count)
                        continue;
                    if (state[successor] == 1)
                    {
                        edges.Add((block, successor));
                    }
                    else if (state[successor] == 0)
                    {
                        state[successor] = 1;
                        stack.Push((successor, NormalSuccessors(function.Blocks[successor].Terminator).GetEnumerator()));
                    }
                }
                else
                {
                    state[block] = 2;
                    stack.Pop();
                }
            }
            return edges;
        }

        /// <summary>The blocks of <paramref name="function"/> that start a loop (exception edges ignored).</summary>
        public static HashSet<int> LoopHeaders(IrFunction function)
        {
            HashSet<int> headers = [];
            int count = function.Blocks.Length;
            if (count == 0)
                return headers;
            var state = new byte[count]; // 0 new, 1 on the walk's path, 2 done
            var stack = new Stack<(int Block, IEnumerator<int> Next)>();
            state[0] = 1;
            stack.Push((0, NormalSuccessors(function.Blocks[0].Terminator).GetEnumerator()));
            while (stack.Count > 0)
            {
                var (block, next) = stack.Peek();
                if (next.MoveNext())
                {
                    int successor = next.Current;
                    if (successor < 0 || successor >= count)
                        continue;
                    if (state[successor] == 1)
                    {
                        headers.Add(successor);
                    }
                    else if (state[successor] == 0)
                    {
                        state[successor] = 1;
                        stack.Push((successor, NormalSuccessors(function.Blocks[successor].Terminator).GetEnumerator()));
                    }
                }
                else
                {
                    state[block] = 2;
                    stack.Pop();
                }
            }
            return headers;
        }

        /// <summary>
        /// The runtime services during which no collection can happen: they neither allocate on the collected heap nor wait
        /// (another thread collects only while this one is stopped at a safepoint or waiting in the runtime).
        /// </summary>
        private static bool Quick(string intrinsic) =>
            intrinsic.StartsWith("atomic.") || intrinsic.StartsWith("math.") || intrinsic.StartsWith("f64.") || intrinsic.StartsWith("print.")
            || intrinsic is "thread.id" or "cpu.count" or "time.now" or "time.utc" or "time.precise" or "time.offset" or "mutex.trylock"
                or "mutex.unlock" or "cond.signal" or "cond.broadcast" or "format.f64" or "format.unit" or "gc.collections" or "gc.objects";

        /// <summary>
        /// For each function: whether a collection can happen while it runs (it allocates, polls, calls through a function
        /// value, waits in the runtime, or calls a function that may). One that cannot need not pin what it borrows inside gc
        /// objects: nothing moves before it returns.
        /// </summary>
        public static bool[] MayCollect(IrModule module)
        {
            int n = module.Functions.Length;
            var entry = EntryPolls(module);
            bool threaded = Threaded(module);
            var may = new bool[n];
            var callers = new List<int>[n];
            for (int f = 0; f < n; f++)
                callers[f] = [];
            for (int f = 0; f < n; f++)
            {
                var function = module.Functions[f];
                if (function.IsDeclaration || entry[f] || PollingLoopHeaders(function, threaded).Count > 0)
                    may[f] = true;
                foreach (var block in function.Blocks)
                {
                    foreach (var statement in block.Statements)
                    {
                        if (statement is AssignStatement { Value: AllocRvalue or ArrayRvalue })
                            may[f] = true;
                        else if (statement is AssignStatement { Value: IntrinsicRvalue intrinsic } && !Quick(intrinsic.Name))
                            may[f] = true;
                    }
                    if (block.Terminator is CallTerminator call)
                    {
                        if (call.Callee is not null || call.Function < 0 || call.Function >= n)
                            may[f] = true;
                        else
                            callers[call.Function].Add(f);
                    }
                }
            }
            // What may collect makes its callers so too.
            var work = new Stack<int>();
            for (int f = 0; f < n; f++)
            {
                if (may[f])
                    work.Push(f);
            }
            while (work.Count > 0)
            {
                foreach (int caller in callers[work.Pop()])
                {
                    if (!may[caller])
                    {
                        may[caller] = true;
                        work.Push(caller);
                    }
                }
            }
            return may;
        }

        /// <summary>For each function of <paramref name="module"/>: whether it polls at its entry (see the class).</summary>
        /// <summary>A function whose body is a call into C (attribute <c>Larva.Import</c> or <c>Larva.NativeCall</c>).</summary>
        private static bool CallsC(IrFunction function) => function.Attributes.Any(a => a.Name is "Larva.Import" or "Larva.NativeCall");

        public static bool[] EntryPolls(IrModule module)
        {
            int n = module.Functions.Length;
            var polls = new bool[n];
            var calls = new List<int>[n];
            for (int f = 0; f < n; f++)
            {
                var targets = new List<int>();
                foreach (var block in module.Functions[f].Blocks)
                {
                    // A function that allocates polls too: what is allocated between two safepoints stays bounded.
                    if (block.Statements.Any(s => s is AssignStatement { Value: AllocRvalue or ArrayRvalue or IntrinsicRvalue { Name: "gc.request" or "gc.custom" } }))
                        polls[f] = true;
                    if (block.Terminator is not CallTerminator call)
                        continue;
                    // A call through a function value polls, unless it calls C code (an import, a func* helper): C does
                    // not allocate here, and a Larva function C calls back polls itself.
                    if (call.Callee is not null && !CallsC(module.Functions[f]))
                        polls[f] = true;
                    else if (call.Function >= 0 && call.Function < n)
                        targets.Add(call.Function);
                }
                calls[f] = targets;
            }
            // Tarjan's strongly connected components, iteratively: a function in a cycle (or calling itself) polls.
            var index = new int[n];
            var low = new int[n];
            var onStack = new bool[n];
            Array.Fill(index, -1);
            var components = new Stack<int>();
            int counter = 0;
            for (int root = 0; root < n; root++)
            {
                if (index[root] >= 0)
                    continue;
                var work = new Stack<(int Function, int Edge)>();
                work.Push((root, 0));
                index[root] = low[root] = counter++;
                components.Push(root);
                onStack[root] = true;
                while (work.Count > 0)
                {
                    var (f, edge) = work.Pop();
                    if (edge < calls[f].Count)
                    {
                        work.Push((f, edge + 1));
                        int g = calls[f][edge];
                        if (g == f)
                        {
                            polls[f] = true;
                        }
                        else if (index[g] < 0)
                        {
                            index[g] = low[g] = counter++;
                            components.Push(g);
                            onStack[g] = true;
                            work.Push((g, 0));
                        }
                        else if (onStack[g])
                        {
                            low[f] = Math.Min(low[f], index[g]);
                        }
                        continue;
                    }
                    if (work.Count > 0)
                    {
                        int parent = work.Peek().Function;
                        low[parent] = Math.Min(low[parent], low[f]);
                    }
                    if (low[f] == index[f])
                    {
                        var members = new List<int>();
                        int member;
                        do
                        {
                            member = components.Pop();
                            onStack[member] = false;
                            members.Add(member);
                        }
                        while (member != f);
                        if (members.Count > 1)
                        {
                            foreach (int m in members)
                                polls[m] = true;
                        }
                    }
                }
            }
            return polls;
        }
    }
}

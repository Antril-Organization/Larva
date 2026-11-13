using Larva.IR;
using Larva.IR.Analysis;
using Larva.IR.Lowering;
using static Larva.Wasm.WasmCodegen;

namespace Larva.Wasm
{
    /// <summary>
    /// Compiles one function: frame slots in linear memory (wasm locals for plain scalars); structured control flow, or a
    /// dispatch loop over the blocks.
    /// </summary>
    internal sealed class WasmFunctionCompiler
    {
        // Wasm locals: the two parameters, then the frame pointer, the dispatch state, an address, the caller's stack
        // pointer and two i64 scratch values; then the i64 locals of the function's scalars (see `registers`) and of
        // its scalar temps.
        private const int Args = 0, Ret = 1, Fp = 2, State = 3, Addr = 4, Saved = 5, A = 6, B = 7;
        private static readonly ValType[] FixedLocals = [ValType.I32, ValType.I32, ValType.I32, ValType.I32, ValType.I64, ValType.I64];
        private int localCount = 2 + FixedLocals.Length;

        private readonly WasmCodegen cg;
        /// <summary>The GC of the function's domain (the program's, with one domain).</summary>
        private readonly GcConfig gc;
        private bool Has(GcCapabilities capability) => (gc.Capabilities & capability) != 0;
        private Code c = new();
        private readonly IrFunction function;
        private readonly IrModule module;
        private readonly TypeContext context;
        private readonly MoveAnalysis moves;
        private readonly BoundsAnalysis? bounds;

        private readonly int[] slots;
        /// <summary>
        /// The wasm local of each IR local kept out of the frame (-1: in its frame slot): integers, bools and floats whose
        /// address is never taken. They hold no handles, so the collector (which scans the frames) has nothing to miss.
        /// </summary>
        private readonly int[] registers;
        private readonly Dictionary<Place, int> flags = [];
        private readonly Dictionary<Location, int> pins = [];
        private readonly List<(int Offset, int Size)> temps = [];
        private int tempCursor;
        // Temps of scalars that are no handles live in wasm locals (a temp below zero is local -1 - temp), the others
        // in the frame (where the collector sees them).
        private readonly List<int> scalarTemps = [];
        private int scalarCursor;
        private int frame;
        private Location current;

        // Dispatch: one state per block, then one per catch landing, then the cleanup landing.
        private int states, caseIndex, depth;
        private readonly Dictionary<int, int> clauseLandings = [];
        private int cleanupLanding = -1;

        private bool handles, needsCleanup;
        private int caughtPayload = int.MinValue, caughtType, regionDepth = int.MinValue;

        public WasmFunctionCompiler(WasmCodegen cg, IrFunction function)
        {
            this.cg = cg;
            gc = cg.Module.CodeGcOf(function.Domain, cg.Gc);
            this.function = function;
            module = cg.Module;
            context = TypeContext.For(function, module);
            moves = new MoveAnalysis(function, module);
            bounds = cg.EliminateBoundsChecks ? BoundsAnalysis.Run(function, module) : null;
            slots = new int[function.Locals.Length];
            registers = new int[function.Locals.Length];
        }

        /// <summary>A value a wasm local can hold: an integer of at most 64 bits, a bool or a float (never a handle).</summary>
        private bool Plain(IrType type) =>
            type is PrimitiveType { Kind: Primitive.Bool or Primitive.F32 or Primitive.F64 } or PrimitiveType { IsInteger: true, BitWidth: <= 64 };

        private int NewLocal() => localCount++;

        /// <summary>A temp for an i64 value (canonical bits; a float's bits) in a wasm local.</summary>
        private int ScalarTemp()
        {
            if (scalarCursor == scalarTemps.Count)
                scalarTemps.Add(NewLocal());
            return -1 - scalarTemps[scalarCursor++];
        }

        /// <summary>A temp for a value of <paramref name="type"/>: a wasm local for a plain scalar, else a frame slot.</summary>
        private int TempFor(IrType type) => Plain(type) ? ScalarTemp() : Temp(Math.Max(8, cg.Shape(type).Size));

        private static bool IsLocal(int temp) => temp < 0;
        private static int LocalOf(int temp) => -1 - temp;

        /// <summary>Where the temps stand (to give back the ones taken after it).</summary>
        private (int Memory, int Scalar) Cursor
        {
            get => (tempCursor, scalarCursor);
            set => (tempCursor, scalarCursor) = value;
        }

        private int Slot(int size, int align)
        {
            frame = Align(frame, align);
            int offset = frame;
            frame += Math.Max(1, size);
            return offset;
        }

        private int Temp(int size = 8)
        {
            size = Math.Max(8, (size + 7) / 8 * 8);
            for (int i = tempCursor; i < temps.Count; i++)
            {
                if (temps[i].Size >= size)
                {
                    (temps[i], temps[tempCursor]) = (temps[tempCursor], temps[i]);
                    return temps[tempCursor++].Offset;
                }
            }
            temps.Add((Slot(size, 16), size));
            (temps[^1], temps[tempCursor]) = (temps[tempCursor], temps[^1]);
            return temps[tempCursor++].Offset;
        }

        public (ValType[] Locals, Code Code) Compile()
        {
            var addressed = new bool[function.Locals.Length];
            foreach (var statement in function.Blocks.SelectMany(b => b.Statements))
            {
                if (statement is AssignStatement { Value: RefRvalue reference })
                    addressed[reference.Place.Local] = true;
            }
            for (int l = 0; l < function.Locals.Length; l++)
            {
                var type = function.Locals[l].Type;
                cg.Shape(type);
                registers[l] = -1;
                if (Plain(type) && !addressed[l])
                {
                    registers[l] = NewLocal();
                    continue;
                }
                var layout = type.IsUnit ? null : Layouts.Of(type, module);
                slots[l] = layout is null ? 0 : Slot(layout.Size, Math.Min(16, Math.Max(8, layout.Align)));
            }
            foreach (var path in Enumerable.Range(0, function.Locals.Length).SelectMany(l => Owned(function.Locals[l].Type, []).Select(o => new Place(l, o.Path))))
                flags[path] = Slot(1, 1);
            PlanPins();
            PlanExceptions();

            int blocks = function.Blocks.Length;
            foreach (var block in function.Blocks)
            {
                if (block.Terminator is IUnwinding unwinding)
                {
                    foreach (var clause in unwinding.Handlers)
                        clauseLandings.TryAdd(clause.Block, blocks + clauseLandings.Count);
                }
            }
            states = blocks + clauseLandings.Count;
            if (needsCleanup)
                cleanupLanding = states++;
            landingTargets = new int[clauseLandings.Count];
            foreach (var (target, landing) in clauseLandings)
                landingTargets[landing - blocks] = target;

            // Body: structured control flow when the graph is reducible (nearly always), else the dispatch loop.
            int checks = cg.BoundsChecks, eliminated = cg.BoundsChecksEliminated;
            if (PlanStructured())
            {
                try
                {
                    DoTree(0);
                    c.Unreachable();
                }
                catch (IrreducibleException)
                {
                    // Not expected (the plan checked the graph): the dispatch loop takes any graph.
                    structured = false;
                    c = new Code();
                    labels.Clear();
                    (cg.BoundsChecks, cg.BoundsChecksEliminated) = (checks, eliminated);
                }
            }
            if (!structured)
            {
                // The dispatch loop, one nested block per state.
                c.Loop();
                for (int s = 0; s < states; s++)
                    c.Block();
                c.LocalGet(State);
                c.BrTable(states);
                for (int s = 0; s < states; s++)
                {
                    c.End();
                    EmitState(s);
                }
                c.End();
                c.Unreachable();
            }

            // Prologue: the frame, the stack check, the arguments, drop flags and exception slots.
            int frameSize = Align(frame, 16);
            var p = new Code();
            p.GlobalGet(Sp);
            p.LocalTee(Saved);
            p.I32(frameSize);
            p.I32Sub();
            p.LocalTee(Fp);
            p.GlobalSet(Sp);
            p.LocalGet(Fp);
            p.I32(cg.StackLimit);
            p.I32LtU();
            p.If();
            p.I32(6);
            p.I64(0);
            p.I64(0);
            p.Call(cg.Runtime("larva_panic"));
            p.Unreachable();
            p.End();
            int offset = 0;
            for (int l = 1; l <= function.ArgumentCount; l++)
            {
                var (kind, size, _) = cg.Shape(function.Locals[l].Type);
                if (kind == Kind.Unit)
                    continue;
                if (registers[l] >= 0)
                {
                    p.LocalGet(Args);
                    p.I64LoadSized(size, cg.Shape(function.Locals[l].Type).Signed, offset);
                    p.LocalSet(registers[l]);
                }
                else
                {
                    Copy(p, () => { p.LocalGet(Fp); p.I32(slots[l]); p.I32Add(); }, () => { p.LocalGet(Args); p.I32(offset); p.I32Add(); }, size);
                }
                offset += Align(size, 8);
            }
            foreach (var (place, flag) in flags)
            {
                p.LocalGet(Fp);
                p.I32(function.IsArgument(place.Local) ? 1 : 0);
                p.I32Store8(flag);
            }
            foreach (var pin in pins.Values)
            {
                p.LocalGet(Fp);
                p.I64(0);
                p.I64Store(pin);
            }
            if (handles)
            {
                p.LocalGet(Fp);
                p.I64(0);
                p.I64Store(caughtPayload);
            }
            if (regionDepth != int.MinValue)
            {
                p.LocalGet(Fp);
                p.Call(cg.Runtime("larva_region_depth"));
                p.I64ExtendI32U();
                p.I64Store(regionDepth);
            }
            p.Append(c);
            var locals = new ValType[localCount - 2];
            Array.Fill(locals, ValType.I64);
            FixedLocals.CopyTo(locals, 0);
            return (locals, p);
        }

        /// <summary>The code of one state: a block's statements and terminator, a catch landing, or the cleanup landing.</summary>
        private void EmitState(int state)
        {
            caseIndex = state;
            depth = 0;
            int blocks = function.Blocks.Length;
            if (state < blocks)
            {
                var block = function.Blocks[state];
                for (int s = 0; s < block.Statements.Length; s++)
                {
                    current = new Location(state, s);
                    Cursor = (0, 0);
                    Statement(block.Statements[s]);
                }
                current = new Location(state, block.Statements.Length);
                Cursor = (0, 0);
                Terminator(block.Terminator);
            }
            else if (state != cleanupLanding)
            {
                // The exception in flight is now the one being handled; the one handled before (if any) is finished.
                FreeCaught();
                c.LocalGet(Fp);
                c.GlobalGet(InFlight);
                c.I64ExtendI32U();
                c.I64Store(caughtPayload);
                c.LocalGet(Fp);
                c.GlobalGet(ExceptionType);
                c.I64ExtendI32U();
                c.I64Store(caughtType);
                c.I32(0);
                c.GlobalSet(InFlight);
                JumpTo(landingTargets[state - blocks]);
            }
            else
            {
                Cursor = (0, 0);
                for (int l = 0; l < function.Locals.Length; l++)
                    ReleaseOwned(new Place(l), []);
                if (handles)
                    FreeCaught();
                foreach (var pin in pins.Values)
                {
                    c.LocalGet(Fp);
                    c.I64Load(pin);
                    c.I32WrapI64();
                    c.Call(cg.Runtime("larva_unpin"));
                }
                if (regionDepth != int.MinValue)
                {
                    c.LocalGet(Fp);
                    c.I64Load(regionDepth);
                    c.I32WrapI64();
                    c.Call(cg.Runtime("larva_region_unwind"));
                }
                Leave(); // the exception goes on to the caller
            }
        }

        // ── Structured control flow ────────────────────────────
        //
        // For a reducible graph (every loop entered only at its header) the code is nested blocks, loops and ifs, the
        // translation of "Beyond Relooper" (Ramsey, 2022): over the dominator tree, a state's code is wrapped in one
        // `block` per child that is a merge node (entered by two forward jumps or more; the innermost block for the
        // earliest in reverse postorder), each child's code following its block's `end`, and in a `loop` when the state is
        // a loop header. A jump backward (to a loop header) or to a merge node is a `br` to its label; any other jump goes
        // to a state that only that jump enters, whose code is emitted in its place. Each state's code is emitted once.

        private sealed class IrreducibleException : Exception;

        private enum LabelKind : byte { If, Block, Loop }

        private bool structured;
        private int[] landingTargets = [];
        private int[] order = [];               // reverse postorder number of each state (-1: unreachable)
        private bool[] merge = [], loopHeader = [];
        private List<int>[] mergeChildren = [];
        private int node = -1;                  // the state whose code is being emitted
        private readonly List<(LabelKind Kind, int State)> labels = [];

        /// <summary>The jumps of a state's code, in the order <see cref="EmitState"/> makes them (repeats included).</summary>
        private List<int> Successors(int state)
        {
            var targets = new List<int>();
            int blocks = function.Blocks.Length;
            void Dispatching(CatchClause[]? handlers)
            {
                foreach (var clause in handlers ?? [])
                {
                    targets.Add(clauseLandings[clause.Block]);
                    if (clause.Type is null)
                        return;
                }
                if (handlers is not null && needsCleanup)
                    targets.Add(cleanupLanding);
            }
            if (state == cleanupLanding)
                return targets;
            if (state >= blocks)
            {
                targets.Add(landingTargets[state - blocks]);
                return targets;
            }
            switch (function.Blocks[state].Terminator)
            {
                case GotoTerminator jump: targets.Add(jump.Target); break;
                case IfTerminator branch: targets.Add(branch.Else); targets.Add(branch.Then); break;
                case SwitchTerminator @switch: targets.AddRange(@switch.Targets); targets.Add(@switch.Otherwise); break;
                case CallTerminator call: Dispatching(call.Handlers); targets.Add(call.Target); break;
                case ThrowTerminator @throw: Dispatching(@throw.Handlers); break;
                case ResumeTerminator resume: Dispatching(resume.Handlers); break;
                case DropTerminator drop: targets.Add(drop.Target); break;
            }
            return targets;
        }

        /// <summary>Plans the structured translation: false when the graph is irreducible (the dispatch loop is used).</summary>
        private bool PlanStructured()
        {
            var successors = new List<int>[states];
            for (int s = 0; s < states; s++)
                successors[s] = Successors(s);

            // Reverse postorder (an iterative depth-first search from the entry, state 0).
            order = new int[states];
            Array.Fill(order, -1);
            var postorder = new List<int>(states);
            var visited = new bool[states];
            var stack = new Stack<(int State, int Next)>();
            stack.Push((0, 0));
            visited[0] = true;
            while (stack.Count > 0)
            {
                var (state, next) = stack.Pop();
                if (next < successors[state].Count)
                {
                    stack.Push((state, next + 1));
                    int target = successors[state][next];
                    if (!visited[target])
                    {
                        visited[target] = true;
                        stack.Push((target, 0));
                    }
                }
                else
                {
                    postorder.Add(state);
                }
            }
            int reachable = postorder.Count;
            var byOrder = new int[reachable];
            for (int i = 0; i < reachable; i++)
            {
                int state = postorder[reachable - 1 - i];
                order[state] = i;
                byOrder[i] = state;
            }

            // Immediate dominators (Cooper, Harvey and Kennedy), over the reachable states.
            var predecessors = new List<int>[states];
            for (int s = 0; s < states; s++)
                predecessors[s] = [];
            foreach (int s in byOrder)
            {
                foreach (int t in successors[s])
                    predecessors[t].Add(s);
            }
            var idom = new int[states];
            Array.Fill(idom, -1);
            idom[0] = 0;
            int Intersect(int a, int b)
            {
                while (a != b)
                {
                    while (order[a] > order[b])
                        a = idom[a];
                    while (order[b] > order[a])
                        b = idom[b];
                }
                return a;
            }
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int i = 1; i < reachable; i++)
                {
                    int state = byOrder[i], best = -1;
                    foreach (int p in predecessors[state])
                    {
                        if (idom[p] >= 0)
                            best = best < 0 ? p : Intersect(p, best);
                    }
                    if (best >= 0 && idom[state] != best)
                    {
                        idom[state] = best;
                        changed = true;
                    }
                }
            }
            bool Dominates(int a, int b)
            {
                while (b != a && b != 0)
                    b = idom[b];
                return b == a;
            }

            // Loop headers (targets of backward jumps, which must dominate where they come from) and merge nodes.
            merge = new bool[states];
            loopHeader = new bool[states];
            var forwardIn = new int[states];
            foreach (int s in byOrder)
            {
                foreach (int t in successors[s])
                {
                    if (order[t] <= order[s])
                    {
                        if (!Dominates(t, s))
                            return false; // irreducible: a loop entered elsewhere than at its header
                        loopHeader[t] = true;
                    }
                    else
                    {
                        forwardIn[t]++;
                    }
                }
            }
            mergeChildren = new List<int>[states];
            for (int s = 0; s < states; s++)
                mergeChildren[s] = [];
            foreach (int s in byOrder)
            {
                merge[s] = forwardIn[s] >= 2;
                if (merge[s])
                    mergeChildren[idom[s]].Add(s);
            }
            foreach (var children in mergeChildren)
                children.Sort((a, b) => order[b].CompareTo(order[a])); // the latest first: the outermost block
            structured = true;
            return true;
        }

        private void DoTree(int state)
        {
            if (loopHeader[state])
            {
                c.Loop();
                labels.Add((LabelKind.Loop, state));
                NodeWithin(state, 0);
                labels.RemoveAt(labels.Count - 1);
                c.End();
            }
            else
            {
                NodeWithin(state, 0);
            }
        }

        private void NodeWithin(int state, int child)
        {
            var children = mergeChildren[state];
            if (child == children.Count)
            {
                int outer = node;
                node = state;
                EmitState(state);
                node = outer;
                return;
            }
            c.Block();
            labels.Add((LabelKind.Block, children[child]));
            NodeWithin(state, child + 1);
            labels.RemoveAt(labels.Count - 1);
            c.End();
            DoTree(children[child]);
        }

        /// <summary>A jump in structured code: a `br` to the target's label, or the target's code in its place.</summary>
        private void StructuredJump(int state)
        {
            if (order[state] <= order[node] || merge[state])
            {
                for (int i = labels.Count - 1; i >= 0; i--)
                {
                    if (labels[i].State == state && labels[i].Kind != LabelKind.If)
                    {
                        c.Br(labels.Count - 1 - i);
                        return;
                    }
                }
                throw new IrreducibleException();
            }
            // Only this jump enters the target: its code goes here, and the code around it carries on as it was.
            var (savedCurrent, savedCursor, savedCase, savedDepth) = (current, Cursor, caseIndex, depth);
            DoTree(state);
            (current, Cursor, caseIndex, depth) = (savedCurrent, savedCursor, savedCase, savedDepth);
        }

        // ── Control ────────────────────────────────────────────

        /// <summary>Continues at another state (block or landing): structured, or through the dispatch loop.</summary>
        private void JumpTo(int state)
        {
            if (structured)
            {
                StructuredJump(state);
                return;
            }
            c.I32(state);
            c.LocalSet(State);
            c.Br(states - 1 - caseIndex + depth);
        }

        private void OpenIf()
        {
            c.If();
            depth++;
            labels.Add((LabelKind.If, -1));
        }

        private void CloseIf()
        {
            c.End();
            depth--;
            labels.RemoveAt(labels.Count - 1);
        }

        /// <summary>Returns to the caller: the result to where it asked, the stack pointer back.</summary>
        private void Leave(bool withResult = false)
        {
            if (withResult)
            {
                var (kind, size, _) = cg.Shape(function.ReturnType);
                if (kind != Kind.Unit && registers[0] >= 0)
                {
                    c.LocalGet(Ret);
                    c.LocalGet(registers[0]);
                    c.I64StoreSized(size);
                }
                else if (kind != Kind.Unit)
                {
                    Copy(c, () => c.LocalGet(Ret), () => { c.LocalGet(Fp); c.I32(slots[0]); c.I32Add(); }, size);
                }
            }
            c.LocalGet(Saved);
            c.GlobalSet(Sp);
            c.Return();
        }

        private void Panic(int code, Action? a = null, Action? b = null)
        {
            c.I32(code);
            if (a is null) c.I64(0); else a();
            if (b is null) c.I64(0); else b();
            c.Call(cg.Runtime("larva_panic"));
            c.Unreachable();
        }

        /// <summary>
        /// An exception is in flight (in the globals): the first matching clause of the site, else this frame's cleanup,
        /// else straight back to the caller.
        /// </summary>
        private void Dispatch(CatchClause[]? handlers)
        {
            foreach (var clause in handlers ?? [])
            {
                if (clause.Type is null)
                {
                    JumpTo(clauseLandings[clause.Block]);
                    return;
                }
                c.GlobalGet(ExceptionType);
                c.I32(cg.TypeId(clause.Type));
                c.I32Eq();
                OpenIf();
                JumpTo(clauseLandings[clause.Block]);
                CloseIf();
            }
            if (handlers is not null && needsCleanup)
                JumpTo(cleanupLanding);
            else
                Leave();
        }

        private void FreeCaught()
        {
            c.LocalGet(Fp);
            c.I64Load(caughtPayload);
            c.I32WrapI64();
            c.Call(cg.Runtime("larva_free"));
        }

        private void PlanExceptions()
        {
            handles = function.Blocks.Any(b => b.Terminator is IUnwinding { Handlers.Length: > 0 });
            bool regions = function.Blocks.SelectMany(b => b.Statements).Any(s => s is RegionEnterStatement);
            needsCleanup = flags.Count > 0 || handles || regions || pins.Count > 0;
            if (handles)
            {
                caughtPayload = Slot(8, 8);
                caughtType = Slot(8, 8);
            }
            if (regions)
                regionDepth = Slot(8, 8);
        }

        // ── Statements ─────────────────────────────────────────

        private void Statement(Statement statement)
        {
            switch (statement)
            {
                case AssignStatement assign:
                    Assign(assign.Target, assign.Value);
                    break;
                case StorageLiveStatement live:
                    UpdateFlags([new Place(live.Local)], null);
                    break;
                case StorageDeadStatement dead:
                    ReleaseOwned(new Place(dead.Local), []);
                    UpdateFlags([new Place(dead.Local)], null);
                    break;
                case StoreGlobalStatement store:
                {
                    var type = module.Globals[store.Global].Type;
                    int size = Layouts.Of(type, module)!.Size;
                    int value = Operand(store.Value);
                    // The old value loses its owner once the new one is in place: its destructor sees the global
                    // already changed, not itself still there.
                    int old = int.MinValue;
                    if (Has(GcCapabilities.RefCounting) && type.Unoptional() is GcType)
                    {
                        old = Temp();
                        c.LocalGet(Fp);
                        c.I32(cg.GlobalAddress[store.Global]);
                        c.I64Load();
                        c.I64Store(old);
                    }
                    CopyTemp(() => c.I32(cg.GlobalAddress[store.Global]), value, size);
                    if (old != int.MinValue)
                    {
                        LoadTemp(old);
                        c.I32WrapI64();
                        c.Call(cg.Runtime("larva_gc_release"));
                    }
                    break;
                }
                case FreeStatement free:
                {
                    int pointer = Load(free.Pointer);
                    LoadTemp(pointer);
                    c.I32WrapI64();
                    c.Call(cg.Runtime("larva_free"));
                    break;
                }
                case RegionEnterStatement enter:
                    c.I32(enter.Region);
                    c.Call(cg.Runtime("larva_region_enter"));
                    break;
                case RegionExitStatement exit:
                    c.I32(exit.Region);
                    c.Call(cg.Runtime("larva_region_exit"));
                    break;
                case NopStatement:
                    break;
                default:
                    throw new WasmBackendException($"statement {statement}");
            }
        }

        private void Assign(Place target, Rvalue value)
        {
            var type = function.TypeOf(target, module);
            if (type.IsUnit)
            {
                if (value is IntrinsicRvalue effect)
                    Intrinsic(effect);
                return;
            }
            int result = Rvalue(value, type);
            var moved = moves.MovedBy(MoveAnalysis.Operands(value), current).ToList();
            // What the target owned is released after the new value is stored: a destructor that runs in the
            // release sees the owner's field already changed, not the dying object still in it.
            var saved = Cursor;
            var old = LoadOwned(target, moved);
            Store(target, result);
            ReleaseLoaded(old);
            Cursor = saved;
            UpdateFlags(moved, target);
        }

        /// <summary>
        /// An intrinsic: its arguments on the wasm stack, each adapted to the import's signature, then the runtime's import,
        /// whose result (if any) is left as an i64 (bits for a float) when the intrinsic has one.
        /// </summary>
        /// <remarks>
        /// Every slot holds an i64 (a bool a byte; an address, a function value or a float's bits widened), while the
        /// imports take an i32 for an address of any kind (references, pointers, function values, byte arrays) and may
        /// return one: the adaptation goes by the import's declared types, never by the IR type's shape, so no argument is
        /// passed at the wrong width (an i64 where an i32 is expected made the module invalid).
        /// </remarks>
        private void Intrinsic(IntrinsicRvalue intrinsic)
        {
            var info = Intrinsics.Find(intrinsic.Name)!;
            var signature = WasmCodegen.RuntimeType(info.Symbol);
            var slots = intrinsic.Arguments.Select(Operand).ToArray();
            if (signature.Parameters.Length != slots.Length)
                throw new WasmBackendException($"`{intrinsic.Name}` takes {slots.Length} argument(s); the runtime's `{info.Symbol}` takes {signature.Parameters.Length}");
            for (int i = 0; i < slots.Length; i++)
            {
                if (info.Parameters[i] == IrType.Bool)
                {
                    // A bool is its low byte.
                    LoadTemp(slots[i]);
                    c.I64(0xFF);
                    c.I64And();
                    if (signature.Parameters[i] == ValType.I32)
                        c.I32WrapI64();
                }
                else
                {
                    LoadTemp(slots[i]);
                    switch (signature.Parameters[i])
                    {
                        case ValType.I32: c.I32WrapI64(); break;
                        case ValType.F64: c.Raw(0xBF); break;                      // f64.reinterpret_i64
                        case ValType.F32: c.I32WrapI64(); c.Raw(0xBE); break;     // f32.reinterpret_i32
                    }
                }
            }
            c.Call(cg.Runtime(info.Symbol));
            bool wanted = !info.Result.IsUnit;
            if (signature.Results.Length == 0)
            {
                if (wanted)
                    c.I64(0);
                return;
            }
            if (!wanted)
            {
                c.Drop();
                return;
            }
            switch (signature.Results[0])
            {
                case ValType.I32:
                    if (cg.Shape(info.Result) is (Kind.Scalar, < 8, true))
                        c.I64ExtendI32S();
                    else
                        c.I64ExtendI32U();                                        // an address or an unsigned value
                    break;
                case ValType.F64: c.Raw(0xBD); break;                             // i64.reinterpret_f64
                case ValType.F32: c.Raw(0xBC); c.I64ExtendI32U(); break;          // i32.reinterpret_f32
            }
        }

        private void FrameAddress(int offset)
        {
            if (IsLocal(offset))
                throw new WasmBackendException($"`{function.Name}`: a temp in a wasm local has no address");
            c.LocalGet(Fp);
            if (offset != 0)
            {
                c.I32(offset);
                c.I32Add();
            }
        }

        /// <summary>Stores temp <paramref name="value"/> into a place.</summary>
        private void Store(Place target, int value)
        {
            if (target.Projections.Length == 0 && registers[target.Local] >= 0)
            {
                var (_, registerSize, registerSigned) = cg.Shape(function.Locals[target.Local].Type);
                LoadTemp(value);
                CanonicalizeTop(registerSize, registerSigned);
                c.LocalSet(registers[target.Local]);
                return;
            }
            var type = Address(target);
            var (kind, size, _) = cg.Shape(type);
            if (kind == Kind.Unit)
                return;
            CopyTemp(() => c.LocalGet(Addr), value, size);
        }

        /// <summary>The i64 on the stack made what loading its low <paramref name="size"/> bytes would give.</summary>
        private void CanonicalizeTop(int size, bool signed)
        {
            if (size >= 8)
                return;
            if (signed)
            {
                c.Raw(size switch { 1 => (byte)0xC2, 2 => (byte)0xC3, _ => (byte)0xC4 }); // i64.extend8/16/32_s
            }
            else
            {
                c.I64(size switch { 1 => 0xFF, 2 => 0xFFFF, _ => 0xFFFF_FFFFL });
                c.I64And();
            }
        }

        /// <summary>Copies <paramref name="size"/> bytes of temp <paramref name="value"/> to an address.</summary>
        private void CopyTemp(Action destination, int value, int size)
        {
            if (!IsLocal(value))
            {
                Copy(c, destination, () => FrameAddress(value), size);
                return;
            }
            if (size <= 0)
                return;
            destination();
            c.LocalGet(LocalOf(value));
            c.I64StoreSized(size);
        }

        /// <summary>Copies <paramref name="size"/> bytes (loads and stores for small sizes, <c>memory.copy</c> otherwise).</summary>
        private static void Copy(Code code, Action destination, Action source, int size)
        {
            if (size <= 0)
                return;
            destination();
            source();
            if (size is 1 or 2 or 4 or 8)
            {
                code.I64LoadSized(size, signed: false);
                code.I64StoreSized(size);
            }
            else
            {
                code.I32(size);
                code.MemoryCopy();
            }
        }

        // ── Terminators ────────────────────────────────────────

        private void Terminator(Terminator terminator)
        {
            switch (terminator)
            {
                case GotoTerminator jump:
                    JumpTo(jump.Target);
                    break;
                case IfTerminator branch:
                {
                    int condition = Operand(branch.Condition);
                    LoadTemp(condition);
                    c.I64Eqz();
                    OpenIf();
                    JumpTo(branch.Else);
                    CloseIf();
                    JumpTo(branch.Then);
                    break;
                }
                case SwitchTerminator @switch:
                {
                    int value = Operand(@switch.Value);
                    for (int i = 0; i < @switch.Targets.Length; i++)
                    {
                        LoadTemp(value);
                        c.I64(i);
                        c.I64Op(0x51); // i64.eq
                        OpenIf();
                        JumpTo(@switch.Targets[i]);
                        CloseIf();
                    }
                    JumpTo(@switch.Otherwise);
                    break;
                }
                case ReturnTerminator:
                    for (int l = 1; l < function.Locals.Length; l++)
                        ReleaseOwned(new Place(l), []);
                    if (handles)
                        FreeCaught();
                    foreach (var pin in pins.Values)
                    {
                        c.LocalGet(Fp);
                        c.I64Load(pin);
                        c.I32WrapI64();
                        c.Call(cg.Runtime("larva_unpin"));
                    }
                    Leave(withResult: true);
                    break;
                case CallTerminator call:
                    Call(call);
                    break;
                case ThrowTerminator @throw:
                {
                    var type = function.TypeOf(@throw.Value, module);
                    int value = Operand(@throw.Value);
                    UpdateFlags(moves.MovedBy([@throw.Value], current).ToList(), null);
                    var layout = Layouts.Of(type, module)!;
                    c.I32(layout.Size);
                    c.I32(layout.Align);
                    c.Call(cg.Runtime("larva_alloc"));
                    c.LocalSet(Addr);
                    CopyTemp(() => c.LocalGet(Addr), value, cg.Shape(type).Size);
                    c.LocalGet(Addr);
                    c.GlobalSet(InFlight);
                    c.I32(cg.TypeId(type));
                    c.GlobalSet(ExceptionType);
                    Dispatch(@throw.Handlers);
                    break;
                }
                case ResumeTerminator resume:
                {
                    if (caughtPayload == int.MinValue)
                        throw new WasmBackendException($"`{function.Name}`: resume outside an exception handler");
                    c.LocalGet(Fp);
                    c.I64Load(caughtPayload);
                    c.I32WrapI64();
                    c.GlobalSet(InFlight);
                    c.LocalGet(Fp);
                    c.I64Load(caughtType);
                    c.I32WrapI64();
                    c.GlobalSet(ExceptionType);
                    c.LocalGet(Fp);
                    c.I64(0);
                    c.I64Store(caughtPayload); // ownership goes with the exception
                    Dispatch(resume.Handlers);
                    break;
                }
                case DropTerminator drop:
                    ReleaseOwned(drop.Place, []);
                    UpdateFlags([drop.Place], null);
                    JumpTo(drop.Target);
                    break;
                case UnreachableTerminator:
                    Panic(3);
                    break;
                default:
                    throw new WasmBackendException($"terminator {terminator}");
            }
        }

        private void Call(CallTerminator call)
        {
            IrType[] parameterTypes;
            IrType returnType;
            int callee = -1;
            if (call.Callee is { } indirect)
            {
                var signature = (FunctionType)function.TypeOf(indirect, module);
                parameterTypes = signature.Parameters;
                returnType = signature.Result;
            }
            else
            {
                var target = module.Functions[call.Function];
                if (target.IsGeneric)
                    throw new WasmBackendException($"`{target.Name}` is shared generic code; compile with GenericsMode.Specialize");
                parameterTypes = [.. target.Locals.Skip(1).Take(target.ArgumentCount).Select(l => l.Type)];
                returnType = target.ReturnType;
                callee = cg.FunctionIndex[call.Function];
            }

            int calleeTemp = call.Callee is { } calleeOperand ? Operand(calleeOperand) : -1;
            var values = new List<(int Temp, int Size)>();
            for (int i = 0; i < call.Arguments.Length; i++)
            {
                var (kind, size, _) = cg.Shape(parameterTypes[i]);
                if (kind == Kind.Unit)
                    continue;
                values.Add((Operand(call.Arguments[i]), size));
            }
            int areaSize = values.Sum(v => Align(v.Size, 8));
            int area = Temp(Math.Max(8, areaSize));
            int offset = 0;
            foreach (var (temp, size) in values)
            {
                int at = area + offset;
                CopyTemp(() => FrameAddress(at), temp, size);
                offset += Align(size, 8);
            }
            var moved = moves.MovedBy(call.Arguments, current).ToList();
            UpdateFlags(moved, null);

            var (resultKind, resultSize, resultSigned) = cg.Shape(returnType);
            int result = Temp(Math.Max(8, resultSize));
            FrameAddress(area);
            FrameAddress(result);
            if (callee >= 0)
            {
                c.Call(callee);
            }
            else
            {
                LoadTemp(calleeTemp);
                c.I32WrapI64();
                c.CallIndirect(cg.Uniform);
            }
            c.GlobalGet(InFlight);
            OpenIf();
            Dispatch(call.Handlers);
            CloseIf();

            if (resultKind != Kind.Unit)
            {
                if (resultKind == Kind.Scalar && resultSize < 8)
                {
                    // Results come back as their bytes: make the temp canonical.
                    c.LocalGet(Fp);
                    c.LocalGet(Fp);
                    c.I64LoadSized(resultSize, resultSigned, result);
                    c.I64Store(result);
                }
                ReleaseOwned(call.Destination, moved);
                Store(call.Destination, result);
            }
            UpdateFlags([], resultKind != Kind.Unit ? call.Destination : null);
            JumpTo(call.Target);
        }

        // ── Values ─────────────────────────────────────────────

        /// <summary>Stores the i64 in local <see cref="A"/> into temp <paramref name="temp"/>.</summary>
        private void StoreA(int temp)
        {
            if (IsLocal(temp))
            {
                c.LocalGet(A);
                c.LocalSet(LocalOf(temp));
                return;
            }
            c.LocalGet(Fp);
            c.LocalGet(A);
            c.I64Store(temp);
        }

        private void LoadTemp(int temp)
        {
            if (IsLocal(temp))
            {
                c.LocalGet(LocalOf(temp));
                return;
            }
            c.LocalGet(Fp);
            c.I64Load(temp);
        }

        /// <summary>Before the i64 to store into temp <paramref name="temp"/> is pushed (see <see cref="EndStore"/>).</summary>
        private void BeginStore(int temp)
        {
            if (!IsLocal(temp))
                c.LocalGet(Fp);
        }

        /// <summary>Stores the i64 on the stack into temp <paramref name="temp"/>.</summary>
        private void EndStore(int temp)
        {
            if (IsLocal(temp))
                c.LocalSet(LocalOf(temp));
            else
                c.I64Store(temp);
        }

        /// <summary>Pushes a float temp's value (an f32 or an f64).</summary>
        private void LoadFloat(int temp, bool f32)
        {
            if (!IsLocal(temp))
            {
                c.LocalGet(Fp);
                if (f32) c.F32Load(temp); else c.F64Load(temp);
                return;
            }
            c.LocalGet(LocalOf(temp));
            if (f32)
            {
                c.I32WrapI64();
                c.Raw(0xBE);    // f32.reinterpret_i32
            }
            else
            {
                c.Raw(0xBF);    // f64.reinterpret_i64
            }
        }

        /// <summary>Stores the float on the stack into a temp (see <see cref="BeginStore"/>); a single's bits zero-extended.</summary>
        private void EndStoreFloat(int temp, bool f32)
        {
            if (!IsLocal(temp))
            {
                if (f32) c.F32Store(temp); else c.F64Store(temp);
                return;
            }
            if (f32)
            {
                c.Raw(0xBC);    // i32.reinterpret_f32
                c.I64ExtendI32U();
            }
            else
            {
                c.Raw(0xBD);    // i64.reinterpret_f64
            }
            c.LocalSet(LocalOf(temp));
        }

        private int Operand(Operand operand)
        {
            switch (operand)
            {
                case CopyOperand or MoveOperand:
                {
                    var place = operand is CopyOperand copy ? copy.Place : ((MoveOperand)operand).Place;
                    int value = Load(place);
                    if (Has(GcCapabilities.RefCounting) && function.TypeOf(place, module).Unoptional() is GcType)
                    {
                        LoadTemp(value);
                        c.I32WrapI64();
                        c.Call(cg.Runtime("larva_gc_retain"));
                    }
                    return value;
                }
                case ConstOperand { Type: var constantType } constant when cg.Shape(constantType).Kind == Kind.Wide:
                {
                    int temp = Temp(16);
                    var (low, high) = Halves(constant);
                    c.LocalGet(Fp);
                    c.I64(low);
                    c.I64Store(temp);
                    c.LocalGet(Fp);
                    c.I64(high);
                    c.I64Store(temp + 8);
                    return temp;
                }
                case ConstOperand constant:
                {
                    int temp = TempFor(constant.Type);
                    BeginStore(temp);
                    c.I64(Canonical(constant));
                    EndStore(temp);
                    return temp;
                }
                default:
                    throw new WasmBackendException($"operand {operand}");
            }
        }

        private int Load(Place place)
        {
            if (place.Projections.Length == 0 && registers[place.Local] >= 0)
            {
                int register = ScalarTemp();
                c.LocalGet(registers[place.Local]);
                c.LocalSet(LocalOf(register));
                return register;
            }
            var type = Address(place);
            if (type is ArrayType)
                throw new WasmBackendException($"`{place}` is an unsized array; index it or take Len");
            var (kind, size, signed) = cg.Shape(type);
            if (kind is Kind.Scalar or Kind.Float && Plain(type))
            {
                int scalar = ScalarTemp();
                c.LocalGet(Addr);
                c.I64LoadSized(size, signed);
                c.LocalSet(LocalOf(scalar));
                return scalar;
            }
            int temp = Temp(size);
            if (kind == Kind.Scalar)
            {
                c.LocalGet(Fp);
                c.LocalGet(Addr);
                c.I64LoadSized(size, signed);
                c.I64Store(temp);
            }
            else
            {
                Copy(c, () => FrameAddress(temp), () => c.LocalGet(Addr), size);
            }
            return temp;
        }

        private int Rvalue(Rvalue rvalue, IrType resultType)
        {
            switch (rvalue)
            {
                case UseRvalue use:
                    return Operand(use.Operand);
                case BinaryRvalue binary:
                    return Binary(binary);
                case UnaryRvalue unary:
                    return Unary(unary);
                case CastRvalue cast:
                    return Convert(Operand(cast.Operand), function.TypeOf(cast.Operand, module), cast.Type);
                case RefRvalue reference:
                {
                    PinIfInsideGcObject(reference.Place);
                    Address(reference.Place);
                    int temp = Temp();
                    c.LocalGet(Fp);
                    c.LocalGet(Addr);
                    c.I64ExtendI32U();
                    c.I64Store(temp);
                    return temp;
                }
                case CaughtRvalue:
                {
                    int size = cg.Shape(resultType).Size;
                    int temp = Temp(size);
                    Copy(c, () => FrameAddress(temp), () => { c.LocalGet(Fp); c.I64Load(caughtPayload); c.I32WrapI64(); }, size);
                    return temp;
                }
                case SizeOfRvalue size:
                {
                    int temp = ScalarTemp();
                    c.I64(Layouts.Of(size.Type, module)!.Size);
                    EndStore(temp);
                    return temp;
                }
                case AllocRvalue alloc:
                {
                    var valueType = function.TypeOf(alloc.Value, module);
                    int value = Operand(alloc.Value);
                    var layout = Layouts.Of(valueType, module)!;
                    c.I32(layout.Size);
                    c.I32(layout.Align);
                    c.Call(cg.Runtime(alloc.Kind == AllocKind.Gc ? "larva_gc_new" : "larva_alloc"));
                    c.LocalSet(Addr);
                    CopyTemp(() => c.LocalGet(Addr), value, cg.Shape(valueType).Size);
                    int cell = Temp();
                    c.LocalGet(Fp);
                    c.LocalGet(Addr);
                    c.I64ExtendI32U();
                    c.I64Store(cell);
                    if (alloc.Finalizer is { } finalizer)
                    {
                        int fn = Operand(finalizer);
                        LoadTemp(cell);
                        c.I32WrapI64();
                        LoadTemp(fn);
                        c.I32WrapI64();
                        c.Call(cg.Runtime("larva_gc_finalizer"));
                    }
                    return cell;
                }
                case ArrayRvalue array:
                {
                    int length = Operand(array.Length);
                    // A negative length (or an unsigned one past i64) is an error, never a huge array.
                    LoadTemp(length);
                    c.I64(0);
                    c.I64Op(0x53); // i64.lt_s
                    OpenIf();
                    Panic(4, () => LoadTemp(length));
                    CloseIf();
                    var (header, stride) = cg.ArrayLayout(array.Element);
                    int managed = array.Kind == AllocKind.Gc ? 1 : 0;
                    if (array.Owner is { } owner)
                    {
                        // An object with its array in place: the runtime puts the length before the elements (bit 4).
                        var trailing = Layouts.Trailing(owner, module) ?? throw new WasmBackendException($"`{owner}` does not end with an array");
                        (header, stride, managed) = (trailing.Header, trailing.Stride, array.Kind == AllocKind.Gc ? 1 | 4 : 4);
                    }
                    int handle = Temp();                       // in the frame: the collector sees the new array
                    c.LocalGet(Fp);
                    LoadTemp(length);
                    c.I32(header);
                    c.I32(stride);
                    c.I32(managed);
                    c.Call(cg.Runtime("larva_array_new"));
                    c.I64ExtendI32U();
                    c.I64Store(handle);
                    return handle;
                }
                case FunctionRefRvalue reference:
                {
                    int temp = Temp();
                    c.LocalGet(Fp);
                    c.I64(cg.TableSlot(reference.Function));
                    c.I64Store(temp);
                    return temp;
                }
                case HandleCastRvalue cast:
                    return Operand(cast.Handle);
                case WrapRvalue wrap:
                    return Operand(wrap.Value);
                case UnwrapRvalue unwrap:
                {
                    int value = Operand(unwrap.Value);
                    LoadTemp(value);
                    c.I64Eqz();
                    OpenIf();
                    Panic(5);
                    CloseIf();
                    return value;
                }
                case IsNullRvalue test:
                {
                    int value = Load(test.Value);
                    int temp = ScalarTemp();
                    LoadTemp(value);
                    c.I64Eqz();
                    c.I64ExtendI32U();
                    EndStore(temp);
                    return temp;
                }
                case GlobalRvalue global:
                {
                    var type = module.Globals[global.Global].Type;
                    int size = Layouts.Of(type, module)!.Size;
                    var (kind, _, signed) = cg.Shape(type);
                    int temp = kind is Kind.Scalar or Kind.Float && Plain(type) ? ScalarTemp() : Temp(size);
                    if (kind == Kind.Scalar || IsLocal(temp))
                    {
                        BeginStore(temp);
                        c.I32(cg.GlobalAddress[global.Global]);
                        c.I64LoadSized(size, signed);
                        EndStore(temp);
                    }
                    else
                    {
                        Copy(c, () => FrameAddress(temp), () => c.I32(cg.GlobalAddress[global.Global]), size);
                    }
                    if (Has(GcCapabilities.RefCounting) && type.Unoptional() is GcType)
                    {
                        LoadTemp(temp);
                        c.I32WrapI64();
                        c.Call(cg.Runtime("larva_gc_retain")); // the copy is a new owner
                    }
                    return temp;
                }
                case ZeroRvalue zero:
                {
                    int size = Layouts.Of(zero.Type, module)!.Size;
                    int temp = Temp(size);
                    FrameAddress(temp);
                    c.I32(0);
                    c.I32(Math.Max(8, Align(size, 8)));
                    c.MemoryFill();
                    return temp;
                }
                case StaticRvalue data:
                {
                    int temp = Temp();
                    c.LocalGet(Fp);
                    c.I64(cg.StaticAddress[data.Data]);
                    c.I64Store(temp);
                    return temp;
                }
                case LenRvalue len:
                {
                    Address(len.Array);
                    int temp = ScalarTemp();
                    c.LocalGet(Addr);
                    c.I64Load();
                    EndStore(temp);
                    return temp;
                }
                case IntrinsicRvalue intrinsic:
                {
                    int temp = TempFor(Intrinsics.Find(intrinsic.Name)!.Result);
                    BeginStore(temp);
                    Intrinsic(intrinsic);
                    EndStore(temp);
                    return temp;
                }
                default:
                    throw new WasmBackendException($"rvalue {rvalue}");
            }
        }

        private int Unary(UnaryRvalue unary)
        {
            int value = Operand(unary.Operand);
            var type = function.TypeOf(unary.Operand, module);
            var (kind, size, _) = cg.Shape(type);
            if (kind == Kind.Wide)
            {
                // ~x on both halves; -x = ~x + 1 with the carry into the high half.
                LoadTemp(value);
                c.I64(-1);
                c.I64Xor();
                c.LocalSet(A);
                LoadTemp(value + 8);
                c.I64(-1);
                c.I64Xor();
                c.LocalSet(B);
                if (unary.Op == UnaryOp.Neg)
                {
                    c.LocalGet(A);
                    c.I64(1);
                    c.I64Add();
                    c.LocalTee(A);
                    c.I64Eqz();             // the low half wrapped to 0: carry
                    c.I64ExtendI32U();
                    c.LocalGet(B);
                    c.I64Add();
                    c.LocalSet(B);
                }
                StoreA(value);
                c.LocalGet(Fp);
                c.LocalGet(B);
                c.I64Store(value + 8);
                return value;
            }
            if (type.IsFloat)
            {
                // Negation flips the sign bit.
                LoadTemp(value);
                c.I64(size == 4 ? 0x8000_0000L : long.MinValue);
                c.I64Xor();
                c.LocalSet(A);
                StoreA(value);
                return value;
            }
            LoadTemp(value);
            if (unary.Op == UnaryOp.Neg)
            {
                c.LocalSet(A);
                c.I64(0);
                c.LocalGet(A);
                c.I64Sub();
            }
            else
            {
                c.I64(type == IrType.Bool ? 1 : -1);
                c.I64Xor();
            }
            c.LocalSet(A);
            StoreA(value);
            return value;
        }

        private int Binary(BinaryRvalue binary)
        {
            var type = function.TypeOf(binary.Left, module);
            if (Has(GcCapabilities.RefCounting) && type.Unoptional() is GcType)
                return HandleComparison(binary);
            int left = Operand(binary.Left);
            int right = Operand(binary.Right);
            var (kind, size, signed) = cg.Shape(type);
            if (type.IsFloat)
                return FloatBinary(binary.Op, left, right, size);
            if (kind == Kind.Wide)
                return WideBinary(binary.Op, left, right, signed, type);

            switch (binary.Op)
            {
                case BinaryOp.Div or BinaryOp.Rem:
                {
                    LoadTemp(right);
                    c.I64Eqz();
                    OpenIf();
                    Panic(2);
                    CloseIf();
                    if (signed)
                    {
                        // x / -1 = -x and x % -1 = 0 (Wasm traps on MIN / -1).
                        LoadTemp(right);
                        c.I64(-1);
                        c.I64Op(0x51);
                        OpenIf();
                        if (binary.Op == BinaryOp.Div)
                        {
                            c.I64(0);
                            LoadTemp(left);
                            c.I64Sub();
                        }
                        else
                        {
                            c.I64(0);
                        }
                        c.LocalSet(A);
                        c.Else();
                        LoadTemp(left);
                        LoadTemp(right);
                        c.I64Op(binary.Op == BinaryOp.Div ? (byte)0x7F : (byte)0x81);
                        c.LocalSet(A);
                        CloseIf();
                    }
                    else
                    {
                        LoadTemp(left);
                        LoadTemp(right);
                        c.I64Op(binary.Op == BinaryOp.Div ? (byte)0x80 : (byte)0x82);
                        c.LocalSet(A);
                    }
                    StoreA(left);
                    return left;
                }
                case BinaryOp.Shl or BinaryOp.Shr:
                    LoadTemp(left);
                    LoadTemp(right);
                    c.I64(type.BitWidth!.Value - 1);
                    c.I64And();
                    if (binary.Op == BinaryOp.Shl)
                        c.I64Shl();
                    else if (signed)
                        c.I64ShrS();
                    else
                        c.I64ShrU();
                    c.LocalSet(A);
                    StoreA(left);
                    return left;
            }
            LoadTemp(left);
            LoadTemp(right);
            switch (binary.Op)
            {
                case BinaryOp.Add: c.I64Add(); break;
                case BinaryOp.Sub: c.I64Sub(); break;
                case BinaryOp.Mul: c.I64Mul(); break;
                case BinaryOp.And: c.I64And(); break;
                case BinaryOp.Or: c.I64Or(); break;
                case BinaryOp.Xor: c.I64Xor(); break;
                default:
                    c.I64Op(binary.Op switch
                    {
                        BinaryOp.Eq => 0x51,
                        BinaryOp.Ne => 0x52,
                        BinaryOp.Lt => signed ? (byte)0x53 : (byte)0x54,
                        BinaryOp.Gt => signed ? (byte)0x55 : (byte)0x56,
                        BinaryOp.Le => signed ? (byte)0x57 : (byte)0x58,
                        BinaryOp.Ge => signed ? (byte)0x59 : (byte)0x5A,
                        _ => throw new WasmBackendException($"`{binary.Op}`"),
                    });
                    c.I64ExtendI32U();
                    break;
            }
            c.LocalSet(A);
            StoreA(left);
            return left;
        }

        /// <summary>
        /// Reference counting: <c>==</c> / <c>!=</c> on handles compare addresses and keep no reference, so the operands are
        /// read without the retain an <see cref="Operand"/> read takes (which nothing would release).
        /// </summary>
        private int HandleComparison(BinaryRvalue binary)
        {
            int Read(Operand operand) => operand switch
            {
                CopyOperand copy => Load(copy.Place),
                MoveOperand move => Load(move.Place),
                _ => Operand(operand),
            };
            int left = Read(binary.Left);
            int right = Read(binary.Right);
            LoadTemp(left);
            LoadTemp(right);
            c.I64Op(binary.Op switch
            {
                BinaryOp.Eq => 0x51,
                BinaryOp.Ne => 0x52,
                _ => throw new WasmBackendException($"`{binary.Op}` on handles"),
            });
            c.I64ExtendI32U();
            c.LocalSet(A);
            StoreA(left);
            return left;
        }

        private int WideBinary(BinaryOp op, int left, int right, bool signed, IrType type)
        {
            void Halves(int temp) { LoadTemp(temp); }
            switch (op)
            {
                case BinaryOp.Add or BinaryOp.Sub:
                {
                    // low = l ± r; carry/borrow from the unsigned compare; high = lh ± rh ± carry.
                    Halves(left);
                    Halves(right);
                    c.I64Op(op == BinaryOp.Add ? (byte)0x7C : (byte)0x7D);
                    c.LocalSet(A);
                    LoadTemp(left + 8);
                    LoadTemp(right + 8);
                    c.I64Op(op == BinaryOp.Add ? (byte)0x7C : (byte)0x7D);
                    if (op == BinaryOp.Add)
                    {
                        c.LocalGet(A);
                        LoadTemp(left);
                        c.I64Op(0x54); // carry: low < l (unsigned)
                    }
                    else
                    {
                        LoadTemp(left);
                        LoadTemp(right);
                        c.I64Op(0x54); // borrow: l < r (unsigned)
                    }
                    c.I64ExtendI32U();
                    c.I64Op(op == BinaryOp.Add ? (byte)0x7C : (byte)0x7D);
                    c.LocalSet(B);
                    break;
                }
                case BinaryOp.And or BinaryOp.Or or BinaryOp.Xor:
                {
                    byte code = op == BinaryOp.And ? (byte)0x83 : op == BinaryOp.Or ? (byte)0x84 : (byte)0x85;
                    LoadTemp(left);
                    LoadTemp(right);
                    c.I64Op(code);
                    c.LocalSet(A);
                    LoadTemp(left + 8);
                    LoadTemp(right + 8);
                    c.I64Op(code);
                    c.LocalSet(B);
                    break;
                }
                case BinaryOp.Mul:
                {
                    // The low 128 bits (the same signed or not): low = l·r, high = mulhi(l, r) + l·rh + lh·r.
                    LoadTemp(left);
                    LoadTemp(right);
                    c.Call(cg.Runtime("larva_mulhi_u64"));
                    LoadTemp(left);
                    LoadTemp(right + 8);
                    c.I64Mul();
                    c.I64Op(0x7C);
                    LoadTemp(left + 8);
                    LoadTemp(right);
                    c.I64Mul();
                    c.I64Op(0x7C);
                    c.LocalSet(B);
                    LoadTemp(left);
                    LoadTemp(right);
                    c.I64Mul();
                    c.LocalSet(A);
                    break;
                }
                case BinaryOp.Eq or BinaryOp.Ne:
                    LoadTemp(left);
                    LoadTemp(right);
                    c.I64Xor();
                    LoadTemp(left + 8);
                    LoadTemp(right + 8);
                    c.I64Xor();
                    c.I64Or();
                    c.I64Eqz();
                    if (op == BinaryOp.Ne)
                        c.I32Eqz();
                    c.I64ExtendI32U();
                    c.LocalSet(A);
                    StoreA(left);
                    return left;
                case BinaryOp.Lt or BinaryOp.Ge or BinaryOp.Gt or BinaryOp.Le:
                {
                    // a < b: high halves decide (signed or not), equal highs fall to the low halves (unsigned).
                    bool swap = op is BinaryOp.Gt or BinaryOp.Le;
                    int a = swap ? right : left, b = swap ? left : right;
                    LoadTemp(a + 8);
                    LoadTemp(b + 8);
                    c.I64Op(signed ? (byte)0x53 : (byte)0x54);
                    LoadTemp(a + 8);
                    LoadTemp(b + 8);
                    c.I64Op(0x51);
                    LoadTemp(a);
                    LoadTemp(b);
                    c.I64Op(0x54);
                    c.I32And();
                    c.I32Or();
                    if (op is BinaryOp.Ge or BinaryOp.Le)
                        c.I32Eqz();
                    c.I64ExtendI32U();
                    c.LocalSet(A);
                    StoreA(left);
                    return left;
                }
                default:
                    throw new WasmBackendException($"`{op}` on `{type}` is not supported by the WebAssembly back end (use the LLVM back end)");
            }
            StoreA(left);
            c.LocalGet(Fp);
            c.LocalGet(B);
            c.I64Store(left + 8);
            return left;
        }

        private int FloatBinary(BinaryOp op, int left, int right, int size)
        {
            bool f32 = size == 4;
            void LoadF(int temp) => LoadFloat(temp, f32);
            switch (op)
            {
                case BinaryOp.Add or BinaryOp.Sub or BinaryOp.Mul or BinaryOp.Div:
                    BeginStore(left);
                    LoadF(left);
                    LoadF(right);
                    int index = op switch { BinaryOp.Add => 0, BinaryOp.Sub => 1, BinaryOp.Mul => 2, _ => 3 };
                    c.Raw((byte)((f32 ? 0x92 : 0xA0) + index));
                    EndStoreFloat(left, f32);
                    return left;
                case BinaryOp.Rem:
                    // fmod (the remainder of the truncating division, with the dividend's sign): the runtime's
                    // `larva_f64_rem` (JavaScript's `%` on doubles, exact), on the bits of doubles; a single is promoted
                    // and demoted around it, which is exact (the remainder of two singles is a single).
                    BeginStore(left);
                    LoadF(left);
                    if (f32) c.Raw(0xBB);     // f64.promote_f32
                    c.Raw(0xBD);              // i64.reinterpret_f64
                    LoadF(right);
                    if (f32) c.Raw(0xBB);
                    c.Raw(0xBD);
                    c.Call(cg.Runtime("larva_f64_rem"));
                    c.Raw(0xBF);              // f64.reinterpret_i64
                    if (f32) c.Raw(0xB6);     // f32.demote_f64
                    EndStoreFloat(left, f32);
                    return left;
                case BinaryOp.Eq or BinaryOp.Ne or BinaryOp.Lt or BinaryOp.Gt or BinaryOp.Le or BinaryOp.Ge:
                    LoadF(left);
                    LoadF(right);
                    int compare = op switch { BinaryOp.Eq => 0, BinaryOp.Ne => 1, BinaryOp.Lt => 2, BinaryOp.Gt => 3, BinaryOp.Le => 4, _ => 5 };
                    c.Raw((byte)((f32 ? 0x5B : 0x61) + compare));
                    c.I64ExtendI32U();
                    c.LocalSet(A);
                    StoreA(left);
                    return left;
                default:
                    throw new WasmBackendException($"`{op}` on floats is not supported by the WebAssembly back end");
            }
        }

        /// <summary>Numeric conversion with Larva semantics (sign follows the source type).</summary>
        private int Convert(int value, IrType from, IrType to)
        {
            if (from == to)
                return value;
            var (fromKind, fromSize, fromSigned) = cg.Shape(from);
            var (toKind, toSize, _) = cg.Shape(to);
            if (fromKind == Kind.Wide || toKind == Kind.Wide)
                return WideConvert(value, from, to, fromKind, fromSigned, toKind);
            if (to == IrType.Bool)
            {
                if (fromKind == Kind.Float)
                {
                    // x != 0.0 (true for NaN too)
                    LoadFloat(value, fromSize == 4);
                    if (fromSize == 4) { c.Raw(0x43); c.Raw(0); c.Raw(0); c.Raw(0); c.Raw(0); c.Raw(0x5C); }
                    else { c.Raw(0x44); for (int i = 0; i < 8; i++) c.Raw(0); c.Raw(0x62); }
                }
                else
                {
                    LoadTemp(value);
                    c.I64(0);
                    c.I64Op(0x52);
                }
                c.I64ExtendI32U();
                c.LocalSet(A);
                StoreA(value);
                return value;
            }
            if (fromKind == Kind.Float && toKind == Kind.Float)
            {
                BeginStore(value);
                LoadFloat(value, fromSize == 4);
                c.Raw(fromSize == 4 ? (byte)0xBB : (byte)0xB6); // f64.promote_f32, f32.demote_f64
                EndStoreFloat(value, fromSize != 4);
                return value;
            }
            if (fromKind == Kind.Float)
            {
                LoadFloat(value, fromSize == 4);
                // Larva's rule for every back end: a value out of the target's range gives the nearest limit and NaN
                // gives 0 (LLVM's fptosi.sat): i64.trunc_sat_f*_s/u saturates to 64 bits (0xFC 4..7), and a narrower
                // target is clamped to its own range before the canonical wrap (which alone would turn i32 max into -1).
                c.Prefixed(to == IrType.U64 ? (fromSize == 4 ? 5u : 7u) : (fromSize == 4 ? 4u : 6u));
                c.LocalSet(A);
                if (toSize < 8)
                {
                    long max = to.IsSigned ? (1L << (8 * toSize - 1)) - 1 : (1L << (8 * toSize)) - 1;
                    long min = to.IsSigned ? -max - 1 : 0;
                    ClampA(min, max);
                }
                Canonicalize(to);
                StoreA(value);
                return value;
            }
            if (toKind == Kind.Float)
            {
                BeginStore(value);
                LoadTemp(value);
                bool unsigned = from == IrType.U64;
                c.Raw(toSize == 4 ? (unsigned ? (byte)0xB5 : (byte)0xB4) : (unsigned ? (byte)0xBA : (byte)0xB9));
                EndStoreFloat(value, toSize == 4);
                return value;
            }
            LoadTemp(value);
            c.LocalSet(A);
            Canonicalize(to);
            StoreA(value);
            return value;
        }

        /// <summary>Makes local <see cref="A"/> the canonical form of <paramref name="type"/> (truncated, then extended).</summary>
        /// <summary>Clamps the signed i64 in <c>A</c> into [<paramref name="min"/>, <paramref name="max"/>] (with <c>select</c>).</summary>
        private void ClampA(long min, long max)
        {
            c.LocalGet(A);
            c.I64(max);
            c.LocalGet(A);
            c.I64(max);
            c.I64Op(0x53); // i64.lt_s
            c.Raw(0x1B);   // select: A when A < max, else max
            c.LocalSet(A);
            c.LocalGet(A);
            c.I64(min);
            c.LocalGet(A);
            c.I64(min);
            c.I64Op(0x55); // i64.gt_s
            c.Raw(0x1B);   // select: A when A > min, else min
            c.LocalSet(A);
        }

        private void Canonicalize(IrType type)
        {
            var (kind, size, signed) = cg.Shape(type);
            if (kind != Kind.Scalar || size >= 8)
                return;
            c.LocalGet(A);
            if (signed)
            {
                c.Raw(size switch { 1 => (byte)0xC2, 2 => (byte)0xC3, _ => (byte)0xC4 });
            }
            else
            {
                c.I64(size switch { 1 => 0xFF, 2 => 0xFFFF, _ => 0xFFFF_FFFFL });
                c.I64And();
            }
            c.LocalSet(A);
        }

        private int WideConvert(int value, IrType from, IrType to, Kind fromKind, bool fromSigned, Kind toKind)
        {
            if (fromKind == Kind.Float || toKind == Kind.Float)
                throw new WasmBackendException($"`{from}` → `{to}` is not supported by the WebAssembly back end (use the LLVM back end)");
            if (fromKind == Kind.Wide && toKind == Kind.Wide)
                return value;
            if (fromKind == Kind.Wide)
            {
                LoadTemp(value);
                if (to == IrType.Bool)
                {
                    LoadTemp(value + 8);
                    c.I64Or();
                    c.I64(0);
                    c.I64Op(0x52);
                    c.I64ExtendI32U();
                    c.LocalSet(A);
                }
                else
                {
                    c.LocalSet(A);
                    Canonicalize(to);
                }
                StoreA(value);
                return value;
            }
            int wide = Temp(16);
            LoadTemp(value);
            c.LocalSet(A);
            StoreA(wide);
            c.LocalGet(Fp);
            if (fromSigned)
            {
                c.LocalGet(A);
                c.I64(63);
                c.I64ShrS();
            }
            else
            {
                c.I64(0);
            }
            c.I64Store(wide + 8);
            return wide;
        }

        // ── Places ─────────────────────────────────────────────

        /// <summary>The address of a place into local <see cref="Addr"/> (bounds-checked indices); returns its type.</summary>
        private IrType Address(Place place)
        {
            if (registers[place.Local] >= 0)
                throw new WasmBackendException($"`{function.Name}`: `{place}` is in a wasm local and has no address");
            FrameAddress(slots[place.Local]);
            c.LocalSet(Addr);
            IrType type = function.Locals[place.Local].Type;
            for (int p = 0; p < place.Projections.Length; p++)
            {
                switch (place.Projections[p])
                {
                    case DerefProjection:
                        c.LocalGet(Addr);
                        c.I64Load();
                        c.I32WrapI64();
                        c.LocalSet(Addr);
                        if (type is OptionalType)
                        {
                            c.LocalGet(Addr);
                            c.I32Eqz();
                            OpenIf();
                            Panic(5);
                            CloseIf();
                        }
                        type = type.Pointee()!;
                        break;
                    case FieldProjection field:
                    {
                        var st = (StructType)type;
                        int offset = Layouts.FieldOffsets(st, module)![field.FieldIndex];
                        if (offset != 0)
                        {
                            c.LocalGet(Addr);
                            c.I32(offset);
                            c.I32Add();
                            c.LocalSet(Addr);
                        }
                        type = module.Structs[st.Id].Fields[field.FieldIndex].Substitute(st.Arguments);
                        break;
                    }
                    case IndexProjection inlineIndex when type is FixedArrayType inline:
                    {
                        var elementLayout = Layouts.Of(inline.Element, module)!;
                        int stride = (elementLayout.Size + elementLayout.Align - 1) / elementLayout.Align * elementLayout.Align;
                        int length = inline.Length(module)!.Value;
                        var (_, indexSize, indexSigned) = cg.Shape(function.Locals[inlineIndex.Local].Type);
                        cg.BoundsChecks++;
                        LoadIndex(inlineIndex.Local, indexSize, indexSigned);
                        c.I64(length);
                        c.I64Op(0x5A); // i64.ge_u
                        OpenIf();
                        Panic(1, () => LoadIndex(inlineIndex.Local, indexSize, indexSigned), () => c.I64(length));
                        CloseIf();
                        c.LocalGet(Addr);
                        LoadIndex(inlineIndex.Local, indexSize, indexSigned);
                        c.I32WrapI64();
                        c.I32(stride);
                        c.I32Mul();
                        c.I32Add();
                        c.LocalSet(Addr);
                        type = inline.Element;
                        break;
                    }
                    case IndexProjection index:
                    {
                        var element = ((ArrayType)type).Element;
                        var (header, stride) = cg.ArrayLayout(element);
                        var (indexKind, indexSize, indexSigned) = cg.Shape(function.Locals[index.Local].Type);
                        if (indexKind != Kind.Scalar)
                            throw new WasmBackendException("array indices must be integers");
                        if ((p == 1 || Larva.IR.Analysis.BoundsAnalysis.InPlace(function, module, place, p)) && bounds?.IsSafe(current, place.Local, index.Local) == true)
                        {
                            cg.BoundsChecksEliminated++;
                        }
                        else
                        {
                            cg.BoundsChecks++;
                            LoadIndex(index.Local, indexSize, indexSigned);
                            c.LocalGet(Addr);
                            c.I64Load();
                            c.I64Op(0x5A); // i64.ge_u
                            OpenIf();
                            Panic(1, () => LoadIndex(index.Local, indexSize, indexSigned), () => { c.LocalGet(Addr); c.I64Load(); });
                            CloseIf();
                        }
                        c.LocalGet(Addr);
                        LoadIndex(index.Local, indexSize, indexSigned);
                        c.I32WrapI64();
                        if (stride != 1)
                        {
                            c.I32(stride);
                            c.I32Mul();
                        }
                        c.I32Add();
                        c.I32(header);
                        c.I32Add();
                        c.LocalSet(Addr);
                        type = element;
                        break;
                    }
                }
            }
            return type;
        }

        private void LoadIndex(int local, int size, bool signed)
        {
            if (registers[local] >= 0)
            {
                c.LocalGet(registers[local]);
                return;
            }
            c.LocalGet(Fp);
            c.I64LoadSized(size, signed, slots[local]);
        }

        // ── Ownership ──────────────────────────────────────────

        private enum Release : byte { FreeBox, ReleaseHandle }

        private IEnumerable<(Projection[] Path, Release Release, IrType Type)> Owned(IrType type, HashSet<StructType> visiting)
        {
            switch (type)
            {
                case BoxType or OptionalType { Inner: BoxType }:
                    yield return ([], Release.FreeBox, type);
                    break;
                case GcType or OptionalType { Inner: GcType } when Has(GcCapabilities.RefCounting):
                    yield return ([], Release.ReleaseHandle, type);
                    break;
                case StructType st when visiting.Add(st):
                    var fields = module.Structs[st.Id].Fields;
                    for (int i = 0; i < fields.Length; i++)
                    {
                        foreach (var (path, release, inner) in Owned(fields[i].Substitute(st.Arguments), visiting))
                            yield return ([Projection.Field(i), .. path], release, inner);
                    }
                    visiting.Remove(st);
                    break;
            }
        }

        private void ReleaseOwned(Place place, IReadOnlyList<Place> movedNow)
        {
            var saved = Cursor;
            ReleaseLoaded(LoadOwned(place, movedNow));
            Cursor = saved;
        }

        /// <summary>A resource of a place read into a temp before the place is overwritten, to be released after (see <see cref="ReleaseLoaded"/>).</summary>
        private readonly record struct OwnedValue(Release Release, int Flag, int Handle);

        /// <summary>Reads the resources the value at <paramref name="place"/> owns into temps (its drop flags decide later whether they are released).</summary>
        private List<OwnedValue> LoadOwned(Place place, IReadOnlyList<Place> movedNow)
        {
            var owned = new List<OwnedValue>();
            foreach (var (path, release, _) in Owned(function.TypeOf(place, module), []))
            {
                var resource = new Place(place.Local, [.. place.Projections, .. path]);
                if (movedNow.Any(m => m.IsPrefixOf(resource) || resource.IsPrefixOf(m)))
                    continue;
                int flag = flags.TryGetValue(resource, out int f) ? f : int.MinValue;
                owned.Add(new OwnedValue(release, flag, Load(resource)));
            }
            return owned;
        }

        /// <summary>Releases what <see cref="LoadOwned"/> read, each guarded by its drop flag when it has one (and the flag cleared).</summary>
        private void ReleaseLoaded(List<OwnedValue> owned)
        {
            foreach (var (release, flag, handle) in owned)
            {
                bool guarded = flag != int.MinValue;
                if (guarded)
                {
                    c.LocalGet(Fp);
                    c.I32Load8U(flag);
                    OpenIf();
                }
                LoadTemp(handle);
                c.I32WrapI64();
                c.Call(cg.Runtime(release == Release.FreeBox ? "larva_free" : "larva_gc_release"));
                if (guarded)
                {
                    c.LocalGet(Fp);
                    c.I32(0);
                    c.I32Store8(flag);
                    CloseIf();
                }
            }
        }

        private void UpdateFlags(IEnumerable<Place> cleared, Place? initialized)
        {
            foreach (var place in cleared)
            {
                foreach (var (resource, flag) in flags)
                {
                    if (place.IsPrefixOf(resource) || resource.IsPrefixOf(place))
                    {
                        c.LocalGet(Fp);
                        c.I32(0);
                        c.I32Store8(flag);
                    }
                }
            }
            if (initialized is not null)
            {
                foreach (var (resource, flag) in flags)
                {
                    if (initialized.IsPrefixOf(resource))
                    {
                        c.LocalGet(Fp);
                        c.I32(1);
                        c.I32Store8(flag);
                    }
                }
            }
        }

        private void PlanPins()
        {
            if (!Has(GcCapabilities.Moving))
                return;
            for (int b = 0; b < function.Blocks.Length; b++)
            {
                var statements = function.Blocks[b].Statements;
                for (int s = 0; s < statements.Length; s++)
                {
                    if (statements[s] is AssignStatement { Value: RefRvalue reference } && PinnedHandle(reference.Place) is not null)
                        pins[new Location(b, s)] = Slot(8, 8);
                }
            }
        }

        private Place? PinnedHandle(Place place)
        {
            for (int j = 0; j < place.Projections.Length; j++)
            {
                var handle = new Place(place.Local, place.Projections[..j]);
                if (place.Projections[j] is DerefProjection && function.TypeOf(handle, module).Unoptional() is GcType)
                    return handle;
            }
            return null;
        }

        private void PinIfInsideGcObject(Place place)
        {
            if (!pins.TryGetValue(current, out int slot) || PinnedHandle(place) is not { } handle)
                return;
            int value = Load(handle);
            // One pin per site per activation: in a loop, the previous pass's pin is given back first (the slot is zero
            // before the first), else the runtime's pin stack grows with every pass until the return.
            c.LocalGet(Fp);
            c.I64Load(slot);
            c.I32WrapI64();
            c.Call(cg.Runtime("larva_unpin"));
            c.LocalGet(Fp);
            LoadTemp(value);
            c.I64Store(slot);
            LoadTemp(value);
            c.I32WrapI64();
            c.Call(cg.Runtime("larva_pin"));
        }
    }
}

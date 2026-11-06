using System.Numerics;
using Larva.IL;
using Larva.IL.Optimization;
using Larva.IR.Analysis;

namespace Larva.IR.Lowering
{
    /// <summary>
    /// Lowers the CFG IR to stack IL. Blocks are laid out in index order, so a jump to the next block
    /// becomes a fall-through. <c>StorageLive</c> and copy-vs-move only matter to the borrow checker.
    /// <para>
    /// Expects a module already processed by <see cref="Monomorphizer"/>. Functions that are still generic are
    /// <b>shared</b>: they receive one witness per type parameter (pushed by the caller before the arguments with
    /// <c>gen.wit</c> / <c>gen.ldtarg</c>), and their type tokens may be open (<c>T0</c>…), resolved against those witnesses.
    /// </para>
    /// <para>
    /// Ownership: values that own resources are released when their owner goes away (overwritten, <c>StorageDead</c>,
    /// <c>drop</c>, or still initialized at <c>return</c>). <c>box T</c> is freed in every GC mode; <c>gc T</c> is released
    /// only under reference counting; a shared function's <c>T</c> is dropped through its witness. Two move analyses decide
    /// each release: initialized on every path → release; uninitialized on every path → skip; otherwise the release is
    /// guarded by a <b>drop flag</b> (a hidden bool local set on initialization and cleared on move).
    /// </para>
    /// <para>
    /// GC cooperation, emitted only when the mode's <see cref="GcCapabilities"/> ask for it:
    /// <c>Tracing</c> → <c>sys.poll</c> at loop headers and a precise root map for every safepoint (polls and calls);
    /// <c>Moving</c> → <c>gc.pin</c> when borrowing inside a <c>gc</c> object, <c>gc.unpin</c> at return;
    /// <c>Barriers</c> → <c>gc.wb</c> for stores of handle-carrying values behind a pointer;
    /// <c>RefCounting</c> → <c>gc.retain</c> when a handle gains an owner and <c>gc.release</c> when an owner dies, except
    /// at a handle's last use, where the reference is transferred instead; <c>Regions</c> → <c>gc.renter</c> / <c>gc.rexit</c>.
    /// </para>
    /// </summary>
    public static class IlLowering
    {
        /// <param name="optimize">Run IL peephole clean-ups (e.g. drop <c>stloc X; ldloc X</c> on single-use temporaries).</param>
        /// <param name="gc">GC mode to generate code for; <see cref="GcConfig.Default"/> when omitted.</param>
        /// <param name="source">The module before monomorphization (generic definitions for reflection); defaults to <paramref name="module"/>.</param>
        /// <param name="reflection">Reflection level of structs without <c>#[reflect]</c>.</param>
        public static IlModule Lower(IrModule module, bool optimize = true, GcConfig? gc = null, IrModule? source = null,
            ReflectionLevel reflection = ReflectionLevel.Layout)
        {
            TokenTable tokens = new();
            IlFunction[] functions;
            using (PhaseTimer.Start("functions"))
            {
                var slowest = new List<(double Ms, string Name, int Blocks, int Locals)>();
                functions = module.Functions.Select(f =>
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var lowered = new FunctionLowering(module, f, tokens, module.CodeGcOf(f.Domain, gc ?? GcConfig.Default)).Lower();
                    if (PhaseTimer.Enabled)
                        slowest.Add((watch.Elapsed.TotalMilliseconds, f.Name, f.Blocks.Length, f.Locals.Length));
                    return lowered;
                }).ToArray();
                if (PhaseTimer.Enabled)
                    Console.Error.WriteLine($"[timing]       liveness {FunctionLowering.Spent[0]:N0} ms, moves {FunctionLowering.Spent[1]:N0} ms, dataflow {FunctionLowering.Spent[2]:N0} ms, emit {FunctionLowering.Spent[3]:N0} ms");
                foreach (var (ms, name, blocks, locals) in slowest.OrderByDescending(x => x.Ms).Take(8))
                    Console.Error.WriteLine($"[timing]       {ms:N0} ms  {name} ({blocks} blocks, {locals} locals)");
            }
            Reflection.ModuleMetadata metadata;
            IlFunction[] stubs;
            using (PhaseTimer.Start("metadata"))
                (metadata, stubs) = new Reflection.MetadataBuilder(source ?? module, module, functions.Length, tokens, reflection).Build();
            functions = [.. functions, .. stubs];
            if (optimize)
            {
                using (PhaseTimer.Start("peephole"))
                    functions = [.. functions.Select(Optimize)];
            }
            return new IlModule(tokens.Types, tokens.Fields, tokens.Constants, tokens.Witnesses, functions)
            {
                Metadata = metadata,
                Gc = gc ?? GcConfig.Default,
                Domains = module.Domains,
                Data = module.Data,
                Globals = module.Globals,
            };
        }

        private static IlFunction Optimize(IlFunction function)
        {
            var body = Peephole.RemoveStoreLoadPairs(function.Body, out var newIndex, function.Handlers.Select(h => (long)h.Handler));
            return function with
            {
                Body = body,
                Safepoints = [.. function.Safepoints.Select(s => s with { Operation = (int)newIndex[s.Operation] })],
                Handlers = [.. function.Handlers.Select(h => h with
                {
                    Start = (int)newIndex[h.Start],
                    End = (int)newIndex[h.End],
                    Handler = (int)newIndex[h.Handler],
                })],
            };
        }

        private enum Release : byte
        {
            /// <summary><c>gc.free</c> of a box.</summary>
            FreeBox,
            /// <summary><c>gc.release</c> of a handle (reference counting).</summary>
            ReleaseHandle,
            /// <summary><c>gen.drop</c> of a value whose type is only known through a witness.</summary>
            WitnessDrop,
        }

        /// <summary>Move states before the current point: may be uninitialized (union) and must be uninitialized (intersection).</summary>
        private readonly record struct States(MoveState May, MoveState Must);

        private sealed class FunctionLowering
        {
            private readonly IrModule module;
            private readonly IrFunction function;
            private readonly TokenTable tokens;
            private readonly GcConfig gc;
            private readonly TypeContext context;
            private readonly Liveness liveness;
            private readonly MoveAnalysis moves;
            private readonly MoveAnalysis mustMoves;
            private readonly HashSet<int> addressTaken;

            // Per-pass emission state (the planning pass runs the same code and is discarded).
            private readonly List<Operation> operations = [];
            private readonly List<(int Operation, int Operand, int Block)> fixups = [];
            private readonly List<(int Operation, int Label)> labelFixups = [];
            private readonly List<int> labels = [];
            private readonly List<IrType> pinSlots = [];
            private readonly List<Safepoint> safepoints = [];
            private readonly HashSet<int> handlerBlocks;
            private readonly List<(int Operation, int? CatchType, int Block, int Label)> handlers = [];
            private readonly List<(int Operation, States States)> cleanupPads = [];
            private readonly int[] blockStarts;
            private Dictionary<Place, int> flagSlots = [];
            private readonly HashSet<Place> flaggedResources = [];
            private bool planning;
            private int nextBlock;
            private Location current;

            public FunctionLowering(IrModule module, IrFunction function, TokenTable tokens, GcConfig gc)
            {
                this.module = module;
                this.function = function;
                this.tokens = tokens;
                this.gc = gc;
                context = TypeContext.For(function, module);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                liveness = Liveness.Compute(function);
                Spent[0] += watch.Elapsed.TotalMilliseconds; watch.Restart();
                moves = new MoveAnalysis(function, module, Transfers);
                mustMoves = new MoveAnalysis(function, module, Transfers, must: true);
                Spent[1] += watch.Elapsed.TotalMilliseconds;
                addressTaken = [.. function.Blocks.SelectMany(b => b.Statements).OfType<AssignStatement>()
                    .Select(a => a.Value).OfType<RefRvalue>().Select(r => r.Place.Local)];
                blockStarts = new int[function.Blocks.Length];
                handlerBlocks = [.. function.Blocks.Select(b => b.Terminator).OfType<IUnwinding>().SelectMany(u => u.Handlers).Select(h => h.Block)];
            }

            /// <summary>Timing (LARVA_TIMINGS): liveness, move analyses, dataflow, emission.</summary>
            public static readonly double[] Spent = new double[4];

            private bool Has(GcCapabilities capability) => (gc.Capabilities & capability) != 0;

            /// <summary>IL locals that hold IR locals (everything but the arguments); drop flags, then pin slots, follow.</summary>
            private int IrLocalSlots => function.Locals.Length - function.ArgumentCount;

            private int PinSlot(int index) => IrLocalSlots + flagSlots.Count + index;

            public IlFunction Lower()
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var may = Dataflow.Forward(function, moves);
                var must = Dataflow.Forward(function, mustMoves);
                Spent[2] += watch.Elapsed.TotalMilliseconds; watch.Restart();

                // Pass 1 finds the owners whose release needs a drop flag; pass 2 emits with those flags allocated.
                planning = true;
                EmitBody(may, must);
                planning = false;
                flagSlots = flaggedResources.Select((place, i) => (place, i)).ToDictionary(x => x.place, x => IrLocalSlots + x.i);
                EmitBody(may, must);
                Spent[3] += watch.Elapsed.TotalMilliseconds;

                int[] localTypes =
                [
                    .. Enumerable.Range(0, function.Locals.Length)
                        .Where(l => !function.IsArgument(l))
                        .Select(l => tokens.Type(function.Locals[l].Type)),
                    .. flagSlots.Keys.Select(_ => tokens.Type(IrType.Bool)),
                    .. pinSlots.Select(tokens.Type),
                ];
                return new IlFunction(function.Name, function.ArgumentCount, localTypes,
                    new OperationBlock(function.Name, [.. PatchedOperations()]))
                {
                    Domain = function.Domain,
                    TypeParameterCount = function.Generics.Length,
                    ArgumentTypes = [.. Enumerable.Range(1, function.ArgumentCount).Select(a => tokens.Type(function.Locals[a].Type))],
                    GcRoots = module.Any(GcCapabilities.Tracing, gc) ? FrameRoots() : [],
                    Safepoints = [.. safepoints],
                    Handlers = [.. handlers.Select(h => new ExceptionHandler(h.Operation, h.Operation + 1, h.CatchType,
                        h.Block >= 0 ? blockStarts[h.Block] : labels[h.Label]))],
                    Native = NativeOf(function),
                };
            }

            /// <summary>The C call a function stands for (its attribute <c>Larva.Import</c> or <c>Larva.NativeCall</c>), if any.</summary>
            private static NativeCall? NativeOf(IrFunction function)
            {
                foreach (var attribute in function.Attributes)
                {
                    if (attribute is { Name: "Larva.Import", Arguments: [TextArgument library, TextArgument symbol] })
                        return new NativeCall(library.Value, symbol.Value);
                    if (attribute.Name == "Larva.NativeCall")
                        return new NativeCall(null, null);
                }
                return null;
            }

            private void EmitBody(MoveState?[] may, MoveState?[] must)
            {
                operations.Clear();
                fixups.Clear();
                labelFixups.Clear();
                labels.Clear();
                pinSlots.Clear();
                safepoints.Clear();
                handlers.Clear();
                cleanupPads.Clear();

                // Owned arguments start initialized; every other flag relies on locals being zero-initialized.
                foreach (var (resource, slot) in flagSlots)
                {
                    if (function.IsArgument(resource.Local))
                        SetFlag(slot, true);
                }

                var loopHeaders = LoopHeaders();
                for (int b = 0; b < function.Blocks.Length; b++)
                {
                    blockStarts[b] = operations.Count;
                    nextBlock = b + 1;

                    // Unreachable blocks own nothing.
                    var states = may[b] is { } entry && must[b] is { } mustEntry
                        ? new States(moves.Clone(entry), mustMoves.Clone(mustEntry))
                        : new States(NothingInitialized(), NothingInitialized());

                    if (Has(GcCapabilities.Tracing) && loopHeaders.Contains(b))
                        EmitSafepoint(new Operation(OpCode.System, SystemOp.Poll), liveness.LiveBefore(new Location(b, 0)), states.Must);

                    var block = function.Blocks[b];
                    // Handler blocks are entered with the exception on the stack; `caught` stores it, otherwise drop it.
                    if (handlerBlocks.Contains(b) && block.Statements is not [AssignStatement { Value: CaughtRvalue }, ..])
                        Emit(new Operation(OpCode.Stack, StackOp.Pop));
                    for (int i = 0; i < block.Statements.Length; i++)
                    {
                        current = new Location(b, i);
                        LowerStatement(block.Statements[i], states);
                        moves.Statement(states.May, block.Statements[i], current);
                        mustMoves.Statement(states.Must, block.Statements[i], current);
                    }
                    current = new Location(b, block.Statements.Length);
                    LowerTerminator(block.Terminator, states);
                }

                foreach (var (operation, unwound) in cleanupPads)
                    EmitCleanupPad(operation, unwound);
            }

            // ── Statements ─────────────────────────────────────────

            private void LowerStatement(Statement statement, States states)
            {
                switch (statement)
                {
                    case AssignStatement { Value: IntrinsicRvalue unitIntrinsic } unitAssign when function.TypeOf(unitAssign.Target, module).IsUnit:
                        LowerRvalue(unitIntrinsic); // runs for its effect; a unit result occupies no stack slot
                        break;
                    case AssignStatement assign:
                        if (function.TypeOf(assign.Target, module).IsUnit)
                            return; // unit values occupy no stack slot
                        var operands = MoveAnalysis.Operands(assign.Value).ToList();
                        var moved = moves.MovedBy(operands, current).ToList();
                        StorePlace(assign.Target, () =>
                        {
                            LowerRvalue(assign.Value);
                            // The new value is on the stack; now release what the target held.
                            ReleaseOwned(assign.Target, states, moved);
                        });
                        UpdateFlags(moved, assign.Target);
                        break;
                    case StorageDeadStatement dead:
                        ReleaseOwned(new Place(dead.Local), states);
                        UpdateFlags([new Place(dead.Local)], null);
                        break;
                    case StorageLiveStatement live:
                        UpdateFlags([new Place(live.Local)], null);
                        break;
                    case StoreGlobalStatement store:
                        if (Has(GcCapabilities.RefCounting) && module.Globals[store.Global].Type.Unoptional() is GcType)
                        {
                            Emit(new Operation(OpCode.Literal, LiteralOp.Global, store.Global));
                            Emit(new Operation(OpCode.Gc, GcOp.Release)); // the old value loses its owner
                        }
                        LoadOperand(store.Value);
                        Emit(new Operation(OpCode.Literal, LiteralOp.SetGlobal, store.Global));
                        break;
                    case FreeStatement free:
                    {
                        var pointee = function.TypeOf(free.Pointer, module).Pointee()!;
                        if (Has(GcCapabilities.RefCounting))
                        {
                            // The handles the memory held lose their owner (the ownership analysis does not follow raw memory).
                            foreach (var slot in Larva.IR.Analysis.HandleSlots.Of(free.Pointer.Deref(), pointee, module))
                            {
                                LoadPlace(slot);
                                Emit(new Operation(OpCode.Gc, GcOp.Release));
                            }
                        }
                        LoadPlace(free.Pointer);
                        Emit(new Operation(OpCode.Gc, GcOp.Free, tokens.Type(pointee)));
                        break;
                    }
                    case RegionEnterStatement enter:
                        Emit(new Operation(OpCode.Gc, GcOp.RegionEnter, enter.Region));
                        break;
                    case RegionExitStatement exit:
                        Emit(new Operation(OpCode.Gc, GcOp.RegionExit, exit.Region));
                        break;
                    case NopStatement:
                        break;
                    default:
                        throw new NotSupportedException(statement.GetType().Name);
                }
            }

            private void LowerRvalue(Rvalue rvalue)
            {
                switch (rvalue)
                {
                    case UseRvalue use:
                        LoadOperand(use.Operand);
                        break;

                    case BinaryRvalue binary:
                        LoadOperand(binary.Left);
                        // A shift wraps its amount at the operand's width: a constant left operand needs its type
                        // (ldc is untyped and would shift at 64 bits).
                        if (binary.Op is BinaryOp.Shl or BinaryOp.Shr && binary.Left is ConstOperand { Type.IsInteger: true, Type.BitWidth: <= 64 } shifted)
                            Emit(new Operation(OpCode.Convert, ConvertOp.Conv, tokens.Type(shifted.Type)));
                        LoadOperand(binary.Right);
                        EmitBinary(binary.Op, function.TypeOf(binary.Left, module));
                        break;

                    case UnaryRvalue unary:
                        LoadOperand(unary.Operand);
                        var type = function.TypeOf(unary.Operand, module);
                        if (unary.Op == UnaryOp.Neg)
                            Emit(type.IsFloat ? new Operation(OpCode.Float, FloatOp.Neg) : new Operation(OpCode.Alu, AluOp.Neg));
                        else if (type == IrType.Bool)
                        {
                            Emit(new Operation(OpCode.Literal, LiteralOp.False));
                            Emit(new Operation(OpCode.Compare, Condition.Eq));
                        }
                        else
                            Emit(new Operation(OpCode.Alu, AluOp.Not));
                        break;

                    case RefRvalue reference:
                        PinIfInsideGcObject(reference.Place);
                        LoadAddress(reference.Place);
                        break;

                    case CaughtRvalue:
                        break; // already on the stack at handler entry

                    case StaticRvalue data:
                        Emit(new Operation(OpCode.Literal, LiteralOp.Data, data.Data));
                        break;

                    case IntrinsicRvalue intrinsic:
                        foreach (var argument in intrinsic.Arguments)
                            LoadOperand(argument);
                        Emit(new Operation(OpCode.System, SystemOp.Intrinsic, Intrinsics.Find(intrinsic.Name)!.Id));
                        break;

                    case ZeroRvalue zero:
                        Emit(new Operation(OpCode.Literal, LiteralOp.Zero, tokens.Type(zero.Type)));
                        break;

                    case GlobalRvalue global:
                        Emit(new Operation(OpCode.Literal, LiteralOp.Global, global.Global));
                        if (Has(GcCapabilities.RefCounting) && module.Globals[global.Global].Type.Unoptional() is GcType)
                            Emit(new Operation(OpCode.Gc, GcOp.Retain)); // the copy is a new owner
                        break;

                    case FunctionRefRvalue reference:
                        Emit(new Operation(OpCode.CallExtended, CallOp.LoadFunction, reference.Function));
                        break;

                    case HandleCastRvalue cast:
                        // The same address, seen as the target type: the interpreter reads through a pointer with the type it
                        // carries, so the cast gives it the new one (a field read right after the cast needs it).
                        LoadOperand(cast.Handle);
                        Emit(new Operation(OpCode.Convert, ConvertOp.Conv, tokens.Type(cast.Target)));
                        break;

                    case WrapRvalue wrap:
                        LoadOperand(wrap.Value);
                        break;

                    case UnwrapRvalue unwrap:
                        LoadOperand(unwrap.Value);
                        Emit(new Operation(OpCode.Stack, StackOp.CheckNull));
                        break;

                    case IsNullRvalue test:
                        LoadPlace(test.Value);
                        Emit(new Operation(OpCode.Stack, StackOp.IsNull));
                        break;

                    case CastRvalue cast:
                        LoadOperand(cast.Operand);
                        if (function.TypeOf(cast.Operand, module) != cast.Type)
                            Emit(new Operation(OpCode.Convert, ConvertOp.Conv, tokens.Type(cast.Type)));
                        break;

                    case SizeOfRvalue size when size.Type.IsGeneric():
                        Emit(new Operation(OpCode.Generic, GenericOp.SizeOf, tokens.Type(size.Type)));
                        break;

                    case SizeOfRvalue size:
                        var layout = Layouts.Of(size.Type, module)
                            ?? throw new InvalidOperationException($"{function.Name}: `{size.Type}` has no static size.");
                        Emit(new Operation(OpCode.Const, layout.Size));
                        break;

                    case ArrayRvalue { Owner: { } owner } array:
                        LoadOperand(array.Length);
                        Emit(new Operation(OpCode.Gc, array.Kind == AllocKind.Gc ? GcOp.NewTrailing : GcOp.AllocTrailing, tokens.Type(owner)));
                        break;

                    case ArrayRvalue array:
                        LoadOperand(array.Length);
                        Emit(array.Kind == AllocKind.Gc
                            ? new Operation(OpCode.Gc, GcOp.NewArray, tokens.Type(array.Element))
                            : new Operation(OpCode.Array, ArrayOp.New, tokens.Type(array.Element)));
                        break;

                    case LenRvalue len:
                        LoadPlace(len.Array);
                        Emit(new Operation(OpCode.Array, ArrayOp.Length));
                        break;

                    case AllocRvalue alloc:
                        LoadOperand(alloc.Value);
                        var allocated = tokens.Type(function.TypeOf(alloc.Value, module));
                        if (alloc.Finalizer is { } finalizer)
                        {
                            LoadOperand(finalizer);
                            Emit(new Operation(OpCode.Gc, GcOp.NewFinalized, allocated));
                            break;
                        }
                        Emit(alloc.Kind == AllocKind.Gc
                            ? new Operation(OpCode.Gc, GcOp.New, allocated)
                            : new Operation(OpCode.Gc, GcOp.Alloc, allocated));
                        break;

                    default:
                        throw new NotSupportedException(rvalue.GetType().Name);
                }
            }

            private void EmitBinary(BinaryOp op, IrType type)
            {
                bool f = type.IsFloat;
                Emit(op switch
                {
                    BinaryOp.Add => f ? new Operation(OpCode.Float, FloatOp.Add) : new Operation(OpCode.Add),
                    BinaryOp.Sub => f ? new Operation(OpCode.Float, FloatOp.Sub) : new Operation(OpCode.Sub),
                    BinaryOp.Mul => f ? new Operation(OpCode.Float, FloatOp.Mul) : new Operation(OpCode.Mul),
                    BinaryOp.Div => f ? new Operation(OpCode.Float, FloatOp.Div) : new Operation(OpCode.Alu, AluOp.Div),
                    BinaryOp.Rem => f ? new Operation(OpCode.Float, FloatOp.Rem) : new Operation(OpCode.Alu, AluOp.Rem),
                    BinaryOp.And => new Operation(OpCode.Alu, AluOp.And),
                    BinaryOp.Or => new Operation(OpCode.Alu, AluOp.Or),
                    BinaryOp.Xor => new Operation(OpCode.Alu, AluOp.Xor),
                    BinaryOp.Shl => new Operation(OpCode.Alu, AluOp.Shl),
                    BinaryOp.Shr => new Operation(OpCode.Alu, type.IsUnsigned ? AluOp.UShr : AluOp.Shr),
                    BinaryOp.Eq => new Operation(OpCode.Compare, Condition.Eq),
                    BinaryOp.Ne => new Operation(OpCode.Compare, Condition.Ne),
                    BinaryOp.Lt => new Operation(OpCode.Compare, Condition.Lt),
                    BinaryOp.Le => new Operation(OpCode.Compare, Condition.Le),
                    BinaryOp.Gt => new Operation(OpCode.Compare, Condition.Gt),
                    BinaryOp.Ge => new Operation(OpCode.Compare, Condition.Ge),
                    _ => throw new ArgumentOutOfRangeException(nameof(op)),
                });
            }

            // ── Terminators ────────────────────────────────────────

            private void LowerTerminator(Terminator terminator, States states)
            {
                switch (terminator)
                {
                    case GotoTerminator jump:
                        JumpUnlessNext(jump.Target);
                        break;

                    case IfTerminator branch:
                        LoadOperand(branch.Condition);
                        if (branch.Else == nextBlock)
                            EmitBranch(OpCode.BranchTrue, branch.Then);
                        else if (branch.Then == nextBlock)
                            EmitBranch(OpCode.BranchFalse, branch.Else);
                        else
                        {
                            EmitBranch(OpCode.BranchTrue, branch.Then);
                            EmitBranch(OpCode.Jump, branch.Else);
                        }
                        break;

                    case SwitchTerminator @switch:
                        LoadOperand(@switch.Value);
                        EmitBranch(OpCode.Switch, @switch.Targets);
                        JumpUnlessNext(@switch.Otherwise);
                        break;

                    case ReturnTerminator:
                        // Everything the function still owns (arguments included) dies here; _0 goes to the caller.
                        for (int local = 1; local < function.Locals.Length; local++)
                            ReleaseOwned(new Place(local), states);
                        for (int slot = 0; slot < pinSlots.Count; slot++)
                        {
                            Emit(new Operation(OpCode.LoadLocal, PinSlot(slot)));
                            Emit(new Operation(OpCode.Gc, GcOp.Unpin));
                        }
                        if (!function.ReturnType.IsUnit)
                            Emit(new Operation(OpCode.LoadLocal, 0));
                        Emit(new Operation(OpCode.Return));
                        break;

                    case CallTerminator call:
                        var calleeType = call.Callee is { } indirectCallee ? (FunctionType)function.TypeOf(indirectCallee, module) : null;
                        var callResult = calleeType?.Result ?? module.Functions[call.Function].ReturnType.Substitute(call.TypeArguments);
                        var moved = moves.MovedBy([.. call.Operands], current).ToList();
                        int callOperation = -1;
                        void EmitCall()
                        {
                            // Shared callee: witnesses first, one per type parameter.
                            foreach (var typeArgument in call.TypeArguments)
                                EmitWitness(typeArgument);
                            foreach (var argument in call.Arguments)
                                LoadOperand(argument);
                            if (call.Callee is { } target)
                                LoadOperand(target);
                            var callOp = call.Callee is not null
                                ? new Operation(OpCode.CallExtended, CallOp.Indirect)
                                : new Operation(OpCode.Call, call.Function);
                            callOperation = operations.Count;
                            if (module.Any(GcCapabilities.Tracing, gc))
                            {
                                // Roots across the call: what is live after it, minus the destination being written.
                                var across = liveness.LiveOut(current.Block).Where(l => !(call.Destination.IsLocal && l == call.Destination.Local));
                                EmitSafepoint(callOp, [.. across], states.Must);
                            }
                            else
                            {
                                Emit(callOp);
                            }
                        }
                        if (callResult.IsUnit)
                            EmitCall();
                        else
                            StorePlace(call.Destination, () =>
                            {
                                EmitCall();
                                ReleaseOwned(call.Destination, states, moved);
                            });
                        UpdateFlags(moved, callResult.IsUnit ? null : call.Destination);
                        RegisterUnwinding(callOperation, call, states);
                        JumpUnlessNext(call.Target);
                        break;

                    case ThrowTerminator @throw:
                        LoadOperand(@throw.Value);
                        if (@throw.Value is ConstOperand { Type: var thrownType } && !thrownType.IsUnit && thrownType != IrType.Bool)
                            Emit(new Operation(OpCode.Convert, ConvertOp.Conv, tokens.Type(thrownType))); // give the literal its type
                        RegisterUnwinding(operations.Count, @throw, states);
                        Emit(new Operation(OpCode.Exception, ExceptionOp.Throw));
                        break;

                    case ResumeTerminator resume:
                        RegisterUnwinding(operations.Count, resume, states);
                        Emit(new Operation(OpCode.Exception, ExceptionOp.Rethrow));
                        break;

                    case DropTerminator drop:
                        ReleaseOwned(drop.Place, states);
                        UpdateFlags([drop.Place], null);
                        JumpUnlessNext(drop.Target);
                        break;

                    case UnreachableTerminator:
                        Emit(new Operation(OpCode.System, SystemOp.Trap, 0));
                        break;

                    default:
                        throw new NotSupportedException(terminator.GetType().Name);
                }
            }

            // ── Exceptions ─────────────────────────────────────────

            /// <summary>
            /// Protects the operation at <paramref name="operation"/>: explicit clauses jump to their blocks; without a catch-all,
            /// a cleanup pad (emitted after the body) releases what this frame owns on the exception path and re-raises.
            /// </summary>
            private void RegisterUnwinding(int operation, Terminator terminator, States before)
            {
                var clauses = ((IUnwinding)terminator).Handlers;
                foreach (var clause in clauses)
                    handlers.Add((operation, clause.Type is null ? null : tokens.Type(clause.Type), clause.Block, -1));
                if (clauses.Any(c => c.Type is null))
                    return;

                var unwound = new States(moves.Clone(before.May), mustMoves.Clone(before.Must));
                moves.Unwind(unwound.May, terminator, current);
                mustMoves.Unwind(unwound.Must, terminator, current);
                cleanupPads.Add((operation, unwound));
            }

            private void EmitCleanupPad(int operation, States states)
            {
                int start = operations.Count;
                int label = NewLabel();
                MarkLabel(label);
                Emit(new Operation(OpCode.Stack, StackOp.Pop)); // the exception; the frame keeps it for eh.rethrow
                for (int local = 0; local < function.Locals.Length; local++)
                    ReleaseOwned(new Place(local), states);
                for (int slot = 0; slot < pinSlots.Count; slot++)
                {
                    Emit(new Operation(OpCode.LoadLocal, PinSlot(slot)));
                    Emit(new Operation(OpCode.Gc, GcOp.Unpin));
                }

                if (operations.Count == start + 1)
                {
                    operations.RemoveAt(start); // nothing to clean up: let the exception pass straight through
                    return;
                }
                Emit(new Operation(OpCode.Exception, ExceptionOp.Rethrow));
                handlers.Add((operation, null, -1, label));
            }

            /// <summary><c>gen.ldtarg N</c> for the function's own parameter N, <c>gen.wit type.N</c> for anything else.</summary>
            private void EmitWitness(IrType type) =>
                Emit(type is GenericParamType p
                    ? new Operation(OpCode.Generic, GenericOp.TypeArgument, p.Index)
                    : new Operation(OpCode.Generic, GenericOp.Witness, tokens.Witness(type, module)));

            private void JumpUnlessNext(int block)
            {
                if (block != nextBlock)
                    EmitBranch(OpCode.Jump, block);
            }

            // ── Ownership and drop flags ───────────────────────────

            /// <summary>
            /// Releases every resource owned by the value at <paramref name="place"/>: unconditionally when initialized on all
            /// paths, behind its drop flag when only on some, not at all when on none or when the current point moves it out.
            /// Net stack effect: none.
            /// </summary>
            private void ReleaseOwned(Place place, States states, IReadOnlyList<Place>? movedNow = null)
            {
                foreach (var (path, release, type) in OwnedResources(function.TypeOf(place, module), []))
                {
                    var resource = new Place(place.Local, [.. place.Projections, .. path]);
                    if (movedNow?.Any(m => m.IsPrefixOf(resource) || resource.IsPrefixOf(m)) == true || MoveAnalysis.Covers(states.Must, resource))
                        continue;

                    int? skip = null;
                    if (states.May.Find(resource) is not null)
                    {
                        if (planning)
                            flaggedResources.Add(resource);
                        if (flagSlots.TryGetValue(resource, out int slot))
                        {
                            skip = NewLabel();
                            Emit(new Operation(OpCode.LoadLocal, slot));
                            EmitBranchToLabel(OpCode.BranchFalse, skip.Value);
                        }
                    }

                    LoadPlace(resource);
                    Emit(release switch
                    {
                        Release.FreeBox => new Operation(OpCode.Gc, GcOp.Free, tokens.Type(type.Pointee()!)),
                        Release.ReleaseHandle => new Operation(OpCode.Gc, GcOp.Release),
                        _ => new Operation(OpCode.Generic, GenericOp.Drop, tokens.Type(type)),
                    });

                    if (skip is int label)
                        MarkLabel(label);
                }
            }

            /// <summary>Keeps drop flags in sync: cleared for places given up here, set for the place just initialized.</summary>
            private void UpdateFlags(IEnumerable<Place> cleared, Place? initialized)
            {
                foreach (var place in cleared)
                {
                    foreach (var (resource, slot) in flagSlots)
                    {
                        if (place.IsPrefixOf(resource) || resource.IsPrefixOf(place))
                            SetFlag(slot, false);
                    }
                }
                if (initialized is not null)
                {
                    foreach (var (resource, slot) in flagSlots)
                    {
                        if (initialized.IsPrefixOf(resource))
                            SetFlag(slot, true);
                    }
                }
            }

            private void SetFlag(int slot, bool value)
            {
                Emit(new Operation(OpCode.Const, value ? 1 : 0));
                Emit(new Operation(OpCode.StoreLocal, slot));
            }

            /// <summary>Owned resources stored inline in a value of <paramref name="type"/>, as field paths from its start.</summary>
            private IEnumerable<(Projection[] Path, Release Release, IrType Type)> OwnedResources(IrType type, HashSet<StructType> visiting)
            {
                switch (type)
                {
                    case BoxType or OptionalType { Inner: BoxType }:
                        yield return ([], Release.FreeBox, type);
                        break;
                    case GcType or OptionalType { Inner: GcType } when Has(GcCapabilities.RefCounting):
                        yield return ([], Release.ReleaseHandle, type);
                        break;
                    case GenericParamType when !Constraints.Satisfies(type, TypeConstraint.Copy, context):
                        yield return ([], Release.WitnessDrop, type);
                        break;
                    case StructType st when visiting.Add(st):
                        var fields = module.Structs[st.Id].Fields;
                        for (int i = 0; i < fields.Length; i++)
                        {
                            foreach (var (path, release, inner) in OwnedResources(fields[i].Substitute(st.Arguments), visiting))
                                yield return ([Projection.Field(i), .. path], release, inner);
                        }
                        visiting.Remove(st);
                        break;
                }
            }

            /// <summary>
            /// Reference counting: reading a handle local at its last use hands its reference to the new owner (no retain,
            /// and the local is treated as moved so it is not released). Only for whole locals whose address is never taken
            /// and that appear once at the location (a second read would need its own reference).
            /// </summary>
            private bool Transfers(Operand operand, Location at)
            {
                if (!Has(GcCapabilities.RefCounting))
                    return false;
                var place = operand switch
                {
                    CopyOperand copy => copy.Place,
                    MoveOperand move => move.Place,
                    _ => null,
                };
                return place is { IsLocal: true }
                    && function.TypeOf(place, module).Unoptional() is GcType
                    && !addressTaken.Contains(place.Local)
                    && Occurrences(place.Local, at) == 1
                    && !liveness.LiveAfter(at).Contains(place.Local);
            }

            /// <summary>How many times <paramref name="local"/> is read by the statement or terminator at <paramref name="at"/>.</summary>
            private int Occurrences(int local, Location at)
            {
                var block = function.Blocks[at.Block];
                (IEnumerable<Operand> Operands, Place? Target, Place? Borrowed) parts = at.Index < block.Statements.Length
                    ? block.Statements[at.Index] switch
                    {
                        AssignStatement assign => (MoveAnalysis.Operands(assign.Value), assign.Target, (assign.Value as RefRvalue)?.Place),
                        _ => ([], null, null),
                    }
                    : block.Terminator switch
                    {
                        CallTerminator call => (call.Arguments, call.Destination, null),
                        IfTerminator branch => ([branch.Condition], null, null),
                        SwitchTerminator @switch => ([@switch.Value], null, null),
                        _ => ([], null, null),
                    };

                static int Count(Place place, int local) =>
                    (place.Local == local ? 1 : 0) + place.IndexLocals().Count(i => i == local);

                int count = parts.Operands.Sum(o => o switch
                {
                    CopyOperand copy => Count(copy.Place, local),
                    MoveOperand move => Count(move.Place, local),
                    _ => 0,
                });
                if (parts.Borrowed is { } borrowed)
                    count += Count(borrowed, local);
                if (parts.Target is { IsLocal: false } target)
                    count += Count(target, local);
                return count;
            }

            private MoveState NothingInitialized()
            {
                MoveState state = new();
                for (int local = 0; local < function.Locals.Length; local++)
                    state.Set(new Place(local), new UninitOrigin(UninitKind.NeverInitialized, default));
                return state;
            }

            // ── GC cooperation ─────────────────────────────────────

            /// <summary>Moving GCs: borrowing inside a <c>gc</c> object pins it; the handle is kept in a hidden slot to unpin at return.</summary>
            private void PinIfInsideGcObject(Place place)
            {
                if (!module.Any(GcCapabilities.Moving, gc))
                    return;
                for (int j = 0; j < place.Projections.Length; j++)
                {
                    var handle = new Place(place.Local, place.Projections[..j]);
                    if (place.Projections[j] is DerefProjection && function.TypeOf(handle, module).Unoptional() is GcType handleType)
                    {
                        LoadPlace(handle);
                        Emit(new Operation(OpCode.Duplicate));
                        Emit(new Operation(OpCode.StoreLocal, PinSlot(pinSlots.Count)));
                        pinSlots.Add(handleType);
                        Emit(new Operation(OpCode.Gc, GcOp.Pin));
                        return;
                    }
                }
            }

            /// <summary>Barrier GCs: storing a value that may carry handles anywhere behind a pointer goes through <c>gc.wb</c>.</summary>
            private bool NeedsWriteBarrier(Place place) =>
                Has(GcCapabilities.Barriers)
                && place.HasDeref()
                && !Constraints.Satisfies(function.TypeOf(place, module), TypeConstraint.Unmanaged, context);

            /// <summary>Blocks entered by a back edge (a jump from a block at or after them): where loops spin.</summary>
            private HashSet<int> LoopHeaders()
            {
                HashSet<int> headers = [];
                for (int b = 0; b < function.Blocks.Length; b++)
                {
                    foreach (int successor in function.Blocks[b].Terminator.Successors)
                    {
                        if (successor <= b)
                            headers.Add(successor);
                    }
                }
                return headers;
            }

            /// <summary>Emits a GC safepoint operation and records the slots holding handles that are live and initialized there.</summary>
            private void EmitSafepoint(Operation operation, IEnumerable<int> live, MoveState must)
            {
                List<GcRoot> roots = [];
                // Under reference counting a local that holds a handle is read once more by its release (at its overwrite or
                // death), after its last use the liveness sees: every initialized handle local stays a root.
                var candidates = Has(GcCapabilities.RefCounting) ? Enumerable.Range(0, function.Locals.Length) : live;
                foreach (int local in candidates.Order())
                {
                    if (!MoveAnalysis.Covers(must, new Place(local)) && RootOf(local) is { } root)
                        roots.Add(root);
                }
                for (int i = 0; i < pinSlots.Count; i++)
                    roots.Add(new GcRoot(false, PinSlot(i), [0]));

                safepoints.Add(new Safepoint(operations.Count, [.. roots]));
                Emit(operation);
            }

            /// <summary>The root entry for a local if its value may hold handles (offsets null = read from the witness).</summary>
            private GcRoot? RootOf(int local)
            {
                var type = function.Locals[local].Type;
                bool isArgument = function.IsArgument(local);
                int slot = isArgument ? local - 1 : IlLocal(local);
                if (type.IsGeneric())
                    return Constraints.Satisfies(type, TypeConstraint.Unmanaged, context) ? null : new GcRoot(isArgument, slot, null);
                return Layouts.Of(type, module) is { GcOffsets.Length: > 0 } layout ? new GcRoot(isArgument, slot, layout.GcOffsets) : null;
            }

            /// <summary>Conservative whole-frame map: every slot that can ever hold a handle.</summary>
            private GcRoot[] FrameRoots() =>
            [
                .. Enumerable.Range(0, function.Locals.Length).Select(RootOf).OfType<GcRoot>(),
                .. Enumerable.Range(0, pinSlots.Count).Select(i => new GcRoot(false, PinSlot(i), [0])),
            ];

            // ── Places and operands ────────────────────────────────

            private void LoadOperand(Operand operand)
            {
                switch (operand)
                {
                    // Reference counting: a handle that gains an owner takes a reference, unless this read transfers it.
                    // `gc T` is Copy, so an ordinary `move` leaves the source owning its reference too.
                    case CopyOperand or MoveOperand:
                        var place = operand is CopyOperand copy ? copy.Place : ((MoveOperand)operand).Place;
                        LoadPlace(place);
                        if (Has(GcCapabilities.RefCounting) && function.TypeOf(place, module).Unoptional() is GcType && !Transfers(operand, current))
                            Emit(new Operation(OpCode.Gc, GcOp.Retain));
                        break;
                    case ConstOperand { Type.IsUnit: true }:
                        break;
                    case ConstOperand { Type: var type } constant when type == IrType.Bool:
                        Emit(new Operation(OpCode.Literal, constant.Bits != 0 ? LiteralOp.True : LiteralOp.False));
                        break;
                    case ConstOperand constant:
                        LoadConstant(constant);
                        break;
                    default:
                        throw new NotSupportedException(operand.GetType().Name);
                }
            }

            /// <summary>
            /// Up to 64 bits: <c>ldc</c> with the two's-complement pattern (+ <c>conv.bitcast</c> for floats).
            /// Wider integers whose value fits 64 bits: <c>ldc</c> + <c>conv.to</c>. Anything else: constant pool.
            /// </summary>
            private void LoadConstant(ConstOperand constant)
            {
                int width = constant.Type.BitWidth!.Value;
                if (width <= 64)
                {
                    Emit(new Operation(OpCode.Const, ToInt64(constant.Bits)));
                    if (constant.Type.IsFloat)
                        Emit(new Operation(OpCode.Convert, ConvertOp.BitCast, tokens.Type(constant.Type)));
                }
                else if (constant.Type.IsInteger && constant.FitsInt64)
                {
                    Emit(new Operation(OpCode.Const, (long)constant.Bits));
                    Emit(new Operation(OpCode.Convert, ConvertOp.Conv, tokens.Type(constant.Type)));
                }
                else
                {
                    Emit(new Operation(OpCode.Literal, LiteralOp.Wide, tokens.Constant(constant.Bits, constant.Type)));
                }
            }

            /// <summary>Low 64 bits as a signed value (u64/f64 patterns above <c>long.MaxValue</c> become negative).</summary>
            private static long ToInt64(BigInteger value) => unchecked((long)(ulong)(value & ulong.MaxValue));

            /// <summary>Whether <paramref name="place"/> indexes an inline array: those elements are reached by address.</summary>
            private bool IndexesInlineArray(Place place)
            {
                for (int i = 0; i < place.Projections.Length; i++)
                {
                    if (place.Projections[i] is IndexProjection && function.TypeOf(new Place(place.Local, place.Projections[..i]), module) is FixedArrayType)
                        return true;
                }
                return false;
            }

            /// <summary>Pushes the value stored at <paramref name="place"/>.</summary>
            private void LoadPlace(Place place)
            {
                if (IndexesInlineArray(place))
                {
                    LoadAddress(place);
                    Emit(new Operation(OpCode.Stack, StackOp.LoadMem));
                    return;
                }
                // An array kept in place at the end of an object is its address (as an array's handle is), so what
                // follows reaches it like any array.
                int start = 0;
                for (int t = place.Projections.Length - 1; t >= 0 && start == 0; t--)
                {
                    if (place.Projections[t] is FieldProjection && function.TypeOf(new Place(place.Local, place.Projections[..(t + 1)]), module) is ArrayType)
                        start = t + 1;
                }
                if (start > 0)
                    LoadAddress(new Place(place.Local, place.Projections[..start]));
                else
                    LoadLocal(place.Local);
                for (int i = start; i < place.Projections.Length; i++)
                {
                    switch (place.Projections[i])
                    {
                        case DerefProjection:
                            Emit(new Operation(OpCode.Stack, StackOp.LoadMem));
                            break;
                        case FieldProjection field:
                            var owner = function.TypeOf(new Place(place.Local, place.Projections[..i]), module);
                            Emit(new Operation(OpCode.LoadField, FieldToken(owner, field)));
                            break;
                        case IndexProjection index:
                            LoadLocal(index.Local);
                            Emit(new Operation(OpCode.Array, ArrayOp.Load));
                            break;
                    }
                }
            }

            /// <summary>Pushes the address of <paramref name="place"/> (used by <c>&amp;</c> / <c>&amp;mut</c>).</summary>
            private void LoadAddress(Place place)
            {
                if (place.IsLocal)
                {
                    Emit(function.IsArgument(place.Local)
                        ? new Operation(OpCode.Stack, StackOp.LoadArgumentAddress, place.Local - 1)
                        : new Operation(OpCode.Stack, StackOp.LoadLocalAddress, IlLocal(place.Local)));
                    return;
                }

                var parent = place.Parent;
                switch (place.Projections[^1])
                {
                    case DerefProjection:
                        LoadPlace(parent); // the address of *p is the value of p
                        break;
                    case FieldProjection field:
                        LoadAddress(parent);
                        Emit(new Operation(OpCode.Object, ObjectOp.LoadFieldAddress, FieldToken(function.TypeOf(parent, module), field)));
                        break;
                    case IndexProjection index when function.TypeOf(parent, module) is FixedArrayType inline:
                        LoadAddress(parent);
                        LoadLocal(index.Local);
                        Emit(new Operation(OpCode.Array, ArrayOp.FixedElementAddress, tokens.Type(inline)));
                        break;
                    case IndexProjection index:
                        LoadPlace(parent);
                        LoadLocal(index.Local);
                        Emit(new Operation(OpCode.Array, ArrayOp.LoadElementAddress));
                        break;
                }
            }

            /// <summary>Stores the value pushed by <paramref name="emitValue"/> into <paramref name="place"/>.</summary>
            private void StorePlace(Place place, Action emitValue)
            {
                if (place.IsLocal)
                {
                    emitValue();
                    Emit(function.IsArgument(place.Local)
                        ? new Operation(OpCode.Stack, StackOp.StoreArgument, place.Local - 1)
                        : new Operation(OpCode.StoreLocal, IlLocal(place.Local)));
                    return;
                }

                if (NeedsWriteBarrier(place))
                {
                    LoadAddress(place);
                    emitValue();
                    Emit(new Operation(OpCode.Gc, GcOp.WriteBarrier, tokens.Type(function.TypeOf(place, module))));
                    return;
                }

                if (IndexesInlineArray(place))
                {
                    LoadAddress(place);
                    emitValue();
                    Emit(new Operation(OpCode.Stack, StackOp.StoreMem));
                    return;
                }

                var parent = place.Parent;
                switch (place.Projections[^1])
                {
                    case DerefProjection:
                        LoadPlace(parent);
                        emitValue();
                        Emit(new Operation(OpCode.Stack, StackOp.StoreMem));
                        break;
                    case FieldProjection field:
                        LoadAddress(parent);
                        emitValue();
                        Emit(new Operation(OpCode.StoreField, FieldToken(function.TypeOf(parent, module), field)));
                        break;
                    case IndexProjection index:
                        LoadPlace(parent);
                        LoadLocal(index.Local);
                        emitValue();
                        Emit(new Operation(OpCode.Array, ArrayOp.Store));
                        break;
                }
            }

            private void LoadLocal(int local) =>
                Emit(function.IsArgument(local)
                    ? new Operation(OpCode.LoadArgument, local - 1)
                    : new Operation(OpCode.LoadLocal, IlLocal(local)));

            /// <summary>IR local → IL local slot (arguments excluded).</summary>
            private int IlLocal(int local) => local == 0 ? 0 : local - function.ArgumentCount;

            private int FieldToken(IrType owner, FieldProjection field) => owner is StructType st
                ? tokens.Field(st, field.FieldIndex)
                : throw new InvalidOperationException($"{function.Name}: field access on non-struct type {owner}.");

            // ── Emission ───────────────────────────────────────────

            private void Emit(Operation operation) => operations.Add(operation);

            private void EmitBranch(OpCode opCode, params int[] blocks)
            {
                for (int i = 0; i < blocks.Length; i++)
                    fixups.Add((operations.Count, i, blocks[i]));
                operations.Add(new Operation(opCode, new long[blocks.Length]));
            }

            /// <summary>Intra-block jump targets (used by drop-flag guards).</summary>
            private int NewLabel()
            {
                labels.Add(-1);
                return labels.Count - 1;
            }

            private void MarkLabel(int label) => labels[label] = operations.Count;

            private void EmitBranchToLabel(OpCode opCode, int label)
            {
                labelFixups.Add((operations.Count, label));
                operations.Add(new Operation(opCode, 0));
            }

            private IEnumerable<Operation> PatchedOperations()
            {
                var patched = operations.ToArray();
                foreach (var group in fixups.GroupBy(f => f.Operation))
                {
                    var operation = patched[group.Key];
                    long[] operands = [.. operation.Operands];
                    foreach (var fixup in group)
                        operands[fixup.Operand] = blockStarts[fixup.Block];
                    patched[group.Key] = Operation.FromCode(operation.OpCode, operation.SubOp, operands);
                }
                foreach (var (index, label) in labelFixups)
                    patched[index] = Operation.FromCode(patched[index].OpCode, patched[index].SubOp, labels[label]);
                return patched;
            }
        }
    }
}

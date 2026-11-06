using Larva.IR;
using Larva.IR.Analysis;
using Larva.IR.Lowering;
using static Larva.Wasm.WasmCodegen;

namespace Larva.Wasm
{
    /// <summary>Compiles one function: frame slots in linear memory, a dispatch loop over the blocks.</summary>
    internal sealed class WasmFunctionCompiler
    {
        // Wasm locals: the two parameters, then the frame pointer, the dispatch state, an address, the caller's stack
        // pointer and two i64 scratch values.
        private const int Args = 0, Ret = 1, Fp = 2, State = 3, Addr = 4, Saved = 5, A = 6, B = 7;
        private static readonly ValType[] Locals = [ValType.I32, ValType.I32, ValType.I32, ValType.I32, ValType.I64, ValType.I64];

        private readonly WasmCodegen cg;
        /// <summary>The GC of the function's domain (the program's, with one domain).</summary>
        private readonly GcConfig gc;
        private bool Has(GcCapabilities capability) => (gc.Capabilities & capability) != 0;
        private readonly Code c = new();
        private readonly IrFunction function;
        private readonly IrModule module;
        private readonly TypeContext context;
        private readonly MoveAnalysis moves;
        private readonly BoundsAnalysis? bounds;

        private readonly int[] slots;
        private readonly Dictionary<Place, int> flags = [];
        private readonly Dictionary<Location, int> pins = [];
        private readonly List<(int Offset, int Size)> temps = [];
        private int tempCursor;
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

        public (ValType[] Locals, byte[] Code) Compile()
        {
            for (int l = 0; l < function.Locals.Length; l++)
            {
                var type = function.Locals[l].Type;
                var layout = type.IsUnit ? null : Layouts.Of(type, module);
                slots[l] = layout is null ? 0 : Slot(layout.Size, Math.Min(16, Math.Max(8, layout.Align)));
                cg.Shape(type);
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

            // Body: the dispatch loop, one nested block per state.
            c.Loop();
            for (int s = 0; s < states; s++)
                c.Block();
            c.LocalGet(State);
            c.BrTable(states);
            for (int b = 0; b < blocks; b++)
            {
                c.End();
                caseIndex = b;
                depth = 0;
                var block = function.Blocks[b];
                for (int s = 0; s < block.Statements.Length; s++)
                {
                    current = new Location(b, s);
                    tempCursor = 0;
                    Statement(block.Statements[s]);
                }
                current = new Location(b, block.Statements.Length);
                tempCursor = 0;
                Terminator(block.Terminator);
            }
            foreach (var (target, landing) in clauseLandings)
            {
                c.End();
                caseIndex = landing;
                depth = 0;
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
                JumpTo(target);
            }
            if (needsCleanup)
            {
                c.End();
                caseIndex = cleanupLanding;
                depth = 0;
                tempCursor = 0;
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
            c.End();
            c.Unreachable();
            var body = c.ToArray();

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
                Copy(p, () => { p.LocalGet(Fp); p.I32(slots[l]); p.I32Add(); }, () => { p.LocalGet(Args); p.I32(offset); p.I32Add(); }, size);
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
            var prologue = p.ToArray();
            return (Locals, [.. prologue, .. body]);
        }

        // ── Control ────────────────────────────────────────────

        /// <summary>Continues at another state (block or landing) through the dispatch loop.</summary>
        private void JumpTo(int state)
        {
            c.I32(state);
            c.LocalSet(State);
            c.Br(states - 1 - caseIndex + depth);
        }

        private void OpenIf()
        {
            c.If();
            depth++;
        }

        private void CloseIf()
        {
            c.End();
            depth--;
        }

        /// <summary>Returns to the caller: the result to where it asked, the stack pointer back.</summary>
        private void Leave(bool withResult = false)
        {
            if (withResult)
            {
                var (kind, size, _) = cg.Shape(function.ReturnType);
                if (kind != Kind.Unit)
                    Copy(c, () => c.LocalGet(Ret), () => { c.LocalGet(Fp); c.I32(slots[0]); c.I32Add(); }, size);
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
                    if (Has(GcCapabilities.RefCounting) && type.Unoptional() is GcType)
                    {
                        c.I32(cg.GlobalAddress[store.Global]);
                        c.I64Load();
                        c.I32WrapI64();
                        c.Call(cg.Runtime("larva_gc_release")); // the old value loses its owner
                    }
                    Copy(c, () => c.I32(cg.GlobalAddress[store.Global]), () => FrameAddress(value), size);
                    break;
                }
                case FreeStatement free:
                {
                    int pointer = Load(free.Pointer);
                    c.LocalGet(Fp);
                    c.I64Load(pointer);
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
            ReleaseOwned(target, moved);
            Store(target, result);
            UpdateFlags(moved, target);
        }

        /// <summary>An intrinsic: its arguments on the wasm stack, then the runtime's import (input leaves an i64 there).</summary>
        private void Intrinsic(IntrinsicRvalue intrinsic)
        {
            var info = Intrinsics.Find(intrinsic.Name)!;
            var slots = intrinsic.Arguments.Select(Operand).ToArray();
            for (int i = 0; i < slots.Length; i++)
            {
                var type = info.Parameters[i];
                c.LocalGet(Fp);
                if (type == IrType.Bool)
                    c.I32Load8U(slots[i]);
                else
                {
                    c.I64Load(slots[i]);
                    if (type is ReferenceType)
                        c.I32WrapI64();
                }
            }
            c.Call(cg.Runtime(info.Symbol));
        }

        private void FrameAddress(int offset)
        {
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
            var type = Address(target);
            var (kind, size, _) = cg.Shape(type);
            if (kind == Kind.Unit)
                return;
            Copy(c, () => c.LocalGet(Addr), () => FrameAddress(value), size);
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
                    c.LocalGet(Fp);
                    c.I64Load(condition);
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
                        c.LocalGet(Fp);
                        c.I64Load(value);
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
                    Copy(c, () => c.LocalGet(Addr), () => FrameAddress(value), cg.Shape(type).Size);
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
                Copy(c, () => FrameAddress(at), () => FrameAddress(temp), size);
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
                c.LocalGet(Fp);
                c.I64Load(calleeTemp);
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
            c.LocalGet(Fp);
            c.LocalGet(A);
            c.I64Store(temp);
        }

        private void LoadTemp(int temp)
        {
            c.LocalGet(Fp);
            c.I64Load(temp);
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
                    int temp = Temp();
                    c.LocalGet(Fp);
                    c.I64(Canonical(constant));
                    c.I64Store(temp);
                    return temp;
                }
                default:
                    throw new WasmBackendException($"operand {operand}");
            }
        }

        private int Load(Place place)
        {
            var type = Address(place);
            if (type is ArrayType)
                throw new WasmBackendException($"`{place}` is an unsized array; index it or take Len");
            var (kind, size, signed) = cg.Shape(type);
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
                    int temp = Temp();
                    c.LocalGet(Fp);
                    c.I64(Layouts.Of(size.Type, module)!.Size);
                    c.I64Store(temp);
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
                    Copy(c, () => c.LocalGet(Addr), () => FrameAddress(value), cg.Shape(valueType).Size);
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
                    c.LocalGet(Fp);
                    LoadTemp(length);
                    c.I32(header);
                    c.I32(stride);
                    c.I32(managed);
                    c.Call(cg.Runtime("larva_array_new"));
                    c.I64ExtendI32U();
                    c.I64Store(length);
                    return length;
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
                    int temp = Temp();
                    c.LocalGet(Fp);
                    LoadTemp(value);
                    c.I64Eqz();
                    c.I64ExtendI32U();
                    c.I64Store(temp);
                    return temp;
                }
                case GlobalRvalue global:
                {
                    var type = module.Globals[global.Global].Type;
                    int size = Layouts.Of(type, module)!.Size;
                    int temp = Temp(size);
                    var (kind, _, signed) = cg.Shape(type);
                    if (kind == Kind.Scalar)
                    {
                        c.LocalGet(Fp);
                        c.I32(cg.GlobalAddress[global.Global]);
                        c.I64LoadSized(size, signed);
                        c.I64Store(temp);
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
                    int temp = Temp();
                    c.LocalGet(Fp);
                    c.LocalGet(Addr);
                    c.I64Load();
                    c.I64Store(temp);
                    return temp;
                }
                case IntrinsicRvalue intrinsic:
                {
                    int temp = Temp();
                    c.LocalGet(Fp);
                    Intrinsic(intrinsic);
                    c.I64Store(temp);
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
            void LoadF(int temp)
            {
                c.LocalGet(Fp);
                if (f32) c.F32Load(temp); else c.F64Load(temp);
            }
            switch (op)
            {
                case BinaryOp.Add or BinaryOp.Sub or BinaryOp.Mul or BinaryOp.Div:
                    c.LocalGet(Fp);
                    LoadF(left);
                    LoadF(right);
                    int index = op switch { BinaryOp.Add => 0, BinaryOp.Sub => 1, BinaryOp.Mul => 2, _ => 3 };
                    c.Raw((byte)((f32 ? 0x92 : 0xA0) + index));
                    if (f32) c.F32Store(left); else c.F64Store(left);
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
                    c.LocalGet(Fp);
                    if (fromSize == 4) c.F32Load(value); else c.F64Load(value);
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
                c.LocalGet(Fp);
                c.LocalGet(Fp);
                if (fromSize == 4)
                {
                    c.F32Load(value);
                    c.Raw(0xBB);                // f64.promote_f32
                    c.F64Store(value);
                }
                else
                {
                    c.F64Load(value);
                    c.Raw(0xB6);                // f32.demote_f64
                    c.F32Store(value);
                }
                return value;
            }
            if (fromKind == Kind.Float)
            {
                c.LocalGet(Fp);
                if (fromSize == 4) c.F32Load(value); else c.F64Load(value);
                // Saturating truncation (no trap on NaN or overflow).
                c.Prefixed(to == IrType.U64 ? (fromSize == 4 ? 5u : 7u) : (fromSize == 4 ? 4u : 6u));
                c.LocalSet(A);
                Canonicalize(to);
                StoreA(value);
                return value;
            }
            if (toKind == Kind.Float)
            {
                c.LocalGet(Fp);
                LoadTemp(value);
                bool unsigned = from == IrType.U64;
                c.Raw(toSize == 4 ? (unsigned ? (byte)0xB5 : (byte)0xB4) : (unsigned ? (byte)0xBA : (byte)0xB9));
                if (toSize == 4) c.F32Store(value); else c.F64Store(value);
                return value;
            }
            LoadTemp(value);
            c.LocalSet(A);
            Canonicalize(to);
            StoreA(value);
            return value;
        }

        /// <summary>Makes local <see cref="A"/> the canonical form of <paramref name="type"/> (truncated, then extended).</summary>
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
            foreach (var (path, release, _) in Owned(function.TypeOf(place, module), []))
            {
                var resource = new Place(place.Local, [.. place.Projections, .. path]);
                if (movedNow.Any(m => m.IsPrefixOf(resource) || resource.IsPrefixOf(m)))
                    continue;
                bool guarded = flags.TryGetValue(resource, out int flag);
                if (guarded)
                {
                    c.LocalGet(Fp);
                    c.I32Load8U(flag);
                    OpenIf();
                }
                int saved = tempCursor;
                int handle = Load(resource);
                LoadTemp(handle);
                c.I32WrapI64();
                c.Call(cg.Runtime(release == Release.FreeBox ? "larva_free" : "larva_gc_release"));
                tempCursor = saved;
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
            c.LocalGet(Fp);
            LoadTemp(value);
            c.I64Store(slot);
            LoadTemp(value);
            c.I32WrapI64();
            c.Call(cg.Runtime("larva_pin"));
        }
    }
}

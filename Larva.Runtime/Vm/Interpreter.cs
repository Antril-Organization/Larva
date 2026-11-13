using System.Numerics;
using Larva.IL;
using Larva.IR;
using Larva.IR.Lowering;
using Larva.IR.Reflection;
using Larva.IR.Text;

namespace Larva.Runtime.Vm
{
    public sealed record InterpreterOptions
    {
        /// <summary>Collect at every safepoint (tracing GCs) — slow, but flushes out missing roots immediately.</summary>
        public bool GcStress { get; init; }

        /// <summary>Tracing GCs: collect at the next safepoint once this many objects were allocated since the last collection.</summary>
        public int CollectEvery { get; init; } = 64;

        /// <summary>Executed operations before giving up (protects against runaway loops).</summary>
        public long StepLimit { get; init; } = 50_000_000;

        /// <summary>
        /// Open frames before a call fails with a "call depth" <see cref="VmException"/> (the VM's stack overflow). Calls do
        /// not use the host stack, so the limit bounds memory only: every frame keeps its slots in the heap.
        /// </summary>
        public int MaxCallDepth { get; init; } = 100_000;

        /// <summary>Where the runtime's print intrinsics write (the console when null).</summary>
        public TextWriter? Output { get; init; }

        /// <summary>Where <c>read.line</c> reads (the console when null).</summary>
        public TextReader? Input { get; init; }

        /// <summary>The command line <c>args.*</c> reads (without the program's name).</summary>
        public string[] Arguments { get; init; } = [];
    }

    public sealed class VmStats
    {
        public long Steps { get; internal set; }
        public int Calls { get; internal set; }
        public int Safepoints { get; internal set; }
        public int Collections { get; internal set; }
        public int Reclaimed { get; internal set; }
        public int Finalized { get; internal set; }
        public int Retains { get; internal set; }
        public int Releases { get; internal set; }
        public int Barriers { get; internal set; }
        public int Pins { get; internal set; }

        /// <summary>Turn handoffs between threads at safepoints (not the ones made to wait in the runtime).</summary>
        public int Turns { get; internal set; }

        public override string ToString() =>
            $"steps {Steps}, calls {Calls}, safepoints {Safepoints}, collections {Collections} (reclaimed {Reclaimed}), " +
            $"retain/release {Retains}/{Releases}, barriers {Barriers}, pins {Pins}, turns {Turns}";
    }

    /// <summary>
    /// Executes Larva IL. Values carry types (see <see cref="Value"/>), every local and argument lives in addressable memory
    /// (<see cref="Heap"/>), shared generic code resolves its open types against the witnesses it receives, and the GC mode the
    /// module was compiled for is honored: tracing collections at safepoints using the precise root maps, reference counting
    /// through <c>gc.retain</c>/<c>gc.release</c>, arena regions through <c>gc.renter</c>/<c>gc.rexit</c>.
    /// </summary>
    public sealed partial class Interpreter
    {
        private readonly IlModule il;
        private readonly IrModule shapes;
        private readonly InterpreterOptions options;
        private readonly List<Frame> callStack = [];
        private readonly Stack<OpenRegion> regions = new();
        private int allocatedSinceCollect;

        public RuntimeModule Runtime { get; }
        public Heap Heap { get; }
        public VmStats Stats { get; } = new();

        public Interpreter(IlModule il, RuntimeModule? runtime = null, InterpreterOptions? options = null)
        {
            this.il = il;
            this.options = options ?? new InterpreterOptions();
            Runtime = runtime ?? RuntimeModule.From(il.Metadata);
            shapes = Runtime.Shapes;
            Heap = new Heap(shapes);
        }

        private bool Has(GcCapabilities capability) => (il.Gc.Capabilities & capability) != 0;

        // ── Entry points ───────────────────────────────────────────

        public int FunctionIndex(string name)
        {
            for (int i = 0; i < il.Functions.Length; i++)
            {
                if (il.Functions[i].Name == name)
                    return i;
            }
            throw new VmException($"no function `{name}`");
        }

        /// <summary>Calls a non-generic function with .NET arguments and converts the result back (see <see cref="ToObject"/>).</summary>
        public object? Run(string function, params object[] arguments)
        {
            int index = FunctionIndex(function);
            try
            {
                return ToObject(Call(index, [], [.. arguments.Select(FromObject)]));
            }
            catch (LarvaException e)
            {
                throw new UnhandledException(e.Value);
            }
        }

        private NativeCalls? nativeCalls;

        public Value Call(int function, IrType[] typeArguments, Value[] arguments)
        {
            var callee = il.Functions[function];
            if (arguments.Length != callee.ArgumentCount || typeArguments.Length != callee.TypeParameterCount)
                throw new VmException($"`{callee.Name}` takes {callee.TypeParameterCount} witness(es) and {callee.ArgumentCount} argument(s)");
            if (callee.Native is { } nativeCall)
            {
                // A call into C: made here (the body is what native code runs).
                Stats.Calls++;
                nativeCalls ??= new NativeCalls(this, Heap);
                return nativeCalls.Call(callee, nativeCall, [.. callee.ArgumentTypes.Select(t => il.Types[t])], il.Types[callee.LocalTypes[0]], arguments);
            }
            return Execute(Enter(function, callee, typeArguments, arguments));
        }

        /// <summary>Opens an activation: its slots, its arguments written, pushed on the call stack.</summary>
        private Frame Enter(int function, IlFunction callee, IrType[] typeArguments, Value[] arguments)
        {
            if (callStack.Count >= options.MaxCallDepth)
                throw new VmException($"call depth limit of {options.MaxCallDepth} frames reached calling `{callee.Name}` (stack overflow)");
            var frame = new Frame(this, function, callee, typeArguments) { RegionDepth = regions.Count };
            try
            {
                for (int a = 0; a < arguments.Length; a++)
                    frame.StoreArgument(a, arguments[a]);
            }
            catch
            {
                frame.Release();
                throw;
            }
            callStack.Add(frame);
            Stats.Calls++;
            return frame;
        }

        /// <summary>Closes an activation: the arena regions it left open (when unwinding), its slots, its place on the call stack.</summary>
        private void Leave(Frame frame, bool unwinding)
        {
            if (unwinding)
            {
                while (regions.Count > frame.RegionDepth)
                    ExitRegion();
            }
            if (callStack.Count > 0 && callStack[^1] == frame)
                callStack.RemoveAt(callStack.Count - 1);
            frame.Release();
        }

        // ── Execution ──────────────────────────────────────────────

        /// <summary>
        /// Runs <paramref name="entry"/> to its return. Calls between interpreted functions do not recurse on the host stack:
        /// a call pushes the callee's frame and the loop continues in it, a return pops back to the caller, and an exception
        /// unwinds frame by frame until a handler takes it (or it leaves <paramref name="entry"/>, to the host caller). The
        /// host stack is only used again by re-entrant calls (finalizers, intrinsics, callbacks from native code).
        /// </summary>
        private Value Execute(Frame entry)
        {
            int entryIndex = callStack.Count - 1; // frames below it belong to the host callers of this run
            var frame = entry;
            try
            {
                while (true)
                {
                    var ops = frame.Function.Body.Operations;
                    var stack = frame.Stack;
                    if (++Stats.Steps > options.StepLimit)
                        throw new VmException($"step limit reached in `{frame.Function.Name}`");
                    if (frame.Pc >= ops.Length)
                        throw new VmException($"`{frame.Function.Name}` ran past its last operation");

                    var op = ops[frame.Pc];
                    long a0 = op.Operands.Length > 0 ? op.Operands[0] : 0;
                    try
                    {
                    switch (op.OpCode)
                    {
                        case OpCode.LoadLocal:
                            stack.Add(frame.LoadLocal((int)a0));
                            break;
                        case OpCode.StoreLocal:
                            frame.StoreLocal((int)a0, Pop(stack));
                            break;
                        case OpCode.LoadArgument:
                            stack.Add(frame.LoadArgument((int)a0));
                            break;
                        case OpCode.Const:
                            stack.Add(Value.FromInt(a0, null));
                            break;
                        case OpCode.Add:
                        case OpCode.Sub:
                        case OpCode.Mul:
                        {
                            var right = stack.Pop();
                            var left = stack.Pop();
                            if (left.Kind == ValueKind.Int && right.Kind == ValueKind.Int && (left.Type ?? right.Type) is PrimitiveType { Kind: >= Primitive.I8 and <= Primitive.U64 } small
                                && left.TryBits(out long x) && right.TryBits(out long y))
                            {
                                // Integers of 64 bits or less: wrapping long arithmetic (congruent modulo 2^64, then wrapped to the type).
                                stack.Add(Value.FromInt(op.OpCode switch { OpCode.Add => unchecked(x + y), OpCode.Sub => unchecked(x - y), _ => unchecked(x * y) }, small));
                                break;
                            }
                            stack.Add(Arithmetic(op.OpCode, left, right));
                            break;
                        }
                        case OpCode.Compare when (Condition)op.SubOp != Condition.Ord:
                        {
                            var b = Pop(stack);
                            var a = Pop(stack);
                            stack.Add(Value.FromBool(Compare((Condition)op.SubOp, a, b)));
                            break;
                        }
                        case OpCode.Call:
                            Safepoint(frame);
                            if (CallFrom(frame, (int)a0) is { } called)
                            {
                                frame = called;
                                continue;
                            }
                            break;
                        case OpCode.CallExtended when (CallOp)op.SubOp == CallOp.Indirect:
                        {
                            var target = Pop(stack);
                            Safepoint(frame);
                            int function = target.Kind == ValueKind.Function ? target.Function : FunctionOf(Heap.AsInteger(target));
                            if (CallFrom(frame, function) is { } calledIndirect)
                            {
                                frame = calledIndirect;
                                continue;
                            }
                            break;
                        }
                        case OpCode.Return:
                        {
                            var resultType = frame.LocalTypes[0];
                            var result = resultType.IsUnit ? Value.UnitValue : Heap.Coerce(Pop(stack), resultType);
                            Leave(frame, unwinding: false);
                            if (frame == entry)
                                return result;
                            frame = callStack[^1];
                            if (!resultType.IsUnit)
                                frame.Stack.Add(result);
                            break; // past the caller's call operation
                        }
                        case OpCode.Jump:
                            frame.Pc = (int)a0;
                            continue;
                        case OpCode.BranchTrue:
                        case OpCode.BranchFalse:
                            if (Pop(stack).IsTrue == (op.OpCode == OpCode.BranchTrue))
                            {
                                frame.Pc = (int)a0;
                                continue;
                            }
                            break;
                        case OpCode.LoadField:
                        {
                            var (owner, index, offset, type) = Field(frame, a0);
                            var value = Heap.Coerce(Pop(stack), owner);
                            stack.Add(Heap.DecodeBytes(type, value.Bytes.AsSpan(offset, Heap.LayoutOf(type).Size)));
                            break;
                        }
                        case OpCode.StoreField:
                        {
                            var (_, _, offset, type) = Field(frame, a0);
                            var value = Pop(stack);
                            Heap.Write(type, Heap.AsAddress(Pop(stack)).Plus(offset), value);
                            break;
                        }
                        case OpCode.Duplicate:
                            stack.Add(stack[^1]);
                            break;
                        case OpCode.Switch:
                            var popped = Pop(stack);
                            var selector = popped.TryLong(out long chosen) ? chosen : Heap.AsInteger(popped);
                            if (selector >= 0 && selector < op.Operands.Length)
                            {
                                frame.Pc = (int)op.Operands[(int)selector];
                                continue;
                            }
                            break;
                        case OpCode.Branch:
                        {
                            var b = Pop(stack);
                            if (Compare((Condition)op.SubOp, Pop(stack), b))
                            {
                                frame.Pc = (int)a0;
                                continue;
                            }
                            break;
                        }
                        // The families straight to their (small) handlers: a call through one large method zeroes all of its
                        // locals every time.
                        case OpCode.Stack when (StackOp)op.SubOp == StackOp.Pop:
                            stack.Pop();
                            break;
                        case OpCode.Stack:
                            StackOperation(frame, op);
                            break;
                        case OpCode.Alu:
                            Alu(stack, (AluOp)op.SubOp);
                            break;
                        case OpCode.Float:
                            Fpu(stack, (FloatOp)op.SubOp);
                            break;
                        case OpCode.Convert:
                            stack.Add(Convert(stack.Pop(), (ConvertOp)op.SubOp, frame, a0));
                            break;
                        case OpCode.Array:
                            ArrayOperation(frame, op);
                            break;
                        case OpCode.Gc:
                            GcOperation(frame, op);
                            break;
                        case OpCode.Generic:
                            GenericOperation(frame, op);
                            break;
                        case OpCode.System when (SystemOp)op.SubOp is SystemOp.Safepoint or SystemOp.Poll:
                            Safepoint(frame);
                            break;
                        default:
                            ExecuteFamily(frame, op);
                            break;
                    }
                    }
                    catch (LarvaException e)
                    {
                        // The nearest handler up the interpreted frames takes it; frames without one are unwound (their
                        // regions closed, their slots freed). A caller's pc still points at its call operation, so its
                        // handlers covering the call are found.
                        while (!Handle(frame, e.Value))
                        {
                            Leave(frame, unwinding: true);
                            if (frame == entry)
                                throw;
                            frame = callStack[^1];
                        }
                        continue;
                    }
                    frame.Pc++;
                }
            }
            catch (Exception) when (callStack.Count > entryIndex)
            {
                // A VM error (or a Larva exception from a finalizer while unwinding) leaves this run: release its frames.
                while (callStack.Count > entryIndex)
                    Leave(callStack[^1], unwinding: false);
                throw;
            }
        }

        /// <summary>Finds a handler protecting the current operation whose type matches; enters it with the exception pushed.</summary>
        private bool Handle(Frame frame, Value exception)
        {
            foreach (var handler in frame.Function.Handlers)
            {
                if (frame.Pc < handler.Start || frame.Pc >= handler.End)
                    continue;
                if (handler.CatchType is int token && frame.Resolve(token) != exception.Type)
                    continue;
                frame.Stack.Clear();
                frame.Stack.Add(exception);
                frame.Exception = exception;
                frame.Pc = handler.Handler;
                return true;
            }
            return false;
        }

        private void ExecuteFamily(Frame frame, Operation op)
        {
            var stack = frame.Stack;
            long a0 = op.Operands.Length > 0 ? op.Operands[0] : 0;
            switch (op.OpCode)
            {
                case OpCode.Compare:
                {
                    var b = Pop(stack);
                    var a = Pop(stack);
                    var condition = (Condition)op.SubOp;
                    stack.Add(condition == Condition.Ord
                        ? Value.FromInt(Ordinal(a, b), IrType.I32)
                        : Value.FromBool(Compare(condition, a, b)));
                    break;
                }
                case OpCode.Object:
                    switch ((ObjectOp)op.SubOp)
                    {
                        case ObjectOp.LoadFieldAddress:
                            var (_, _, offset, type) = Field(frame, a0);
                            stack.Add(Value.FromPointer(Heap.AsAddress(Pop(stack)).Plus(offset), IrType.RefMut(type)));
                            break;
                        case ObjectOp.SizeOf:
                            stack.Add(Value.FromInt(Heap.LayoutOf(frame.Resolve(a0)).Size, IrType.U64));
                            break;
                        default:
                            throw Unsupported(op);
                    }
                    break;
                case OpCode.Literal when (LiteralOp)op.SubOp == LiteralOp.SetGlobal:
                {
                    var global = il.Globals[a0];
                    Heap.Write(global.Type, GlobalAddress(a0), Pop(stack));
                    break;
                }
                case OpCode.Literal:
                    stack.Add((LiteralOp)op.SubOp switch
                    {
                        LiteralOp.Null => Value.FromPointer(default, null),
                        LiteralOp.True => Value.FromBool(true),
                        LiteralOp.False => Value.FromBool(false),
                        LiteralOp.Data => StaticArray(a0),
                        LiteralOp.Global => Heap.Read(il.Globals[a0].Type, GlobalAddress(a0)),
                        LiteralOp.Zero => Heap.DecodeBytes(frame.Resolve(a0), new byte[Heap.LayoutOf(frame.Resolve(a0)).Size]),
                        _ => Constant(il.Constants[a0]),
                    });
                    break;
                case OpCode.CallExtended:
                    switch ((CallOp)op.SubOp)
                    {
                        case CallOp.LoadFunction:
                            stack.Add(Value.FromFunction((int)a0));
                            break;
                        default:
                            throw Unsupported(op);
                    }
                    break;
                case OpCode.System:
                    switch ((SystemOp)op.SubOp)
                    {
                        case SystemOp.Nop or SystemOp.Break or SystemOp.Debug:
                            break;
                        case SystemOp.Trap:
                            throw new VmTrapException(a0);
                        case SystemOp.Safepoint or SystemOp.Poll:
                            Safepoint(frame);
                            break;
                        case SystemOp.Intrinsic:
                            RunIntrinsic(frame, (int)a0);
                            break;
                        default:
                            throw Unsupported(op);
                    }
                    break;
                case OpCode.Exception:
                    switch ((ExceptionOp)op.SubOp)
                    {
                        case ExceptionOp.Throw:
                            throw new LarvaException(Pop(stack));
                        case ExceptionOp.Rethrow:
                            throw new LarvaException(frame.Exception ?? throw new VmException("eh.rethrow outside an exception handler"));
                        default:
                            throw Unsupported(op);
                    }
                default:
                    throw Unsupported(op);
            }
        }

        private void StackOperation(Frame frame, Operation op)
        {
            var stack = frame.Stack;
            long a0 = op.Operands.Length > 0 ? op.Operands[0] : 0;
            switch ((StackOp)op.SubOp)
            {
                case StackOp.CheckNull:
                    if (Heap.AsAddress(stack[^1]).IsNull)
                        throw new VmException("null reference: a `?` value was used without a value");
                    break;
                case StackOp.IsNull:
                    stack.Add(Value.FromBool(Heap.AsAddress(Pop(stack)).IsNull));
                    break;
                case StackOp.Pop:
                    Pop(stack);
                    break;
                case StackOp.Swap:
                    (stack[^1], stack[^2]) = (stack[^2], stack[^1]);
                    break;
                case StackOp.LoadMem:
                {
                    var pointer = Pop(stack);
                    stack.Add(pointer switch
                    {
                        // An array is not copied onto the stack: "the array" stays a reference to it.
                        { Kind: ValueKind.Pointer, Type: { } type } when type.Pointee() is ArrayType => pointer,
                        { Kind: ValueKind.Pointer, Type: { } type } when type.Pointee() is { } pointee => Heap.Read(pointee, pointer.Address),
                        { Kind: ValueKind.Int } => Value.Deferred(Heap.AsAddress(pointer)),
                        _ => throw new VmException($"cannot load through {pointer}"),
                    });
                    break;
                }
                case StackOp.StoreMem:
                {
                    var value = Pop(stack);
                    var pointer = Pop(stack);
                    var type = pointer is { Kind: ValueKind.Pointer, Type: { } t } && t.Pointee() is { } pointee
                        ? pointee
                        : value.Type ?? IrType.U64;
                    Heap.Write(type, Heap.AsAddress(pointer), value);
                    break;
                }
                case StackOp.StoreArgument:
                    frame.StoreArgument((int)a0, Pop(stack));
                    break;
                case StackOp.Move:
                    frame.StoreLocal((int)a0, frame.LoadLocal((int)op.Operands[1]));
                    break;
                case StackOp.LoadLocalAddress:
                    stack.Add(Value.FromPointer(frame.Locals[a0], IrType.RefMut(frame.LocalTypes[a0])));
                    break;
                case StackOp.LoadArgumentAddress:
                    stack.Add(Value.FromPointer(frame.Arguments[a0], IrType.RefMut(frame.ArgumentTypes[a0])));
                    break;
            }
        }

        private void ArrayOperation(Frame frame, Operation op)
        {
            var stack = frame.Stack;
            switch ((ArrayOp)op.SubOp)
            {
                case ArrayOp.New:
                {
                    var element = frame.Resolve(op.Operands[0]);
                    var array = Heap.AllocateArray(AllocationKind.Heap, element, Heap.AsInteger(Pop(stack)));
                    stack.Add(Value.FromPointer(new Address(array.Id, 0), IrType.PtrMut(new ArrayType(element))));
                    break;
                }
                case ArrayOp.Length:
                    stack.Add(Value.FromInt(Heap.ArrayLength(Heap.AsAddress(Pop(stack))), IrType.U64));
                    break;
                case ArrayOp.Load or ArrayOp.LoadElement:
                {
                    var index = Heap.AsInteger(Pop(stack));
                    var address = Heap.Element(Heap.AsAddress(Pop(stack)), index, out var element);
                    stack.Add(Heap.Read(element, address));
                    break;
                }
                case ArrayOp.Store or ArrayOp.StoreElement:
                {
                    var value = Pop(stack);
                    var index = Heap.AsInteger(Pop(stack));
                    var address = Heap.Element(Heap.AsAddress(Pop(stack)), index, out var element);
                    Heap.Write(element, address, value);
                    break;
                }
                case ArrayOp.LoadElementAddress:
                {
                    var index = Heap.AsInteger(Pop(stack));
                    var address = Heap.Element(Heap.AsAddress(Pop(stack)), index, out var element);
                    stack.Add(Value.FromPointer(address, IrType.RefMut(element)));
                    break;
                }
                case ArrayOp.FixedElementAddress:
                {
                    var type = frame.Resolve(op.Operands[0]) as FixedArrayType ?? throw new VmException("ldfixa needs an inline array type");
                    var index = Heap.AsInteger(Pop(stack));
                    var address = Heap.InlineElement(Heap.AsAddress(Pop(stack)), type, index, out var element);
                    stack.Add(Value.FromPointer(address, IrType.RefMut(element)));
                    break;
                }
                default:
                    throw Unsupported(op);
            }
        }

        private void GenericOperation(Frame frame, Operation op)
        {
            var stack = frame.Stack;
            long a0 = op.Operands[0];
            switch ((GenericOp)op.SubOp)
            {
                case GenericOp.Witness:
                    stack.Add(Value.FromWitness(frame.Resolve(a0)));
                    break;
                case GenericOp.TypeArgument:
                    stack.Add(Value.FromWitness(frame.TypeArguments[a0]));
                    break;
                case GenericOp.SizeOf:
                    stack.Add(Value.FromInt(Heap.LayoutOf(frame.Resolve(a0)).Size, IrType.U64));
                    break;
                case GenericOp.Drop:
                    DropValue(Pop(stack), frame.Resolve(a0));
                    break;
            }
        }

        // ── Calls ──────────────────────────────────────────────────

        /// <summary>
        /// Takes a call's witnesses and arguments off the caller's operand stack and opens the callee's frame (the loop in
        /// <see cref="Execute"/> continues in it). Null for a native callee, which ran here: its result is already pushed.
        /// </summary>
        private Frame? CallFrom(Frame caller, int function)
        {
            var callee = il.Functions[function];
            var stack = caller.Stack;
            var arguments = new Value[callee.ArgumentCount];
            for (int a = arguments.Length - 1; a >= 0; a--)
                arguments[a] = Pop(stack);
            var witnesses = callee.TypeParameterCount == 0 ? [] : new IrType[callee.TypeParameterCount];
            for (int w = witnesses.Length - 1; w >= 0; w--)
            {
                var witness = Pop(stack);
                witnesses[w] = witness.Kind == ValueKind.Witness ? witness.Witness! : Runtime.WitnessType((int)Heap.AsInteger(witness));
            }

            if (callee.Native is null)
                return Enter(function, callee, witnesses, arguments);
            var result = Call(function, witnesses, arguments);
            if (!il.Types[callee.LocalTypes[0]].Substitute(witnesses).IsUnit)
                stack.Add(result);
            return null;
        }

        private int FunctionOf(BigInteger tagged)
        {
            ulong raw = (ulong)(tagged & ulong.MaxValue);
            return (raw & Address.FunctionTag) != 0
                ? (int)(raw & ~Address.FunctionTag)
                : throw new VmException($"0x{raw:X} is not a function reference");
        }

        // ── Fields, constants, conversions ─────────────────────────

        private (StructType Owner, int Index, int Offset, IrType Type) Field(Frame frame, long token)
        {
            var field = il.Fields[token];
            var owner = (StructType)field.Owner.Substitute(frame.TypeArguments);
            return (owner, field.Field, Heap.FieldOffsets(owner)[field.Field], Heap.FieldType(owner, field.Field));
        }

        private readonly Dictionary<long, Value> statics = [];
        private readonly Dictionary<long, Address> globals = [];

        /// <summary>The storage of global <paramref name="index"/>, allocated (with its initial value) on first use.</summary>
        private Address GlobalAddress(long index)
        {
            if (globals.TryGetValue(index, out var known))
                return known;
            if (index < 0 || index >= il.Globals.Length)
                throw new VmException($"global {index} does not exist");
            var global = il.Globals[index];
            var address = new Address(Heap.Allocate(AllocationKind.Static, global.Type).Id, 0);
            if (global.Initial is { } initial)
            {
                var value = global.Type.IsFloat ? Value.FromFloatBits(initial, global.Type)
                    : global.Type == IrType.Bool ? Value.FromBool(!initial.IsZero)
                    : Value.FromInt(initial, global.Type);
                Heap.Write(global.Type, address, value);
            }
            return globals[index] = address;
        }

        /// <summary>A <c>&amp;[T]</c> to static array <paramref name="index"/>, materialized (and sealed) on first use.</summary>
        private Value StaticArray(long index)
        {
            if (statics.TryGetValue(index, out var cached))
                return cached;
            if (index < 0 || index >= il.Data.Length)
                throw new VmException($"lit.data {index}: no such static array");
            var data = il.Data[index];
            var array = Heap.AllocateArray(AllocationKind.Static, data.Element, data.Values.Length);
            var start = new Address(array.Id, 0);
            for (int i = 0; i < data.Values.Length; i++)
            {
                var value = data.Element.IsFloat
                    ? Value.FromFloatBits(data.Values[i], data.Element)
                    : Value.FromInt(data.Values[i], data.Element);
                Heap.Write(data.Element, Heap.Element(start, i, out _), value);
            }
            array.Sealed = true;
            return statics[index] = Value.FromPointer(start, data.ReferenceType);
        }

        private static Value Constant(PoolConstant constant) =>
            constant.Type.IsFloat
                ? Value.FromFloatBits(constant.Bits, constant.Type)
                : Value.FromInt(constant.Bits, constant.Type);

        /// <summary>
        /// A float as an integer with Larva's rule for every back end: truncation; a value out of the target's range gives
        /// the nearest limit, NaN gives 0 (LLVM's <c>fptosi.sat</c>), never a wrapped value.
        /// </summary>
        private static BigInteger SaturatedInteger(Value value, PrimitiveType target)
        {
            int bits = target.BitWidth!.Value;
            BigInteger max = target.IsSigned ? (BigInteger.One << (bits - 1)) - 1 : (BigInteger.One << bits) - 1;
            BigInteger min = target.IsSigned ? -max - 1 : BigInteger.Zero;
            if (!Numbers.IsWide(value.Type))
            {
                double d = value.Float;
                if (double.IsNaN(d))
                    return BigInteger.Zero;
                if (double.IsPositiveInfinity(d))
                    return max;
                if (double.IsNegativeInfinity(d))
                    return min;
            }
            var truncated = Heap.AsInteger(value);
            return truncated > max ? max : truncated < min ? min : truncated;
        }

        /// <summary><see cref="SaturatedInteger"/> for a double and an integer type of 64 bits or less, without a BigInteger.</summary>
        private static Value SaturatedSmall(double value, PrimitiveType target)
        {
            const double TwoTo63 = 9223372036854775808.0, TwoTo64 = 18446744073709551616.0;
            if (double.IsNaN(value))
                return Value.FromInt(0L, target);
            double truncated = Math.Truncate(value);
            int bits = target.BitWidth!.Value;
            if (target.Kind == Primitive.U64)
            {
                ulong unsigned = truncated <= 0 ? 0 : truncated >= TwoTo64 ? ulong.MaxValue : (ulong)truncated;
                return Value.FromInt((long)unsigned, target);
            }
            long max = target.IsSigned ? (bits == 64 ? long.MaxValue : (1L << (bits - 1)) - 1) : (1L << bits) - 1;
            long min = target.IsSigned ? -max - 1 : 0;
            long whole = truncated >= TwoTo63 ? long.MaxValue : truncated < -TwoTo63 ? long.MinValue : (long)truncated;
            return Value.FromInt(Math.Clamp(whole, min, max), target);
        }

        /// <summary><c>conv.bitcast</c> between 32/64-bit floats and integers of 64 bits or less, without a BigInteger (null otherwise).</summary>
        private static Value? FastBitCast(Value value, IrType to)
        {
            long bits;
            if (value.Kind == ValueKind.Float)
            {
                switch (value.Type)
                {
                    case PrimitiveType { Kind: Primitive.F64 }: bits = BitConverter.DoubleToInt64Bits(value.Float); break;
                    case PrimitiveType { Kind: Primitive.F32 }: bits = BitConverter.SingleToUInt32Bits((float)value.Float); break;
                    default: return null;
                }
            }
            else if (value.Kind is ValueKind.Int or ValueKind.Bool && value.TryBits(out long raw))
            {
                bits = raw;
            }
            else
            {
                return null;
            }
            return to switch
            {
                PrimitiveType { Kind: Primitive.F64 } => Value.FromFloat(BitConverter.Int64BitsToDouble(bits), to),
                PrimitiveType { Kind: Primitive.F32 } => Value.FromFloat(BitConverter.UInt32BitsToSingle((uint)bits), to),
                PrimitiveType p when Numbers.SmallInteger(p.Kind) => Value.FromInt(bits, to),
                _ => null,
            };
        }

        private Value Convert(Value value, ConvertOp op, Frame frame, long operand)
        {
            switch (op)
            {
                case ConvertOp.Conv or ConvertOp.Cast:
                    var target = frame.Resolve(operand);
                    if (target is PrimitiveType { IsInteger: true } integer && value.Kind == ValueKind.Float)
                    {
                        return Numbers.SmallInteger(integer.Kind) && !Numbers.IsWide(value.Type)
                            ? SaturatedSmall(value.Float, integer)
                            : Value.FromInt(SaturatedInteger(value, integer), target);
                    }
                    return Heap.Coerce(value, target);
                case ConvertOp.BitCast:
                    var to = frame.Resolve(operand);
                    if (FastBitCast(value, to) is { } cast)
                        return cast;
                    var bits = value.Kind == ValueKind.Float ? value.FloatBits : Heap.AsInteger(value);
                    return to.IsFloat
                        ? Value.FromFloatBits(bits & ((BigInteger.One << to.BitWidth!.Value) - 1), to)
                        : Value.FromInt(bits, to);
                case ConvertOp.SignExtend or ConvertOp.ZeroExtend or ConvertOp.Truncate:
                    int width = (int)operand;
                    if (width is > 0 and <= 64 && value.Kind == ValueKind.Int && Numbers.SmallInteger((value.Type as PrimitiveType)?.Kind ?? Primitive.Unit) && value.TryBits(out long raw))
                    {
                        ulong low = width == 64 ? (ulong)raw : (ulong)raw & ((1UL << width) - 1);
                        long extended = op == ConvertOp.SignExtend && width < 64 && (low >> (width - 1) & 1) != 0 ? (long)low - (1L << width) : (long)low;
                        return Value.FromInt(extended, value.Type);
                    }
                    var masked = Heap.AsInteger(value) & ((BigInteger.One << width) - 1);
                    if (op == ConvertOp.SignExtend && !(masked >> (width - 1)).IsZero)
                        masked -= BigInteger.One << width;
                    return Value.FromInt(masked, value.Type);
                default:
                    throw new VmException($"conv.{op} is not supported by the interpreter");
            }
        }

        // ── Helpers ────────────────────────────────────────────────

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static Value Pop(ValueStack stack) => stack.Pop();

        private static VmException Unsupported(Operation op) =>
            new($"`{op.Mnemonic}` is not supported by the interpreter yet");

        /// <summary>Converts a .NET value into an (untyped where possible) interpreter value; arguments get their types when stored.</summary>
        public static Value FromObject(object value) => value switch
        {
            Value v => v,
            bool b => Value.FromBool(b),
            double d => Value.FromFloat(d, IrType.F64),
            float f => Value.FromFloat(f, IrType.F32),
            BigInteger big => Value.FromInt(big, null),
            long l => Value.FromInt(l, null),
            ulong u => Value.FromInt(u, null),
            int i => Value.FromInt(i, null),
            byte[] bytes => new Value(ValueKind.Struct, null, Bytes: bytes),
            RuntimeObject obj => Value.FromStruct(obj.ToArray(), obj.Type.Type),
            _ => throw new VmException($"cannot pass a {value.GetType().Name} to the interpreter"),
        };

        /// <summary>
        /// Interpreter value → .NET: integers up to 64 bits as <see cref="long"/>/<see cref="ulong"/>, wider as <see cref="BigInteger"/>;
        /// floats as <see cref="double"/>; bool; unit as <c>null</c>; pointers as their packed address; structs as their bytes.
        /// </summary>
        public static object? ToObject(Value value) => value.Kind switch
        {
            ValueKind.Unit => null,
            ValueKind.Bool => value.IsTrue,
            ValueKind.Int => value.Type is PrimitiveType { BitWidth: <= 64 } p
                ? p.IsSigned ? (object)(long)value.Int : (ulong)value.Int
                : value.Type is null ? (long)value.Int : value.Int,
            ValueKind.Float => value.Float,
            ValueKind.Pointer => value.Address.Packed,
            ValueKind.Struct => value.Bytes!.ToArray(), // a copy: slots may share a value's bytes
            _ => value,
        };

        /// <summary>
        /// What every activation of a function shares: which slots have their address taken (<c>ldloca</c>, <c>ldarga</c>)
        /// and so live in memory, and for non-generic code the slot types and the zero value of each held slot.
        /// </summary>
        private sealed class FunctionShape
        {
            public required bool[] AddressedLocals;
            public required bool[] AddressedArguments;
            /// <summary>Null addresses for every slot, shared by the frames that keep all their slots as values.</summary>
            public required Address[] NoLocals;
            public required Address[] NoArguments;
            public IrType[]? LocalTypes;
            public IrType[]? ArgumentTypes;
            public Value[]? LocalZeros;
            public Value[]? ArgumentZeros;
        }

        private FunctionShape?[]? functionShapes;

        private FunctionShape ShapeOf(int index, IlFunction function, IrType[] typeArguments)
        {
            functionShapes ??= new FunctionShape?[il.Functions.Length];
            if (functionShapes[index] is { } known)
                return known;
            var locals = new bool[function.LocalTypes.Length];
            var arguments = new bool[function.ArgumentTypes.Length];
            foreach (var op in function.Body.Operations)
            {
                if (op.OpCode != OpCode.Stack || op.Operands.Length == 0)
                    continue;
                if ((StackOp)op.SubOp == StackOp.LoadLocalAddress && op.Operands[0] < locals.Length)
                    locals[op.Operands[0]] = true;
                else if ((StackOp)op.SubOp == StackOp.LoadArgumentAddress && op.Operands[0] < arguments.Length)
                    arguments[op.Operands[0]] = true;
            }
            var shape = new FunctionShape
            {
                AddressedLocals = locals,
                AddressedArguments = arguments,
                NoLocals = new Address[locals.Length],
                NoArguments = new Address[arguments.Length],
            };
            if (function.TypeParameterCount == 0)
            {
                shape.LocalTypes = [.. function.LocalTypes.Select(t => il.Types[t])];
                shape.ArgumentTypes = [.. function.ArgumentTypes.Select(t => il.Types[t])];
                shape.LocalZeros = Zeros(shape.LocalTypes, locals);
                shape.ArgumentZeros = Zeros(shape.ArgumentTypes, arguments);
            }
            return functionShapes[index] = shape;
        }

        /// <summary>The value an unwritten slot reads as (its zero bytes decoded), for the slots held as values.</summary>
        private Value[] Zeros(IrType[] types, bool[] addressed)
        {
            var zeros = new Value[types.Length];
            for (int i = 0; i < types.Length; i++)
            {
                if (!addressed[i])
                    zeros[i] = Heap.DecodeBytes(types[i], new byte[Heap.SizeOf(types[i])]);
            }
            return zeros;
        }

        /// <summary>
        /// One activation: witnesses, argument and local slots, operand stack and program counter. A slot whose address is
        /// never taken holds its value (coerced to the slot's type, as storing it in memory would); the others live in the
        /// heap, so pointers to them work like any address.
        /// </summary>
        private sealed class Frame
        {
            public readonly IlFunction Function;
            public readonly int Index;
            public readonly IrType[] TypeArguments;
            public readonly IrType[] ArgumentTypes;
            /// <summary>The memory of each argument whose address is taken (null addresses for the ones held as values).</summary>
            public readonly Address[] Arguments;
            public readonly IrType[] LocalTypes;
            /// <summary>The memory of each local whose address is taken (null addresses for the ones held as values).</summary>
            public readonly Address[] Locals;
            private readonly Value[] argumentValues;
            private readonly Value[] localValues;
            public readonly ValueStack Stack = new();
            public int Pc;

            /// <summary>The exception being handled (set when a handler is entered; used by <c>eh.rethrow</c>).</summary>
            public Value? Exception;

            /// <summary>Open arena regions when the frame started; unwinding closes anything above.</summary>
            public int RegionDepth;
            private readonly Interpreter vm;
            private readonly Heap heap;

            /// <summary>Whether any slot lives in the heap (freed when the frame is released).</summary>
            private readonly bool inMemory;

            public Frame(Interpreter vm, int index, IlFunction function, IrType[] typeArguments)
            {
                this.vm = vm;
                heap = vm.Heap;
                Index = index;
                Function = function;
                TypeArguments = typeArguments;
                var shape = vm.ShapeOf(index, function, typeArguments);
                ArgumentTypes = shape.ArgumentTypes ?? [.. function.ArgumentTypes.Select(t => Resolve(t))];
                LocalTypes = shape.LocalTypes ?? [.. function.LocalTypes.Select(t => Resolve(t))];
                argumentValues = shape.ArgumentZeros is { } argumentZeros ? (Value[])argumentZeros.Clone() : vm.Zeros(ArgumentTypes, shape.AddressedArguments);
                localValues = shape.LocalZeros is { } localZeros ? (Value[])localZeros.Clone() : vm.Zeros(LocalTypes, shape.AddressedLocals);
                Arguments = Slots(ArgumentTypes, shape.AddressedArguments, shape.NoArguments);
                Locals = Slots(LocalTypes, shape.AddressedLocals, shape.NoLocals);
                inMemory = !ReferenceEquals(Arguments, shape.NoArguments) || !ReferenceEquals(Locals, shape.NoLocals);
            }

            private Address[] Slots(IrType[] types, bool[] addressed, Address[] none)
            {
                if (Array.IndexOf(addressed, true) < 0)
                    return none; // every slot held as a value: the function's shared array of null addresses (never written)
                var slots = new Address[types.Length];
                for (int i = 0; i < types.Length; i++)
                {
                    if (addressed[i])
                        slots[i] = new Address(heap.Allocate(AllocationKind.Frame, types[i]).Id, 0);
                }
                return slots;
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public Value LoadLocal(int slot) => Locals[slot].IsNull ? localValues[slot] : heap.Read(LocalTypes[slot], Locals[slot]);

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public void StoreLocal(int slot, Value value)
            {
                if (Locals[slot].IsNull)
                    localValues[slot] = heap.Coerce(value, LocalTypes[slot]);
                else
                    heap.Write(LocalTypes[slot], Locals[slot], value);
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public Value LoadArgument(int slot) => Arguments[slot].IsNull ? argumentValues[slot] : heap.Read(ArgumentTypes[slot], Arguments[slot]);

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public void StoreArgument(int slot, Value value)
            {
                if (Arguments[slot].IsNull)
                    argumentValues[slot] = heap.Coerce(value, ArgumentTypes[slot]);
                else
                    heap.Write(ArgumentTypes[slot], Arguments[slot], value);
            }

            /// <summary>The 8 bytes at <paramref name="offset"/> of a slot (where a root map says a handle is).</summary>
            public ulong SlotWord(bool argument, int slot, int offset)
            {
                var memory = argument ? Arguments[slot] : Locals[slot];
                if (!memory.IsNull)
                    return (ulong)Heap.AsLong(heap.Read(IrType.U64, memory.Plus(offset)));
                var value = argument ? argumentValues[slot] : localValues[slot];
                if (value.Kind == ValueKind.Pointer && offset == 0)
                    return value.Address.Packed;
                var bytes = heap.EncodeBytes(argument ? ArgumentTypes[slot] : LocalTypes[slot], value);
                return BitConverter.ToUInt64(bytes, offset);
            }

            /// <summary>Type token, with this frame's type arguments substituted (shared generic code).</summary>
            public IrType Resolve(long token) => vm.il.Types[token].Substitute(TypeArguments);

            public void Release()
            {
                if (!inMemory)
                    return;
                foreach (var slot in Arguments)
                    heap.Free(slot, "frame exit");
                foreach (var slot in Locals)
                    heap.Free(slot, "frame exit");
            }
        }
    }
}

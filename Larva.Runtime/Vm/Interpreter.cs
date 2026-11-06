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

        public override string ToString() =>
            $"steps {Steps}, calls {Calls}, safepoints {Safepoints}, collections {Collections} (reclaimed {Reclaimed}), " +
            $"retain/release {Retains}/{Releases}, barriers {Barriers}, pins {Pins}";
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
        private readonly Stack<int> regions = new();
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

            var frame = new Frame(this, function, callee, typeArguments) { RegionDepth = regions.Count };
            try
            {
                for (int a = 0; a < arguments.Length; a++)
                    Heap.Write(frame.ArgumentTypes[a], frame.Arguments[a], arguments[a]);
                callStack.Add(frame);
                Stats.Calls++;
                return Execute(frame);
            }
            catch (LarvaException)
            {
                // Unwinding out of this frame: close the arena regions it left open.
                while (regions.Count > frame.RegionDepth)
                    ExitRegion(regions.Peek());
                throw;
            }
            finally
            {
                if (callStack.Count > 0 && callStack[^1] == frame)
                    callStack.RemoveAt(callStack.Count - 1);
                frame.Release();
            }
        }

        // ── Execution ──────────────────────────────────────────────

        private Value Execute(Frame frame)
        {
            var ops = frame.Function.Body.Operations;
            var stack = frame.Stack;
            while (true)
            {
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
                        stack.Add(Heap.Read(frame.LocalTypes[a0], frame.Locals[a0]));
                        break;
                    case OpCode.StoreLocal:
                        Heap.Write(frame.LocalTypes[a0], frame.Locals[a0], Pop(stack));
                        break;
                    case OpCode.LoadArgument:
                        stack.Add(Heap.Read(frame.ArgumentTypes[a0], frame.Arguments[a0]));
                        break;
                    case OpCode.Const:
                        stack.Add(Value.FromInt(a0, null));
                        break;
                    case OpCode.Add:
                    case OpCode.Sub:
                    case OpCode.Mul:
                        var right = Pop(stack);
                        stack.Add(Arithmetic(op.OpCode, Pop(stack), right));
                        break;
                    case OpCode.Call:
                        Safepoint(frame);
                        CallFrom(frame, (int)a0);
                        break;
                    case OpCode.Return:
                        var resultType = frame.LocalTypes[0];
                        return resultType.IsUnit ? Value.UnitValue : Heap.Coerce(Pop(stack), resultType);
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
                        var selector = Heap.AsInteger(Pop(stack));
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
                    default:
                        ExecuteFamily(frame, op);
                        break;
                }
                }
                catch (LarvaException e) when (Handle(frame, e.Value))
                {
                    continue;
                }
                frame.Pc++;
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
                case OpCode.Alu:
                    Alu(stack, (AluOp)op.SubOp);
                    break;
                case OpCode.Float:
                    Fpu(stack, (FloatOp)op.SubOp);
                    break;
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
                case OpCode.Convert:
                    stack.Add(Convert(Pop(stack), (ConvertOp)op.SubOp, frame, a0));
                    break;
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
                case OpCode.Array:
                    ArrayOperation(frame, op);
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
                case OpCode.Stack:
                    StackOperation(frame, op);
                    break;
                case OpCode.CallExtended:
                    switch ((CallOp)op.SubOp)
                    {
                        case CallOp.LoadFunction:
                            stack.Add(Value.FromFunction((int)a0));
                            break;
                        case CallOp.Indirect:
                            var target = Pop(stack);
                            Safepoint(frame);
                            CallFrom(frame, target.Kind == ValueKind.Function ? target.Function : FunctionOf(Heap.AsInteger(target)));
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
                case OpCode.Generic:
                    GenericOperation(frame, op);
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
                case OpCode.Gc:
                    GcOperation(frame, op);
                    break;
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
                    Heap.Write(frame.ArgumentTypes[a0], frame.Arguments[a0], Pop(stack));
                    break;
                case StackOp.Move:
                    Heap.Write(frame.LocalTypes[a0], frame.Locals[a0], Heap.Read(frame.LocalTypes[op.Operands[1]], frame.Locals[op.Operands[1]]));
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

        private void CallFrom(Frame caller, int function)
        {
            var callee = il.Functions[function];
            var stack = caller.Stack;
            var arguments = new Value[callee.ArgumentCount];
            for (int a = arguments.Length - 1; a >= 0; a--)
                arguments[a] = Pop(stack);
            var witnesses = new IrType[callee.TypeParameterCount];
            for (int w = witnesses.Length - 1; w >= 0; w--)
            {
                var witness = Pop(stack);
                witnesses[w] = witness.Kind == ValueKind.Witness ? witness.Witness! : Runtime.WitnessType((int)Heap.AsInteger(witness));
            }

            var result = Call(function, witnesses, arguments);
            if (!il.Types[callee.LocalTypes[0]].Substitute(witnesses).IsUnit)
                stack.Add(result);
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

        private Value Convert(Value value, ConvertOp op, Frame frame, long operand)
        {
            switch (op)
            {
                case ConvertOp.Conv or ConvertOp.Cast:
                    var target = frame.Resolve(operand);
                    if (target is PrimitiveType { IsInteger: true } && value.Kind == ValueKind.Float)
                        return Value.FromInt(Heap.AsInteger(value), target);
                    return Heap.Coerce(value, target);
                case ConvertOp.BitCast:
                    var to = frame.Resolve(operand);
                    var bits = value.Kind == ValueKind.Float ? value.FloatBits : Heap.AsInteger(value);
                    return to.IsFloat
                        ? Value.FromFloatBits(bits & ((BigInteger.One << to.BitWidth!.Value) - 1), to)
                        : Value.FromInt(bits, to);
                case ConvertOp.SignExtend or ConvertOp.ZeroExtend or ConvertOp.Truncate:
                    int width = (int)operand;
                    var masked = Heap.AsInteger(value) & ((BigInteger.One << width) - 1);
                    if (op == ConvertOp.SignExtend && !(masked >> (width - 1)).IsZero)
                        masked -= BigInteger.One << width;
                    return Value.FromInt(masked, value.Type);
                default:
                    throw new VmException($"conv.{op} is not supported by the interpreter");
            }
        }

        // ── Helpers ────────────────────────────────────────────────

        private static Value Pop(List<Value> stack)
        {
            if (stack.Count == 0)
                throw new VmException("operand stack underflow");
            var value = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return value;
        }

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
            ValueKind.Struct => value.Bytes,
            _ => value,
        };

        /// <summary>One activation: witnesses, argument and local slots in memory, operand stack and program counter.</summary>
        private sealed class Frame
        {
            public readonly IlFunction Function;
            public readonly int Index;
            public readonly IrType[] TypeArguments;
            public readonly IrType[] ArgumentTypes;
            public readonly Address[] Arguments;
            public readonly IrType[] LocalTypes;
            public readonly Address[] Locals;
            public readonly List<Value> Stack = [];
            public int Pc;

            /// <summary>The exception being handled (set when a handler is entered; used by <c>eh.rethrow</c>).</summary>
            public Value? Exception;

            /// <summary>Open arena regions when the frame started; unwinding closes anything above.</summary>
            public int RegionDepth;
            private readonly Interpreter vm;

            public Frame(Interpreter vm, int index, IlFunction function, IrType[] typeArguments)
            {
                this.vm = vm;
                Index = index;
                Function = function;
                TypeArguments = typeArguments;
                ArgumentTypes = [.. function.ArgumentTypes.Select(t => Resolve(t))];
                LocalTypes = [.. function.LocalTypes.Select(t => Resolve(t))];
                Arguments = [.. ArgumentTypes.Select(t => new Address(vm.Heap.Allocate(AllocationKind.Frame, t).Id, 0))];
                Locals = [.. LocalTypes.Select(t => new Address(vm.Heap.Allocate(AllocationKind.Frame, t).Id, 0))];
            }

            /// <summary>Type token, with this frame's type arguments substituted (shared generic code).</summary>
            public IrType Resolve(long token) => vm.il.Types[token].Substitute(TypeArguments);

            public void Release()
            {
                foreach (var slot in Arguments.Concat(Locals))
                    vm.Heap.Free(slot, "frame exit");
            }
        }
    }
}

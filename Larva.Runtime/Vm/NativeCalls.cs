using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Larva.IR;
using Larva.IR.Lowering;

namespace Larva.Runtime.Vm
{
    /// <summary>
    /// Calls between the interpreter and C (P/Invoke): imports and <c>func*</c> calls are made with an invoker per signature
    /// (a <c>calli</c> with the platform's C convention); interpreter functions passed to C as <c>func*</c> become native
    /// entry points (a delegate per function and signature) that run them in the interpreter. Arguments are numbers,
    /// pointers and <c>func*</c>: a pointer into the interpreter's memory is pinned for the call.
    /// </summary>
    internal sealed class NativeCalls(Interpreter vm, Heap heap)
    {
        private readonly Interpreter interpreter = vm;
        private readonly Dictionary<string, nint> libraries = [];
        private readonly Dictionary<(string, string), nint> symbols = [];
        private static readonly Dictionary<string, Func<object?[], nint, object?>> invokers = [];
        private readonly Dictionary<(int, FunctionType), nint> entries = [];
        private readonly List<Delegate> alive = [];

        /// <summary>Calls the C function <paramref name="function"/> stands for (an import, or the address in its first argument).</summary>
        public Value Call(IlFunction function, NativeCall native, IrType[] parameters, IrType result, Value[] arguments)
        {
            nint address;
            int first = 0;
            if (native.Library is { } library)
            {
                address = Symbol(library, native.Symbol!);
            }
            else
            {
                var code = arguments[0];
                first = 1;
                int index = code.Kind == ValueKind.Function ? code.Function : FunctionIndex(Heap.AsInteger(code));
                if (index == int.MinValue)
                    throw new VmException("call through a null `func*`");
                if (index >= 0)
                    return vm.Call(index, [], arguments[1..]); // an interpreter function: no C involved
                address = Heap.NativeAddressOf(index);
            }
            var pins = new List<GCHandle>();
            try
            {
                var clrArguments = new object?[parameters.Length - first];
                for (int i = first; i < parameters.Length; i++)
                    clrArguments[i - first] = ToClr(arguments[i], parameters[i], pins);
                var invoker = Invoker([.. parameters[first..].Select(ClrType)], result.IsUnit ? typeof(void) : ClrType(result));
                return FromClr(invoker(clrArguments, address), result);
            }
            finally
            {
                heap.Unpin(pins);
            }
        }

        private static int FunctionIndex(BigInteger raw)
        {
            ulong bits = (ulong)(raw & ulong.MaxValue);
            if (bits == 0)
                return int.MinValue;
            return (bits & Address.FunctionTag) != 0 ? (int)(bits & ~Address.FunctionTag) : Heap.NativeFunction((nint)bits).Function;
        }

        private nint Symbol(string library, string name)
        {
            if (symbols.TryGetValue((library, name), out var known))
                return known;
            if (!libraries.TryGetValue(library, out var handle))
                libraries[library] = handle = NativeLibrary.Load(library);
            if (!NativeLibrary.TryGetExport(handle, name, out var address))
                throw new VmException($"`{name}` not found in `{library}`");
            return symbols[(library, name)] = address;
        }

        // ── Values ─────────────────────────────────────────────────

        private static Type ClrType(IrType type) => type switch
        {
            PrimitiveType { Kind: Primitive.Bool } => typeof(byte),
            PrimitiveType { Kind: Primitive.I8 } => typeof(sbyte),
            PrimitiveType { Kind: Primitive.U8 } => typeof(byte),
            PrimitiveType { Kind: Primitive.I16 } => typeof(short),
            PrimitiveType { Kind: Primitive.U16 } => typeof(ushort),
            PrimitiveType { Kind: Primitive.I32 } => typeof(int),
            PrimitiveType { Kind: Primitive.U32 } => typeof(uint),
            PrimitiveType { Kind: Primitive.I64 } => typeof(long),
            PrimitiveType { Kind: Primitive.U64 } => typeof(ulong),
            PrimitiveType { Kind: Primitive.F32 } => typeof(float),
            PrimitiveType { Kind: Primitive.F64 } => typeof(double),
            PointerType or FunctionType or ReferenceType => typeof(nint),
            _ => throw new VmException($"`{type}` cannot cross to C in the interpreter (numbers, pointers and func* can)"),
        };

        private object? ToClr(Value value, IrType type, List<GCHandle> pins)
        {
            switch (type)
            {
                case PointerType or ReferenceType:
                    return heap.ToNative(Heap.AsAddress(value), pins);
                case FunctionType function:
                {
                    int index = value.Kind == ValueKind.Function ? value.Function : FunctionIndex(Heap.AsInteger(value));
                    return index == int.MinValue ? (nint)0 : index < 0 ? Heap.NativeAddressOf(index) : Entry(index, function);
                }
                case PrimitiveType { IsFloat: true } p:
                    double real = value.Kind == ValueKind.Float ? value.Float : (double)Heap.AsInteger(value);
                    return p.Kind == Primitive.F32 ? (float)real : real;
                default:
                {
                    var bits = (ulong)(Heap.AsInteger(value) & ulong.MaxValue);
                    return Type.GetTypeCode(ClrType(type)) switch
                    {
                        TypeCode.SByte => (sbyte)bits,
                        TypeCode.Byte => (byte)bits,
                        TypeCode.Int16 => (short)bits,
                        TypeCode.UInt16 => (ushort)bits,
                        TypeCode.Int32 => (int)bits,
                        TypeCode.UInt32 => (uint)bits,
                        TypeCode.Int64 => (long)bits,
                        _ => (object)bits,
                    };
                }
            }
        }

        private Value FromClr(object? value, IrType type) => type switch
        {
            PrimitiveType { Kind: Primitive.Unit } => Value.UnitValue,
            PrimitiveType { Kind: Primitive.Bool } => Value.FromBool((byte)value! != 0),
            PrimitiveType { IsFloat: true } => Value.FromFloat(System.Convert.ToDouble(value), type),
            PrimitiveType => Value.FromInt(value switch { ulong u => u, nint n => (long)n, _ => System.Convert.ToInt64(value) }, type),
            FunctionType => (nint)value! == 0 ? Value.FromFunction(int.MinValue) : FromEntry((nint)value!),
            _ => Value.FromPointer(heap.FromNative((nint)value!), type),
        };

        private Value FromEntry(nint address)
        {
            foreach (var ((function, _), entry) in entries)
                if (entry == address)
                    return Value.FromFunction(function);
            return Heap.NativeFunction(address);
        }

        // ── Calls out: an invoker per signature ──────────────────

        private static Func<object?[], nint, object?> Invoker(Type[] parameters, Type result)
        {
            string key = string.Join(",", parameters.Select(p => p.Name)) + "->" + result.Name;
            lock (invokers)
            {
                if (invokers.TryGetValue(key, out var known))
                    return known;
                var method = new DynamicMethod("larva_native_call", typeof(object), [typeof(object?[]), typeof(nint)], typeof(NativeCalls).Module, skipVisibility: true);
                var il = method.GetILGenerator();
                for (int i = 0; i < parameters.Length; i++)
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4, i);
                    il.Emit(OpCodes.Ldelem_Ref);
                    il.Emit(OpCodes.Unbox_Any, parameters[i]);
                }
                il.Emit(OpCodes.Ldarg_1);
                il.EmitCalli(OpCodes.Calli, CallingConvention.Winapi, result, parameters);
                if (result == typeof(void))
                    il.Emit(OpCodes.Ldnull);
                else
                    il.Emit(OpCodes.Box, result);
                il.Emit(OpCodes.Ret);
                return invokers[key] = method.CreateDelegate<Func<object?[], nint, object?>>();
            }
        }

        // ── Calls in: interpreter functions as C function pointers ──

        private static readonly ModuleBuilder thunkModule =
            AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Larva.NativeEntries"), AssemblyBuilderAccess.Run).DefineDynamicModule("entries");
        private static readonly Dictionary<string, Type> delegateTypes = [];

        /// <summary>A native entry point that runs interpreter function <paramref name="function"/> with the C convention.</summary>
        private nint Entry(int function, FunctionType signature)
        {
            if (entries.TryGetValue((function, signature), out var known))
                return known;
            var parameters = signature.Parameters.Select(ClrType).ToArray();
            var result = signature.Result.IsUnit ? typeof(void) : ClrType(signature.Result);
            var delegateType = DelegateType(parameters, result);
            var arguments = parameters.Select((p, i) => Expression.Parameter(p, $"a{i}")).ToArray();
            var boxed = Expression.NewArrayInit(typeof(object), arguments.Select(a => Expression.Convert(a, typeof(object))));
            var run = Expression.Call(Expression.Constant(new Callback(this, function, signature)), typeof(Callback).GetMethod(nameof(Callback.Run))!, boxed);
            Expression body = result == typeof(void) ? run : Expression.Convert(run, result);
            var entry = Expression.Lambda(delegateType, body, arguments).Compile();
            alive.Add(entry);
            nint address = Marshal.GetFunctionPointerForDelegate(entry);
            entries[(function, signature)] = address;
            return address;
        }

        private sealed class Callback(NativeCalls owner, int function, FunctionType signature)
        {
            public object? Run(object?[] arguments)
            {
                var values = arguments.Select((a, i) => owner.FromClr(a, signature.Parameters[i])).ToArray();
                var result = owner.interpreter.Call(function, [], values);
                if (signature.Result.IsUnit)
                    return null;
                var pins = new List<GCHandle>();
                return owner.ToClr(result, signature.Result, pins); // a pointer into interpreter memory stays valid only while pinned
            }
        }

        private static Type DelegateType(Type[] parameters, Type result)
        {
            string key = string.Join(",", parameters.Select(p => p.Name)) + "->" + result.Name;
            lock (delegateTypes)
            {
                if (delegateTypes.TryGetValue(key, out var known))
                    return known;
                var type = thunkModule.DefineType($"Entry{delegateTypes.Count}", TypeAttributes.Sealed | TypeAttributes.Public, typeof(MulticastDelegate));
                var constructor = type.DefineConstructor(MethodAttributes.RTSpecialName | MethodAttributes.HideBySig | MethodAttributes.Public,
                    CallingConventions.Standard, [typeof(object), typeof(nint)]);
                constructor.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
                var invoke = type.DefineMethod("Invoke", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
                    result, parameters);
                invoke.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
                return delegateTypes[key] = type.CreateType();
            }
        }
    }
}

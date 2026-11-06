using System.Numerics;
using Larva.IR;
using Larva.IR.Analysis;
using Larva.IR.Linking;
using Larva.IR.Lowering;

namespace Larva.Wasm
{
    public sealed class WasmBackendException(string message) : Exception(message);

    /// <summary>
    /// WebAssembly code generation without LLVM: a baseline compiler in the spirit of the direct x86-64 back end.
    /// <list type="bullet">
    /// <item>Frames live in linear memory (a shadow stack growing down from <c>$sp</c>): every IR local has its slot there, so
    /// every value has an address and the collector finds every root by scanning the stack, conservatively.</item>
    /// <item>One calling convention for all Larva functions, <c>(args: i32, ret: i32) -&gt; ()</c>: the caller lays the
    /// arguments out in its frame and passes where they are and where the result goes. Indirect calls (function values,
    /// finalizers) are then a <c>call_indirect</c> of that one type through the exported table.</item>
    /// <item>Control flow: each function is a dispatch loop over its basic blocks (<c>br_table</c> on a state local), which
    /// takes any IR control-flow graph as it is.</item>
    /// <item>Exceptions: <c>throw</c> stores the payload (a heap cell) and its type id in two globals and goes to the site's
    /// handlers; after every call a pending exception does the same, or the frame cleans up and returns with it.</item>
    /// <item>Pointers are 32-bit but kept in 8-byte slots (zero-extended), so struct layouts are the native ones.</item>
    /// </list>
    /// The runtime (<c>larva_*</c>: allocation, collection, reference counting, regions, printing, panics) is imported from
    /// the JavaScript host (<c>larva_rt.js</c>). Not supported: 128-bit multiplication, division and shifts, i256/u256,
    /// f16/f128/f256.
    /// </summary>
    internal sealed class WasmCodegen
    {
        internal const int StackSize = 1 << 20;
        internal const int ArgsScratch = 16;   // 32 bytes for the host's entry arguments, then 16 for the result
        internal const int ResultScratch = 48;

        internal const int Sp = 0, InFlight = 1, ExceptionType = 2;

        private readonly IrModule module;
        private readonly GcConfig gc;
        private readonly bool eliminateBoundsChecks;
        internal readonly WasmModule Wasm = new();
        internal readonly int Uniform;

        internal readonly int[] FunctionIndex;
        private readonly Dictionary<int, int> tableSlots = [];
        internal readonly int[] GlobalAddress;
        internal readonly int[] StaticAddress;
        internal int StackLimit, StackTop;

        // Runtime imports: name → (wasm type, index once assigned).
        private static readonly Dictionary<string, FuncType> RuntimeTypes = new()
        {
            ["larva_alloc"] = new([ValType.I32, ValType.I32], [ValType.I32]),
            ["larva_free"] = new([ValType.I32], []),
            ["larva_gc_new"] = new([ValType.I32, ValType.I32], [ValType.I32]),
            ["larva_gc_domain"] = new([ValType.I32], [ValType.I64]),
            ["larva_gc_retain"] = new([ValType.I32], []),
            ["larva_gc_release"] = new([ValType.I32], []),
            ["larva_gc_finalizer"] = new([ValType.I32, ValType.I32], []),
            ["larva_array_new"] = new([ValType.I64, ValType.I32, ValType.I32, ValType.I32], [ValType.I32]),
            ["larva_pin"] = new([ValType.I32], []),
            ["larva_unpin"] = new([ValType.I32], []),
            ["larva_region_enter"] = new([ValType.I32], []),
            ["larva_region_exit"] = new([ValType.I32], []),
            ["larva_region_depth"] = new([], [ValType.I32]),
            ["larva_region_unwind"] = new([ValType.I32], []),
            ["larva_print_i64"] = new([ValType.I64], []),
            ["larva_print_u64"] = new([ValType.I64], []),
            ["larva_print_f64"] = new([ValType.I64], []),
            ["larva_print_bool"] = new([ValType.I32], []),
            ["larva_print_str"] = new([ValType.I32], []),
            ["larva_print_line"] = new([], []),
            ["larva_print_utf8"] = new([ValType.I32], []),
            ["larva_read_line"] = new([], [ValType.I64]),
            ["larva_read_unit"] = new([ValType.I64], [ValType.I64]),
            ["larva_args_count"] = new([], [ValType.I64]),
            ["larva_args_length"] = new([ValType.I64], [ValType.I64]),
            ["larva_args_unit"] = new([ValType.I64, ValType.I64], [ValType.I64]),
            ["larva_format_f64"] = new([ValType.I64], [ValType.I64]),
            ["larva_format_unit"] = new([ValType.I64], [ValType.I64]),
            ["larva_gc_request"] = new([], []),
            ["larva_gc_collections"] = new([], [ValType.I64]),
            ["larva_gc_objects"] = new([], [ValType.I64]),
            ["larva_gc_custom"] = new([ValType.I64, ValType.I64, ValType.I64], []),
            ["larva_time_now"] = new([], [ValType.I64]),
            ["larva_time_sleep"] = new([ValType.I64], []),
            ["larva_time_utc"] = new([], [ValType.I64]),
            ["larva_time_offset"] = new([ValType.I64], [ValType.I64]),
            ["larva_time_precise"] = new([], [ValType.I64]),
            ["larva_thread_start"] = new([ValType.I32, ValType.I32], [ValType.I64]),
            ["larva_thread_join"] = new([ValType.I64, ValType.I64], [ValType.I64]),
            ["larva_thread_id"] = new([], [ValType.I64]),
            ["larva_thread_yield"] = new([], []),
            ["larva_cpu_count"] = new([], [ValType.I64]),
            ["larva_mutex_new"] = new([], [ValType.I64]),
            ["larva_mutex_lock"] = new([ValType.I64], []),
            ["larva_mutex_trylock"] = new([ValType.I64], [ValType.I64]),
            ["larva_mutex_unlock"] = new([ValType.I64], []),
            ["larva_mutex_free"] = new([ValType.I64], []),
            ["larva_cond_new"] = new([], [ValType.I64]),
            ["larva_cond_wait"] = new([ValType.I64, ValType.I64, ValType.I64], [ValType.I64]),
            ["larva_cond_signal"] = new([ValType.I64], []),
            ["larva_cond_broadcast"] = new([ValType.I64], []),
            ["larva_cond_free"] = new([ValType.I64], []),
            ["larva_atomic_load_i64"] = new([ValType.I32], [ValType.I64]),
            ["larva_atomic_store_i64"] = new([ValType.I32, ValType.I64], []),
            ["larva_atomic_add_i64"] = new([ValType.I32, ValType.I64], [ValType.I64]),
            ["larva_atomic_xchg_i64"] = new([ValType.I32, ValType.I64], [ValType.I64]),
            ["larva_atomic_cas_i64"] = new([ValType.I32, ValType.I64, ValType.I64], [ValType.I64]),
            ["larva_atomic_load_i32"] = new([ValType.I32], [ValType.I32]),
            ["larva_atomic_store_i32"] = new([ValType.I32, ValType.I32], []),
            ["larva_atomic_add_i32"] = new([ValType.I32, ValType.I32], [ValType.I32]),
            ["larva_atomic_xchg_i32"] = new([ValType.I32, ValType.I32], [ValType.I32]),
            ["larva_atomic_cas_i32"] = new([ValType.I32, ValType.I32, ValType.I32], [ValType.I32]),
            ["larva_stream_open"] = new([ValType.I32, ValType.I64], [ValType.I64]),
            ["larva_net_connect"] = new([ValType.I32, ValType.I64], [ValType.I64]),
            ["larva_net_listen"] = new([ValType.I64], [ValType.I64]),
            ["larva_net_accept"] = new([ValType.I64], [ValType.I64]),
            ["larva_net_port"] = new([ValType.I64], [ValType.I64]),
            ["larva_proc_start"] = new([ValType.I32, ValType.I32], [ValType.I64]),
            ["larva_proc_wait"] = new([ValType.I64], [ValType.I64]),
            ["larva_stream_close"] = new([ValType.I64], [ValType.I64]),
            ["larva_stream_read"] = new([ValType.I64, ValType.I32, ValType.I64, ValType.I64], [ValType.I64]),
            ["larva_stream_write"] = new([ValType.I64, ValType.I32, ValType.I64, ValType.I64], [ValType.I64]),
            ["larva_stream_seek"] = new([ValType.I64, ValType.I64, ValType.I64], [ValType.I64]),
            ["larva_stream_length"] = new([ValType.I64], [ValType.I64]),
            ["larva_stream_flush"] = new([ValType.I64], [ValType.I64]),
            ["larva_math_sqrt"] = new([ValType.I64], [ValType.I64]),
            ["larva_math_sin"] = new([ValType.I64], [ValType.I64]),
            ["larva_math_cos"] = new([ValType.I64], [ValType.I64]),
            ["larva_math_tan"] = new([ValType.I64], [ValType.I64]),
            ["larva_math_asin"] = new([ValType.I64], [ValType.I64]),
            ["larva_math_acos"] = new([ValType.I64], [ValType.I64]),
            ["larva_math_atan"] = new([ValType.I64], [ValType.I64]),
            ["larva_math_exp"] = new([ValType.I64], [ValType.I64]),
            ["larva_math_log"] = new([ValType.I64], [ValType.I64]),
            ["larva_math_log10"] = new([ValType.I64], [ValType.I64]),
            ["larva_math_floor"] = new([ValType.I64], [ValType.I64]),
            ["larva_math_ceil"] = new([ValType.I64], [ValType.I64]),
            ["larva_math_atan2"] = new([ValType.I64, ValType.I64], [ValType.I64]),
            ["larva_math_pow"] = new([ValType.I64, ValType.I64], [ValType.I64]),
            ["larva_f64_bits"] = new([ValType.I64], [ValType.I64]),
            ["larva_f64_from_bits"] = new([ValType.I64], [ValType.I64]),
            ["larva_mem_alloc"] = new([ValType.I64], [ValType.I32]),
            ["larva_mem_free"] = new([ValType.I32], []),
            ["larva_mem_copy"] = new([ValType.I32, ValType.I32, ValType.I64], []),
            ["larva_mem_cmp"] = new([ValType.I32, ValType.I32, ValType.I64], [ValType.I64]),
            ["larva_native_symbol"] = new([ValType.I32, ValType.I32], [ValType.I64]),
            ["larva_file_read"] = new([ValType.I32], [ValType.I64]),
            ["larva_file_take"] = new([ValType.I32], []),
            ["larva_file_write"] = new([ValType.I32, ValType.I32, ValType.I64], [ValType.I64]),
            ["larva_file_kind"] = new([ValType.I32], [ValType.I64]),
            ["larva_file_delete"] = new([ValType.I32], [ValType.I64]),
            ["larva_file_time"] = new([ValType.I32], [ValType.I64]),
            ["larva_dir_create"] = new([ValType.I32], [ValType.I64]),
            ["larva_dir_list"] = new([ValType.I32], [ValType.I64]),
            ["larva_dir_length"] = new([ValType.I64], [ValType.I64]),
            ["larva_dir_unit"] = new([ValType.I64, ValType.I64], [ValType.I64]),
            ["larva_env_get"] = new([ValType.I32], [ValType.I64]),
            ["larva_env_unit"] = new([ValType.I64], [ValType.I64]),
            ["larva_process_run"] = new([ValType.I32], [ValType.I64]),
            ["larva_parse_f64"] = new([ValType.I32], [ValType.I64]),
            ["larva_panic"] = new([ValType.I32, ValType.I64, ValType.I64], []),
            // The high 64 bits of an unsigned 64 × 64 product (128-bit multiplication).
            ["larva_mulhi_u64"] = new([ValType.I64, ValType.I64], [ValType.I64]),
        };

        private readonly Dictionary<string, int> runtime = [];
        private readonly HashSet<string>? used;

        public int BoundsChecks { get; set; }
        public int BoundsChecksEliminated { get; set; }

        /// <param name="used">First pass: collects the runtime functions the code calls (indices are placeholders).</param>
        public WasmCodegen(IrModule module, GcConfig gc, bool eliminateBoundsChecks, HashSet<string>? used, IReadOnlySet<string>? imports)
        {
            this.module = module;
            this.gc = gc;
            this.eliminateBoundsChecks = eliminateBoundsChecks;
            this.used = used;
            foreach (string name in RuntimeTypes.Keys.Where(n => imports is null || imports.Contains(n)))
                runtime[name] = Wasm.Import("env", name, RuntimeTypes[name]);
            Uniform = Wasm.Type(new FuncType([ValType.I32, ValType.I32], []));
            FunctionIndex = new int[module.Functions.Length];
            for (int f = 0; f < module.Functions.Length; f++)
            {
                var function = module.Functions[f];
                if (function.IsGeneric)
                {
                    FunctionIndex[f] = -1;
                    continue;
                }
                if (function.IsDeclaration)
                    throw new WasmBackendException($"`{function.Name}` is only declared: link the program first");
                FunctionIndex[f] = Wasm.Declare(new FuncType([ValType.I32, ValType.I32], []));
            }

            // Memory: [0, 16) null; entry scratch; static data; globals; the stack; then the heap (the host's).
            int at = 64;
            StaticAddress = new int[module.Data.Length];
            for (int d = 0; d < module.Data.Length; d++)
            {
                var bytes = Layouts.StaticBytes(module.Data[d], module, out _);
                at = Align(at, 16);
                StaticAddress[d] = at;
                Wasm.Data(at, bytes);
                at += bytes.Length;
            }
            GlobalAddress = new int[module.Globals.Length];
            GlobalsStart = Align(at, 16);
            at = GlobalsStart;
            for (int g = 0; g < module.Globals.Length; g++)
            {
                var global = module.Globals[g];
                var layout = Layouts.Of(global.Type, module);
                int size = Math.Max(1, layout?.Size ?? 1);
                at = Align(at, Math.Max(8, layout?.Align ?? 8));
                GlobalAddress[g] = at;
                if (global.Initial is { } initial && !initial.IsZero && global.Type is PrimitiveType)
                {
                    var bits = initial & ((BigInteger.One << (8 * size)) - 1);
                    var bytes = new byte[size];
                    for (int i = 0; i < size; i++)
                        bytes[i] = (byte)(bits >> (8 * i) & 0xFF);
                    Wasm.Data(at, bytes);
                }
                at += size;
            }
            GlobalsEnd = Align(at, 16);
            StackLimit = GlobalsEnd;
            StackTop = StackLimit + StackSize;
            Wasm.MemoryPages = StackTop / 65536 + 2;
            Wasm.Global(ValType.I32, true, StackTop);  // $sp
            Wasm.Global(ValType.I32, true, 0);         // $exception: the payload cell of the exception in flight, 0 for none
            Wasm.Global(ValType.I32, true, 0);         // $exception_type
        }

        internal int GlobalsStart { get; }
        internal int GlobalsEnd { get; }

        internal static int Align(int value, int align) => (value + align - 1) / align * align;

        internal bool Has(GcCapabilities capability) => (gc.Capabilities & capability) != 0;
        internal GcConfig Gc => gc;
        internal IrModule Module => module;
        internal bool EliminateBoundsChecks => eliminateBoundsChecks;

        internal int Runtime(string name)
        {
            used?.Add(name);
            return runtime.TryGetValue(name, out int index) ? index : 0;
        }

        /// <summary>The table slot of a function (what a function value holds), made on first use.</summary>
        internal int TableSlot(int function)
        {
            int wasmFunction = FunctionIndex[function];
            if (!tableSlots.TryGetValue(wasmFunction, out int slot))
            {
                Wasm.Table.Add(wasmFunction);
                tableSlots[wasmFunction] = slot = Wasm.Table.Count;
            }
            return slot;
        }

        internal int TypeId(IrType type) => NativeImages.ExceptionTypeId(type, module);

        public void Generate()
        {
            for (int f = 0; f < module.Functions.Length; f++)
            {
                if (FunctionIndex[f] < 0)
                    continue;
                ValType[] locals;
                byte[] code;
                try
                {
                    (locals, code) = new WasmFunctionCompiler(this, module.Functions[f]).Compile();
                }
                catch (WasmBackendException e) when (!e.Message.StartsWith('`' + module.Functions[f].Name, StringComparison.Ordinal))
                {
                    throw new WasmBackendException($"`{module.Functions[f].Name}`: {e.Message}");
                }
                Wasm.Define(FunctionIndex[f], locals, code);
                string name = module.Functions[f].Name;
                if (name.StartsWith("larva_", StringComparison.Ordinal))
                    Wasm.ExportFunction(name, FunctionIndex[f]);
            }
            Wasm.ExportGlobal("sp", Sp);
            Wasm.ExportGlobal("exception", InFlight);
            Wasm.ExportGlobal("exception_type", ExceptionType);
            Wasm.ExportGlobal("stack_limit", Wasm.Global(ValType.I32, false, StackLimit));
            Wasm.ExportGlobal("stack_top", Wasm.Global(ValType.I32, false, StackTop));
            Wasm.ExportGlobal("globals_start", Wasm.Global(ValType.I32, false, GlobalsStart));
            Wasm.ExportGlobal("globals_end", Wasm.Global(ValType.I32, false, GlobalsEnd));
        }

        // ── Value representation ───────────────────────────────

        internal enum Kind : byte { Unit, Scalar, Float, Aggregate, Wide }

        /// <summary>How a value is held: scalars canonical in 8 bytes (extended by signedness) once loaded into a temp.</summary>
        internal (Kind Kind, int Size, bool Signed) Shape(IrType type)
        {
            switch (type)
            {
                case PrimitiveType { Kind: Primitive.Unit }:
                    return (Kind.Unit, 0, false);
                case PrimitiveType { Kind: Primitive.Bool }:
                    return (Kind.Scalar, 1, false);
                case PrimitiveType { Kind: Primitive.F32 or Primitive.F64 } p:
                    return (Kind.Float, p.BitWidth!.Value / 8, false);
                case PrimitiveType { IsInteger: true } p when p.BitWidth <= 64:
                    return (Kind.Scalar, p.BitWidth!.Value / 8, p.IsSigned);
                case PrimitiveType { Kind: Primitive.I128 or Primitive.U128 } p:
                    return (Kind.Wide, 16, p.IsSigned);
                case PrimitiveType p:
                    throw new WasmBackendException($"`{p}` is not supported by the WebAssembly back end");
                case ReferenceType or GcType or BoxType or PointerType or FunctionType or OptionalType:
                    return (Kind.Scalar, 8, false);
                case StructType or FixedArrayType:
                    var layout = Layouts.Of(type, module) ?? throw new WasmBackendException($"no layout for `{type}`");
                    return (Kind.Aggregate, layout.Size, false);
                default:
                    throw new WasmBackendException($"values of type `{type}` are not supported by the WebAssembly back end");
            }
        }

        internal (int Header, int Stride) ArrayLayout(IrType element)
        {
            var layout = Layouts.Of(element, module)!;
            int stride = Math.Max(1, (layout.Size + layout.Align - 1) / layout.Align * layout.Align);
            return (Math.Max(8, layout.Align), stride);
        }

        internal static long Canonical(ConstOperand constant)
        {
            var type = constant.Type;
            if (type == IrType.Bool)
                return constant.Bits.IsZero ? 0 : 1;
            var bits = constant.Bits;
            if (type.IsFloat || type.IsUnsigned)
                return (long)(ulong)(bits & ulong.MaxValue);
            return (long)bits;
        }

        internal static (long Low, long High) Halves(ConstOperand constant)
        {
            var bits = constant.Bits & ((BigInteger.One << 128) - 1);
            return ((long)(ulong)(bits & ulong.MaxValue), (long)(ulong)(bits >> 64));
        }
    }
}

using System.Numerics;
using System.Text;
using Larva.IR;
using Larva.IR.Analysis;
using Larva.IR.Lowering;
using Larva.IR.Text;
using LLVMSharp.Interop;
using Larva.Native;

namespace Larva.Llvm
{
    /// <summary>A program the LLVM backend cannot translate yet (or at all), with the reason.</summary>
    public sealed class LlvmBackendException(string message) : Exception(message);

    /// <summary>
    /// Translates a concrete (monomorphized, import-free) IR module into an LLVM module.
    /// <list type="bullet">
    /// <item>Types: integers of any width map to <c>iN</c>; f16/f32/f64 to half/float/double; f128 and f256 to i128 and i256
    /// (their IEEE bits: the runtime's soft-float routines compute them, as for the direct x86-64 back end); every pointer kind
    /// to <c>ptr</c>; structs to packed LLVM structs with explicit padding, so offsets are exactly <see cref="Layouts"/>'s
    /// (shared with the VM, the GC maps and reflection).</item>
    /// <item>Locals live in <c>alloca</c>s (promoted to SSA by <c>mem2reg</c>); places become GEPs; array accesses are
    /// bounds-checked; division checks for zero. Comparisons honor signedness.</item>
    /// <item>Ownership: every owned resource (boxes; <c>gc</c> handles under reference counting) gets a drop flag. Flags are
    /// set on initialization, cleared on move, and guard every release; LLVM folds the ones that are constant.</item>
    /// <item>Runtime services (allocation, RC, regions, barriers, pins, polls, panics) are calls to <c>larva_*</c> functions
    /// (see <see cref="NativeRuntime"/>).</item>
    /// <item>Exceptions are zero-cost Windows SEH: <c>throw</c> raises an exception with Larva's code whose information
    /// carries a heap cell holding the value and the value's type id; calls with catch clauses become <c>invoke</c>s into a
    /// chain of <c>catchswitch</c>es (one handler each, as SEH requires) whose <c>catchpad</c>s use generated filter functions (code and type id must match); one
    /// <c>cleanuppad</c> per function releases what the frame owns (by drop flags), closes the arena regions it opened and
    /// unpins, then lets the exception continue. The personality is <c>__C_specific_handler</c>.</item>
    /// </list>
    /// In tracing modes, the slots of locals that can hold <c>gc</c> handles become precise stack roots (see
    /// <see cref="StackMaps"/>); safepoints are the <c>larva_poll</c> calls at loop headers and before calls.
    /// </summary>
    internal sealed unsafe class LlvmCodegen
    {
        private readonly IrModule module;
        private readonly GcConfig gc;
        private readonly bool noalias;
        private readonly LLVMBuilderRef builder;
        private readonly LLVMTypeRef ptr;
        private readonly LLVMTypeRef unit;
        private readonly Dictionary<int, (LLVMTypeRef Type, int[] Elements)> structs = [];
        private readonly LLVMValueRef[] functions;
        private readonly LLVMTypeRef[] functionTypes;
        private readonly Dictionary<string, (LLVMValueRef Function, LLVMTypeRef Type)> runtime = [];

        /// <summary>SEH exception code of Larva exceptions ("LVA" in the customer range).</summary>
        public const uint ExceptionCode = 0xE04C5641;

        private readonly Dictionary<IrType, int> typeIds = [];
        private readonly Dictionary<int, LLVMValueRef> filters = [];
        private LLVMValueRef personality;
        private LLVMValueRef payloadGlobal;
        private LLVMValueRef typeGlobal;
        private LLVMValueRef throwFunction;
        private LLVMTypeRef throwType;
        private LLVMTypeRef filterType;
        private LLVMValueRef none;

        /// <summary>While emitting a cleanup funclet: calls must carry its <c>"funclet"</c> operand bundle.</summary>
        private LLVMValueRef funclet;

        /// <summary>
        /// Exceptions without unwinding (WebAssembly): a throw stores the payload and type id in <c>larva_exception</c> /
        /// <c>larva_exception_type</c>, and after every Larva call a pending exception goes to the site's handlers, or the
        /// frame cleans up and returns with it.
        /// </summary>
        private readonly bool flagExceptions;

        /// <summary>
        /// Safepoints read the runtime's flag (through <c>__imp_larva_poll_pending</c>, as an import from the runtime DLL)
        /// and call <c>larva_poll</c> only when it is set. WebAssembly has no safepoints: its program runs on one thread and
        /// its runtime collects only inside an allocation.
        /// </summary>
        private readonly bool inlinePolls;

        /// <summary>Whether locals that may hold handles are marked as stack roots (see <see cref="LlvmOptions.PreciseStackRoots"/>).</summary>
        private readonly bool stackRoots;
        private LLVMValueRef pollCell;

        public LLVMModuleRef Module { get; }

        private readonly bool eliminateBoundsChecks;

        /// <summary>Bounds checks emitted, and those proven unnecessary by <see cref="BoundsAnalysis"/>.</summary>
        public int BoundsChecks { get; private set; }
        public int BoundsChecksEliminated { get; private set; }

        private readonly string? symbolPrefix;

        /// <summary>The object-file symbol of a Larva function.</summary>
        private readonly Dictionary<int, LLVMValueRef> statics = [];
        private readonly Dictionary<int, LLVMValueRef> globals = [];

        internal LLVMValueRef GlobalVariable(int index, IrType type, Func<LLVMValueRef> initializer, LLVMTypeRef? storage = null)
        {
            if (globals.TryGetValue(index, out var global))
                return global;
            global = Module.AddGlobal(storage ?? ValueType(type), $"larva.global.{index}");
            global.Initializer = initializer();
            global.Alignment = 16;
            if (partitioned)
            {
                // Every object that uses it defines it; the linker keeps one (the same initial value everywhere).
                global.Linkage = LLVMLinkage.LLVMLinkOnceAnyLinkage;
                var bytes = Encoding.ASCII.GetBytes(global.Name);
                fixed (byte* text = bytes)
                    LLVM.SetComdat(global, LLVM.GetOrInsertComdat(Module, (sbyte*)text));
            }
            else
            {
                global.Linkage = LLVMLinkage.LLVMInternalLinkage;
            }
            return globals[index] = global;
        }

        /// <summary>Static array N as a private constant global with the heap array layout (aligned like a gc object).</summary>
        internal LLVMValueRef StaticGlobal(int index)
        {
            if (statics.TryGetValue(index, out var global))
                return global;
            var bytes = Layouts.StaticBytes(module.Data[index], module, out _);
            var i8 = Ctx.Int8Type;
            var elements = bytes.Select(x => LLVMValueRef.CreateConstInt(i8, x)).ToArray();
            var initializer = LLVMValueRef.CreateConstArray(i8, elements);
            global = Module.AddGlobal(LLVMTypeRef.CreateArray(i8, (uint)bytes.Length), $"larva.data.{index}");
            global.Initializer = initializer;
            global.Linkage = LLVMLinkage.LLVMPrivateLinkage;
            global.IsGlobalConstant = true;
            global.Alignment = 16;
            return statics[index] = global;
        }

        public static string Symbol(string name, string? prefix) => prefix is null || name.StartsWith("larva_", StringComparison.Ordinal) ? name : prefix + name;

        /// <summary>
        /// The LLVM context the module is made in: the global one, or a context of its own (one per thread when code is
        /// generated on several at once).
        /// </summary>
        internal LLVMContextRef Ctx { get; }

        /// <summary>Which functions get a body here (the others are declared: another object defines them); null: all.</summary>
        private readonly Func<int, bool>? defines;

        /// <summary>One object among several of a program: its Larva globals are link-once (each object defines the ones it uses).</summary>
        private readonly bool partitioned;

        public LlvmCodegen(IrModule module, GcConfig gc, bool noalias, string name, bool eliminateBoundsChecks = true, string? symbolPrefix = null,
            bool flagExceptions = false, LLVMContextRef? context = null, Func<int, bool>? defines = null, bool[]? compilable = null,
            bool inlinePolls = true, bool stackRoots = true)
        {
            this.inlinePolls = inlinePolls;
            this.stackRoots = stackRoots;
            this.compilable = compilable;
            Ctx = context ?? LLVM.GetGlobalContext();
            this.defines = defines;
            partitioned = defines is not null;
            this.flagExceptions = flagExceptions;
            this.symbolPrefix = symbolPrefix;
            this.eliminateBoundsChecks = eliminateBoundsChecks;
            this.module = module;
            this.gc = gc;
            this.noalias = noalias;
            Module = Ctx.CreateModuleWithName(name);
            builder = LLVMBuilderRef.Create(Module.Context);
            ptr = LLVMTypeRef.CreatePointer(Ctx.Int8Type, 0);
            unit = Ctx.GetStructType([], false);
            functions = new LLVMValueRef[module.Functions.Length];
            functionTypes = new LLVMTypeRef[module.Functions.Length];
        }

        /// <summary>Functions left out (see <see cref="LlvmResult.Skipped"/>).</summary>
        public Dictionary<string, string> Skipped { get; } = [];

        public void Generate()
        {
            DeclareRuntime();
            // Functions over what this back end lacks (nothing among the primitives now), and what reaches them, are left out.
            compilable ??= Larva.IR.Linking.Compilable.Of(module, LlvmBackend.Supports);
            for (int f = 0; f < module.Functions.Length; f++)
            {
                // One object of several declares the rest when its code first names them.
                if (compilable[f] && (defines is null || defines(f)))
                    Declare(f);
                else if (!compilable[f])
                    Skipped[module.Functions[f].Name] = LlvmBackend.Supports(module.Functions[f], module)
                        ? "it uses a function that is left out" : "it uses a type this back end lacks";
            }
            var entryPolls = Larva.IR.Analysis.Safepoints.EntryPolls(module);
            var mayCollect = Larva.IR.Analysis.Safepoints.MayCollect(module);
            for (int f = 0; f < module.Functions.Length; f++)
            {
                if (functions[f].Handle != IntPtr.Zero && !module.Functions[f].IsDeclaration && (defines is null || defines(f)))
                    new FunctionEmitter(this, module.Functions[f], functions[f]) { EntryPoll = entryPolls[f], MayCollect = mayCollect[f] }.Emit();
            }
        }

        private string? Unsupported(IrFunction function)
        {
            try
            {
                foreach (var local in function.Locals)
                    ValueType(local.Type);
                return null;
            }
            catch (LlvmBackendException e)
            {
                return $"`{function.Name}`: {e.Message}";
            }
        }

        private bool Has(GcCapabilities capability) => (gc.Capabilities & capability) != 0;



        private LLVMValueRef lengthTag, elementTag;

        /// <summary>
        /// Type-based alias tags: an array's length word and its elements are never the same memory, so a store into an
        /// element does not make LLVM read the length again (a sort's loop reloaded it after every swap). Accesses without a
        /// tag (raw pointers, copies, the runtime) still alias everything.
        /// </summary>
        internal void TagLength(LLVMValueRef access) => access.SetMetadata(Kind("tbaa"), TbaaTag(ref lengthTag, "length"));

        internal void TagElement(LLVMValueRef access) => access.SetMetadata(Kind("tbaa"), TbaaTag(ref elementTag, "element"));

        private LLVMValueRef TbaaTag(ref LLVMValueRef tag, string name)
        {
            if (tag.Handle != IntPtr.Zero)
                return tag;
            var context = Module.Context;
            LLVMOpaqueMetadata* Text(string text)
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                fixed (byte* chars = bytes)
                    return LLVM.MDStringInContext2(context, (sbyte*)chars, (nuint)bytes.Length);
            }
            LLVMOpaqueMetadata* Node(params LLVMOpaqueMetadata*[] items)
            {
                fixed (LLVMOpaqueMetadata** values = items)
                    return LLVM.MDNodeInContext2(context, values, (nuint)items.Length);
            }
            var zero = LLVM.ValueAsMetadata(LLVMValueRef.CreateConstInt(Ctx.Int64Type, 0));
            var root = Node(Text("larva tbaa"));
            var type = Node(Text(name), root, zero);
            tag = LLVM.MetadataAsValue(context, Node(type, type, zero));
            return tag;
        }

        private uint Kind(string metadata)
        {
            var bytes = Encoding.ASCII.GetBytes(metadata);
            fixed (byte* name = bytes)
                return LLVM.GetMDKindIDInContext(Module.Context, (sbyte*)name, (uint)bytes.Length);
        }

        // ── Types ──────────────────────────────────────────────────

        public LLVMTypeRef ValueType(IrType type) => type switch
        {
            PrimitiveType { Kind: Primitive.Unit } => unit,
            PrimitiveType { Kind: Primitive.Bool } => Ctx.Int1Type,
            PrimitiveType { IsInteger: true } p => Ctx.GetIntType((uint)p.BitWidth!.Value),
            PrimitiveType { Kind: Primitive.F16 } => Ctx.HalfType,
            PrimitiveType { Kind: Primitive.F32 } => Ctx.FloatType,
            PrimitiveType { Kind: Primitive.F64 } => Ctx.DoubleType,
            // Wide floats are their bits: LLVM's own fp128 lowering is not used (its libcalls crash MCJIT on Win64), and
            // f256 has no LLVM type; the runtime's soft-float routines compute both.
            PrimitiveType { Kind: Primitive.F128 } => Ctx.GetIntType(128),
            PrimitiveType { Kind: Primitive.F256 } => Ctx.GetIntType(256),
            ReferenceType or GcType or BoxType or PointerType or FunctionType or OptionalType => ptr,
            StructType st => StructOf(st).Type,
            // Inline arrays are their bytes: elements are reached by offset, so strides follow Layouts exactly.
            FixedArrayType inline => LLVMTypeRef.CreateArray(Ctx.Int8Type, (uint)(Layouts.Of(inline, module)?.Size
                ?? throw new LlvmBackendException($"`{IrPrinter.FormatType(type, module)}` was not monomorphized"))),
            _ => throw new LlvmBackendException($"`{IrPrinter.FormatType(type, module)}` cannot be a native value"),
        };

        /// <summary>Packed LLVM struct with explicit padding so field offsets equal <see cref="Layouts.FieldOffsets"/>.</summary>
        public (LLVMTypeRef Type, int[] Elements) StructOf(StructType type)
        {
            if (type.Arguments.Length > 0)
                throw new LlvmBackendException($"`{IrPrinter.FormatType(type, module)}` was not monomorphized");
            if (structs.TryGetValue(type.Id, out var known))
                return known;

            var def = module.Structs[type.Id];
            var named = Module.Context.CreateNamedStruct(def.Name);
            var elements = new int[def.Fields.Length];
            structs[type.Id] = (named, elements);

            var offsets = Layouts.FieldOffsets(type, module) ?? throw new LlvmBackendException($"`{def.Name}` has no static layout");
            int size = Layouts.Of(type, module)!.Size;
            List<LLVMTypeRef> body = [];
            int position = 0;
            for (int i = 0; i < def.Fields.Length; i++)
            {
                if (offsets[i] > position)
                    body.Add(LLVMTypeRef.CreateArray(Ctx.Int8Type, (uint)(offsets[i] - position)));
                elements[i] = body.Count;
                // An array kept in place at the end: its length word here, the elements after the struct.
                body.Add(def.Fields[i] is ArrayType ? Ctx.Int64Type : ValueType(def.Fields[i]));
                position = offsets[i] + (def.Fields[i] is ArrayType ? 8 : Layouts.Of(def.Fields[i], module)!.Size);
            }
            if (size > position)
                body.Add(LLVMTypeRef.CreateArray(Ctx.Int8Type, (uint)(size - position)));
            named.StructSetBody([.. body], true);
            return (named, elements);
        }

        // ── Declarations ───────────────────────────────────────────

        private void DeclareRuntime()
        {
            var i64 = Ctx.Int64Type;
            var i32 = Ctx.Int32Type;
            var v = Ctx.VoidType;
            Runtime("larva_alloc", ptr, i64, i64);
            Runtime("larva_raw_alloc", ptr, i64, i64);
            Runtime("larva_exception_cell", ptr, i64, i64);
            Runtime("larva_global", ptr, ptr, i64);
            Runtime("larva_free", v, ptr);
            Runtime("larva_gc_new", ptr, i64, i64);
            Runtime("larva_gc_new_in", ptr, i64, i64, i64);
            Runtime("larva_gc_domain", i64, ptr);
            Runtime("larva_gc_alloc", ptr, ptr, i64);
            Runtime("larva_gc_array", ptr, ptr, i64);
            Runtime("larva_gc_retain", v, ptr);
            Runtime("larva_gc_release", v, ptr);
            Runtime("larva_rc_dead", v, ptr);
            Runtime("larva_gc_finalizer", v, ptr, ptr);
            Runtime("larva_print_i64", v, i64);
            Runtime("larva_print_u64", v, i64);
            Runtime("larva_print_f64", v, i64);
            Runtime("larva_print_bool", v, i32);
            Runtime("larva_print_str", v, ptr);
            Runtime("larva_print_line", v);
            Runtime("larva_print_utf8", v, ptr);
            Runtime("larva_read_line", i64);
            Runtime("larva_read_unit", i64, i64);
            Runtime("larva_args_count", i64);
            Runtime("larva_args_length", i64, i64);
            Runtime("larva_args_unit", i64, i64, i64);
            Runtime("larva_format_f64", i64, i64);
            Runtime("larva_format_unit", i64, i64);
            Runtime("larva_gc_request", v);
            Runtime("larva_gc_collections", i64);
            Runtime("larva_gc_objects", i64);
            Runtime("larva_gc_custom", v, i64, i64, i64);
            Runtime("larva_time_now", i64);
            Runtime("larva_time_sleep", v, i64);
            Runtime("larva_time_utc", i64);
            Runtime("larva_time_offset", i64, i64);
            Runtime("larva_time_precise", i64);
            Runtime("larva_thread_start", i64, ptr, ptr);
            Runtime("larva_thread_join", i64, i64, i64);
            Runtime("larva_thread_id", i64);
            Runtime("larva_thread_yield", v);
            Runtime("larva_cpu_count", i64);
            Runtime("larva_mutex_new", i64);
            Runtime("larva_mutex_lock", v, i64);
            Runtime("larva_mutex_trylock", i64, i64);
            Runtime("larva_mutex_unlock", v, i64);
            Runtime("larva_mutex_free", v, i64);
            Runtime("larva_cond_new", i64);
            Runtime("larva_cond_wait", i64, i64, i64, i64);
            Runtime("larva_cond_signal", v, i64);
            Runtime("larva_cond_broadcast", v, i64);
            Runtime("larva_cond_free", v, i64);
            Runtime("larva_stream_open", i64, ptr, i64);
            Runtime("larva_stream_close", i64, i64);
            Runtime("larva_stream_read", i64, i64, ptr, i64, i64);
            Runtime("larva_stream_write", i64, i64, ptr, i64, i64);
            Runtime("larva_stream_seek", i64, i64, i64, i64);
            Runtime("larva_stream_length", i64, i64);
            Runtime("larva_stream_flush", i64, i64);
            Runtime("larva_net_connect", i64, ptr, i64);
            Runtime("larva_net_listen", i64, i64);
            Runtime("larva_net_accept", i64, i64);
            Runtime("larva_net_port", i64, i64);
            Runtime("larva_proc_start", i64, ptr, ptr);
            Runtime("larva_proc_wait", i64, i64);
            Runtime("larva_math_sqrt", i64, i64);
            Runtime("larva_math_sin", i64, i64);
            Runtime("larva_math_cos", i64, i64);
            Runtime("larva_math_tan", i64, i64);
            Runtime("larva_math_asin", i64, i64);
            Runtime("larva_math_acos", i64, i64);
            Runtime("larva_math_atan", i64, i64);
            Runtime("larva_math_exp", i64, i64);
            Runtime("larva_math_log", i64, i64);
            Runtime("larva_math_log10", i64, i64);
            Runtime("larva_math_floor", i64, i64);
            Runtime("larva_math_ceil", i64, i64);
            Runtime("larva_math_atan2", i64, i64, i64);
            Runtime("larva_math_pow", i64, i64, i64);
            Runtime("larva_f64_bits", i64, i64);
            Runtime("larva_f64_from_bits", i64, i64);
            Runtime("larva_mem_alloc", ptr, i64);
            Runtime("larva_mem_free", v, ptr);
            Runtime("larva_mem_copy", v, ptr, ptr, i64);
            Runtime("larva_mem_cmp", i64, ptr, ptr, i64);
            Runtime("larva_native_symbol", i64, ptr, ptr);
            Runtime("larva_file_read", i64, ptr);
            Runtime("larva_file_take", v, ptr);
            Runtime("larva_file_write", i64, ptr, ptr, i64);
            Runtime("larva_file_kind", i64, ptr);
            Runtime("larva_file_delete", i64, ptr);
            Runtime("larva_file_time", i64, ptr);
            Runtime("larva_dir_create", i64, ptr);
            Runtime("larva_dir_list", i64, ptr);
            Runtime("larva_dir_length", i64, i64);
            Runtime("larva_dir_unit", i64, i64, i64);
            Runtime("larva_env_get", i64, ptr);
            Runtime("larva_env_unit", i64, i64);
            Runtime("larva_process_run", i64, ptr);
            Runtime("larva_parse_f64", i64, ptr);
            Runtime("larva_array_new", ptr, i64, i64, i64, i32);
            Runtime("larva_region_enter", v, i32);
            Runtime("larva_region_exit", v, i32);
            Runtime("larva_gc_wb", v, ptr, ptr, i64);
            Runtime("larva_pin", v, ptr);
            Runtime("larva_unpin", v, ptr);
            Runtime("larva_poll", v);
            // LARVA_POLL_OPAQUE=1: the safepoint is an ordinary call again (to tell whether an optimization across it is wrong).
            if (inlinePolls && !stackRoots && Environment.GetEnvironmentVariable("LARVA_POLL_OPAQUE") != "1")
                PollTouchesOnlyRuntime();
            Runtime("larva_panic", v, i32, i64, i64);
            // Soft floats (f128, f256): limbs by address, as the direct x86-64 back end calls them.
            Runtime("larva_float_add", v, ptr, ptr, ptr, i32);
            Runtime("larva_float_sub", v, ptr, ptr, ptr, i32);
            Runtime("larva_float_mul", v, ptr, ptr, ptr, i32);
            Runtime("larva_float_div", v, ptr, ptr, ptr, i32);
            Runtime("larva_float_rem", v, ptr, ptr, ptr, i32);
            Runtime("larva_float_cmp", i32, ptr, ptr, i32, i32);
            Runtime("larva_float_conv", v, ptr, ptr, i32, i32);
            Runtime("larva_region_depth", i32);
            Runtime("larva_region_unwind", v, i32);
            if (module.Traces(gc)) // a tracing domain's functions record their stack roots
                Runtime(StackMaps.Marker, v, ptr);
            DeclareExceptions();
        }

        /// <summary>Personality, <c>RaiseException</c>, the slots filters fill in, and <c>larva_throw(payload, type)</c>.</summary>
        private void DeclareExceptions()
        {
            var i32 = Ctx.Int32Type;
            var i64 = Ctx.Int64Type;
            if (flagExceptions)
            {
                // The exception in flight: visible to the host, which reports one that leaves the program.
                // Weak: the program, its shared libraries and the runtime all end up with the same one.
                payloadGlobal = Module.AddGlobal(ptr, "larva_exception");
                payloadGlobal.Initializer = LLVMValueRef.CreateConstNull(ptr);
                payloadGlobal.Linkage = LLVMLinkage.LLVMWeakAnyLinkage;
                typeGlobal = Module.AddGlobal(i64, "larva_exception_type");
                typeGlobal.Initializer = LLVMValueRef.CreateConstInt(i64, 0);
                typeGlobal.Linkage = LLVMLinkage.LLVMWeakAnyLinkage;
                return;
            }
            personality = Module.AddFunction("__C_specific_handler", LLVMTypeRef.CreateFunction(i32, [ptr, ptr, ptr, ptr]));
            var raiseType = LLVMTypeRef.CreateFunction(Ctx.VoidType, [i32, i32, i32, ptr]);
            // The runtime's larva_raise: RaiseException's signature, the handler found without entering the kernel.
            var raise = Module.AddFunction("larva_raise", raiseType);
            none = LLVM.ConstNull(LLVM.TokenTypeInContext(Module.Context));

            payloadGlobal = Module.AddGlobal(ptr, "larva.eh.payload");
            payloadGlobal.Initializer = LLVMValueRef.CreateConstNull(ptr);
            payloadGlobal.Linkage = LLVMLinkage.LLVMInternalLinkage;
            typeGlobal = Module.AddGlobal(i64, "larva.eh.type");
            typeGlobal.Initializer = LLVMValueRef.CreateConstInt(i64, 0);
            typeGlobal.Linkage = LLVMLinkage.LLVMInternalLinkage;
            filterType = LLVMTypeRef.CreateFunction(i32, [ptr, ptr]);

            // larva_throw(payload, type): larva_raise(code, NONCONTINUABLE, 2, [payload, type])
            throwType = LLVMTypeRef.CreateFunction(Ctx.VoidType, [ptr, i64]);
            throwFunction = Module.AddFunction("larva_throw", throwType);
            throwFunction.Linkage = LLVMLinkage.LLVMInternalLinkage;
            builder.PositionAtEnd(throwFunction.AppendBasicBlock("entry"));
            var information = builder.BuildAlloca(LLVMTypeRef.CreateArray(i64, 2), "information");
            builder.BuildStore(builder.BuildPtrToInt(throwFunction.GetParam(0), i64, ""), information);
            builder.BuildStore(throwFunction.GetParam(1), builder.BuildGEP2(Ctx.Int8Type, information, new[] { LLVMValueRef.CreateConstInt(i64, 8) }, ""));
            builder.BuildCall2(raiseType, raise, new[]
            {
                LLVMValueRef.CreateConstInt(i32, ExceptionCode), LLVMValueRef.CreateConstInt(i32, 1), LLVMValueRef.CreateConstInt(i32, 2), information,
            }, "");
            builder.BuildUnreachable();
        }

        /// <summary>Id of an exception type, the same in every native image of the program (0 means "any Larva exception").</summary>
        private int TypeId(IrType type)
        {
            if (!typeIds.TryGetValue(type, out int id))
                typeIds[type] = id = Larva.IR.Linking.NativeImages.ExceptionTypeId(type, module);
            return id;
        }

        /// <summary>
        /// SEH filter for a catch clause: <c>EXCEPTION_EXECUTE_HANDLER</c> when the exception is Larva's and its type id matches
        /// (any id for 0); it records the payload and type for the handler to pick up.
        /// </summary>
        private LLVMValueRef Filter(int id)
        {
            if (filters.TryGetValue(id, out var known))
                return known;
            var i32 = Ctx.Int32Type;
            var i64 = Ctx.Int64Type;
            var filter = Module.AddFunction($"larva.filter.{id}", filterType);
            filter.Linkage = LLVMLinkage.LLVMInternalLinkage;
            filters[id] = filter;

            var here = builder.InsertBlock;
            var start = filter.AppendBasicBlock("entry");
            var check = filter.AppendBasicBlock("check");
            var yes = filter.AppendBasicBlock("match");
            var no = filter.AppendBasicBlock("no");
            builder.PositionAtEnd(start);
            var record = builder.BuildLoad2(ptr, filter.GetParam(0), "record");                 // EXCEPTION_POINTERS.ExceptionRecord
            var code = builder.BuildLoad2(i32, record, "code");                                    // EXCEPTION_RECORD.ExceptionCode
            builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, code, LLVMValueRef.CreateConstInt(i32, ExceptionCode), ""), check, no);
            builder.PositionAtEnd(check);
            var type = builder.BuildLoad2(i64, builder.BuildGEP2(Ctx.Int8Type, record, new[] { LLVMValueRef.CreateConstInt(i64, 40) }, ""), "type");
            if (id == 0)
                builder.BuildBr(yes);
            else
                builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, type, LLVMValueRef.CreateConstInt(i64, (ulong)id), ""), yes, no);
            builder.PositionAtEnd(yes);
            var payload = builder.BuildLoad2(ptr, builder.BuildGEP2(Ctx.Int8Type, record, new[] { LLVMValueRef.CreateConstInt(i64, 32) }, ""), "payload");
            builder.BuildStore(payload, payloadGlobal);
            builder.BuildStore(type, typeGlobal);
            builder.BuildRet(LLVMValueRef.CreateConstInt(i32, 1));
            builder.PositionAtEnd(no);
            builder.BuildRet(LLVMValueRef.CreateConstInt(i32, 0));
            if (here.Handle != IntPtr.Zero)
                builder.PositionAtEnd(here);
            return filter;
        }

        private void Runtime(string name, LLVMTypeRef result, params LLVMTypeRef[] parameters)
        {
            var type = LLVMTypeRef.CreateFunction(result, parameters);
            runtime[name] = (Module.AddFunction(name, type), type);
        }

        /// <summary>
        /// Declares <c>larva_poll</c> as touching only memory the program cannot see (<c>memory(inaccessiblemem: readwrite)</c>),
        /// so a loop that calls something keeps the fields it uses in registers across its safepoint. Sound when the
        /// collector scans the stack and registers conservatively: whatever the code still holds is pinned (it neither
        /// moves nor changes), and what the collector does change that the code reads (the nursery's cursor, an
        /// object's age) is read with volatile loads (see <see cref="BumpAllocate"/> and <see cref="Barrier"/>).
        /// </summary>
        private unsafe void PollTouchesOnlyRuntime()
        {
            var (poll, _) = runtime["larva_poll"];
            var name = "memory"u8;
            fixed (byte* text = name)
            {
                uint kind = LLVM.GetEnumAttributeKindForName((sbyte*)text, (nuint)name.Length);
                // MemoryEffects: two bits (ref 1, mod 2) per location; inaccessible memory is location 1.
                var attribute = LLVM.CreateEnumAttribute(Ctx, kind, 3 << 2);
                LLVM.AddAttributeAtIndex(poll, LLVMAttributeIndex.LLVMAttributeFunctionIndex, attribute);
            }
        }

        /// <summary>A safepoint at the builder's position: a load and a branch, <c>larva_poll</c> on the cold path.</summary>
        private void Poll(LLVMValueRef function)
        {
            if (!inlinePolls)
                return;
            if (pollCell.Handle == IntPtr.Zero)
            {
                pollCell = Module.AddGlobal(ptr, "__imp_larva_poll_pending");
                pollCell.Linkage = LLVMLinkage.LLVMExternalLinkage;
            }
            var flagAddress = builder.BuildLoad2(ptr, pollCell, "poll.flag.address");
            var flag = builder.BuildLoad2(Ctx.Int32Type, flagAddress, "poll.flag");
            flag.Volatile = true;
            var due = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, flag, LLVMValueRef.CreateConstInt(Ctx.Int32Type, 0), "poll.due");
            var call = function.AppendBasicBlock("poll");
            var after = function.AppendBasicBlock("polled");
            var branch = builder.BuildCondBr(due, call, after);
            Unlikely(branch);
            builder.PositionAtEnd(call);
            CallRuntime("larva_poll");
            builder.BuildBr(after);
            builder.PositionAtEnd(after);
        }

        /// <summary>Branch weights: the first successor of <paramref name="branch"/> is rarely taken.</summary>
        private void Unlikely(LLVMValueRef branch, bool second = false)
        {
            var kind = "prof"u8.ToArray();
            fixed (byte* kindText = kind)
            {
                uint id = LLVM.GetMDKindIDInContext(Ctx, (sbyte*)kindText, (uint)kind.Length);
                var name = "branch_weights"u8.ToArray();
                LLVMOpaqueMetadata* nameMd;
                fixed (byte* nameText = name)
                    nameMd = LLVM.MDStringInContext2(Ctx, (sbyte*)nameText, (nuint)name.Length);
                var weights = stackalloc LLVMOpaqueMetadata*[3];
                weights[0] = nameMd;
                weights[1] = LLVM.ValueAsMetadata(LLVMValueRef.CreateConstInt(Ctx.Int32Type, second ? 2000UL : 1UL));
                weights[2] = LLVM.ValueAsMetadata(LLVMValueRef.CreateConstInt(Ctx.Int32Type, second ? 1UL : 2000UL));
                var node = LLVM.MDNodeInContext2(Ctx, weights, 3);
                LLVM.SetMetadata(branch, id, LLVM.MetadataAsValue(Ctx, node));
            }
        }

        // ── The runtime's heap, inline (larva_rt.c: GcHeader, LarvaHeap, LarvaType) ──

        /// <summary>Bytes of the header before a gc object's payload (larva_rt.c: GC_HEADER).</summary>
        private const int GcHeaderSize = 16;

        /// <summary>The largest object (header included) made in the nursery: larger ones are old from the start.</summary>
        private const int NurseryObjectMax = 2048;

        private const int TypeArray = 1, TypeConservative = 2, TypeLeaf = 4, TypeTrailing = 8;

        private LLVMValueRef heapCell;

        /// <summary>Whether objects are allocated inline and stores into them run the generational write barrier.</summary>
        private bool InlineHeap => InlineHeapFor(gc);

        /// <summary>The same for code of a GC domain with <paramref name="domain"/> as its GC.</summary>
        internal bool InlineHeapFor(GcConfig domain) =>
            inlinePolls && (domain.Capabilities & GcCapabilities.Tracing) != 0 && (domain.Capabilities & GcCapabilities.RefCounting) == 0;

        private readonly Dictionary<(IrType, bool, int), (LLVMValueRef Global, int Flags)> descriptors = [];

        /// <summary>
        /// The type descriptor of gc objects of <paramref name="type"/> (an array's element when <paramref name="array"/>):
        /// <c>{ i32 size, i32 stride, i32 flags, i16 handles, i16 cells, [i32 offsets] }</c>, what the collector reads to know
        /// where the object holds handles. Null when the type has no layout (the runtime then scans it conservatively).
        /// </summary>
        internal (LLVMValueRef Global, int Flags)? Descriptor(IrType type, bool array, int header = 0, int stride = 0, int domain = 0)
        {
            if (descriptors.TryGetValue((type, array, domain), out var known))
                return known;
            if (Layouts.Of(type, module) is not { } layout || Layouts.PointerMap(type, module) is not var (handles, cells, ambiguous))
                return null;
            // An object ending with an array in place: its size is up to the elements (the length is the word before), the
            // stride theirs; the handles are its fields' (the elements hold none).
            bool trailing = !array && type is StructType owner && Layouts.Trailing(owner, module) is not null;
            if (trailing)
                (array, header, stride) = (true, Layouts.Trailing((StructType)type, module)!.Value.Header, Layouts.Trailing((StructType)type, module)!.Value.Stride);
            // Bits 8..15: the GC domain of the objects made with the descriptor (the runtime copies it into their header).
            int flags = (array && !trailing ? TypeArray : 0) | (trailing ? TypeTrailing : 0)
                | (ambiguous ? TypeConservative : handles.Length == 0 && cells.Length == 0 ? TypeLeaf : 0) | (domain & 0xFF) << 8;
            var i32 = Ctx.Int32Type;
            var i16 = Ctx.Int16Type;
            int[] offsets = ambiguous ? [] : [.. handles, .. cells];
            var fields = new List<LLVMValueRef>
            {
                LLVMValueRef.CreateConstInt(i32, (ulong)(array ? header : layout.Size)),
                LLVMValueRef.CreateConstInt(i32, (ulong)(array ? stride : 0)),
                LLVMValueRef.CreateConstInt(i32, (ulong)flags),
                LLVMValueRef.CreateConstInt(i16, (ulong)(ambiguous ? 0 : handles.Length)),
                LLVMValueRef.CreateConstInt(i16, (ulong)(ambiguous ? 0 : cells.Length)),
                LLVMValueRef.CreateConstArray(i32, [.. offsets.Select(o => LLVMValueRef.CreateConstInt(i32, (ulong)o))]),
            };
            var initializer = Ctx.GetConstStruct([.. fields], false);
            var global = Module.AddGlobal(initializer.TypeOf, $"larva.type.{descriptors.Count}");
            global.Initializer = initializer;
            global.Linkage = LLVMLinkage.LLVMPrivateLinkage;
            global.IsGlobalConstant = true;
            global.Alignment = 8;
            var made = (global, flags);
            descriptors[(type, array && !trailing, domain)] = made;
            return made;
        }

        private LLVMValueRef Heap()
        {
            if (heapCell.Handle == IntPtr.Zero)
            {
                heapCell = Module.AddGlobal(ptr, "__imp_larva_heap");
                heapCell.Linkage = LLVMLinkage.LLVMExternalLinkage;
            }
            return builder.BuildLoad2(ptr, heapCell, "heap");
        }

        private LLVMValueRef At(LLVMValueRef pointer, long offset) =>
            builder.BuildGEP2(Ctx.Int8Type, pointer, new[] { LLVMValueRef.CreateConstInt(Ctx.Int64Type, (ulong)offset, true) }, "");

        /// <summary>
        /// A young object made inline: <paramref name="total"/> bytes (header included, 16-byte aligned) taken from the
        /// nursery's current block (<c>larva_heap.cursor</c> up to <c>limit</c>), whose memory is already zero; the header
        /// gets the type and a reference count of 1. The runtime (<c>larva_gc_alloc</c>) when the block is full or it says
        /// not to (<c>fast</c> clear: regions, its own collector…). <paramref name="small"/> also guards the inline path (an
        /// array's size is known only now).
        /// </summary>
        private LLVMValueRef BumpAllocate(LLVMValueRef function, LLVMValueRef total, LLVMValueRef? small, LLVMValueRef type, Func<LLVMValueRef> slow, int domain = 0)
        {
            var i64 = Ctx.Int64Type;
            var heap = Heap();
            // Volatile: a safepoint changes them (and is not a barrier to the optimizer, see PollTouchesOnlyRuntime).
            var fast = builder.BuildLoad2(Ctx.Int32Type, heap, "heap.fast");
            fast.Volatile = true;
            var cursor = builder.BuildLoad2(ptr, At(heap, 8), "nursery.cursor");
            cursor.Volatile = true;
            var limit = builder.BuildLoad2(ptr, At(heap, 16), "nursery.limit");
            limit.Volatile = true;
            var next = builder.BuildGEP2(Ctx.Int8Type, cursor, new[] { total }, "nursery.next");
            var fits = builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, builder.BuildPtrToInt(next, i64, ""), builder.BuildPtrToInt(limit, i64, ""), "");
            var ok = builder.BuildAnd(builder.BuildAnd(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, fast, LLVMValueRef.CreateConstInt(Ctx.Int32Type, 0), ""),
                builder.BuildIsNotNull(cursor, ""), ""), fits, "alloc.fast");
            if (small is { } guard)
                ok = builder.BuildAnd(ok, guard, "");
            var fastBlock = function.AppendBasicBlock("alloc.inline");
            var slowBlock = function.AppendBasicBlock("alloc.call");
            var join = function.AppendBasicBlock("alloc.done");
            Unlikely(builder.BuildCondBr(ok, fastBlock, slowBlock), second: true);
            builder.PositionAtEnd(fastBlock);
            // Volatile too, so they are in memory (in this order) before the next safepoint walks the block.
            builder.BuildStore(next, At(heap, 8)).Volatile = true;
            builder.BuildStore(type, cursor).Volatile = true;                                           // type
            // refcount 1; mark, flags 0; the domain byte; age 0.
            builder.BuildStore(LLVMValueRef.CreateConstInt(i64, 1UL | (ulong)(domain & 0xFF) << 48), At(cursor, 8)).Volatile = true;
            var payload = At(cursor, GcHeaderSize);
            builder.BuildBr(join);
            builder.PositionAtEnd(slowBlock);
            var called = slow();
            var slowEnd = builder.InsertBlock;
            builder.BuildBr(join);
            builder.PositionAtEnd(join);
            var result = builder.BuildPhi(ptr, "alloc");
            result.AddIncoming([payload, called], [fastBlock, slowEnd], 2);
            return result;
        }

        /// <summary>A gc object of <paramref name="type"/> (<paramref name="size"/> payload bytes): young and inline when it can be.</summary>
        internal LLVMValueRef AllocateObject(LLVMValueRef function, IrType type, int size, int align, bool inline, GcConfig domainGc, int domain = 0)
        {
            var i64 = Ctx.Int64Type;
            if (Descriptor(type, array: false, domain: domain) is not var (descriptor, flags))
                return domain == 0
                    ? CallRuntime("larva_gc_new", LLVMValueRef.CreateConstInt(i64, (ulong)size), LLVMValueRef.CreateConstInt(i64, (ulong)align))
                    : CallRuntime("larva_gc_new_in", LLVMValueRef.CreateConstInt(i64, (ulong)size), LLVMValueRef.CreateConstInt(i64, (ulong)align), LLVMValueRef.CreateConstInt(i64, (ulong)domain));
            int total = GcHeaderSize + (Math.Max(8, size) + 15) / 16 * 16;
            LLVMValueRef Slow() => CallRuntime("larva_gc_alloc", descriptor, LLVMValueRef.CreateConstInt(i64, (ulong)size));
            if (!inline || !InlineHeapFor(domainGc) || (flags & TypeConservative) != 0 || total > NurseryObjectMax)
                return Slow();
            return BumpAllocate(function, LLVMValueRef.CreateConstInt(i64, (ulong)total), null, descriptor, Slow, domain);
        }

        /// <summary>A zeroed gc array of <paramref name="length"/> elements: young and inline when it is small.</summary>
        internal LLVMValueRef AllocateArray(LLVMValueRef function, IrType element, LLVMValueRef length, int header, int stride, int flags, GcConfig domainGc, int domain = 0, StructType? owner = null)
        {
            var i64 = Ctx.Int64Type;
            LLVMValueRef I(long v) => LLVMValueRef.CreateConstInt(i64, (ulong)v, true);
            int lengthOffset = 0;
            if (owner is not null)
            {
                // An object with its array in place: the owner's descriptor, the length at its last field.
                var trailing = Layouts.Trailing(owner, module) ?? throw new LlvmBackendException($"`{IrPrinter.FormatType(owner, module)}` does not end with an array");
                (lengthOffset, header, stride) = (trailing.LengthOffset, trailing.Header, trailing.Stride);
            }
            var described = owner is null ? Descriptor(element, array: true, header, stride, domain) : Descriptor(owner, array: false, domain: domain);
            if (described is not var (descriptor, typeFlags))
                return owner is null
                    ? CallRuntime("larva_array_new", length, I(header), I(stride), LLVMValueRef.CreateConstInt(Ctx.Int32Type, (ulong)(flags | (domain & 0xFF) << 8)))
                    : throw new LlvmBackendException($"`{IrPrinter.FormatType(owner, module)}` has no layout");
            LLVMValueRef Slow() => CallRuntime("larva_gc_array", descriptor, length);
            if (!InlineHeapFor(domainGc) || (typeFlags & TypeConservative) != 0)
                return Slow();
            // Small enough for the nursery (checked before multiplying, so a huge length cannot overflow).
            int maxElements = stride == 0 ? int.MaxValue : (NurseryObjectMax - GcHeaderSize - header - 16) / Math.Max(1, stride);
            var small = builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, length, I(Math.Max(0, maxElements)), "array.small");
            var payloadSize = builder.BuildAdd(builder.BuildMul(builder.BuildSelect(small, length, I(0), ""), I(stride), ""), I(header), "array.size");
            var rounded = builder.BuildAnd(builder.BuildAdd(builder.BuildSelect(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, payloadSize, I(8), ""), I(8), payloadSize, ""), I(15), ""), I(~15L), "");
            var total = builder.BuildAdd(rounded, I(GcHeaderSize), "array.total");
            var array = BumpAllocate(function, total, small, descriptor, Slow, domain);
            // The length (on both paths: the runtime's arrays have it already; storing it again is harmless). Volatile: the
            // collector needs it to walk the block.
            builder.BuildStore(length, lengthOffset == 0 ? array : At(array, lengthOffset)).Volatile = true;
            return array;
        }

        /// <summary>
        /// The generational write barrier after <paramref name="size"/> bytes were stored at <paramref name="slot"/> in the gc
        /// object <paramref name="owner"/>: one load and a branch, and a call only when the owner is old (age 1).
        /// </summary>
        private void Barrier(LLVMValueRef function, LLVMValueRef owner, LLVMValueRef slot, int size)
        {
            // Volatile: a safepoint can make the owner old (see PollTouchesOnlyRuntime).
            var age = builder.BuildLoad2(Ctx.Int8Type, At(owner, -1), "age");
            age.Volatile = true;
            var old = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, age, LLVMValueRef.CreateConstInt(Ctx.Int8Type, 1), "old");
            var call = function.AppendBasicBlock("barrier");
            var after = function.AppendBasicBlock("stored");
            Unlikely(builder.BuildCondBr(old, call, after));
            builder.PositionAtEnd(call);
            CallRuntime("larva_gc_wb", owner, slot, LLVMValueRef.CreateConstInt(Ctx.Int64Type, (ulong)size));
            builder.BuildBr(after);
            builder.PositionAtEnd(after);
        }

        /// <summary>
        /// Reference counting: <paramref name="handle"/> gains an owner. In an executable the count is the header's
        /// (a null check, the domain's byte when domains mix, an add); in-process code asks the runtime.
        /// </summary>
        internal void Retain(LLVMValueRef function, LLVMValueRef handle)
        {
            if (!inlinePolls)
            {
                CallRuntime("larva_gc_retain", handle);
                return;
            }
            var after = CountedBlock(function, handle, "retain");
            var slot = At(handle, -8);
            var count = builder.BuildLoad2(Ctx.Int32Type, slot, "count");
            builder.BuildStore(builder.BuildAdd(count, LLVMValueRef.CreateConstInt(Ctx.Int32Type, 1), ""), slot);
            builder.BuildBr(after);
            builder.PositionAtEnd(after);
        }

        /// <summary>
        /// Reference counting: <paramref name="handle"/> loses an owner; at 0 the runtime frees it (and releases what it holds).
        /// </summary>
        internal void ReleaseHandle(LLVMValueRef function, LLVMValueRef handle)
        {
            if (!inlinePolls)
            {
                CallRuntime("larva_gc_release", handle);
                return;
            }
            var after = CountedBlock(function, handle, "release");
            var slot = At(handle, -8);
            var count = builder.BuildSub(builder.BuildLoad2(Ctx.Int32Type, slot, "count"), LLVMValueRef.CreateConstInt(Ctx.Int32Type, 1), "");
            builder.BuildStore(count, slot);
            var dead = function.AppendBasicBlock("dead");
            Unlikely(builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, count, LLVMValueRef.CreateConstInt(Ctx.Int32Type, 0), ""), dead, after));
            builder.PositionAtEnd(dead);
            CallRuntime("larva_rc_dead", handle);
            builder.BuildBr(after);
            builder.PositionAtEnd(after);
        }

        /// <summary>
        /// Branches to a new block when <paramref name="handle"/> is a counted object (not null; with mixed domains, its
        /// header's domain byte without the uncounted bit) and leaves the builder there; returns the block after.
        /// </summary>
        private LLVMBasicBlockRef CountedBlock(LLVMValueRef function, LLVMValueRef handle, string name)
        {
            var counted = function.AppendBasicBlock(name);
            var after = function.AppendBasicBlock(name + "d");
            var some = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, handle, LLVMValueRef.CreateConstNull(ptr), "");
            if (module.MixedDomains)
            {
                var check = function.AppendBasicBlock(name + "domain");
                builder.BuildCondBr(some, check, after);
                builder.PositionAtEnd(check);
                var domain = builder.BuildLoad2(Ctx.Int8Type, At(handle, -2), "domain");
                some = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, builder.BuildAnd(domain, LLVMValueRef.CreateConstInt(Ctx.Int8Type, (ulong)IrModule.UncountedDomain), ""),
                    LLVMValueRef.CreateConstInt(Ctx.Int8Type, 0), "");
            }
            builder.BuildCondBr(some, counted, after);
            builder.PositionAtEnd(counted);
            return after;
        }

        /// <summary>
        /// The intrinsics LLVM has an instruction or an intrinsic for (no call into the runtime): square root, floor and
        /// ceiling, and reinterpreting a float's bits. Null for the others.
        /// </summary>
        private static readonly HashSet<string> InlinedIntrinsics = ["math.sqrt", "math.floor", "math.ceil", "f64.bits", "f64.from_bits",
            "atomic.load.i64", "atomic.store.i64", "atomic.add.i64", "atomic.xchg.i64", "atomic.cas.i64",
            "atomic.load.i32", "atomic.store.i32", "atomic.add.i32", "atomic.xchg.i32", "atomic.cas.i32"];

        /// <summary>An atomic operation, as LLVM's instruction (sequentially consistent); its result (the value before).</summary>
        private LLVMValueRef Atomic(string name, LLVMValueRef[] arguments)
        {
            const LLVMAtomicOrdering order = LLVMAtomicOrdering.LLVMAtomicOrderingSequentiallyConsistent;
            var type = name.EndsWith("i64") ? Ctx.Int64Type : Ctx.Int32Type;
            uint align = name.EndsWith("i64") ? 8u : 4u;
            switch (name[7..name.LastIndexOf('.')])
            {
                case "load":
                {
                    var load = builder.BuildLoad2(type, arguments[0], "");
                    LLVM.SetOrdering(load, order);
                    LLVM.SetAlignment(load, align);
                    return load;
                }
                case "store":
                {
                    var store = builder.BuildStore(arguments[1], arguments[0]);
                    LLVM.SetOrdering(store, order);
                    LLVM.SetAlignment(store, align);
                    return default;
                }
                case "add":
                    return builder.BuildAtomicRMW(LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpAdd, arguments[0], arguments[1], order, false);
                case "xchg":
                    return builder.BuildAtomicRMW(LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpXchg, arguments[0], arguments[1], order, false);
                default:
                {
                    var pair = builder.BuildAtomicCmpXchg(arguments[0], arguments[1], arguments[2], order, order, false);
                    return builder.BuildExtractValue(pair, 0, "");
                }
            }
        }

        private LLVMValueRef InlineIntrinsic(string name, LLVMValueRef[] arguments)
        {
            if (name.StartsWith("atomic."))
                return Atomic(name, arguments);
            string? llvm = name switch
            {
                "math.sqrt" => "llvm.sqrt.f64",
                "math.floor" => "llvm.floor.f64",
                "math.ceil" => "llvm.ceil.f64",
                _ => null,
            };
            if (llvm is not null)
            {
                var type = LLVMTypeRef.CreateFunction(Ctx.DoubleType, [Ctx.DoubleType]);
                var function = Module.GetNamedFunction(llvm);
                if (function.Handle == IntPtr.Zero)
                    function = Module.AddFunction(llvm, type);
                return builder.BuildCall2(type, function, arguments, "");
            }
            return name == "f64.bits"
                ? builder.BuildBitCast(arguments[0], Ctx.Int64Type, "")
                : builder.BuildBitCast(arguments[0], Ctx.DoubleType, "");
        }

        private LLVMValueRef CallRuntime(string name, params LLVMValueRef[] arguments)
        {
            var (function, type) = runtime[name];
            return funclet.Handle == IntPtr.Zero ? builder.BuildCall2(type, function, arguments, "") : CallInFunclet(type, function, arguments);
        }

        private LLVMValueRef CallInFunclet(LLVMTypeRef type, LLVMValueRef function, LLVMValueRef[] arguments)
        {
            var tag = Encoding.ASCII.GetBytes("funclet");
            var name = new byte[] { 0 };
            fixed (byte* tagText = tag)
            fixed (byte* nameText = name)
            {
                LLVMOpaqueValue* pad = funclet;
                var bundle = LLVM.CreateOperandBundle((sbyte*)tagText, (nuint)tag.Length, &pad, 1);
                var values = arguments.Select(a => (nint)(LLVMOpaqueValue*)a).ToArray();
                fixed (nint* args = values)
                {
                    LLVMOpaqueOperandBundle* bundles = bundle;
                    var call = LLVM.BuildCallWithOperandBundles(builder, type, function, (LLVMOpaqueValue**)args, (uint)values.Length, &bundles, 1, (sbyte*)nameText);
                    LLVM.DisposeOperandBundle(bundle);
                    return call;
                }
            }
        }

        /// <summary>What can be compiled (shared by the generators of one program's objects; computed when null).</summary>
        private bool[]? compilable;

        /// <summary>A function's LLVM value, declared now if it was not yet (zero when it is left out).</summary>
        internal LLVMValueRef FunctionValue(int index)
        {
            if (functions[index].Handle == IntPtr.Zero && compilable![index])
                Declare(index);
            return functions[index];
        }

        private void Declare(int index)
        {
            var function = module.Functions[index];
            if (function.IsGeneric)
                return; // shared generic code needs witnesses: specialize for native targets
            var parameters = Enumerable.Range(1, function.ArgumentCount).Select(a => ValueType(function.Locals[a].Type)).ToArray();
            var result = function.ReturnType.IsUnit ? Ctx.VoidType : ValueType(function.ReturnType);
            functionTypes[index] = LLVMTypeRef.CreateFunction(result, parameters);
            functions[index] = Module.AddFunction(Larva.IR.Linking.NativeSymbols.Of(function, symbolPrefix), functionTypes[index]);

            foreach (var attribute in function.Attributes)
            {
                if (attribute.Name == IrFunction.InlineAttribute)
                    AddParameterAttribute(functions[index], -1, "alwaysinline");
                else if (attribute.Name == IrFunction.NoInlineAttribute)
                    AddParameterAttribute(functions[index], -1, "noinline");
                else if (attribute.Name == IrFunction.ColdAttribute)
                {
                    AddParameterAttribute(functions[index], -1, "cold");
                    AddParameterAttribute(functions[index], -1, "minsize");
                    AddParameterAttribute(functions[index], -1, "optsize");
                }
            }
            // The borrow checker guarantees what `noalias` promises: no other access while a borrow is alive.
            if (noalias)
            {
                for (int a = 1; a <= function.ArgumentCount; a++)
                {
                    if (function.Locals[a].Type is ReferenceType)
                        AddParameterAttribute(functions[index], a, "noalias");
                }
            }
        }

        private static void AddParameterAttribute(LLVMValueRef function, int parameter, string name)
        {
            var bytes = Encoding.ASCII.GetBytes(name);
            fixed (byte* text = bytes)
            {
                uint kind = LLVM.GetEnumAttributeKindForName((sbyte*)text, (nuint)bytes.Length);
                var attribute = LLVM.CreateEnumAttribute(function.TypeOf.Context, kind, 0);
                LLVM.AddAttributeAtIndex(function, (LLVMAttributeIndex)parameter, attribute);
            }
        }

        // ── Constants ──────────────────────────────────────────────

        public LLVMValueRef Constant(ConstOperand constant)
        {
            var type = constant.Type;
            switch (type)
            {
                case PrimitiveType { Kind: Primitive.Unit }:
                    return LLVMValueRef.CreateConstNull(unit);
                case PrimitiveType { Kind: Primitive.Bool }:
                    return LLVMValueRef.CreateConstInt(Ctx.Int1Type, constant.Bits.IsZero ? 0UL : 1UL);
                case PrimitiveType { IsInteger: true } p:
                    return Integer(Ctx.GetIntType((uint)p.BitWidth!.Value), constant.Bits, p.BitWidth.Value);
                case PrimitiveType { IsFloat: true, BitWidth: > 64 } p:
                    return Integer(Ctx.GetIntType((uint)p.BitWidth!.Value), constant.Bits, p.BitWidth.Value);
                case PrimitiveType { IsFloat: true } p:
                    var bits = Integer(Ctx.GetIntType((uint)p.BitWidth!.Value), constant.Bits, p.BitWidth.Value);
                    return LLVMValueRef.CreateConstBitCast(bits, ValueType(type));
                default:
                    throw new LlvmBackendException($"constant of type `{type}`");
            }
        }

        public static LLVMValueRef Integer(LLVMTypeRef type, BigInteger value, int width)
        {
            var modulus = BigInteger.One << width;
            var normalized = ((value % modulus) + modulus) % modulus;
            var words = new ulong[(width + 63) / 64];
            for (int w = 0; w < words.Length; w++)
                words[w] = (ulong)((normalized >> (64 * w)) & ulong.MaxValue);
            return LLVMValueRef.CreateConstIntOfArbitraryPrecision(type, words);
        }

        // ── Function bodies ────────────────────────────────────────

        private enum Release : byte { FreeBox, ReleaseHandle }

        private sealed class FunctionEmitter
        {
            private readonly LlvmCodegen cg;
            /// <summary>The GC of the function's domain (the program's, with one domain).</summary>
            private readonly GcConfig gc;
            /// <summary>The header domain byte of the objects the function makes (0 with one domain).</summary>
            private readonly int domainByte;
            private bool Has(GcCapabilities capability) => (gc.Capabilities & capability) != 0;
            private LLVMContextRef Ctx => cg.Ctx;
            private readonly IrFunction function;
            private readonly LLVMValueRef fn;
            private readonly LLVMBuilderRef b;
            private readonly IrModule module;
            private readonly TypeContext context;
            private readonly MoveAnalysis moves;
            private readonly Liveness liveness;
            private readonly HashSet<int> addressTaken;

            /// <summary>Locals that borrow a handle read from memory instead of taking a reference (<see cref="FieldBorrows"/>).</summary>
            private readonly HashSet<int> fieldBorrows;
            private readonly LLVMValueRef[] locals;
            private readonly Dictionary<Place, LLVMValueRef> flags = [];
            private readonly List<LLVMValueRef> pinSlots = [];
            private LLVMBasicBlockRef entry;
            private LLVMBasicBlockRef[] blocks = [];
            private Location current;

            // Exceptions: where the exception being handled lives, the shared cleanup pad, the region depth at entry.
            private LLVMValueRef caughtPayload;
            private LLVMValueRef caughtType;
            private LLVMValueRef regionDepth;
            private LLVMBasicBlockRef cleanup;
            private bool needsCleanup;
            private bool usesExceptions;
            private BoundsAnalysis? bounds;

            /// <summary>Whether the function polls at its entry (see <see cref="Larva.IR.Analysis.Safepoints"/>).</summary>
            public bool EntryPoll { get; init; }

            /// <summary>Whether a collection can happen while the function runs (else what it borrows needs no pin).</summary>
            public bool MayCollect { get; init; } = true;

            public FunctionEmitter(LlvmCodegen cg, IrFunction function, LLVMValueRef fn)
            {
                this.cg = cg;
                gc = cg.module.CodeGcOf(function.Domain, cg.gc);
                domainByte = cg.module.DomainByte(function.Domain, cg.gc);
                this.function = function;
                this.fn = fn;
                b = cg.builder;
                module = cg.module;
                context = TypeContext.For(function, module);
                liveness = Liveness.Compute(function);
                addressTaken = [.. function.Blocks.SelectMany(x => x.Statements).OfType<AssignStatement>()
                    .Select(x => x.Value).OfType<RefRvalue>().Select(r => r.Place.Local)];
                fieldBorrows = BorrowsHandles && !module.Traces(gc) ? FieldBorrows.Of(function, module, addressTaken, liveness) : [];
                moves = new MoveAnalysis(function, module, Transfers);
                locals = new LLVMValueRef[function.Locals.Length];
            }

            /// <summary>
            /// Whether a read of a handle hands its reference over instead of taking one (IlLowering's rule): a local
            /// nobody borrowed, read once here and dead afterwards. The move analysis then treats the read as a move, so
            /// the local's drop flag is cleared and it is not released again at its death.
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
                    && !Borrowed(place.Local)
                    && !fieldBorrows.Contains(place.Local)
                    && function.TypeOf(place, module).Unoptional() is GcType
                    && !addressTaken.Contains(place.Local)
                    && MoveAnalysis.Occurrences(function, place.Local, at) == 1
                    && !liveness.LiveAfter(at).Contains(place.Local);
            }


            /// <summary>
            /// Native code passes gc handles at +0 under reference counting: the caller keeps its reference for the call and
            /// the callee borrows it (no retain before, no release inside). A callee that keeps the handle copies it (a retain).
            /// </summary>
            private bool BorrowsHandles => cg.inlinePolls && Has(GcCapabilities.RefCounting);

            /// <summary>A parameter holding a borrowed handle: not owned until the function stores something else in it.</summary>
            private bool Borrowed(int local) =>
                BorrowsHandles && function.IsArgument(local) && function.Locals[local].Type.Unoptional() is GcType;

            /// <summary>The places an operation moves, except borrowed parameters (what they hold is not this frame's to give).</summary>
            private List<Place> Moved(IEnumerable<Operand> operands) =>
                [.. moves.MovedBy(operands, current).Where(p => !(p.IsLocal && Borrowed(p.Local)))];

            public void Emit()
            {
                if (cg.eliminateBoundsChecks)
                    bounds = BoundsAnalysis.Run(function, module);
                entry = fn.AppendBasicBlock("entry");
                b.PositionAtEnd(entry);
                for (int l = 0; l < function.Locals.Length; l++)
                    locals[l] = b.BuildAlloca(cg.ValueType(function.Locals[l].Type), Name(l));
                for (int l = 0; l < function.Locals.Length; l++)
                {
                    foreach (var (path, _, _) in Owned(function.Locals[l].Type, []))
                    {
                        var flag = b.BuildAlloca(Ctx.Int1Type, $"{Name(l)}.owned");
                        FlagStore(function.IsArgument(l) && !(path.Length == 0 && Borrowed(l)), flag);
                        flags[new Place(l, path)] = flag;
                    }
                }
                if (cg.module.Any(GcCapabilities.Tracing, gc) && cg.stackRoots)
                {
                    // Slots that may hold gc handles stay in memory and become this frame's stack roots (see StackMaps).
                    for (int l = 0; l < function.Locals.Length; l++)
                    {
                        var type = function.Locals[l].Type;
                        if (Larva.IR.Lowering.StackRoots.MayHoldTracked(type, context))
                            cg.CallRuntime(StackMaps.Marker, locals[l]);
                    }
                }
                for (int a = 1; a <= function.ArgumentCount; a++)
                    b.BuildStore(fn.GetParam((uint)(a - 1)), locals[a]);

                bool handles = function.Blocks.Any(x => x.Terminator is IUnwinding { Handlers.Length: > 0 });
                if (handles)
                {
                    caughtPayload = b.BuildAlloca(cg.ptr, "caught.payload");
                    b.BuildStore(LLVMValueRef.CreateConstNull(cg.ptr), caughtPayload);
                    caughtType = b.BuildAlloca(Ctx.Int64Type, "caught.type");
                }
                if (function.Blocks.SelectMany(x => x.Statements).Any(x => x is RegionEnterStatement))
                    regionDepth = cg.CallRuntime("larva_region_depth");
                needsCleanup = flags.Count > 0 || handles || regionDepth.Handle != IntPtr.Zero
                    || cg.module.Any(GcCapabilities.Moving, gc) && function.Blocks.SelectMany(x => x.Statements).Any(x => x is AssignStatement { Value: RefRvalue });

                blocks = [.. Enumerable.Range(0, function.Blocks.Length).Select(i => fn.AppendBasicBlock($"bb{i}"))];
                var loopHeaders = LoopHeaders();
                // Safepoints: every loop header, and the entry of a function that can be re-entered without a loop.
                bool entryPoll = EntryPoll;
                for (int i = 0; i < function.Blocks.Length; i++)
                {
                    b.PositionAtEnd(blocks[i]);
                    if (Has(GcCapabilities.Tracing) && (loopHeaders.Contains(i) || i == 0 && entryPoll))
                        cg.Poll(fn);
                    var block = function.Blocks[i];
                    for (int s = 0; s < block.Statements.Length; s++)
                    {
                        current = new Location(i, s);
                        Statement(block.Statements[s]);
                    }
                    current = new Location(i, block.Statements.Length);
                    Terminator(block.Terminator);
                }

                b.PositionAtEnd(entry);
                if (cleanup.Handle != IntPtr.Zero && !cg.flagExceptions)
                    EscapeOwned();
                b.BuildBr(blocks[0]);

                if (cleanup.Handle != IntPtr.Zero)
                    EmitCleanup();
                if (usesExceptions && !cg.flagExceptions)
                    LLVM.SetPersonalityFn(fn, cg.personality);
            }

            private string Name(int local) => function.Locals[local].Name is { } name ? $"{name}" : $"_{local}";

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
                    case FreeStatement free:
                    {
                        if (Has(GcCapabilities.RefCounting))
                        {
                            // The handles the memory held lose their owner.
                            foreach (var slot in Larva.IR.Analysis.HandleSlots.Of(free.Pointer.Deref(), function.TypeOf(free.Pointer, module).Pointee()!, module))
                                cg.ReleaseHandle(fn, Load(slot));
                        }
                        cg.CallRuntime("larva_free", Load(free.Pointer));
                        break;
                    }
                    case StoreGlobalStatement store:
                    {
                        var value = Operand(store.Value);
                        var address = Global(store.Global);
                        if (Has(GcCapabilities.RefCounting) && module.Globals[store.Global].Type.Unoptional() is GcType)
                            cg.ReleaseHandle(fn, b.BuildLoad2(cg.ptr, address, "")); // the old value loses its owner
                        b.BuildStore(value, address);
                        break;
                    }
                    case RegionEnterStatement enter:
                        cg.CallRuntime("larva_region_enter", I32(enter.Region));
                        break;
                    case RegionExitStatement exit:
                        cg.CallRuntime("larva_region_exit", I32(exit.Region));
                        break;
                    case NopStatement:
                        break;
                    default:
                        throw new LlvmBackendException($"statement {statement}");
                }
            }

            private void Assign(Place target, Rvalue value)
            {
                if (target.IsLocal && fieldBorrows.Contains(target.Local) && value is UseRvalue { Operand: CopyOperand borrowed })
                {
                    // The handle stays the memory's: no reference taken, none to give up (its drop flag stays clear).
                    Store(target, Load(borrowed.Place));
                    return;
                }
                var targetType = function.TypeOf(target, module);
                if (targetType.IsUnit)
                {
                    if (value is IntrinsicRvalue effect)
                        Rvalue(effect); // runs for its effect
                    return;
                }
                var result = value is CaughtRvalue
                    ? b.BuildLoad2(cg.ValueType(targetType), b.BuildLoad2(cg.ptr, caughtPayload, "payload"), "caught")
                    : Rvalue(value);
                var moved = Moved(MoveAnalysis.Operands(value));
                ReleaseOwned(target, moved);
                Store(target, result);
                UpdateFlags(moved, target);
            }

            private void Store(Place target, LLVMValueRef value)
            {
                var (address, type) = Address(target);
                var owner = lastOwner;
                var stored = b.BuildStore(value, address);
                if (IsScalarElement(target, type))
                    cg.TagElement(stored);
                // Generations: a store into an old object remembers it (handles only: numbers need no barrier).
                if (Has(GcCapabilities.Barriers) && cg.InlineHeapFor(gc) && owner.Handle != IntPtr.Zero && !Constraints.Satisfies(type, TypeConstraint.Unmanaged, context))
                    cg.Barrier(fn, owner, address, Layouts.Of(type, module)!.Size);
            }

            // ── Terminators ────────────────────────────────────────

            private void Terminator(Terminator terminator)
            {
                switch (terminator)
                {
                    case GotoTerminator jump:
                        b.BuildBr(blocks[jump.Target]);
                        break;
                    case IfTerminator branch:
                        b.BuildCondBr(Operand(branch.Condition), blocks[branch.Then], blocks[branch.Else]);
                        break;
                    case SwitchTerminator @switch:
                    {
                        var value = Operand(@switch.Value);
                        var instruction = b.BuildSwitch(value, blocks[@switch.Otherwise], (uint)@switch.Targets.Length);
                        for (int i = 0; i < @switch.Targets.Length; i++)
                            instruction.AddCase(LLVMValueRef.CreateConstInt(value.TypeOf, (ulong)i), blocks[@switch.Targets[i]]);
                        break;
                    }
                    case ReturnTerminator:
                        for (int l = 1; l < function.Locals.Length; l++)
                            ReleaseOwned(new Place(l), []);
                        foreach (var slot in pinSlots)
                            cg.CallRuntime("larva_unpin", b.BuildLoad2(cg.ptr, slot, ""));
                        if (caughtPayload.Handle != IntPtr.Zero)
                            cg.CallRuntime("larva_free", b.BuildLoad2(cg.ptr, caughtPayload, ""));
                        if (function.ReturnType.IsUnit)
                            b.BuildRetVoid();
                        else
                            b.BuildRet(b.BuildLoad2(cg.ValueType(function.ReturnType), locals[0], "result"));
                        break;
                    case CallTerminator call:
                    {
                        LLVMTypeRef calleeType;
                        LLVMValueRef calleeValue;
                        IrType returnType;
                        IrType[] parameters;
                        if (call.Callee is { } indirect)
                        {
                            // An indirect call: the signature comes from the `fn(…) -> …` type, lowered like a declaration.
                            var signature = (FunctionType)function.TypeOf(indirect, module);
                            calleeType = LLVMTypeRef.CreateFunction(signature.Result.IsUnit ? Ctx.VoidType : cg.ValueType(signature.Result),
                                [.. signature.Parameters.Select(cg.ValueType)]);
                            calleeValue = Operand(indirect);
                            returnType = signature.Result;
                            parameters = signature.Parameters;
                        }
                        else
                        {
                            var callee = module.Functions[call.Function];
                            if (cg.FunctionValue(call.Function).Handle == IntPtr.Zero)
                                throw new LlvmBackendException($"`{callee.Name}` is shared generic code; compile with GenericsMode.Specialize");
                            calleeType = cg.functionTypes[call.Function];
                            calleeValue = cg.functions[call.Function];
                            returnType = callee.ReturnType;
                            parameters = [.. Enumerable.Range(1, callee.ArgumentCount).Select(a => callee.Locals[a].Type)];
                        }
                        // Handles go at +0 (BorrowsHandles): a local's own value as it is, released right after the call when
                        // this was its last use; any other place's value retained for the call and released after it.
                        var arguments = new LLVMValueRef[call.Arguments.Length];
                        var lent = new List<Place>();
                        var lastUse = new List<Place>();
                        var held = new List<LLVMValueRef>();
                        for (int a = 0; a < call.Arguments.Length; a++)
                        {
                            var operand = call.Arguments[a];
                            var place = operand switch { CopyOperand copy => copy.Place, MoveOperand move => move.Place, _ => null };
                            if (BorrowsHandles && place is not null && a < parameters.Length && parameters[a].Unoptional() is GcType)
                            {
                                if (place.IsLocal && !addressTaken.Contains(place.Local))
                                {
                                    arguments[a] = Load(place);
                                    lent.Add(place);
                                    if (Transfers(operand, current))
                                        lastUse.Add(place);
                                    continue;
                                }
                                var value = Load(place);
                                cg.Retain(fn, value);
                                held.Add(value);
                                arguments[a] = value;
                                continue;
                            }
                            arguments[a] = Operand(operand);
                        }
                        // Arguments belong to the callee from here on, even if it throws (lent handles stay this frame's).
                        var moved = Moved(call.Arguments).Where(p => !lent.Contains(p)).ToList();
                        UpdateFlags(moved, null);
                        var result = Invoke(calleeType, calleeValue, arguments, Unwind(call.Handlers));
                        foreach (var place in lastUse)
                        {
                            ReleaseOwned(place, []);
                            UpdateFlags([place], null);
                        }
                        foreach (var value in held)
                            cg.ReleaseHandle(fn, value);
                        bool returnsValue = !returnType.IsUnit;
                        if (returnsValue)
                        {
                            ReleaseOwned(call.Destination, moved);
                            Store(call.Destination, result);
                        }
                        UpdateFlags([], returnsValue ? call.Destination : null);
                        b.BuildBr(blocks[call.Target]);
                        break;
                    }
                    case ThrowTerminator @throw:
                    {
                        var type = function.TypeOf(@throw.Value, module);
                        var value = Operand(@throw.Value);
                        UpdateFlags(Moved([@throw.Value]), null);
                        var layout = Layouts.Of(type, module)!;
                        var cell = cg.CallRuntime("larva_exception_cell", I64(layout.Size), I64(layout.Align));
                        b.BuildStore(value, cell);
                        if (cg.flagExceptions)
                        {
                            Raise(cell, I64(cg.TypeId(type)), @throw.Handlers);
                            break;
                        }
                        Invoke(cg.throwType, cg.throwFunction, [cell, I64(cg.TypeId(type))], Unwind(@throw.Handlers));
                        b.BuildUnreachable();
                        break;
                    }
                    case ResumeTerminator resume:
                    {
                        if (caughtPayload.Handle == IntPtr.Zero)
                            throw new LlvmBackendException($"`{function.Name}`: resume outside an exception handler");
                        var payload = b.BuildLoad2(cg.ptr, caughtPayload, "");
                        var type = b.BuildLoad2(Ctx.Int64Type, caughtType, "");
                        b.BuildStore(LLVMValueRef.CreateConstNull(cg.ptr), caughtPayload); // ownership goes with the exception
                        if (cg.flagExceptions)
                        {
                            Raise(payload, type, resume.Handlers);
                            break;
                        }
                        Invoke(cg.throwType, cg.throwFunction, [payload, type], Unwind(resume.Handlers));
                        b.BuildUnreachable();
                        break;
                    }
                    case DropTerminator drop:
                        ReleaseOwned(drop.Place, []);
                        UpdateFlags([drop.Place], null);
                        b.BuildBr(blocks[drop.Target]);
                        break;
                    case UnreachableTerminator:
                        cg.CallRuntime("larva_panic", I32(3), I64(0), I64(0));
                        b.BuildUnreachable();
                        break;
                    default:
                        throw new LlvmBackendException($"terminator {terminator}");
                }
            }

            // ── Exceptions ─────────────────────────────────────────

            /// <summary>A call that may unwind: an <c>invoke</c> when the exception has somewhere to go in this frame.</summary>
            private LLVMValueRef Invoke(LLVMTypeRef type, LLVMValueRef function, LLVMValueRef[] arguments, LLVMBasicBlockRef unwind)
            {
                if (cg.flagExceptions)
                {
                    // A plain call; then a pending exception goes to this site's handlers (or the caller).
                    var called = b.BuildCall2(type, function, arguments, "");
                    var pending = b.BuildICmp(LLVMIntPredicate.LLVMIntNE, b.BuildLoad2(cg.ptr, cg.payloadGlobal, "exception"), LLVMValueRef.CreateConstNull(cg.ptr), "");
                    var returned = fn.AppendBasicBlock("returned");
                    b.BuildCondBr(pending, unwind.Handle == IntPtr.Zero ? Propagate() : unwind, returned);
                    b.PositionAtEnd(returned);
                    return called;
                }
                if (unwind.Handle == IntPtr.Zero)
                    return b.BuildCall2(type, function, arguments, "");
                usesExceptions = true;
                var normal = fn.AppendBasicBlock("returned");
                var result = b.BuildInvoke2(type, function, arguments, normal, unwind, "");
                b.PositionAtEnd(normal);
                return result;
            }

            /// <summary>
            /// Where an exception from the current point goes: a chain of <c>catchswitch</c>es, one per clause (falling through to the
            /// cleanup pad), the cleanup pad alone, or nowhere (the caller) when the frame owns nothing.
            /// </summary>
            private LLVMBasicBlockRef Unwind(CatchClause[] clauses)
            {
                if (clauses.Length == 0)
                    return needsCleanup ? Cleanup() : default;
                if (cg.flagExceptions)
                    return FlagDispatch(clauses);

                // SEH (__C_specific_handler) allows one handler per catchswitch: one catchswitch per clause, each
                // unwinding to the next clause's, the last to the cleanup pad (or the caller).
                var here = b.InsertBlock;
                var unwindTo = needsCleanup ? Cleanup() : default;
                LLVMBasicBlockRef dispatch = default;
                for (int c = clauses.Length - 1; c >= 0; c--)
                {
                    var clause = clauses[c];
                    dispatch = fn.AppendBasicBlock("dispatch");
                    b.PositionAtEnd(dispatch);
                    var catchSwitch = LLVM.BuildCatchSwitch(b, cg.none, unwindTo, 1, Text("catch.switch"));
                    var handler = fn.AppendBasicBlock("catch");
                    LLVM.AddHandler(catchSwitch, handler);
                    b.PositionAtEnd(handler);
                    LLVMOpaqueValue* filter = cg.Filter(clause.Type is null ? 0 : cg.TypeId(clause.Type));
                    var pad = LLVM.BuildCatchPad(b, catchSwitch, &filter, 1, Text("catch.pad"));
                    var landing = fn.AppendBasicBlock("caught");
                    LLVM.BuildCatchRet(b, pad, landing);
                    b.PositionAtEnd(landing);
                    // The previous exception of this frame (if any) is finished; keep the new one for `caught` / `resume`.
                    cg.CallRuntime("larva_free", b.BuildLoad2(cg.ptr, caughtPayload, ""));
                    b.BuildStore(b.BuildLoad2(cg.ptr, cg.payloadGlobal, ""), caughtPayload);
                    b.BuildStore(b.BuildLoad2(Ctx.Int64Type, cg.typeGlobal, ""), caughtType);
                    b.BuildBr(blocks[clause.Block]);
                    unwindTo = dispatch;
                }
                usesExceptions = true;
                b.PositionAtEnd(here);
                return dispatch;
            }

            /// <summary>Flag exceptions: the exception in flight goes to the first clause whose type matches, else on.</summary>
            private LLVMBasicBlockRef FlagDispatch(CatchClause[] clauses)
            {
                var here = b.InsertBlock;
                var next = needsCleanup ? Cleanup() : Propagate();
                for (int c = clauses.Length - 1; c >= 0; c--)
                {
                    var clause = clauses[c];
                    var dispatch = fn.AppendBasicBlock("dispatch");
                    var landing = fn.AppendBasicBlock("caught");
                    b.PositionAtEnd(dispatch);
                    if (clause.Type is null)
                        b.BuildBr(landing);
                    else
                        b.BuildCondBr(b.BuildICmp(LLVMIntPredicate.LLVMIntEQ, b.BuildLoad2(Ctx.Int64Type, cg.typeGlobal, ""), I64(cg.TypeId(clause.Type)), ""), landing, next);
                    b.PositionAtEnd(landing);
                    // The previous exception of this frame (if any) is finished; the one in flight is now handled here.
                    cg.CallRuntime("larva_free", b.BuildLoad2(cg.ptr, caughtPayload, ""));
                    b.BuildStore(b.BuildLoad2(cg.ptr, cg.payloadGlobal, ""), caughtPayload);
                    b.BuildStore(b.BuildLoad2(Ctx.Int64Type, cg.typeGlobal, ""), caughtType);
                    b.BuildStore(LLVMValueRef.CreateConstNull(cg.ptr), cg.payloadGlobal);
                    b.BuildBr(blocks[clause.Block]);
                    next = dispatch;
                }
                b.PositionAtEnd(here);
                return next;
            }

            /// <summary>Flag exceptions: <c>throw</c> / <c>resume</c> set the exception in flight and go to the site's handlers.</summary>
            private void Raise(LLVMValueRef payload, LLVMValueRef type, CatchClause[] handlers)
            {
                b.BuildStore(payload, cg.payloadGlobal);
                b.BuildStore(type, cg.typeGlobal);
                var unwind = Unwind(handlers);
                b.BuildBr(unwind.Handle == IntPtr.Zero ? Propagate() : unwind);
            }

            private LLVMBasicBlockRef propagate;

            /// <summary>Flag exceptions: back to the caller with the exception still in flight (the result is not used).</summary>
            private LLVMBasicBlockRef Propagate()
            {
                if (propagate.Handle != IntPtr.Zero)
                    return propagate;
                var here = b.InsertBlock;
                propagate = fn.AppendBasicBlock("propagate");
                b.PositionAtEnd(propagate);
                if (function.ReturnType.IsUnit)
                    b.BuildRetVoid();
                else
                    b.BuildRet(LLVMValueRef.CreateConstNull(cg.ValueType(function.ReturnType)));
                b.PositionAtEnd(here);
                return propagate;
            }

            private LLVMBasicBlockRef Cleanup()
            {
                if (cleanup.Handle == IntPtr.Zero)
                    cleanup = fn.AppendBasicBlock("cleanup");
                return cleanup;
            }

            /// <summary>
            /// The frame's cleanup funclet: what is still owned (per drop flags) is released, the handled exception freed,
            /// pins dropped and regions opened by this call closed; then the exception continues to the caller.
            /// </summary>
            private void EmitCleanup()
            {
                b.PositionAtEnd(cleanup);
                if (cg.flagExceptions)
                {
                    for (int l = 0; l < function.Locals.Length; l++)
                        ReleaseOwned(new Place(l), []);
                    if (caughtPayload.Handle != IntPtr.Zero)
                        cg.CallRuntime("larva_free", b.BuildLoad2(cg.ptr, caughtPayload, ""));
                    foreach (var slot in pinSlots)
                        cg.CallRuntime("larva_unpin", b.BuildLoad2(cg.ptr, slot, ""));
                    if (regionDepth.Handle != IntPtr.Zero)
                        cg.CallRuntime("larva_region_unwind", regionDepth);
                    b.BuildBr(Propagate());
                    return;
                }
                var pad = LLVM.BuildCleanupPad(b, cg.none, null, 0, Text("cleanup.pad"));
                cg.funclet = pad;
                for (int l = 0; l < function.Locals.Length; l++)
                    ReleaseOwned(new Place(l), []);
                if (caughtPayload.Handle != IntPtr.Zero)
                    cg.CallRuntime("larva_free", b.BuildLoad2(cg.ptr, caughtPayload, ""));
                foreach (var slot in pinSlots)
                    cg.CallRuntime("larva_unpin", b.BuildLoad2(cg.ptr, slot, ""));
                if (regionDepth.Handle != IntPtr.Zero)
                    cg.CallRuntime("larva_region_unwind", regionDepth);
                cg.funclet = default;
                LLVM.BuildCleanupRet(b, pad, null);
                usesExceptions = true;
            }

            private static sbyte* Text(string text)
            {
                // Names are only cosmetic; a static empty string avoids leaking unmanaged memory per call.
                return (sbyte*)EmptyName;
            }

            private static readonly nint EmptyName = System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi("");

            // ── Values ─────────────────────────────────────────────

            private LLVMValueRef Operand(Operand operand)
            {
                switch (operand)
                {
                    case CopyOperand or MoveOperand:
                        var place = operand is CopyOperand copy ? copy.Place : ((MoveOperand)operand).Place;
                        var value = Load(place);
                        // Reference counting: a handle that gains an owner takes a reference, unless this read transfers it.
                        if (Has(GcCapabilities.RefCounting) && function.TypeOf(place, module).Unoptional() is GcType && !Transfers(operand, current))
                            cg.Retain(fn, value);
                        return value;
                    case ConstOperand constant:
                        return cg.Constant(constant);
                    default:
                        throw new LlvmBackendException($"operand {operand}");
                }
            }

            /// <summary>Global <paramref name="index"/>: an internal variable with its initial constant (or zero).</summary>
            private LLVMValueRef Global(int index)
            {
                var global = module.Globals[index];
                if (!global.IsPlain(module))
                {
                    // Handles live in a heap cell the collector scans; the global keeps its address. The cell is made on
                    // first use: after that, reading the global is a load (which a loop can hoist), not a call.
                    var slot = cg.GlobalVariable(index, global.Type, () => LLVMValueRef.CreateConstNull(cg.ptr), cg.ptr);
                    var cell = b.BuildLoad2(cg.ptr, slot, "global.cell");
                    var ready = b.InsertBlock;
                    var make = fn.AppendBasicBlock("global.make");
                    var join = fn.AppendBasicBlock("global.ready");
                    cg.Unlikely(b.BuildCondBr(b.BuildIsNotNull(cell, ""), join, make), second: true);
                    b.PositionAtEnd(make);
                    var made = cg.CallRuntime("larva_global", slot, I64(Layouts.Of(global.Type, module)!.Size));
                    var madeEnd = b.InsertBlock;
                    b.BuildBr(join);
                    b.PositionAtEnd(join);
                    var address = b.BuildPhi(cg.ptr, "global.address");
                    address.AddIncoming([cell, made], [ready, madeEnd], 2);
                    return address;
                }
                return cg.GlobalVariable(index, global.Type, () => global.Initial is { } initial
                    ? Operand(new ConstOperand(initial, global.Type))
                    : LLVMValueRef.CreateConstNull(cg.ValueType(global.Type)));
            }

            private LLVMValueRef Rvalue(Rvalue rvalue)
            {
                switch (rvalue)
                {
                    case UseRvalue use:
                        return Operand(use.Operand);
                    case BinaryRvalue binary:
                        return Binary(binary);
                    case UnaryRvalue unary:
                    {
                        var value = Operand(unary.Operand);
                        var type = function.TypeOf(unary.Operand, module);
                        return unary.Op switch
                        {
                            UnaryOp.Neg when IsWideFloat(type) => b.BuildXor(value, LlvmCodegen.Integer(value.TypeOf, BigInteger.One << (type.BitWidth!.Value - 1), type.BitWidth.Value), ""),
                            UnaryOp.Neg when type.IsFloat => b.BuildFNeg(value, ""),
                            UnaryOp.Neg => b.BuildNeg(value, ""),
                            _ => b.BuildNot(value, ""),
                        };
                    }
                    case CastRvalue cast:
                        return Convert(Operand(cast.Operand), function.TypeOf(cast.Operand, module), cast.Type);
                    case RefRvalue reference:
                        PinIfInsideGcObject(reference.Place);
                        return Address(reference.Place).Pointer;
                    case SizeOfRvalue size:
                        return LLVMValueRef.CreateConstInt(Ctx.Int64Type, (ulong)Layouts.Of(size.Type, module)!.Size);
                    case AllocRvalue alloc:
                    {
                        var valueType = function.TypeOf(alloc.Value, module);
                        var value = Operand(alloc.Value);
                        var layout = Layouts.Of(valueType, module)!;
                        var cell = alloc.Kind == AllocKind.Gc
                            ? cg.AllocateObject(fn, valueType, layout.Size, layout.Align, inline: alloc.Finalizer is null, gc, domainByte)
                            : cg.CallRuntime(alloc.Kind == AllocKind.Raw ? "larva_raw_alloc" : "larva_alloc", I64(layout.Size), I64(layout.Align));
                        b.BuildStore(value, cell);
                        if (alloc.Finalizer is { } finalizer)
                            cg.CallRuntime("larva_gc_finalizer", cell, Operand(finalizer));
                        return cell;
                    }
                    case ArrayRvalue array:
                    {
                        var length = ToI64(Operand(array.Length), function.TypeOf(array.Length, module));
                        // Like the VM: a negative length (or an unsigned one past i64) is an error, never a huge array.
                        Guard(b.BuildICmp(LLVMIntPredicate.LLVMIntSGE, length, I64(0), ""), 4, length, I64(0));
                        var (header, stride) = ArrayLayout(array.Element);
                        if (array.Owner is { } owner)
                        {
                            if (array.Kind == AllocKind.Gc)
                                return cg.AllocateArray(fn, array.Element, length, header, stride, 0, gc, domainByte, owner);
                            // A heap cell the program frees: the runtime puts the length before the elements (flag 4).
                            var trailing = Layouts.Trailing(owner, module) ?? throw new LlvmBackendException($"`{owner}` does not end with an array");
                            return cg.CallRuntime("larva_array_new", length, I64(trailing.Header), I64(trailing.Stride), I32(4 | (domainByte & 0xFF) << 8));
                        }
                        if (array.Kind == AllocKind.Gc)
                            return cg.AllocateArray(fn, array.Element, length, header, stride, array.ArrayFlags(module), gc, domainByte);
                        return cg.CallRuntime("larva_array_new", length, I64(header), I64(stride), I32(array.ArrayFlags(module) | (domainByte & 0xFF) << 8));
                    }
                    case LenRvalue len:
                        return LoadLength(Address(len.Array).Pointer);
                    case StaticRvalue data:
                        return cg.StaticGlobal(data.Data);
                    case IntrinsicRvalue intrinsic:
                    {
                        if (LlvmCodegen.InlinedIntrinsics.Contains(intrinsic.Name))
                            return cg.InlineIntrinsic(intrinsic.Name, [.. intrinsic.Arguments.Select(Operand)]);
                        var info = Intrinsics.Find(intrinsic.Name)!;
                        var arguments = intrinsic.Arguments.Select((argument, i) =>
                        {
                            var value = Operand(argument);
                            var type = info.Parameters[i];
                            // f64 travels as its bits; bool widens to i32 (the C signatures).
                            return type.IsFloat ? b.BuildBitCast(value, Ctx.Int64Type, "")
                                : type == IrType.Bool ? b.BuildZExt(value, Ctx.Int32Type, "")
                                : value;
                        }).ToArray();
                        var called = cg.CallRuntime(info.Symbol, arguments);
                        // Input gives an i64; a float comes back as its bits.
                        return info.Result.IsUnit ? default : info.Result.IsFloat ? b.BuildBitCast(called, Ctx.DoubleType, "") : called;
                    }
                    case ZeroRvalue zero:
                        return LLVMValueRef.CreateConstNull(cg.ValueType(zero.Type));
                    case GlobalRvalue global:
                    {
                        var type = module.Globals[global.Global].Type;
                        var value = b.BuildLoad2(cg.ValueType(type), Global(global.Global), "");
                        if (Has(GcCapabilities.RefCounting) && type.Unoptional() is GcType)
                            cg.Retain(fn, value); // the copy is a new owner
                        return value;
                    }
                    case FunctionRefRvalue reference:
                        if (cg.FunctionValue(reference.Function).Handle == IntPtr.Zero)
                            throw new LlvmBackendException($"`{module.Functions[reference.Function].Name}` is shared generic code; compile with GenericsMode.Specialize");
                        return cg.functions[reference.Function];
                    case HandleCastRvalue cast:
                        return Operand(cast.Handle); // the same address (opaque pointers)
                    case WrapRvalue wrap:
                        return Operand(wrap.Value);
                    case UnwrapRvalue unwrap:
                    {
                        var value = Operand(unwrap.Value);
                        Guard(b.BuildIsNotNull(value, ""), 5, I64(0), I64(0));
                        return value;
                    }
                    case IsNullRvalue test:
                        return b.BuildIsNull(b.BuildLoad2(cg.ptr, Address(test.Value).Pointer, ""), "");
                    default:
                        throw new LlvmBackendException($"rvalue {rvalue}");
                }
            }

            private LLVMValueRef Binary(BinaryRvalue binary)
            {
                var type = function.TypeOf(binary.Left, module);
                var left = Operand(binary.Left);
                var right = Operand(binary.Right);
                if (IsWideFloat(type))
                    return WideFloatBinary(binary.Op, left, right, type);
                if (type.IsFloat)
                {
                    return binary.Op switch
                    {
                        BinaryOp.Add => b.BuildFAdd(left, right, ""),
                        BinaryOp.Sub => b.BuildFSub(left, right, ""),
                        BinaryOp.Mul => b.BuildFMul(left, right, ""),
                        BinaryOp.Div => b.BuildFDiv(left, right, ""),
                        BinaryOp.Rem => b.BuildFRem(left, right, ""),
                        BinaryOp.Eq => b.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, left, right, ""),
                        BinaryOp.Ne => b.BuildFCmp(LLVMRealPredicate.LLVMRealUNE, left, right, ""),
                        BinaryOp.Lt => b.BuildFCmp(LLVMRealPredicate.LLVMRealOLT, left, right, ""),
                        BinaryOp.Le => b.BuildFCmp(LLVMRealPredicate.LLVMRealOLE, left, right, ""),
                        BinaryOp.Gt => b.BuildFCmp(LLVMRealPredicate.LLVMRealOGT, left, right, ""),
                        BinaryOp.Ge => b.BuildFCmp(LLVMRealPredicate.LLVMRealOGE, left, right, ""),
                        _ => throw new LlvmBackendException($"`{binary.Op}` on floats"),
                    };
                }

                bool signed = type.IsSigned;
                var rightType = function.TypeOf(binary.Right, module);
                if (rightType != type)
                    right = Convert(right, rightType, type);
                switch (binary.Op)
                {
                    case BinaryOp.Div or BinaryOp.Rem:
                        CheckNotZero(right);
                        return binary.Op == BinaryOp.Div
                            ? signed ? b.BuildSDiv(left, right, "") : b.BuildUDiv(left, right, "")
                            : signed ? b.BuildSRem(left, right, "") : b.BuildURem(left, right, "");
                    case BinaryOp.Shl or BinaryOp.Shr:
                        // Shift amounts wrap at the width, as in the VM (an oversized LLVM shift would be poison).
                        var amount = b.BuildAnd(right, LLVMValueRef.CreateConstInt(right.TypeOf, (ulong)(type.BitWidth!.Value - 1)), "");
                        return binary.Op == BinaryOp.Shl ? b.BuildShl(left, amount, "")
                            : signed ? b.BuildAShr(left, amount, "") : b.BuildLShr(left, amount, "");
                }
                return binary.Op switch
                {
                    BinaryOp.Add => b.BuildAdd(left, right, ""),
                    BinaryOp.Sub => b.BuildSub(left, right, ""),
                    BinaryOp.Mul => b.BuildMul(left, right, ""),
                    BinaryOp.And => b.BuildAnd(left, right, ""),
                    BinaryOp.Or => b.BuildOr(left, right, ""),
                    BinaryOp.Xor => b.BuildXor(left, right, ""),
                    BinaryOp.Eq => b.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, right, ""),
                    BinaryOp.Ne => b.BuildICmp(LLVMIntPredicate.LLVMIntNE, left, right, ""),
                    BinaryOp.Lt => b.BuildICmp(signed ? LLVMIntPredicate.LLVMIntSLT : LLVMIntPredicate.LLVMIntULT, left, right, ""),
                    BinaryOp.Le => b.BuildICmp(signed ? LLVMIntPredicate.LLVMIntSLE : LLVMIntPredicate.LLVMIntULE, left, right, ""),
                    BinaryOp.Gt => b.BuildICmp(signed ? LLVMIntPredicate.LLVMIntSGT : LLVMIntPredicate.LLVMIntUGT, left, right, ""),
                    BinaryOp.Ge => b.BuildICmp(signed ? LLVMIntPredicate.LLVMIntSGE : LLVMIntPredicate.LLVMIntUGE, left, right, ""),
                    _ => throw new LlvmBackendException($"`{binary.Op}`"),
                };
            }

            /// <summary>Numeric conversion with Larva semantics (sign follows the source type).</summary>
            private LLVMValueRef Convert(LLVMValueRef value, IrType from, IrType to)
            {
                if (from == to)
                    return value;
                var target = cg.ValueType(to);
                if (IsWideFloat(from) || IsWideFloat(to))
                    return WideFloatConvert(value, from, to);
                // Pointers (`T*`, `func*`) and addresses: the same pointer seen as another, or its bits as an integer.
                bool fromAddress = value.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind, toAddress = target.Kind == LLVMTypeKind.LLVMPointerTypeKind;
                if (fromAddress && toAddress)
                    return value;
                if (fromAddress)
                    return b.BuildPtrToInt(value, target, "");
                if (toAddress)
                    return b.BuildIntToPtr(value.TypeOf.IntWidth == 64 ? value : b.BuildZExt(value, Ctx.Int64Type, ""), target, "");
                if (to == IrType.Bool)
                    return from.IsFloat
                        ? b.BuildFCmp(LLVMRealPredicate.LLVMRealUNE, value, LLVMValueRef.CreateConstReal(value.TypeOf, 0), "")
                        : b.BuildICmp(LLVMIntPredicate.LLVMIntNE, value, LLVMValueRef.CreateConstInt(value.TypeOf, 0), "");
                if (from.IsFloat && to.IsFloat)
                    return from.BitWidth < to.BitWidth ? b.BuildFPExt(value, target, "") : b.BuildFPTrunc(value, target, "");
                if (from.IsFloat)
                {
                    // Saturating (as .NET converts): out of range gives the nearest limit and NaN gives 0, never poison.
                    string? source = value.TypeOf.Kind == LLVMTypeKind.LLVMDoubleTypeKind ? "f64" : value.TypeOf.Kind == LLVMTypeKind.LLVMFloatTypeKind ? "f32" : null;
                    if (source is not null)
                    {
                        string name = $"llvm.{(to.IsSigned ? "fptosi" : "fptoui")}.sat.i{target.IntWidth}.{source}";
                        var type = LLVMTypeRef.CreateFunction(target, [value.TypeOf]);
                        var function = cg.Module.GetNamedFunction(name);
                        if (function.Handle == IntPtr.Zero)
                            function = cg.Module.AddFunction(name, type);
                        return b.BuildCall2(type, function, new[] { value }, "");
                    }
                    return to.IsSigned ? b.BuildFPToSI(value, target, "") : b.BuildFPToUI(value, target, "");
                }
                bool signedSource = from.IsSigned;
                if (to.IsFloat)
                    return signedSource ? b.BuildSIToFP(value, target, "") : b.BuildUIToFP(value, target, "");
                int fromBits = from == IrType.Bool ? 1 : from.BitWidth!.Value;
                int toBits = to.BitWidth!.Value;
                if (fromBits == toBits)
                    return value;
                return fromBits > toBits ? b.BuildTrunc(value, target, "")
                    : signedSource ? b.BuildSExt(value, target, "") : b.BuildZExt(value, target, "");
            }

            private LLVMValueRef ToI64(LLVMValueRef value, IrType type) => Convert(value, type, type.IsSigned ? IrType.I64 : IrType.U64);

            // ── Wide floats (f128, f256): the runtime's soft-float routines, over values in stack slots ──

            private static bool IsWideFloat(IrType type) => type is PrimitiveType { IsFloat: true, BitWidth: > 64 };

            /// <summary>A stack slot made in the entry block (so a loop does not grow the stack).</summary>
            private LLVMValueRef EntryAlloca(LLVMTypeRef type, string name)
            {
                var current = b.InsertBlock;
                b.PositionBefore(entry.FirstInstruction);
                var slot = b.BuildAlloca(type, name);
                b.PositionAtEnd(current);
                return slot;
            }

            private LLVMValueRef Spilled(LLVMValueRef value, string name)
            {
                var slot = EntryAlloca(value.TypeOf, name);
                b.BuildStore(value, slot);
                return slot;
            }

            private LLVMValueRef WideFloatBinary(BinaryOp op, LLVMValueRef left, LLVMValueRef right, IrType type)
            {
                int limbs = type.BitWidth!.Value / 64;
                var a = Spilled(left, "wf.a");
                var c = Spilled(right, "wf.b");
                if (op is BinaryOp.Eq or BinaryOp.Ne or BinaryOp.Lt or BinaryOp.Le or BinaryOp.Gt or BinaryOp.Ge)
                {
                    int condition = op switch { BinaryOp.Eq => 0, BinaryOp.Ne => 1, BinaryOp.Lt => 2, BinaryOp.Le => 3, BinaryOp.Gt => 4, _ => 5 };
                    var holds = cg.CallRuntime("larva_float_cmp", a, c, I32(limbs), I32(condition));
                    return b.BuildICmp(LLVMIntPredicate.LLVMIntNE, holds, I32(0), "");
                }
                string name = op switch
                {
                    BinaryOp.Add => "larva_float_add",
                    BinaryOp.Sub => "larva_float_sub",
                    BinaryOp.Mul => "larva_float_mul",
                    BinaryOp.Div => "larva_float_div",
                    BinaryOp.Rem => "larva_float_rem",
                    _ => throw new LlvmBackendException($"`{op}` on `{type}`"),
                };
                var result = EntryAlloca(cg.ValueType(type), "wf.r");
                cg.CallRuntime(name, a, c, result, I32(limbs));
                return b.BuildLoad2(cg.ValueType(type), result, "");
            }

            /// <summary>The runtime's name for a type in <c>larva_float_conv</c>: its width, 0x1000 when signed, 0x2000 for a float.</summary>
            private static int FloatCode(IrType type) =>
                (type == IrType.Bool ? 1 : type.BitWidth!.Value) | (type.IsSigned ? Larva.Native.NativeRuntime.SignedCode : 0) | (type.IsFloat ? Larva.Native.NativeRuntime.FloatCode : 0);

            private LLVMValueRef WideFloatConvert(LLVMValueRef value, IrType from, IrType to)
            {
                if (to == IrType.Bool)
                {
                    // Nonzero of either sign (NaN counts).
                    int width = from.BitWidth!.Value;
                    var magnitude = b.BuildAnd(value, LlvmCodegen.Integer(value.TypeOf, (BigInteger.One << (width - 1)) - 1, width), "");
                    return b.BuildICmp(LLVMIntPredicate.LLVMIntNE, magnitude, LlvmCodegen.Integer(value.TypeOf, BigInteger.Zero, width), "");
                }
                // Integers up to 64 bits go in and come out in their canonical 64-bit form (extended by signedness).
                var source = !from.IsFloat && (from == IrType.Bool || from.BitWidth!.Value <= 64) ? ToI64(value, from) : value;
                var src = Spilled(source, "wf.src");
                var wanted = cg.ValueType(to);
                var slotType = !to.IsFloat && to.BitWidth!.Value <= 64 ? Ctx.Int64Type : wanted;
                var dst = EntryAlloca(slotType, "wf.dst");
                cg.CallRuntime("larva_float_conv", src, dst, I32(FloatCode(from)), I32(FloatCode(to)));
                var loaded = b.BuildLoad2(slotType, dst, "");
                return slotType == wanted ? loaded : b.BuildTrunc(loaded, wanted, "");
            }

            // ── Places ─────────────────────────────────────────────

            private LLVMValueRef Load(Place place)
            {
                var (address, type) = Address(place);
                if (type is ArrayType)
                    throw new LlvmBackendException($"`{IrPrinter.Format(place)}` is an unsized array; index it or take Len");
                var loaded = b.BuildLoad2(cg.ValueType(type), address, "");
                if (IsScalarElement(place, type))
                    cg.TagElement(loaded);
                return loaded;
            }

            /// <summary>An array element of a number or pointer type (the accesses the element alias tag covers).</summary>
            private static bool IsScalarElement(Place place, IrType type) =>
                place.Projections.Length > 0 && place.Projections[^1] is IndexProjection
                && type is PrimitiveType or GcType or BoxType or PointerType or OptionalType { Inner: GcType or BoxType or PointerType };

            /// <summary>Address of a place: GEPs for fields, loads for dereferences, bounds-checked element pointers for indices.</summary>
            /// <summary>The gc object the last <see cref="Address"/> went into (its last dereference), if it went into one.</summary>
            private LLVMValueRef lastOwner;

            private (LLVMValueRef Pointer, IrType Type) Address(Place place)
            {
                var address = locals[place.Local];
                IrType type = function.Locals[place.Local].Type;
                lastOwner = default;
                for (int p = 0; p < place.Projections.Length; p++)
                {
                    switch (place.Projections[p])
                    {
                        case DerefProjection:
                            address = b.BuildLoad2(cg.ptr, address, "");
                            if (type is OptionalType)
                                Guard(b.BuildIsNotNull(address, ""), 5, I64(0), I64(0)); // null reference
                            lastOwner = type.Unoptional() is GcType ? address : default;
                            type = type.Pointee()!;
                            break;
                        case FieldProjection field:
                            var st = (StructType)type;
                            var (structType, elements) = cg.StructOf(st);
                            address = b.BuildStructGEP2(structType, address, (uint)elements[field.FieldIndex], "");
                            type = module.Structs[st.Id].Fields[field.FieldIndex];
                            break;
                        case IndexProjection inlineIndex when type is FixedArrayType inline:
                        {
                            var inlineElement = inline.Element;
                            var elementLayout = Layouts.Of(inlineElement, module)!;
                            int inlineStride = (elementLayout.Size + elementLayout.Align - 1) / elementLayout.Align * elementLayout.Align;
                            int length = inline.Length(module)!.Value;
                            var position = ToI64(b.BuildLoad2(cg.ValueType(function.Locals[inlineIndex.Local].Type), locals[inlineIndex.Local], ""), function.Locals[inlineIndex.Local].Type);
                            cg.BoundsChecks++;
                            Guard(b.BuildICmp(LLVMIntPredicate.LLVMIntULT, position, I64(length), ""), 1, position, I64(length));
                            address = b.BuildGEP2(Ctx.Int8Type, address, new[] { b.BuildMul(position, I64(inlineStride), "") }, "");
                            type = inlineElement;
                            break;
                        }
                        case IndexProjection index:
                            var element = ((ArrayType)type).Element;
                            var (header, stride) = ArrayLayout(element);
                            var i = ToI64(b.BuildLoad2(cg.ValueType(function.Locals[index.Local].Type), locals[index.Local], ""), function.Locals[index.Local].Type);
                            if ((p == 1 || Larva.IR.Analysis.BoundsAnalysis.InPlace(function, module, place, p)) && bounds?.IsSafe(current, place.Local, index.Local) == true)
                                cg.BoundsChecksEliminated++;
                            else
                                CheckBounds(address, i);
                            var offset = b.BuildAdd(b.BuildMul(i, I64(stride), ""), I64(header), "");
                            address = b.BuildGEP2(Ctx.Int8Type, address, new[] { offset }, "");
                            type = element;
                            break;
                    }
                }
                return (address, type);
            }

            private (int Header, int Stride) ArrayLayout(IrType element)
            {
                var layout = Layouts.Of(element, module)!;
                int stride = Math.Max(1, (layout.Size + layout.Align - 1) / layout.Align * layout.Align);
                return (Math.Max(8, layout.Align), stride);
            }

            private void CheckBounds(LLVMValueRef array, LLVMValueRef index)
            {
                cg.BoundsChecks++;
                var length = LoadLength(array);
                var inside = b.BuildICmp(LLVMIntPredicate.LLVMIntULT, index, length, "");
                Guard(inside, 1, index, length);
            }

            /// <summary>
            /// An array's length: in <c>[0, 2^63)</c>, which lets LLVM fold the checks it can prove. Not marked
            /// <c>invariant.load</c>: that let LLVM sink the load past the <c>larva_free</c> of the array.
            /// </summary>
            private LLVMValueRef LoadLength(LLVMValueRef array)
            {
                var length = b.BuildLoad2(Ctx.Int64Type, array, "len");
                cg.TagLength(length);
                var context = cg.Module.Context;
                var range = new[] { LLVM.ValueAsMetadata(I64(0)), LLVM.ValueAsMetadata(I64(long.MaxValue)) };
                fixed (LLVMOpaqueMetadata** values = range)
                    length.SetMetadata(cg.Kind("range"), LLVM.MetadataAsValue(context, LLVM.MDNodeInContext2(context, values, 2)));
                return length;
            }

            private void CheckNotZero(LLVMValueRef divisor)
            {
                var nonZero = b.BuildICmp(LLVMIntPredicate.LLVMIntNE, divisor, LLVMValueRef.CreateConstInt(divisor.TypeOf, 0), "");
                Guard(nonZero, 2, I64(0), I64(0));
            }

            /// <summary>Continues when <paramref name="condition"/> holds; otherwise calls <c>larva_panic</c>.</summary>
            private void Guard(LLVMValueRef condition, int code, LLVMValueRef a, LLVMValueRef bValue)
            {
                var ok = fn.AppendBasicBlock("ok");
                var fail = fn.AppendBasicBlock("panic");
                b.BuildCondBr(condition, ok, fail);
                b.PositionAtEnd(fail);
                cg.CallRuntime("larva_panic", I32(code), a, bValue);
                b.BuildUnreachable();
                b.PositionAtEnd(ok);
            }

            // ── Ownership ──────────────────────────────────────────

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

            /// <summary>
            /// Releases what the value at <paramref name="place"/> owns. Resources of locals are guarded by their drop flag;
            /// resources behind a pointer belong to that memory and are always initialized.
            /// </summary>
            private void ReleaseOwned(Place place, IReadOnlyList<Place> movedNow)
            {
                foreach (var (path, release, _) in Owned(function.TypeOf(place, module), []))
                {
                    var resource = new Place(place.Local, [.. place.Projections, .. path]);
                    if (movedNow.Any(m => m.IsPrefixOf(resource) || resource.IsPrefixOf(m)))
                        continue;

                    LLVMBasicBlockRef done = default;
                    if (flags.TryGetValue(resource, out var flag))
                    {
                        var owned = fn.AppendBasicBlock("release");
                        done = fn.AppendBasicBlock("released");
                        b.BuildCondBr(b.BuildLoad2(Ctx.Int1Type, flag, ""), owned, done);
                        b.PositionAtEnd(owned);
                    }
                    var handle = Load(resource);
                    if (release == Release.FreeBox)
                        cg.CallRuntime("larva_free", handle);
                    else
                        cg.ReleaseHandle(fn, handle);
                    if (flag.Handle != IntPtr.Zero)
                    {
                        FlagStore(false, flag);
                        b.BuildBr(done);
                        b.PositionAtEnd(done);
                    }
                }
            }

            /// <summary>Drop flags: cleared for places given up here, set for the place just initialized.</summary>
            private void UpdateFlags(IEnumerable<Place> cleared, Place? initialized)
            {
                foreach (var place in cleared)
                {
                    foreach (var (resource, flag) in flags)
                    {
                        if (place.IsPrefixOf(resource) || resource.IsPrefixOf(place))
                            FlagStore(false, flag);
                    }
                }
                if (initialized is not null)
                {
                    foreach (var (resource, flag) in flags)
                    {
                        if (initialized.IsPrefixOf(resource))
                            FlagStore(true, flag);
                    }
                }
            }

            private void FlagStore(bool value, LLVMValueRef flag) => b.BuildStore(Bool(value), flag);

            /// <summary>Owning locals from which a frame with a cleanup keeps them in memory (see <see cref="EscapeOwned"/>).</summary>
            private const int EscapeThreshold = 24;

            /// <summary>
            /// With a cleanup funclet, what it releases (owning locals and their drop flags) stays in the frame
            /// (<c>llvm.localescape</c>): promoted to registers, every call that may throw would first copy all of them out for
            /// the funclet, hundreds of stores a turn in a large function's loop. A small frame stays in registers: its few
            /// copies cost less than keeping every flag and handle in memory.
            /// </summary>
            private void EscapeOwned()
            {
                var slots = new List<LLVMValueRef>();
                for (int l = 0; l < function.Locals.Length; l++)
                    if (Owned(function.Locals[l].Type, []).Any())
                        slots.Add(locals[l]);
                if (slots.Count < EscapeThreshold)
                    return;
                slots.AddRange(flags.Values);
                var type = LLVMTypeRef.CreateFunction(Ctx.VoidType, [], IsVarArg: true);
                var escape = cg.Module.GetNamedFunction("llvm.localescape");
                if (escape.Handle == IntPtr.Zero)
                    escape = cg.Module.AddFunction("llvm.localescape", type);
                b.BuildCall2(type, escape, slots.ToArray(), "");
            }

            /// <summary>Moving GCs: a borrow inside a <c>gc</c> object pins it until the function returns.</summary>
            private void PinIfInsideGcObject(Place place)
            {
                if (!cg.module.Any(GcCapabilities.Moving, gc) || !MayCollect)
                    return;
                for (int j = 0; j < place.Projections.Length; j++)
                {
                    var handle = new Place(place.Local, place.Projections[..j]);
                    if (place.Projections[j] is DerefProjection && function.TypeOf(handle, module).Unoptional() is GcType)
                    {
                        var value = Load(handle);
                        cg.CallRuntime("larva_pin", value);
                        var here = b.InsertBlock;
                        b.PositionBefore(entry.FirstInstruction);
                        var slot = b.BuildAlloca(cg.ptr, "pin");
                        b.BuildStore(LLVMValueRef.CreateConstNull(cg.ptr), slot);
                        b.PositionAtEnd(here);
                        b.BuildStore(value, slot);
                        pinSlots.Add(slot);
                        return;
                    }
                }
            }

            /// <summary>
            /// Blocks a jump goes back to (loops), where safepoints poll. Exception edges do not count: a handler block starts by
            /// reading the exception, which no collection may run before.
            /// </summary>
            private HashSet<int> LoopHeaders() => Larva.IR.Analysis.Safepoints.PollingLoopHeaders(function, module);

            private static IEnumerable<int> NormalSuccessors(Terminator terminator) => terminator switch
            {
                CallTerminator call => [call.Target],
                ThrowTerminator or ResumeTerminator => [],
                _ => terminator.Successors,
            };

            private LLVMValueRef Bool(bool value) => LLVMValueRef.CreateConstInt(Ctx.Int1Type, value ? 1UL : 0UL);
            private LLVMValueRef I32(long value) => LLVMValueRef.CreateConstInt(Ctx.Int32Type, (ulong)value, true);
            private LLVMValueRef I64(long value) => LLVMValueRef.CreateConstInt(Ctx.Int64Type, (ulong)value, true);
        }
    }
}

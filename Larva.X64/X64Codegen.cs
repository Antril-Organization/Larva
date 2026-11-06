using System.Numerics;
using Larva.IR;
using Larva.IR.Analysis;
using Larva.IR.Lowering;
using Larva.Native;

namespace Larva.X64
{
    public sealed class X64BackendException(string message) : Exception(message);

    /// <summary>A compiled function inside the module's code: where it is, its frame, and its GC stack roots.</summary>
    public sealed record X64Function(string Name, int Offset, int Size, int FrameSize, StackRoot[] Roots, byte[] UnwindInfo, UnwindFixup[] UnwindFixups);

    /// <summary>
    /// A 32-bit RVA inside a function's unwind info, filled in when the image is laid out: of <see cref="Symbol"/> (the
    /// personality routine) or, when that is <c>null</c>, of <see cref="CodeOffset"/> in the module's code.
    /// </summary>
    public readonly record struct UnwindFixup(int At, string? Symbol, int CodeOffset);

    /// <summary>A <c>__C_specific_handler</c> scope: code range, filter function and jump target (code offsets).</summary>
    internal readonly record struct Scope(int Begin, int End, int Filter, int Target);

    /// <summary>
    /// Direct x86-64 code generation for the Win64 ABI, without LLVM: a baseline compiler that keeps every local in its stack
    /// slot and evaluates each statement through <c>rax</c> / <c>rcx</c> / <c>rdx</c> (and <c>xmm0</c> / <c>xmm1</c>), so it
    /// needs no register allocator and every value has an address at every call — which is also what the tracing GC needs.
    /// <list type="bullet">
    /// <item>Frames: <c>push rbp; mov rbp, rsp; sub rsp, N</c>, described by Win64 unwind info so the OS (exceptions,
    /// <c>RtlVirtualUnwind</c>) and the collector can walk them. Only volatile registers are used besides <c>rbp</c>.</item>
    /// <item>Calls follow the Microsoft x64 convention: four register arguments (integer or <c>xmm</c> by position), shadow
    /// space, structs of 1/2/4/8 bytes by value and larger ones by reference to a copy, large results through a hidden
    /// pointer.</item>
    /// <item>Semantics follow the LLVM back end and the VM: drop flags, reference counting, write barriers, pins, polls
    /// and bounds checks (minus those <see cref="BoundsAnalysis"/> proves), the same <c>larva_*</c> runtime.</item>
    /// </list>
    /// <c>i128</c>/<c>u128</c> are 16-byte values (passed by reference between Larva functions) with add/sub/mul,
    /// bitwise operations, comparisons and casts.
    /// <para>
    /// Exceptions are Windows SEH with <c>__C_specific_handler</c>, as in the LLVM back end: <c>throw</c> raises Larva's
    /// exception code with a heap cell holding the value and its type id. Every call, throw or resume site that can be
    /// caught or needs cleanup gets scope-table entries covering its <c>call</c> (plus a <c>nop</c>, so the return address
    /// falls inside): one per catch clause, whose generated filter checks code and type id and records the payload in the
    /// handling frame (the establisher frame it receives), and, when the frame owns something, a last "any Larva exception"
    /// entry whose landing releases what the frame owns, unpins, closes its regions and raises the exception again.
    /// </para>
    /// Wide integers are limbs in the frame (division and shifts call the runtime); f16 is converted to and from f32
    /// with F16C; f128 and f256 are limbs too, computed by the runtime's soft-float routines (<c>larva_float_*</c>).
    /// </summary>
    internal sealed class X64Codegen
    {
        private readonly IrModule module;
        private readonly GcConfig gc;
        private readonly bool eliminateBoundsChecks;

        public Assembler Asm { get; } = new();
        public List<X64Function> Functions { get; } = [];

        /// <summary>Static arrays used by the code, each a read-only blob with the heap array layout.</summary>
        public List<X64Data> Data { get; } = [];
        private readonly Dictionary<int, string> dataSymbols = [];

        private readonly Dictionary<int, string> globalSymbols = [];

        /// <summary>Global variable N: writable bytes (<c>.data</c>) with its initial value.</summary>
        internal string GlobalSymbol(int index, bool slotOnly = false)
        {
            lock (Data)
                return GlobalSymbolLocked(index, slotOnly);
        }

        private string GlobalSymbolLocked(int index, bool slotOnly)
        {
            if (globalSymbols.TryGetValue(index, out var symbol))
                return symbol;
            var global = module.Globals[index];
            var bytes = new byte[slotOnly ? 8 : Math.Max(1, Layouts.Of(global.Type, module)!.Size)];
            if (global.Initial is { } initial)
            {
                var value = initial.Sign < 0 ? initial + (BigInteger.One << (bytes.Length * 8)) : initial;
                var raw = value.ToByteArray(isUnsigned: true, isBigEndian: false);
                raw.AsSpan(0, Math.Min(raw.Length, bytes.Length)).CopyTo(bytes);
            }
            symbol = $"larva.global.{index}";
            Data.Add(new X64Data(symbol, bytes, Writable: true));
            return globalSymbols[index] = symbol;
        }

        internal string DataSymbol(int index)
        {
            lock (Data)
            {
                if (dataSymbols.TryGetValue(index, out var symbol))
                    return symbol;
                symbol = $"larva.data.{index}";
                Data.Add(new X64Data(symbol, Layouts.StaticBytes(module.Data[index], module, out _)));
                return dataSymbols[index] = symbol;
            }
        }

        private readonly Dictionary<(IrType, int, bool), string?> typeSymbols = [];

        /// <summary>
        /// The runtime's description of gc objects of <paramref name="type"/> (<c>{ i32 size, i32 stride, i32 flags,
        /// i16 handles, i16 cells, i32 offsets[] }</c>, as the LLVM back end makes it), when nothing in the program traces:
        /// an object whose count reaches 0 then releases its handles. Null otherwise (the object stays conservative: a
        /// tracing collector scans it word by word and never moves it), or when the type has no exact map. With
        /// <paramref name="array"/>, the map of an array's elements (<paramref name="header"/> bytes before them).
        /// </summary>
        internal string? TypeSymbol(IrType type, int domain, bool array = false, int header = 0, int stride = 0)
        {
            if (!Has(GcCapabilities.RefCounting) || module.Traces(gc))
                return null;
            lock (Data)
            {
                if (typeSymbols.TryGetValue((type, domain, array), out var known))
                    return known;
                string? symbol = null;
                if (Layouts.Of(type, module) is { } layout && Layouts.PointerMap(type, module) is var (handles, cells, ambiguous) && !ambiguous
                    && !(type is StructType owner && Layouts.Trailing(owner, module) is not null))
                {
                    const int typeArray = 1, leaf = 4;
                    int flags = (array ? typeArray : 0) | (handles.Length == 0 && cells.Length == 0 ? leaf : 0) | (domain & 0xFF) << 8;
                    var bytes = new List<byte>();
                    bytes.AddRange(BitConverter.GetBytes(array ? header : layout.Size));
                    bytes.AddRange(BitConverter.GetBytes(array ? stride : 0));
                    bytes.AddRange(BitConverter.GetBytes(flags));
                    bytes.AddRange(BitConverter.GetBytes((ushort)handles.Length));
                    bytes.AddRange(BitConverter.GetBytes((ushort)cells.Length));
                    foreach (int offset in handles.Concat(cells))
                        bytes.AddRange(BitConverter.GetBytes(offset));
                    symbol = $"larva.type.{typeSymbols.Count}";
                    Data.Add(new X64Data(symbol, [.. bytes]));
                }
                return typeSymbols[(type, domain, array)] = symbol;
            }
        }

        private int boundsChecks, boundsChecksEliminated;
        public int BoundsChecks => boundsChecks;
        public int BoundsChecksEliminated => boundsChecksEliminated;

        /// <summary>Counts a bounds check emitted, or one left out because <see cref="BoundsAnalysis"/> proved it.</summary>
        internal void CountBoundsCheck(bool eliminated)
        {
            if (eliminated)
                Interlocked.Increment(ref boundsChecksEliminated);
            else
                Interlocked.Increment(ref boundsChecks);
        }

        private readonly string? symbolPrefix;

        /// <summary>The object-file symbol of a Larva function.</summary>
        internal string Symbol(IrFunction function) => Larva.IR.Linking.NativeSymbols.Of(function, symbolPrefix);

        public X64Codegen(IrModule module, GcConfig gc, bool eliminateBoundsChecks, string? symbolPrefix = null)
        {
            this.symbolPrefix = symbolPrefix;
            this.module = module;
            this.gc = gc;
            this.eliminateBoundsChecks = eliminateBoundsChecks;
        }

        internal bool Has(GcCapabilities capability) => (gc.Capabilities & capability) != 0;
        internal GcConfig Gc => gc;


        internal IrModule Module => module;
        internal bool EliminateBoundsChecks => eliminateBoundsChecks;

        /// <summary>SEH exception code of Larva exceptions (same as the LLVM back end).</summary>
        internal const uint ExceptionCode = 0xE04C5641;

        private readonly Dictionary<IrType, int> typeIds = [];

        /// <summary>Id of an exception type, the same in every native image of the program (0 means "any Larva exception").</summary>
        internal int TypeId(IrType type)
        {
            lock (typeIds)
            {
                if (!typeIds.TryGetValue(type, out int id))
                    typeIds[type] = id = Larva.IR.Linking.NativeImages.ExceptionTypeId(type, module);
                return id;
            }
        }

        /// <summary>Functions left out (see <see cref="X64Result.Skipped"/>).</summary>
        public Dictionary<string, string> Skipped { get; } = [];

        public void Generate()
        {
            // Functions over what this back end lacks (nothing among the primitives now; kept for what a module may add) are
            // left out; using one is the error.
            var unsupported = new Dictionary<string, string>();
            var compilable = Larva.IR.Linking.Compilable.Of(module, X64Backend.Supports);
            var compiled = new List<int>();
            for (int index = 0; index < module.Functions.Length; index++)
            {
                var function = module.Functions[index];
                if (function.IsGeneric)
                    continue; // shared generic code needs witnesses: specialize for native targets
                if (function.IsDeclaration)
                    continue; // defined in another native image (a DLL): called by symbol
                if (!compilable[index])
                {
                    unsupported[Symbol(function)] = Unsupported(function) ?? $"`{function.Name}` uses a function this back end cannot compile";
                    Skipped[function.Name] = unsupported[Symbol(function)];
                    continue;
                }
                if (Unsupported(function) is { } problem)
                {
                    unsupported[Symbol(function)] = problem;
                    continue;
                }
                compiled.Add(index);
            }

            // Each function compiles into its own assembler, on several threads; the pieces are then laid out in order, at
            // 16-byte boundaries (code inside a function is position independent: branches are relative).
            var entryPolls = Larva.IR.Analysis.Safepoints.EntryPolls(module);
            var results = new (X64Function Function, Assembler Code)?[compiled.Count];
            var failures = new X64BackendException?[compiled.Count];
            Parallel.For(0, compiled.Count, i =>
            {
                try
                {
                    var compiler = new FunctionCompiler(this, module.Functions[compiled[i]]) { EntryPoll = entryPolls[compiled[i]] };
                    results[i] = (compiler.Compile(), compiler.Code);
                }
                catch (X64BackendException e)
                {
                    failures[i] = e;
                }
            });
            if (failures.FirstOrDefault(f => f is not null) is { } failure)
                throw failure;
            foreach (var result in results)
            {
                var (function, code) = result!.Value;
                while (Asm.Position % 16 != 0)
                    Asm.Int3();
                int at = Asm.Position;
                Asm.Append(code);
                Functions.Add(function with
                {
                    Offset = function.Offset + at,
                    UnwindFixups = [.. function.UnwindFixups.Select(f => f.Symbol is null ? f with { CodeOffset = f.CodeOffset + at } : f)],
                });
            }
            Data.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name)); // threads add them in any order
            foreach (var relocation in Asm.Relocations)
            {
                if (unsupported.TryGetValue(relocation.Symbol, out var problem))
                    throw new X64BackendException(problem);
            }
        }

        private string? Unsupported(IrFunction function)
        {
            try
            {
                foreach (var local in function.Locals)
                    Shape(local.Type);
                return null;
            }
            catch (X64BackendException e)
            {
                return $"`{function.Name}`: {e.Message}";
            }
        }

        // ── Value representation ───────────────────────────────

        internal enum Kind : byte { Unit, Scalar, Float, Aggregate, Wide }

        /// <summary>Win64: what does not fit a register travels by reference (and comes back through a hidden pointer).</summary>
        internal static bool ByReference(Kind kind, int size) => kind == Kind.Wide || kind == Kind.Aggregate && !InRegister(size);

        /// <summary>How a type is held: scalars are canonical 64-bit values (extended by signedness) once loaded.</summary>
        internal (Kind Kind, int Size, bool Signed) Shape(IrType type)
        {
            switch (type)
            {
                case PrimitiveType { Kind: Primitive.Unit }:
                    return (Kind.Unit, 0, false);
                case PrimitiveType { Kind: Primitive.Bool }:
                    return (Kind.Scalar, 1, false);
                case PrimitiveType { Kind: Primitive.F16 or Primitive.F32 or Primitive.F64 } p:
                    return (Kind.Float, p.BitWidth!.Value / 8, false);
                case PrimitiveType { IsInteger: true } p when p.BitWidth <= 64:
                    return (Kind.Scalar, p.BitWidth!.Value / 8, p.IsSigned);
                case PrimitiveType { Kind: Primitive.I128 or Primitive.U128 or Primitive.F128 } p:
                    return (Kind.Wide, 16, p.IsSigned);
                case PrimitiveType { Kind: Primitive.I256 or Primitive.U256 or Primitive.F256 } p:
                    return (Kind.Wide, 32, p.IsSigned);
                case PrimitiveType p:
                    throw new X64BackendException($"`{p}` is not supported by the direct x86-64 back end (use the LLVM back end)");
                case ReferenceType or GcType or BoxType or PointerType or FunctionType or OptionalType:
                    return (Kind.Scalar, 8, false);
                case StructType or FixedArrayType:
                    var layout = Layouts.Of(type, module) ?? throw new X64BackendException($"no layout for `{type}`");
                    return (Kind.Aggregate, layout.Size, false);
                default:
                    throw new X64BackendException($"values of type `{type}` are not supported by the direct x86-64 back end");
            }
        }

        /// <summary>Win64: aggregates of 1, 2, 4 or 8 bytes travel in a register; others by reference.</summary>
        internal static bool InRegister(int size) => size is 1 or 2 or 4 or 8;

        internal (int Header, int Stride) ArrayLayout(IrType element)
        {
            var layout = Layouts.Of(element, module)!;
            int stride = Math.Max(1, (layout.Size + layout.Align - 1) / layout.Align * layout.Align);
            return (Math.Max(8, layout.Align), stride);
        }

        /// <summary>The two 64-bit halves of a 128-bit constant.</summary>
        /// <summary>A wide constant as its 64-bit limbs, lowest first (two's complement for negative values).</summary>
        internal static long[] Limbs(ConstOperand constant, int limbs)
        {
            var bits = constant.Bits & ((BigInteger.One << (64 * limbs)) - 1);
            var result = new long[limbs];
            for (int i = 0; i < limbs; i++)
                result[i] = (long)(ulong)((bits >> (64 * i)) & ulong.MaxValue);
            return result;
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

        // ── Unwind info ────────────────────────────────────────

        internal const int PrologueSize = 11; // push rbp (1) + mov rbp, rsp (3) + sub rsp, imm32 (7)

        /// <summary>
        /// UNWIND_INFO for the fixed prologue, with RBP as frame register; with scopes, followed by the exception handler
        /// (<c>__C_specific_handler</c>) and its scope table, whose RVAs are left as fixups.
        /// </summary>
        internal static (byte[] Info, UnwindFixup[] Fixups) UnwindInfo(int frameSize, IReadOnlyList<Scope> scopes)
        {
            var codes = new List<byte>();
            if (frameSize / 8 <= ushort.MaxValue)
            {
                codes.AddRange([PrologueSize, 0x01, (byte)(frameSize / 8), (byte)(frameSize / 8 >> 8)]); // UWOP_ALLOC_LARGE, size/8
            }
            else
            {
                codes.AddRange([PrologueSize, 0x11]); // UWOP_ALLOC_LARGE with a 32-bit size
                codes.AddRange(BitConverter.GetBytes(frameSize));
            }
            codes.AddRange([4, 0x03]);         // UWOP_SET_FPREG (rbp = rsp + 0)
            codes.AddRange([1, 0x50]);         // UWOP_PUSH_NONVOL rbp
            int slots = codes.Count / 2;
            byte flags = scopes.Count > 0 ? (byte)(1 << 3) : (byte)0; // UNW_FLAG_EHANDLER
            var info = new List<byte> { (byte)(0x01 | flags), PrologueSize, (byte)slots, 0x05 }; // version 1; frame register rbp, offset 0
            info.AddRange(codes);
            if (slots % 2 != 0)
                info.AddRange([0, 0]);
            var fixups = new List<UnwindFixup>();
            if (scopes.Count > 0)
            {
                fixups.Add(new UnwindFixup(info.Count, "__C_specific_handler", 0));
                info.AddRange(new byte[4]);
                info.AddRange(BitConverter.GetBytes(scopes.Count));
                foreach (var scope in scopes)
                {
                    foreach (int offset in new[] { scope.Begin, scope.End, scope.Filter, scope.Target })
                    {
                        fixups.Add(new UnwindFixup(info.Count, null, offset));
                        info.AddRange(new byte[4]);
                    }
                }
            }
            return ([.. info], [.. fixups]);
        }
    }

    /// <summary>Compiles one function into an assembler of its own.</summary>
    internal sealed class FunctionCompiler
    {
        private static readonly Reg[] IntegerArguments = [Reg.Rcx, Reg.Rdx, Reg.R8, Reg.R9];

        private readonly X64Codegen cg;
        /// <summary>The GC of the function's domain (the program's, with one domain).</summary>
        private readonly GcConfig gc;
        /// <summary>The header domain byte of the objects the function makes (0 with one domain).</summary>
        private readonly int domainByte;
        private bool Has(GcCapabilities capability) => (gc.Capabilities & capability) != 0;
        private readonly Assembler a;

        /// <summary>The function's code (offsets from its start), once compiled.</summary>
        public Assembler Code => a;
        private readonly IrFunction function;
        private readonly IrModule module;
        private readonly TypeContext context;
        private readonly MoveAnalysis moves;
        private readonly Liveness liveness;
        private readonly HashSet<int> addressTaken;
        private readonly BoundsAnalysis? bounds;

        private readonly int[] slots;
        private readonly Dictionary<Place, int> flags = [];
        private readonly Dictionary<Location, int> pins = [];
        private readonly Label[] blocks;
        private readonly Label epilogue = new(), panicBounds = new(), panicDivide = new(), panicLength = new(), panicNull = new();
        private readonly List<(int Offset, int Size)> temps = [];
        private int tempCursor;
        private int frame;            // bytes below rbp used by slots
        private int outgoing = 32;    // outgoing argument area (shadow space at least)
        private int returnPointer = int.MinValue;
        private Location current;

        // Exceptions: the exception being handled (caught), the one a filter just matched (incoming), the arguments of
        // RaiseException, the region depth at entry; the sites with their scope entries and the landings they jump to.
        private bool handles, needsCleanup;
        private int caughtPayload = int.MinValue, caughtType, incomingPayload, incomingType, raiseArguments, regionDepth = int.MinValue;
        private readonly List<(int Begin, int End, CatchClause[] Handlers)> sites = [];
        private readonly Dictionary<int, Label> clauseLandings = [];
        private readonly Label cleanupLanding = new();

        /// <summary>Whether the function polls at its entry (see <see cref="Larva.IR.Analysis.Safepoints"/>).</summary>
        public bool EntryPoll { get; init; }

        public FunctionCompiler(X64Codegen cg, IrFunction function)
        {
            this.cg = cg;
            gc = cg.Module.CodeGcOf(function.Domain, cg.Gc);
            domainByte = cg.Module.DomainByte(function.Domain, cg.Gc);
            a = new Assembler();
            this.function = function;
            module = cg.Module;
            context = TypeContext.For(function, module);
            liveness = Liveness.Compute(function);
            addressTaken = [.. function.Blocks.SelectMany(x => x.Statements).OfType<AssignStatement>()
                .Select(x => x.Value).OfType<RefRvalue>().Select(r => r.Place.Local)];
            moves = new MoveAnalysis(function, module, Transfers);
            bounds = cg.EliminateBoundsChecks ? BoundsAnalysis.Run(function, module) : null;
            slots = new int[function.Locals.Length];
            blocks = [.. function.Blocks.Select(_ => new Label())];
        }

        private int Slot(int size, int align)
        {
            frame = (frame + Math.Max(1, size) + align - 1) / align * align;
            return -frame;
        }

        /// <summary>A scratch slot for the current statement (reused by later statements).</summary>
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

        private static Mem Frame(int offset) => Mem.At(Reg.Rbp, offset);

        /// <summary>
        /// A safepoint: <c>larva_poll</c> only when the runtime's flag says a collection may be due (one load and a branch
        /// otherwise). The flag is reached through <c>__imp_larva_poll_pending</c>, as an import from the runtime DLL.
        /// </summary>
        private void Poll()
        {
            var skip = new Label();
            a.LoadRip(Reg.Rax, "__imp_larva_poll_pending");
            a.Load(Reg.Rax, Mem.At(Reg.Rax), 4, signed: false);
            a.Test(Reg.Rax, Reg.Rax);
            a.Jcc(Cond.E, skip);
            a.Call("larva_poll");
            a.Bind(skip);
        }

        public X64Function Compile()
        {
            int start = a.Position;
            for (int l = 0; l < function.Locals.Length; l++)
            {
                var type = function.Locals[l].Type;
                var layout = type.IsUnit ? null : Layouts.Of(type, module);
                slots[l] = layout is null ? 0 : Slot(layout.Size, Math.Min(16, Math.Max(8, layout.Align)));
                cg.Shape(type); // rejects unsupported types early
            }
            foreach (var (path, _, _) in Enumerable.Range(0, function.Locals.Length).SelectMany(l => Owned(function.Locals[l].Type, []).Select(o => (new Place(l, o.Path), o.Release, o.Type))))
                flags[path] = Slot(1, 1);
            PlanPins();
            PlanExceptions();

            // Prologue (fixed size: the unwind info describes it).
            a.PushRbp();
            a.Mov(Reg.Rbp, Reg.Rsp);
            int frameSizeAt = a.Position + 3;
            a.SubRsp32(0);

            foreach (var (place, flag) in flags)
                a.StoreByte(Frame(flag), function.IsArgument(place.Local) && !(place.Projections.Length == 0 && Borrowed(place.Local)) ? (byte)1 : (byte)0);
            foreach (var pin in pins.Values)
            {
                a.MovImm(Reg.Rax, 0);
                a.Store(Frame(pin), Reg.Rax, 8);
            }
            StoreParameters();
            if (handles)
            {
                a.MovImm(Reg.Rax, 0);
                a.Store(Frame(caughtPayload), Reg.Rax, 8);
            }
            if (regionDepth != int.MinValue)
            {
                a.Call("larva_region_depth");
                a.Store(Frame(regionDepth), Reg.Rax, 8);
            }

            var loopHeaders = LoopHeaders();
            // Safepoints: every loop header, and the entry of a function that can be re-entered without a loop.
            bool entryPoll = EntryPoll;
            for (int b = 0; b < function.Blocks.Length; b++)
            {
                a.Bind(blocks[b]);
                if (Has(GcCapabilities.Tracing) && (loopHeaders.Contains(b) || b == 0 && entryPoll))
                    Poll();
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

            EmitEpilogue();
            EmitPanics();
            EmitLandings();

            int frameSize = (frame + outgoing + 15) / 16 * 16;
            a.PatchInt32(frameSizeAt, frameSize);
            int size = a.Position - start;
            var scopes = EmitFilters();

            var roots = new List<StackRoot>();
            if (cg.Module.Any(GcCapabilities.Tracing, gc))
            {
                for (int l = 0; l < function.Locals.Length; l++)
                {
                    var type = function.Locals[l].Type;
                    if (Larva.IR.Lowering.StackRoots.MayHoldTracked(type, context))
                        roots.Add(new StackRoot(6, slots[l], Math.Max(8, Layouts.Of(type, module)!.Size)));
                }
            }
            var (info, fixups) = X64Codegen.UnwindInfo(frameSize, scopes);
            return new X64Function(cg.Symbol(function), start, size, frameSize, [.. roots], info, fixups);
        }

        // ── Exceptions ─────────────────────────────────────────

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
            if (handles || needsCleanup || function.Blocks.Any(b => b.Terminator is ThrowTerminator or ResumeTerminator))
            {
                incomingPayload = Slot(8, 8);
                incomingType = Slot(8, 8);
                raiseArguments = Slot(16, 16);
            }
        }

        /// <summary>
        /// A call that may raise: when this frame can catch or must clean up, the call (and a <c>nop</c> holding its return
        /// address) becomes a scope-table range.
        /// </summary>
        private void Site(CatchClause[] handlers, Action call)
        {
            if (handlers.Length == 0 && !needsCleanup)
            {
                call();
                return;
            }
            int begin = a.Position;
            call();
            a.Nop();
            sites.Add((begin, a.Position, handlers));
            foreach (var clause in handlers)
                clauseLandings.TryAdd(clause.Block, new Label());
        }

        /// <summary><c>RaiseException(LARVA, NONCONTINUABLE, 2, [payload, type])</c> with payload in rax and type in rdx.</summary>
        /// <remarks><paramref name="handlers"/> <c>null</c>: the raise leaves this frame (no scope entry, not even cleanup).</remarks>
        private void Raise(CatchClause[]? handlers)
        {
            a.Store(Frame(raiseArguments), Reg.Rax, 8);
            a.Store(Frame(raiseArguments + 8), Reg.Rdx, 8);
            a.MovImm(Reg.Rcx, X64Codegen.ExceptionCode);
            a.MovImm(Reg.Rdx, 1);
            a.MovImm(Reg.R8, 2);
            a.Lea(Reg.R9, Frame(raiseArguments));
            if (handlers is null)
                a.Call("RaiseException");
            else
                Site(handlers, () => a.Call("RaiseException"));
            a.Int3();
        }

        /// <summary>
        /// Landings: per handler block, take the matched exception as the one being handled; for cleanup, release what the
        /// frame owns and let the exception continue to the caller.
        /// </summary>
        private void EmitLandings()
        {
            foreach (var (block, landing) in clauseLandings)
            {
                a.Bind(landing);
                a.Load(Reg.Rcx, Frame(caughtPayload), 8, false);
                a.Call("larva_free"); // the exception handled before (if any) is finished
                a.Load(Reg.Rax, Frame(incomingPayload), 8, false);
                a.Store(Frame(caughtPayload), Reg.Rax, 8);
                a.Load(Reg.Rax, Frame(incomingType), 8, false);
                a.Store(Frame(caughtType), Reg.Rax, 8);
                a.Jmp(blocks[block]);
            }
            if (!needsCleanup || sites.Count == 0)
                return;
            a.Bind(cleanupLanding);
            tempCursor = 0;
            for (int l = 0; l < function.Locals.Length; l++)
                ReleaseOwned(new Place(l), []);
            if (handles)
            {
                a.Load(Reg.Rcx, Frame(caughtPayload), 8, false);
                a.Call("larva_free");
            }
            foreach (var pin in pins.Values)
            {
                a.Load(Reg.Rcx, Frame(pin), 8, false);
                a.Call("larva_unpin");
            }
            if (regionDepth != int.MinValue)
            {
                a.Load(Reg.Rcx, Frame(regionDepth), 8, false);
                a.Call("larva_region_unwind");
            }
            a.Load(Reg.Rax, Frame(incomingPayload), 8, false);
            a.Load(Reg.Rdx, Frame(incomingType), 8, false);
            Raise(null); // not a site of this frame: the exception goes on to the caller
        }

        /// <summary>
        /// Filters <c>int filter(EXCEPTION_POINTERS*, establisher frame)</c>, after the function (leaf code, outside its
        /// range): 1 when the exception is Larva's with a matching type id, after storing payload and type into the
        /// handling frame; 0 otherwise. Returns the scope table.
        /// </summary>
        private List<Scope> EmitFilters()
        {
            var scopes = new List<Scope>();
            var filters = new Dictionary<int, int>();
            int Filter(int id)
            {
                if (filters.TryGetValue(id, out int known))
                    return known;
                while (a.Position % 16 != 0)
                    a.Int3();
                int at = filters[id] = a.Position;
                var no = new Label();
                a.Load(Reg.Rax, Mem.At(Reg.Rcx), 8, false);         // EXCEPTION_RECORD*
                a.Load(Reg.R8, Mem.At(Reg.Rax), 4, false);          // ExceptionCode
                a.MovImm(Reg.R9, X64Codegen.ExceptionCode);
                a.Cmp(Reg.R8, Reg.R9);
                a.Jcc(Cond.NE, no);
                a.Load(Reg.R8, Mem.At(Reg.Rax, 40), 8, false);      // ExceptionInformation[1]: type id
                if (id != 0)
                {
                    a.MovImm(Reg.R9, id);
                    a.Cmp(Reg.R8, Reg.R9);
                    a.Jcc(Cond.NE, no);
                }
                a.Load(Reg.R9, Mem.At(Reg.Rax, 32), 8, false);      // ExceptionInformation[0]: payload
                a.Store(Mem.At(Reg.Rdx, incomingPayload), Reg.R9, 8);
                a.Store(Mem.At(Reg.Rdx, incomingType), Reg.R8, 8);
                a.MovImm(Reg.Rax, 1);                              // EXCEPTION_EXECUTE_HANDLER
                a.Ret();
                a.Bind(no);
                a.MovImm(Reg.Rax, 0);                              // EXCEPTION_CONTINUE_SEARCH
                a.Ret();
                return at;
            }
            foreach (var (begin, end, handlers) in sites)
            {
                // Clauses in order, then the frame's cleanup: the dispatcher takes the first entry whose filter accepts.
                foreach (var clause in handlers)
                    scopes.Add(new Scope(begin, end, Filter(clause.Type is null ? 0 : cg.TypeId(clause.Type)), clauseLandings[clause.Block].Position));
                if (needsCleanup)
                    scopes.Add(new Scope(begin, end, Filter(0), cleanupLanding.Position));
            }
            return scopes;
        }

        // ── Parameters and results ─────────────────────────────

        private bool HiddenReturn(IrFunction f)
        {
            var (kind, size, _) = cg.Shape(f.ReturnType);
            return X64Codegen.ByReference(kind, size);
        }

        /// <summary>Copies incoming arguments (registers or the caller's stack) into their local slots.</summary>
        private void StoreParameters()
        {
            int position = 0;
            if (HiddenReturn(function))
            {
                returnPointer = Slot(8, 8);
                a.Store(Frame(returnPointer), Reg.Rcx, 8);
                position++;
            }
            for (int l = 1; l <= function.ArgumentCount; l++, position++)
            {
                var (kind, size, _) = cg.Shape(function.Locals[l].Type);
                if (kind == X64Codegen.Kind.Unit)
                {
                    position--;
                    continue;
                }
                var slot = Frame(slots[l]);
                if (kind == X64Codegen.Kind.Float && position < 4)
                {
                    a.StoreFloat(slot, (Xmm)position, size);
                    continue;
                }
                Reg value = position < 4 ? IntegerArguments[position] : Reg.R10;
                if (position >= 4)
                    a.Load(Reg.R10, Frame(16 + 8 * position), 8, false);
                if (X64Codegen.ByReference(kind, size))
                    Copy(Reg.R11, slots[l], value, 0, size);
                else
                    a.Store(slot, value, size);
            }
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
                    int size = Layouts.Of(module.Globals[store.Global].Type, module)!.Size;
                    int value = Operand(store.Value);
                    if (Has(GcCapabilities.RefCounting) && module.Globals[store.Global].Type.Unoptional() is GcType)
                    {
                        GlobalAddress(store.Global);
                        a.Load(Reg.Rcx, Mem.At(Reg.Rax), 8, false);
                        a.Call("larva_gc_release"); // the old value loses its owner
                    }
                    GlobalAddress(store.Global);
                    a.Mov(Reg.Rcx, Reg.Rax);
                    Copy(Reg.Rdx, 0, Reg.Rbp, value, size, toRegister: Reg.Rcx);
                    break;
                }
                case FreeStatement free:
                {
                    if (Has(GcCapabilities.RefCounting))
                    {
                        // The handles the memory held lose their owner.
                        foreach (var slot in Larva.IR.Analysis.HandleSlots.Of(free.Pointer.Deref(), function.TypeOf(free.Pointer, module).Pointee()!, module))
                        {
                            int handle = Load(slot);
                            a.Load(Reg.Rcx, Frame(handle), 8, false);
                            a.Call("larva_gc_release");
                        }
                    }
                    int pointer = Load(free.Pointer);
                    a.Load(Reg.Rcx, Frame(pointer), 8, false);
                    a.Call("larva_free");
                    break;
                }
                case RegionEnterStatement enter:
                    a.MovImm(Reg.Rcx, enter.Region);
                    a.Call("larva_region_enter");
                    break;
                case RegionExitStatement exit:
                    a.MovImm(Reg.Rcx, exit.Region);
                    a.Call("larva_region_exit");
                    break;
                case NopStatement:
                    break;
                default:
                    throw new X64BackendException($"statement {statement}");
            }
        }

        private void Assign(Place target, Rvalue value)
        {
            var type = function.TypeOf(target, module);
            if (type.IsUnit)
            {
                if (value is IntrinsicRvalue effect)
                    Intrinsic(effect); // runs for its effect
                return;
            }
            int result = Rvalue(value, type);
            var moved = Moved(MoveAnalysis.Operands(value));
            ReleaseOwned(target, moved);
            Store(target, result);
            UpdateFlags(moved, target);
        }

        /// <summary>
        /// An intrinsic: its arguments in rcx, rdx, r8 and r9 (an f64 as its bits, a bool widened), then the runtime's
        /// function; a result (input's i64) comes back in rax.
        /// </summary>
        private void Intrinsic(IntrinsicRvalue intrinsic)
        {
            var info = Intrinsics.Find(intrinsic.Name)!;
            var slots = intrinsic.Arguments.Select(Operand).ToArray();
            Reg[] registers = [Reg.Rcx, Reg.Rdx, Reg.R8, Reg.R9];
            for (int i = 0; i < slots.Length; i++)
                a.Load(registers[i], Frame(slots[i]), info.Parameters[i] == IrType.Bool ? 1 : 8, false);
            a.Call(info.Symbol);
        }

        /// <summary>rax = the address of global N: its <c>.data</c> bytes, or (for handles) its heap cell, made on first use.</summary>
        private void GlobalAddress(int index)
        {
            var global = module.Globals[index];
            if (global.IsPlain(module))
            {
                a.LeaRip(Reg.Rax, cg.GlobalSymbol(index));
                return;
            }
            a.LeaRip(Reg.Rcx, cg.GlobalSymbol(index, slotOnly: true));
            a.MovImm(Reg.Rdx, Layouts.Of(global.Type, module)!.Size);
            a.Call("larva_global");
        }

        /// <summary>Stores the value held in temp <paramref name="value"/> into a place (with a write barrier if needed).</summary>
        private void Store(Place target, int value)
        {
            var type = Address(target);
            var (kind, size, _) = cg.Shape(type);
            if (kind == X64Codegen.Kind.Unit)
                return;
            a.Mov(Reg.Rcx, Reg.Rax);
            bool owned = ownerInR11;
            Copy(Reg.Rdx, 0, Reg.Rbp, value, size, toRegister: Reg.Rcx);
            // Generations: a store of handles into an old object (its age byte, before the payload, is 1) remembers it.
            if (Has(GcCapabilities.Barriers) && Has(GcCapabilities.Tracing) && owned && !Constraints.Satisfies(type, TypeConstraint.Unmanaged, context))
            {
                var stored = new Label();
                a.Load(Reg.Rax, Mem.At(Reg.R11, -1), 1, false);
                a.MovImm(Reg.Rdx, 1);
                a.Cmp(Reg.Rax, Reg.Rdx);
                a.Jcc(Cond.NE, stored);
                a.Mov(Reg.Rdx, Reg.Rcx);  // the slot
                a.Mov(Reg.Rcx, Reg.R11);  // the owner
                a.MovImm(Reg.R8, size);
                a.Call("larva_gc_wb");
                a.Bind(stored);
            }
        }

        /// <summary>
        /// Copies <paramref name="size"/> bytes from <c>[from + fromOffset]</c> to <c>[to + toOffset]</c> through
        /// <paramref name="scratch"/>; <c>to</c> is <c>rbp</c> unless <paramref name="toRegister"/> is given.
        /// </summary>
        private void Copy(Reg scratch, int toOffset, Reg from, int fromOffset, int size, Reg toRegister = Reg.Rbp)
        {
            int done = 0;
            foreach (int chunk in new[] { 8, 4, 2, 1 })
            {
                while (size - done >= chunk)
                {
                    a.Load(scratch, Mem.At(from, fromOffset + done), chunk, false);
                    a.Store(Mem.At(toRegister, toOffset + done), scratch, chunk);
                    done += chunk;
                }
            }
        }

        // ── Terminators ────────────────────────────────────────

        private void Terminator(Terminator terminator)
        {
            switch (terminator)
            {
                case GotoTerminator jump:
                    a.Jmp(blocks[jump.Target]);
                    break;
                case IfTerminator branch:
                {
                    int condition = Operand(branch.Condition);
                    a.Load(Reg.Rax, Frame(condition), 8, false);
                    a.Test(Reg.Rax, Reg.Rax);
                    a.Jcc(Cond.NE, blocks[branch.Then]);
                    a.Jmp(blocks[branch.Else]);
                    break;
                }
                case SwitchTerminator @switch:
                {
                    int value = Operand(@switch.Value);
                    a.Load(Reg.Rax, Frame(value), 8, false);
                    for (int i = 0; i < @switch.Targets.Length; i++)
                    {
                        a.CmpImm(Reg.Rax, i);
                        a.Jcc(Cond.E, blocks[@switch.Targets[i]]);
                    }
                    a.Jmp(blocks[@switch.Otherwise]);
                    break;
                }
                case ReturnTerminator:
                    for (int l = 1; l < function.Locals.Length; l++)
                        ReleaseOwned(new Place(l), []);
                    if (handles)
                    {
                        a.Load(Reg.Rcx, Frame(caughtPayload), 8, false);
                        a.Call("larva_free");
                    }
                    foreach (var pin in pins.Values)
                    {
                        a.Load(Reg.Rcx, Frame(pin), 8, false);
                        a.Call("larva_unpin");
                    }
                    a.Jmp(epilogue);
                    break;
                case CallTerminator call:
                    Call(call);
                    break;
                case ThrowTerminator @throw:
                {
                    var type = function.TypeOf(@throw.Value, module);
                    int value = Operand(@throw.Value);
                    UpdateFlags(Moved([@throw.Value]), null);
                    var layout = Layouts.Of(type, module)!;
                    a.MovImm(Reg.Rcx, layout.Size);
                    a.MovImm(Reg.Rdx, layout.Align);
                    a.Call("larva_exception_cell");
                    Copy(Reg.Rcx, 0, Reg.Rbp, value, cg.Shape(type).Size, toRegister: Reg.Rax);
                    a.MovImm(Reg.Rdx, cg.TypeId(type));
                    Raise(@throw.Handlers);
                    break;
                }
                case ResumeTerminator resume:
                {
                    if (caughtPayload == int.MinValue)
                        throw new X64BackendException($"`{function.Name}`: resume outside an exception handler");
                    a.Load(Reg.Rax, Frame(caughtPayload), 8, false);
                    a.Load(Reg.Rdx, Frame(caughtType), 8, false);
                    a.MovImm(Reg.Rcx, 0);
                    a.Store(Frame(caughtPayload), Reg.Rcx, 8); // ownership goes with the exception
                    Raise(resume.Handlers);
                    break;
                }
                case DropTerminator drop:
                    ReleaseOwned(drop.Place, []);
                    UpdateFlags([drop.Place], null);
                    a.Jmp(blocks[drop.Target]);
                    break;
                case UnreachableTerminator:
                    a.MovImm(Reg.Rcx, 3);
                    a.MovImm(Reg.Rdx, 0);
                    a.MovImm(Reg.R8, 0);
                    a.Call("larva_panic");
                    a.Int3();
                    break;
                default:
                    throw new X64BackendException($"terminator {terminator}");
            }
        }

        private void EmitEpilogue()
        {
            a.Bind(epilogue);
            var (kind, size, signed) = cg.Shape(function.ReturnType);
            var result = Frame(slots[0]);
            switch (kind)
            {
                case X64Codegen.Kind.Scalar:
                    a.Load(Reg.Rax, result, size, signed);
                    break;
                case X64Codegen.Kind.Float:
                    a.LoadFloat(Xmm.X0, result, size);
                    break;
                case X64Codegen.Kind.Aggregate when X64Codegen.InRegister(size):
                    a.Load(Reg.Rax, result, size, false);
                    break;
                case X64Codegen.Kind.Aggregate or X64Codegen.Kind.Wide:
                    a.Load(Reg.Rax, Frame(returnPointer), 8, false);
                    Copy(Reg.Rdx, 0, Reg.Rbp, slots[0], size, toRegister: Reg.Rax);
                    break;
            }
            a.Lea(Reg.Rsp, Mem.At(Reg.Rbp, 0)); // the epilogue form the Win64 unwinder recognizes
            a.PopRbp();
            a.Ret();
        }

        /// <summary>Out-of-line panics shared by the function's checks (arguments already in registers).</summary>
        private void EmitPanics()
        {
            void Panic(Label label, int code, Action setup)
            {
                if (label.Fixups.Count == 0)
                    return;
                a.Bind(label);
                setup();
                a.MovImm(Reg.Rcx, code);
                a.Call("larva_panic");
                a.Int3();
            }
            Panic(panicBounds, 1, () => { a.Mov(Reg.R8, Reg.Rdx); a.Mov(Reg.Rdx, Reg.Rcx); });   // index in rcx, length in rdx
            Panic(panicDivide, 2, () => { a.MovImm(Reg.Rdx, 0); a.MovImm(Reg.R8, 0); });
            Panic(panicLength, 4, () => { a.Mov(Reg.Rdx, Reg.Rax); a.MovImm(Reg.R8, 0); });    // length in rax
            Panic(panicNull, 5, () => { a.MovImm(Reg.Rdx, 0); a.MovImm(Reg.R8, 0); });
        }

        private void Call(CallTerminator call)
        {
            // The signature: the callee's, or for an indirect call the `fn(…) -> …` type of the callee operand.
            IrType[] parameterTypes;
            IrType returnType;
            string? symbol = null;
            if (call.Callee is { } indirect)
            {
                var signature = (FunctionType)function.TypeOf(indirect, module);
                parameterTypes = signature.Parameters;
                returnType = signature.Result;
            }
            else
            {
                var callee = module.Functions[call.Function];
                if (callee.IsGeneric)
                    throw new X64BackendException($"`{callee.Name}` is shared generic code; compile with GenericsMode.Specialize");
                parameterTypes = [.. callee.Locals.Skip(1).Take(callee.ArgumentCount).Select(l => l.Type)];
                returnType = callee.ReturnType;
                symbol = cg.Symbol(callee);
            }
            // Evaluate the arguments into temps (big aggregates: the temp is the copy; its address is passed).
            var (returnKind, returnSize, _) = cg.Shape(returnType);
            bool hidden = X64Codegen.ByReference(returnKind, returnSize);
            var passed = new List<(int Temp, X64Codegen.Kind Kind, int Size, bool ByReference)>();
            int resultArea = 0;
            if (hidden)
            {
                resultArea = Temp(returnSize);
                passed.Add((resultArea, X64Codegen.Kind.Scalar, 8, true));
            }
            int calleeTemp = call.Callee is { } calleeOperand ? Operand(calleeOperand) : -1;
            // Handles go at +0: a local's own value as it is (released right after the call when this was its last use);
            // any other place's value retained for the call and released after it.
            var lent = new List<Place>();
            var lastUse = new List<Place>();
            var held = new List<int>();
            for (int i = 0; i < call.Arguments.Length; i++)
            {
                var (kind, size, _) = cg.Shape(parameterTypes[i]);
                if (kind == X64Codegen.Kind.Unit)
                    continue;
                var operand = call.Arguments[i];
                var place = operand switch { CopyOperand copy => copy.Place, MoveOperand move => move.Place, _ => null };
                if (Has(GcCapabilities.RefCounting) && place is not null && parameterTypes[i].Unoptional() is GcType)
                {
                    int handle = Load(place);
                    if (place.IsLocal && !addressTaken.Contains(place.Local))
                    {
                        lent.Add(place);
                        if (Transfers(operand, current))
                            lastUse.Add(place);
                    }
                    else
                    {
                        a.Load(Reg.Rcx, Frame(handle), 8, false);
                        a.Call("larva_gc_retain");
                        held.Add(handle);
                    }
                    passed.Add((handle, kind, size, X64Codegen.ByReference(kind, size)));
                    continue;
                }
                int value = Operand(operand);
                passed.Add((value, kind, size, X64Codegen.ByReference(kind, size)));
            }
            var moved = Moved(call.Arguments).Where(p => !lent.Contains(p)).ToList();
            UpdateFlags(moved, null);

            outgoing = Math.Max(outgoing, 8 * Math.Max(4, passed.Count));
            for (int p = passed.Count - 1; p >= 0; p--)
            {
                var (temp, kind, size, byReference) = passed[p];
                Reg target = p < 4 ? IntegerArguments[p] : Reg.Rax;
                if (byReference)
                    a.Lea(target, Frame(temp));
                else if (kind == X64Codegen.Kind.Float && p < 4)
                    a.LoadFloat((Xmm)p, Frame(temp), size);
                else
                    a.Load(target, Frame(temp), kind == X64Codegen.Kind.Aggregate ? size : 8, false);
                if (p >= 4)
                    a.Store(Mem.At(Reg.Rsp, 8 * p), Reg.Rax, 8);
            }
            if (symbol is not null)
            {
                Site(call.Handlers, () => a.Call(symbol));
            }
            else
            {
                a.Load(Reg.R11, Frame(calleeTemp), 8, false); // r11: volatile and not an argument register
                Site(call.Handlers, () => a.CallRegister(Reg.R11));
            }

            var (resultKind, resultSize, _) = cg.Shape(returnType);
            int returned = -1;
            if (resultKind != X64Codegen.Kind.Unit)
            {
                returned = hidden ? resultArea : Temp(resultSize);
                if (resultKind == X64Codegen.Kind.Float)
                    a.StoreFloat(Frame(returned), Xmm.X0, resultSize);
                else if (!hidden)
                    a.Store(Frame(returned), Reg.Rax, resultKind == X64Codegen.Kind.Aggregate ? resultSize : 8);
            }
            foreach (var place in lastUse)
            {
                ReleaseOwned(place, []);
                UpdateFlags([place], null);
            }
            foreach (int handle in held)
            {
                a.Load(Reg.Rcx, Frame(handle), 8, false);
                a.Call("larva_gc_release");
            }
            if (resultKind != X64Codegen.Kind.Unit)
            {
                int result = returned;
                ReleaseOwned(call.Destination, moved);
                Store(call.Destination, result);
            }
            UpdateFlags([], resultKind != X64Codegen.Kind.Unit ? call.Destination : null);
            a.Jmp(blocks[call.Target]);
        }

        // ── Values ─────────────────────────────────────────────

        /// <summary>Evaluates an operand into a fresh temp (scalars canonical in 8 bytes, floats and aggregates as bytes).</summary>
        private int Operand(Operand operand)
        {
            switch (operand)
            {
                case CopyOperand or MoveOperand:
                {
                    var place = operand is CopyOperand copy ? copy.Place : ((MoveOperand)operand).Place;
                    int value = Load(place);
                    // Reference counting: a handle that gains an owner takes a reference, unless this read transfers it.
                    if (Has(GcCapabilities.RefCounting) && function.TypeOf(place, module).Unoptional() is GcType && !Transfers(operand, current))
                    {
                        a.Load(Reg.Rcx, Frame(value), 8, false);
                        a.Call("larva_gc_retain");
                    }
                    return value;
                }
                case ConstOperand { Type: var constantType } constant when cg.Shape(constantType) is (X64Codegen.Kind.Wide, var wideSize, _):
                {
                    int temp = Temp(wideSize);
                    var limbs = X64Codegen.Limbs(constant, wideSize / 8);
                    for (int i = 0; i < limbs.Length; i++)
                    {
                        a.MovImm(Reg.Rax, limbs[i]);
                        a.Store(Frame(temp + 8 * i), Reg.Rax, 8);
                    }
                    return temp;
                }
                case ConstOperand constant:
                {
                    int temp = Temp();
                    a.MovImm(Reg.Rax, X64Codegen.Canonical(constant));
                    a.Store(Frame(temp), Reg.Rax, 8);
                    return temp;
                }
                default:
                    throw new X64BackendException($"operand {operand}");
            }
        }

        /// <summary>Reads a place into a temp.</summary>
        private int Load(Place place)
        {
            var type = Address(place);
            if (type is ArrayType)
                throw new X64BackendException($"`{place}` is an unsized array; index it or take Len");
            var (kind, size, signed) = cg.Shape(type);
            int temp = Temp(size);
            if (kind == X64Codegen.Kind.Scalar)
            {
                a.Load(Reg.Rcx, Mem.At(Reg.Rax), size, signed);
                a.Store(Frame(temp), Reg.Rcx, 8);
            }
            else
            {
                Copy(Reg.Rcx, temp, Reg.Rax, 0, size);
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
                {
                    int value = Operand(unary.Operand);
                    var type = function.TypeOf(unary.Operand, module);
                    if (cg.Shape(type) is (X64Codegen.Kind.Wide, var wideSize, _))
                    {
                        int limbs = wideSize / 8;
                        if (type.IsFloat)
                        {
                            // -x flips the sign bit (the top bit of the top limb).
                            a.Load(Reg.Rax, Frame(value + 8 * (limbs - 1)), 8, false);
                            a.MovImm(Reg.Rcx, long.MinValue);
                            a.Xor(Reg.Rax, Reg.Rcx);
                            a.Store(Frame(value + 8 * (limbs - 1)), Reg.Rax, 8);
                            return value;
                        }
                        // ~x limb by limb; -x = ~x + 1, the carry running up the limbs (moves leave the flags alone).
                        a.MovImm(Reg.Rcx, unary.Op == UnaryOp.Neg ? 1 : 0);
                        a.MovImm(Reg.Rdx, 0);
                        for (int i = 0; i < limbs; i++)
                        {
                            a.Load(Reg.Rax, Frame(value + 8 * i), 8, false);
                            a.Not(Reg.Rax);
                            if (i == 0)
                                a.Add(Reg.Rax, Reg.Rcx);
                            else
                                a.Adc(Reg.Rax, Reg.Rdx);
                            a.Store(Frame(value + 8 * i), Reg.Rax, 8);
                        }
                        return value;
                    }
                    a.Load(Reg.Rax, Frame(value), 8, false);
                    if (unary.Op == UnaryOp.Neg && type.IsFloat)
                    {
                        a.MovImm(Reg.Rcx, type == IrType.F32 ? 0x8000_0000L : type == IrType.F16 ? 0x8000L : long.MinValue);
                        a.Xor(Reg.Rax, Reg.Rcx);
                    }
                    else if (unary.Op == UnaryOp.Neg)
                    {
                        a.Neg(Reg.Rax);
                    }
                    else if (type == IrType.Bool)
                    {
                        a.MovImm(Reg.Rcx, 1);
                        a.Xor(Reg.Rax, Reg.Rcx);
                    }
                    else
                    {
                        a.Not(Reg.Rax);
                    }
                    a.Store(Frame(value), Reg.Rax, 8);
                    return value;
                }
                case CastRvalue cast:
                    return Convert(Operand(cast.Operand), function.TypeOf(cast.Operand, module), cast.Type);
                case RefRvalue reference:
                {
                    PinIfInsideGcObject(reference.Place);
                    Address(reference.Place);
                    int temp = Temp();
                    a.Store(Frame(temp), Reg.Rax, 8);
                    return temp;
                }
                case CaughtRvalue:
                {
                    int size = cg.Shape(resultType).Size;
                    int temp = Temp(size);
                    a.Load(Reg.Rax, Frame(caughtPayload), 8, false);
                    Copy(Reg.Rcx, temp, Reg.Rax, 0, size);
                    return temp;
                }
                case SizeOfRvalue size:
                {
                    int temp = Temp();
                    a.MovImm(Reg.Rax, Layouts.Of(size.Type, module)!.Size);
                    a.Store(Frame(temp), Reg.Rax, 8);
                    return temp;
                }
                case AllocRvalue alloc:
                {
                    var valueType = function.TypeOf(alloc.Value, module);
                    int value = Operand(alloc.Value);
                    var layout = Layouts.Of(valueType, module)!;
                    a.MovImm(Reg.Rcx, layout.Size);
                    a.MovImm(Reg.Rdx, layout.Align);
                    if (alloc.Kind == AllocKind.Gc && cg.TypeSymbol(valueType, domainByte) is { } described)
                    {
                        // Counted alone (nothing traces): the type's map lets the runtime release what a dead object holds.
                        a.LeaRip(Reg.Rcx, described);
                        a.MovImm(Reg.Rdx, layout.Size);
                        a.Call("larva_gc_alloc");
                    }
                    else if (alloc.Kind == AllocKind.Gc && domainByte != 0)
                    {
                        // The object's header records the function's GC domain (and whether it is counted).
                        a.MovImm(Reg.R8, domainByte);
                        a.Call("larva_gc_new_in");
                    }
                    else
                        a.Call(alloc.Kind == AllocKind.Gc ? "larva_gc_new" : "larva_alloc");
                    Copy(Reg.Rcx, 0, Reg.Rbp, value, cg.Shape(valueType).Size, toRegister: Reg.Rax);
                    int cell = Temp();
                    a.Store(Frame(cell), Reg.Rax, 8);
                    if (alloc.Finalizer is { } finalizer)
                    {
                        int function = Operand(finalizer);
                        a.Load(Reg.Rcx, Frame(cell), 8, false);
                        a.Load(Reg.Rdx, Frame(function), 8, false);
                        a.Call("larva_gc_finalizer");
                    }
                    return cell;
                }
                case ArrayRvalue array:
                {
                    int length = Operand(array.Length);
                    var lengthType = function.TypeOf(array.Length, module);
                    a.Load(Reg.Rax, Frame(length), 8, false);
                    // Like the VM: a negative length (or an unsigned one past i64) is an error, never a huge array.
                    a.Test(Reg.Rax, Reg.Rax);
                    a.Jcc(Cond.S, panicLength);
                    var (header, stride) = cg.ArrayLayout(array.Element);
                    int arrayFlags = array.ArrayFlags(module);
                    if (array.Owner is { } owner)
                    {
                        // An object with its array in place: the runtime puts the length before the elements (flag 4).
                        var trailing = Layouts.Trailing(owner, module) ?? throw new X64BackendException($"`{owner}` does not end with an array");
                        (header, stride) = (trailing.Header, trailing.Stride);
                        var fields = module.Structs[owner.Id].Fields;
                        arrayFlags = array.Kind == AllocKind.Gc ? 1 | 4 | (fields[..^1].All(f => f.Substitute(owner.Arguments).HoldsNoReferences(module)) ? 2 : 0) : 4;
                    }
                    if (array.Owner is null && array.Kind == AllocKind.Gc && cg.TypeSymbol(array.Element, domainByte, true, header, stride) is { } described)
                    {
                        // Counted alone: the elements' map lets the runtime release them when the array dies.
                        a.Mov(Reg.Rdx, Reg.Rax);
                        a.LeaRip(Reg.Rcx, described);
                        a.Call("larva_gc_array");
                    }
                    else
                    {
                        a.Mov(Reg.Rcx, Reg.Rax);
                        a.MovImm(Reg.Rdx, header);
                        a.MovImm(Reg.R8, stride);
                        a.MovImm(Reg.R9, arrayFlags | (domainByte & 0xFF) << 8);
                        a.Call("larva_array_new");
                    }
                    a.Store(Frame(length), Reg.Rax, 8);
                    _ = lengthType;
                    return length;
                }
                case FunctionRefRvalue reference:
                {
                    int temp = Temp();
                    a.LeaRip(Reg.Rax, cg.Symbol(module.Functions[reference.Function]));
                    a.Store(Frame(temp), Reg.Rax, 8);
                    return temp;
                }
                case HandleCastRvalue cast:
                    return Operand(cast.Handle); // the same address: only the static type changes
                case WrapRvalue wrap:
                    return Operand(wrap.Value);
                case UnwrapRvalue unwrap:
                {
                    int value = Operand(unwrap.Value);
                    a.Load(Reg.Rax, Frame(value), 8, false);
                    a.Test(Reg.Rax, Reg.Rax);
                    a.Jcc(Cond.E, panicNull);
                    return value;
                }
                case IsNullRvalue test:
                {
                    int value = Load(test.Value);
                    int temp = Temp();
                    a.Load(Reg.Rax, Frame(value), 8, false);
                    a.Test(Reg.Rax, Reg.Rax);
                    a.SetCondition(Cond.E, Reg.Rax);
                    a.Store(Frame(temp), Reg.Rax, 8);
                    return temp;
                }
                case GlobalRvalue global:
                {
                    int size = Layouts.Of(module.Globals[global.Global].Type, module)!.Size;
                    int temp = Temp(size);
                    GlobalAddress(global.Global);
                    Copy(Reg.Rdx, temp, Reg.Rax, 0, size);
                    if (Has(GcCapabilities.RefCounting) && module.Globals[global.Global].Type.Unoptional() is GcType)
                    {
                        a.Load(Reg.Rcx, Frame(temp), 8, false);
                        a.Call("larva_gc_retain"); // the copy is a new owner
                    }
                    return temp;
                }
                case ZeroRvalue zero:
                {
                    int size = Layouts.Of(zero.Type, module)!.Size;
                    int temp = Temp(size);
                    a.MovImm(Reg.Rax, 0);
                    for (int offset = 0; offset < size; offset += 8)
                        a.Store(Frame(temp + offset), Reg.Rax, 8);
                    return temp;
                }
                case StaticRvalue data:
                {
                    int temp = Temp();
                    a.LeaRip(Reg.Rax, cg.DataSymbol(data.Data));
                    a.Store(Frame(temp), Reg.Rax, 8);
                    return temp;
                }
                case LenRvalue len:
                {
                    Address(len.Array);
                    int temp = Temp();
                    a.Load(Reg.Rcx, Mem.At(Reg.Rax), 8, false);
                    a.Store(Frame(temp), Reg.Rcx, 8);
                    return temp;
                }
                case IntrinsicRvalue intrinsic:
                {
                    Intrinsic(intrinsic);
                    int temp = Temp();
                    a.Store(Frame(temp), Reg.Rax, 8);
                    return temp;
                }
                default:
                    throw new X64BackendException($"rvalue {rvalue}");
            }
        }

        private int Binary(BinaryRvalue binary)
        {
            var type = function.TypeOf(binary.Left, module);
            int left = Operand(binary.Left);
            int right = Operand(binary.Right);
            var (kind, size, signed) = cg.Shape(type);
            if (kind == X64Codegen.Kind.Wide && type.IsFloat)
                return WideFloatBinary(binary.Op, left, right, size / 8, type);
            if (type.IsFloat)
                return FloatBinary(binary.Op, left, right, size);
            if (kind == X64Codegen.Kind.Wide)
                return WideBinary(binary.Op, left, right, signed, type);

            a.Load(Reg.Rax, Frame(left), 8, false);
            a.Load(Reg.Rcx, Frame(right), 8, false);
            switch (binary.Op)
            {
                case BinaryOp.Add: a.Add(Reg.Rax, Reg.Rcx); break;
                case BinaryOp.Sub: a.Sub(Reg.Rax, Reg.Rcx); break;
                case BinaryOp.Mul: a.Imul(Reg.Rax, Reg.Rcx); break;
                case BinaryOp.And: a.And(Reg.Rax, Reg.Rcx); break;
                case BinaryOp.Or: a.Or(Reg.Rax, Reg.Rcx); break;
                case BinaryOp.Xor: a.Xor(Reg.Rax, Reg.Rcx); break;
                case BinaryOp.Div or BinaryOp.Rem:
                {
                    a.Test(Reg.Rcx, Reg.Rcx);
                    a.Jcc(Cond.E, panicDivide);
                    var done = new Label();
                    if (signed)
                    {
                        // x / -1 = -x and x % -1 = 0, without the #DE that idiv raises for MIN / -1.
                        var regular = new Label();
                        a.CmpImm(Reg.Rcx, -1);
                        a.Jcc(Cond.NE, regular);
                        if (binary.Op == BinaryOp.Div)
                            a.Neg(Reg.Rax);
                        else
                            a.MovImm(Reg.Rax, 0);
                        a.Jmp(done);
                        a.Bind(regular);
                        a.Cqo();
                        a.Idiv(Reg.Rcx);
                    }
                    else
                    {
                        a.MovImm(Reg.Rdx, 0);
                        a.Div(Reg.Rcx);
                    }
                    if (binary.Op == BinaryOp.Rem)
                        a.Mov(Reg.Rax, Reg.Rdx);
                    a.Bind(done);
                    break;
                }
                case BinaryOp.Shl or BinaryOp.Shr:
                    // Shift amounts wrap at the width, as in the VM and the LLVM back end.
                    a.AndImm(Reg.Rcx, type.BitWidth!.Value - 1);
                    if (binary.Op == BinaryOp.Shl)
                        a.ShlCl(Reg.Rax);
                    else if (signed)
                        a.SarCl(Reg.Rax);
                    else
                        a.ShrCl(Reg.Rax);
                    break;
                default:
                    a.Cmp(Reg.Rax, Reg.Rcx);
                    a.SetCondition(binary.Op switch
                    {
                        BinaryOp.Eq => Cond.E,
                        BinaryOp.Ne => Cond.NE,
                        BinaryOp.Lt => signed ? Cond.L : Cond.B,
                        BinaryOp.Le => signed ? Cond.LE : Cond.BE,
                        BinaryOp.Gt => signed ? Cond.G : Cond.A,
                        BinaryOp.Ge => signed ? Cond.GE : Cond.AE,
                        _ => throw new X64BackendException($"`{binary.Op}`"),
                    }, Reg.Rax);
                    break;
            }
            a.Store(Frame(left), Reg.Rax, 8);
            return left;
        }

        /// <summary>128-bit integers: the low half in rax / rcx, the high half in rdx / r8.</summary>
        /// <summary>
        /// Arithmetic on 128- and 256-bit integers held in the frame as 64-bit limbs (lowest first): add, sub, and, or,
        /// xor and the comparisons run limb by limb with the carry chained (moves leave the flags alone); mul is the
        /// schoolbook product kept to the low limbs; div, rem and the shifts call the runtime (<c>larva_wide_*</c>).
        /// </summary>
        private int WideBinary(BinaryOp op, int left, int right, bool signed, IrType type)
        {
            int limbs = cg.Shape(type).Size / 8;
            switch (op)
            {
                case BinaryOp.Add or BinaryOp.Sub or BinaryOp.And or BinaryOp.Or or BinaryOp.Xor:
                    for (int i = 0; i < limbs; i++)
                    {
                        a.Load(Reg.Rax, Frame(left + 8 * i), 8, false);
                        a.Load(Reg.Rcx, Frame(right + 8 * i), 8, false);
                        switch (op)
                        {
                            case BinaryOp.Add: if (i == 0) a.Add(Reg.Rax, Reg.Rcx); else a.Adc(Reg.Rax, Reg.Rcx); break;
                            case BinaryOp.Sub: if (i == 0) a.Sub(Reg.Rax, Reg.Rcx); else a.Sbb(Reg.Rax, Reg.Rcx); break;
                            case BinaryOp.And: a.And(Reg.Rax, Reg.Rcx); break;
                            case BinaryOp.Or: a.Or(Reg.Rax, Reg.Rcx); break;
                            default: a.Xor(Reg.Rax, Reg.Rcx); break;
                        }
                        a.Store(Frame(left + 8 * i), Reg.Rax, 8);
                    }
                    return left;
                case BinaryOp.Mul:
                    return WideMultiply(left, right, limbs);
                case BinaryOp.Eq or BinaryOp.Ne:
                    a.MovImm(Reg.R8, 0);
                    for (int i = 0; i < limbs; i++)
                    {
                        a.Load(Reg.Rax, Frame(left + 8 * i), 8, false);
                        a.Load(Reg.Rcx, Frame(right + 8 * i), 8, false);
                        a.Xor(Reg.Rax, Reg.Rcx);
                        a.Or(Reg.R8, Reg.Rax);
                    }
                    a.Test(Reg.R8, Reg.R8);
                    a.SetCondition(op == BinaryOp.Eq ? Cond.E : Cond.NE, Reg.Rax);
                    a.Store(Frame(left), Reg.Rax, 8);
                    return left;
                case BinaryOp.Lt or BinaryOp.Ge or BinaryOp.Gt or BinaryOp.Le:
                {
                    // The flags of a wide subtraction: left - right for < and >=, right - left for > and <=.
                    var (first, second) = op is BinaryOp.Lt or BinaryOp.Ge ? (left, right) : (right, left);
                    for (int i = 0; i < limbs; i++)
                    {
                        a.Load(Reg.Rax, Frame(first + 8 * i), 8, false);
                        a.Load(Reg.Rcx, Frame(second + 8 * i), 8, false);
                        if (i == 0)
                            a.Cmp(Reg.Rax, Reg.Rcx);
                        else
                            a.Sbb(Reg.Rax, Reg.Rcx);
                    }
                    a.SetCondition(op is BinaryOp.Lt or BinaryOp.Gt ? (signed ? Cond.L : Cond.B) : (signed ? Cond.GE : Cond.AE), Reg.Rax);
                    a.Store(Frame(left), Reg.Rax, 8);
                    return left;
                }
                case BinaryOp.Div:
                    return WideHelper(signed ? "larva_wide_sdiv" : "larva_wide_udiv", left, right, limbs);
                case BinaryOp.Rem:
                    return WideHelper(signed ? "larva_wide_srem" : "larva_wide_urem", left, right, limbs);
                case BinaryOp.Shl:
                    return WideHelper("larva_wide_shl", left, right, limbs);
                case BinaryOp.Shr:
                    return WideHelper(signed ? "larva_wide_ashr" : "larva_wide_lshr", left, right, limbs);
                default:
                    throw new X64BackendException($"`{op}` on `{type}` is not supported by the direct x86-64 back end (use the LLVM back end)");
            }
        }

        /// <summary>The low limbs of the product: every partial product a[i]·b[j] with i + j below the width, carried up.</summary>
        private int WideMultiply(int left, int right, int limbs)
        {
            int product = Temp(limbs * 8);
            a.MovImm(Reg.Rax, 0);
            for (int i = 0; i < limbs; i++)
                a.Store(Frame(product + 8 * i), Reg.Rax, 8);
            for (int i = 0; i < limbs; i++)
            {
                for (int j = 0; i + j < limbs; j++)
                {
                    a.Load(Reg.Rax, Frame(left + 8 * i), 8, false);
                    a.Load(Reg.Rcx, Frame(right + 8 * j), 8, false);
                    a.Mul(Reg.Rcx); // rdx:rax = a[i] * b[j]
                    a.MovImm(Reg.R9, 0);
                    // The low half into limb i + j, the high half into i + j + 1, each carry running to the top.
                    a.Load(Reg.R8, Frame(product + 8 * (i + j)), 8, false);
                    a.Add(Reg.R8, Reg.Rax);
                    a.Store(Frame(product + 8 * (i + j)), Reg.R8, 8);
                    for (int k = i + j + 1; k < limbs; k++)
                    {
                        a.Load(Reg.R8, Frame(product + 8 * k), 8, false);
                        a.Adc(Reg.R8, Reg.R9);
                        a.Store(Frame(product + 8 * k), Reg.R8, 8);
                    }
                    if (i + j + 1 < limbs)
                    {
                        a.Load(Reg.R8, Frame(product + 8 * (i + j + 1)), 8, false);
                        a.Add(Reg.R8, Reg.Rdx);
                        a.Store(Frame(product + 8 * (i + j + 1)), Reg.R8, 8);
                        for (int k = i + j + 2; k < limbs; k++)
                        {
                            a.Load(Reg.R8, Frame(product + 8 * k), 8, false);
                            a.Adc(Reg.R8, Reg.R9);
                            a.Store(Frame(product + 8 * k), Reg.R8, 8);
                        }
                    }
                }
            }
            return product;
        }

        /// <summary>A runtime routine over wide limbs: <c>name(left, right, result, limbs)</c>, the three by address.</summary>
        private int WideHelper(string symbol, int left, int right, int limbs)
        {
            int result = Temp(limbs * 8);
            a.Lea(Reg.Rcx, Frame(left));
            a.Lea(Reg.Rdx, Frame(right));
            a.Lea(Reg.R8, Frame(result));
            a.MovImm(Reg.R9, limbs);
            a.Call(symbol);
            return result;
        }

        /// <summary>An f128/f256 operation: the runtime's soft float (<c>larva_float_add</c>…; comparisons through <c>larva_float_cmp</c>).</summary>
        private int WideFloatBinary(BinaryOp op, int left, int right, int limbs, IrType type)
        {
            switch (op)
            {
                case BinaryOp.Add: return WideHelper("larva_float_add", left, right, limbs);
                case BinaryOp.Sub: return WideHelper("larva_float_sub", left, right, limbs);
                case BinaryOp.Mul: return WideHelper("larva_float_mul", left, right, limbs);
                case BinaryOp.Div: return WideHelper("larva_float_div", left, right, limbs);
                case BinaryOp.Rem: return WideHelper("larva_float_rem", left, right, limbs);
                case BinaryOp.Eq or BinaryOp.Ne or BinaryOp.Lt or BinaryOp.Le or BinaryOp.Gt or BinaryOp.Ge:
                    a.Lea(Reg.Rcx, Frame(left));
                    a.Lea(Reg.Rdx, Frame(right));
                    a.MovImm(Reg.R8, limbs);
                    a.MovImm(Reg.R9, op switch { BinaryOp.Eq => 0, BinaryOp.Ne => 1, BinaryOp.Lt => 2, BinaryOp.Le => 3, BinaryOp.Gt => 4, _ => 5 });
                    a.Call("larva_float_cmp");
                    a.Extend(Reg.Rax, 1, false);
                    a.Store(Frame(left), Reg.Rax, 8);
                    return left;
                default:
                    throw new X64BackendException($"`{op}` on `{type}` is not supported by the direct x86-64 back end");
            }
        }

        /// <summary>The runtime's name for a type in <c>larva_float_conv</c>: its width, 0x100 when signed, 0x200 for a float.</summary>
        private static long TypeCode(IrType type) =>
            (type.BitWidth ?? 64) | (type.IsSigned ? Larva.Native.NativeRuntime.SignedCode : 0) | (type.IsFloat ? Larva.Native.NativeRuntime.FloatCode : 0);

        private int FloatBinary(BinaryOp op, int left, int right, int size)
        {
            a.LoadFloat(Xmm.X0, Frame(left), size);
            a.LoadFloat(Xmm.X1, Frame(right), size);
            bool half = size == 2;
            if (half)
            {
                // A half computes as a single (exactly: a single has more than twice the bits), then rounds back.
                a.HalfToSingle(Xmm.X0, Xmm.X0);
                a.HalfToSingle(Xmm.X1, Xmm.X1);
                size = 4;
            }
            switch (op)
            {
                case BinaryOp.Add: a.FloatAdd(Xmm.X0, Xmm.X1, size); break;
                case BinaryOp.Sub: a.FloatSub(Xmm.X0, Xmm.X1, size); break;
                case BinaryOp.Mul: a.FloatMul(Xmm.X0, Xmm.X1, size); break;
                case BinaryOp.Div: a.FloatDiv(Xmm.X0, Xmm.X1, size); break;
                case BinaryOp.Eq or BinaryOp.Ne:
                    // Unordered (NaN) sets PF: == is false and != is true for NaN.
                    a.FloatCompare(Xmm.X0, Xmm.X1, size);
                    a.SetCondition(op == BinaryOp.Eq ? Cond.E : Cond.NE, Reg.Rax);
                    a.SetCondition(op == BinaryOp.Eq ? Cond.NP : Cond.P, Reg.Rcx);
                    if (op == BinaryOp.Eq)
                        a.And(Reg.Rax, Reg.Rcx);
                    else
                        a.Or(Reg.Rax, Reg.Rcx);
                    a.Store(Frame(left), Reg.Rax, 8);
                    return left;
                case BinaryOp.Lt or BinaryOp.Le or BinaryOp.Gt or BinaryOp.Ge:
                    // Ordered compares via "above": a < b ⇔ b above a, which is false when unordered.
                    if (op is BinaryOp.Lt or BinaryOp.Le)
                        a.FloatCompare(Xmm.X1, Xmm.X0, size);
                    else
                        a.FloatCompare(Xmm.X0, Xmm.X1, size);
                    a.SetCondition(op is BinaryOp.Lt or BinaryOp.Gt ? Cond.A : Cond.AE, Reg.Rax);
                    a.Store(Frame(left), Reg.Rax, 8);
                    return left;
                default:
                    throw new X64BackendException($"`{op}` on floats is not supported by the direct x86-64 back end");
            }
            if (half)
            {
                a.SingleToHalf(Xmm.X0, Xmm.X0);
                size = 2;
            }
            a.StoreFloat(Frame(left), Xmm.X0, size);
            return left;
        }

        /// <summary>Numeric conversion with Larva semantics (sign follows the source type).</summary>
        private int Convert(int value, IrType from, IrType to)
        {
            if (from == to)
                return value;
            var (fromKind, fromSize, fromSigned) = cg.Shape(from);
            var (toKind, toSize, _) = cg.Shape(to);
            if (fromKind == X64Codegen.Kind.Wide || toKind == X64Codegen.Kind.Wide)
                return WideConvert(value, from, to, fromKind, fromSigned, toKind);
            if (from == IrType.F16)
            {
                a.LoadFloat(Xmm.X0, Frame(value), 2);
                a.HalfToSingle(Xmm.X0, Xmm.X0);
                a.StoreFloat(Frame(value), Xmm.X0, 4);
                return Convert(value, IrType.F32, to);
            }
            if (to == IrType.F16)
            {
                int single = Convert(value, from, IrType.F32);
                a.LoadFloat(Xmm.X0, Frame(single), 4);
                a.SingleToHalf(Xmm.X0, Xmm.X0);
                a.StoreFloat(Frame(single), Xmm.X0, 2);
                return single;
            }
            if (to == IrType.Bool)
            {
                if (fromKind == X64Codegen.Kind.Float)
                {
                    a.LoadFloat(Xmm.X0, Frame(value), fromSize);
                    a.MovImm(Reg.Rax, 0);
                    a.IntToFloat(Xmm.X1, Reg.Rax, fromSize);
                    a.FloatCompare(Xmm.X0, Xmm.X1, fromSize);
                    a.SetCondition(Cond.NE, Reg.Rax);
                    a.SetCondition(Cond.P, Reg.Rcx);
                    a.Or(Reg.Rax, Reg.Rcx);
                }
                else
                {
                    a.Load(Reg.Rax, Frame(value), 8, false);
                    a.Test(Reg.Rax, Reg.Rax);
                    a.SetCondition(Cond.NE, Reg.Rax);
                }
                a.Store(Frame(value), Reg.Rax, 8);
                return value;
            }
            if (fromKind == X64Codegen.Kind.Float && toKind == X64Codegen.Kind.Float)
            {
                a.LoadFloat(Xmm.X0, Frame(value), fromSize);
                a.FloatResize(Xmm.X0, Xmm.X0, fromSize);
                a.StoreFloat(Frame(value), Xmm.X0, toSize);
                return value;
            }
            if (fromKind == X64Codegen.Kind.Float)
            {
                a.LoadFloat(Xmm.X0, Frame(value), fromSize);
                if (to == IrType.U64)
                {
                    // ≥ 2^63: convert x - 2^63 and set the top bit back.
                    var small = new Label();
                    var done = new Label();
                    a.MovImm(Reg.Rax, fromSize == 4 ? 0x5F00_0000L : 0x43E0_0000_0000_0000L); // 2^63
                    if (fromSize == 4)
                        a.BitsToFloat(Xmm.X1, Reg.Rax);
                    else
                        a.BitsToFloat(Xmm.X1, Reg.Rax);
                    a.FloatCompare(Xmm.X0, Xmm.X1, fromSize);
                    a.Jcc(Cond.B, small);
                    a.FloatSub(Xmm.X0, Xmm.X1, fromSize);
                    a.FloatToInt(Reg.Rax, Xmm.X0, fromSize);
                    a.MovImm(Reg.Rcx, long.MinValue);
                    a.Xor(Reg.Rax, Reg.Rcx);
                    a.Jmp(done);
                    a.Bind(small);
                    a.FloatToInt(Reg.Rax, Xmm.X0, fromSize);
                    a.Bind(done);
                }
                else
                {
                    a.FloatToInt(Reg.Rax, Xmm.X0, fromSize);
                }
                Canonicalize(Reg.Rax, to);
                a.Store(Frame(value), Reg.Rax, 8);
                return value;
            }
            if (toKind == X64Codegen.Kind.Float)
            {
                a.Load(Reg.Rax, Frame(value), 8, false);
                if (from == IrType.U64)
                {
                    // Values ≥ 2^63 look negative to cvtsi2sd: halve (keeping the low bit for rounding), convert, double.
                    var positive = new Label();
                    var done = new Label();
                    a.Test(Reg.Rax, Reg.Rax);
                    a.Jcc(Cond.NS, positive);
                    a.Mov(Reg.Rcx, Reg.Rax);
                    a.ShiftImm(5, Reg.Rcx, 1);
                    a.AndImm(Reg.Rax, 1);
                    a.Or(Reg.Rcx, Reg.Rax);
                    a.IntToFloat(Xmm.X0, Reg.Rcx, toSize);
                    a.FloatAdd(Xmm.X0, Xmm.X0, toSize);
                    a.Jmp(done);
                    a.Bind(positive);
                    a.IntToFloat(Xmm.X0, Reg.Rax, toSize);
                    a.Bind(done);
                }
                else
                {
                    a.IntToFloat(Xmm.X0, Reg.Rax, toSize);
                }
                a.StoreFloat(Frame(value), Xmm.X0, toSize);
                return value;
            }
            // Integer ↔ integer (and pointers): the canonical value is already extended by the source's signedness.
            _ = fromSigned;
            a.Load(Reg.Rax, Frame(value), 8, false);
            Canonicalize(Reg.Rax, to);
            a.Store(Frame(value), Reg.Rax, 8);
            return value;
        }

        private int WideConvert(int value, IrType from, IrType to, X64Codegen.Kind fromKind, bool fromSigned, X64Codegen.Kind toKind)
        {
            int fromSize = cg.Shape(from).Size, toSize = cg.Shape(to).Size;
            if (from.IsFloat && to == IrType.Bool)
            {
                // A wide float is true unless zero (either sign); NaN counts as nonzero.
                int limbs = fromSize / 8;
                a.Load(Reg.Rax, Frame(value + 8 * (limbs - 1)), 8, false);
                a.MovImm(Reg.Rcx, long.MaxValue);
                a.And(Reg.Rax, Reg.Rcx);
                for (int i = 0; i < limbs - 1; i++)
                {
                    a.Load(Reg.Rcx, Frame(value + 8 * i), 8, false);
                    a.Or(Reg.Rax, Reg.Rcx);
                }
                a.Test(Reg.Rax, Reg.Rax);
                a.SetCondition(Cond.NE, Reg.Rax);
                a.Store(Frame(value), Reg.Rax, 8);
                return value;
            }
            if (from.IsFloat || to.IsFloat)
            {
                // The runtime converts between any float and any integer or float width: larva_float_conv(source, target, from, to).
                int converted = Temp(Math.Max(8, toSize));
                a.Lea(Reg.Rcx, Frame(value));
                a.Lea(Reg.Rdx, Frame(converted));
                a.MovImm(Reg.R8, TypeCode(from));
                a.MovImm(Reg.R9, TypeCode(to));
                a.Call("larva_float_conv");
                return converted;
            }
            _ = (fromKind, toKind);
            if (fromKind == X64Codegen.Kind.Wide && toKind == X64Codegen.Kind.Wide)
            {
                if (fromSize == toSize)
                    return value;
                // 128 ↔ 256: the low limbs stay; a widening fills the rest with the sign (signed sources) or zero.
                int wide = Temp(toSize);
                int kept = Math.Min(fromSize, toSize) / 8;
                for (int i = 0; i < kept; i++)
                {
                    a.Load(Reg.Rax, Frame(value + 8 * i), 8, false);
                    a.Store(Frame(wide + 8 * i), Reg.Rax, 8);
                }
                if (toSize > fromSize)
                {
                    if (fromSigned)
                        a.ShiftImm(7, Reg.Rax, 63);
                    else
                        a.MovImm(Reg.Rax, 0);
                    for (int i = kept; i < toSize / 8; i++)
                        a.Store(Frame(wide + 8 * i), Reg.Rax, 8);
                }
                return wide;
            }
            if (fromKind == X64Codegen.Kind.Wide)
            {
                a.Load(Reg.Rax, Frame(value), 8, false);
                if (to == IrType.Bool)
                {
                    for (int i = 1; i < fromSize / 8; i++)
                    {
                        a.Load(Reg.Rcx, Frame(value + 8 * i), 8, false);
                        a.Or(Reg.Rax, Reg.Rcx);
                    }
                    a.Test(Reg.Rax, Reg.Rax);
                    a.SetCondition(Cond.NE, Reg.Rax);
                }
                else
                {
                    Canonicalize(Reg.Rax, to);
                }
                a.Store(Frame(value), Reg.Rax, 8);
                return value;
            }
            // Widening a scalar: the higher limbs repeat the sign (signed sources) or are zero.
            int widened = Temp(toSize);
            a.Load(Reg.Rax, Frame(value), 8, false);
            a.Store(Frame(widened), Reg.Rax, 8);
            if (fromSigned)
                a.ShiftImm(7, Reg.Rax, 63);
            else
                a.MovImm(Reg.Rax, 0);
            for (int i = 1; i < toSize / 8; i++)
                a.Store(Frame(widened + 8 * i), Reg.Rax, 8);
            return widened;
        }

        /// <summary>Truncates and re-extends a register to the canonical form of <paramref name="type"/>.</summary>
        private void Canonicalize(Reg reg, IrType type)
        {
            var (kind, size, signed) = cg.Shape(type);
            if (kind == X64Codegen.Kind.Scalar && size < 8)
                a.Extend(reg, size, signed);
        }

        // ── Places ─────────────────────────────────────────────

        /// <summary>Computes the address of a place into <c>rax</c> (bounds-checked indices); returns its type.</summary>
        /// <summary>Whether the last <see cref="Address"/> went into a gc object (its last dereference), kept in r11.</summary>
        private bool ownerInR11;

        private IrType Address(Place place)
        {
            a.Lea(Reg.Rax, Frame(slots[place.Local]));
            IrType type = function.Locals[place.Local].Type;
            ownerInR11 = false;
            for (int p = 0; p < place.Projections.Length; p++)
            {
                switch (place.Projections[p])
                {
                    case DerefProjection:
                        a.Load(Reg.Rax, Mem.At(Reg.Rax), 8, false);
                        if (type is OptionalType)
                        {
                            a.Test(Reg.Rax, Reg.Rax);
                            a.Jcc(Cond.E, panicNull); // null reference
                        }
                        ownerInR11 = type.Unoptional() is GcType;
                        if (ownerInR11)
                            a.Mov(Reg.R11, Reg.Rax);
                        type = type.Pointee()!;
                        break;
                    case FieldProjection field:
                        var st = (StructType)type;
                        a.AddImm(Reg.Rax, Layouts.FieldOffsets(st, module)![field.FieldIndex]);
                        type = module.Structs[st.Id].Fields[field.FieldIndex].Substitute(st.Arguments);
                        break;
                    case IndexProjection inlineIndex when type is FixedArrayType inline:
                    {
                        // Inline array: the elements start at the place itself; the length is a constant.
                        var elementLayout = Layouts.Of(inline.Element, module)!;
                        int inlineStride = (elementLayout.Size + elementLayout.Align - 1) / elementLayout.Align * elementLayout.Align;
                        int length = inline.Length(module)!.Value;
                        var (indexKind, indexSize, indexSigned) = cg.Shape(function.Locals[inlineIndex.Local].Type);
                        if (indexKind != X64Codegen.Kind.Scalar)
                            throw new X64BackendException("inline array indices must be integers");
                        a.Load(Reg.Rcx, Frame(slots[inlineIndex.Local]), indexSize, indexSigned);
                        cg.CountBoundsCheck(eliminated: false);
                        a.MovImm(Reg.Rdx, length);
                        a.Cmp(Reg.Rcx, Reg.Rdx);
                        a.Jcc(Cond.AE, panicBounds);
                        if (inlineStride != 1)
                            a.ImulImm(Reg.Rcx, Reg.Rcx, inlineStride);
                        a.Add(Reg.Rax, Reg.Rcx);
                        type = inline.Element;
                        break;
                    }
                    case IndexProjection index:
                    {
                        var element = ((ArrayType)type).Element;
                        var (header, stride) = cg.ArrayLayout(element);
                        var indexType = function.Locals[index.Local].Type;
                        var (indexKind, indexSize, indexSigned) = cg.Shape(indexType);
                        if (indexKind != X64Codegen.Kind.Scalar)
                            throw new X64BackendException($"index of type `{indexType}` is not supported by the direct x86-64 back end");
                        a.Load(Reg.Rcx, Frame(slots[index.Local]), indexSize, indexSigned);
                        if ((p == 1 || Larva.IR.Analysis.BoundsAnalysis.InPlace(function, module, place, p)) && bounds?.IsSafe(current, place.Local, index.Local) == true)
                        {
                            cg.CountBoundsCheck(eliminated: true);
                        }
                        else
                        {
                            cg.CountBoundsCheck(eliminated: false);
                            a.Load(Reg.Rdx, Mem.At(Reg.Rax), 8, false);
                            a.Cmp(Reg.Rcx, Reg.Rdx);
                            a.Jcc(Cond.AE, panicBounds);
                        }
                        if (stride != 1)
                            a.ImulImm(Reg.Rcx, Reg.Rcx, stride);
                        a.Add(Reg.Rax, Reg.Rcx);
                        a.AddImm(Reg.Rax, header);
                        type = element;
                        break;
                    }
                }
            }
            return type;
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

        /// <summary>Releases what the value at <paramref name="place"/> owns (guarded by drop flags for locals).</summary>
        private void ReleaseOwned(Place place, IReadOnlyList<Place> movedNow)
        {
            foreach (var (path, release, _) in Owned(function.TypeOf(place, module), []))
            {
                var resource = new Place(place.Local, [.. place.Projections, .. path]);
                if (movedNow.Any(m => m.IsPrefixOf(resource) || resource.IsPrefixOf(m)))
                    continue;
                var skip = new Label();
                bool guarded = flags.TryGetValue(resource, out int flag);
                if (guarded)
                {
                    a.TestByte(Frame(flag));
                    a.Jcc(Cond.E, skip);
                }
                int saved = tempCursor;
                int handle = Load(resource);
                a.Load(Reg.Rcx, Frame(handle), 8, false);
                a.Call(release == Release.FreeBox ? "larva_free" : "larva_gc_release");
                tempCursor = saved;
                if (guarded)
                    a.StoreByte(Frame(flag), 0);
                a.Bind(skip);
            }
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
                && function.TypeOf(place, module).Unoptional() is GcType
                && !addressTaken.Contains(place.Local)
                && MoveAnalysis.Occurrences(function, place.Local, at) == 1
                && !liveness.LiveAfter(at).Contains(place.Local);
        }

        /// <summary>
        /// Under reference counting gc handles are passed at +0 (as the LLVM back end does): the caller keeps its reference
        /// for the call, the callee borrows it.
        /// </summary>
        private bool Borrowed(int local) =>
            Has(GcCapabilities.RefCounting) && function.IsArgument(local) && function.Locals[local].Type.Unoptional() is GcType;

        /// <summary>The places an operation moves, except borrowed parameters.</summary>
        private List<Place> Moved(IEnumerable<Operand> operands) =>
            [.. moves.MovedBy(operands, current).Where(p => !(p.IsLocal && Borrowed(p.Local)))];

        /// <summary>Drop flags: cleared for places given up here, set for the place just initialized.</summary>
        private void UpdateFlags(IEnumerable<Place> cleared, Place? initialized)
        {
            foreach (var place in cleared)
            {
                foreach (var (resource, flag) in flags)
                {
                    if (place.IsPrefixOf(resource) || resource.IsPrefixOf(place))
                        a.StoreByte(Frame(flag), 0);
                }
            }
            if (initialized is not null)
            {
                foreach (var (resource, flag) in flags)
                {
                    if (initialized.IsPrefixOf(resource))
                        a.StoreByte(Frame(flag), 1);
                }
            }
        }

        /// <summary>Moving GCs: each borrow inside a <c>gc</c> object gets a pin slot (nulled at entry, unpinned at return).</summary>
        private void PlanPins()
        {
            if (!cg.Module.Any(GcCapabilities.Moving, gc))
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
            a.Load(Reg.Rcx, Frame(value), 8, false);
            a.Store(Frame(slot), Reg.Rcx, 8);
            a.Call("larva_pin");
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
    }
}

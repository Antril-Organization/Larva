namespace Larva.IR
{
    /// <summary>A local slot. <c>_0</c> is the return place, <c>_1.._N</c> are the arguments.</summary>
    /// <param name="Region">
    /// Signature annotation (arguments and <c>_0</c> only): index into <see cref="IrFunction.RegionParams"/> of the arena
    /// region the value's handles and borrows may belong to. <c>null</c> means "private to this call": the function may
    /// not let it escape.
    /// </param>
    public sealed record LocalDecl(IrType Type, string? Name = null, bool Mutable = false, int? Region = null);

    public sealed record BasicBlock(Statement[] Statements, Terminator Terminator);

    /// <summary>
    /// A function as a control-flow graph of <see cref="BasicBlock"/>s. Block 0 is the entry.
    /// Local 0 holds the return value and locals <c>1..ArgumentCount</c> are the arguments.
    /// </summary>
    public sealed record IrFunction(string Name, int ArgumentCount, LocalDecl[] Locals, BasicBlock[] Blocks)
    {
        /// <summary>Type parameters; the body refers to them as <see cref="GenericParamType"/>.</summary>
        public GenericParam[] Generics { get; init; } = [];

        /// <summary><c>unsafe fn</c>: may dereference raw pointers and mutate through <c>gc</c> handles.</summary>
        public bool Unsafe { get; init; }

        /// <summary>
        /// The GC domain the function belongs to, an index into <see cref="IrModule.Domains"/> of the linked program: the
        /// GC mode of the module it came from. 0 before linking and in programs with one domain (every module checked for
        /// the program's GC). Objects it allocates belong to that domain's collector.
        /// </summary>
        public int Domain { get; init; }

        /// <summary>
        /// Region parameters (<c>'a</c>, <c>'b</c>…) for arena escape checking, referenced by <see cref="LocalDecl.Region"/>
        /// on arguments and the return place. Arguments sharing a region may be stored into each other; a return in
        /// <c>'a</c> may hand back values from the arguments in <c>'a</c>. Ignored by GC modes without regions.
        /// </summary>
        public string[] RegionParams { get; init; } = [];

        /// <summary>
        /// <c>#[reflect(...)]</c>: <see cref="ReflectionLevel.Full"/> makes the function invocable through reflection (and keeps a
        /// shared generic function compiled even if nothing calls it). Functions are not reflected by default.
        /// </summary>
        public ReflectionLevel? Reflect { get; init; }

        /// <summary>Set by monomorphization: the source function (and type arguments, empty when shared) this one implements.</summary>
        public FunctionInstance? InstanceOf { get; init; }

        public Linkage Linkage { get; init; }

        /// <summary>
        /// A mangled symbol (private, internal or file access): native code names it <c>larva.$</c> plus a hash of
        /// its name instead of the name itself (see <see cref="Linking.NativeSymbols"/>). Metadata keeps the name.
        /// </summary>
        public bool Mangled { get; init; }

        /// <summary>
        /// The region parameter the caller allocates in: objects the function makes at its top level (outside its own
        /// regions) belong to that region, so values from "the caller's region" and from it are the same (the arena
        /// mode, where every function shares one caller region <c>'a</c>). Calls made inside a region block of the caller
        /// account for the block instead. Kept in object files as the attribute <see cref="CallerRegionAttribute"/>.
        /// </summary>
        public int? CallerRegion { get; init; }

        public const string CallerRegionAttribute = "larva.caller_region";

        /// <summary>Native back ends inline the function wherever it is called.</summary>
        public const string InlineAttribute = "larva.inline";

        /// <summary>Native back ends never inline the function (a rarely taken path kept out of its callers).</summary>
        public const string NoInlineAttribute = "larva.noinline";

        /// <summary>Rarely run (once, or on failures): native back ends compile it for size and out of the way.</summary>
        public const string ColdAttribute = "larva.cold";

        /// <summary>Attributes of the function (see <see cref="IrAttribute"/>).</summary>
        public IrAttribute[] Attributes { get; init; } = [];

        /// <summary>For imports: the exported function this declaration stands for, as <c>module::name</c>. Imports have no blocks.</summary>
        public string? Symbol { get; init; }

        /// <summary>
        /// Where in the source each statement came from, by block and index (a block's terminator is at index
        /// <c>Statements.Length</c>), for diagnostics: only the places the frontend knew. Kept in memory, not in object files.
        /// </summary>
        public IReadOnlyDictionary<(int Block, int Statement), SourcePosition>? Positions { get; init; }

        /// <summary>The source of a statement, or of the nearest statement before it in its block that has one.</summary>
        public SourcePosition? PositionOf(int block, int statement)
        {
            if (Positions is null)
                return null;
            for (int s = statement; s >= 0; s--)
            {
                if (Positions.TryGetValue((block, s), out var found))
                    return found;
            }
            return null;
        }

        public bool IsDeclaration => Blocks.Length == 0;

        public bool IsGeneric => Generics.Length > 0;

        public IrType ReturnType => Locals[0].Type;

        public bool IsArgument(int local) => local >= 1 && local <= ArgumentCount;

        /// <summary>Type of <paramref name="place"/> after applying its projections.</summary>
        public IrType TypeOf(Place place, IrModule module)
        {
            IrType type = Locals[place.Local].Type;
            foreach (var projection in place.Projections)
            {
                type = (projection, type) switch
                {
                    (DerefProjection, _) when type.Pointee() is { } pointee => pointee,
                    (FieldProjection field, StructType st) => module.Structs[st.Id].Fields[field.FieldIndex].Substitute(st.Arguments),
                    (IndexProjection, ArrayType array) => array.Element,
                    (IndexProjection, FixedArrayType inline) => inline.Element,
                    _ => throw new InvalidOperationException($"Cannot apply {projection} to {type} in {Name}."),
                };
            }
            return type;
        }

        public IrType TypeOf(Operand operand, IrModule module) => operand switch
        {
            CopyOperand copy => TypeOf(copy.Place, module),
            MoveOperand move => TypeOf(move.Place, module),
            ConstOperand constant => constant.Type,
            _ => throw new ArgumentOutOfRangeException(nameof(operand)),
        };

        public override string ToString() => Text.IrPrinter.Print(this);
    }

    /// <summary>Origin of a function after monomorphization: index in the source module plus its type arguments.</summary>
    public sealed record FunctionInstance(int Function, IrType[] Arguments);

    /// <summary>
    /// Compilation unit. Function tokens used by <see cref="CallTerminator"/> are indices into <see cref="Functions"/>.
    /// </summary>
    /// <summary>
    /// A read-only array of constants with the layout of every Larva array (length, then the elements), so <c>Len</c> and
    /// indexing work through the <c>&amp;[T]</c> that <see cref="StaticRvalue"/> produces. Elements are primitives; float
    /// values are their IEEE bit patterns, as in <see cref="ConstOperand"/>.
    /// </summary>
    public sealed record StaticData(IrType Element, System.Numerics.BigInteger[] Values)
    {
        public IrType ReferenceType => new ReferenceType(new ArrayType(Element), Mutable: false);

        public override string ToString() => $"[{Element}; {Values.Length}] {{ {string.Join(", ", Values.Take(16))}{(Values.Length > 16 ? ", …" : "")} }}";
    }

    /// <summary>
    /// A global variable of plain data (numbers, <c>bool</c>, inline arrays and structs of those): it holds no handles or
    /// references, so it needs no GC roots or ownership, and it is copied in and out whole. Starts as
    /// <see cref="Initial"/> (a primitive constant, float bits as in <see cref="ConstOperand"/>) or zero.
    /// </summary>
    public sealed record GlobalDef(string Name, IrType Type, System.Numerics.BigInteger? Initial = null)
    {
        /// <summary>
        /// Type parameters: a generic global (a static field of a generic type) is one global per instantiation, made by
        /// the monomorphizer; <see cref="Type"/> may use them. Reads and writes name the type arguments.
        /// </summary>
        public int GenericCount { get; init; }

        /// <summary>Internal, exported (<c>module::name</c>) or imported from another module (<see cref="Symbol"/>, no storage here).</summary>
        public Linkage Linkage { get; init; }

        /// <summary>For an import: the exported global it stands for, as <c>module::name</c>.</summary>
        public string? Symbol { get; init; }

        /// <summary>
        /// Whether the global holds plain data. Otherwise it holds a copyable handle or reference (an object, a <c>str</c>,
        /// a <c>?T</c>): it is a GC root, reference counted in RefCount, and native code keeps it in a heap cell the
        /// collector scans.
        /// </summary>
        public bool IsPlain(IrModule module) => Constraints.Satisfies(Type, TypeConstraint.Plain, new TypeContext(module, []));
    }

    public sealed record IrModule(StructDef[] Structs, IrFunction[] Functions)
    {
        /// <summary>Module name; qualifies its symbols (<c>name::fn</c>) when linking. Empty for single-module programs.</summary>
        public string Name { get; init; } = "";

        /// <summary>
        /// The GC domains of a linked program: one per distinct GC mode among its modules, domain 0 being the program's own
        /// (<see cref="CompilationOptions.Gc"/>); <see cref="IrFunction.Domain"/> and <see cref="StructDef.Domain"/> index it.
        /// Empty before linking: everything is in the program's GC.
        /// </summary>
        public GcConfig[] Domains { get; init; } = [];

        /// <summary>The GC of domain <paramref name="domain"/>: <paramref name="program"/>'s when the module has no domains (before linking, or one domain).</summary>
        public GcConfig GcOf(int domain, GcConfig program) => Domains.Length == 0 || domain < 0 || domain >= Domains.Length ? program : Domains[domain];

        /// <summary>Whether the linked program mixes GC domains (more than one GC mode among its modules).</summary>
        public bool MixedDomains => Domains.Length > 1;

        /// <summary>Whether a tracing collector runs in the program: its own GC traces, or any of its domains does.</summary>
        public bool Traces(GcConfig program) => Any(GcCapabilities.Tracing, program);

        /// <summary>
        /// Whether <paramref name="capability"/> is in play anywhere in the program: in <paramref name="own"/> (the code's
        /// GC, or the program's) or in any domain. A function of a counting domain still records its stack roots and pins
        /// when another domain traces or moves: the collector walks every frame.
        /// </summary>
        public bool Any(GcCapabilities capability, GcConfig own) =>
            (own.Capabilities & capability) != 0 || Domains.Any(d => (d.Capabilities & capability) != 0);

        /// <summary>
        /// The GC the code of domain <paramref name="domain"/> is compiled with: its own, plus reference counting when any
        /// domain of the program counts, so a handle of a counting domain stored or dropped anywhere keeps its count
        /// (retain and release do nothing to objects of the other domains: <see cref="UncountedDomain"/>).
        /// </summary>
        public GcConfig CodeGcOf(int domain, GcConfig program)
        {
            var own = GcOf(domain, program);
            if (!MixedDomains || (own.Capabilities & GcCapabilities.RefCounting) != 0 || !Domains.Any(d => (d.Capabilities & GcCapabilities.RefCounting) != 0))
                return own;
            return own with { Capabilities = own.Capabilities | GcCapabilities.RefCounting };
        }

        /// <summary>In an object's header domain byte: the object is not reference counted (retain and release skip it).</summary>
        public const int UncountedDomain = 0x40;

        /// <summary>
        /// The header domain byte for objects made by code of <paramref name="domain"/>: the domain (6 bits) and
        /// <see cref="UncountedDomain"/> unless that domain counts references. 0 in programs with one domain (unchanged paths).
        /// </summary>
        public int DomainByte(int domain, GcConfig program) =>
            !MixedDomains ? 0 : (domain & 0x3F) | ((GcOf(domain, program).Capabilities & GcCapabilities.RefCounting) != 0 ? 0 : UncountedDomain);

        public bool HasImports => Functions.Any(f => f.Linkage == Linkage.Import) || Structs.Any(s => s.Linkage == Linkage.Import)
            || Globals.Any(g => g.Linkage == Linkage.Import);

        /// <summary>Read-only static arrays (string literals, constant tables), referenced by <see cref="StaticRvalue"/>.</summary>
        public StaticData[] Data { get; init; } = [];

        /// <summary>Functions that meet requirements (<see cref="Requirement"/>) for the types of their signature.</summary>
        public Implementation[] Implementations { get; init; } = [];

        /// <summary>Mutable module-level variables (static fields), read with <see cref="GlobalRvalue"/> and written with <see cref="StoreGlobalStatement"/>.</summary>
        public GlobalDef[] Globals { get; init; } = [];

        public override string ToString() => Text.IrPrinter.Print(this);
    }
}

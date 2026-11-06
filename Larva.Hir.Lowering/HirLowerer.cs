
namespace Larva.Hir.Lowering
{
    using Larva.IR;
    using H = Larva.Hir;

    public sealed record LoweringOptions
    {
        public static readonly LoweringOptions Default = new();

        /// <summary>The GC mode objects are managed by: handles are <c>gc T</c>, or <c>*mut T</c> when nothing manages them.</summary>
        public GcConfig Gc { get; init; } = GcConfig.Default;

        /// <summary>
        /// Input assemblies (by name, see <see cref="H.HirAssembly.Origins"/>) built as objects of their own, for their own
        /// GC domain: their types' methods, structs and globals are lowered as declarations the IR linker binds to that
        /// object (<c>assembly::name</c>), not as definitions.
        /// </summary>
        public IReadOnlySet<string> ExternalAssemblies { get; init; } = new HashSet<string>();

        /// <summary>Exports everything internal: a library built as a GC domain of its own, whose users declare its members.</summary>
        public bool ExportAll { get; init; }

        /// <summary>Lower method bodies on several threads (the result is the same).</summary>
        public bool Parallel { get; init; } = true;

        /// <summary>
        /// The assembly is the whole program (every class that will ever exist is in it), so a virtual call whose
        /// method no class below the receiver's type overrides is a direct call.
        /// </summary>
        public bool ClosedWorld { get; init; } = true;

        /// <summary>
        /// The program runs from its entry (and exported functions) only, as an executable or a DLL does: only the static
        /// constructors of the types those can reach run. Off for hosts that call any function of the program.
        /// </summary>
        public bool StaticConstructorsFromEntry { get; init; }
    }

    public sealed record LoweringError(string Where, string Message)
    {
        public override string ToString() => $"{Where}: {Message}";
    }

    public sealed class LoweringResult
    {
        public required IrModule? Module { get; init; }
        public required IReadOnlyList<LoweringError> Errors { get; init; }

        /// <summary>The IR function of each method (by its token), for hosts and tests.</summary>
        public required IReadOnlyDictionary<H.MemberToken, int> Functions { get; init; }

        public bool Success => Module is not null && Errors.Count == 0;

        public string FunctionName(H.MemberToken method) => Module!.Functions[Functions[method]].Name;
    }

    /// <summary>
    /// Lowers a Larva HIR assembly to Larva IR: what makes objects, dispatch, generics and exceptions work, decided here
    /// once for every frontend.
    /// <list type="bullet">
    /// <item>Every object starts with the root header <c>Larva.Object</c>: <c>$class</c> (an id in preorder over the class
    /// tree, so <c>is</c> is a range check) and <c>$vt</c>, its class's vtable. A class's struct is its base's fields, then
    /// its own; a handle to it is a handle to its base (<see cref="StructDef.Base"/>).</item>
    /// <item>A vtable has one function per virtual method (its base's slots first), made the first time an object of the
    /// class (instance of a generic class) is. Every implementation of a virtual method takes <c>this</c> as the class that
    /// introduced the method, so the slot calls it directly.</item>
    /// <item>Generic types and methods stay generic in IR: a function has its class's type parameters, then its own; the
    /// back ends specialize them. A call through an interface constraint (<c>T : IHashable&lt;T&gt;</c>) is a requirement
    /// of the type parameter, met for each type by a wrapper that calls the type's method.</item>
    /// <item>Structs are IR structs; their methods take <c>this</c> as <c>&amp;mut</c>. A primitive's members (<c>Int32</c>
    /// for <c>i32</c>) take <c>this</c> as the value.</item>
    /// <item>Exceptions are objects, thrown as the root handle: a <c>try</c> has one handler that tests its clauses in
    /// order; <c>finally</c> runs on every way out.</item>
    /// </list>
    /// </summary>
    public sealed partial class HirLowerer
    {
        internal readonly H.HirAssembly Assembly;
        internal readonly LoweringOptions Options;
        private readonly List<LoweringError> errors = [];

        internal readonly List<StructDef> Structs = [];
        internal readonly List<IrFunction?> Functions = [];
        internal readonly List<GlobalDef> Globals = [];
        internal readonly List<StaticData> Data = [];
        internal readonly List<Implementation> Implementations = [];

        /// <summary>The root header struct and its vtable struct.</summary>
        internal int ObjectStruct, ObjectVtable;

        internal TypeInfo[] Types;
        internal readonly Dictionary<H.MemberToken, int> MethodFunctions = [];

        private HirLowerer(H.HirAssembly assembly, LoweringOptions options)
        {
            Assembly = assembly;
            Options = options;
            Types = new TypeInfo[assembly.Types.Count];
        }

        public static LoweringResult Lower(H.HirAssembly assembly, LoweringOptions? options = null)
        {
            var lowerer = new HirLowerer(assembly, options ?? LoweringOptions.Default);
            return lowerer.Run();
        }

        internal void Error(string where, string message)
        {
            lock (errors)
                errors.Add(new LoweringError(where, message));
        }

        /// <summary>
        /// Linkage across GC domains. A type from <see cref="LoweringOptions.ExternalAssemblies"/> is defined in an object of
        /// its own domain, with its own System: only its static methods whose signatures carry no handle can be called
        /// there, so those become declarations the IR linker binds to that object (<c>assembly::name</c>); everything
        /// else of the type (instance methods, its struct, vtable and statics, methods over strings or objects) is compiled
        /// here as well, in this domain, since its objects would not be this domain's anyway. With
        /// <see cref="LoweringOptions.ExportAll"/> everything internal is exported, which is what lets another domain declare it.
        /// </summary>
        /// <summary>The input assembly a type comes from when that assembly is a GC domain of its own (<see cref="LoweringOptions.ExternalAssemblies"/>), else null.</summary>
        internal string? ExternalOrigin(TypeInfo info) =>
            Options.ExternalAssemblies.Count > 0 && Assembly.OriginOf(info.Index) is { } origin && Options.ExternalAssemblies.Contains(origin) ? origin : null;

        /// <summary>
        /// Whether a value of this type crosses between GC domains: numbers, bools and pointers to them (packets, see
        /// System.Exchange). Handles, borrows, arrays, structs and functions belong to one domain's objects.
        /// </summary>
        internal static bool Crosses(IrType type) => type.Walk().All(t =>
            t is not (GcType or BoxType or IR.ReferenceType or StructType or IR.ArrayType or FunctionType)
            && (t is not IR.PointerType pointer || pointer.Pointee.IsInteger || pointer.Pointee.IsFloat || pointer.Pointee == IrType.Bool));

        /// <summary>Why method <paramref name="m"/> of an external type cannot be called from this domain, or null when it can.</summary>
        internal string? NotCrossable(TypeInfo info, int m, MethodPlan? plan)
        {
            var method = info.Definition.Methods[m];
            if (!method.IsStatic || plan is null || plan.This is not null)
                return "it is an instance method, and objects of that domain cannot be used from this one";
            if (plan.Generics.Length > 0 || info.GenericCount > 0)
                return "it is generic";
            for (int p = 0; p < plan.Parameters.Length; p++)
            {
                if (!Crosses(plan.Parameters[p]))
                    return $"its parameter `{method.Parameters[p].Name}` is a `{H.Text.HirPrinter.Print(method.Parameters[p].Type, Assembly)}`, an object of that domain that cannot cross";
            }
            if (!Crosses(plan.Result))
                return $"its result is a `{H.Text.HirPrinter.Print(method.Result, Assembly)}`, an object of that domain that cannot cross";
            return null;
        }

        /// <summary>The error for using a member of another GC domain that cannot cross: what it is, why, and what does cross.</summary>
        internal static string CrossingError(string what, string origin, string reason) =>
            $"{what} belongs to the GC domain of `{origin}` (a library built for another gc): {reason}. " +
            "Between domains cross only static functions over numbers, bools and `u8*` packets: pack strings and bytes with System.Exchange";

        private void ApplyLinkage()
        {
            if (Options.ExternalAssemblies.Count > 0)
            {
                foreach (var plan in Plans)
                {
                    var info = Types[plan.Type];
                    if (plan.Synthetic is not null || ExternalOrigin(info) is not { } origin || NotCrossable(info, plan.Method, plan) is not null || Functions[plan.Function] is not { } f)
                        continue;
                    Functions[plan.Function] = f with { Blocks = [], Locals = f.Locals[..(f.ArgumentCount + 1)], Linkage = Linkage.Import, Symbol = $"{origin}::{f.Name}" };
                }
            }
            if (Options.ExportAll)
            {
                for (int s = 0; s < Structs.Count; s++)
                {
                    if (Structs[s].Linkage == Linkage.Internal)
                        Structs[s] = Structs[s] with { Linkage = Linkage.Export };
                }
                for (int f = 0; f < Functions.Count; f++)
                {
                    if (Functions[f] is { Linkage: Linkage.Internal } function)
                        Functions[f] = function with { Linkage = Linkage.Export };
                }
                for (int g = 0; g < Globals.Count; g++)
                {
                    if (Globals[g].Linkage == Linkage.Internal)
                        Globals[g] = Globals[g] with { Linkage = Linkage.Export };
                }
            }
        }

        private LoweringResult Run()
        {
            if (Assembly.TypeRefs.Count > 0 || Assembly.MemberRefs.Count > 0)
                Error(Assembly.Name, "references to other assemblies are linked into one assembly first (HirLinker)");
            PrepareReflection();
            DeclareRoot();
            DeclareTypes();
            if (errors.Count == 0)
            {
                LayOutTypes();
                DeclareMembers();
                OrderStaticConstructors();
                DeclareImplementations();
                RegisterReflection();
                RegisterStructural();
                DeclareItables();
                BuildItables();
            }
            if (errors.Count == 0)
                LowerBodies();
            if (errors.Count == 0)
                ApplyLinkage();
            if (errors.Count > 0)
                return new LoweringResult { Module = null, Errors = [.. errors], Functions = MethodFunctions };
            bool regions = (Options.Gc.Capabilities & GcCapabilities.Regions) != 0;
            var module = new IrModule([.. Structs], [.. Functions.Select(f => regions ? ArenaSignature(f!) : f!)])
            {
                Name = Assembly.Name,
                Globals = [.. Globals],
                Data = [.. Data],
                Implementations = [.. Implementations],
            };
            return new LoweringResult { Module = module, Errors = [], Functions = MethodFunctions };
        }

        // ── Types ──────────────────────────────────────────

        internal sealed class TypeInfo(H.TypeDef definition, int index)
        {
            public readonly H.TypeDef Definition = definition;
            public readonly int Index = index;
            public int Struct = -1;
            public int Vtable = -1;

            /// <summary>Classes: the base class's index (-1: the root), and its type arguments in this class's parameters.</summary>
            public int Base = -1;
            public H.HirType[] BaseArguments = [];

            /// <summary>Instance fields: the IR field index of each HIR field (-1 for static and const fields).</summary>
            public int[] FieldIndex = [];

            /// <summary>The HIR field kept in place at the end of the object (<see cref="H.FieldFlags.InPlace"/>), or -1.</summary>
            public int InPlaceField = -1;

            /// <summary>
            /// A value with `[ValueLayout]`: each instance field's offset in bytes (-1 for the others), its IR struct being
            /// storage words of the value's size and alignment; its fields are read and written through pointers there.
            /// Null for every other type.
            /// </summary>
            public int[]? FieldOffset;

            /// <summary>Static fields: the IR global of each HIR field (-1 otherwise).</summary>
            public int[] FieldGlobal = [];

            /// <summary>Vtable slots: the slot of each virtual method this class introduces or overrides (-1 otherwise).</summary>
            public int[] MethodSlot = [];

            /// <summary>The class whose vtable slot <see cref="MethodSlot"/> is in (the introducer of each virtual method).</summary>
            public int[] MethodIntroducer = [];

            /// <summary>The vtable: for each slot, the class and method whose function fills it.</summary>
            public List<(int Type, int Method)> Slots = [];

            /// <summary>For each slot, the class and method that introduced it (whose signature the slot has).</summary>
            public List<(int Type, int Method)> SlotIntroducers = [];
            /// <summary>Per slot: whether some class below this one runs another implementation in it.</summary>
            public bool[] SlotVaries = [];

            /// <summary>The global holding the class's vtable once made, and the function that makes it.</summary>
            public int VtableGlobal = -1, VtableMaker = -1;

            /// <summary>The <c>$destroy(self)</c> function a `gc` allocation of this class names as its finalizer, or -1 (no destructor in its chain).</summary>
            public int Finalizer = -1;

            public bool IsClass => Definition.Kind == H.TypeKind.Class;
            public bool IsPrimitive => (Definition.Flags & H.TypeFlags.Primitive) != 0;
            public bool IsStruct => Definition.Kind == H.TypeKind.Struct && !IsPrimitive;
            public bool IsEnum => Definition.Kind is H.TypeKind.Enum or H.TypeKind.Bitflags;
            public bool IsInterface => Definition.Kind == H.TypeKind.Interface;
            public int GenericCount => Definition.Generics.Count;
            /// <summary>Its IR name: the full name (with <c>`N</c> for a generic type that shares it with another type).</summary>
            public string Name = definition.FullName;

            /// <summary>The type's own parameters as IR types (<c>T0…Tn</c>): its struct seen from inside it.</summary>
            public IrType[] OwnArguments => [.. Enumerable.Range(0, GenericCount).Select(i => (IrType)new IR.GenericParamType(i))];
        }

        private void DeclareRoot()
        {
            // An interface entry of a vtable (see Interfaces.cs); every vtable starts with the list of them.
            InterfaceEntry = Structs.Count;
            Structs.Add(new StructDef("Larva.Interface", IrType.I32, IrType.U64) { FieldNames = ["id", "table"], Linkage = Linkage.LinkOnce });
            ObjectVtable = Structs.Count;
            Structs.Add(new StructDef("Larva.Object$vt", VtablePrefix) { FieldNames = VtablePrefixNames, Mutable = true, Linkage = Linkage.LinkOnce });
            ObjectStruct = Structs.Count;
            Structs.Add(new StructDef("Larva.Object", IrType.I32, VtablePointer(IrType.Struct(ObjectVtable))) { FieldNames = ["$class", "$vt"], Mutable = true, Linkage = Linkage.LinkOnce });
            NullableStruct = Structs.Count;
            Structs.Add(new StructDef("Larva.Nullable", IrType.Bool, new IR.GenericParamType(0))
            {
                FieldNames = ["$has", "$value"],
                Linkage = Linkage.LinkOnce,
                Generics = [new GenericParam("T", TypeConstraint.Copy)],
            });
            // Function values: Larva.Func{n}<P0…, R>, an object holding the function and its environment (the display
            // object of the lambda's method; null when it captures nothing). The function takes the environment first.
            for (int n = 0; n <= MaxFuncParameters; n++)
            {
                var generics = Enumerable.Range(0, n + 1).Select(i => (IrType)new IR.GenericParamType(i)).ToArray();
                FuncStructs[n] = Structs.Count;
                Structs.Add(new StructDef($"Larva.Func{n}", IrType.I32, VtablePointer(IrType.Struct(ObjectVtable)),
                    new FunctionType([RootHandle, .. generics[..n]], generics[n]), RootHandle)
                {
                    FieldNames = ["$class", "$vt", "$fn", "$env"],
                    Mutable = true,
                    Base = (StructType)IrType.Struct(ObjectStruct),
                    Linkage = Linkage.LinkOnce,
                    Generics = [.. Enumerable.Range(0, n + 1).Select(i => new GenericParam(i < n ? $"P{i}" : "R", TypeConstraint.Copy))],
                });
            }
            DeclareGrids();
        }

        private void DeclareGrids()
        {
            for (int rank = 2; rank <= 8; rank++)
            {
                GridStructs[rank] = Structs.Count;
                Structs.Add(new StructDef($"Larva.Array{rank}",
                    [IrType.I32, VtablePointer(IrType.Struct(ObjectVtable)), ElementsOf(new IR.GenericParamType(0)), .. Enumerable.Repeat(IrType.I32, rank)])
                {
                    FieldNames = ["$class", "$vt", "$items", .. Enumerable.Range(0, rank).Select(d => $"$length{d}")],
                    Mutable = true,
                    Base = (StructType)IrType.Struct(ObjectStruct),
                    Linkage = Linkage.LinkOnce,
                    Generics = [new GenericParam("T", TypeConstraint.Copy)],
                });
            }
        }

        /// <summary>
        /// Nullable values (not handles): Larva.Nullable&lt;T&gt; { $has, $value }, a value type. Its zero value is null.
        /// </summary>
        internal int NullableStruct;

        /// <summary>Whether <paramref name="type"/> is a nullable value (Larva.Nullable&lt;T&gt;).</summary>
        internal bool IsNullableValue(IrType type) => type is StructType s && s.Id == NullableStruct;

        /// <summary>The most parameters a function value takes.</summary>
        internal const int MaxFuncParameters = 8;

        /// <summary>The struct of function values taking n parameters.</summary>
        internal readonly int[] FuncStructs = new int[MaxFuncParameters + 1];

        /// <summary>
        /// N-dimensional arrays (2 to 8): Larva.Array{N}&lt;T&gt;, an object holding the elements in row order (one plain
        /// array) and each dimension's length.
        /// </summary>
        internal readonly int[] GridStructs = new int[9];

        /// <summary>The plain array of T's that holds a grid's elements.</summary>
        internal IrType ElementsOf(IrType element) => Options.Gc.ManagesObjects ? new GcType(new IR.ArrayType(element)) : new IR.PointerType(new IR.ArrayType(element), Mutable: true);

        /// <summary>Larva.Func{n} with its parameters and result (Unit: nothing).</summary>
        internal StructType FuncStruct(IrType[] parameters, IrType result) => (StructType)IrType.Struct(FuncStructs[parameters.Length], [.. parameters, result]);

        /// <summary>
        /// A pointer to a vtable: vtables are made once per class and never freed, so they live outside the collector (raw
        /// memory); the global holding one is plain data, read with a load instead of a call to the runtime.
        /// </summary>
        internal static IrType VtablePointer(IrType vtable) => new IR.PointerType(vtable, Mutable: true);

        /// <summary>A handle to an object of <paramref name="structType"/>: <c>gc T</c>, or <c>*mut T</c> when nothing manages objects.</summary>
        internal IrType Handle(IrType structType) => Options.Gc.ManagesObjects ? new GcType(structType) : new IR.PointerType(structType, Mutable: true);

        internal AllocKind ObjectAlloc => Options.Gc.ManagesObjects ? AllocKind.Gc : AllocKind.Raw;

        /// <summary>
        /// The arena mode: every function shares one region parameter <c>'a</c> (its arguments, its result and what it
        /// allocates at its top level, <see cref="IrFunction.CallerRegion"/>), so objects flow between arguments, fields and
        /// results as in the other modes; only what a <c>region { }</c> block allocates is checked not to leave the block.
        /// </summary>
        private static IrFunction ArenaSignature(IrFunction function)
        {
            if (function.RegionParams.Length > 0)
                return function;
            return function with
            {
                RegionParams = ["a"],
                CallerRegion = 0,
                Locals = [.. function.Locals.Select((local, i) => i <= function.ArgumentCount ? local with { Region = 0 } : local)],
            };
        }

        internal IrType RootHandle => Handle(IrType.Struct(ObjectStruct));

        /// <summary>The IR struct of a type (with its arguments).</summary>
        internal IrType StructOf(TypeInfo info, IrType[] arguments) => IrType.Struct(info.Struct, arguments);

        private void DeclareTypes()
        {
            if (Types.Length != Assembly.Types.Count)
                Types = new TypeInfo[Assembly.Types.Count]; // reflection added describers
            for (int t = 0; t < Assembly.Types.Count; t++)
            {
                var type = Assembly.Types[t];
                Types[t] = new TypeInfo(type, t);
                if (type.Kind == H.TypeKind.Func)
                    Error(type.FullName, "func types are lowered in a later phase");
            }
            // Types told apart by their number of type parameters (Task, Task<T>): the generic ones get it in their name.
            foreach (var group in Types.GroupBy(i => i.Definition.FullName).Where(g => g.Count() > 1))
            {
                foreach (var info in group.Where(i => i.Definition.Generics.Count > 0))
                    info.Name = $"{info.Definition.FullName}`{info.Definition.Generics.Count}";
            }
            foreach (var info in Types)
            {
                if (!info.IsClass)
                    continue;
                if (info.Definition.Base is { } b)
                {
                    if (b is H.NamedType { Type.IsDefinition: true } named && Types[named.Type.Index].IsClass)
                    {
                        info.Base = named.Type.Index;
                        info.BaseArguments = named.Arguments;
                    }
                    else
                    {
                        Error(info.Name, $"the base of a class is a class, not {H.Text.HirPrinter.Print(b, Assembly)}");
                    }
                }
            }
            // Every class and struct has its struct index before any layout: fields may name types laid out later.
            foreach (var info in Types)
            {
                if (info.IsClass || info.IsStruct)
                {
                    info.Struct = Structs.Count;
                    Structs.Add(new StructDef(info.Name));
                }
            }
            foreach (var info in Types.Where(i => i.IsClass))
            {
                var seen = new HashSet<int>();
                for (int c = info.Index; c >= 0; c = Types[c].Base)
                {
                    if (!seen.Add(c))
                    {
                        Error(info.Name, "a class cannot inherit from itself");
                        break;
                    }
                }
            }
        }

        /// <summary>IR type parameters of a type or function: each copyable (every Larva value is copied freely).</summary>
        internal static GenericParam[] IrGenerics(IEnumerable<H.GenericParam> generics) =>
            [.. generics.Select(g => new GenericParam(g.Name, ConstraintOf(g)))];

        /// <summary>
        /// A type parameter's IR constraint: one of a list of primitives (<c>T : [u8, u16, u32]</c>: arithmetic and casts
        /// on T are then allowed), else any copyable type.
        /// </summary>
        internal static TypeConstraint ConstraintOf(H.GenericParam generic) =>
            generic.OneOf is { Length: > 0 } list && list.All(t => t is H.PrimitiveType)
                ? TypeConstraint.OneOf([.. list.Select(t => Primitive(((H.PrimitiveType)t).Kind))])
                : (generic.Kinds & H.ConstraintKinds.Object) != 0
                    ? TypeConstraint.Object & TypeConstraint.Unborrowed // a class's: an object handle (cast to the class, thrown)
                    : TypeConstraint.Copy & TypeConstraint.Unborrowed; // type arguments are never `&T` borrows

        /// <summary>Structs before the types that contain them, bases before derived classes.</summary>
        private void LayOutTypes()
        {
            var state = new byte[Types.Length]; // 0 new, 1 in progress, 2 done
            void Visit(TypeInfo info)
            {
                if (state[info.Index] == 2 || !(info.IsClass || info.IsStruct))
                    return;
                if (state[info.Index] == 1)
                {
                    Error(info.Name, "a struct contains itself (directly or through other structs)");
                    return;
                }
                state[info.Index] = 1;
                if (info.Base >= 0)
                    Visit(Types[info.Base]);
                foreach (var field in info.Definition.Fields)
                {
                    if ((field.Flags & (H.FieldFlags.Static | H.FieldFlags.Const)) == 0 && Inline(field.Type) is { } inner)
                        Visit(inner);
                }
                LayOut(info);
                state[info.Index] = 2;
            }
            foreach (var info in Types)
                Visit(info);
        }

        /// <summary>The struct a field of this type stores inline (null for handles and primitives).</summary>
        private TypeInfo? Inline(H.HirType type) => type switch
        {
            H.NamedType { Type.IsDefinition: true } n when Types[n.Type.Index].IsStruct => Types[n.Type.Index],
            H.InlineArrayType i => Inline(i.Element),
            _ => null,
        };

        private void LayOut(TypeInfo info)
        {
            var definition = info.Definition;
            var context = new GenericContext(info.GenericCount);
            info.FieldIndex = new int[definition.Fields.Count];
            info.FieldGlobal = new int[definition.Fields.Count];
            var fields = new List<IrType>();
            var names = new List<string>();
            StructType? baseStruct = null;
            if (info.IsClass)
            {
                if (info.Base >= 0)
                {
                    var baseInfo = Types[info.Base];
                    var baseArguments = info.BaseArguments.Select(a => Lower(a, info.Name, context)).ToArray();
                    var b = Structs[baseInfo.Struct];
                    fields.AddRange(b.Fields.Select(f => f.Substitute(baseArguments)));
                    names.AddRange(b.FieldNames);
                    baseStruct = (StructType)StructOf(baseInfo, baseArguments);
                }
                else
                {
                    var root = Structs[ObjectStruct];
                    fields.AddRange(root.Fields);
                    names.AddRange(root.FieldNames);
                    baseStruct = (StructType)IrType.Struct(ObjectStruct);
                }
            }
            info.InPlaceField = -1;
            for (int f = 0; f < definition.Fields.Count; f++)
            {
                var field = definition.Fields[f];
                info.FieldIndex[f] = -1;
                info.FieldGlobal[f] = -1;
                if ((field.Flags & H.FieldFlags.Const) != 0)
                    continue;
                if ((field.Flags & H.FieldFlags.InPlace) != 0 && field.Type is H.ArrayType inPlace)
                {
                    // Laid out last (after the loop): its length word, the elements after the object's fields.
                    info.InPlaceField = f;
                    continue;
                }
                var type = Lower(field.Type, $"{info.Name}.{field.Name}", context);
                if ((field.Flags & H.FieldFlags.Static) != 0)
                {
                    // A generic type's static field: one global per instance of the type.
                    info.FieldGlobal[f] = Globals.Count;
                    var initial = field.Value switch
                    {
                        H.IntegerConstant i => i.Value,
                        H.FloatConstant x => x.Bits,
                        H.BoolConstant b => b.Value ? 1 : 0,
                        H.CharConstant c => c.Value,
                        H.EnumConstant e => e.Value,
                        _ => (System.Numerics.BigInteger?)null,
                    };
                    Globals.Add(new GlobalDef($"{info.Name}.{field.Name}", type, initial) { GenericCount = info.GenericCount });
                    continue;
                }
                info.FieldIndex[f] = fields.Count;
                fields.Add(type);
                names.Add(field.Name);
            }
            if (info.InPlaceField >= 0)
            {
                var field = definition.Fields[info.InPlaceField];
                info.FieldIndex[info.InPlaceField] = fields.Count;
                fields.Add(new IR.ArrayType(Lower(((H.ArrayType)field.Type).Element, $"{info.Name}.{field.Name}", context)));
                names.Add(field.Name);
            }
            if (info.IsStruct && Attribute(definition.Attributes, ValueLayoutName) is { } layout)
                (fields, names) = ValueLayout(info, layout, fields);
            Structs[info.Struct] = new StructDef(info.Name, [.. fields])
            {
                FieldNames = [.. names],
                Mutable = info.IsClass,
                Base = baseStruct,
                Linkage = LinkageOf(info),
                Generics = IrGenerics(definition.Generics),
            };
        }

        internal const string ValueLayoutName = "System.Runtime.InteropServices.ValueLayoutAttribute";
        internal const string FieldOffsetName = "System.Runtime.InteropServices.FieldOffsetAttribute";

        /// <summary>The attribute of class <paramref name="fullName"/> among <paramref name="attributes"/>, or null.</summary>
        internal H.HirAttribute? Attribute(IEnumerable<H.HirAttribute> attributes, string fullName)
        {
            foreach (var attribute in attributes)
            {
                var parent = attribute.Constructor.IsDefinition ? H.TypeToken.Def(attribute.Constructor.Type) : Assembly.MemberRefs[attribute.Constructor.Member].Parent;
                string name;
                if (parent.IsDefinition)
                {
                    var type = Assembly.Types[parent.Index];
                    name = type.Namespace.Length == 0 ? type.Name : $"{type.Namespace}.{type.Name}";
                }
                else
                {
                    var other = Assembly.TypeRefs[parent.Index];
                    name = other.Namespace.Length == 0 ? other.Name : $"{other.Namespace}.{other.Name}";
                }
                if (name == fullName)
                    return attribute;
            }
            return null;
        }

        /// <summary>
        /// `[ValueLayout(kind, Pack = p, Size = s)]`: each field's offset (sequential: in order, aligned to min(its
        /// alignment, Pack); explicit: its `[FieldOffset]`), the value's alignment (the largest of its fields', down to
        /// Pack) and size (the end of the last byte used, at least Size, rounded to the alignment). The IR struct becomes
        /// that many storage words of that alignment; <see cref="TypeInfo.FieldOffset"/> says where each field is.
        /// </summary>
        private (List<IrType> Fields, List<string> Names) ValueLayout(TypeInfo info, H.HirAttribute layout, List<IrType> fields)
        {
            var definition = info.Definition;
            bool isExplicit = layout.Arguments is [H.EnumConstant kind] && kind.Value == 1;
            int pack = 0, minimum = 0;
            foreach (var (name, value) in layout.Named)
            {
                if (name == "Pack" && value is H.IntegerConstant p)
                    pack = (int)p.Value;
                if (name == "Size" && value is H.IntegerConstant s)
                    minimum = (int)s.Value;
            }
            var module = new IrModule([.. Structs], []);
            info.FieldOffset = new int[definition.Fields.Count];
            int cursor = 0, end = 0, align = 1;
            for (int f = 0; f < definition.Fields.Count; f++)
            {
                info.FieldOffset[f] = -1;
                if (info.FieldIndex[f] < 0)
                    continue;
                var type = fields[info.FieldIndex[f]];
                if (Larva.IR.Lowering.Layouts.Of(type, module) is not { } measured)
                {
                    Error(info.Name, $"`{definition.Fields[f].Name}` has no fixed size, so `[ValueLayout]` cannot place it");
                    continue;
                }
                if (measured.GcOffsets.Length > 0)
                    Error(info.Name, $"`{definition.Fields[f].Name}` holds objects: a value with `[ValueLayout]` holds no objects");
                int fieldAlign = pack > 0 ? Math.Min(measured.Align, pack) : measured.Align;
                int offset;
                if (isExplicit)
                    offset = Attribute(definition.Fields[f].Attributes, FieldOffsetName) is { Arguments: [H.IntegerConstant at] } ? (int)at.Value : 0;
                else
                    offset = (cursor + fieldAlign - 1) / fieldAlign * fieldAlign;
                info.FieldOffset[f] = offset;
                info.FieldIndex[f] = -1;
                cursor = offset + measured.Size;
                end = Math.Max(end, cursor);
                align = Math.Max(align, fieldAlign);
            }
            align = Math.Min(align, 8); // storage words are at most 8 bytes
            int size = Math.Max(end, minimum);
            size = Math.Max(align, (size + align - 1) / align * align);
            var word = align switch { 1 => IrType.U8, 2 => IrType.U16, 4 => IrType.U32, _ => IrType.U64 };
            var storage = new List<IrType>();
            var names = new List<string>();
            for (int w = 0; w < size / align; w++)
            {
                storage.Add(word);
                names.Add($"$bytes{w}");
            }
            return (storage, names);
        }

        /// <summary>Public types are exported (their structs appear in exported signatures); the others stay in the module.</summary>
        internal static Linkage LinkageOf(TypeInfo info) => info.Definition.Access == H.Access.Public ? IR.Linkage.Export : IR.Linkage.Internal;

        /// <summary>
        /// Where type parameters land in IR: those of the type first (<c>T0…</c>), then the method's. A function has both, a
        /// type's struct only its own.
        /// </summary>
        internal readonly record struct GenericContext(int TypeCount);

        /// <summary>The IR type of a HIR type, in a generic context (errors for what is not lowered yet).</summary>
        internal IrType Lower(H.HirType type, string where, GenericContext context)
        {
            switch (type)
            {
                case H.PrimitiveType p:
                    return Primitive(p.Kind);
                case H.GenericParamType g:
                    return new IR.GenericParamType(g.Owner == H.GenericOwner.Type ? g.Index : context.TypeCount + g.Index);
                case H.NamedType { Type.IsDefinition: true } n:
                {
                    var info = Types[n.Type.Index];
                    if (info.IsEnum)
                        return info.Definition.Underlying is H.PrimitiveType u ? Lower(u, where, context) : IrType.I32;
                    if (info.IsPrimitive)
                        return Lower(info.Definition.Underlying!, where, context);
                    var arguments = n.Arguments.Select(a => Lower(a, where, context)).ToArray();
                    if (info.IsClass)
                        return Handle(StructOf(info, arguments));
                    if (info.IsStruct)
                        return StructOf(info, arguments);
                    if (info.IsInterface)
                        return RootHandle;
                    break;
                }
                case H.NullableType { Inner: var inner } when Lower(inner, where, context) is var lowered && lowered is GcType or IR.PointerType:
                    return new OptionalType(lowered);
                case H.NullableType { Inner: var inner }:
                    return IrType.Struct(NullableStruct, Lower(inner, where, context));
                case H.FuncType f:
                    if (f.Parameters.Length > MaxFuncParameters)
                    {
                        Error(where, $"a function value takes at most {MaxFuncParameters} parameters");
                        return IrType.Unit;
                    }
                    return Handle(FuncStruct([.. f.Parameters.Select(p => Lower(p, where, context))], Lower(f.Result, where, context)));
                case H.ArrayType { Rank: 1 } a:
                    return ElementsOf(Lower(a.Element, where, context));
                case H.ArrayType { Rank: >= 2 and <= 8 } grid:
                    return Handle(IrType.Struct(GridStructs[grid.Rank], Lower(grid.Element, where, context)));
                case H.InlineArrayType i:
                    return new FixedArrayType(Lower(i.Element, where, context), i.Count, i.SizeOf is null ? null : Lower(i.SizeOf, where, context));
                case H.ReferenceType { Target: H.ArrayType { Rank: 1 } array } r:
                    // `&T[]`: a borrow of the elements themselves (what runtime services read and write).
                    return new IR.ReferenceType(new IR.ArrayType(Lower(array.Element, where, context)), r.Mutable);
                case H.ReferenceType r:
                    return new IR.ReferenceType(Lower(r.Target, where, context), r.Mutable);
                case H.PointerType p:
                    return new IR.PointerType(Lower(p.Target, where, context), p.Mutable);
                case H.NativeFuncType f:
                    return new FunctionType([.. f.Parameters.Select(t => Lower(t, where, context))], Lower(f.Result, where, context));
            }
            Error(where, $"the type {H.Text.HirPrinter.Print(type, Assembly)} is lowered in a later phase");
            return IrType.Unit;
        }

        internal static IrType Primitive(H.Primitive kind) => kind switch
        {
            H.Primitive.Void => IrType.Unit,
            H.Primitive.Bool => IrType.Bool,
            H.Primitive.Char => IrType.U32,
            H.Primitive.I8 => IrType.I8, H.Primitive.I16 => IrType.I16, H.Primitive.I32 => IrType.I32,
            H.Primitive.I64 => IrType.I64, H.Primitive.I128 => IrType.I128, H.Primitive.I256 => IrType.I256,
            H.Primitive.U8 => IrType.U8, H.Primitive.U16 => IrType.U16, H.Primitive.U32 => IrType.U32,
            H.Primitive.U64 => IrType.U64, H.Primitive.U128 => IrType.U128, H.Primitive.U256 => IrType.U256,
            H.Primitive.F16 => IrType.F16, H.Primitive.F32 => IrType.F32, H.Primitive.F64 => IrType.F64,
            H.Primitive.F128 => IrType.F128, _ => IrType.F256,
        };

        /// <summary>A HIR type with the type's parameters replaced by <paramref name="arguments"/> (the method's stay).</summary>
        internal static H.HirType Substitute(H.HirType type, H.HirType[] arguments) => arguments.Length == 0 ? type : type switch
        {
            H.GenericParamType { Owner: H.GenericOwner.Type } g when g.Index < arguments.Length => arguments[g.Index],
            H.NamedType n when n.Arguments.Length > 0 => new H.NamedType(n.Type, [.. n.Arguments.Select(a => Substitute(a, arguments))]),
            H.ArrayType a => a with { Element = Substitute(a.Element, arguments) },
            H.NullableType n => new H.NullableType(Substitute(n.Inner, arguments)),
            H.ReferenceType r => r with { Target = Substitute(r.Target, arguments) },
            H.PointerType p => p with { Target = Substitute(p.Target, arguments) },
            H.InlineArrayType i => i with { Element = Substitute(i.Element, arguments), SizeOf = i.SizeOf is null ? null : Substitute(i.SizeOf, arguments) },
            H.FuncType f => new H.FuncType([.. f.Parameters.Select(p => Substitute(p, arguments))], Substitute(f.Result, arguments)),
            _ => type,
        };

        /// <summary>
        /// The type arguments of <paramref name="ancestor"/> (a base class of <paramref name="type"/>, or the type itself) as
        /// seen from <paramref name="type"/> instantiated with <paramref name="arguments"/>.
        /// </summary>
        internal H.HirType[] AncestorArguments(int type, H.HirType[] arguments, int ancestor)
        {
            var current = arguments;
            for (int t = type; t >= 0; t = Types[t].Base)
            {
                if (t == ancestor)
                    return current;
                current = [.. Types[t].BaseArguments.Select(a => Substitute(a, current))];
            }
            throw new InvalidOperationException($"{Types[ancestor].Name} is not a base of {Types[type].Name}");
        }

        /// <summary>A type's own parameters as HIR types (<c>!0…!n</c>).</summary>
        internal static H.HirType[] OwnParameters(TypeInfo info) =>
            [.. Enumerable.Range(0, info.GenericCount).Select(i => (H.HirType)new H.GenericParamType(H.GenericOwner.Type, i))];

        // ── Members ────────────────────────────────────────

        internal sealed record MethodPlan(int Type, int Method, int Function, IrType? This, IrType[] Parameters, IrType Result)
        {
            public string Name { get; init; } = "";

            /// <summary>The function's type parameters: the type's, then the method's.</summary>
            public GenericParam[] Generics { get; init; } = [];

            /// <summary>A function the lowering writes itself (an interface implementation's wrapper): its HIR method.</summary>
            public H.MethodDef? Synthetic { get; init; }
        }

        internal readonly List<MethodPlan> Plans = [];

        private void DeclareMembers()
        {
            AssignSlots();
            foreach (var info in Types)
            {
                var definition = info.Definition;
                var names = definition.Methods.GroupBy(m => m.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
                for (int m = 0; m < definition.Methods.Count; m++)
                {
                    var method = definition.Methods[m];
                    // An interface's methods are what implementers provide; only its defaults (methods with a body) are functions.
                    if (info.IsInterface && method.Body is null)
                        continue;
                    string where = $"{info.Name}.{method.Name}";
                    if (method.Generics.Count > 0 && info.MethodSlot is { } slots && slots[m] >= 0)
                    {
                        Error(where, "a virtual method cannot be generic");
                        continue;
                    }
                    var context = new GenericContext(info.GenericCount);
                    IrType? self = null;
                    if (!method.IsStatic)
                    {
                        if (info.IsClass)
                        {
                            int introducer = info.MethodIntroducer[m] >= 0 ? info.MethodIntroducer[m] : info.Index;
                            var arguments = AncestorArguments(info.Index, OwnParameters(info), introducer);
                            self = Handle(StructOf(Types[introducer], [.. arguments.Select(a => Lower(a, where, context))]));
                        }
                        else if (info.IsPrimitive)
                        {
                            self = Lower(info.Definition.Underlying!, where, context);
                        }
                        else if (info.IsStruct)
                        {
                            self = IrType.RefMut(StructOf(info, info.OwnArguments));
                        }
                        else if (info.IsInterface)
                        {
                            self = RootHandle; // a default method: `this` is the interface value (the object, or a box)
                        }
                        else if (info.IsEnum)
                        {
                            self = Lower(new H.NamedType(H.TypeToken.Def(info.Index), []), where, context); // its integer, by value
                        }
                        else
                        {
                            Error(where, "instance methods belong to classes and structs");
                        }
                    }
                    var parameters = method.Parameters.Select(p => Lower(p.Type, where, context)).ToArray();
                    var result = Lower(method.Result, where, context);
                    string name = names.Contains(method.Name) ? $"{info.Name}.{method.Name}${m}" : $"{info.Name}.{method.Name}";
                    int function = Functions.Count;
                    Functions.Add(null);
                    MethodFunctions[H.MemberToken.Def(info.Index, m)] = function;
                    var plan = new MethodPlan(info.Index, m, function, self, parameters, result)
                    {
                        Name = name,
                        Generics = FunctionGenerics(info, method, where),
                    };
                    Plans.Add(plan);
                    PlanOf[(info.Index, m)] = plan;
                }
            }
            // Bases first: a vtable struct extends its base's.
            void Declare(TypeInfo info)
            {
                if (info.Vtable >= 0)
                    return;
                if (info.Base >= 0)
                    Declare(Types[info.Base]);
                DeclareVtable(info);
            }
            foreach (var info in Types.Where(i => i.IsClass))
                Declare(info);
        }

        /// <summary>
        /// Virtual methods: a class's slots are its base's, then one per virtual method it introduces; an override (same
        /// name and parameters as a virtual method of a base) fills its base's slot.
        /// </summary>
        private void AssignSlots()
        {
            var done = new bool[Types.Length];
            void Assign(TypeInfo info)
            {
                if (done[info.Index])
                    return;
                done[info.Index] = true;
                var definition = info.Definition;
                info.MethodSlot = Enumerable.Repeat(-1, definition.Methods.Count).ToArray();
                info.MethodIntroducer = Enumerable.Repeat(-1, definition.Methods.Count).ToArray();
                if (!info.IsClass)
                    return;
                if (info.Base >= 0)
                {
                    Assign(Types[info.Base]);
                    info.Slots = [.. Types[info.Base].Slots];
                    info.SlotIntroducers = [.. Types[info.Base].SlotIntroducers];
                }
                for (int m = 0; m < definition.Methods.Count; m++)
                {
                    var method = definition.Methods[m];
                    string where = $"{info.Name}.{method.Name}";
                    if ((method.Flags & H.MethodFlags.Override) != 0)
                    {
                        var overridden = FindVirtual(info, method);
                        if (overridden is not { } found)
                        {
                            Error(where, "overrides no virtual method of a base class");
                            continue;
                        }
                        if (Types[found.Type].Definition.Methods[found.Method].Flags.HasFlag(H.MethodFlags.Final))
                            Error(where, "overrides a final method");
                        info.MethodSlot[m] = Types[found.Type].MethodSlot[found.Method];
                        info.MethodIntroducer[m] = Types[found.Type].MethodIntroducer[found.Method];
                        info.Slots[info.MethodSlot[m]] = (info.Index, m);
                    }
                    else if ((method.Flags & (H.MethodFlags.Virtual | H.MethodFlags.Abstract)) != 0)
                    {
                        if (method.IsStatic)
                        {
                            Error(where, "a static method cannot be virtual");
                            continue;
                        }
                        info.MethodSlot[m] = info.Slots.Count;
                        info.MethodIntroducer[m] = info.Index;
                        info.Slots.Add((info.Index, m));
                        info.SlotIntroducers.Add((info.Index, m));
                    }
                }
            }
            foreach (var info in Types)
                Assign(info);
            foreach (var info in Types)
                info.SlotVaries = new bool[info.Slots.Count];
            foreach (var info in Types)
            {
                for (int a = info.Base; a >= 0; a = Types[a].Base)
                {
                    var ancestor = Types[a];
                    for (int s = 0; s < ancestor.Slots.Count; s++)
                    {
                        if (info.Slots[s] != ancestor.Slots[s])
                            ancestor.SlotVaries[s] = true;
                    }
                }
            }
        }

        /// <summary>The virtual method of a base of <paramref name="info"/> that <paramref name="method"/> overrides (signatures compared in this class's terms).</summary>
        private (int Type, int Method)? FindVirtual(TypeInfo info, H.MethodDef method)
        {
            var own = OwnParameters(info);
            for (int t = info.Base; t >= 0; t = Types[t].Base)
            {
                var arguments = AncestorArguments(info.Index, own, t);
                var methods = Types[t].Definition.Methods;
                for (int m = 0; m < methods.Count; m++)
                {
                    var candidate = methods[m];
                    if (Types[t].MethodSlot.Length > m && Types[t].MethodSlot[m] >= 0 && candidate.Name == method.Name
                        && candidate.Parameters.Select(p => Substitute(p.Type, arguments)).SequenceEqual(method.Parameters.Select(p => p.Type))
                        && Substitute(candidate.Result, arguments) == method.Result)
                        return (t, m);
                }
            }
            return null;
        }

        internal readonly Dictionary<(int Type, int Method), MethodPlan> PlanOf = [];

        /// <summary>The function type of a vtable slot: the signature of the method that introduced it (in its terms).</summary>
        internal IrType SlotType((int Type, int Method) introducer)
        {
            var plan = PlanOf[introducer];
            return new FunctionType([plan.This!, .. plan.Parameters], plan.Result);
        }

        /// <summary>The IR arguments of class <paramref name="ancestor"/> seen from <paramref name="info"/> inside it.</summary>
        internal IrType[] AncestorIrArguments(TypeInfo info, int ancestor) =>
            [.. AncestorArguments(info.Index, OwnParameters(info), ancestor).Select(a => Lower(a, info.Name, new GenericContext(info.GenericCount)))];

        /// <summary>
        /// The vtable struct of a class: <c>$unused</c> (as in the root's, so every vtable extends it), its base's slots,
        /// then its own; the global that holds it once made (one per instance of a generic class), and the function that
        /// makes it.
        /// </summary>
        private void DeclareVtable(TypeInfo info)
        {
            var slotTypes = info.SlotIntroducers.Select(s => SlotType(s).Substitute(AncestorIrArguments(info, s.Type))).ToList();
            var baseVtable = info.Base >= 0
                ? (StructType)IrType.Struct(Types[info.Base].Vtable, AncestorIrArguments(info, info.Base))
                : (StructType)IrType.Struct(ObjectVtable);
            info.Vtable = Structs.Count;
            Structs.Add(new StructDef(info.Name + "$vt", [.. VtablePrefix, .. slotTypes])
            {
                FieldNames = [.. VtablePrefixNames, .. info.Slots.Select(s => Types[s.Type].Definition.Methods[s.Method].Name)],
                Mutable = true,
                Base = baseVtable,
                Linkage = LinkageOf(info),
                Generics = IrGenerics(info.Definition.Generics),
            });
            info.VtableGlobal = Globals.Count;
            Globals.Add(new GlobalDef(info.Name + "$vtable", IrType.U64) { GenericCount = info.GenericCount });
            info.VtableMaker = Functions.Count;
            Functions.Add(null);
        }

        // ── Interface implementations ──────────────────────

        /// <summary>
        /// For each type that implements an interface, and each method of the interface: a wrapper <c>C.$I.M(self, …)</c>
        /// that calls the type's method (virtually when it is virtual), registered as the implementation of the requirement
        /// <c>I.M</c> that calls through a constrained type parameter make.
        /// </summary>
        /// <summary>The nearest `destroy()` in a class's chain (the declaring type's index and the method's), or (-1, -1).</summary>
        internal (int Type, int Method) DestructorOf(TypeInfo info)
        {
            for (var type = info; type is not null; type = type.Base >= 0 ? Types[type.Base] : null)
            {
                var methods = type.Definition.Methods;
                for (int m = 0; m < methods.Count; m++)
                {
                    if (methods[m].Name == "destroy" && methods[m].Parameters.Count == 0 && (methods[m].Flags & H.MethodFlags.Static) == 0)
                        return (type.Index, m);
                }
            }
            return (-1, -1);
        }

        /// <summary>Whether any type parameter in the program asks for constructors (<c>T : new(…)</c>): then every constructor gets a <c>$new</c> wrapper.</summary>
        private bool ConstructorsRequired() =>
            Types.Any(t => t.Definition.Generics.Concat(t.Definition.Methods.SelectMany(m => m.Generics)).Any(g => g.Constructors.Length > 0 || (g.Kinds & H.ConstraintKinds.New) != 0));

        private void DeclareImplementations()
        {
            bool constructors = ConstructorsRequired();
            foreach (var info in Types)
            {
                if (info.IsInterface || info.IsEnum)
                    continue;
                if (constructors && !info.IsPrimitive && (info.Definition.Flags & (H.TypeFlags.Abstract | H.TypeFlags.Static)) == 0)
                {
                    // `$new.N(params) -> Self`: each constructor as a function meeting `new(params) -> T` (a type without
                    // constructors: one making the default object or value).
                    var selfType = new H.NamedType(H.TypeToken.Def(info.Index), OwnParameters(info));
                    var ctors = info.Definition.Methods.Select((m, i) => (Method: m, Index: i)).Where(x => x.Method.Kind == H.MethodKind.Constructor).ToList();
                    var signatures = ctors.Count > 0
                        ? ctors.Select(x => (Token: (H.MemberToken?)H.MemberToken.Def(info.Index, x.Index), Parameters: x.Method.Parameters.Select(p => p.Type).ToArray())).ToList()
                        : [(Token: null, Parameters: Array.Empty<H.HirType>())];
                    int n = 0;
                    foreach (var (token, parameters) in signatures)
                    {
                        var arguments = parameters.Select((t, p) => (H.Expr)new H.ParamExpr(p, t)).ToArray();
                        var wrapper = new H.MethodDef($"$new.{n++}", selfType, [.. parameters.Select((t, p) => new H.Parameter($"p{p}", t))])
                        {
                            Flags = H.MethodFlags.Static | H.MethodFlags.Generated,
                            Body = new H.HirBody([], new H.BlockStmt([new H.ReturnStmt(new H.NewExpr(token, arguments, [], selfType))])),
                        };
                        string where = $"{info.Name}.{wrapper.Name}";
                        var context = new GenericContext(info.GenericCount);
                        int function = Functions.Count;
                        Functions.Add(null);
                        Plans.Add(new MethodPlan(info.Index, -1, function, null,
                            [.. wrapper.Parameters.Select(p => Lower(p.Type, where, context))], Lower(selfType, where, context))
                        {
                            Name = $"{info.Name}.{wrapper.Name}",
                            Generics = FunctionGenerics(info, null, where),
                            Synthetic = wrapper,
                        });
                        Implementations.Add(new Implementation(Requirement.Constructor, function));
                    }
                }
                if (info.IsClass && (info.Definition.Flags & (H.TypeFlags.Abstract | H.TypeFlags.Static)) == 0 && DestructorOf(info) is var (declaring, destructor)
                    && declaring >= 0 && Types[declaring].Base >= 0)
                {
                    // `$destroy(Self self)`: the object's `destroy()` (the class's own, which runs its base's), as the `fn(gc Self)`
                    // the runtime calls when the object dies (a count at zero, a collection, a region closing).
                    var selfType = new H.NamedType(H.TypeToken.Def(info.Index), OwnParameters(info));
                    var call = new H.CallExpr(new H.ParamExpr(0, selfType), H.MemberToken.Def(declaring, destructor), AncestorArguments(info.Index, OwnParameters(info), declaring), [], H.HirType.Void);
                    var wrapper = new H.MethodDef("$destroy", H.HirType.Void, [new H.Parameter("self", selfType)])
                    {
                        Flags = H.MethodFlags.Static | H.MethodFlags.Generated,
                        Body = new H.HirBody([], new H.BlockStmt([new H.ExprStmt(call)])),
                    };
                    string where = $"{info.Name}.$destroy";
                    var context = new GenericContext(info.GenericCount);
                    int function = Functions.Count;
                    Functions.Add(null);
                    Plans.Add(new MethodPlan(info.Index, -1, function, null, [Lower(selfType, where, context)], Lower(H.HirType.Void, where, context))
                    {
                        Name = where,
                        Generics = FunctionGenerics(info, null, where),
                        Synthetic = wrapper,
                    });
                    info.Finalizer = function;
                }
                var seen = new HashSet<string>();
                foreach (var implemented in info.Definition.Interfaces)
                {
                    foreach (var (iface, required, interfaceArguments) in InterfaceMethods(implemented, statics: true))
                    {
                        if (!seen.Add($"{RequirementName(iface, required)}<{string.Join(",", interfaceArguments.Select(x => H.Text.HirPrinter.Print(x, Assembly)))}>"))
                            continue;
                        var parameters = required.Parameters.Select(p => Substitute(p.Type, interfaceArguments)).ToArray();
                        var result = Substitute(required.Result, interfaceArguments);
                        var found = FindImplementation(info, required.Name, parameters, result);
                        if (found is null && required.Body is null)
                        {
                            Error(info.Name, $"implements {iface.Name} but has no method {required.Name} with its parameters");
                            continue;
                        }
                        var selfType = info.IsPrimitive ? info.Definition.Underlying! : new H.NamedType(H.TypeToken.Def(info.Index), OwnParameters(info));
                        int first = required.IsStatic ? 0 : 1;
                        var arguments = new List<H.Expr>();
                        for (int p = 0; p < parameters.Length; p++)
                            arguments.Add(new H.ParamExpr(p + first, parameters[p]));
                        H.CallExpr call;
                        if (found is { } own)
                        {
                            var typeArguments = AncestorArguments(info.Index, OwnParameters(info), own.Type);
                            call = new H.CallExpr(required.IsStatic ? null : new H.ParamExpr(0, selfType), H.MemberToken.Def(own.Type, own.Method), typeArguments, [.. arguments], result);
                        }
                        else if (required.IsStatic)
                        {
                            // A static property's default: the interface's own accessor, for this type.
                            call = new H.CallExpr(null, H.MemberToken.Def(iface.Index, iface.Definition.Methods.IndexOf(required)), interfaceArguments, [.. arguments], result);
                        }
                        else
                        {
                            // The interface's default: called directly (not through the itable, which leads back here) with
                            // the value as the interface (an object as is, a value boxed).
                            // An interface that extends this one may replace the default with its own: the most derived wins.
                            var (defaultOwner, defaultMethod, defaultArguments) = (iface, required, interfaceArguments);
                            foreach (var other in info.Definition.Interfaces)
                            {
                                foreach (var (candidateOwner, candidate, candidateArguments) in InterfaceMethods(other, statics: false))
                                {
                                    if (candidate.Body is not null && candidate.Name == required.Name && candidateOwner != defaultOwner && Extends(candidateOwner, defaultOwner)
                                        && candidate.Parameters.Select(p => Substitute(p.Type, candidateArguments)).SequenceEqual(parameters)
                                        && Substitute(candidate.Result, candidateArguments) == result)
                                        (defaultOwner, defaultMethod, defaultArguments) = (candidateOwner, candidate, candidateArguments);
                                }
                            }
                            var ifaceType = new H.NamedType(H.TypeToken.Def(defaultOwner.Index), defaultArguments);
                            var asInterface = new H.ConvertExpr(new H.ParamExpr(0, selfType), info.IsClass ? H.ConversionKind.Upcast : H.ConversionKind.Box, ifaceType);
                            int defaultIndex = defaultOwner.Definition.Methods.IndexOf(defaultMethod);
                            call = new H.CallExpr(asInterface, H.MemberToken.Def(defaultOwner.Index, defaultIndex), defaultArguments, [.. arguments], result) { NonVirtual = true };
                        }
                        H.Stmt body = result == H.HirType.Void ? new H.ExprStmt(call) : new H.ReturnStmt(call);
                        H.Parameter[] wrapperParameters = [.. parameters.Select((t, i) => new H.Parameter($"p{i}", t))];
                        if (!required.IsStatic)
                            wrapperParameters = [new H.Parameter("self", selfType), .. wrapperParameters];
                        var wrapper = new H.MethodDef($"$impl.{iface.Name}.{required.Name}", result, wrapperParameters)
                        {
                            Flags = H.MethodFlags.Static | H.MethodFlags.Generated,
                            Body = new H.HirBody([], new H.BlockStmt([body])),
                        };
                        string where = $"{info.Name}.{wrapper.Name}";
                        var context = new GenericContext(info.GenericCount);
                        int function = Functions.Count;
                        Functions.Add(null);
                        Plans.Add(new MethodPlan(info.Index, -1, function, null,
                            [.. wrapper.Parameters.Select(p => Lower(p.Type, where, context))], Lower(result, where, context))
                        {
                            // The arguments tell apart the wrappers of two instances (IPut<i32> and IPut<str>), whose names back ends use.
                            Name = interfaceArguments.Length == 0 ? $"{info.Name}.$impl.{iface.Name}.{required.Name}" : $"{info.Name}.$impl.{WrapperKey(iface, required, interfaceArguments)}",
                            Generics = FunctionGenerics(info, null, where),
                            Synthetic = wrapper,
                        });
                        Implementations.Add(new Implementation(RequirementName(iface, required), function));
                        Wrappers[(info.Index, WrapperKey(iface, required, interfaceArguments))] = function;
                    }
                }
                // The type's operators meet operator requirements (`op+(V, V) -> V`) for its own types; its conversions
                // meet `conv(V) -> T`.
                foreach (var op in info.Definition.Operators)
                {
                    if (MethodFunctions.TryGetValue(H.MemberToken.Def(info.Index, op.Method), out int function))
                        Implementations.Add(new Implementation(OperatorRequirementName(op.Symbol, op.Fixity == H.Fixity.Postfix), function));
                }
                for (int m = 0; m < info.Definition.Methods.Count; m++)
                {
                    if (info.Definition.Methods[m].Kind == H.MethodKind.Conversion && MethodFunctions.TryGetValue(H.MemberToken.Def(info.Index, m), out int function))
                        Implementations.Add(new Implementation(Requirement.Conversion, function));
                }
            }
        }

        /// <summary>The IR name of an operator requirement: <c>op+</c>, <c>op!.postfix</c>.</summary>
        internal static string OperatorRequirementName(string symbol, bool postfix) => "op" + symbol + (postfix ? Requirement.PostfixSuffix : "");

        /// <summary>
        /// The operators a type parameter's constraints ask for: its own, and those the interfaces constraining it ask of
        /// their own parameter (<c>T : INumber&lt;T&gt;</c>: <c>op +(T, T) : T</c>…), through the interfaces those extend.
        /// </summary>
        internal IEnumerable<H.OperatorRequirement> OperatorsOf(H.GenericParam param)
        {
            foreach (var op in param.Operators)
                yield return op;
            var seen = new HashSet<H.NamedType>();
            foreach (var constraint in param.Types.OfType<H.NamedType>())
            {
                foreach (var op in InterfaceOperators(constraint, seen))
                    yield return op;
            }
        }

        private IEnumerable<H.OperatorRequirement> InterfaceOperators(H.NamedType iface, HashSet<H.NamedType> seen)
        {
            if (!seen.Add(iface) || iface.Type is not { IsDefinition: true } || !Types[iface.Type.Index].IsInterface)
                yield break;
            var definition = Types[iface.Type.Index].Definition;
            foreach (var generic in definition.Generics)
            {
                foreach (var op in generic.Operators)
                    yield return new H.OperatorRequirement(op.Symbol, [.. op.Operands.Select(o => Substitute(o, iface.Arguments))], Substitute(op.Result, iface.Arguments));
            }
            foreach (var extended in definition.Interfaces)
            {
                foreach (var op in InterfaceOperators((H.NamedType)Substitute(extended, iface.Arguments), seen))
                    yield return op;
            }
        }

        /// <summary><c>op+(T, T) -> T</c>: an operator a type argument must have, as the IR requirement.</summary>
        internal Requirement OperatorRequirement(H.OperatorRequirement op, string where, GenericContext context)
        {
            bool postfix = op.Symbol.EndsWith(Requirement.PostfixSuffix, StringComparison.Ordinal);
            string symbol = postfix ? op.Symbol[..^Requirement.PostfixSuffix.Length] : op.Symbol;
            return new Requirement(OperatorRequirementName(symbol, postfix), [.. op.Operands.Select(o => Lower(o, where, context))], Lower(op.Result, where, context));
        }

        internal static string RequirementName(TypeInfo iface, H.MethodDef method) => $"{iface.Name}.{method.Name}";

        /// <summary>
        /// The instance methods of an interface type and of the interfaces it extends, with their type arguments; with
        /// <paramref name="statics"/>, its static (abstract) methods too, which are requirements of constrained type
        /// parameters but have no slot in itables.
        /// </summary>
        internal IEnumerable<(TypeInfo Interface, H.MethodDef Method, H.HirType[] Arguments)> InterfaceMethods(H.HirType type, bool statics = false)
        {
            if (type is not H.NamedType { Type.IsDefinition: true } named || !Types[named.Type.Index].IsInterface)
                yield break;
            var iface = Types[named.Type.Index];
            foreach (var method in iface.Definition.Methods)
            {
                // A static method with a body is the interface's own (`I.M()`), not something implementers provide; a
                // static property's accessor with a body is a default they may replace (`static i32 Precision { get => 53; }`).
                if (method.Generics.Count == 0 && (statics && (method.Body is null || StaticDefault(iface, method)) || !method.IsStatic))
                    yield return (iface, method, named.Arguments);
            }
            foreach (var extended in iface.Definition.Interfaces)
            {
                foreach (var inherited in InterfaceMethods(Substitute(extended, named.Arguments), statics))
                    yield return inherited;
            }
        }

        /// <summary>
        /// The IR type parameters of a function of <paramref name="info"/> (the type's, then the method's): each copyable,
        /// with one requirement per method of each interface that constrains it (<c>K : IHashable&lt;K&gt;</c> requires
        /// <c>IHashable.Hash(K) -&gt; i32</c>…). Every function of a generic type declares them, so whatever it calls with
        /// its parameters finds them met.
        /// </summary>
        internal GenericParam[] FunctionGenerics(TypeInfo info, H.MethodDef? method, string where)
        {
            var context = new GenericContext(info.GenericCount);
            var all = new List<(H.GenericParam Param, H.HirType Type)>();
            for (int g = 0; g < info.GenericCount; g++)
                all.Add((info.Definition.Generics[g], new H.GenericParamType(H.GenericOwner.Type, g)));
            if (method is not null)
            {
                for (int g = 0; g < method.Generics.Count; g++)
                    all.Add((method.Generics[g], new H.GenericParamType(H.GenericOwner.Method, g)));
            }
            var result = new GenericParam[all.Count];
            for (int i = 0; i < all.Count; i++)
            {
                var (param, self) = all[i];
                var requirements = new List<Requirement>();
                foreach (var constraint in param.Types)
                {
                    foreach (var (iface, required, arguments) in InterfaceMethods(constraint, statics: true))
                    {
                        var requirement = InterfaceRequirement(iface, required, arguments, self, where, context);
                        if (!requirements.Any(r => r.SameAs(requirement)))
                            requirements.Add(requirement);
                    }
                }
                foreach (var op in OperatorsOf(param))
                {
                    var requirement = OperatorRequirement(op, where, context);
                    if (!requirements.Any(r => r.SameAs(requirement)))
                        requirements.Add(requirement);
                }
                foreach (var target in param.ConvertsTo)
                {
                    // `T : (i64)T`: `conv(T) -> i64`, built into the numbers or a conversion the type declares.
                    var requirement = new Requirement(Requirement.Conversion, [Lower(self, where, context)], Lower(target, where, context));
                    if (!requirements.Any(r => r.SameAs(requirement)))
                        requirements.Add(requirement);
                }
                foreach (var source in param.ConvertsFrom)
                {
                    // `(T)n` with `T : number`: `conv(i32) -> T`, met by each number.
                    var requirement = new Requirement(Requirement.Conversion, [Lower(source, where, context)], Lower(self, where, context));
                    if (!requirements.Any(r => r.SameAs(requirement)))
                        requirements.Add(requirement);
                }
                foreach (var constructor in (param.Kinds & H.ConstraintKinds.New) != 0 ? param.Constructors.Append([]) : param.Constructors)
                {
                    // `T : new(i32)`: `new(i32) -> T`, met by the `$new` wrapper of each type's constructor.
                    var requirement = new Requirement(Requirement.Constructor, [.. constructor.Select(t => Lower(t, where, context))], Lower(self, where, context));
                    if (!requirements.Any(r => r.SameAs(requirement)))
                        requirements.Add(requirement);
                }
                result[i] = new GenericParam(param.Name, ConstraintOf(param)) { Requirements = [.. requirements] };
            }
            return result;
        }

        /// <summary><c>I.M(T, params…) -&gt; result</c>: calling interface method <paramref name="method"/> on a <paramref name="self"/>.</summary>
        internal Requirement InterfaceRequirement(TypeInfo iface, H.MethodDef method, H.HirType[] arguments, H.HirType self, string where, GenericContext context) =>
            new(RequirementName(iface, method),
                [.. method.IsStatic ? [] : new[] { Lower(self, where, context) }, .. method.Parameters.Select(p => Lower(Substitute(p.Type, arguments), where, context))],
                Lower(Substitute(method.Result, arguments), where, context));

        /// <summary>The method of <paramref name="info"/> (or a base) named <paramref name="name"/> taking those parameters.</summary>
        /// <summary>Whether interface <paramref name="derived"/> extends <paramref name="root"/> (directly or through others).</summary>
        private bool Extends(TypeInfo derived, TypeInfo root)
        {
            foreach (var extended in derived.Definition.Interfaces)
            {
                if (extended is H.NamedType { Type.IsDefinition: true } n && (n.Type.Index == root.Index || Extends(Types[n.Type.Index], root)))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Whether <paramref name="method"/> is the accessor, with a body, of a static property of the interface: a default
        /// an implementer's own property replaces (a static virtual member), reached through the type's requirement.
        /// </summary>
        internal static bool StaticDefault(TypeInfo iface, H.MethodDef method)
        {
            if (!iface.IsInterface || !method.IsStatic || method.Body is null)
                return false;
            int index = iface.Definition.Methods.IndexOf(method);
            return iface.Definition.Properties.Any(p => p.Getter == index || p.Setter == index);
        }

        internal (int Type, int Method)? FindImplementation(TypeInfo info, string name, H.HirType[] parameters, H.HirType result)
        {
            var own = OwnParameters(info);
            for (int t = info.Index; t >= 0; t = Types[t].Base)
            {
                var arguments = AncestorArguments(info.Index, own, t);
                var methods = Types[t].Definition.Methods;
                for (int m = 0; m < methods.Count; m++)
                {
                    var candidate = methods[m];
                    if (candidate.Name == name && candidate.Generics.Count == 0
                        && candidate.Parameters.Select(p => Substitute(p.Type, arguments)).SequenceEqual(parameters.Select(p => SelfAsPrimitive(p, info)))
                        && Substitute(candidate.Result, arguments) == SelfAsPrimitive(result, info))
                        return (t, m);
                }
                if (!Types[t].IsClass)
                    break;
            }
            return null;
        }

        /// <summary>A primitive's members name it by its type (<c>Int32</c>) or its keyword (<c>i32</c>): both match.</summary>
        private H.HirType SelfAsPrimitive(H.HirType type, TypeInfo info) =>
            info.IsPrimitive && type is H.NamedType { Type.IsDefinition: true } n && n.Type.Index == info.Index ? info.Definition.Underlying! : type;

        // ── String literals ────────────────────────────────

        private readonly Dictionary<H.Primitive, int> stringUnits = [];

        /// <summary>The static data of a literal's code units in <paramref name="unit"/> (UTF-8, UTF-16 or UTF-32), each once.</summary>
        internal int StringData(string text, H.Primitive unit = H.Primitive.U16)
        {
            lock (Data)
            {
                if (!literalData.TryGetValue((text, unit), out int index))
                {
                    index = Data.Count;
                    IEnumerable<System.Numerics.BigInteger> units = unit switch
                    {
                        H.Primitive.U8 => System.Text.Encoding.UTF8.GetBytes(text).Select(b => new System.Numerics.BigInteger(b)),
                        H.Primitive.U32 => text.EnumerateRunes().Select(r => new System.Numerics.BigInteger(r.Value)),
                        _ => text.Select(c => new System.Numerics.BigInteger(c)),
                    };
                    Data.Add(new StaticData(Primitive(unit), [.. units]));
                    literalData[(text, unit)] = index;
                }
                return index;
            }
        }

        private readonly Dictionary<(string, H.Primitive), int> literalData = [];

        private readonly Dictionary<(string, H.Primitive), int> literalGlobals = [];

        /// <summary>The global that keeps a literal's string once made (null until then).</summary>
        /// <summary>Static data for a constant array literal (a resource's bytes): its index.</summary>
        internal int ConstantData(H.Primitive unit, IEnumerable<System.Numerics.BigInteger> values)
        {
            lock (Data)
            {
                Data.Add(new StaticData(Primitive(unit), [.. values]));
                return Data.Count - 1;
            }
        }

        internal int LiteralGlobal(string text, H.Primitive unit, IrType type)
        {
            lock (Globals)
            {
                if (!literalGlobals.TryGetValue((text, unit), out int index))
                {
                    index = Globals.Count;
                    Globals.Add(new GlobalDef($"$literal{index}", type));
                    literalGlobals[(text, unit)] = index;
                }
                return index;
            }
        }

        /// <summary><c>$units(&amp;[T]) -&gt; gc [T]</c>: a new array with a literal's units (the string takes it).</summary>
        internal int StringUnits(H.Primitive unit = H.Primitive.U16)
        {
            lock (Data)
            {
                if (stringUnits.TryGetValue(unit, out int existing))
                    return existing;
                var element = Primitive(unit);
                var array = Options.Gc.ManagesObjects ? new GcType(new IR.ArrayType(element)) : (IrType)new IR.PointerType(new IR.ArrayType(element), Mutable: true);
                var b = new FunctionBuilder(unit == H.Primitive.U16 ? "$units" : $"$units.{unit.Keyword()}", array) { Unsafe = true };
                // The units of the literal copied in one mem.copy (none for an empty literal).
                int source = b.Argument(IrType.Ref(new IR.ArrayType(element)), "units");
                int length = b.Local(IrType.U64, "length", mutable: true);
                int made = b.Local(array, "made", mutable: true);
                int some = b.Local(IrType.Bool, null, mutable: true);
                var pointer = IrType.PtrMut(IrType.U8);
                int zero = b.Local(IrType.U64, null, mutable: true), size = b.Local(IrType.U64, null, mutable: true), bytes = b.Local(IrType.U64, null, mutable: true);
                int targetRef = b.Local(IrType.RefMut(element), null, mutable: true), sourceRef = b.Local(IrType.Ref(element), null, mutable: true);
                int target = b.Local(pointer, null, mutable: true), from = b.Local(pointer, null, mutable: true);
                int count = b.Local(IrType.I64, null, mutable: true), moved = b.Local(IrType.Unit, null, mutable: true);
                int copy = b.NewBlock(), done = b.NewBlock();
                b.Assign(length, Rvalue.Len(new Place(source).Deref()))
                 .Assign(made, new ArrayRvalue(ObjectAlloc, element, Operand.Copy(length)))
                 .Assign(some, Rvalue.Binary(BinaryOp.Gt, Operand.Copy(length), Operand.Const(0, IrType.U64)))
                 .If(Operand.Copy(some), copy, done);
                b.At(copy)
                 .Assign(zero, Operand.Const(0, IrType.U64))
                 .Assign(targetRef, Rvalue.RefMut(new Place(made).Deref().Index(zero)))
                 .Assign(target, Rvalue.Cast(Operand.Move(targetRef), pointer))
                 .Assign(sourceRef, Rvalue.Ref(new Place(source).Deref().Index(zero)))
                 .Assign(from, Rvalue.Cast(Operand.Move(sourceRef), pointer))
                 .Assign(size, Rvalue.SizeOf(element))
                 .Assign(bytes, Rvalue.Binary(BinaryOp.Mul, Operand.Copy(length), Operand.Copy(size)))
                 .Assign(count, Rvalue.Cast(Operand.Copy(bytes), IrType.I64))
                 .Assign(moved, new IntrinsicRvalue("mem.copy", [Operand.Copy(target), Operand.Copy(from), Operand.Copy(count)]))
                 .Goto(done);
                b.At(done).Assign(FunctionBuilder.ReturnPlace, Operand.Copy(made)).Return();
                var built = b.Build();
                int function;
                lock (Functions)
                {
                    function = Functions.Count;
                    Functions.Add(built);
                }
                stringUnits[unit] = function;
                return function;
            }
        }

        /// <summary>The core string type (<c>System.String</c>) and its constructor from code units.</summary>
        internal (TypeInfo Type, int Constructor)? StringType()
        {
            foreach (var info in Types)
            {
                if (info.Name != "System.String" || !info.IsClass)
                    continue;
                for (int m = 0; m < info.Definition.Methods.Count; m++)
                {
                    // From its code units: `new(u16[] units)`, or `new(T[] units)` when it is generic over the unit.
                    var method = info.Definition.Methods[m];
                    if (method.Kind == H.MethodKind.Constructor && method.Parameters is [{ Type: H.ArrayType { Element: H.PrimitiveType { Kind: H.Primitive.U16 } or H.GenericParamType } }])
                        return (info, m);
                }
            }
            return null;
        }
    }
}

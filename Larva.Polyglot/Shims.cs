using Larva.Hir;
using Larva.Hir.Services;

namespace Larva.Polyglot
{
    /// <summary>A parameter of an exported C function: its C type and name.</summary>
    public sealed record CParameter(string Type, string Name);

    /// <summary>
    /// A function the library exports for C: its symbol, C result and parameters, documentation, and what it stands for
    /// in the library (<c>Acme.Json.Parser.Parse(str)</c>, or "" for the library's own helpers).
    /// </summary>
    public sealed record CExport(string Name, string Result, IReadOnlyList<CParameter> Parameters, string? Doc, string Source)
    {
        public ExportRole Role { get; init; }

        /// <summary>The member it stands for (constructors, methods, getters, setters), or null.</summary>
        public PolyglotMember? Member { get; init; }

        /// <summary>The list type of a list operation, or null.</summary>
        public CList? List { get; init; }

        /// <summary>The type a constructor makes when it has no member (a class's implicit constructor), or null.</summary>
        public PolyglotType? Owner { get; init; }

        /// <summary>
        /// Its arguments as the library sees them, in order (<c>self</c> first, a setter's value last), each with how it
        /// crosses: each becomes one C parameter, or two (text, arrays: a pointer and a length).
        /// </summary>
        public IReadOnlyList<(string Name, Cross Cross)> Arguments { get; init; } = [];

        /// <summary>
        /// How its result crosses (null: none). A value or an array result adds a C parameter after the arguments (where it
        /// is written; an array's count), and so does an optional (the value; the function gives whether there is one).
        /// </summary>
        public Cross? Returns { get; init; }
    }

    /// <summary>An enum as C declares it: its type name, the integer type under it, and its members' names and values.</summary>
    public sealed record CEnum(string Name, string Underlying, IReadOnlyList<(string Name, string Value)> Members, string? Doc);

    /// <summary>A constant as C defines it (<c>#define ACME_JSON_MAX_DEPTH 64</c>).</summary>
    public sealed record CConstant(string Name, string Value, string? Doc);

    /// <summary>A value type as a C struct: the same fields in the same order (a value is laid out as C lays out a struct).</summary>
    public sealed record CStruct(string Name, IReadOnlyList<(string Type, string Name)> Fields, string? Doc)
    {
        /// <summary>A C union: every field at offset 0 (the library's <c>[ValueLayout(Layout.Explicit)]</c>).</summary>
        public bool Union { get; init; }

        /// <summary>The largest alignment a field gets (0: each field's own): <c>#pragma pack</c>.</summary>
        public int Pack { get; init; }

        /// <summary>Its size in bytes when the library makes it larger than its fields (0: what the fields need).</summary>
        public int Size { get; init; }

        /// <summary>The library's type (the faces read its fields again).</summary>
        public CatalogType? Type { get; init; }
    }

    /// <summary>How a value crosses to C (see <see cref="ShimGenerator"/>).</summary>
    public enum CrossKind { Void, Number, Enum, Text, Handle, Value, Array, List, Pointer, Optional, Function }

    /// <summary>
    /// How a value of a type crosses: its kind, the type in the library (as the shims' assembly writes it), its C type,
    /// whether it may be null, how elements cross (arrays, lists, optionals), and its type in the exports' signature.
    /// <see cref="Global"/> is the catalog's type; <see cref="Target"/> the exposed type it is (handles, enums, values);
    /// <see cref="List"/> the list type (lists).
    /// </summary>
    public sealed record Cross(CrossKind Kind, HirType Library, string C, bool Nullable = false, Cross? Element = null, HirType? Abi = null)
    {
        public HirType? Global { get; init; }
        public PolyglotType? Target { get; init; }
        public CList? List { get; init; }

        /// <summary>The callback type (functions), or null.</summary>
        public CCallback? Callback { get; init; }
    }

    /// <summary>
    /// A function type the library takes (<c>func&lt;i32&gt; : void</c>) as a C callback: its typedef's name
    /// (<c>acme_fn_i32_void_t</c>), how its arguments cross to the callback (the library to the other language) and how
    /// its result comes back, and its number. The callback is <c>R fn(void *context, arguments..., error_t *error)</c>.
    /// </summary>
    public sealed record CCallback(string Name, int Index, IReadOnlyList<Cross> Arguments, Cross Result);

    /// <summary>
    /// An interface of the library another language can implement: <c>prefix_iface_new_foreign(fn..., context, release,
    /// error)</c> makes an object of the library that is one, each method calling the function given for it (in the order
    /// of <see cref="Methods"/>: the interface's and those of the interfaces it extends).
    /// </summary>
    public sealed record CForeign(PolyglotType Interface, string Function, int Index, IReadOnlyList<(PolyglotMember Member, CCallback Fn)> Methods);

    /// <summary>A <c>List&lt;T&gt;</c> C sees: its handle type's name, the word for T, the type, and how T crosses.</summary>
    public sealed record CList(string Name, string Word, HirType Global, HirType Library, Cross Element);

    /// <summary>What an export stands for: a member of the library, an operation of a list type, or a helper of the library.</summary>
    public enum ExportRole { Constructor, Method, Getter, Setter, ListNew, ListCount, ListGet, ListSet, ListAdd, ListRemoveAt, ListClear, Release, Free, StringFree, ErrorMessage, ThreadDetach, ErrorType, TypeOf, CallbackError, ForeignNew, ForeignContext, StringNew }

    /// <summary>What a library offers C: its prefix, the handle types, structs, enums, constants and exported functions.</summary>
    public sealed class CLibrary
    {
        /// <summary>What every name of the library starts with (<c>acme_json</c>).</summary>
        public required string Prefix { get; init; }

        /// <summary>The handle types (<c>acme_json_parser_t</c>, <c>acme_json_list_str_t</c>), each with its documentation.</summary>
        public List<(string Name, string? Doc)> Handles { get; } = [];
        public List<CEnum> Enums { get; } = [];

        /// <summary>The structs, each after the structs it holds.</summary>
        public List<CStruct> Structs { get; } = [];
        public List<CConstant> Constants { get; } = [];
        public List<CExport> Exports { get; } = [];

        /// <summary>
        /// The exception types an error can be (their full names, most derived first): what <c>prefix_error_type</c>
        /// tells apart. The last is <c>System.Exception</c>.
        /// </summary>
        public List<string> ErrorTypes { get; } = [];

        /// <summary>The classes C sees, by the number <c>prefix_type_of</c> gives an object (most derived first).</summary>
        public List<PolyglotType> Classes { get; } = [];

        /// <summary>The callback types the library's functions take, by their number.</summary>
        public List<CCallback> Callbacks { get; } = [];

        /// <summary>The interfaces another language can implement, by their number.</summary>
        public List<CForeign> Foreigns { get; } = [];

        /// <summary>A callback's context release: <c>void prefix_release_t(void *context)</c>.</summary>
        public string ReleaseType => Prefix + "_release_t";

        public string ErrorType => Prefix + "_error_t";
    }

    /// <summary>
    /// The shims of a library for C: a HIR assembly (<c>Name.Polyglot</c>) that references the library and exports, for
    /// each member C can call, a C function, plus the <see cref="CLibrary"/> the header is written from.
    /// <para>
    /// Each export is minimal (it enters the runtime, calls the function that does the work, and leaves): it allocates
    /// nothing, so no safepoint runs on a thread before it is attached, and the static constructors run after it entered.
    /// The function that does the work converts the arguments, calls the member, converts the result, and catches what it
    /// throws: the exception, as a handle, goes to the last parameter (<c>error</c>, which may be null) and the result is
    /// zero.
    /// </para>
    /// <para>
    /// How values cross: numbers and bools as they are; an enum as its number; text in as a pointer and a length (UTF-8)
    /// and out as a <c>char *</c> the caller frees; an object as a handle (checked to be of the type; 0 for null where the
    /// library takes null); a value type whose fields are such numbers as a struct, in by pointer and out through a
    /// pointer the caller gives; an array of numbers or such values in as a pointer and a count (copied in) and out as a
    /// block the caller frees and its count; a <c>List&lt;T&gt;</c> as a handle of its own type, with functions to make,
    /// count, read, write, add to, remove from and clear it (the list itself, not a copy).
    /// </para>
    /// </summary>
    public sealed class ShimGenerator
    {
        private readonly PolyglotModel model;
        private readonly HirCatalog catalog;
        private readonly HirAssembly shims;
        private readonly TypeDef holder;
        private readonly int holderIndex;
        private readonly CLibrary api;
        private readonly CRules c;
        private readonly Dictionary<CatalogType, PolyglotType> exposed;
        private readonly Dictionary<CatalogType, CStruct?> structs = [];
        private readonly Dictionary<string, CList> lists = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Callback> callbacks = new(StringComparer.Ordinal);
        private readonly List<(int Type, int Context)> foreignTypes = [];

        /// <summary>A callback type's class in the shims (<c>$Polyglot.Callback0</c>): its fields, its Invoke.</summary>
        private sealed record Callback(CCallback Api, FuncType Function, int Type, NativeFuncType Native, int Fn, int Context, int Release, int Invoke);

        // System.Core, as the shims use it.
        private readonly CatalogType listType;
        private readonly HirType any, exception, text, textPointer, voidPointer, bytePointer, u64Pointer;
        private readonly MemberToken fromUtf8, toUtf8, freeNative, allocNative, copyBytes, message, argumentError;
        private readonly int enter, leave, detach, handleNew, handleGet, handleFree, callout, callin;
        private readonly MemberToken textLength, newException;
        private readonly NativeFuncType releaseFunction;

        private static readonly HirType U64 = HirType.Of(Primitive.U64), I32 = HirType.I32, I64 = HirType.I64;

        private ShimGenerator(PolyglotModel model, HirCatalog catalog)
        {
            this.model = model;
            this.catalog = catalog;
            shims = new HirAssembly { Name = model.Assembly.Name + ".Polyglot" };
            holder = new TypeDef { Namespace = "$Polyglot", Name = "Exports", Kind = TypeKind.Class, Flags = TypeFlags.Static | TypeFlags.Unsafe };
            shims.Add(holder);
            holderIndex = 0;
            c = (CRules)model.Rules(TargetLanguage.C);
            api = new CLibrary { Prefix = c.Namespace(model.Assembly.Name) };
            exposed = model.Types.Where(t => !t.IsModule).ToDictionary(t => t.Source);

            listType = Core("System.List");
            any = Named(Core("System.Any"));
            exception = Named(Core("System.Exception"));
            var marshal = Core("System.Runtime.InteropServices.Marshal");
            var from = marshal.Declared.First(m => m.Name == "PtrToStringUTF8" && m.Parameters.Count == 2);
            var to = marshal.Declared.First(m => m.Name == "StringToHGlobalUTF8");
            var free = marshal.Declared.First(m => m.Name == "FreeHGlobal");
            var alloc = marshal.Declared.First(m => m.Name == "AllocHGlobal");
            var copy = Core("System.Runtime.InteropServices.Memory").Declared.First(m => m.Name == "Copy");
            fromUtf8 = Ref(from);
            toUtf8 = Ref(to);
            freeNative = Ref(free);
            allocNative = Ref(alloc);
            copyBytes = Ref(copy);
            text = Local(from.ValueType);
            textPointer = Local(from.Parameters[0].Type);
            voidPointer = Local(free.Parameters[0].Type);
            bytePointer = Local(copy.Parameters[0].Type);
            u64Pointer = new PointerType(U64);
            message = Ref(Core("System.Exception").Declared.First(m => m.Name == "Message"));
            argumentError = Ref(Core("System.ArgumentError").Declared.First(m => m.Kind == CatalogMemberKind.Constructor && m.Parameters.Count == 1));

            enter = Extern("runtime.enter", HirType.Void);
            leave = Extern("runtime.leave", HirType.Void);
            detach = Extern("thread.detach", HirType.Void);
            handleNew = Extern("handle.new", U64, new Parameter("value", any));
            handleGet = Extern("handle.get", any, new Parameter("handle", U64));
            handleFree = Extern("handle.free", HirType.Void, new Parameter("handle", U64));
            callout = Extern("runtime.callout", HirType.Void);
            callin = Extern("runtime.callin", HirType.Void);
            textLength = Ref(Core("System.String").Declared.First(m => m.Name == "Length" && m.Kind == CatalogMemberKind.Property));
            newException = Ref(Core("System.Exception").Declared.First(m => m.Kind == CatalogMemberKind.Constructor && m.Parameters.Count == 1));
            releaseFunction = new NativeFuncType([voidPointer], HirType.Void);
        }

        /// <summary>The shims of <paramref name="model"/> (which must name C among its languages), and what they export.</summary>
        public static (HirAssembly Shims, CLibrary Api) Generate(PolyglotModel model, HirCatalog catalog)
        {
            var generator = new ShimGenerator(model, catalog);
            generator.Run();
            return (generator.shims, generator.api);
        }

        // ── System.Core and references ─────────────────────────────

        private CatalogType Core(string fullName) =>
            catalog.AllTypes.FirstOrDefault(t => t.FullName == fullName && t.Assembly.Name == "System.Core")
            ?? throw new InvalidOperationException($"System.Core has no {fullName}");

        private NamedType Named(CatalogType type, params HirType[] arguments) =>
            new(shims.Reference(type.Assembly.Name, type.Namespace, type.Name, type.Arity), arguments);

        /// <summary>A global type (the catalog's) as the shims' assembly writes it: its named types are references.</summary>
        private HirType Local(HirType global) => global switch
        {
            NamedType n when catalog.TypeOf(n) is { } t => Named(t, [.. n.Arguments.Select(Local)]),
            NullableType n => new NullableType(Local(n.Inner)),
            ArrayType a => a with { Element = Local(a.Element) },
            PointerType p => new PointerType(Local(p.Target), p.Mutable),
            FuncType f => new FuncType([.. f.Parameters.Select(Local)], Local(f.Result)),
            NativeFuncType f => new NativeFuncType([.. f.Parameters.Select(Local)], Local(f.Result)),
            InlineArrayType i => i with { Element = Local(i.Element), SizeOf = i.SizeOf is null ? null : Local(i.SizeOf) },
            _ => global,
        };

        /// <summary>A reference to a member of the library or of System.Core.</summary>
        private MemberToken Ref(CatalogMember member)
        {
            var parent = shims.Reference(member.Type.Assembly.Name, member.Type.Namespace, member.Type.Name, member.Type.Arity);
            var reference = member.Kind switch
            {
                CatalogMemberKind.Field or CatalogMemberKind.Constant or CatalogMemberKind.EnumValue =>
                    new MemberRef(MemberKind.Field, parent, member.Name, 0, [], Local(member.ValueType)),
                CatalogMemberKind.Property or CatalogMemberKind.Indexer =>
                    new MemberRef(MemberKind.Property, parent, member.Name, 0, [.. member.Parameters.Select(p => Local(p.Type))], Local(member.ValueType)),
                _ => new MemberRef(MemberKind.Method, parent, member.Method!.Name, member.Generics.Count,
                    [.. member.Parameters.Select(p => Local(p.Type))], Local(member.ValueType)),
            };
            return shims.Reference(reference);
        }

        /// <summary>A runtime service of the shims' class (<c>extern("handle.new")</c>): its method index.</summary>
        private int Extern(string service, HirType result, params Parameter[] parameters) =>
            holder.AddMethod(new MethodDef("$" + service.Replace('.', '_'), result, parameters)
            {
                Flags = MethodFlags.Static | MethodFlags.Extern,
                ExternName = service,
            });

        private CallExpr Call(int method, HirType result, params Expr[] arguments) =>
            new(null, MemberToken.Def(holderIndex, method), [], arguments, result);

        // ── Expressions the shims are made of ──────────────────────

        private static LiteralExpr Zero(HirType type) => new(new IntegerConstant(0, ((PrimitiveType)type).Kind), type);

        private static Expr Number(Expr value, HirType to) => value.Type.Equals(to) ? value : new ConvertExpr(value, ConversionKind.Numeric, to);

        /// <summary>Whether a pointer is not null (compared as a number).</summary>
        private static BinaryExpr NotNull(Expr pointer) =>
            new(BinaryOp.NotEqual, new ConvertExpr(pointer, ConversionKind.Pointer, U64), Zero(U64), HirType.Bool);

        private ThrowStmt Fails(string message) =>
            new(new NewExpr(argumentError, [new LiteralExpr(new StringConstant(message), text)], [], Named(Core("System.ArgumentError"))));

        /// <summary><c>Memory.Copy(target, source, bytes)</c> (the pointers as bytes).</summary>
        private ExprStmt CopyBytes(Expr target, Expr source, Expr bytes) =>
            new(new CallExpr(null, copyBytes, [],
                [new ConvertExpr(target, ConversionKind.Pointer, bytePointer), new ConvertExpr(source, ConversionKind.Pointer, bytePointer), bytes], HirType.Void));

        /// <summary>The address of an array's first element.</summary>
        private static Expr First(Expr array, HirType element) =>
            new AddressOfExpr(new ElementExpr(array, [new LiteralExpr(new IntegerConstant(0, Primitive.I32), I32)], element), new PointerType(element));

        // ── How values cross ───────────────────────────────────────



        /// <summary>How a value of <paramref name="type"/> (global) crosses, or null: it does not cross yet.</summary>
        private Cross? Crossing(HirType type)
        {
            if (CrossingOf(type) is not { } made)
                return null;
            var named = (type is NullableType n ? n.Inner : type) as NamedType;
            var target = named is not null && catalog.TypeOf(named) is { } t && exposed.TryGetValue(t, out var e) ? e : null;
            return made with { Global = type, Target = target ?? made.Target };
        }

        private Cross? CrossingOf(HirType type)
        {
            switch (type)
            {
                case PrimitiveType { Kind: Primitive.Void }:
                    return new Cross(CrossKind.Void, type, "void");
                case PrimitiveType p when CNumber(p.Kind) is { } number:
                    return new Cross(CrossKind.Number, type, number, Abi: type);
                case NullableType { Inner: NamedType inner } when Handle(inner) is { } nullable:
                    return new Cross(CrossKind.Handle, Local(type), nullable, Nullable: true, Abi: U64);
                case NullableType { Inner: var inner } when Crossing(inner) is { Kind: CrossKind.Text } text:
                    return text with { Library = Local(type), Nullable = true };
                case NullableType { Inner: var inner } when Crossing(inner) is { Kind: CrossKind.Number or CrossKind.Enum or CrossKind.Value } value:
                    return new Cross(CrossKind.Optional, Local(type), value.C, Element: value, Abi: new PointerType(value.Library));
                case PointerType { Target: PrimitiveType target } pointer when target.Kind == Primitive.Void || CNumber(target.Kind) is not null:
                    return new Cross(CrossKind.Pointer, Local(type), (pointer.Mutable ? "" : "const ") + (target.Kind == Primitive.Void ? "void" : CNumber(target.Kind)) + " *", Abi: Local(type));
                case PointerType { Target: NamedType target } pointer when catalog.TypeOf(target) is { IsValue: true } t && Struct(t) is { } pointed:
                    return new Cross(CrossKind.Pointer, Local(type), (pointer.Mutable ? "" : "const ") + pointed.Name + " *", Abi: Local(type)) { Target = exposed[t] };
                case PointerType pointer:
                    // What C cannot name (an opaque struct, a pointer to a pointer): void*.
                    return new Cross(CrossKind.Pointer, Local(type), (pointer.Mutable ? "" : "const ") + "void *", Abi: new PointerType(HirType.Void, pointer.Mutable));
                case NativeFuncType:
                    // A C function pointer: as it is (void* in C's declarations).
                    return new Cross(CrossKind.Pointer, Local(type), "void *", Abi: voidPointer);
                case FuncType f when CallbackOf(f) is { } callback:
                    return new Cross(CrossKind.Function, Local(type), callback.Api.Name, Abi: callback.Native) { Callback = callback.Api };
                case NullableType { Inner: FuncType f } when CallbackOf(f) is { } callback:
                    return new Cross(CrossKind.Function, Local(type), callback.Api.Name, Nullable: true, Abi: callback.Native) { Callback = callback.Api };
                case ArrayType { Rank: 1, Element: var element } when Crossing(element) is { Kind: CrossKind.Number or CrossKind.Enum or CrossKind.Value } elementCross:
                    return new Cross(CrossKind.Array, Local(type), elementCross.C, Element: elementCross, Abi: new PointerType(elementCross.Library));
                case NamedType n when catalog.TypeOf(n) is { } t:
                    if (t.FullName == "System.String" && n.Arguments is [PrimitiveType { Kind: Primitive.U8 }])
                        return new Cross(CrossKind.Text, Local(type), "const char *", Abi: textPointer);
                    if (t.IsEnum && exposed.TryGetValue(t, out var e) && t.Definition.Underlying is PrimitiveType u && CNumber(u.Kind) is not null)
                        return new Cross(CrossKind.Enum, Local(type), e.Names[TargetLanguage.C], Abi: u);
                    if (t.IsValue && Struct(t) is { } s)
                        return new Cross(CrossKind.Value, Local(type), s.Name, Abi: new PointerType(Local(type)));
                    if (t == listType && List(n) is { } list)
                        return new Cross(CrossKind.List, list.Library, list.Name, Element: list.Element, Abi: U64) { List = list };
                    if (Handle(n) is { } handle)
                        return new Cross(CrossKind.Handle, Local(type), handle, Abi: U64);
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>The C-level type of an argument a callback gets (the library to the other language).</summary>
        private HirType[] CallbackAbi(Cross x) => x.Kind switch
        {
            CrossKind.Number => [x.Abi!],
            CrossKind.Enum => [x.Abi!],
            CrossKind.Text => [textPointer, U64],
            CrossKind.Value => [new PointerType(x.Library, Mutable: false)],
            _ => [U64],
        };

        /// <summary>Whether a callback (or a method another language implements) can take and give these.</summary>
        private static bool CallsOut(IEnumerable<Cross?> arguments, Cross? result) =>
            arguments.All(a => a is { Kind: CrossKind.Number or CrossKind.Enum or CrossKind.Text or CrossKind.Handle or CrossKind.Value })
            && result is { Kind: CrossKind.Void or CrossKind.Number or CrossKind.Enum or CrossKind.Handle } or { Kind: CrossKind.Text, Nullable: false };

        /// <summary>The type of a callback's result in C (text comes back as a handle to a string the library made).</summary>
        private static HirType OutAbi(Cross result) => result.Kind switch
        {
            CrossKind.Void => HirType.Void,
            CrossKind.Handle or CrossKind.Text => U64,
            _ => result.Abi!,
        };

        private NativeFuncType OutFunction(IEnumerable<Cross> arguments, Cross result) =>
            new([voidPointer, .. arguments.SelectMany(CallbackAbi), u64Pointer], OutAbi(result));

        /// <summary>
        /// The body of a method that calls another language's function <paramref name="fn"/> (with
        /// <paramref name="context"/> first, the method's parameters converted, an error last): it leaves Larva code for the
        /// call (callout, callin), gives back text it copied, throws what the function failed with, and gives its result.
        /// </summary>
        private HirBody OutCall(Expr fn, Expr context, IReadOnlyList<Cross> arguments, IReadOnlyList<HirType> parameters, Cross result)
        {
            var resultAbi = OutAbi(result);
            var locals = new List<HirLocal> { new("$error", U64) };
            var body = new List<Stmt> { new LocalStmt(0, Zero(U64)) };
            var cArguments = new List<Expr> { context };
            var after = new List<Stmt>();
            for (int i = 0; i < arguments.Count; i++)
            {
                var x = arguments[i];
                var a = new ParamExpr(i, parameters[i]);
                switch (x.Kind)
                {
                    case CrossKind.Number:
                        cArguments.Add(a);
                        break;
                    case CrossKind.Enum:
                        cArguments.Add(new ConvertExpr(a, ConversionKind.Enum, x.Abi!));
                        break;
                    case CrossKind.Text:
                    {
                        // A copy for the call (UTF-8, and its length), freed after it.
                        locals.Add(new HirLocal("$text" + i, textPointer));
                        int copy = locals.Count - 1;
                        body.Add(new LocalStmt(copy, new CallExpr(null, toUtf8, [], [a], textPointer)));
                        cArguments.Add(new LocalExpr(copy, textPointer));
                        cArguments.Add(Number(new PropertyExpr(a, textLength, [], I32), U64));
                        after.Add(new ExprStmt(new CallExpr(null, freeNative, [], [new ConvertExpr(new LocalExpr(copy, textPointer), ConversionKind.Pointer, voidPointer)], HirType.Void)));
                        break;
                    }
                    case CrossKind.Value:
                    {
                        locals.Add(new HirLocal("$value" + i, x.Library));
                        int copy = locals.Count - 1;
                        body.Add(new LocalStmt(copy, a));
                        cArguments.Add(new AddressOfExpr(new LocalExpr(copy, x.Library), new PointerType(x.Library, Mutable: false)));
                        break;
                    }
                    default:
                        // An object: a handle the other language owns (and gives back).
                        cArguments.Add(x.Nullable
                            ? new ConditionalExpr(new BinaryExpr(BinaryOp.Equal, a, new LiteralExpr(new NullConstant(), x.Library), HirType.Bool), Zero(U64),
                                Call(handleNew, U64, new ConvertExpr(new ConvertExpr(a, ConversionKind.Unwrap, ((NullableType)x.Library).Inner), ConversionKind.Upcast, any)), U64)
                            : Call(handleNew, U64, new ConvertExpr(a, ConversionKind.Upcast, any)));
                        break;
                }
            }
            cArguments.Add(new AddressOfExpr(new LocalExpr(0, U64), u64Pointer));
            var invoke = new InvokeExpr(fn, [.. cArguments], resultAbi);
            body.Add(new ExprStmt(Call(callout, HirType.Void)));
            int resultLocal = -1;
            if (result.Kind == CrossKind.Void)
                body.Add(new ExprStmt(invoke));
            else
            {
                locals.Add(new HirLocal("$result", resultAbi));
                resultLocal = locals.Count - 1;
                body.Add(new LocalStmt(resultLocal, invoke));
            }
            body.Add(new ExprStmt(Call(callin, HirType.Void)));
            body.AddRange(after);
            // What the other language failed with: its error (a handle to an exception) thrown here.
            locals.Add(new HirLocal("$failure", exception));
            int failure = locals.Count - 1;
            body.Add(new IfStmt(new BinaryExpr(BinaryOp.NotEqual, new LocalExpr(0, U64), Zero(U64), HirType.Bool), new BlockStmt([
                new LocalStmt(failure, new ConvertExpr(Call(handleGet, any, new LocalExpr(0, U64)), ConversionKind.Downcast, exception)),
                new ExprStmt(Call(handleFree, HirType.Void, new LocalExpr(0, U64))),
                new ThrowStmt(new LocalExpr(failure, exception))]), null));
            if (resultLocal < 0)
            {
                body.Add(new ReturnStmt(null));
                return new HirBody([.. locals], new BlockStmt([.. body]));
            }
            var value = new LocalExpr(resultLocal, resultAbi);
            switch (result.Kind)
            {
                case CrossKind.Text:
                {
                    // A string the library made for the other language (string_new): taken, its handle given back.
                    locals.Add(new HirLocal("$string", result.Library));
                    int made = locals.Count - 1;
                    body.Add(new LocalStmt(made, new ConvertExpr(Call(handleGet, any, value), ConversionKind.Downcast, result.Library)));
                    body.Add(new ExprStmt(Call(handleFree, HirType.Void, value)));
                    body.Add(new ReturnStmt(new LocalExpr(made, result.Library)));
                    break;
                }
                case CrossKind.Enum:
                    body.Add(new ReturnStmt(new ConvertExpr(value, ConversionKind.Enum, result.Library)));
                    break;
                case CrossKind.Handle when result.Nullable:
                    body.Add(new ReturnStmt(new ConditionalExpr(new BinaryExpr(BinaryOp.Equal, value, Zero(U64), HirType.Bool),
                        new LiteralExpr(new NullConstant(), result.Library),
                        new ConvertExpr(new ConvertExpr(Call(handleGet, any, value), ConversionKind.Downcast, ((NullableType)result.Library).Inner), ConversionKind.Wrap, result.Library), result.Library)));
                    break;
                case CrossKind.Handle:
                    body.Add(new ReturnStmt(new ConvertExpr(Call(handleGet, any, value), ConversionKind.Downcast, result.Library)));
                    break;
                default:
                    body.Add(new ReturnStmt(value));
                    break;
            }
            return new HirBody([.. locals], new BlockStmt([.. body]));
        }

        /// <summary><c>destroy()</c> of a class that holds another language's context: the context given back (when it gave a release).</summary>
        private MethodDef Destroy(Expr release, Expr context)
        {
            var hasRelease = new BinaryExpr(BinaryOp.NotEqual, new ConvertExpr(release, ConversionKind.Pointer, U64), Zero(U64), HirType.Bool);
            // An override of System.Any's destroy(), which does nothing.
            return new MethodDef("destroy", HirType.Void)
            {
                Flags = MethodFlags.Unsafe | MethodFlags.Virtual | MethodFlags.Override,
                Access = Access.Inherited,
                Body = new HirBody([], new BlockStmt([
                    new IfStmt(hasRelease, new BlockStmt([
                        new ExprStmt(Call(callout, HirType.Void)),
                        new ExprStmt(new InvokeExpr(release, [context], HirType.Void)),
                        new ExprStmt(Call(callin, HirType.Void))]), null),
                    new ReturnStmt(null)])),
            };
        }

        /// <summary>
        /// A function type as a callback, made the first time (its class in the shims): arguments that are numbers, enums,
        /// text, objects or such values; a result that is nothing, a number, an enum, text or an object. Null otherwise.
        /// </summary>
        private Callback? CallbackOf(FuncType function)
        {
            var arguments = function.Parameters.Select(Crossing).ToList();
            var result = Crossing(function.Result);
            if (!CallsOut(arguments, result))
                return null;
            string words = function.Parameters.Length == 0 ? "" : string.Join("_", function.Parameters.Select(p => CRules.TypeWord(p, catalog))) + "_";
            string name = $"{api.Prefix}_fn_{words}{(result!.Kind == CrossKind.Void ? "void" : CRules.TypeWord(function.Result, catalog))}_t";
            if (callbacks.TryGetValue(name, out var known))
                return known;
            var native = OutFunction(arguments!, result);
            var made = new CCallback(name, api.Callbacks.Count, arguments!, result);
            api.Callbacks.Add(made);

            // The class: the C function, its context and release; Invoke calls out; its destructor releases the context.
            var def = new TypeDef { Namespace = "$Polyglot", Name = "Callback" + made.Index, Kind = TypeKind.Class, Flags = TypeFlags.Final | TypeFlags.Unsafe, Base = any };
            int fn = def.AddField(new FieldDef("fn", native));
            int context = def.AddField(new FieldDef("context", voidPointer));
            int release = def.AddField(new FieldDef("release", releaseFunction));
            int type = shims.Types.Count;
            shims.Add(def);
            var self = new NamedType(TypeToken.Def(type), []);
            Expr Field(int index, HirType fieldType) => new FieldExpr(new ThisExpr(self), MemberToken.Def(type, index), fieldType);
            var parameters = function.Parameters.Select((p, i) => new Parameter("a" + i, Local(p))).ToArray();
            int invokeIndex = def.AddMethod(new MethodDef("Invoke", result.Kind == CrossKind.Void ? HirType.Void : result.Library, parameters)
            {
                Flags = MethodFlags.Unsafe,
                Body = OutCall(Field(fn, native), Field(context, voidPointer), arguments!, [.. parameters.Select(p => p.Type)], result),
            });
            def.AddMethod(Destroy(Field(release, releaseFunction), Field(context, voidPointer)));
            var callback = new Callback(made, function, type, native, fn, context, release, invokeIndex);
            callbacks[name] = callback;
            return callback;
        }

        /// <summary>The C type of a class or interface C sees (its handle type), or null.</summary>
        private string? Handle(NamedType type) =>
            catalog.TypeOf(type) is { } t && (t.IsClass || t.IsInterface) && !t.IsStatic && t.Arity == 0 && exposed.TryGetValue(t, out var p)
                ? p.Names[TargetLanguage.C] : null;

        private static string? CNumber(Primitive p) => p switch
        {
            Primitive.Bool => "bool",
            Primitive.I8 => "int8_t",
            Primitive.I16 => "int16_t",
            Primitive.I32 => "int32_t",
            Primitive.I64 => "int64_t",
            Primitive.U8 => "uint8_t",
            Primitive.U16 => "uint16_t",
            Primitive.U32 => "uint32_t",
            Primitive.U64 => "uint64_t",
            Primitive.F32 => "float",
            Primitive.F64 => "double",
            _ => null,
        };

        /// <summary>
        /// A value type as a C struct, when C can hold it as it is: exposed, not generic, laid out as C does (no
        /// [ValueLayout]), and every instance field a number, a bool, an enum C sees or another such value. Null otherwise.
        /// </summary>
        private CStruct? Struct(CatalogType type)
        {
            if (structs.TryGetValue(type, out var known))
                return known;
            structs[type] = null; // a value that holds itself (it cannot, but a cycle must end)
            if (!exposed.TryGetValue(type, out var seen) || type.Arity > 0)
                return null;
            // [ValueLayout(kind, Pack = p, Size = s)]: a union (explicit, every field at 0) or packing C can say.
            bool union = false;
            int pack = 0, size = 0;
            if (FindAttribute(type, type.Definition.Attributes, "ValueLayoutAttribute") is { } layout)
            {
                union = layout.Arguments is [EnumConstant { Value: var kind }, ..] && kind == 1;
                foreach (var (named, value) in layout.Named)
                {
                    if (named == "Pack" && value is IntegerConstant packed)
                        pack = (int)packed.Value;
                    else if (named == "Size" && value is IntegerConstant sized)
                        size = (int)sized.Value;
                }
            }
            var fields = new List<(string, string)>();
            foreach (var field in type.Definition.Fields)
            {
                if ((field.Flags & (FieldFlags.Static | FieldFlags.Const)) != 0)
                    continue;
                if (union && FindAttribute(type, field.Attributes, "FieldOffsetAttribute") is { Arguments: [IntegerConstant { Value: var offset }] } && offset != 0)
                    return null; // fields that overlap at other offsets: C has no way to say it
                var fieldType = type.Global(field.Type);
                if (fieldType is InlineArrayType { Element: PrimitiveType element, SizeOf: null } array && CNumber(element.Kind) is { } number)
                {
                    fields.Add((number, $"{c.Parameter(field.Name)}[{array.Count}]"));
                    continue;
                }
                var crossing = Crossing(fieldType);
                if (crossing is not { Kind: CrossKind.Number or CrossKind.Enum or CrossKind.Value or CrossKind.Pointer })
                    return null;
                fields.Add((crossing.C, c.Parameter(field.Name)));
            }
            if (fields.Count == 0)
                return null;
            var made = new CStruct(seen.Names[TargetLanguage.C], fields, type.Doc) { Union = union, Pack = pack, Size = size, Type = type };
            structs[type] = made;
            api.Structs.Add(made); // after the structs its fields needed (made first, above)
            return made;
        }

        /// <summary>The attribute among <paramref name="attributes"/> (of <paramref name="type"/>'s assembly) whose class is <paramref name="name"/>.</summary>
        private static HirAttribute? FindAttribute(CatalogType type, IEnumerable<HirAttribute> attributes, string name)
        {
            var hir = type.Assembly.Hir;
            foreach (var attribute in attributes)
            {
                var token = attribute.Constructor;
                string? owner = token.IsDefinition ? hir.Types[token.Type].Name
                    : hir.MemberRefs[token.Member].Parent is var parent && parent.Value < 0 ? hir.TypeRefs[~parent.Value].Name
                    : hir.Types[parent.Value].Name;
                if (owner == name)
                    return attribute;
            }
            return null;
        }

        private static bool HasAttribute(CatalogType type, string name)
        {
            var hir = type.Assembly.Hir;
            foreach (var attribute in type.Definition.Attributes)
            {
                string? owner = attribute.Constructor.IsDefinition ? hir.Types[attribute.Constructor.Type].Name
                    : hir.MemberRefs[attribute.Constructor.Member].Parent is var parent && parent.Value < 0 ? hir.TypeRefs[~parent.Value].Name
                    : hir.Types[hir.MemberRefs[attribute.Constructor.Member].Parent.Value].Name;
                if (owner == name)
                    return true;
            }
            return false;
        }

        /// <summary>A <c>List&lt;T&gt;</c> C sees (T one that crosses as an element), registered the first time.</summary>
        private CList? List(NamedType type)
        {
            if (type.Arguments is not [var argument])
                return null;
            string word = CRules.TypeWord(argument, catalog);
            if (lists.TryGetValue(word, out var known))
                return known;
            if (Crossing(argument) is not { Kind: CrossKind.Number or CrossKind.Enum or CrossKind.Text or CrossKind.Value or CrossKind.Handle, Nullable: false } element)
                return null;
            var list = new CList($"{api.Prefix}_list_{word}_t", word, type, Local(type), element);
            lists[word] = list;
            return list;
        }

        // ── Members ────────────────────────────────────────────────

        private void Run()
        {
            Helpers();
            foreach (var type in model.Types)
            {
                var source = type.Source;
                if (source.Arity > 0)
                {
                    Skip(type.SourceName, "generic types are not offered to C yet");
                    continue;
                }
                if (source.IsEnum)
                {
                    Enum(type);
                    continue;
                }
                if (source.IsValue && Struct(source) is null)
                {
                    Skip(type.SourceName, "a value type crosses to C only when its fields are numbers, bools, enums and such values");
                    continue;
                }
                if (!type.IsModule && !source.IsStatic && !source.IsValue)
                    api.Handles.Add((type.Names[TargetLanguage.C], source.Doc));
                foreach (var member in type.Members)
                    Member(type, member);
                if (source.IsClass && !source.IsAbstract && !source.IsStatic && !type.IsModule
                    && !source.Declared.Any(m => m.Kind == CatalogMemberKind.Constructor))
                    DefaultConstructor(type);
            }
            foreach (var type in model.Types.Where(t => t.Source.IsInterface && t.Source.Arity == 0))
                Foreign(type);
            foreach (var list in lists.Values.ToList())
                ListFunctions(list);
            TypeHelpers();
            ForeignContext();
        }

        /// <summary>How deep a class is (its bases): a test for a class must come before its bases'.</summary>
        private static int Depth(CatalogType type)
        {
            int depth = 0;
            for (var b = type.Base; b is not null && depth < 64; b = b.Base)
                depth++;
            return depth;
        }

        /// <summary>
        /// <c>prefix_error_type(error)</c>: the full name of an error's exception type (the exception classes of System.Core
        /// and of the library, tested most derived first); <c>prefix_type_of(handle)</c>: the number of the class C sees
        /// that an object is (most derived first), or -1. Both tell apart with `is`: no reflection needed.
        /// </summary>
        private void TypeHelpers()
        {
            string p = api.Prefix;
            var exceptionType = Core("System.Exception");
            bool IsException(CatalogType type)
            {
                for (CatalogType? b = type; b is not null; b = b.Base)
                    if (b == exceptionType)
                        return true;
                return false;
            }
            var exceptions = catalog.AllTypes.Where(type => !type.IsHidden && type.IsClass && type.Arity == 0 && type.Access == Access.Public && IsException(type))
                .OrderByDescending(Depth).ThenBy(type => type.FullName, StringComparer.Ordinal).ToList();
            api.ErrorTypes.AddRange(exceptions.Select(type => type.FullName));
            // error_type(error)
            {
                var shape = new Shape();
                int handle = shape.Add("error", U64, api.ErrorType, "error");
                var thrown = shape.Local("$thrown", exception, new ConvertExpr(Call(handleGet, any, new ParamExpr(handle, U64)), ConversionKind.Downcast, exception));
                var statements = new List<Stmt>(shape.Work);
                foreach (var type in exceptions.Where(type => type != exceptionType))
                    statements.Add(new IfStmt(new IsExpr(thrown, new TypePattern(Named(type)), HirType.Bool),
                        new ReturnStmt(new CallExpr(null, toUtf8, [], [new LiteralExpr(new StringConstant(type.FullName), text)], textPointer)), null));
                statements.Add(new ReturnStmt(new CallExpr(null, toUtf8, [], [new LiteralExpr(new StringConstant("System.Exception"), text)], textPointer)));
                int work = holder.AddMethod(new MethodDef($"${p}_error_type", textPointer, [.. shape.Parameters])
                {
                    Flags = MethodFlags.Static | MethodFlags.Unsafe,
                    Body = new HirBody([.. shape.Locals], new BlockStmt([.. statements])),
                });
                Export($"{p}_error_type", shape.Parameters, work, textPointer);
                api.Exports.Add(new CExport($"{p}_error_type", "char *", shape.C,
                    $"The full name of an error's exception type (`System.FormatError`; free it with {p}_string_free).", "") { Role = ExportRole.ErrorType });
            }
            // type_of(handle)
            {
                var classes = model.Types.Where(type => type.Source.IsClass && !type.Source.IsStatic && !type.IsModule && type.Source.Arity == 0
                    && api.Handles.Any(h => h.Name == type.Names[TargetLanguage.C])).OrderByDescending(type => Depth(type.Source)).ToList();
                api.Classes.AddRange(classes);
                var shape = new Shape();
                int handle = shape.Add("handle", U64, "uint64_t", "handle");
                var held = shape.Local("$object", any, Call(handleGet, any, new ParamExpr(handle, U64)));
                var statements = new List<Stmt>(shape.Work);
                for (int i = 0; i < classes.Count; i++)
                    statements.Add(new IfStmt(new IsExpr(held, new TypePattern(Local(classes[i].Source.SelfType)), HirType.Bool),
                        new ReturnStmt(new LiteralExpr(new IntegerConstant(i, Primitive.I32), I32)), null));
                statements.Add(new ReturnStmt(new LiteralExpr(new IntegerConstant(-1, Primitive.I32), I32)));
                int work = holder.AddMethod(new MethodDef($"${p}_type_of", I32, [.. shape.Parameters])
                {
                    Flags = MethodFlags.Static,
                    Body = new HirBody([.. shape.Locals], new BlockStmt([.. statements])),
                });
                Export($"{p}_type_of", shape.Parameters, work, I32);
                api.Exports.Add(new CExport($"{p}_type_of", "int32_t", shape.C,
                    "What class an object is, as a number of the library's (-1: none C sees). The language faces use it.", "") { Role = ExportRole.TypeOf });
            }
        }

        private void Skip(string what, string why) => model.Diagnostics.Add(new("PG0202", false, $"`{what}`: {why}; it is left out of the C library"));

        private void Enum(PolyglotType type)
        {
            if (type.Source.Definition.Underlying is not PrimitiveType u || CNumber(u.Kind) is not { } underlying)
            {
                Skip(type.SourceName, "its values are not a C integer type");
                return;
            }
            var members = type.Members.Where(m => m.Kind == NameKind.EnumMember && m.Source.Field?.Value is IntegerConstant or EnumConstant)
                .Select(m => (m.Names[TargetLanguage.C][0], (m.Source.Field!.Value switch
                {
                    IntegerConstant i => i.Value,
                    EnumConstant e => e.Value,
                    _ => 0,
                }).ToString())).ToList();
            api.Enums.Add(new CEnum(type.Names[TargetLanguage.C], underlying, members, type.Source.Doc));
        }

        private void Member(PolyglotType type, PolyglotMember member)
        {
            var names = member.Names[TargetLanguage.C];
            if (names.Length == 0)
                return;
            var source = member.Source;
            switch (member.Kind)
            {
                case NameKind.Constant:
                    if (source.Field?.Value is IntegerConstant integer)
                        api.Constants.Add(new CConstant(names[0], integer.Value.ToString(), source.Doc));
                    else if (source.Field?.Value is BoolConstant flag)
                        api.Constants.Add(new CConstant(names[0], flag.Value ? "1" : "0", source.Doc));
                    else
                        Skip(member.Display, "only integer and bool constants are defined in C");
                    return;
                case NameKind.Constructor when type.Source.IsAbstract || type.Source.IsInterface:
                    return;
                case NameKind.Constructor or NameKind.Method:
                    if (member.Source.Generics.Count > 0)
                    {
                        Skip(member.Display, "generic methods are not offered to C yet");
                        return;
                    }
                    Function(member, names[0]);
                    return;
                case NameKind.Field when type.Source.IsValue && !source.IsStatic:
                    return; // a field of the struct itself
                case NameKind.Property or NameKind.Field:
                    Accessors(member, names);
                    return;
                default:
                    Skip(member.Display, $"{member.Kind.ToString().ToLowerInvariant()}s are not offered to C yet");
                    return;
            }
        }

        /// <summary>One export and the function that does its work: their parameters and C's, the work's locals and statements.</summary>
        private sealed class Shape
        {
            public readonly List<Parameter> Parameters = [];
            public readonly List<CParameter> C = [];
            public readonly List<HirLocal> Locals = [];
            public readonly List<Stmt> Work = [];
            public readonly List<(string Name, Cross Cross)> Arguments = [];
            public Cross? Returns;

            public int Add(string name, HirType type, string c, string cName)
            {
                Parameters.Add(new Parameter(name, type));
                C.Add(new CParameter(c, cName));
                return Parameters.Count - 1;
            }

            public LocalExpr Local(string name, HirType type, Expr? value = null)
            {
                Locals.Add(new HirLocal(name, type));
                if (value is not null)
                    Work.Add(new LocalStmt(Locals.Count - 1, value));
                return new LocalExpr(Locals.Count - 1, type);
            }

            /// <summary>A C name not taken by a parameter yet (<c>result</c>, else <c>result_</c>).</summary>
            public string Free(string name) => C.Any(p => p.Name == name) ? Free(name + "_") : name;
        }

        /// <summary>
        /// The argument the library takes for a parameter of <paramref name="type"/> (global), from the export's parameters
        /// (added to <paramref name="shape"/>, with what converts them), or null when it does not cross.
        /// </summary>
        private Expr? Argument(Shape shape, string name, string cName, HirType type, string owner)
        {
            if (Crossing(type) is not { } crossing || crossing.Kind == CrossKind.Void)
                return null;
            return Argument(shape, name, cName, crossing, owner);
        }

        private Expr Argument(Shape shape, string name, string cName, Cross crossing, string owner)
        {
            shape.Arguments.Add((name, crossing));
            switch (crossing.Kind)
            {
                case CrossKind.Number:
                    return new ParamExpr(shape.Add(name, crossing.Abi!, crossing.C, cName), crossing.Abi!);
                case CrossKind.Pointer:
                {
                    Expr pointer = new ParamExpr(shape.Add(name, crossing.Abi!, crossing.C, cName), crossing.Abi!);
                    return crossing.Abi!.Equals(crossing.Library) ? pointer : new ConvertExpr(pointer, ConversionKind.Pointer, crossing.Library);
                }
                case CrossKind.Enum:
                    return new ConvertExpr(new ParamExpr(shape.Add(name, crossing.Abi!, crossing.C, cName), crossing.Abi!), ConversionKind.Enum, crossing.Library);
                case CrossKind.Text:
                {
                    int pointer = shape.Add(name, textPointer, crossing.C, cName);
                    int length = shape.Add(name + "Length", U64, "size_t", cName.TrimEnd('_') + "_length");
                    var bare = crossing.Nullable ? ((NullableType)crossing.Library).Inner : crossing.Library;
                    Expr made = new CallExpr(null, fromUtf8, [], [new ParamExpr(pointer, textPointer), Number(new ParamExpr(length, U64), I32)], bare);
                    if (!crossing.Nullable)
                        return made;
                    // NULL is null.
                    return new ConditionalExpr(NotNull(new ParamExpr(pointer, textPointer)), new ConvertExpr(made, ConversionKind.Wrap, crossing.Library),
                        new LiteralExpr(new NullConstant(), crossing.Library), crossing.Library);
                }
                case CrossKind.Optional:
                {
                    // A pointer to the value, or NULL for null.
                    var element = crossing.Element!;
                    var pointerType = new PointerType(element.Library, Mutable: false);
                    var pointer = new ParamExpr(shape.Add(name, pointerType, $"const {element.C} *", cName), pointerType);
                    return new ConditionalExpr(NotNull(pointer), new ConvertExpr(new DerefExpr(pointer, element.Library), ConversionKind.Wrap, crossing.Library),
                        new LiteralExpr(new NullConstant(), crossing.Library), crossing.Library);
                }
                case CrossKind.Function:
                {
                    // A callback: the C function, its context, and what gives the context back (called when the library
                    // no longer holds the function).
                    var callback = callbacks[crossing.C];
                    var fn = new ParamExpr(shape.Add(name, callback.Native, crossing.C, cName), callback.Native);
                    var context = new ParamExpr(shape.Add(name + "Context", voidPointer, "void *", cName.TrimEnd('_') + "_context"), voidPointer);
                    var release = new ParamExpr(shape.Add(name + "Release", releaseFunction, api.ReleaseType, cName.TrimEnd('_') + "_release"), releaseFunction);
                    var given = new BinaryExpr(BinaryOp.NotEqual, new ConvertExpr(fn, ConversionKind.Pointer, U64), Zero(U64), HirType.Bool);
                    var self = new NamedType(TypeToken.Def(callback.Type), []);
                    var made = new NewExpr(null, [], [
                        new MemberInit(MemberToken.Def(callback.Type, callback.Fn), fn),
                        new MemberInit(MemberToken.Def(callback.Type, callback.Context), context),
                        new MemberInit(MemberToken.Def(callback.Type, callback.Release), release)], self);
                    var function = (FuncType)(crossing.Nullable ? ((NullableType)crossing.Library).Inner : crossing.Library);
                    // The function value: a lambda that calls the callback object's Invoke (the object holds the C function).
                    var held = shape.Local("$" + name + "Callback", self, made);
                    var lambdaParameters = function.Parameters.Select((p, i) => shape.Local($"${name}{i}", p)).ToList();
                    var call = new CallExpr(held, MemberToken.Def(callback.Type, callback.Invoke), [], [.. lambdaParameters], function.Result);
                    Stmt lambdaBody = function.Result is PrimitiveType { Kind: Primitive.Void }
                        ? new BlockStmt([new ExprStmt(call), new ReturnStmt(null)])
                        : new BlockStmt([new ReturnStmt(call)]);
                    Expr value = new LambdaExpr([.. lambdaParameters.Select(l => l.Local)], lambdaBody, function);
                    if (crossing.Nullable)
                        return new ConditionalExpr(given, new ConvertExpr(value, ConversionKind.Wrap, crossing.Library), new LiteralExpr(new NullConstant(), crossing.Library), crossing.Library);
                    shape.Work.Add(new IfStmt(new UnaryExpr(UnaryOp.Not, given, HirType.Bool), Fails($"`{cName}` of {owner} is null"), null));
                    return value;
                }
                case CrossKind.Value:
                {
                    // A struct by pointer: copied into the work's local (C keeps its own).
                    var pointer = new ParamExpr(shape.Add(name, crossing.Abi!, $"const {crossing.C} *", cName), crossing.Abi!);
                    shape.Work.Add(new IfStmt(new UnaryExpr(UnaryOp.Not, NotNull(pointer), HirType.Bool), Fails($"`{cName}` of {owner} is null"), null));
                    return shape.Local("$" + name, crossing.Library, new DerefExpr(pointer, crossing.Library));
                }
                case CrossKind.Array:
                {
                    // A pointer and a count: the elements copied into a new array.
                    var element = crossing.Element!;
                    var elementType = element.Library;
                    var pointerType = new PointerType(elementType, Mutable: false);
                    var pointer = new ParamExpr(shape.Add(name, pointerType, $"const {element.C} *", cName), pointerType);
                    var count = new ParamExpr(shape.Add(name + "Count", U64, "size_t", cName.TrimEnd('_') + "_count"), U64);
                    var any = new BinaryExpr(BinaryOp.Greater, count, Zero(U64), HirType.Bool);
                    shape.Work.Add(new IfStmt(new BinaryExpr(BinaryOp.And, any, new UnaryExpr(UnaryOp.Not, NotNull(pointer), HirType.Bool), HirType.Bool),
                        Fails($"`{cName}` of {owner} is null"), null));
                    var array = shape.Local("$" + name, crossing.Library, new NewArrayExpr(elementType, [Number(count, I32)], crossing.Library));
                    var bytes = new BinaryExpr(BinaryOp.Multiply, Number(count, I64), new SizeOfExpr(elementType, I64), I64);
                    shape.Work.Add(new IfStmt(any, CopyBytes(First(array, elementType), pointer, bytes), null));
                    return array;
                }
                default:
                {
                    // A handle (an object, or a list).
                    int handle = shape.Add(name, U64, crossing.C, cName);
                    var target = crossing.Nullable ? ((NullableType)crossing.Library).Inner : crossing.Library;
                    var isNull = new BinaryExpr(BinaryOp.Equal, new ParamExpr(handle, U64), Zero(U64), HirType.Bool);
                    Expr get = new ConvertExpr(Call(handleGet, any, new ParamExpr(handle, U64)), ConversionKind.Downcast, target);
                    if (crossing.Nullable)
                        return new ConditionalExpr(isNull, new LiteralExpr(new NullConstant(), crossing.Library), new ConvertExpr(get, ConversionKind.Wrap, crossing.Library), crossing.Library);
                    shape.Work.Add(new IfStmt(isNull, Fails($"`{cName}` of {owner} is null"), null));
                    return get;
                }
            }
        }

        /// <summary>
        /// What the work does with the library's <paramref name="value"/> of <paramref name="type"/> (global): statements
        /// added to the shape, then the value the export gives (null: none), its type in the export and its C type. Null
        /// when it does not cross.
        /// </summary>
        private (Expr? Value, HirType Abi, string C)? Result(Shape shape, Expr value, HirType type)
        {
            if (Crossing(type) is not { } crossing)
                return null;
            return Result(shape, value, crossing);
        }

        private (Expr? Value, HirType Abi, string C) Result(Shape shape, Expr value, Cross crossing)
        {
            if (crossing.Kind != CrossKind.Void)
                shape.Returns = crossing;
            switch (crossing.Kind)
            {
                case CrossKind.Void:
                    shape.Work.Add(new ExprStmt(value));
                    return (null, HirType.Void, "void");
                case CrossKind.Number:
                    return (value, crossing.Abi!, crossing.C);
                case CrossKind.Pointer:
                    return (crossing.Abi!.Equals(crossing.Library) ? value : new ConvertExpr(value, ConversionKind.Pointer, crossing.Abi!), crossing.Abi!, crossing.C);
                case CrossKind.Enum:
                    return (new ConvertExpr(value, ConversionKind.Enum, crossing.Abi!), crossing.Abi!, crossing.C);
                case CrossKind.Text when !crossing.Nullable:
                    return (new CallExpr(null, toUtf8, [], [value], textPointer), textPointer, "char *");
                case CrossKind.Text:
                {
                    // null is NULL.
                    var held = shape.Local("$text", crossing.Library, value);
                    var bare = ((NullableType)crossing.Library).Inner;
                    var isNull = new BinaryExpr(BinaryOp.Equal, held, new LiteralExpr(new NullConstant(), crossing.Library), HirType.Bool);
                    var copy = new CallExpr(null, toUtf8, [], [new ConvertExpr(held, ConversionKind.Unwrap, bare)], textPointer);
                    return (new ConditionalExpr(isNull, new ConvertExpr(Zero(U64), ConversionKind.Pointer, textPointer), copy, textPointer), textPointer, "char *");
                }
                case CrossKind.Optional:
                {
                    // Whether there is a value (the result), and the value through a pointer C gives.
                    var element = crossing.Element!;
                    var into = new ParamExpr(shape.Add("result", new PointerType(element.Library), element.C + " *", shape.Free("result")), new PointerType(element.Library));
                    var held = shape.Local("$optional", crossing.Library, value);
                    var has = shape.Local("$has", HirType.Bool,
                        new BinaryExpr(BinaryOp.NotEqual, held, new LiteralExpr(new NullConstant(), crossing.Library), HirType.Bool));
                    var write = new ExprStmt(new AssignExpr(new DerefExpr(into, element.Library), new ConvertExpr(held, ConversionKind.Unwrap, element.Library), element.Library));
                    shape.Work.Add(new IfStmt(new BinaryExpr(BinaryOp.And, has, NotNull(into), HirType.Bool), write, null));
                    return (has, HirType.Bool, "bool");
                }
                case CrossKind.Value:
                {
                    // Through a pointer C gives (written when it is not null).
                    var into = new ParamExpr(shape.Add("result", crossing.Abi!, crossing.C + " *", shape.Free("result")), crossing.Abi!);
                    var made = shape.Local("$result", crossing.Library, value);
                    shape.Work.Add(new IfStmt(NotNull(into), new ExprStmt(new AssignExpr(new DerefExpr(into, crossing.Library), made, crossing.Library)), null));
                    return (null, HirType.Void, "void");
                }
                case CrossKind.Array:
                {
                    // A block of native memory with a copy of the elements (the caller frees it), and their count.
                    var element = crossing.Element!;
                    var elementType = element.Library;
                    var count = new ParamExpr(shape.Add("resultCount", u64Pointer, "size_t *", shape.Free("result_count")), u64Pointer);
                    var array = shape.Local("$array", crossing.Library, value);
                    var length = shape.Local("$length", I32, new ArrayLengthExpr(array, I32));
                    shape.Work.Add(new IfStmt(NotNull(count), new ExprStmt(new AssignExpr(new DerefExpr(count, U64), Number(length, U64), U64)), null));
                    var bytes = shape.Local("$bytes", I64, new BinaryExpr(BinaryOp.Multiply, Number(length, I64), new SizeOfExpr(elementType, I64), I64));
                    var block = shape.Local("$block", voidPointer, new CallExpr(null, allocNative, [], [bytes], voidPointer));
                    shape.Work.Add(new IfStmt(new BinaryExpr(BinaryOp.Greater, length, new LiteralExpr(new IntegerConstant(0, Primitive.I32), I32), HirType.Bool),
                        CopyBytes(block, First(array, elementType), bytes), null));
                    var pointer = new PointerType(elementType);
                    return (new ConvertExpr(block, ConversionKind.Pointer, pointer), pointer, element.C + " *");
                }
                default:
                {
                    var library = crossing.Library;
                    if (!crossing.Nullable)
                        return (Call(handleNew, U64, new ConvertExpr(value, ConversionKind.Upcast, any)), U64, crossing.C);
                    // A null object is the handle 0.
                    var held = shape.Local("$value", library, value);
                    var inner = ((NullableType)library).Inner;
                    var made = Call(handleNew, U64, new ConvertExpr(new ConvertExpr(held, ConversionKind.Unwrap, inner), ConversionKind.Upcast, any));
                    var isNull = new BinaryExpr(BinaryOp.Equal, held, new LiteralExpr(new NullConstant(), library), HirType.Bool);
                    return (new ConditionalExpr(isNull, Zero(U64), made, U64), U64, crossing.C);
                }
            }
        }

        /// <summary>A constructor or a method as one export.</summary>
        private void Function(PolyglotMember member, string name)
        {
            var source = member.Source;
            var shape = new Shape();
            string owner = member.Display;
            Expr? self = null;
            if (member.Kind == NameKind.Method && !source.IsStatic)
                self = Argument(shape, "self", "self", source.Type.SelfType, owner);
            var arguments = new List<Expr>();
            var cNames = member.Parameters[TargetLanguage.C];
            for (int i = 0; i < source.Parameters.Count; i++)
            {
                if (Argument(shape, source.Parameters[i].Name, cNames[i], source.Parameters[i].Type, owner) is not { } argument)
                {
                    Skip(member.Display, $"its parameter `{source.Parameters[i].Name}` is a {member.TypeText(source.Parameters[i].Type)}, which does not cross to C yet");
                    return;
                }
                arguments.Add(argument);
            }
            Expr call;
            HirType resultType;
            if (member.Kind == NameKind.Constructor)
            {
                resultType = source.Type.SelfType;
                call = new NewExpr(Ref(source), [.. arguments], [], Local(resultType));
            }
            else
            {
                resultType = source.ValueType;
                call = new CallExpr(self, Ref(source), [], [.. arguments], Local(resultType));
            }
            if (Result(shape, call, resultType) is not { } result)
            {
                Skip(member.Display, $"its result is a {member.TypeText(resultType)}, which does not cross to C yet");
                return;
            }
            Emit(name, shape, result, source.Doc, member.Display, member.Kind == NameKind.Constructor ? ExportRole.Constructor : ExportRole.Method, member);
        }

        /// <summary>
        /// An interface another language implements: a class of the shims (<c>$Polyglot.Foreign&lt;n&gt;</c>) that is one, a
        /// function of the other language for each method (and its context and release), and the export that makes one.
        /// Only interfaces whose members (theirs and their bases') are methods that call out as callbacks do.
        /// </summary>
        private void Foreign(PolyglotType iface)
        {
            if (!api.Handles.Any(h => h.Name == iface.Names[TargetLanguage.C]))
                return;
            // The interface and the interfaces it extends, each once.
            var all = new List<CatalogType>();
            void Add(CatalogType i)
            {
                if (all.Contains(i))
                    return;
                all.Add(i);
                foreach (var b in i.Interfaces)
                    Add(b);
            }
            Add(iface.Source);
            var members = new List<(PolyglotMember Member, List<Cross> Arguments, Cross Result)>();
            foreach (var i in all)
            {
                if (model.Types.FirstOrDefault(m => m.Source == i) is not { } exposedInterface)
                    return;
                foreach (var member in exposedInterface.Members)
                {
                    if (member.Source.IsStatic)
                        continue;
                    if (member.Kind != NameKind.Method || member.Source.Generics.Count > 0)
                        return; // properties, events, generic methods: not implemented from outside yet
                    var arguments = member.Source.Parameters.Select(p => Crossing(p.Type)).ToList();
                    var result = Crossing(member.Source.ValueType);
                    if (!CallsOut(arguments, result) || member.Names[TargetLanguage.C].Length == 0)
                        return;
                    members.Add((member, arguments.Select(a => a!).ToList(), result!));
                }
            }
            int index = api.Foreigns.Count;
            var def = new TypeDef
            {
                Namespace = "$Polyglot", Name = "Foreign" + index, Kind = TypeKind.Class, Flags = TypeFlags.Final | TypeFlags.Unsafe, Base = any,
            };
            def.Interfaces.Add(Local(iface.Source.SelfType));
            int type = shims.Types.Count;
            shims.Add(def);
            var self = new NamedType(TypeToken.Def(type), []);
            Expr Field(int at, HirType fieldType) => new FieldExpr(new ThisExpr(self), MemberToken.Def(type, at), fieldType);
            var functions = new List<(PolyglotMember, CCallback)>();
            var fields = new List<(int Field, NativeFuncType Native, CCallback Fn)>();
            foreach (var (member, arguments, result) in members)
            {
                var native = OutFunction(arguments, result);
                var fn = new CCallback(member.Names[TargetLanguage.C][0] + "_fn_t", -1, arguments, result);
                functions.Add((member, fn));
                fields.Add((def.AddField(new FieldDef("fn" + fields.Count, native)), native, fn));
            }
            int context = def.AddField(new FieldDef("context", voidPointer));
            int release = def.AddField(new FieldDef("release", releaseFunction));
            for (int m = 0; m < members.Count; m++)
            {
                var (member, arguments, result) = members[m];
                var parameters = member.Source.Parameters.Select(p => new Parameter(p.Name, Local(p.Type))).ToArray();
                def.AddMethod(new MethodDef(member.Source.Method!.Name, Local(member.Source.ValueType), parameters)
                {
                    Flags = MethodFlags.Unsafe | MethodFlags.Virtual,
                    Body = OutCall(Field(fields[m].Field, fields[m].Native), Field(context, voidPointer), arguments, [.. parameters.Select(p => p.Type)], result),
                });
            }
            def.AddMethod(Destroy(Field(release, releaseFunction), Field(context, voidPointer)));
            foreignTypes.Add((type, context));

            // prefix_iface_new_foreign(fn..., context, release, error): one of these objects, as the interface.
            var shape = new Shape();
            var inits = new List<MemberInit>();
            for (int m = 0; m < members.Count; m++)
            {
                var member = members[m].Member;
                int at = shape.Add(member.Source.Method!.Name, fields[m].Native, fields[m].Fn.Name, c.Parameter(member.Source.Method!.Name));
                inits.Add(new MemberInit(MemberToken.Def(type, fields[m].Field), new ParamExpr(at, fields[m].Native)));
            }
            int contextParameter = shape.Add("context", voidPointer, "void *", "context");
            int releaseParameter = shape.Add("release", releaseFunction, api.ReleaseType, "release");
            inits.Add(new MemberInit(MemberToken.Def(type, context), new ParamExpr(contextParameter, voidPointer)));
            inits.Add(new MemberInit(MemberToken.Def(type, release), new ParamExpr(releaseParameter, releaseFunction)));
            string name = c.Prefix(iface) + "_new_foreign";
            var made = new NewExpr(null, [], [.. inits], self);
            Emit(name, shape, Result(shape, made, iface.Source.SelfType)!.Value,
                $"A {iface.Source.Name} made of functions of another language: one for each method (with `context` first), and the release " +
                "the library calls with the context when it lets the object go.", iface.SourceName + " implemented outside", ExportRole.ForeignNew, owner: iface);
            api.Foreigns.Add(new CForeign(iface, name, index, functions));
        }

        /// <summary>
        /// <c>prefix_foreign_context(handle)</c>: the context of an object another language made (new_foreign), or NULL: what
        /// gives that language back its own object (the same, not a proxy) when the library hands it back.
        /// </summary>
        private void ForeignContext()
        {
            string p = api.Prefix;
            var shape = new Shape();
            int handle = shape.Add("handle", U64, "uint64_t", "handle");
            var held = shape.Local("$object", any, Call(handleGet, any, new ParamExpr(handle, U64)));
            var statements = new List<Stmt>(shape.Work);
            foreach (var (type, context) in foreignTypes)
            {
                var self = new NamedType(TypeToken.Def(type), []);
                statements.Add(new IfStmt(new IsExpr(held, new TypePattern(self), HirType.Bool),
                    new ReturnStmt(new FieldExpr(new ConvertExpr(held, ConversionKind.Downcast, self), MemberToken.Def(type, context), voidPointer)), null));
            }
            statements.Add(new ReturnStmt(new ConvertExpr(Zero(U64), ConversionKind.Pointer, voidPointer)));
            int work = holder.AddMethod(new MethodDef($"${p}_foreign_context", voidPointer, [.. shape.Parameters])
            {
                Flags = MethodFlags.Static | MethodFlags.Unsafe,
                Body = new HirBody([.. shape.Locals], new BlockStmt([.. statements])),
            });
            Export($"{p}_foreign_context", shape.Parameters, work, voidPointer);
            api.Exports.Add(new CExport($"{p}_foreign_context", "void *", shape.C,
                "The context an object was made with by another language (an interface's new_foreign), or NULL for the library's own objects.", "")
                { Role = ExportRole.ForeignContext });
        }

        /// <summary>A class that declares no constructor: one that makes it (its fields at their first values), <c>prefix_type_new</c>.</summary>
        private void DefaultConstructor(PolyglotType type)
        {
            var shape = new Shape();
            var self = type.Source.SelfType;
            string name = c.Prefix(type) + "_new";
            // The constructor a frontend made (field initializers), when there is one; else none.
            var made = type.Source.Definition.Methods.FirstOrDefault(m => m.Kind == MethodKind.Constructor && m.Parameters.Count == 0);
            MemberToken? constructor = made is null ? null : shims.Reference(new MemberRef(MemberKind.Method,
                shims.Reference(type.Source.Assembly.Name, type.Source.Namespace, type.Source.Name, 0), made.Name, 0, [], HirType.Void));
            Emit(name, shape, Result(shape, new NewExpr(constructor, [], [], Local(self)), self)!.Value, $"A new {type.Source.Name}.", type.SourceName + ".new()",
                ExportRole.Constructor, owner: type);
        }

        /// <summary>A property or field: a getter export, and a setter export when it can be written.</summary>
        private void Accessors(PolyglotMember member, string[] names)
        {
            var source = member.Source;
            bool instance = !source.IsStatic;
            Expr Access(Expr? self) => member.Kind == NameKind.Field
                ? new FieldExpr(self, Ref(source), Local(source.ValueType))
                : new PropertyExpr(self, Ref(source), [], Local(source.ValueType));
            if (Crossing(source.ValueType) is null)
            {
                Skip(member.Display, $"its type is a {member.TypeText(source.ValueType)}, which does not cross to C yet");
                return;
            }
            if (source.CanRead)
            {
                var getter = new Shape();
                Expr? self = instance ? Argument(getter, "self", "self", source.Type.SelfType, member.Display) : null;
                Emit(names[0], getter, Result(getter, Access(self), source.ValueType)!.Value, source.Doc, member.Display, ExportRole.Getter, member);
            }
            if (names.Length < 2 || !source.CanWrite || source.Type.IsValue)
                return;
            var setter = new Shape();
            Expr? setSelf = instance ? Argument(setter, "self", "self", source.Type.SelfType, member.Display) : null;
            var value = Argument(setter, "value", "value", source.ValueType, member.Display)!;
            setter.Work.Add(new ExprStmt(new AssignExpr(Access(setSelf), value, Local(source.ValueType))));
            Emit(names[1], setter, (null, HirType.Void, "void"), source.Doc, member.Display, ExportRole.Setter, member);
        }

        /// <summary>A list type's functions: make, count, read, write, add, remove, clear.</summary>
        private void ListFunctions(CList list)
        {
            api.Handles.Add((list.Name, $"A list of {list.Element.C.TrimEnd(' ', '*')} (the library's own, not a copy): give it back with {api.Prefix}_release."));
            string prefix = list.Name[..^2];
            CatalogMember Member(string name, int parameters) =>
                listType.Declared.First(m => m.Name == name && m.Parameters.Count == parameters && m.Kind != CatalogMemberKind.Constructor);
            var constructor = listType.Declared.First(m => m.Kind == CatalogMemberKind.Constructor && m.Parameters.Count == 0);
            var listCross = new Cross(CrossKind.List, list.Library, list.Name, Element: list.Element, Abi: U64) { List = list, Global = list.Global };
            string owner = $"List<{list.Word}>";
            Expr Self(Shape shape) => Argument(shape, "list", "list", listCross, owner);
            Expr Index(Shape shape) => Argument(shape, "index", "index", new Cross(CrossKind.Number, I32, "int32_t", Abi: I32) { Global = I32 }, owner);

            var made = new Shape();
            Emit(prefix + "_new", made, Result(made, new NewExpr(Ref(constructor), [], [], list.Library), listCross), "A new, empty list.", owner + ".new()", ExportRole.ListNew, list: list);

            var count = new Shape();
            var countMember = listType.Declared.First(m => m.Name == "Count");
            Emit(prefix + "_count", count, Result(count, new PropertyExpr(Self(count), Ref(countMember), [], I32), new Cross(CrossKind.Number, I32, "int32_t", Abi: I32) { Global = I32 }),
                "How many items the list has.", owner + ".Count", ExportRole.ListCount, list: list);

            var get = new Shape();
            var getSelf = Self(get);
            var at = new CallExpr(getSelf, Ref(Member("At", 1)), [list.Element.Library], [Index(get)], list.Element.Library);
            Emit(prefix + "_get", get, Result(get, at, list.Element), "The item at `index` (an error past the end).", owner + ".At(i32)", ExportRole.ListGet, list: list);

            var set = new Shape();
            var setSelf = Self(set);
            var setIndex = Index(set);
            var setValue = Argument(set, "value", "value", list.Element, owner);
            set.Work.Add(new ExprStmt(new CallExpr(setSelf, Ref(Member("SetAt", 2)), [list.Element.Library], [setIndex, setValue], HirType.Void)));
            Emit(prefix + "_set", set, (null, HirType.Void, "void"), "Replaces the item at `index`.", owner + ".SetAt(i32, T)", ExportRole.ListSet, list: list);

            var add = new Shape();
            var addSelf = Self(add);
            var addValue = Argument(add, "value", "value", list.Element, owner);
            add.Work.Add(new ExprStmt(new CallExpr(addSelf, Ref(Member("Add", 1)), [list.Element.Library], [addValue], HirType.Void)));
            Emit(prefix + "_add", add, (null, HirType.Void, "void"), "Adds an item at the end.", owner + ".Add(T)", ExportRole.ListAdd, list: list);

            var remove = new Shape();
            var removeSelf = Self(remove);
            remove.Work.Add(new ExprStmt(new CallExpr(removeSelf, Ref(Member("RemoveAt", 1)), [list.Element.Library], [Index(remove)], list.Element.Library)));
            Emit(prefix + "_remove_at", remove, (null, HirType.Void, "void"), "Takes out the item at `index`.", owner + ".RemoveAt(i32)", ExportRole.ListRemoveAt, list: list);

            var clear = new Shape();
            var clearSelf = Self(clear);
            clear.Work.Add(new ExprStmt(new CallExpr(clearSelf, Ref(Member("Clear", 0)), [list.Element.Library], [], HirType.Void)));
            Emit(prefix + "_clear", clear, (null, HirType.Void, "void"), "Takes out every item.", owner + ".Clear()", ExportRole.ListClear, list: list);
        }

        /// <summary>
        /// The function that does the work (conversions, the call, the error) and the export around it, named
        /// <paramref name="name"/>, with the error parameter last.
        /// </summary>
        private void Emit(string name, Shape shape, (Expr? Value, HirType Abi, string C) result, string? doc, string source,
            ExportRole role, PolyglotMember? member = null, CList? list = null, PolyglotType? owner = null)
        {
            int error = shape.Add("error", u64Pointer, api.ErrorType + " *", "error");
            var abi = result.Value is null ? HirType.Void : result.Abi;

            // The work, inside a try: an exception goes to *error (when C gave somewhere to put it).
            var locals = new List<HirLocal>(shape.Locals) { new("$e", exception) };
            int caught = locals.Count - 1;
            var tryBody = new List<Stmt>(shape.Work);
            if (result.Value is not null)
                tryBody.Add(new ReturnStmt(result.Value));
            var errorParam = new ParamExpr(error, u64Pointer);
            var store = new ExprStmt(new AssignExpr(new DerefExpr(errorParam, U64), Call(handleNew, U64, new ConvertExpr(new LocalExpr(caught, exception), ConversionKind.Upcast, any)), U64));
            var statements = new List<Stmt>
            {
                new TryStmt(new BlockStmt([.. tryBody]), [new CatchClause(exception, caught, null, new BlockStmt([new IfStmt(NotNull(errorParam), store, null)]))], null),
                result.Value is null ? new ReturnStmt(null) : new ReturnStmt(new DefaultExpr(abi)),
            };
            int work = holder.AddMethod(new MethodDef("$" + name, abi, [.. shape.Parameters])
            {
                Flags = MethodFlags.Static | MethodFlags.Unsafe,
                Body = new HirBody([.. locals], new BlockStmt([.. statements])),
            });
            Export(name, shape.Parameters, work, abi);
            api.Exports.Add(new CExport(name, result.Value is null && shape.Returns?.Kind is not (CrossKind.Value or CrossKind.Optional) ? "void" : result.C, shape.C, doc, source)
            {
                Role = role,
                Member = member,
                List = list,
                Owner = owner,
                Arguments = [.. shape.Arguments],
                Returns = shape.Returns,
            });
        }

        /// <summary>The export: enter, call the work with the same arguments, leave, give its result.</summary>
        private void Export(string name, IReadOnlyList<Parameter> parameters, int work, HirType result, bool enters = true)
        {
            bool isVoid = result is PrimitiveType { Kind: Primitive.Void };
            var locals = isVoid ? Array.Empty<HirLocal>() : [new HirLocal("$r", result)];
            var arguments = parameters.Select((p, i) => (Expr)new ParamExpr(i, p.Type)).ToArray();
            var call = new CallExpr(null, MemberToken.Def(holderIndex, work), [], arguments, result);
            var statements = new List<Stmt>();
            if (enters)
                statements.Add(new ExprStmt(Call(enter, HirType.Void)));
            statements.Add(isVoid ? new ExprStmt(call) : new LocalStmt(0, call));
            if (enters)
                statements.Add(new ExprStmt(Call(leave, HirType.Void)));
            statements.Add(new ReturnStmt(isVoid ? null : new LocalExpr(0, result)));
            holder.AddMethod(new MethodDef(name, result, [.. parameters])
            {
                Flags = MethodFlags.Static | MethodFlags.Export | MethodFlags.Unsafe,
                ExternName = name,
                Body = new HirBody(locals, new BlockStmt([.. statements])),
            });
        }

        /// <summary>The library's own functions: a handle given back, a block or a string freed, an error's message, a thread detached.</summary>
        private void Helpers()
        {
            string p = api.Prefix;
            // release(handle)
            {
                var parameters = new[] { new Parameter("handle", U64) };
                int work = holder.AddMethod(new MethodDef($"${p}_release", HirType.Void, parameters)
                {
                    Flags = MethodFlags.Static,
                    Body = new HirBody([], new BlockStmt([new ExprStmt(Call(handleFree, HirType.Void, new ParamExpr(0, U64))), new ReturnStmt(null)])),
                });
                Export($"{p}_release", parameters, work, HirType.Void);
                api.Exports.Add(new CExport($"{p}_release", "void", [new CParameter("uint64_t", "handle")],
                    "Gives back a handle (of any type, an error's and a list's too): its object is no longer held for C. 0 does nothing.", "") { Role = ExportRole.Release });
            }
            // free(block), string_free(text): native memory the library gave.
            foreach (var (helper, type, cType, doc) in new[]
            {
                ("free", voidPointer, "void *", "Frees a block the library gave (an array result)."),
                ("string_free", textPointer, "char *", "Frees text the library gave (a `char *` result): UTF-8, null-terminated."),
            })
            {
                var parameters = new[] { new Parameter("block", type) };
                int work = holder.AddMethod(new MethodDef($"${p}_{helper}", HirType.Void, parameters)
                {
                    Flags = MethodFlags.Static | MethodFlags.Unsafe,
                    Body = new HirBody([], new BlockStmt([
                        new ExprStmt(new CallExpr(null, freeNative, [], [new ConvertExpr(new ParamExpr(0, type), ConversionKind.Pointer, voidPointer)], HirType.Void)),
                        new ReturnStmt(null)])),
                });
                Export($"{p}_{helper}", parameters, work, HirType.Void, enters: false);
                api.Exports.Add(new CExport($"{p}_{helper}", "void", [new CParameter(cType, helper == "free" ? "block" : "text")], doc, "")
                {
                    Role = helper == "free" ? ExportRole.Free : ExportRole.StringFree,
                });
            }
            // error_message(error)
            {
                var shape = new Shape();
                int handle = shape.Add("error", U64, api.ErrorType, "error");
                var thrown = new ConvertExpr(Call(handleGet, any, new ParamExpr(handle, U64)), ConversionKind.Downcast, exception);
                var said = new CallExpr(null, toUtf8, [], [new PropertyExpr(thrown, message, [], text)], textPointer);
                int work = holder.AddMethod(new MethodDef($"${p}_error_message", textPointer, [.. shape.Parameters])
                {
                    Flags = MethodFlags.Static | MethodFlags.Unsafe,
                    Body = new HirBody([], new BlockStmt([new ReturnStmt(said)])),
                });
                Export($"{p}_error_message", shape.Parameters, work, textPointer);
                api.Exports.Add(new CExport($"{p}_error_message", "char *", shape.C,
                    $"An error's message (free it with {p}_string_free; the error itself with {p}_release).", "") { Role = ExportRole.ErrorMessage });
            }
            // string_new(text, length): a string of the library, as a handle (what a callback gives for text).
            {
                var shape = new Shape();
                int textAt = shape.Add("text", textPointer, "const char *", "text");
                int length = shape.Add("length", U64, "size_t", "length");
                var made = new CallExpr(null, fromUtf8, [], [new ParamExpr(textAt, textPointer), Number(new ParamExpr(length, U64), I32)], text);
                int work = holder.AddMethod(new MethodDef($"${p}_string_new", U64, [.. shape.Parameters])
                {
                    Flags = MethodFlags.Static | MethodFlags.Unsafe,
                    Body = new HirBody([], new BlockStmt([new ReturnStmt(Call(handleNew, U64, new ConvertExpr(made, ConversionKind.Upcast, any)))])),
                });
                Export($"{p}_string_new", shape.Parameters, work, U64);
                api.Exports.Add(new CExport($"{p}_string_new", "uint64_t", shape.C,
                    "A string of the library (UTF-8 in), as a handle: what a callback that gives text returns (the library takes it).", "")
                    { Role = ExportRole.StringNew });
            }
            // callback_error(error, message, length): what a callback failed with.
            {
                var shape = new Shape();
                int error = shape.Add("error", u64Pointer, api.ErrorType + " *", "error");
                int message = shape.Add("message", textPointer, "const char *", "message");
                int length = shape.Add("length", U64, "size_t", "length");
                var said = new CallExpr(null, fromUtf8, [], [new ParamExpr(message, textPointer), Number(new ParamExpr(length, U64), I32)], text);
                var thrown = new NewExpr(newException, [said], [], exception);
                var errorParam = new ParamExpr(error, u64Pointer);
                int work = holder.AddMethod(new MethodDef($"${p}_callback_error", HirType.Void, [.. shape.Parameters])
                {
                    Flags = MethodFlags.Static | MethodFlags.Unsafe,
                    Body = new HirBody([], new BlockStmt([
                        new IfStmt(NotNull(errorParam), new ExprStmt(new AssignExpr(new DerefExpr(errorParam, U64),
                            Call(handleNew, U64, new ConvertExpr(thrown, ConversionKind.Upcast, any)), U64)), null),
                        new ReturnStmt(null)])),
                });
                Export($"{p}_callback_error", shape.Parameters, work, HirType.Void);
                api.Exports.Add(new CExport($"{p}_callback_error", "void", shape.C,
                    "What a callback failed with: `message` (UTF-8) becomes the exception the library's caller of the callback gets. " +
                    "A callback calls it with the `error` it was given, then returns.", "") { Role = ExportRole.CallbackError });
            }
            // thread_detach()
            {
                holder.AddMethod(new MethodDef($"{p}_thread_detach", HirType.Void)
                {
                    Flags = MethodFlags.Static | MethodFlags.Export,
                    ExternName = $"{p}_thread_detach",
                    Body = new HirBody([], new BlockStmt([new ExprStmt(Call(detach, HirType.Void)), new ReturnStmt(null)])),
                });
                api.Exports.Add(new CExport($"{p}_thread_detach", "void", [],
                    "A thread that called the library and will not again (before it ends): the library forgets it.", "") { Role = ExportRole.ThreadDetach });
            }
        }
    }
}

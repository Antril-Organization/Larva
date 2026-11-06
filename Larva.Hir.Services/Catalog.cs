namespace Larva.Hir.Services
{
    /// <summary>A place in a source file (line and column from 1); what "go to definition" opens.</summary>
    public sealed record SourceSpot(string File, int Line, int Column)
    {
        public override string ToString() => $"{File}({Line},{Column})";
    }

    /// <summary>One assembly in a catalog: its HIR and where its types start in the catalog's list.</summary>
    public sealed class CatalogAssembly
    {
        internal CatalogAssembly(HirCatalog catalog, HirAssembly hir, int index)
        {
            Catalog = catalog;
            Hir = hir;
            Index = index;
        }

        public HirCatalog Catalog { get; }
        public HirAssembly Hir { get; }
        public int Index { get; }
        public string Name => Hir.Name;

        /// <summary>The catalog types of <see cref="Hir"/>'s definitions, in their order.</summary>
        public IReadOnlyList<CatalogType> Types => types;
        internal CatalogType[] types = [];

        internal SourceSpot? Spot(SourceLocation location) =>
            location.File >= 0 && location.File < Hir.Files.Count ? new SourceSpot(Hir.Files[location.File], location.Line, location.Column) : null;

        public override string ToString() => Name;
    }

    /// <summary>
    /// The types of a set of HIR assemblies (a program and the libraries it references, or one linked assembly), by
    /// namespace, with their members: what every language built on Larva shows, searches and completes. Language-neutral:
    /// a <see cref="LanguageView"/> writes what it holds in a language's syntax.
    /// </summary>
    public sealed class HirCatalog
    {
        private readonly List<CatalogAssembly> assemblies = [];
        private readonly List<CatalogType> all = [];
        private readonly Dictionary<(string Assembly, string Namespace, string Name, int Arity), CatalogType> byIdentity = [];
        private readonly Dictionary<(string Namespace, string Name, int Arity), CatalogType> byName = [];
        private readonly Dictionary<string, List<CatalogType>> byNamespace = new(StringComparer.Ordinal);
        private readonly Dictionary<Primitive, CatalogType> primitives = [];
        private string[] namespaces = [];

        /// <summary>A catalog of <paramref name="assemblies"/> (their references to each other resolved by name).</summary>
        public static HirCatalog Create(IEnumerable<HirAssembly> assemblies)
        {
            var catalog = new HirCatalog();
            foreach (var assembly in assemblies)
                catalog.Add(assembly);
            catalog.Seal();
            return catalog;
        }

        public static HirCatalog Create(params HirAssembly[] assemblies) => Create((IEnumerable<HirAssembly>)assemblies);

        private void Add(HirAssembly hir)
        {
            var assembly = new CatalogAssembly(this, hir, assemblies.Count);
            assemblies.Add(assembly);
            assembly.types = new CatalogType[hir.Types.Count];
            for (int t = 0; t < hir.Types.Count; t++)
            {
                var def = hir.Types[t];
                var type = new CatalogType(assembly, t, def) { Id = all.Count };
                assembly.types[t] = type;
                all.Add(type);
                byIdentity.TryAdd((hir.Name, def.Namespace, def.Name, def.Generics.Count), type);
                byName.TryAdd((def.Namespace, def.Name, def.Generics.Count), type);
                if ((def.Flags & TypeFlags.Primitive) != 0 && def.Underlying is PrimitiveType p)
                    primitives.TryAdd(p.Kind, type);
            }
        }

        private void Seal()
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var type in all)
            {
                if (type.IsHidden)
                    continue;
                if (!byNamespace.TryGetValue(type.Namespace, out var list))
                    byNamespace[type.Namespace] = list = [];
                list.Add(type);
                for (string ns = type.Namespace; ns.Length > 0; ns = Parent(ns))
                    names.Add(ns);
            }
            foreach (var list in byNamespace.Values)
                list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name) is var c && c != 0 ? c : a.Arity.CompareTo(b.Arity));
            namespaces = [.. names];
            foreach (var type in all)
                type.Seal();
        }

        private static string Parent(string ns) => ns.LastIndexOf('.') is var dot && dot > 0 ? ns[..dot] : "";

        public IReadOnlyList<CatalogAssembly> Assemblies => assemblies;

        /// <summary>Every type (hidden ones too: closures and other types a frontend made).</summary>
        public IReadOnlyList<CatalogType> AllTypes => all;

        /// <summary>The namespaces that have visible types, and their parents, in order.</summary>
        public IReadOnlyList<string> Namespaces => namespaces;

        public CatalogAssembly? Assembly(string name) => assemblies.Find(a => a.Name == name);

        /// <summary>The visible types of namespace <paramref name="ns"/> (not of the namespaces inside it), by name.</summary>
        public IReadOnlyList<CatalogType> TypesIn(string ns) => byNamespace.TryGetValue(ns, out var list) ? list : [];

        /// <summary>The extension blocks declared in a namespace (hidden from <see cref="TypesIn"/>).</summary>
        public IEnumerable<CatalogType> ExtensionsIn(string ns) => all.Where(t => t.IsExtension && t.Namespace == ns);

        /// <summary>
        /// The extension blocks whose methods a value of <paramref name="type"/> (global) has: those on the type itself (any
        /// arguments), and generic ones (<c>ext&lt;T : bitflag enum&gt;(T flags)</c>) whose constraint it meets.
        /// </summary>
        public IEnumerable<CatalogType> ExtensionsOf(HirType type)
        {
            var target = type is NullableType n ? n.Inner : type;
            var hit = TypeOf(target);
            // What a value of the type also is: its base classes and every interface it has.
            HashSet<CatalogType>? kinds = null;
            foreach (var extension in all)
            {
                if (!extension.IsExtension || extension.Extended is not { } extended)
                    continue;
                if (extended is GenericParamType)
                {
                    var generic = extension.Definition.Generics.Count > 0 ? extension.Definition.Generics[0] : null;
                    var constraint = generic?.Kinds ?? ConstraintKinds.None;
                    bool meets = (constraint & ConstraintKinds.Bitflags) != 0 ? hit?.Definition.Kind == TypeKind.Bitflags
                        : (constraint & ConstraintKinds.Enum) != 0 ? hit is { IsEnum: true }
                        : (constraint & ConstraintKinds.Object) != 0 ? hit is { Kind: CatalogTypeKind.Class } && (constraint & ~ConstraintKinds.Object) == ConstraintKinds.None
                        : constraint == ConstraintKinds.None;
                    // `ext<T : IShape>(T s)`: every type that is an IShape (and each type constraint met).
                    if (meets && generic is { Types.Length: > 0 })
                        meets = hit is { IsInterface: false } && generic.Types.All(c => TypeOf(c) is { } wanted && IsA(hit, wanted, ref kinds));
                    if (meets)
                        yield return extension;
                    continue;
                }
                bool same = extended is NamedType a && target is NamedType b ? a.Type == b.Type : extended == target;
                // `ext(IShape s)` or `ext(Animal a)`: also on the types that implement or derive from it.
                if (same || hit is not null && extended is NamedType && TypeOf(extended) is { } on && on != hit && IsA(hit, on, ref kinds))
                    yield return extension;
            }
        }

        /// <summary>Whether a value of <paramref name="type"/> is a <paramref name="wanted"/>: the type, a base or an interface of it.</summary>
        private static bool IsA(CatalogType type, CatalogType wanted, ref HashSet<CatalogType>? kinds)
        {
            if (type == wanted)
                return true;
            kinds ??= [.. type.BaseChain(), .. type.AllInterfaces()];
            return kinds.Contains(wanted);
        }

        /// <summary>The namespaces directly inside <paramref name="ns"/> ("" for the top level).</summary>
        public IEnumerable<string> NamespacesIn(string ns)
        {
            foreach (var name in namespaces)
                if (Parent(name) == ns)
                    yield return name;
        }

        public bool IsNamespace(string name) => Array.BinarySearch(namespaces, name, StringComparer.Ordinal) >= 0;

        public CatalogType? Find(string assembly, string ns, string name, int arity) =>
            byIdentity.TryGetValue((assembly, ns, name, arity), out var type) ? type : null;

        public CatalogType? Find(string ns, string name, int arity) => byName.TryGetValue((ns, name, arity), out var type) ? type : null;

        /// <summary>A type by full name (<c>System.List</c>, <c>System.List`1</c>, <c>List</c> when one namespace has it).</summary>
        public CatalogType? Find(string fullName)
        {
            int arity = -1;
            string name = fullName;
            if (name.IndexOf('`') is var tick && tick > 0 && int.TryParse(name.AsSpan(tick + 1), out int written))
            {
                arity = written;
                name = name[..tick];
            }
            if (name.IndexOf('<') is var open && open > 0)
            {
                arity = name.Count(c => c == ',') + 1;
                name = name[..open];
            }
            int dot = name.LastIndexOf('.');
            string ns = dot > 0 ? name[..dot] : "";
            string simple = dot > 0 ? name[(dot + 1)..] : name;
            CatalogType? best = null;
            foreach (var type in all)
            {
                if (type.IsHidden || type.Name != simple || (dot > 0 && type.Namespace != ns) || (arity >= 0 && type.Arity != arity))
                    continue;
                if (best is null || type.Arity < best.Arity)
                    best = type;
            }
            return best;
        }

        /// <summary>The value type that gives a primitive its members (<c>Int32</c> for <c>i32</c>).</summary>
        public CatalogType? PrimitiveType(Primitive primitive) => primitives.TryGetValue(primitive, out var type) ? type : null;

        /// <summary>The token global types give a type no assembly of the catalog has.</summary>
        public static readonly TypeToken Unknown = new(int.MaxValue - 2);

        /// <summary>
        /// <paramref name="type"/> (in <paramref name="context"/>'s tokens) in the catalog's own numbering: every named type
        /// a definition token whose index is the type's <see cref="CatalogType.Id"/>. Views and members speak these.
        /// </summary>
        public HirType Globalize(HirType type, CatalogAssembly context) => type switch
        {
            NamedType n => new NamedType(Resolve(n.Type, context) is { } found ? TypeToken.Def(found.Id) : Unknown, [.. n.Arguments.Select(a => Globalize(a, context))]),
            ArrayType a => a with { Element = Globalize(a.Element, context) },
            InlineArrayType i => i with { Element = Globalize(i.Element, context), SizeOf = i.SizeOf is null ? null : Globalize(i.SizeOf, context) },
            NullableType n => new NullableType(Globalize(n.Inner, context)),
            ReferenceType r => r with { Target = Globalize(r.Target, context) },
            PointerType p => p with { Target = Globalize(p.Target, context) },
            FuncType f => new FuncType([.. f.Parameters.Select(x => Globalize(x, context))], Globalize(f.Result, context)),
            NativeFuncType f => new NativeFuncType([.. f.Parameters.Select(x => Globalize(x, context))], Globalize(f.Result, context)),
            PackExpansionType e => new PackExpansionType(Globalize(e.Pattern, context)),
            _ => type,
        };

        /// <summary>The type a global type (<see cref="Globalize"/>) names: a named type, a primitive's, a nullable's inner one.</summary>
        public CatalogType? TypeOf(HirType global) => global switch
        {
            NamedType { Type.IsDefinition: true } n when n.Type.Index < all.Count => all[n.Type.Index],
            PrimitiveType p => PrimitiveType(p.Kind),
            NullableType n => TypeOf(n.Inner),
            _ => null,
        };

        /// <summary>The type a HIR type names, seen from <paramref name="context"/> (whose tokens it uses).</summary>
        public CatalogType? Resolve(HirType type, CatalogAssembly context) => type switch
        {
            NamedType n => Resolve(n.Type, context),
            PrimitiveType p => PrimitiveType(p.Kind),
            NullableType n => Resolve(n.Inner, context),
            _ => null,
        };

        public CatalogType? Resolve(TypeToken token, CatalogAssembly context)
        {
            var hir = context.Hir;
            if (token.IsDefinition)
                return token.Index < context.types.Length ? context.types[token.Index] : null;
            if (token.Index >= hir.TypeRefs.Count)
                return null;
            var reference = hir.TypeRefs[token.Index];
            string assembly = reference.Assembly < hir.References.Count ? hir.References[reference.Assembly].Name : "";
            return Find(assembly, reference.Namespace, reference.Name, reference.Arity) ?? Find(reference.Namespace, reference.Name, reference.Arity);
        }

        /// <summary>
        /// The member a token names, seen from <paramref name="context"/>: a definition, or a reference resolved by kind,
        /// name and signature in the type it names.
        /// </summary>
        public CatalogMember? Resolve(MemberToken token, MemberKind kind, CatalogAssembly context)
        {
            if (token.IsDefinition)
                return token.Type < context.types.Length ? context.types[token.Type].Member(kind, token.Member) : null;
            if (token.Member >= context.Hir.MemberRefs.Count)
                return null;
            var reference = context.Hir.MemberRefs[token.Member];
            if (Resolve(reference.Parent, context) is not { } type)
                return null;
            CatalogMember? fallback = null;
            foreach (var member in type.Declared)
            {
                if (member.Name != reference.Name || member.HirKind != reference.Kind)
                    continue;
                var parameters = member.Kind == CatalogMemberKind.Indexer ? member.Property!.Indices : member.Method?.Parameters.Select(p => p.Type).ToArray() ?? [];
                if (parameters.Length != reference.Parameters.Length || (member.Method?.Generics.Count ?? 0) != reference.GenericArity)
                    continue;
                fallback ??= member;
                bool same = true;
                for (int p = 0; p < parameters.Length && same; p++)
                    same = SameType(parameters[p], type.Assembly, reference.Parameters[p], context);
                if (same)
                    return member;
            }
            return fallback;
        }

        /// <summary>Whether two HIR types (each in its assembly's tokens) are the same type.</summary>
        public bool SameType(HirType a, CatalogAssembly contextA, HirType b, CatalogAssembly contextB) => (a, b) switch
        {
            (NamedType x, NamedType y) => Resolve(x.Type, contextA) is { } tx && tx == Resolve(y.Type, contextB)
                && x.Arguments.Length == y.Arguments.Length && x.Arguments.Zip(y.Arguments).All(p => SameType(p.First, contextA, p.Second, contextB)),
            (ArrayType x, ArrayType y) => x.Rank == y.Rank && SameType(x.Element, contextA, y.Element, contextB),
            (NullableType x, NullableType y) => SameType(x.Inner, contextA, y.Inner, contextB),
            (ReferenceType x, ReferenceType y) => x.Mutable == y.Mutable && SameType(x.Target, contextA, y.Target, contextB),
            (PointerType x, PointerType y) => SameType(x.Target, contextA, y.Target, contextB),
            (FuncType x, FuncType y) => x.Parameters.Length == y.Parameters.Length && SameType(x.Result, contextA, y.Result, contextB)
                && x.Parameters.Zip(y.Parameters).All(p => SameType(p.First, contextA, p.Second, contextB)),
            _ => a == b,
        };

        /// <summary>The classes whose base is <paramref name="type"/> (directly).</summary>
        public IEnumerable<CatalogType> DerivedFrom(CatalogType type) => all.Where(t => !t.IsHidden && t.Base == type);

        /// <summary>The types that implement interface <paramref name="type"/> (directly or through a base).</summary>
        public IEnumerable<CatalogType> Implementers(CatalogType type) =>
            all.Where(t => !t.IsHidden && t != type && t.AllInterfaces().Any(i => i == type));

        /// <summary>
        /// Types and members whose names match <paramref name="query"/> (case does not matter): the exact name first, then
        /// names that start with it, then those whose capitals spell it (<c>sb</c> finds <c>StringBuilder</c>), then those
        /// that contain it.
        /// </summary>
        public IReadOnlyList<SearchHit> Search(string query, int limit = 50, bool members = true, bool nonPublic = false)
        {
            var hits = new List<SearchHit>();
            if (query.Length == 0)
                return hits;
            foreach (var type in all)
            {
                if (type.IsHidden || !nonPublic && type.Access != Access.Public)
                    continue;
                if (!type.IsModule && Score(type.Name, query) is int score)
                    hits.Add(new SearchHit(type, null, score));
                if (!members)
                    continue;
                foreach (var member in type.Declared)
                {
                    if (!nonPublic && member.Access is not (Access.Public or Access.Inherited))
                        continue;
                    if (Score(member.Name, query) is int memberScore)
                        hits.Add(new SearchHit(type, member, memberScore + 1));
                }
            }
            hits.Sort((a, b) => a.Score != b.Score ? a.Score.CompareTo(b.Score)
                : a.Name.Length != b.Name.Length ? a.Name.Length.CompareTo(b.Name.Length)
                : string.CompareOrdinal(a.FullName, b.FullName));
            return hits.Count > limit ? hits.GetRange(0, limit) : hits;
        }

        /// <summary>How well <paramref name="name"/> matches (lower is better; null: it does not).</summary>
        public static int? Score(string name, string query)
        {
            if (name.Equals(query, StringComparison.OrdinalIgnoreCase))
                return name == query ? 0 : 2;
            if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                return name.StartsWith(query, StringComparison.Ordinal) ? 4 : 6;
            if (Humps(name, query))
                return 8;
            if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
                return 10;
            return null;
        }

        /// <summary>Whether the query's letters are, in order, the starts of the name's words (<c>sB</c>, <c>SB</c>, <c>strb</c>).</summary>
        private static bool Humps(string name, string query)
        {
            int q = 0;
            for (int i = 0; i < name.Length && q < query.Length; i++)
            {
                bool start = i == 0 || char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]) || name[i - 1] == '_';
                if (char.ToLowerInvariant(name[i]) != char.ToLowerInvariant(query[q]))
                {
                    if (start)
                        continue;
                    continue;
                }
                if (start || q > 0 && i > 0 && char.ToLowerInvariant(name[i - 1]) == char.ToLowerInvariant(query[q - 1]))
                    q++;
            }
            return q == query.Length && query.Length > 1;
        }
    }

    /// <summary>A search result: a type, or a member of one.</summary>
    public sealed record SearchHit(CatalogType Type, CatalogMember? Member, int Score)
    {
        public string Name => Member?.Name ?? Type.Name;
        public string FullName => Member is null ? Type.FullName : $"{Type.FullName}.{Member.Name}";
    }

    public enum CatalogTypeKind : byte { Class, Struct, Interface, Enum, Bitflags, Func, Module }

    /// <summary>A type of the catalog: its definition, where it is, and its members as languages show them.</summary>
    public sealed class CatalogType
    {
        internal CatalogType(CatalogAssembly assembly, int index, TypeDef definition)
        {
            Assembly = assembly;
            Index = index;
            Definition = definition;
        }

        public CatalogAssembly Assembly { get; }
        public int Index { get; }

        /// <summary>The type's number in the catalog: the token global types give it (<see cref="HirCatalog.Globalize"/>).</summary>
        public int Id { get; init; }

        public TypeDef Definition { get; }
        public HirCatalog Catalog => Assembly.Catalog;

        public string Namespace => Definition.Namespace;
        public string Name => Definition.Name;
        public string FullName => Definition.FullName;
        public int Arity => Definition.Generics.Count;
        public string? Doc => Definition.Doc;
        public SourceSpot? Location => Assembly.Spot(Definition.Location);
        public Access Access => Definition.Access;
        public TypeToken Token => TypeToken.Def(Index);

        /// <summary>
        /// A module: the type a frontend puts a namespace's top-level functions and fields in (named <c>$Module</c>). Its
        /// members are shown as the namespace's own.
        /// </summary>
        public bool IsModule => Name == "$Module";

        /// <summary>
        /// Made by a frontend and not written in the source (closures, tables): not listed. A namespace's module is made too,
        /// but its members are the namespace's own (its functions and constants): it stays.
        /// </summary>
        public bool IsHidden => !IsModule && ((Definition.Flags & TypeFlags.Generated) != 0 || Name.StartsWith('$'));

        public CatalogTypeKind Kind => IsModule ? CatalogTypeKind.Module : (CatalogTypeKind)Definition.Kind;
        public bool IsClass => Definition.Kind == TypeKind.Class && !IsModule;
        public bool IsInterface => Definition.Kind == TypeKind.Interface;
        public bool IsValue => Definition.Kind == TypeKind.Struct;
        public bool IsEnum => Definition.Kind is TypeKind.Enum or TypeKind.Bitflags;
        public bool IsStatic => (Definition.Flags & TypeFlags.Static) != 0;
        public bool IsFinal => (Definition.Flags & TypeFlags.Final) != 0;
        public bool IsAbstract => (Definition.Flags & TypeFlags.Abstract) != 0;

        /// <summary>The primitive this type gives members to (<c>i32</c> for <c>Int32</c>), or null.</summary>
        public Primitive? Primitive => (Definition.Flags & TypeFlags.Primitive) != 0 && Definition.Underlying is PrimitiveType p ? p.Kind : null;

        /// <summary>An <c>ext(…)</c> block (a frontend's holder of extension methods, hidden from the types): its methods extend <see cref="Extended"/>.</summary>
        public bool IsExtension => (Definition.Flags & TypeFlags.Extension) != 0;

        /// <summary>The type an extension block extends (global; a type parameter for a generic <c>ext&lt;T&gt;</c>), or null.</summary>
        public HirType? Extended => IsExtension && Definition.Underlying is { } extended ? Global(extended) : null;

        /// <summary>The type as itself (global): its own parameters as arguments (<c>List&lt;T&gt;</c>).</summary>
        public HirType SelfType => Primitive is { } p ? HirType.Of(p)
            : new NamedType(TypeToken.Def(Id), [.. Enumerable.Range(0, Arity).Select(i => (HirType)new GenericParamType(GenericOwner.Type, i))]);

        /// <summary>A HIR type of this type's assembly, global (<see cref="HirCatalog.Globalize"/>).</summary>
        public HirType Global(HirType type) => Catalog.Globalize(type, Assembly);

        /// <summary>The base class as written (global, with its type arguments), or null.</summary>
        public HirType? BaseType => Definition.Base is { } b ? Global(b) : null;

        /// <summary>The interfaces as written (global).</summary>
        public IEnumerable<HirType> InterfaceTypes => Definition.Interfaces.Select(Global);

        /// <summary>The base class (null for the root and for other kinds).</summary>
        public CatalogType? Base { get; private set; }

        /// <summary>The interfaces it declares (not those of its base or of the interfaces it extends).</summary>
        public IReadOnlyList<CatalogType> Interfaces { get; private set; } = [];

        /// <summary>Its own members (fields, constants, enum values, constructors, methods, properties, indexers, events), in order.</summary>
        public IReadOnlyList<CatalogMember> Declared => declared;
        private CatalogMember[] declared = [];

        private CatalogMember?[] fieldMembers = [], methodMembers = [], propertyMembers = [], eventMembers = [];

        internal void Seal()
        {
            if (Definition.Base is { } b)
                Base = Catalog.Resolve(b, Assembly);
            Interfaces = [.. Definition.Interfaces.Select(i => Catalog.Resolve(i, Assembly)).OfType<CatalogType>()];
            var list = new List<CatalogMember>();
            fieldMembers = new CatalogMember?[Definition.Fields.Count];
            methodMembers = new CatalogMember?[Definition.Methods.Count];
            propertyMembers = new CatalogMember?[Definition.Properties.Count];
            eventMembers = new CatalogMember?[Definition.Events.Count];
            for (int f = 0; f < Definition.Fields.Count; f++)
            {
                var field = Definition.Fields[f];
                if ((field.Flags & FieldFlags.Generated) != 0 || field.Name.StartsWith('$'))
                    continue;
                var kind = IsEnum && (field.Flags & FieldFlags.Const) != 0 ? CatalogMemberKind.EnumValue
                    : (field.Flags & FieldFlags.Const) != 0 ? CatalogMemberKind.Constant : CatalogMemberKind.Field;
                list.Add(fieldMembers[f] = new CatalogMember(this, kind, f) { Field = field });
            }
            for (int m = 0; m < Definition.Methods.Count; m++)
            {
                var method = Definition.Methods[m];
                if ((method.Flags & MethodFlags.Generated) != 0 || method.Name.StartsWith('$') && method.Kind is not (MethodKind.Operator or MethodKind.Conversion))
                    continue;
                var kind = method.Kind switch
                {
                    MethodKind.Normal => CatalogMemberKind.Method,
                    MethodKind.Constructor => CatalogMemberKind.Constructor,
                    MethodKind.Operator => CatalogMemberKind.Operator,
                    MethodKind.Conversion => CatalogMemberKind.Conversion,
                    _ => (CatalogMemberKind?)null,
                };
                if (kind is { } k)
                    list.Add(methodMembers[m] = new CatalogMember(this, k, m) { Method = method });
            }
            for (int p = 0; p < Definition.Properties.Count; p++)
            {
                var property = Definition.Properties[p];
                var kind = property.Indices.Length > 0 ? CatalogMemberKind.Indexer : CatalogMemberKind.Property;
                list.Add(propertyMembers[p] = new CatalogMember(this, kind, p) { Property = property });
            }
            for (int e = 0; e < Definition.Events.Count; e++)
                list.Add(eventMembers[e] = new CatalogMember(this, CatalogMemberKind.Event, e) { Event = Definition.Events[e] });
            declared = [.. list];
        }

        /// <summary>The member that definition <paramref name="index"/> of the kind is (null when it is not shown).</summary>
        public CatalogMember? Member(MemberKind kind, int index)
        {
            var table = kind switch
            {
                MemberKind.Field => fieldMembers,
                MemberKind.Method => methodMembers,
                MemberKind.Property => propertyMembers,
                _ => eventMembers,
            };
            if (index >= 0 && index < table.Length && table[index] is { } member)
                return member;
            // An accessor: the property it belongs to.
            if (kind == MemberKind.Method)
            {
                for (int p = 0; p < Definition.Properties.Count; p++)
                    if (Definition.Properties[p].Getter == index || Definition.Properties[p].Setter == index)
                        return propertyMembers[p];
            }
            return null;
        }

        /// <summary>Its base classes, nearest first.</summary>
        public IEnumerable<CatalogType> BaseChain()
        {
            var seen = new HashSet<CatalogType>();
            for (var t = Base; t is not null && seen.Add(t); t = t.Base)
                yield return t;
        }

        /// <summary>Every interface it has: its own, its bases', and those they extend.</summary>
        public IEnumerable<CatalogType> AllInterfaces()
        {
            var seen = new HashSet<CatalogType>();
            var pending = new Stack<CatalogType>();
            foreach (var t in BaseChain().Prepend(this))
                foreach (var i in t.Interfaces)
                    pending.Push(i);
            while (pending.Count > 0)
            {
                var i = pending.Pop();
                if (!seen.Add(i))
                    continue;
                yield return i;
                foreach (var extended in i.Interfaces)
                    pending.Push(extended);
            }
        }

        /// <summary>
        /// The members of a value of this type with <paramref name="arguments"/> (its own, then its bases' not overridden
        /// or hidden by name and parameters), each with the type arguments of its declaring type seen from here.
        /// </summary>
        public IReadOnlyList<MemberView> Members(HirType[]? arguments = null, bool inherited = true)
        {
            arguments ??= [.. Enumerable.Range(0, Arity).Select(i => (HirType)new GenericParamType(GenericOwner.Type, i))];
            var result = new List<MemberView>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var type = this;
            var current = arguments;
            var guard = new HashSet<CatalogType>();
            while (type is not null && guard.Add(type))
            {
                foreach (var member in type.declared)
                {
                    if (member.Kind == CatalogMemberKind.Constructor && type != this)
                        continue;
                    string key = member.Key;
                    if (seen.Add(key))
                        result.Add(new MemberView(member, current, type != this));
                }
                if (!inherited || type.BaseType is not NamedType baseType)
                    break;
                var next = type.Base;
                current = [.. baseType.Arguments.Select(a => a.Substitute(current))];
                type = next;
            }
            // An interface's members include those of the interfaces it extends.
            if (inherited && IsInterface)
            {
                foreach (var extended in AllInterfaces())
                    foreach (var member in extended.declared)
                        if (seen.Add(member.Key))
                            result.Add(new MemberView(member, [], true));
            }
            return result;
        }

        public override string ToString() => FullName;
    }

    public enum CatalogMemberKind : byte { Field, Constant, EnumValue, Constructor, Method, Property, Indexer, Event, Operator, Conversion }

    /// <summary>A member as a catalog shows it (accessors are part of their property; generated members are not shown).</summary>
    public sealed class CatalogMember
    {
        internal CatalogMember(CatalogType type, CatalogMemberKind kind, int index)
        {
            Type = type;
            Kind = kind;
            Index = index;
        }

        public CatalogType Type { get; }
        public CatalogMemberKind Kind { get; }

        /// <summary>The index in the definition's list of its HIR kind (fields, methods, properties, events).</summary>
        public int Index { get; }

        public FieldDef? Field { get; init; }
        public MethodDef? Method { get; init; }
        public PropertyDef? Property { get; init; }
        public EventDef? Event { get; init; }

        public MemberKind HirKind => Kind switch
        {
            CatalogMemberKind.Field or CatalogMemberKind.Constant or CatalogMemberKind.EnumValue => MemberKind.Field,
            CatalogMemberKind.Property or CatalogMemberKind.Indexer => MemberKind.Property,
            CatalogMemberKind.Event => MemberKind.Event,
            _ => MemberKind.Method,
        };

        public string Name => Field?.Name ?? Method?.Name ?? Property?.Name ?? Event!.Name;

        public bool IsStatic => Field is { } f ? (f.Flags & (FieldFlags.Static | FieldFlags.Const)) != 0
            : Method is { } m ? m.IsStatic
            : Property is { } p ? (p.Flags & MemberFlags.Static) != 0
            : (Event!.Flags & MemberFlags.Static) != 0;

        public Access Access => Field?.Access ?? Method?.Access ?? Property?.Access ?? Event!.Access;

        public string? Doc => Field?.Doc ?? Method?.Doc ?? Property?.Doc
            ?? (Property is { Getter: >= 0 } p ? Type.Definition.Methods[p.Getter].Doc : null);

        public SourceSpot? Location
        {
            get
            {
                var location = Field?.Location ?? Method?.Location ?? Property?.Location ?? SourceLocation.None;
                if (location.File < 0 && Property is { Getter: >= 0 } p)
                    location = Type.Definition.Methods[p.Getter].Location;
                return Type.Assembly.Spot(location);
            }
        }

        /// <summary>A field's, property's or event's type; a method's result (global).</summary>
        public HirType ValueType => valueType ??= Type.Global(Field?.Type ?? Property?.Type ?? Event?.Type ?? Method!.Result);
        private HirType? valueType;

        /// <summary>The parameters (an indexer's indices), their types global.</summary>
        public IReadOnlyList<Parameter> Parameters => parameters ??= Method is { } m
            ? [.. m.Parameters.Select(p => p with { Type = Type.Global(p.Type) })]
            : Property is { } p ? [.. p.Indices.Select((t, i) => new Parameter(i == 0 ? "index" : $"index{i}", Type.Global(t)))] : [];
        private Parameter[]? parameters;

        /// <summary>A generic method's type parameters (their constraints global).</summary>
        public IReadOnlyList<GenericParam> Generics => generics ??= Method is { } m
            ? [.. m.Generics.Select(g => g with { Types = [.. g.Types.Select(Type.Global)], OneOf = [.. g.OneOf.Select(Type.Global)] })] : [];
        private GenericParam[]? generics;

        public bool CanRead => Property is null || Property.Getter >= 0;
        public bool CanWrite => Property is { Setter: >= 0 } || Field is { } f && (f.Flags & (FieldFlags.Readonly | FieldFlags.Const)) == 0;

        /// <summary>Name and parameter types: what an override or a hiding member has in common with this one.</summary>
        /// <summary>The member among its type's: its name, and a method's parameter types (overloads differ).</summary>
        public string Key => Method is not null ? $"{Name}({string.Join(",", Parameters.Select(p => p.Type))})" : Name;

        public override string ToString() => $"{Type.FullName}.{Name}";
    }

    /// <summary>A member seen through a type: the type arguments its declaring type has there.</summary>
    public sealed record MemberView(CatalogMember Member, HirType[] TypeArguments, bool Inherited)
    {
        public string Name => Member.Name;
        public HirType ValueType => Member.ValueType.Substitute(TypeArguments);
        public Parameter[] Parameters => [.. Member.Parameters.Select(p => p with { Type = p.Type.Substitute(TypeArguments) })];
    }
}

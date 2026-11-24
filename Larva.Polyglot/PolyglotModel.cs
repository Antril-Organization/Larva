using Larva.Hir;
using Larva.Hir.Services;

namespace Larva.Polyglot
{
    /// <summary>A finding of the generator: an error stops it, a warning does not.</summary>
    public sealed record PolyglotDiagnostic(string Code, bool Error, string Message)
    {
        public override string ToString() => $"{(Error ? "error" : "warning")} {Code}: {Message}";
    }

    /// <summary>What a generation exposes and how: languages, acronyms, packages, and a profile's hooks.</summary>
    public sealed class PolyglotOptions
    {
        public IReadOnlyList<TargetLanguage> Languages { get; init; } = Enum.GetValues<TargetLanguage>();

        /// <summary>The acronym policy of each language that is not the default (<see cref="AcronymPolicy.Keep"/>).</summary>
        public IReadOnlyDictionary<TargetLanguage, AcronymPolicy> Acronyms { get; init; } = new Dictionary<TargetLanguage, AcronymPolicy>();

        /// <summary>What Java packages start with (<c>com.acme</c>).</summary>
        public string JavaPackageRoot { get; init; } = "";

        /// <summary>What C# namespaces start with (a library whose namespaces .NET has: <c>System</c>).</summary>
        public string CSharpNamespaceRoot { get; init; } = "";

        /// <summary>Whether a type is exposed (after the rules: public, not made by a frontend).</summary>
        public Func<CatalogType, bool>? IncludeType { get; init; }

        /// <summary>Whether a member is exposed (after the rules).</summary>
        public Func<CatalogMember, bool>? IncludeMember { get; init; }

        /// <summary>
        /// The name a declaration gets in a language: given the language, the kind, the declaration's full name in the
        /// library (<c>Acme.Json.Parser.Parse</c>) and the name the rules propose; returns it, or another. A profile's
        /// <c>Name</c> hook and the library's attributes come in here. For a member with two names (a getter and a
        /// setter) it is asked about the first; the second follows it.
        /// </summary>
        public Func<TargetLanguage, NameKind, string, string, string>? Rename { get; init; }
    }

    /// <summary>A type the library exposes (or a namespace's module: its functions and constants), with its name in each language.</summary>
    public sealed class PolyglotType
    {
        internal PolyglotType(CatalogType source, PolyglotType? owner)
        {
            Source = source;
            Owner = owner;
        }

        public CatalogType Source { get; }

        /// <summary>The type it is declared in (a nested type), or null.</summary>
        public PolyglotType? Owner { get; }

        public string Namespace => Source.Namespace;

        /// <summary>A namespace's module: its members are the namespace's functions and constants.</summary>
        public bool IsModule => Source.IsModule;

        public bool IsRecord => (Source.Definition.Flags & TypeFlags.Record) != 0;

        /// <summary>An interface whose name without <c>I</c> another type of its scope has (<c>INumber</c> and <c>Number</c>): it keeps it everywhere.</summary>
        public bool KeepsInterfacePrefix { get; internal set; }

        /// <summary>The name in the library: the type's full name, or the namespace for a module.</summary>
        public string SourceName => IsModule ? (Namespace.Length == 0 ? "(global)" : Namespace) : Source.FullName;

        /// <summary>
        /// Its name in each language. A module's is the class its functions go in (C#, Java), or "" where they are the
        /// module's own (C, Python, TypeScript).
        /// </summary>
        public Dictionary<TargetLanguage, string> Names { get; } = [];

        /// <summary>Its namespace as each language names the module or package.</summary>
        public Dictionary<TargetLanguage, string> Namespaces { get; } = [];

        public List<PolyglotMember> Members { get; } = [];

        public override string ToString() => SourceName;
    }

    /// <summary>A member the library exposes, with the names it takes in each language and its parameters' names.</summary>
    public sealed class PolyglotMember
    {
        internal PolyglotMember(PolyglotType owner, CatalogMember source, NameKind kind)
        {
            Owner = owner;
            Source = source;
            Kind = kind;
        }

        public PolyglotType Owner { get; }
        public CatalogMember Source { get; }
        public NameKind Kind { get; }

        /// <summary>An operator's symbol, or null.</summary>
        public string? Symbol { get; init; }

        /// <summary>The member's full name in the library (<c>Acme.Json.Parser.Parse</c>; a constructor's is <c>…Parser.new</c>).</summary>
        public string SourceName => Owner.IsModule
            ? (Owner.Namespace.Length == 0 ? "" : Owner.Namespace + ".") + Name
            : Owner.Source.FullName + "." + Name;

        /// <summary>Its own name in the library (<c>new</c> for a constructor, the symbol for an operator).</summary>
        public string Name => Kind switch
        {
            NameKind.Constructor => "new",
            NameKind.Operator => "operator " + Symbol,
            NameKind.Indexer => "this[]",
            _ => Source.Name,
        };

        /// <summary>How it is written in the library with its parameters (<c>Parse(str, i32)</c>): what tells overloads apart in messages.</summary>
        public string Display => Source.Method is null && Kind != NameKind.Indexer ? SourceName
            : $"{SourceName}({string.Join(", ", Source.Parameters.Select(p => TypeText(p.Type)))})";

        /// <summary>
        /// The names it takes in each language, the first the one shown (a property in Java: getter, then setter). Empty
        /// where the language cannot express it.
        /// </summary>
        public Dictionary<TargetLanguage, string[]> Names { get; } = [];

        /// <summary>Its parameters' names in each language, in order.</summary>
        public Dictionary<TargetLanguage, string[]> Parameters { get; } = [];

        /// <summary>
        /// The dispatching languages (Python, TypeScript) in which this overload shares its entry with others that differ
        /// only in the width of numbers (<c>Write(i32)</c>, <c>Write(i64)</c>): the entry picks one by the value's range.
        /// </summary>
        public HashSet<TargetLanguage> ValueDispatch { get; } = [];

        internal string TypeText(HirType type) => type switch
        {
            PrimitiveType p => p.Kind.Keyword(),
            NullableType n => TypeText(n.Inner) + "?",
            ArrayType a => TypeText(a.Element) + "[]",
            NamedType n when Owner.Source.Catalog.TypeOf(n) is { } t => t.FullName == "System.String" ? "str"
                : t.Name + (n.Arguments.Length > 0 ? $"<{string.Join(", ", n.Arguments.Select(TypeText))}>" : ""),
            FuncType f => $"func<{string.Join(", ", f.Parameters.Select(TypeText))}> : {TypeText(f.Result)}",
            PointerType p => TypeText(p.Target) + (p.Mutable ? "*" : " const*"),
            GenericParamType g => g.Owner == GenericOwner.Type
                ? (g.Index < Owner.Source.Definition.Generics.Count ? Owner.Source.Definition.Generics[g.Index].Name : "T" + g.Index)
                : (g.Index < Source.Generics.Count ? Source.Generics[g.Index].Name : "M" + g.Index),
            _ => type.ToString(),
        };

        public override string ToString() => Display;
    }

    /// <summary>
    /// What a library offers other languages: its public types and members, each with the names the language's rules give
    /// it (and the options' renames), checked so that no two declarations end up with one name. Every generator and the
    /// <c>larva polyglot</c> command start from it.
    /// </summary>
    public sealed class PolyglotModel
    {
        private PolyglotModel(CatalogAssembly assembly, IReadOnlyList<TargetRules> targets)
        {
            Assembly = assembly;
            Targets = targets;
        }

        public CatalogAssembly Assembly { get; }
        public IReadOnlyList<TargetRules> Targets { get; }
        public List<PolyglotType> Types { get; } = [];
        public List<PolyglotDiagnostic> Diagnostics { get; } = [];
        public bool HasErrors => Diagnostics.Any(d => d.Error);

        public TargetRules Rules(TargetLanguage language) => Targets.First(t => t.Language == language);

        /// <summary>The model of <paramref name="assembly"/> (one of <paramref name="catalog"/>'s, which also holds what it references).</summary>
        public static PolyglotModel Build(HirCatalog catalog, CatalogAssembly assembly, PolyglotOptions? options = null)
        {
            options ??= new PolyglotOptions();
            var targets = options.Languages.Distinct().Select(l =>
                TargetRules.For(l, options.Acronyms.TryGetValue(l, out var a) ? a : null, options.JavaPackageRoot, options.CSharpNamespaceRoot)).ToList();
            var model = new PolyglotModel(assembly, targets);
            new Builder(model, catalog, options).Run();
            return model;
        }

        private sealed class Builder(PolyglotModel model, HirCatalog catalog, PolyglotOptions options)
        {
            private readonly Dictionary<int, PolyglotType> byIndex = [];

            public void Run()
            {
                // `INumber` keeps its `I` where `Number` is a type of its scope too.
                var scopeNames = model.Assembly.Types.Where(Exposed).Select(t => (t.Namespace, t.Definition.DeclaringType, t.Name)).ToHashSet();
                // Types first (members' names may use their type's: C's prefix, a constructor's name), outer before nested.
                foreach (var type in model.Assembly.Types.OrderBy(Depth))
                {
                    if (!Exposed(type))
                        continue;
                    var owner = type.Definition.DeclaringType >= 0 ? byIndex.GetValueOrDefault(type.Definition.DeclaringType) : null;
                    if (type.Definition.DeclaringType >= 0 && owner is null)
                        continue; // nested in a type that is not exposed
                    var made = new PolyglotType(type, owner)
                    {
                        KeepsInterfacePrefix = type.IsInterface && TargetRules.WithoutI(type.Name) is var bare && bare != type.Name
                            && scopeNames.Contains((type.Namespace, type.Definition.DeclaringType, bare)),
                    };
                    byIndex[type.Index] = made;
                    model.Types.Add(made);
                    foreach (var rules in model.Targets)
                    {
                        made.Namespaces[rules.Language] = rules.Namespace(type.Namespace);
                        made.Names[rules.Language] = made.IsModule
                            ? Rename(rules, NameKind.ModuleClass, made.SourceName, rules.ModuleClass(type.Namespace, model.Assembly.Name) ?? "")
                            : Rename(rules, type.IsInterface ? NameKind.Interface : NameKind.Type, made.SourceName, rules.Type(made));
                    }
                }
                foreach (var type in model.Types)
                    Members(type);
                foreach (var rules in model.Targets)
                    Check(rules);
            }

            private int Depth(CatalogType type)
            {
                int depth = 0;
                for (int t = type.Definition.DeclaringType; t >= 0 && depth < 64; t = model.Assembly.Hir.Types[t].DeclaringType)
                    depth++;
                return depth;
            }

            private bool Exposed(CatalogType type) =>
                (type.IsModule || !type.IsHidden) && !type.IsExtension && type.Access == Access.Public
                && type.Definition.Kind != TypeKind.Func && options.IncludeType?.Invoke(type) != false;

            private string Rename(TargetRules rules, NameKind kind, string source, string proposed) =>
                options.Rename?.Invoke(rules.Language, kind, source, proposed) ?? proposed;

            private void Members(PolyglotType type)
            {
                foreach (var member in type.Source.Declared)
                {
                    // An interface's members are public whatever their access says (they are the interface).
                    if (member.Access != Access.Public && !type.Source.IsInterface || options.IncludeMember?.Invoke(member) == false)
                        continue;
                    if (member.Kind == CatalogMemberKind.Conversion)
                    {
                        model.Diagnostics.Add(new("PG0105", false, $"`{type.SourceName}`: conversions are not offered to other languages yet; the conversion is left out"));
                        continue;
                    }
                    var kind = member.Kind switch
                    {
                        CatalogMemberKind.Field => NameKind.Field,
                        CatalogMemberKind.Constant => NameKind.Constant,
                        CatalogMemberKind.EnumValue => NameKind.EnumMember,
                        CatalogMemberKind.Constructor => NameKind.Constructor,
                        CatalogMemberKind.Property => NameKind.Property,
                        CatalogMemberKind.Indexer => NameKind.Indexer,
                        CatalogMemberKind.Event => NameKind.Event,
                        CatalogMemberKind.Operator => NameKind.Operator,
                        _ => NameKind.Method,
                    };
                    string? symbol = kind == NameKind.Operator
                        ? type.Source.Definition.Operators.FirstOrDefault(o => o.Method == member.Index)?.Symbol ?? member.Name
                        : null;
                    bool unary = kind == NameKind.Operator && member.Parameters.Count == (member.IsStatic ? 1 : 0);
                    var made = new PolyglotMember(type, member, kind) { Symbol = symbol };
                    var request = new MemberRequest(type, kind, member.Name, member) { Symbol = symbol, Unary = unary };
                    var silent = new List<string>();
                    foreach (var rules in model.Targets)
                    {
                        string[] names = rules.Member(request);
                        if (names.Length > 0 && options.Rename is { } rename)
                        {
                            string chosen = rename(rules.Language, kind, made.SourceName, names[0]);
                            if (chosen != names[0])
                                names = [chosen, .. names.Skip(1).Select(n => Follow(n, names[0], chosen))];
                        }
                        if (kind != NameKind.Constructor && names.FirstOrDefault(rules.Inherited.Contains) is { } hidden)
                        {
                            model.Diagnostics.Add(new("PG0106", false, $"`{made.Display}` would hide `{hidden}`, which every {rules.Id} object has: it is `{hidden}_` there"));
                            names = [.. names.Select(n => rules.Inherited.Contains(n) ? n + "_" : n)];
                        }
                        made.Names[rules.Language] = names;
                        made.Parameters[rules.Language] = [.. member.Parameters.Select(p => Rename(rules, NameKind.Parameter, made.SourceName + "." + p.Name, rules.Parameter(p.Name)))];
                        if (names.Length == 0)
                            silent.Add(rules.Id);
                    }
                    if (silent.Count > 0)
                        model.Diagnostics.Add(new("PG0104", false, $"`{made.Display}` has no form in {string.Join(", ", silent)}: it is left out there"));
                    type.Members.Add(made);
                }
            }

            /// <summary>The second name of a renamed member (a setter) follows the first: <c>getDepth</c> → <c>getLevel</c> makes <c>setDepth</c> <c>setLevel</c>.</summary>
            private static string Follow(string second, string first, string chosen)
            {
                foreach (var (from, to) in new[] { ("get_", "set_"), ("get", "set"), ("is", "set"), ("add", "remove"), ("__getitem__", "__setitem__") })
                {
                    if (first.StartsWith(from, StringComparison.Ordinal) && second.StartsWith(to, StringComparison.Ordinal) && chosen.StartsWith(from, StringComparison.Ordinal))
                        return to + chosen[from.Length..];
                }
                // `acme_parser_get_depth` / `acme_parser_set_depth` in C: the word after the type.
                int at = first.LastIndexOf("_get_", StringComparison.Ordinal);
                if (at >= 0 && chosen.Contains("_get_", StringComparison.Ordinal))
                    return chosen.Replace("_get_", "_set_", StringComparison.Ordinal);
                return second == first ? chosen : second;
            }

            // ── Overloads and collisions ────────────────────────────────────

            private void Check(TargetRules rules)
            {
                var lang = rules.Language;
                // Without operator syntax an operator is a named function; where the type has a method of that name
                // (`TimeSpan.Add` and `+`), the method is the way to do it there, and the operator is not offered twice.
                if (!rules.OperatorSyntax)
                {
                    foreach (var type in model.Types)
                    {
                        var methods = type.Members.Where(m => m.Kind == NameKind.Method).SelectMany(m => m.Names[lang]).ToHashSet(StringComparer.Ordinal);
                        foreach (var op in type.Members.Where(m => m.Kind == NameKind.Operator && m.Names[lang].Any(methods.Contains)))
                            op.Names[lang] = [];
                    }
                }
                // C: a function of an overload set other than the first takes a suffix from its parameter types.
                if (!rules.Overloads && !rules.Dispatches)
                {
                    foreach (var type in model.Types)
                    {
                        foreach (var set in type.Members.Where(m => m.Source.Method is not null && m.Names[lang].Length == 1).GroupBy(m => m.Names[lang][0]))
                        {
                            foreach (var member in set.Skip(1))
                                member.Names[lang] = [member.Names[lang][0] + CRules.OverloadSuffix(member.Source.Parameters, catalog)];
                        }
                    }
                }
                // Types: one scope per namespace (C: one for everything), nested types in their owner's.
                var scopes = new Dictionary<string, Dictionary<string, (string Source, string Display)>>(StringComparer.Ordinal);
                void Claim(string scope, string name, string source, string display)
                {
                    if (name.Length == 0)
                        return;
                    if (!scopes.TryGetValue(scope, out var names))
                        scopes[scope] = names = new(StringComparer.Ordinal);
                    if (!names.TryGetValue(name, out var held))
                        names[name] = (source, display);
                    else if (held.Source != source)
                        model.Diagnostics.Add(new("PG0101", true,
                            $"`{held.Display}` and `{display}` both become `{name}` in {rules.Id}: rename one with [PolyglotName(\"{rules.Id}\", …)] or in the profile's Name hook"));
                }
                string TypeScope(PolyglotType t) => lang == TargetLanguage.C ? "" : t.Owner is { } o ? "type " + o.SourceName : "ns " + t.Namespaces[lang];
                foreach (var type in model.Types)
                    Claim(TypeScope(type), type.Names[lang], type.SourceName, type.SourceName);
                foreach (var type in model.Types)
                {
                    // A member's scope: its type's members; in C every name is global; a module's members are the
                    // namespace's in languages with free functions, so they meet its types there.
                    string scope = lang == TargetLanguage.C ? ""
                        : type.IsModule && type.Names[lang].Length == 0 ? "ns " + type.Namespaces[lang]
                        : "members " + type.SourceName;
                    foreach (var member in type.Members)
                    {
                        // Overloads share one name: the source of a name is the member's name, not the overload.
                        string source = member.Kind is NameKind.Constructor or NameKind.Indexer ? $"{member.Kind} {type.SourceName}"
                            : member.Source.Method is not null && member.Kind != NameKind.Operator ? $"method {type.SourceName}.{member.Source.Name}"
                            : member.SourceName;
                        foreach (var name in member.Names[lang])
                            Claim(scope, name, source, member.Display);
                    }
                    if (rules.Dispatches)
                        Indistinct(rules, type);
                    if (lang == TargetLanguage.Java)
                        Erasure(type);
                }
            }

            /// <summary>PG0103: overloads a dispatching language cannot tell apart (same count, every argument alike).</summary>
            private void Indistinct(TargetRules rules, PolyglotType type)
            {
                foreach (var set in type.Members.Where(m => m.Source.Method is not null && m.Names[rules.Language].Length > 0).GroupBy(m => m.Names[rules.Language][0]))
                {
                    var seen = new Dictionary<string, PolyglotMember>(StringComparer.Ordinal);
                    foreach (var member in set)
                    {
                        string shape = string.Join(",", member.Source.Parameters.Select(p => rules.Looks(p.Type, catalog)));
                        if (!seen.TryGetValue(shape, out var first))
                            seen[shape] = member;
                        else if (OnlyWidths(first, member))
                        {
                            first.ValueDispatch.Add(rules.Language);
                            member.ValueDispatch.Add(rules.Language);
                        }
                        else
                            model.Diagnostics.Add(new("PG0103", true,
                                $"`{first.Display}` and `{member.Display}` cannot be told apart in {rules.Id} (both take ({shape})): rename one with [PolyglotName(\"{rules.Id}\", …)] or in the profile's Name hook"));
                    }
                }
            }

            /// <summary>Whether two overloads differ only in number types (<c>i8</c> and <c>i64</c>, <c>f32</c> and <c>f64</c>; nullable in both or in neither).</summary>
            private static bool OnlyWidths(PolyglotMember a, PolyglotMember b)
            {
                static HirType Bare(HirType t) => t is NullableType n ? n.Inner : t;
                static bool Number(HirType t) => Bare(t) is PrimitiveType p && (p.Kind.IsInteger() || p.Kind.IsFloat());
                var x = a.Source.Parameters;
                var y = b.Source.Parameters;
                return x.Count == y.Count && Enumerable.Range(0, x.Count).All(i =>
                    x[i].Type.Equals(y[i].Type) || Number(x[i].Type) && Number(y[i].Type) && (x[i].Type is NullableType) == (y[i].Type is NullableType));
            }

            /// <summary>PG0102: Java overloads whose parameters are the same after erasure.</summary>
            private void Erasure(PolyglotType type)
            {
                foreach (var set in type.Members.Where(m => m.Source.Method is not null && m.Names[TargetLanguage.Java].Length > 0).GroupBy(m => m.Names[TargetLanguage.Java][0]))
                {
                    var seen = new Dictionary<string, PolyglotMember>(StringComparer.Ordinal);
                    foreach (var member in set)
                    {
                        string erased = string.Join(",", member.Source.Parameters.Select(p => JavaRules.Erased(p.Type, catalog)));
                        if (seen.TryGetValue(erased, out var first))
                        {
                            // Different overloads in the library that differ only in type arguments (List<i32> and List<str>).
                            if (first.Display != member.Display)
                                model.Diagnostics.Add(new("PG0102", true,
                                    $"`{first.Display}` and `{member.Display}` are the same method in java after erasure ({erased}): rename one with [PolyglotName(\"java\", …)] or in the profile's Name hook"));
                        }
                        else
                            seen[erased] = member;
                    }
                }
            }
        }
    }
}

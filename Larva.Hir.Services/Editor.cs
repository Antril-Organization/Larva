namespace Larva.Hir.Services
{
    public enum CompletionKind : byte
    {
        Keyword, Namespace, Class, Struct, Interface, Enum, Field, Constant, EnumValue, Property, Method, Function, Constructor,
        Event, Local, Parameter, TypeParameter, Snippet,
    }

    /// <summary>One completion: what the list shows, what it inserts, and its signature and documentation.</summary>
    public sealed record CompletionItem(string Label, CompletionKind Kind, string? Detail = null, string? Doc = null)
    {
        public string InsertText { get; init; } = Label;

        /// <summary>How many overloads the label stands for (methods).</summary>
        public int Overloads { get; init; } = 1;

        /// <summary>Its place in the list (lower first): how well it matches, then its kind.</summary>
        public int Sort { get; init; }

        /// <summary>The namespace to import when it is chosen (a type of a namespace the document does not import yet).</summary>
        public string? Import { get; init; }
    }

    /// <summary>The list for one position, and the text it replaces (the word being written).</summary>
    public sealed record CompletionList(IReadOnlyList<CompletionItem> Items, int ReplaceStart, int ReplaceLength);

    /// <summary>A name in scope at a position (a local, a parameter, a type parameter).</summary>
    public sealed record ScopeSymbol(string Name, CompletionKind Kind, HirType Type, SourceSpot? Declared = null);

    /// <summary>What can be written at a position, as the language saw it.</summary>
    public abstract record CompletionContext
    {
        /// <summary>After <c>value.</c>: the members of a value of <paramref name="Type"/> (global), or of the type itself when <paramref name="Static"/>.</summary>
        public sealed record Members(HirType Type, bool Static) : CompletionContext;

        /// <summary>After <c>Some.Namespace.</c>: its namespaces, types and top-level functions.</summary>
        public sealed record Namespace(string Name) : CompletionContext;

        /// <summary>
        /// A name on its own: the locals in scope, the members of the enclosing type, the types and functions of the
        /// visible namespaces, keywords. <paramref name="TypesOnly"/> where only a type can go.
        /// </summary>
        public sealed record Scope(IReadOnlyList<ScopeSymbol> Locals, IReadOnlyList<string> Namespaces, bool TypesOnly = false, bool StaticOnly = false) : CompletionContext;

        /// <summary>Nothing to complete here (inside a comment or a string, a name being declared).</summary>
        public sealed record Nothing : CompletionContext;
    }

    /// <summary>A completion request: the context, the word written so far and where it is, the type it is written in.</summary>
    public sealed record CompletionRequest(CompletionContext Context, string Prefix, int ReplaceStart, int ReplaceLength)
    {
        /// <summary>The type the code is in (what decides which private and internal members are visible).</summary>
        public CatalogType? From { get; init; }

        /// <summary>Items only the language knows (its own members of arrays, nullables…), added to the list.</summary>
        public IReadOnlyList<CompletionItem> Extra { get; init; } = [];
    }

    public enum SymbolKind : byte { Local, Parameter, TypeParameter, Type, Member, Namespace }

    /// <summary>What a name at a position is.</summary>
    public sealed record SymbolInfo(SymbolKind Kind, string Name)
    {
        /// <summary>A local's or parameter's type, or a member's value type (global).</summary>
        public HirType? Type { get; init; }
        public CatalogType? CatalogType { get; init; }
        public CatalogMember? Member { get; init; }

        /// <summary>The member as seen through its receiver's type arguments.</summary>
        public MemberView? View { get; init; }

        /// <summary>Where a local or parameter is declared.</summary>
        public SourceSpot? Declared { get; init; }
    }

    /// <summary>A call being written: the methods it can call and which argument the position is in.</summary>
    public sealed record CallSite(IReadOnlyList<MemberView> Candidates, int ActiveParameter, int ArgumentCount);

    /// <summary>A declaration of the document (its outline).</summary>
    public sealed record DocumentSymbol(string Name, CompletionKind Kind, string Detail, int Line, int Column, IReadOnlyList<DocumentSymbol> Children);

    /// <summary>
    /// What a name is, for coloring it: the standard token types of the Language Server Protocol, and four of Larva's own
    /// that editors declare with a standard one as their parent (a theme that knows only the standard colors them as
    /// that): <see cref="Field"/> (property), <see cref="Exception"/> and <see cref="Attribute"/> (class),
    /// <see cref="Bitflags"/> (enum). A constant is the standard <see cref="Variable"/>, <c>readonly</c> and <c>static</c>.
    /// </summary>
    public enum SemanticKind : byte
    {
        Namespace, Type, Class, Enum, Interface, Struct, TypeParameter, Parameter, Variable, Property, EnumMember, Function, Method,
        /// <summary>A field (properties are <see cref="Property"/>).</summary>
        Field,
        /// <summary>A word with a meaning the grammar cannot see: the type groups of generic constraints (<c>T : copy</c>).</summary>
        Keyword,
        Event,
        /// <summary>A class derived from System.Exception (<c>exception NotFound</c>).</summary>
        Exception,
        /// <summary>A class derived from System.Attribute (<c>attribute Tag</c>), where it is named and where it is applied.</summary>
        Attribute,
        /// <summary>An enum of flags (<c>bitflag enum</c>).</summary>
        Bitflags,
    }

    [Flags]
    public enum SemanticModifiers : byte
    {
        None = 0,
        Declaration = 1 << 0,
        Static = 1 << 1,
        Readonly = 1 << 2,
        /// <summary>From a library (another assembly of the catalog), not the program.</summary>
        DefaultLibrary = 1 << 3,
    }

    /// <summary>A name in the document and what it is (offsets in the text; one line each).</summary>
    public readonly record struct SemanticToken(int Offset, int Length, SemanticKind Kind, SemanticModifiers Modifiers);

    /// <summary>The semantic kinds of catalog types and members, shared by every language's analysis.</summary>
    public static class Semantic
    {
        public static SemanticKind Of(CatalogType type) => type.Kind switch
        {
            // A value (`value`, `record value`) is a struct; a primitive's own type (Int32) reads as the type it gives.
            CatalogTypeKind.Struct => type.Primitive is null ? SemanticKind.Struct : SemanticKind.Type,
            CatalogTypeKind.Interface => SemanticKind.Interface,
            CatalogTypeKind.Enum => SemanticKind.Enum,
            CatalogTypeKind.Bitflags => SemanticKind.Bitflags,
            CatalogTypeKind.Module => SemanticKind.Namespace,
            _ => Derives(type, "System.Exception") ? SemanticKind.Exception : Derives(type, "System.Attribute") ? SemanticKind.Attribute : SemanticKind.Class,
        };

        /// <summary>Whether a class is <paramref name="root"/> or derives from it.</summary>
        private static bool Derives(CatalogType type, string root) =>
            type.FullName == root || type.BaseChain().Any(b => b.FullName == root);

        public static SemanticKind Of(CatalogMember member) => member.Kind switch
        {
            CatalogMemberKind.EnumValue => SemanticKind.EnumMember,
            CatalogMemberKind.Constant => SemanticKind.Variable,
            CatalogMemberKind.Field => SemanticKind.Field,
            CatalogMemberKind.Event => SemanticKind.Event,
            CatalogMemberKind.Property or CatalogMemberKind.Indexer => SemanticKind.Property,
            CatalogMemberKind.Constructor => Of(member.Type),
            _ => member.Type.IsModule ? SemanticKind.Function : SemanticKind.Method,
        };

        public static SemanticModifiers Modifiers(CatalogMember member, CatalogAssembly program)
        {
            var modifiers = SemanticModifiers.None;
            if (member.IsStatic && member.Kind != CatalogMemberKind.EnumValue && !member.Type.IsModule || member.Kind == CatalogMemberKind.Constant)
                modifiers |= SemanticModifiers.Static;
            if (member.Kind is CatalogMemberKind.Constant or CatalogMemberKind.EnumValue || member.Field is { } f && (f.Flags & FieldFlags.Readonly) != 0
                || member.Property is { Setter: < 0 })
                modifiers |= SemanticModifiers.Readonly;
            if (member.Type.Assembly != program)
                modifiers |= SemanticModifiers.DefaultLibrary;
            return modifiers;
        }
    }

    public enum ProblemSeverity : byte { Error, Warning }

    /// <summary>A diagnostic of the document (lines and columns from 1).</summary>
    public sealed record Problem(ProblemSeverity Severity, string Code, string Message, int Line, int Column, int EndLine, int EndColumn);

    /// <summary>
    /// What a language knows about one document once analyzed: what is at a position. The language implements it (it
    /// parses and binds its own syntax); <see cref="LanguageService"/> turns it into completion lists, hovers, definitions
    /// and signature help with the shared catalog and the language's view.
    /// </summary>
    public interface IDocumentAnalysis
    {
        HirCatalog Catalog { get; }

        CompletionRequest? CompletionAt(int offset);

        SymbolInfo? SymbolAt(int offset);

        CallSite? CallAt(int offset);

        IReadOnlyList<DocumentSymbol> Outline { get; }

        IReadOnlyList<Problem> Problems { get; }

        /// <summary>Every name of the document with what it is, in order, none overlapping.</summary>
        IReadOnlyList<SemanticToken> Tokens { get; }
    }

    public sealed record HoverInfo(string Signature, string? Doc, SourceSpot? Definition);

    public sealed record SignatureItem(string Label, string? Doc, IReadOnlyList<(int Start, int Length)> Parameters);

    public sealed record SignatureHelp(IReadOnlyList<SignatureItem> Signatures, int ActiveSignature, int ActiveParameter);

    /// <summary>Editor features for any language on Larva: its view writes the text, its analysis says what is where.</summary>
    public sealed class LanguageService(LanguageView view)
    {
        public LanguageView View { get; } = view;

        // ── Completion ─────────────────────────────────────

        public CompletionList Complete(IDocumentAnalysis document, int offset)
        {
            if (document.CompletionAt(offset) is not { } request)
                return new CompletionList([], offset, 0);
            var catalog = document.Catalog;
            var items = new Dictionary<string, CompletionItem>(StringComparer.Ordinal);
            void Add(CompletionItem item)
            {
                if (request.Prefix.Length > 0 && HirCatalog.Score(item.Label, request.Prefix) is null)
                    return;
                if (items.TryGetValue(item.Label, out var existing))
                {
                    if (existing.Kind is CompletionKind.Method or CompletionKind.Function && item.Kind == existing.Kind)
                        items[item.Label] = existing with { Overloads = existing.Overloads + 1 };
                    return;
                }
                items[item.Label] = item;
            }
            switch (request.Context)
            {
                case CompletionContext.Members members:
                    MembersOf(catalog, members.Type, members.Static, request.From, Add);
                    break;
                case CompletionContext.Namespace ns:
                    NamespaceContents(catalog, ns.Name, request.From, Add, typesOnly: false);
                    break;
                case CompletionContext.Scope scope:
                    foreach (var local in scope.Locals)
                        Add(new CompletionItem(local.Name, local.Kind, View.Local(local.Name, local.Type, catalog, local.Kind == CompletionKind.Parameter)));
                    if (!scope.TypesOnly && request.From is { } owner && !owner.IsModule)
                        MembersOf(catalog, owner.SelfType, scope.StaticOnly, owner, Add, includeStatic: true);
                    var namespaces = scope.Namespaces.Prepend("").Distinct().ToList();
                    foreach (var ns in namespaces)
                        NamespaceContents(catalog, ns, request.From, Add, scope.TypesOnly, nested: ns.Length == 0);
                    // The types of every other namespace, of the program and all its libraries: chosen, the namespace is imported.
                    var imported = new HashSet<string>(namespaces, StringComparer.Ordinal);
                    foreach (var type in catalog.AllTypes)
                    {
                        if (type.IsModule || type.IsHidden || type.Namespace.Length == 0 || imported.Contains(type.Namespace) || type.Name.Contains('.')
                            || items.ContainsKey(type.Name) || !Visible(type, request.From))
                            continue;
                        Add(new CompletionItem(type.Name, Kind(type), $"{View.Header(type)}   (import {type.Namespace})", type.Doc) { Import = type.Namespace });
                    }
                    foreach (var keyword in View.Keywords)
                        Add(new CompletionItem(keyword, CompletionKind.Keyword));
                    break;
            }
            foreach (var extra in request.Extra)
                Add(extra);
            // By kind first (what is closest first, keywords last; a type to import after those the document sees), then by
            // how well the written prefix matches, then by name.
            var sorted = items.Values
                .Select(i => i with { Sort = (i.Import is null ? Rank(i.Kind) : ImportRank) * 1000 + (request.Prefix.Length > 0 ? Math.Min(HirCatalog.Score(i.Label, request.Prefix) ?? 999, 999) : 0) })
                .OrderBy(i => i.Sort).ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase).ToList();
            return new CompletionList(sorted, request.ReplaceStart, request.ReplaceLength);
        }

        /// <summary>The order of the kinds in a list: locals, members, functions, constants, types, namespaces, keywords last.</summary>
        private static int Rank(CompletionKind kind) => kind switch
        {
            CompletionKind.Local or CompletionKind.Parameter => 0,
            CompletionKind.Field or CompletionKind.Property or CompletionKind.Event => 1,
            CompletionKind.Method => 2,
            CompletionKind.Function => 3,
            CompletionKind.Constant or CompletionKind.EnumValue => 4,
            CompletionKind.TypeParameter => 5,
            CompletionKind.Class => 6,
            CompletionKind.Struct => 7,
            CompletionKind.Interface => 8,
            CompletionKind.Enum => 9,
            CompletionKind.Constructor => 10,
            CompletionKind.Namespace => 12,
            CompletionKind.Snippet => 13,
            CompletionKind.Keyword => 14,
            _ => 13,
        };

        /// <summary>A type of a namespace not imported yet: after the document's own types, before namespaces.</summary>
        private const int ImportRank = 11;

        /// <summary>Whether code in <paramref name="from"/> can use <paramref name="member"/>.</summary>
        public static bool Visible(CatalogMember member, CatalogType? from)
        {
            // What an interface asks for is what every user of it calls.
            if (member.Type.IsInterface && !member.IsStatic)
                return true;
            switch (member.Access)
            {
                case Access.Public:
                    return true;
                case Access.Internal or Access.Friendly:
                    return from is null || from.Assembly == member.Type.Assembly;
                case Access.Inherited:
                    return from is not null && (from == member.Type || from.BaseChain().Contains(member.Type));
                default:
                    return from is not null && (from == member.Type || from.Assembly == member.Type.Assembly && member.Type.IsModule && from.Namespace == member.Type.Namespace);
            }
        }

        public static bool Visible(CatalogType type, CatalogType? from) =>
            type.Access is Access.Public || from is null || from.Assembly == type.Assembly;

        private void MembersOf(HirCatalog catalog, HirType type, bool isStatic, CatalogType? from, Action<CompletionItem> add, bool includeStatic = false)
        {
            if (catalog.TypeOf(type is NullableType { Inner: var inner } ? inner : type) is not { } target)
                return;
            var arguments = type switch
            {
                NamedType n => n.Arguments,
                NullableType { Inner: NamedType n } => n.Arguments,
                _ => [],
            };
            foreach (var view in target.Members(arguments))
            {
                var member = view.Member;
                if (member.Kind is CatalogMemberKind.Constructor or CatalogMemberKind.Operator or CatalogMemberKind.Conversion or CatalogMemberKind.Indexer)
                    continue;
                if (!includeStatic && member.IsStatic != isStatic || !Visible(member, from))
                    continue;
                add(Item(member, view));
            }
            // Extension methods: `ext(i32 n) { Twice() … }` gives an i32 `Twice()`; `static ext(i32)` gives `i32.F()`.
            foreach (var extension in catalog.ExtensionsOf(type))
            {
                if (extension.IsStatic != isStatic && !includeStatic)
                    continue;
                foreach (var member in extension.Declared)
                {
                    if (member.Kind != CatalogMemberKind.Method || !Visible(member, from))
                        continue;
                    add(new CompletionItem(member.Name, CompletionKind.Method, View.ExtensionSignature(member), member.Doc));
                }
            }
        }

        private void NamespaceContents(HirCatalog catalog, string ns, CatalogType? from, Action<CompletionItem> add, bool typesOnly, bool nested = true)
        {
            if (nested || ns.Length > 0)
            {
                foreach (var inner in catalog.NamespacesIn(ns))
                {
                    string last = inner[(inner.LastIndexOf('.') + 1)..];
                    add(new CompletionItem(last, CompletionKind.Namespace, $"namespace {inner}"));
                }
            }
            foreach (var type in catalog.TypesIn(ns))
            {
                if (!Visible(type, from))
                    continue;
                if (type.IsModule)
                {
                    if (typesOnly)
                        continue;
                    foreach (var member in type.Declared)
                        if (Visible(member, from) && member.Kind is not CatalogMemberKind.Constructor)
                            add(Item(member, null) with { Kind = member.Kind == CatalogMemberKind.Method ? CompletionKind.Function : Kind(member) });
                    continue;
                }
                add(new CompletionItem(type.Name, Kind(type), View.Header(type), type.Doc));
            }
        }

        private CompletionItem Item(CatalogMember member, MemberView? view) =>
            new(member.Name, Kind(member), View.Signature(member, view), member.Doc);

        public static CompletionKind Kind(CatalogType type) => type.Kind switch
        {
            CatalogTypeKind.Struct => CompletionKind.Struct,
            CatalogTypeKind.Interface => CompletionKind.Interface,
            CatalogTypeKind.Enum or CatalogTypeKind.Bitflags => CompletionKind.Enum,
            _ => CompletionKind.Class,
        };

        public static CompletionKind Kind(CatalogMember member) => member.Kind switch
        {
            CatalogMemberKind.Field => CompletionKind.Field,
            CatalogMemberKind.Constant => CompletionKind.Constant,
            CatalogMemberKind.EnumValue => CompletionKind.EnumValue,
            CatalogMemberKind.Property or CatalogMemberKind.Indexer => CompletionKind.Property,
            CatalogMemberKind.Constructor => CompletionKind.Constructor,
            CatalogMemberKind.Event => CompletionKind.Event,
            _ => member.Type.IsModule ? CompletionKind.Function : CompletionKind.Method,
        };

        // ── Hover, definition, signatures ──────────────────

        public HoverInfo? Hover(IDocumentAnalysis document, int offset)
        {
            if (document.SymbolAt(offset) is not { } symbol)
                return null;
            var catalog = document.Catalog;
            return symbol.Kind switch
            {
                SymbolKind.Local or SymbolKind.Parameter => new HoverInfo(View.Local(symbol.Name, symbol.Type ?? HirType.Void, catalog, symbol.Kind == SymbolKind.Parameter), null, symbol.Declared),
                SymbolKind.TypeParameter => new HoverInfo(symbol.Name, null, symbol.Declared),
                SymbolKind.Type when symbol.CatalogType is { } type => new HoverInfo(View.Header(type), type.Doc, type.Location),
                SymbolKind.Member when symbol.Member is { } member => new HoverInfo(View.Signature(member, symbol.View), member.Doc, member.Location),
                SymbolKind.Namespace => new HoverInfo($"namespace {symbol.Name}", null, null),
                _ => null,
            };
        }

        /// <summary>Where the symbol at <paramref name="offset"/> is declared (null: nowhere a source file says).</summary>
        public SourceSpot? Definition(IDocumentAnalysis document, int offset) => document.SymbolAt(offset) switch
        {
            { Declared: { } spot } => spot,
            { Member: { } member } => member.Location,
            { CatalogType: { } type } => type.Location,
            _ => null,
        };

        public SignatureHelp? Signatures(IDocumentAnalysis document, int offset)
        {
            if (document.CallAt(offset) is not { } call || call.Candidates.Count == 0)
                return null;
            var catalog = document.Catalog;
            var items = new List<SignatureItem>();
            int active = 0;
            for (int c = 0; c < call.Candidates.Count; c++)
            {
                var view = call.Candidates[c];
                string label = View.Signature(view.Member, view);
                var names = GenericNames.Of(view.Member.Type, view.Member);
                var ranges = new List<(int, int)>();
                int from = Math.Max(0, label.IndexOf('('));
                foreach (var parameter in view.Parameters)
                {
                    string text = View.Parameter(parameter, catalog, names);
                    int at = label.IndexOf(text, from, StringComparison.Ordinal);
                    if (at < 0)
                        continue;
                    ranges.Add((at, text.Length));
                    from = at + text.Length;
                }
                items.Add(new SignatureItem(label, view.Member.Doc, ranges));
            }
            // The overload the arguments so far fit best: as many parameters, else the fewest more.
            int best = int.MaxValue;
            for (int c = 0; c < call.Candidates.Count; c++)
            {
                int extra = call.Candidates[c].Parameters.Length - call.ArgumentCount;
                if (extra >= 0 && extra < best)
                {
                    best = extra;
                    active = c;
                }
            }
            return new SignatureHelp(items, active, call.ActiveParameter);
        }
    }
}

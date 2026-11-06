using System.Text;

namespace Larva.Hir.Services
{
    /// <summary>The names type parameters have where a type is written: the type's, then the method's.</summary>
    public readonly record struct GenericNames(IReadOnlyList<string> Type, IReadOnlyList<string> Method)
    {
        public static readonly GenericNames None = new([], []);

        public static GenericNames Of(CatalogType? type, CatalogMember? member = null) =>
            new(type?.Definition.Generics.Select(g => g.Name).ToArray() ?? [], member?.Generics.Select(g => g.Name).ToArray() ?? []);

        public string Name(GenericParamType g)
        {
            var list = g.Owner == GenericOwner.Type ? Type : Method;
            return g.Index < list.Count ? list[g.Index] : (g.Owner == GenericOwner.Type ? "T" : "M") + g.Index;
        }
    }

    public sealed record DecompileOptions
    {
        public static readonly DecompileOptions Default = new();

        /// <summary>Method bodies (else declarations only, as a reference page).</summary>
        public bool Bodies { get; init; } = true;

        /// <summary>Private and internal members too (else only what other code can use).</summary>
        public bool NonPublic { get; init; } = true;

        /// <summary>The documentation comments.</summary>
        public bool Docs { get; init; } = true;
    }

    /// <summary>
    /// How one language writes what a <see cref="HirCatalog"/> holds: type names, declarations, member signatures, and a
    /// type "decompiled" to that language's source (from its HIR, whatever language it was written in). Each frontend on
    /// Larva provides one; tools (the CLI, editors, documentation) show catalogs through it.
    /// </summary>
    public abstract class LanguageView
    {
        /// <summary>The language's name (<c>hir</c>, or a language pack's id).</summary>
        public abstract string Name { get; }

        /// <summary>Its source files' extension (<c>.ant</c>).</summary>
        public abstract string Extension { get; }

        /// <summary>A global type (<see cref="HirCatalog.Globalize"/>) as the language writes it.</summary>
        public abstract string Type(HirType type, HirCatalog catalog, GenericNames names);

        /// <summary>A type's declaration line (<c>public final class String : IComparable&lt;str&gt;</c>).</summary>
        public abstract string Header(CatalogType type);

        /// <summary>A member's declaration in one line, seen through <paramref name="view"/>'s type arguments when given.</summary>
        public abstract string Signature(CatalogMember member, MemberView? view = null);

        /// <summary>An extension method as the receiver sees it (without the receiver parameter and `static`).</summary>
        public virtual string ExtensionSignature(CatalogMember member) => Signature(member);

        /// <summary>The whole type as the language's source.</summary>
        public abstract string Decompile(CatalogType type, DecompileOptions? options = null);

        /// <summary>A local or a parameter as its declaration writes it.</summary>
        public virtual string Local(string name, HirType type, HirCatalog catalog, bool parameter) =>
            $"{(parameter ? "parameter" : "local")} {name}: {Type(type, catalog, GenericNames.None)}";

        /// <summary>A parameter as a signature writes it (signature help highlights it there).</summary>
        public virtual string Parameter(Parameter parameter, HirCatalog catalog, GenericNames names) =>
            $"{parameter.Name}: {Type(parameter.Type, catalog, names)}";

        /// <summary>The language's keywords (what completion offers where a statement or an expression starts).</summary>
        public virtual IReadOnlyList<string> Keywords => [];

        /// <summary>A word for a type's kind (<c>class</c>, <c>interface</c>…).</summary>
        public virtual string KindWord(CatalogType type) => type.Kind.ToString().ToLowerInvariant();

        /// <summary>A namespace's listing: its namespaces and types, each with its header.</summary>
        public virtual string Listing(HirCatalog catalog, string ns)
        {
            var text = new StringBuilder();
            text.Append(ns.Length == 0 ? "(global)" : ns).Append('\n');
            foreach (var inner in catalog.NamespacesIn(ns))
                text.Append("  namespace ").Append(inner).Append('\n');
            foreach (var type in catalog.TypesIn(ns))
            {
                if (type.IsModule)
                {
                    foreach (var member in type.Declared)
                        text.Append("  ").Append(Signature(member)).Append('\n');
                    continue;
                }
                text.Append("  ").Append(Header(type)).Append('\n');
            }
            foreach (var extension in catalog.ExtensionsIn(ns))
                text.Append("  ").Append(Header(extension)).Append('\n');
            return text.ToString();
        }

        /// <summary>The documentation of a type or member as plain text lines, prefixed for a source comment.</summary>
        protected static void Doc(StringBuilder text, string? doc, string indent, string prefix = "/// ")
        {
            if (doc is null)
                return;
            foreach (var line in doc.Split('\n'))
                text.Append(indent).Append(prefix).Append(line).Append('\n');
        }
    }

    /// <summary>The views tools can show a catalog through, by name or by source extension.</summary>
    public static class LanguageViews
    {
        private static readonly List<LanguageView> views = [new HirView()];

        public static IReadOnlyList<LanguageView> All
        {
            get
            {
                lock (views)
                    return [.. views];
            }
        }

        public static void Register(LanguageView view)
        {
            lock (views)
            {
                views.RemoveAll(v => v.Name == view.Name);
                views.Add(view);
            }
        }

        /// <summary>The view named <paramref name="name"/> (or whose extension it is), or null.</summary>
        public static LanguageView? Get(string name)
        {
            lock (views)
                return views.Find(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || v.Extension.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || v.Extension.Equals("." + name, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The language-neutral view: Larva HIR's own text syntax (what <c>Text.HirPrinter</c> writes).</summary>
    public sealed class HirView : LanguageView
    {
        public override string Name => "hir";
        public override string Extension => ".hir";

        public override string Type(HirType type, HirCatalog catalog, GenericNames names) => type switch
        {
            PrimitiveType p => p.Kind.Keyword(),
            NamedType n => (catalog.TypeOf(n) is { } t ? t.FullName : "?")
                + (n.Arguments.Length > 0 ? $"<{string.Join(", ", n.Arguments.Select(a => Type(a, catalog, names)))}>" : ""),
            ArrayType a => $"{Type(a.Element, catalog, names)}[{new string(',', a.Rank - 1)}]",
            InlineArrayType i => $"fixed {Type(i.Element, catalog, names)}[{(i.SizeOf is null ? i.Count.ToString() : (i.Count == 1 ? "" : $"{i.Count} * ") + $"sizeof({Type(i.SizeOf, catalog, names)})")}]",
            NullableType n => Type(n.Inner, catalog, names) + "?",
            ReferenceType r => (r.Mutable ? "&mut " : "&") + Type(r.Target, catalog, names),
            PointerType p => Type(p.Target, catalog, names) + (p.Mutable ? "*" : " const*"),
            FuncType f => $"func<{string.Join(", ", f.Parameters.Select(x => Type(x, catalog, names)))}> : {Type(f.Result, catalog, names)}",
            NativeFuncType f => $"func*<{string.Join(", ", f.Parameters.Select(x => Type(x, catalog, names)))}> : {Type(f.Result, catalog, names)}",
            GenericParamType g => names.Name(g),
            PackExpansionType e => Type(e.Pattern, catalog, names) + "...",
            _ => type.ToString(),
        };

        public override string Header(CatalogType type)
        {
            var names = GenericNames.Of(type);
            var text = new StringBuilder();
            text.Append(type.Access.ToString().ToLowerInvariant()).Append(' ').Append(type.Kind.ToString().ToLowerInvariant()).Append(' ').Append(type.FullName);
            if (type.Arity > 0)
                text.Append('<').Append(string.Join(", ", type.Definition.Generics.Select(g => g.Name))).Append('>');
            var bases = (type.BaseType is { } b ? [b] : Array.Empty<HirType>()).Concat(type.InterfaceTypes).Select(t => Type(t, type.Catalog, names)).ToList();
            if (bases.Count > 0)
                text.Append(" : ").Append(string.Join(", ", bases));
            return text.ToString();
        }

        public override string Signature(CatalogMember member, MemberView? view = null)
        {
            var catalog = member.Type.Catalog;
            var names = GenericNames.Of(member.Type, member);
            var value = view?.ValueType ?? member.ValueType;
            var parameters = view?.Parameters ?? [.. member.Parameters];
            string Params() => string.Join(", ", parameters.Select(p => $"{p.Name}: {Type(p.Type, catalog, names)}"));
            string prefix = (member.IsStatic ? "static " : "") + member.Kind.ToString().ToLowerInvariant() + " ";
            return member.Kind switch
            {
                CatalogMemberKind.Constructor => $"{prefix}{member.Type.Name}({Params()})",
                CatalogMemberKind.Method or CatalogMemberKind.Operator or CatalogMemberKind.Conversion =>
                    $"{prefix}{member.Name}{(member.Generics.Count > 0 ? $"<{string.Join(", ", member.Generics.Select(g => g.Name))}>" : "")}({Params()}) -> {Type(value, catalog, names)}",
                CatalogMemberKind.Indexer => $"{prefix}this[{Params()}]: {Type(value, catalog, names)}",
                _ => $"{prefix}{member.Name}: {Type(value, catalog, names)}",
            };
        }

        public override string Decompile(CatalogType type, DecompileOptions? options = null)
        {
            options ??= DecompileOptions.Default;
            var text = new StringBuilder();
            Doc(text, options.Docs ? type.Doc : null, "", "// ");
            text.Append(Header(type)).Append("\n{\n");
            foreach (var member in type.Declared)
            {
                if (!options.NonPublic && member.Access is not Access.Public)
                    continue;
                Doc(text, options.Docs ? member.Doc : null, "    ", "// ");
                text.Append("    ").Append(Signature(member)).Append('\n');
            }
            text.Append("}\n");
            return text.ToString();
        }
    }
}

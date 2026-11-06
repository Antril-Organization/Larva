namespace Larva.Hir.Services
{
    /// <summary>
    /// <c>larva types</c>, <c>larva show</c> and <c>larva search</c> over a catalog, through one language's view: the
    /// same text whether the view is Larva's own (HIR) or a language's (run inside its language pack).
    /// </summary>
    public static class CatalogCommands
    {
        /// <summary>The command's output written to <paramref name="output"/>, problems to <paramref name="error"/>: its exit code.</summary>
        public static int Run(string command, HirCatalog catalog, LanguageView view, IReadOnlyList<string> words, DecompileOptions options,
            TextWriter output, TextWriter error)
        {
            switch (command)
            {
                case "types":
                    Types(catalog, view, words.FirstOrDefault(), output);
                    return 0;
                case "show":
                {
                    if (words.Count == 0)
                    {
                        error.WriteLine("larva show: which type? (larva show System.List)");
                        return 2;
                    }
                    foreach (var name in words)
                    {
                        if (catalog.Find(name) is { } type)
                            output.WriteLine(view.Decompile(type, options));
                        else if (catalog.IsNamespace(name))
                            output.WriteLine(view.Listing(catalog, name));
                        else
                        {
                            error.WriteLine($"larva show: no type `{name}`");
                            var hits = catalog.Search(name[(name.LastIndexOf('.') + 1)..], 5, members: false);
                            if (hits.Count > 0)
                                error.WriteLine("  did you mean: " + string.Join(", ", hits.Select(h => h.FullName)));
                            return 1;
                        }
                    }
                    return 0;
                }
                default:
                {
                    string query = string.Join(" ", words);
                    foreach (var hit in catalog.Search(query, 40))
                    {
                        if (hit.Member is { } member)
                            output.WriteLine($"{hit.Type.FullName}.{view.Signature(member)}");
                        else
                            output.WriteLine($"{hit.Type.Namespace}: {view.Header(hit.Type)}");
                    }
                    return 0;
                }
            }
        }

        private static void Types(HirCatalog catalog, LanguageView view, string? ns, TextWriter output)
        {
            if (ns is not null)
            {
                output.Write(view.Listing(catalog, ns));
                return;
            }
            foreach (var name in catalog.Namespaces)
            {
                if (catalog.TypesIn(name).Count == 0)
                    continue;
                output.Write(view.Listing(catalog, name));
                output.WriteLine();
            }
        }
    }
}

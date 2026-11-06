using Larva.IR.Lowering;
using Larva.IR.Text;

namespace Larva.IR.Linking
{
    public sealed record LinkOptions
    {
        public static readonly LinkOptions Default = new();

        /// <summary>
        /// Generics, reflection, optimization and GC settings for the linked program: its GC is domain 0, the one every
        /// object must have been checked for unless <see cref="MixedDomains"/> allows others.
        /// </summary>
        public CompilationOptions Compilation { get; init; } = CompilationOptions.Default;

        /// <summary>
        /// Lets objects checked for other GC modes in: each distinct mode becomes a domain of the linked program
        /// (<see cref="IrModule.Domains"/>), and every function and struct records its domain. The back ends still compile the
        /// whole program for domain 0, so this is for building the domain machinery (the design's stage 1), not for programs yet.
        /// </summary>
        public bool MixedDomains { get; init; }

        /// <summary>Entry function: <c>module::name</c>, or a bare name that exactly one module defines.</summary>
        public string Entry { get; init; } = "main";

        /// <summary>Building a library: every export is kept (and no entry is required).</summary>
        public bool Library { get; init; }

        /// <summary>
        /// Building a native library for C: the functions with a C name (<c>Larva.Export</c>) are what it starts from, and
        /// no entry is required.
        /// </summary>
        public bool CLibrary { get; init; }

        /// <summary>
        /// Whether the linked program is also lowered to the VM's IL. Native and WebAssembly builds compile
        /// <see cref="LinkResult.Merged"/> themselves and skip it (on a large program it is most of the link).
        /// </summary>
        public bool LowerToIl { get; init; } = true;
    }

    public sealed record LinkError(string Symbol, string Message)
    {
        public override string ToString() => $"link error: {Message}\n   --> {Symbol}";
    }

    public sealed record LinkResult(IlModule? Il, IReadOnlyList<LinkError> Errors)
    {
        /// <summary>The whole program after symbol resolution (before monomorphization).</summary>
        public IrModule? Merged { get; init; }

        /// <summary>Functions of <see cref="Merged"/> the program starts from (entry, kept exports, reflected functions).</summary>
        public IReadOnlySet<int> Roots { get; init; } = new HashSet<int>();

        /// <summary>Qualified name of the entry function, if any.</summary>
        public string? Entry { get; init; }

        public bool Success => Il is not null || IlSkipped && Merged is not null && Errors.Count == 0;

        /// <summary>Linked without lowering to IL (<see cref="LinkOptions.LowerToIl"/> off).</summary>
        public bool IlSkipped { get; init; }
    }

    /// <summary>
    /// Links verified modules into one program (whole-program, at the IR level):
    /// <list type="number">
    /// <item>module names are unique and every object was checked for the same GC mode;</item>
    /// <item>every struct and function gets a global identity (<c>module::name</c>); imports resolve to exports;</item>
    /// <item>an imported struct must have the exported struct's exact shape, an imported function its exact signature
    /// (types, generics and constraints, region annotations, <c>unsafe</c>) — call sites were checked against the import,
    /// so this makes them sound against the definition;</item>
    /// <item>bodies are rewritten to global ids, and the program is monomorphized from the roots (entry, exports when
    /// building a library, reflected functions): generic definitions from any module are instantiated once, unreachable
    /// code is dropped.</item>
    /// </list>
    /// </summary>
    public static class Linker
    {
        public static LinkResult Link(IEnumerable<LarvaObject> objects, LinkOptions? options = null) =>
            new Session([.. objects], options ?? LinkOptions.Default).Run();

        private sealed class Session(LarvaObject[] objects, LinkOptions options)
        {
            private readonly List<LinkError> errors = [];
            private readonly List<StructDef> structs = [];
            private readonly List<IrFunction?> functions = [];
            private readonly Dictionary<string, int> exportedStructs = [];
            private readonly Dictionary<string, int> exportedFunctions = [];
            private readonly Dictionary<string, int> internalSymbols = [];
            private int[][] structMap = [];
            private int[][] functionMap = [];
            private int[][] globalMap = [];
            private readonly List<GlobalDef> globals = [];
            private readonly Dictionary<string, int> exportedGlobals = [];
            // Link-once items: the module that defines the kept copy, and the copies other modules carry (checked equal).
            private readonly Dictionary<int, int> structOwner = [], functionOwner = [];
            private readonly List<(int Module, int Index)> repeatedStructs = [], repeatedFunctions = [];
            // GC domains: the program's GC first, then every other mode an object was checked for; each object's domain.
            private readonly List<GcConfig> domains = [];
            private int[] domainOf = [];

            /// <summary>Whether a type holds no handle, pointer or borrow (and no struct, which could): the same bytes in every GC mode.</summary>
            private static bool HandleFree(IrType type) => type.Walk().All(t => t is not (GcType or BoxType or PointerType or ReferenceType or StructType));

            private static bool HandleFree(StructDef def) => def.Fields.All(HandleFree);

            /// <summary>A function whose code is the same in every GC mode: no handles in its locals, no allocation, no region.</summary>
            private static bool HandleFree(IrFunction function) =>
                function.Locals.All(l => HandleFree(l.Type))
                && function.Blocks.All(b => b.Statements.All(s => s is not (RegionEnterStatement or RegionExitStatement or FreeStatement)
                    && (s is not AssignStatement a || a.Value is not (AllocRvalue or ArrayRvalue))));

            /// <summary>
            /// The key a link-once item is kept under: shared (<c>*::name</c>) by the domains whose objects have the same
            /// shape (every domain with a collector uses <c>gc</c> handles; a Manual domain's objects are raw pointers, so it
            /// gets its own <c>*@N::name</c> copies), and by every domain when handle-free. The shared copy is the program's
            /// (domain 0): what it allocates belongs there. With one domain the key is the plain <c>*::name</c> of before.
            /// </summary>
            private string OnceKey(string name, int domain, bool handleFree) =>
                domain == 0 || handleFree || domains[domain].ManagesObjects == domains[0].ManagesObjects ? $"*::{name}" : $"*@{domain}::{name}";

            public LinkResult Run()
            {
                CheckModules();
                if (errors.Count > 0)
                    return new LinkResult(null, errors);

                structMap = [.. objects.Select(o => new int[o.Module.Structs.Length])];
                functionMap = [.. objects.Select(o => new int[o.Module.Functions.Length])];
                globalMap = [.. objects.Select(o => new int[o.Module.Globals.Length])];
                DefineSymbols();
                ResolveImports();
                if (errors.Count > 0)
                    return new LinkResult(null, errors);

                RewriteDefinitions();
                CheckImportShapes();
                CheckRepeatedShapes();
                if (errors.Count > 0)
                    return new LinkResult(null, errors);

                var merged = new IrModule([.. structs], [.. functions.Select(f => f!)])
                {
                    Domains = [.. domains],
                    Data = [.. objects.SelectMany(o => o.Module.Data)],
                    Globals = [.. globals],
                    // Link-once implementations appear in several modules: once each.
                    Implementations = [.. objects.SelectMany((o, m) => o.Module.Implementations.Select(i => i with { Function = functionMap[m][i.Function] })).Distinct()],
                };
                var roots = Roots(merged, out string? entry);
                // Regions across domains: what a region's code hands to another domain, checked with every domain known.
                foreach (var error in Analysis.RegionChecker.CheckMixed(merged, options.Compilation.Gc))
                    errors.Add(new(error.Item, error.Message));
                if (errors.Count > 0)
                    return new LinkResult(null, errors);

                if (!options.LowerToIl)
                    return new LinkResult(null, errors) { Merged = merged, Roots = roots, Entry = entry, IlSkipped = true };
                var result = Compiler.Lower(merged, options.Compilation with { Entry = entry ?? "" }, [], roots);
                return new LinkResult(result.Il, errors) { Merged = merged, Roots = roots, Entry = entry };
            }

            // ── 1. Modules ─────────────────────────────────────────

            private void CheckModules()
            {
                foreach (var group in objects.GroupBy(o => o.Module.Name))
                {
                    if (group.Key.Length == 0)
                        errors.Add(new("<module>", "every linked module needs a name"));
                    else if (group.Count() > 1)
                        errors.Add(new(group.Key, $"module `{group.Key}` is linked more than once"));
                }
                // Domain 0 is the program's GC; an object checked for another mode joins (or opens) that mode's domain.
                var gc = options.Compilation.Gc;
                domains.Add(gc);
                domainOf = new int[objects.Length];
                for (int m = 0; m < objects.Length; m++)
                {
                    var obj = objects[m];
                    int domain = domains.FindIndex(d => d.Mode == obj.Gc.Mode && d.Capabilities == obj.Gc.Capabilities && d.PluginName == obj.Gc.PluginName);
                    if (domain < 0)
                    {
                        if (!options.MixedDomains)
                        {
                            errors.Add(new(obj.Module.Name, $"module `{obj.Module.Name}` was checked for GC mode {obj.Gc}, but the program uses {gc}"));
                            continue;
                        }
                        domain = domains.Count;
                        domains.Add(obj.Gc);
                    }
                    domainOf[m] = domain;
                }
            }

            // ── 2. Symbols ─────────────────────────────────────────

            private void DefineSymbols()
            {
                for (int m = 0; m < objects.Length; m++)
                {
                    var module = objects[m].Module;
                    for (int g = 0; g < module.Globals.Length; g++)
                    {
                        var global = module.Globals[g];
                        if (global.Linkage == Linkage.Import)
                            continue;
                        globalMap[m][g] = globals.Count;
                        if (global.Linkage == Linkage.Export)
                            exportedGlobals[$"{module.Name}::{global.Name}"] = globals.Count;
                        globals.Add(global);
                    }
                    for (int s = 0; s < module.Structs.Length; s++)
                    {
                        var def = module.Structs[s];
                        if (def.Linkage == Linkage.Import)
                            continue;
                        if (def.Linkage == Linkage.LinkOnce)
                        {
                            string once = OnceKey(def.Name, domainOf[m], HandleFree(def));
                            if (exportedStructs.TryGetValue(once, out int kept))
                            {
                                structMap[m][s] = kept;
                                repeatedStructs.Add((m, s));
                                continue;
                            }
                            structMap[m][s] = structs.Count;
                            structOwner[structs.Count] = m;
                            exportedStructs[once] = structs.Count;
                            structs.Add(def with { Name = once, Symbol = null, Domain = domainOf[m] });
                            continue;
                        }
                        string symbol = $"{module.Name}::{def.Name}";
                        structMap[m][s] = structs.Count;
                        (def.Linkage == Linkage.Export ? exportedStructs : internalSymbols)[symbol] = structs.Count;
                        structs.Add(def with { Name = symbol, Symbol = null, Domain = domainOf[m] });
                    }
                    for (int f = 0; f < module.Functions.Length; f++)
                    {
                        var function = module.Functions[f];
                        if (function.Linkage == Linkage.Import)
                            continue;
                        if (function.Linkage == Linkage.LinkOnce)
                        {
                            string once = OnceKey(function.Name, domainOf[m], HandleFree(function));
                            if (exportedFunctions.TryGetValue(once, out int kept))
                            {
                                functionMap[m][f] = kept;
                                repeatedFunctions.Add((m, f));
                                continue;
                            }
                            functionMap[m][f] = functions.Count;
                            functionOwner[functions.Count] = m;
                            exportedFunctions[once] = functions.Count;
                            functions.Add(function with { Name = once, Domain = domainOf[m] });
                            continue;
                        }
                        string symbol = $"{module.Name}::{function.Name}";
                        functionMap[m][f] = functions.Count;
                        (function.Linkage == Linkage.Export ? exportedFunctions : internalSymbols)[symbol] = functions.Count;
                        functions.Add(function with { Name = symbol, Domain = domainOf[m] });
                    }
                }
            }

            private void ResolveImports()
            {
                for (int m = 0; m < objects.Length; m++)
                {
                    var module = objects[m].Module;
                    for (int g = 0; g < module.Globals.Length; g++)
                    {
                        if (module.Globals[g].Linkage == Linkage.Import)
                            globalMap[m][g] = Resolve(module.Globals[g].Symbol!, exportedGlobals, $"{module.Name}: global {module.Globals[g].Name}");
                    }
                    for (int s = 0; s < module.Structs.Length; s++)
                    {
                        if (module.Structs[s].Linkage == Linkage.Import)
                            structMap[m][s] = Resolve(module.Structs[s].Symbol!, exportedStructs, $"{module.Name}: struct {module.Structs[s].Name}");
                    }
                    for (int f = 0; f < module.Functions.Length; f++)
                    {
                        if (module.Functions[f].Linkage == Linkage.Import)
                            functionMap[m][f] = Resolve(module.Functions[f].Symbol!, exportedFunctions, $"{module.Name}: fn {module.Functions[f].Name}");
                    }
                }
            }

            private int Resolve(string symbol, Dictionary<string, int> exports, string user)
            {
                if (exports.TryGetValue(symbol, out int id))
                    return id;
                errors.Add(new(user, internalSymbols.ContainsKey(symbol)
                    ? $"`{symbol}` exists but is not exported"
                    : $"unresolved symbol `{symbol}`"));
                return -1;
            }

            // ── 3. Rewriting ───────────────────────────────────────

            private void RewriteDefinitions()
            {
                // Globals: this module's own (internal or exported) get storage; imports point at the exporter's.
                for (int m = 0; m < objects.Length; m++)
                {
                    var module = objects[m].Module;
                    for (int g = 0; g < module.Globals.Length; g++)
                    {
                        var global = module.Globals[g];
                        if (global.Linkage == Linkage.Import)
                            continue;
                        globals[globalMap[m][g]] = global with { Type = MapType(global.Type, m), Name = $"{module.Name}::{global.Name}", Linkage = Linkage.Internal, Symbol = null };
                    }
                }
                for (int m = 0; m < objects.Length; m++)
                {
                    var module = objects[m].Module;
                    for (int s = 0; s < module.Structs.Length; s++)
                    {
                        if (module.Structs[s].Linkage == Linkage.Import || structOwner.TryGetValue(structMap[m][s], out int owner) && owner != m
                            || module.Structs[s].Linkage == Linkage.LinkOnce && !structOwner.ContainsKey(structMap[m][s]))
                            continue;
                        int id = structMap[m][s];
                        structs[id] = structs[id] with
                        {
                            Fields = [.. structs[id].Fields.Select(t => MapType(t, m))],
                            Generics = MapGenerics(structs[id].Generics, m),
                            Base = structs[id].Base is { } baseType ? (StructType)MapType(baseType, m) : null,
                        };
                    }
                    for (int f = 0; f < module.Functions.Length; f++)
                    {
                        if (module.Functions[f].Linkage == Linkage.Import || functionOwner.TryGetValue(functionMap[m][f], out int owner) && owner != m)
                            continue;
                        int id = functionMap[m][f];
                        functions[id] = Rewrite(functions[id]!, m);
                    }
                }
            }

            private IdMap[]? ids;

            private IdMap Ids(int m) => (ids ??= [.. objects.Select((_, i) =>
            {
                // Static data of all objects is concatenated in order: this object's indices move by what comes before it.
                int dataBase = objects.Take(i).Sum(o => o.Module.Data.Length);
                return new IdMap(s => structMap[i][s], f => functionMap[i][f], g => globalMap[i][g], d => d + dataBase);
            })])[m];

            private IrFunction Rewrite(IrFunction function, int m) => Ids(m).Function(function);

            private IrType MapType(IrType type, int m) => Ids(m).Type(type);

            private GenericParam[] MapGenerics(GenericParam[] generics, int m) => Ids(m).Generics(generics);

            // ── 4. Import declarations against definitions ─────────

            private void CheckImportShapes()
            {
                var merged = new IrModule([.. structs], []);
                for (int m = 0; m < objects.Length; m++)
                {
                    var module = objects[m].Module;
                    for (int s = 0; s < module.Structs.Length; s++)
                    {
                        var declared = module.Structs[s];
                        if (declared.Linkage != Linkage.Import)
                            continue;
                        var defined = structs[structMap[m][s]];
                        bool same = declared.Fields.Select(t => MapType(t, m)).SequenceEqual(defined.Fields)
                            && SameGenerics(MapGenerics(declared.Generics, m), defined.Generics, merged);
                        if (!same)
                            errors.Add(new($"{module.Name}: struct {declared.Name}",
                                $"imported struct `{declared.Name}` does not match `{declared.Symbol}`: " +
                                $"{{ {Fields(declared.Fields.Select(t => MapType(t, m)), merged)} }} vs {{ {Fields(defined.Fields, merged)} }}"));
                    }
                    for (int f = 0; f < module.Functions.Length; f++)
                    {
                        var declared = module.Functions[f];
                        if (declared.Linkage != Linkage.Import)
                            continue;
                        var defined = functions[functionMap[m][f]]!;
                        if (Signature(Rewrite(declared, m), merged) is var a && Signature(defined, merged) is var b && a != b)
                            errors.Add(new($"{module.Name}: fn {declared.Name}",
                                $"imported `{declared.Name}` does not match `{declared.Symbol}`:\n      declared: {a}\n      defined:  {b}"));
                    }
                }
            }

            /// <summary>Every copy of a link-once item has the kept one's shape (structs) or signature (functions).</summary>
            private void CheckRepeatedShapes()
            {
                var merged = new IrModule([.. structs], []);
                foreach (var (m, s) in repeatedStructs)
                {
                    var copy = objects[m].Module.Structs[s];
                    var kept = structs[structMap[m][s]];
                    if (!copy.Fields.Select(t => MapType(t, m)).SequenceEqual(kept.Fields) || !SameGenerics(MapGenerics(copy.Generics, m), kept.Generics, merged))
                        errors.Add(new($"{objects[m].Module.Name}: struct {copy.Name}", $"link-once struct `{copy.Name}` differs between modules"));
                }
                foreach (var (m, f) in repeatedFunctions)
                {
                    var copy = objects[m].Module.Functions[f];
                    var kept = functions[functionMap[m][f]]!;
                    if (Signature(Rewrite(copy with { Blocks = [] }, m), merged) != Signature(kept, merged))
                        errors.Add(new($"{objects[m].Module.Name}: fn {copy.Name}", $"link-once `{copy.Name}` differs between modules: " +
                            $"`{Signature(Rewrite(copy with { Blocks = [] }, m), merged)}` and `{Signature(kept, merged)}`"));
                }
            }

            private static string Fields(IEnumerable<IrType> fields, IrModule module) =>
                string.Join(", ", fields.Select(t => IrPrinter.FormatType(t, module)));

            private static bool SameGenerics(GenericParam[] a, GenericParam[] b, IrModule module) =>
                a.Length == b.Length && a.Zip(b).All(p =>
                    IrPrinter.FormatConstraint(p.First.Constraint, module, a) == IrPrinter.FormatConstraint(p.Second.Constraint, module, b));

            /// <summary>Everything a caller relies on, as text: generics with constraints, regions, argument and return types, unsafe.</summary>
            private static string Signature(IrFunction function, IrModule module)
            {
                string Region(LocalDecl local) => local.Region is int r ? $" in '{r}" : "";
                var generics = string.Join(", ", function.Generics.Select((g, i) => $"T{i}: {IrPrinter.FormatConstraint(g.Constraint, module)}"));
                var arguments = Enumerable.Range(1, function.ArgumentCount)
                    .Select(a => $"{IrPrinter.FormatType(function.Locals[a].Type, module)}{Region(function.Locals[a])}");
                // Not whether it is unsafe: that is about its body (a safe function may hold an `unsafe` block), not its callers.
                return $"fn<{generics}; regions {function.RegionParams.Length}>" +
                       $"({string.Join(", ", arguments)}) -> {IrPrinter.FormatType(function.ReturnType, module)}{Region(function.Locals[0])}";
            }

            // ── 5. Roots ───────────────────────────────────────────

            private HashSet<int> Roots(IrModule merged, out string? entry)
            {
                HashSet<int> roots = [];
                entry = null;
                var candidates = Enumerable.Range(0, merged.Functions.Length)
                    .Where(f => options.Entry.Contains("::") ? merged.Functions[f].Name == options.Entry : merged.Functions[f].Name.EndsWith($"::{options.Entry}"))
                    .ToList();
                if (candidates.Count == 1)
                {
                    entry = merged.Functions[candidates[0]].Name;
                    roots.Add(candidates[0]);
                }
                else if (candidates.Count > 1)
                    errors.Add(new(options.Entry, $"entry `{options.Entry}` is ambiguous: {string.Join(", ", candidates.Select(c => merged.Functions[c].Name))}"));
                else if (!options.Library && !options.CLibrary)
                    errors.Add(new(options.Entry, $"no entry function `{options.Entry}` (build a library to link without one)"));

                for (int f = 0; f < merged.Functions.Length; f++)
                {
                    var function = merged.Functions[f];
                    if (!function.IsGeneric && (function.Reflect == ReflectionLevel.Full || options.Library && function.Linkage == Linkage.Export
                        || NativeSymbols.ExportName(function) is not null))
                        roots.Add(f);
                }
                return roots;
            }
        }
    }
}

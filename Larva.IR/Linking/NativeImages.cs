using Larva.IR.Lowering;

namespace Larva.IR.Linking
{
    /// <summary>
    /// A linked program cut into native images: each library module (a namespace) becomes a DLL, the program an
    /// executable that loads them. Every image is compiled from the whole linked program, but:
    /// <list type="bullet">
    /// <item>non-generic functions of <b>foreign</b> modules (built into other images) are only declared: calls go to the
    /// image that defines them, by symbol;</item>
    /// <item>generic code is instantiated by each image that uses it (as C++ templates);</item>
    /// <item>a foreign module's globals are read and written through the accessors its image exports
    /// (<c>G.$get</c>, <c>G.$set</c>);</item>
    /// <item>a library cannot number types and classes (only the program sees them all), so its <c>typeid</c> and
    /// <c>classid</c> read slots (<see cref="IdSlots"/>); the program fills them at start-up through the library's
    /// <c>$ids.set</c>, with the ids of what the library's manifest lists.</item>
    /// </list>
    /// Generic globals (static fields of generic types) stay per image.
    /// </summary>
    public static class NativeImages
    {
        public const string IdsSetter = "$ids.set";

        /// <summary>The module a linked name (<c>module::name</c>) comes from; empty for runtime names.</summary>
        public static string ModuleOf(string name)
        {
            int separator = name.IndexOf("::", StringComparison.Ordinal);
            return separator < 0 ? "" : name[..separator];
        }

        /// <summary>What the DLL of a library module exports, as object-file symbols (see <see cref="NativeSymbols"/>).</summary>
        public static IReadOnlyList<string> ExportSymbols(IrModule module, string prefix)
        {
            var mangled = module.Functions.Where(f => f.Mangled).Select(f => $"{module.Name}::{f.Name}").ToHashSet();
            var exported = module.Functions.Where(f => NativeSymbols.ExportName(f) is not null)
                .ToDictionary(f => $"{module.Name}::{f.Name}", f => NativeSymbols.ExportName(f)!);
            return [.. Exports(module).Select(name => exported.TryGetValue(name, out var c) ? c : NativeSymbols.Of(name, mangled.Contains(name), prefix))];
        }

        /// <summary>What the DLL of a library module exports, as linked names: its functions, global accessors and id setter.</summary>
        public static IReadOnlyList<string> Exports(IrModule module) =>
            [.. module.Functions.Where(f => f.Linkage == Linkage.Export && !f.IsGeneric).Select(f => $"{module.Name}::{f.Name}")
                .Concat(module.Globals.Where(g => g.Linkage == Linkage.Export && g.GenericCount == 0)
                    .SelectMany(g => new[] { $"{module.Name}::{g.Name}.$get", $"{module.Name}::{g.Name}.$set" }))
                .Append($"{module.Name}::{IdsSetter}")];

        /// <summary>
        /// The program for one image: foreign functions declared, foreign globals behind accessors, and (for a library
        /// module <paramref name="own"/>) accessors for its own globals. Roots: what the image exports, or the linked roots
        /// for the executable.
        /// </summary>
        public static LinkResult Prepare(LinkResult linked, IReadOnlySet<string> foreign, string? own)
        {
            var module = linked.Merged!;
            var functions = module.Functions.ToList();
            for (int f = 0; f < functions.Count; f++)
            {
                // Another image's code is called there; a link-once function (compiler-made) is copied here instead.
                if (!functions[f].IsGeneric && functions[f].Linkage != Linkage.LinkOnce && foreign.Contains(ModuleOf(functions[f].Name)))
                    functions[f] = ModuleSplitter.Declaration(functions[f]) with { Symbol = functions[f].Name };
            }

            // Foreign globals: accessor declarations, and every read or write becomes a call.
            var accessors = new Dictionary<int, (int Get, int Set)>();
            var roots = own is null ? new HashSet<int>(linked.Roots) : [];
            for (int g = 0; g < module.Globals.Length; g++)
            {
                var global = module.Globals[g];
                string owner = ModuleOf(global.Name);
                if (global.GenericCount > 0 || owner.Length == 0 || !foreign.Contains(owner) && owner != own)
                    continue;
                int get = functions.Count, set = get + 1;
                functions.Add(Getter(global, g, define: owner == own));
                functions.Add(Setter(global, g, define: owner == own));
                if (owner == own)
                    roots.UnionWith([get, set]);
                else
                    accessors[g] = (get, set);
            }
            if (accessors.Count > 0)
            {
                for (int f = 0; f < functions.Count; f++)
                {
                    if (!functions[f].IsDeclaration)
                        functions[f] = ThroughAccessors(functions[f], accessors);
                }
            }
            if (own is not null)
            {
                for (int f = 0; f < module.Functions.Length; f++)
                {
                    if (!module.Functions[f].IsGeneric && ModuleOf(module.Functions[f].Name) == own && module.Functions[f].Linkage == Linkage.Export)
                        roots.Add(f);
                }
            }
            return linked with { Merged = module with { Functions = [.. functions] }, Roots = roots };
        }

        private static IrFunction Getter(GlobalDef global, int index, bool define)
        {
            var f = new FunctionBuilder($"{global.Name}.$get", global.Type);
            if (!define)
                return Declared(f, $"{global.Name}.$get");
            f.Assign(FunctionBuilder.ReturnPlace, Rvalue.ReadGlobal(index)).Return();
            return f.Build() with { Linkage = Linkage.Export };
        }

        private static IrFunction Setter(GlobalDef global, int index, bool define)
        {
            var f = new FunctionBuilder($"{global.Name}.$set", IrType.Unit);
            int value = f.Argument(global.Type, "value");
            if (!define)
                return Declared(f, $"{global.Name}.$set");
            f.StoreGlobal(index, [], Operand.Move(new Place(value)));
            f.Return();
            return f.Build() with { Linkage = Linkage.Export };
        }

        /// <summary>A function another image defines: its signature, named by its linked name.</summary>
        private static IrFunction Declared(FunctionBuilder f, string name) => f.ImportFrom(name).Build();

        /// <summary>Reads and writes of the globals in <paramref name="accessors"/> become calls (blocks split at each).</summary>
        private static IrFunction ThroughAccessors(IrFunction function, Dictionary<int, (int Get, int Set)> accessors)
        {
            bool Touches(Statement s) => s is AssignStatement { Value: GlobalRvalue { TypeArguments.Length: 0 } read } && accessors.ContainsKey(read.Global)
                || s is StoreGlobalStatement { TypeArguments.Length: 0 } store && accessors.ContainsKey(store.Global);
            if (!function.Blocks.Any(b => b.Statements.Any(Touches)))
                return function;

            var locals = function.Locals.ToList();
            int unit = locals.Count;
            locals.Add(new LocalDecl(IrType.Unit, "$unit", Mutable: true));
            var blocks = function.Blocks.ToList();
            for (int b = 0; b < blocks.Count; b++)
            {
                var statements = blocks[b].Statements;
                int at = Array.FindIndex(statements, s => Touches(s));
                if (at < 0)
                    continue;
                // This block ends with the call; the rest (and the old terminator) moves to a new block after it.
                int rest = blocks.Count;
                blocks.Add(new BasicBlock(statements[(at + 1)..], blocks[b].Terminator));
                Terminator call = statements[at] switch
                {
                    AssignStatement { Value: GlobalRvalue read } assign => new CallTerminator(accessors[read.Global].Get, [], assign.Target, rest),
                    StoreGlobalStatement store => new CallTerminator(accessors[store.Global].Set, [store.Value], new Place(unit), rest),
                    _ => throw new InvalidOperationException(),
                };
                blocks[b] = new BasicBlock(statements[..at], call);
            }
            return function with { Locals = [.. locals], Blocks = [.. blocks] };
        }

        /// <summary>
        /// The id native code gives an exception type (0 is "any"): a hash of its name, the same in every image of a
        /// program, so an exception thrown in one DLL is caught by type in another.
        /// </summary>
        public static int ExceptionTypeId(IrType type, IrModule module)
        {
            uint hash = 2166136261;
            foreach (char c in Text.IrPrinter.FormatType(type, module))
                hash = (hash ^ c) * 16777619;
            int id = (int)(hash & 0x7FFFFFFF);
            return id == 0 ? 1 : id;
        }

        // ── Ids ──────────────────────────────────────────────────

        /// <summary>
        /// After monomorphization of a library image: <c>$ids.set(&amp;[i32])</c>, exported, which stores each value in
        /// its slot. Returns the module with it and the manifest: what each slot holds, as portable type names.
        /// </summary>
        public static (IrModule Module, IReadOnlyList<string> Manifest) AddIdSetter(IrModule concrete, IdSlots slots, IrModule linked, string own)
        {
            var f = new FunctionBuilder($"{own}::{IdsSetter}", IrType.Unit);
            int values = f.Argument(IrType.Ref(IrType.ArrayOf(IrType.I32)), "values");
            int index = f.Local(IrType.U64, "index", mutable: true);
            foreach (var (_, _, global) in slots.Entries)
            {
                f.Assign(index, Operand.Int(slots.Entries.FindIndex(e => e.Global == global), IrType.U64));
                f.StoreGlobal(global, [], Operand.Copy(new Place(values).Deref().Index(index)));
            }
            f.Return();
            var setter = f.Build() with { Linkage = Linkage.Export };
            var manifest = slots.Entries.Select(e => $"{(int)e.Kind} {TypeNames.Format(e.Type, linked)}").ToList();
            return (concrete with { Functions = [.. concrete.Functions, setter] }, manifest);
        }

        /// <summary>
        /// For the executable: <c>$native.init</c>, which gives each library image the ids its manifest asks for (in this
        /// program's numbering). Returns the program with it (and the setters it calls, declared) and its index.
        /// </summary>
        public static (LinkResult Program, int Init) AddIdInit(LinkResult program, IReadOnlyDictionary<string, IReadOnlyList<string>> manifests)
        {
            var module = program.Merged!;
            var functions = module.Functions.ToList();
            var f = new FunctionBuilder("$native.init", IrType.Unit);
            int unit = f.Local(IrType.Unit, "unit", mutable: true);
            int index = f.Local(IrType.U64, "index", mutable: true);
            int value = f.Local(IrType.I32, "value", mutable: true);
            foreach (var (library, manifest) in manifests.OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                if (manifest.Count == 0)
                    continue;
                var setter = new FunctionBuilder($"{library}::{IdsSetter}", IrType.Unit);
                setter.Argument(IrType.Ref(IrType.ArrayOf(IrType.I32)), "values");
                int setterIndex = functions.Count;
                functions.Add(Declared(setter, $"{library}::{IdsSetter}"));

                int array = f.Local(IrType.Box(IrType.ArrayOf(IrType.I32)), $"ids.{library}", mutable: true);
                int view = f.Local(IrType.Ref(IrType.ArrayOf(IrType.I32)), $"view.{library}");
                f.Assign(array, Rvalue.NewArray(AllocKind.Box, IrType.I32, Operand.Int(manifest.Count, IrType.U64)));
                for (int k = 0; k < manifest.Count; k++)
                {
                    int space = manifest[k].IndexOf(' ');
                    var kind = (IdKind)int.Parse(manifest[k][..space]);
                    var type = TypeNames.Parse(manifest[k][(space + 1)..], module)
                        ?? throw new InvalidOperationException($"{library}: the program has no type `{manifest[k][(space + 1)..]}` the library was built with: rebuild it");
                    f.Assign(value, kind == IdKind.Type ? Rvalue.TypeId(type) : Rvalue.ClassId(type, end: kind == IdKind.ClassEnd));
                    f.Assign(index, Operand.Int(k, IrType.U64));
                    f.Assign(new Place(array).Deref().Index(index), Operand.Copy(new Place(value)));
                }
                f.Assign(view, Rvalue.Ref(new Place(array).Deref()));
                int next = f.NewBlock();
                f.Call(setterIndex, [Operand.Move(new Place(view))], new Place(unit), next);
                f.At(next);
            }
            f.Return();
            int init = functions.Count;
            functions.Add(f.Build());
            var roots = new HashSet<int>(program.Roots) { init };
            return (program with { Merged = module with { Functions = [.. functions] }, Roots = roots }, init);
        }
    }
}

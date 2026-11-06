namespace Larva.IR.Linking
{
    /// <summary>
    /// Shapes a module for distribution, as small as it can be:
    /// <list type="bullet">
    /// <item><see cref="Trim"/> keeps only what its roots reach (imports nobody calls, link-once copies nobody uses and
    /// unused static data go away);</item>
    /// <item><see cref="Partition"/> splits it into one module per owner (a namespace): each defines and exports
    /// its own items, imports what it uses from the others, and carries the link-once items it needs.</item>
    /// </list>
    /// </summary>
    public static class ModuleSplitter
    {
        /// <summary>
        /// The part of <paramref name="module"/> its roots reach. The module's own definitions that meet requirements
        /// (implementations) are roots too. Ids are renumbered in their original order.
        /// </summary>
        public static IrModule Trim(IrModule module, Func<int, bool> rootStruct, Func<int, bool> rootFunction, Func<int, bool> rootGlobal)
        {
            var usedStructs = new bool[module.Structs.Length];
            var usedFunctions = new bool[module.Functions.Length];
            var usedGlobals = new bool[module.Globals.Length];
            var usedData = new bool[module.Data.Length];
            var pending = new Stack<(char Kind, int Id)>();

            int Mark(bool[] used, char kind, int id)
            {
                if (id >= 0 && !used[id])
                {
                    used[id] = true;
                    if (kind != 'd')
                        pending.Push((kind, id));
                }
                return id;
            }

            // Walking an item through a map that records every id it meets finds what the item uses.
            var marker = new IdMap(s => Mark(usedStructs, 's', s), f => Mark(usedFunctions, 'f', f), g => Mark(usedGlobals, 'g', g), d => Mark(usedData, 'd', d));
            for (int s = 0; s < module.Structs.Length; s++)
            {
                if (rootStruct(s))
                    Mark(usedStructs, 's', s);
            }
            for (int f = 0; f < module.Functions.Length; f++)
            {
                if (rootFunction(f))
                    Mark(usedFunctions, 'f', f);
            }
            for (int g = 0; g < module.Globals.Length; g++)
            {
                if (rootGlobal(g))
                    Mark(usedGlobals, 'g', g);
            }
            // A module's own implementations stay: generic code linked later may need them (another module's generic code
            // instantiated for this module's types, which then carries a copy of a link-once one).
            foreach (var implementation in module.Implementations)
            {
                if (module.Functions[implementation.Function].Linkage is Linkage.Export or Linkage.Internal or Linkage.LinkOnce)
                    Mark(usedFunctions, 'f', implementation.Function);
            }
            while (pending.TryPop(out var item))
            {
                _ = item.Kind switch
                {
                    's' => (object)marker.Struct(module.Structs[item.Id]),
                    'f' => marker.Function(module.Functions[item.Id]),
                    _ => marker.Global(module.Globals[item.Id]),
                };
            }

            int[] Renumber(bool[] used)
            {
                var map = new int[used.Length];
                int next = 0;
                for (int i = 0; i < used.Length; i++)
                    map[i] = used[i] ? next++ : -1;
                return map;
            }
            int[] structMap = Renumber(usedStructs), functionMap = Renumber(usedFunctions), globalMap = Renumber(usedGlobals), dataMap = Renumber(usedData);
            var ids = new IdMap(s => structMap[s], f => functionMap[f], g => globalMap[g], d => dataMap[d]);
            return module with
            {
                Structs = [.. module.Structs.Where((_, s) => usedStructs[s]).Select(ids.Struct)],
                Functions = [.. module.Functions.Where((_, f) => usedFunctions[f]).Select(ids.Function)],
                Globals = [.. module.Globals.Where((_, g) => usedGlobals[g]).Select(ids.Global)],
                Data = [.. module.Data.Where((_, d) => usedData[d])],
                Implementations = [.. module.Implementations.Where(i => usedFunctions[i.Function]).Select(i => i with { Function = functionMap[i.Function] })],
            };
        }

        /// <summary>
        /// A library module without what nothing exported or internal reaches (link-once copies and imports nobody uses).
        /// </summary>
        public static IrModule TrimLibrary(IrModule module) => Trim(module,
            s => module.Structs[s].Linkage is Linkage.Export or Linkage.Internal,
            f => module.Functions[f].Linkage is Linkage.Export,
            g => module.Globals[g].Linkage is Linkage.Export);

        /// <summary>
        /// Splits a module into one per owner: <paramref name="owners"/> names the owner of every struct, function and
        /// global (link-once items and imports need none). Each part is named after its owner, exports everything it
        /// defines, imports what it uses from other parts as <c>owner::name</c>, and is trimmed.
        /// </summary>
        public static IReadOnlyList<IrModule> Partition(IrModule module, ModuleOwners owners)
        {
            var names = owners.Structs.Concat(owners.Functions).Concat(owners.Globals).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();
            var parts = new List<IrModule>();
            foreach (string part in names)
            {
                bool Shared(Linkage linkage) => linkage is Linkage.Import or Linkage.LinkOnce;
                var structs = module.Structs.Select((def, s) =>
                    Shared(def.Linkage) ? def
                    : owners.Structs[s] == part ? def with { Linkage = Linkage.Export }
                    : def with { Linkage = Linkage.Import, Symbol = $"{owners.Structs[s]}::{def.Name}" }).ToArray();
                var functions = module.Functions.Select((function, f) =>
                    Shared(function.Linkage) ? function
                    : owners.Functions[f] == part ? function with { Linkage = Linkage.Export }
                    : Declaration(function) with { Symbol = $"{owners.Functions[f]}::{function.Name}" }).ToArray();
                var globals = module.Globals.Select((global, g) =>
                    Shared(global.Linkage) ? global
                    : owners.Globals[g] == part ? global with { Linkage = Linkage.Export }
                    : global with { Linkage = Linkage.Import, Symbol = $"{owners.Globals[g]}::{global.Name}", Initial = null }).ToArray();
                var whole = module with { Name = part, Structs = structs, Functions = functions, Globals = globals };
                parts.Add(Trim(whole,
                    s => owners.Structs[s] == part && !Shared(module.Structs[s].Linkage),
                    f => owners.Functions[f] == part && !Shared(module.Functions[f].Linkage),
                    g => owners.Globals[g] == part && !Shared(module.Globals[g].Linkage)));
            }
            return parts;
        }

        /// <summary>A function reduced to what callers need: its signature (arguments and return place), no body.</summary>
        public static IrFunction Declaration(IrFunction function) => function with
        {
            Locals = function.Locals[..(function.ArgumentCount + 1)],
            Blocks = [],
            Linkage = Linkage.Import,
        };
    }

    /// <summary>Who owns each struct, function and global of a module (by index); <c>null</c> for link-once items and imports.</summary>
    public sealed record ModuleOwners(string?[] Structs, string?[] Functions, string?[] Globals);
}

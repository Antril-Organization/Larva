namespace Larva.Hir.Lowering
{
    using Larva.IR;
    using H = Larva.Hir;

    /// <summary>
    /// Lowering only what the program can run (<see cref="LoweringOptions.ReachedOnly"/>): a library's methods are
    /// lowered when something lowered refers to them, the way the IR linker follows the program from its entry, and
    /// the rest become stubs that stop the program (<c>unreachable</c>) with their signatures, which keep every table
    /// that names them (implementations, vtables of classes never made) as it was.
    /// <para>
    /// Lowered from the roots (the entry, exported methods, every method of the assemblies not in the option: the
    /// program's own, whose errors are reported whether it runs them or not), in rounds: each round lowers the functions
    /// the previous ones referred to (on several threads), then reads what those refer to. Followed:
    /// <list type="bullet">
    /// <item>calls, function values and instance tables (what the IR linker follows), so a class's vtable maker is
    /// lowered with the first object made of it, and with it every method in its slots and its interfaces' thunks;</item>
    /// <item>implementations (interface wrappers, operators, conversions, <c>$new</c>, reflection's <c>$typeof</c>…):
    /// generics resolve them by the types they are instantiated with, so those of every type the lowered code names (in
    /// its values' types and its type arguments) are lowered, with those of its bases; last resorts always are.</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed partial class HirLowerer
    {
        /// <summary>The functions left as stubs: nothing lowered refers to them.</summary>
        internal readonly HashSet<int> Unlowered = [];

        /// <summary>Whether methods are lowered only when reached (an assembly whose users declare everything is lowered whole).</summary>
        private bool LowersReachedOnly => Options.ReachedOnly.Count > 0 && !Options.ExportAll;

        /// <summary>Whether a type's methods are lowered only when reached: its assembly (a generated type's, its declaring type's) is in <see cref="LoweringOptions.ReachedOnly"/>.</summary>
        internal bool Prunable(int type)
        {
            for (int t = type; t >= 0 && t < Assembly.Types.Count; t = Assembly.Types[t].DeclaringType)
            {
                if (Assembly.OriginOf(t) is { } origin)
                    return Options.ReachedOnly.Contains(origin);
            }
            return false;
        }

        private void LowerReached()
        {
            var planOf = new Dictionary<int, MethodPlan>();
            foreach (var plan in Plans)
                planOf[plan.Function] = plan;
            var makerOf = new Dictionary<int, TypeInfo>();
            var typeOfStruct = new Dictionary<int, int>();
            var primitives = new Dictionary<IrType, List<int>>();
            foreach (var info in Types)
            {
                if (info.IsClass)
                    makerOf[info.VtableMaker] = info;
                if (info.Struct >= 0)
                    typeOfStruct[info.Struct] = info.Index;
                if (info.IsPrimitive && info.Definition.Underlying is H.PrimitiveType p)
                {
                    var type = Primitive(p.Kind);
                    if (!primitives.TryGetValue(type, out var list))
                        primitives[type] = list = [];
                    list.Add(info.Index);
                }
            }
            // A describer's implementations ($typeof, $box, $unbox of X) are X's.
            var ownerOf = new Dictionary<int, int>();
            if (Reflection is { } kit)
            {
                foreach (var (described, (describer, _)) in kit.Described)
                    ownerOf[describer] = described;
            }
            var implementationsOf = new Dictionary<int, List<int>>();
            var always = new List<int>();
            foreach (var implementation in Implementations)
            {
                if (planOf.TryGetValue(implementation.Function, out var plan) && !implementation.Name.EndsWith(Requirements.FallbackSuffix, StringComparison.Ordinal))
                {
                    int owner = ownerOf.TryGetValue(plan.Type, out int o) ? o : plan.Type;
                    if (!implementationsOf.TryGetValue(owner, out var list))
                        implementationsOf[owner] = list = [];
                    list.Add(implementation.Function);
                }
                else
                {
                    always.Add(implementation.Function);
                }
            }

            var kept = new List<bool>();
            var pending = new List<int>();
            var used = new bool[Types.Length];
            void Keep(int f)
            {
                if (f < 0)
                    return;
                while (kept.Count <= f)
                    kept.Add(false);
                if (!kept[f])
                {
                    kept[f] = true;
                    pending.Add(f);
                }
            }
            void Use(int type)
            {
                while (type >= 0 && type < used.Length && !used[type])
                {
                    used[type] = true;
                    if (implementationsOf.TryGetValue(type, out var functions))
                    {
                        foreach (int f in functions)
                            Keep(f);
                    }
                    Use(Types[type].Base);
                    type = Types[type].Definition.DeclaringType;
                }
            }
            void UsePrimitives(IrType type)
            {
                foreach (var t in type.Walk())
                {
                    if (t is PrimitiveType && primitives.TryGetValue(t, out var types))
                    {
                        foreach (int index in types)
                            Use(index);
                    }
                }
            }
            // Reading a function through a map that records what it meets finds what it refers to.
            var marker = new IR.Linking.IdMap(s => { if (typeOfStruct.TryGetValue(s, out int t)) Use(t); return s; }, f => { Keep(f); return f; }, g => g, d => d);
            void Scan(IrFunction function)
            {
                marker.Function(function);
                foreach (var local in function.Locals)
                    UsePrimitives(local.Type);
                foreach (var block in function.Blocks)
                {
                    if (block.Terminator is CallTerminator call)
                    {
                        foreach (var argument in call.TypeArguments)
                            UsePrimitives(argument);
                    }
                    foreach (var statement in block.Statements)
                    {
                        if (statement is AssignStatement { Value: FunctionRefRvalue reference })
                        {
                            foreach (var argument in reference.TypeArguments)
                                UsePrimitives(argument);
                        }
                    }
                }
            }

            // The roots.
            foreach (var plan in Plans)
            {
                if (!Prunable(plan.Type) || plan.Synthetic is null && (Types[plan.Type].Definition.Methods[plan.Method].Flags & H.MethodFlags.Export) != 0)
                    Keep(plan.Function);
            }
            if (Assembly.EntryPoint is { IsDefinition: true } entry && MethodFunctions.TryGetValue(entry, out int entryFunction))
                Keep(entryFunction);
            Keep(StaticInitFunction);
            foreach (int f in always)
                Keep(f);

            BeginParallelAdditions();
            int additions = Functions.Count;
            var batch = new List<MethodPlan>();
            while (pending.Count > 0)
            {
                var round = pending.Order().ToArray();
                pending.Clear();
                batch.Clear();
                foreach (int f in round)
                {
                    if (Functions[f] is not null)
                        continue;
                    if (planOf.TryGetValue(f, out var plan))
                    {
                        batch.Add(plan);
                    }
                    else if (makerOf.TryGetValue(f, out var info))
                    {
                        currentPlan = f;
                        currentRank = 0;
                        Functions[f] = VtableMaker(info);
                        currentPlan = -1;
                    }
                }
                if (Options.Parallel && batch.Count > 1)
                    System.Threading.Tasks.Parallel.ForEach(batch, LowerPlan);
                else
                    batch.ForEach(LowerPlan);
                foreach (int f in round)
                {
                    if (Functions[f] is { } function)
                        Scan(function);
                }
                // What the round added (lambdas, boxes, iterator steps…) is part of the functions that made it.
                for (; additions < Functions.Count; additions++)
                    Keep(additions);
            }
            foreach (var plan in Plans)
            {
                if (Functions[plan.Function] is null)
                {
                    var method = plan.Synthetic ?? Types[plan.Type].Definition.Methods[plan.Method];
                    var linkage = method.Access == H.Access.Public && LinkageOf(Types[plan.Type]) == Linkage.Export ? Linkage.Export : Linkage.Internal;
                    Functions[plan.Function] = Stub(plan.Name, plan.Result, plan.Generics, plan.This, plan.Parameters, linkage);
                    Unlowered.Add(plan.Function);
                }
            }
            foreach (var (f, info) in makerOf)
            {
                if (Functions[f] is null)
                {
                    var vtable = VtablePointer(IrType.Struct(info.Vtable, info.OwnArguments));
                    Functions[f] = Stub(info.Name + ".$vtable", vtable, FunctionGenerics(info, null, info.Name), null, [], Linkage.Internal);
                    Unlowered.Add(f);
                }
            }
            OrderParallelAdditions();
        }

        /// <summary>A function with this signature that stops the program: one nothing the program runs calls.</summary>
        private static IrFunction Stub(string name, IrType result, GenericParam[] generics, IrType? self, IrType[] parameters, Linkage linkage)
        {
            var b = new FunctionBuilder(name, result) { Linkage = linkage };
            DeclareGenerics(b, generics);
            if (self is not null)
                b.Argument(self, "this", mutable: false);
            for (int p = 0; p < parameters.Length; p++)
                b.Argument(parameters[p], $"p{p}", mutable: true);
            b.Unreachable();
            return b.Build();
        }
    }
}

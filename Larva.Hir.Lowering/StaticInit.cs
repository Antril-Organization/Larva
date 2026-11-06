namespace Larva.Hir.Lowering
{
    using Larva.IR;
    using H = Larva.Hir;

    public sealed partial class HirLowerer
    {
        /// <summary>
        /// The static constructors' functions in the order the program's entry runs them: a type's after those of the
        /// types its constructor uses (through the methods it calls too); a cycle runs in type order.
        /// </summary>
        internal int[] StaticInitFunctions = [];

        /// <summary>
        /// <c>$StaticInit()</c> (exported; -1 when there are no static constructors): runs them all, the first time it is
        /// called. The entry calls it first; a host that calls functions of the program directly (a test, a library's
        /// user) calls it before them.
        /// </summary>
        internal int StaticInitFunction = -1;

        public const string StaticInitName = "$StaticInit";

        /// <summary>
        /// Static constructors run once, before <c>Main</c>: then reading a static field costs nothing more than the read
        /// (no check that its type is ready).
        /// </summary>
        private void OrderStaticConstructors()
        {
            var constructors = new Dictionary<int, int>(); // type → its static constructor (method index)
            foreach (var info in Types)
            {
                if (info.GenericCount > 0)
                    continue;
                var methods = info.Definition.Methods;
                for (int m = 0; m < methods.Count; m++)
                {
                    if (methods[m].Kind == H.MethodKind.StaticConstructor && methods[m].Body is not null)
                        constructors[info.Index] = m;
                }
            }
            if (constructors.Count == 0)
                return;
            var order = new List<int>();
            var state = new Dictionary<int, int>(); // 1 visiting, 2 done
            void Visit(int type)
            {
                if (state.ContainsKey(type))
                    return;
                state[type] = 1;
                var uses = new UsedTypes(Assembly);
                uses.Method(type, constructors[type]);
                foreach (int used in uses.Types.Order())
                {
                    if (used != type && constructors.ContainsKey(used))
                        Visit(used);
                }
                state[type] = 2;
                order.Add(MethodFunctions[H.MemberToken.Def(type, constructors[type])]);
            }
            var reachable = Options.StaticConstructorsFromEntry ? ReachableTypes() : null;
            foreach (int type in constructors.Keys.Order())
            {
                if (reachable is null || reachable.Contains(type))
                    Visit(type);
            }
            if (order.Count == 0)
                return;
            StaticInitFunctions = [.. order];
            int done = Globals.Count;
            Globals.Add(new GlobalDef("$StaticInit.done", IrType.Bool));
            var b = new FunctionBuilder(StaticInitName, IrType.Unit) { Linkage = Linkage.Export };
            int already = b.Local(IrType.Bool, null, mutable: true);
            int run = b.NewBlock(), finished = b.NewBlock();
            b.Assign(already, Rvalue.ReadGlobal(done)).If(Operand.Copy(already), finished, run);
            b.At(run).StoreGlobal(done, Operand.Bool(true));
            foreach (int function in order)
            {
                int next = b.NewBlock();
                b.Call(function, [], new Place(b.Local(IrType.Unit, null, mutable: true)), next);
                b.At(next);
            }
            b.Goto(finished);
            b.At(finished).Return();
            StaticInitFunction = Functions.Count;
            Functions.Add(b.Build());
        }

        /// <summary>
        /// A program's types that can be used from its entry (and its exported functions): only theirs need their static
        /// constructors run (null without an entry: a host may call anything, so every one runs). Followed: calls, fields,
        /// properties, objects made, function values, and every instance method of a type in use (virtual and interface
        /// calls reach those).
        /// </summary>
        private HashSet<int>? ReachableTypes()
        {
            var roots = new List<H.MemberToken>();
            if (Assembly.EntryPoint is { IsDefinition: true } entry)
                roots.Add(entry);
            for (int t = 0; t < Assembly.Types.Count; t++)
            {
                for (int m = 0; m < Assembly.Types[t].Methods.Count; m++)
                {
                    if ((Assembly.Types[t].Methods[m].Flags & H.MethodFlags.Export) != 0)
                        roots.Add(H.MemberToken.Def(t, m));
                }
            }
            if (roots.Count == 0)
                return null;
            var walker = new Reachable(Assembly);
            foreach (var root in roots)
                walker.Enqueue(root.Type, root.Member);
            walker.Run();
            return walker.Types;
        }

        private sealed class Reachable(H.HirAssembly assembly) : H.HirWalker
        {
            public readonly HashSet<int> Types = [];
            private readonly HashSet<H.MemberToken> seen = [];
            private readonly Queue<H.MemberToken> work = [];

            public void Enqueue(int type, int method)
            {
                if (seen.Add(H.MemberToken.Def(type, method)))
                    work.Enqueue(H.MemberToken.Def(type, method));
                Use(type);
            }

            private void Use(int type)
            {
                if (!Types.Add(type))
                    return;
                var definition = assembly.Types[type];
                for (int m = 0; m < definition.Methods.Count; m++)
                {
                    if (!definition.Methods[m].IsStatic || definition.Methods[m].Kind == H.MethodKind.StaticConstructor)
                        Enqueue(type, m);
                }
                if (definition.DeclaringType >= 0)
                    Use(definition.DeclaringType);
                if (definition.Base is H.NamedType { Type.IsDefinition: true } baseType)
                    Use(baseType.Type.Index);
            }

            public void Run()
            {
                while (work.TryDequeue(out var token))
                {
                    if (assembly.Types[token.Type].Methods[token.Member].Body is { } body)
                        Visit(body.Block);
                }
            }

            public override void Visit(H.Expr? e)
            {
                switch (e)
                {
                    case H.FieldExpr { Field.IsDefinition: true } f:
                        Use(f.Field.Type);
                        break;
                    case H.CallExpr { Method.IsDefinition: true } call:
                        Enqueue(call.Method.Type, call.Method.Member);
                        break;
                    case H.NewExpr { Constructor: { IsDefinition: true } constructor }:
                        Enqueue(constructor.Type, constructor.Member);
                        break;
                    case H.NewExpr { Type: H.NamedType { Type.IsDefinition: true } made }:
                        Use(made.Type.Index);
                        break;
                    case H.PropertyExpr { Property.IsDefinition: true } p:
                    {
                        var property = assembly.Types[p.Property.Type].Properties[p.Property.Member];
                        if (property.Getter >= 0)
                            Enqueue(p.Property.Type, property.Getter);
                        if (property.Setter >= 0)
                            Enqueue(p.Property.Type, property.Setter);
                        break;
                    }
                    case H.MethodRefExpr { Method.IsDefinition: true } m:
                        Enqueue(m.Method.Type, m.Method.Member);
                        break;
                }
                base.Visit(e);
            }
        }

        /// <summary>The types whose members a method's body uses, following the methods it calls (each once).</summary>
        private sealed class UsedTypes(H.HirAssembly assembly) : H.HirWalker
        {
            public readonly HashSet<int> Types = [];
            private readonly HashSet<H.MemberToken> methods = [];
            private const int Limit = 4096;

            public void Method(int type, int method)
            {
                var token = H.MemberToken.Def(type, method);
                if (methods.Count >= Limit || !methods.Add(token))
                    return;
                if (assembly.Types[type].Methods[method].Body is { } body)
                    Visit(body.Block);
            }

            private void Use(H.MemberToken token)
            {
                if (token.IsDefinition)
                    Types.Add(token.Type);
            }

            public override void Visit(H.Expr? e)
            {
                switch (e)
                {
                    case H.FieldExpr f:
                        Use(f.Field);
                        break;
                    case H.CallExpr call when call.Method.IsDefinition:
                        Use(call.Method);
                        Method(call.Method.Type, call.Method.Member);
                        break;
                    case H.NewExpr { Constructor: { IsDefinition: true } constructor }:
                        Use(constructor);
                        Method(constructor.Type, constructor.Member);
                        break;
                    case H.PropertyExpr { Property.IsDefinition: true } p:
                    {
                        Use(p.Property);
                        var property = assembly.Types[p.Property.Type].Properties[p.Property.Member];
                        if (property.Getter >= 0)
                            Method(p.Property.Type, property.Getter);
                        if (property.Setter >= 0)
                            Method(p.Property.Type, property.Setter);
                        break;
                    }
                    case H.MethodRefExpr { Method.IsDefinition: true } m:
                        Use(m.Method);
                        Method(m.Method.Type, m.Method.Member);
                        break;
                }
                base.Visit(e);
            }
        }
    }
}

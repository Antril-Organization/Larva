namespace Larva.Hir.Lowering
{
    using Larva.IR;
    using H = Larva.Hir;

    /// <summary>
    /// What the lambdas of a body capture. Contexts: 0 is the method, k + 1 is lambda k (in the order they appear). A local
    /// belongs to the context that first names it (where it is declared); it is captured when another context names it.
    /// The method's parameters and <c>this</c> are captured when a lambda names them.
    /// </summary>
    internal sealed class CaptureScan
    {
        public readonly List<H.LambdaExpr> Lambdas = [];
        public readonly Dictionary<H.LambdaExpr, int> Index = new(ReferenceEqualityComparer.Instance);
        public readonly int[] Owner;
        public readonly bool[] Captured;
        public readonly bool[] CapturedParameters;
        public bool This;

        /// <summary>
        /// Scopes: 0 is the method's; each loop of the method's own body (not of a lambda's) has one for its body, made
        /// anew each time round, so a lambda made in a pass captures that pass's variables. <c>Parent[s]</c> is the scope
        /// around scope s (-1 for the method's).
        /// </summary>
        public readonly List<int> Parent = [-1];

        /// <summary>The scope of each loop body.</summary>
        public readonly Dictionary<H.Stmt, int> BodyScope = new(ReferenceEqualityComparer.Instance);

        /// <summary>The scope each local is declared in.</summary>
        public readonly int[] Scope;

        /// <summary>The scope each lambda is made in.</summary>
        public readonly List<int> LambdaScope = [];

        /// <summary>The scope being scanned.</summary>
        private int scope;

        private CaptureScan(int locals, int parameters)
        {
            Owner = new int[locals];
            Array.Fill(Owner, -1);
            Captured = new bool[locals];
            Scope = new int[locals];
            CapturedParameters = new bool[parameters];
        }

        /// <summary>Whether scope s keeps anything (the method's scope: also captured parameters and <c>this</c>).</summary>
        public bool ScopeNeedsDisplay(int s)
        {
            if (s == 0 && (This || CapturedParameters.Contains(true)))
                return true;
            for (int l = 0; l < Captured.Length; l++)
            {
                if (Captured[l] && Scope[l] == s)
                    return true;
            }
            return false;
        }

        private void Declare(int local, int context)
        {
            if (local >= 0 && context == 0)
                Scope[local] = scope;
            Use(local, context);
        }

        /// <summary>A loop body of the method's own code gets a scope of its own.</summary>
        private void LoopBody(H.Stmt body, int c, int declared = -1)
        {
            if (c != 0)
            {
                Use(declared, c);
                Statement(body, c);
                return;
            }
            int outer = scope;
            scope = Parent.Count;
            Parent.Add(outer);
            BodyScope[body] = scope;
            Declare(declared, c);
            Statement(body, c);
            scope = outer;
        }

        /// <summary>The scan of a body, or null when it has no lambdas.</summary>
        public static CaptureScan? Run(H.HirBody body, int parameters)
        {
            if (!HasLambda(body.Block))
                return null;
            var scan = new CaptureScan(body.Locals.Length, parameters);
            scan.Statement(body.Block, 0);
            return scan;
        }

        private void Use(int local, int context)
        {
            if (local < 0)
                return;
            if (Owner[local] < 0)
                Owner[local] = context;
            else if (Owner[local] != context)
                Captured[local] = true;
        }

        private void Statement(H.Stmt? s, int c)
        {
            switch (s)
            {
                case null:
                    return;
                case H.BlockStmt block:
                    foreach (var statement in block.Statements)
                        Statement(statement, c);
                    return;
                case H.LocalStmt local:
                    Declare(local.Local, c);
                    Expression(local.Value, c);
                    return;
                case H.ExprStmt e:
                    Expression(e.Expression, c);
                    return;
                case H.IfStmt i:
                    Expression(i.Condition, c);
                    Statement(i.Then, c);
                    Statement(i.Else, c);
                    return;
                case H.WhileStmt w:
                    Expression(w.Condition, c);
                    LoopBody(w.Body, c);
                    return;
                case H.DoWhileStmt d:
                    LoopBody(d.Body, c);
                    Expression(d.Condition, c);
                    return;
                case H.ForStmt f:
                    foreach (var initializer in f.Initializers)
                        Statement(initializer, c);
                    Expression(f.Condition, c);
                    foreach (var step in f.Steps)
                        Expression(step, c);
                    LoopBody(f.Body, c);
                    return;
                case H.ForeachStmt f:
                    Expression(f.Collection, c);
                    LoopBody(f.Body, c, f.Local);
                    return;
                case H.SwitchStmt sw:
                    Expression(sw.Value, c);
                    foreach (var section in sw.Sections)
                    {
                        foreach (var pattern in section.Patterns)
                            Pattern(pattern, c);
                        Expression(section.Guard, c);
                        Statement(section.Body, c);
                    }
                    return;
                case H.ReturnStmt r:
                    Expression(r.Value, c);
                    return;
                case H.ThrowStmt t:
                    Expression(t.Value, c);
                    return;
                case H.TryStmt t:
                    Statement(t.Body, c);
                    foreach (var clause in t.Catches)
                    {
                        Declare(clause.Local, c);
                        Expression(clause.Filter, c);
                        Statement(clause.Body, c);
                    }
                    Statement(t.Finally, c);
                    return;
                case H.UsingStmt u:
                    Use(u.Local, c);
                    Expression(u.Resource, c);
                    Statement(u.Body, c);
                    return;
                case H.DeleteStmt d:
                    Expression(d.Value, c);
                    return;
                case H.RegionStmt r:
                    Statement(r.Body, c);
                    return;
                case H.UnsafeStmt u:
                    Statement(u.Body, c);
                    return;
                case H.CheckedStmt k:
                    Statement(k.Body, c);
                    return;
                case H.YieldStmt y:
                    Expression(y.Value, c);
                    return;
            }
        }

        private void Expressions(H.Expr[] expressions, int c)
        {
            foreach (var e in expressions)
                Expression(e, c);
        }

        private void Expression(H.Expr? e, int c)
        {
            switch (e)
            {
                case null:
                    return;
                case H.LocalExpr l:
                    Use(l.Local, c);
                    return;
                case H.ParamExpr p:
                    if (c > 0 && p.Parameter < CapturedParameters.Length)
                        CapturedParameters[p.Parameter] = true;
                    return;
                case H.ThisExpr:
                    if (c > 0)
                        This = true;
                    return;
                case H.LambdaExpr lambda:
                {
                    int k = Lambdas.Count;
                    Lambdas.Add(lambda);
                    LambdaScope.Add(scope);
                    Index[lambda] = k;
                    foreach (int parameter in lambda.Parameters)
                        Use(parameter, k + 1);
                    Statement(lambda.Body, k + 1);
                    return;
                }
                case H.FieldExpr f:
                    Expression(f.Instance, c);
                    return;
                case H.PropertyExpr p:
                    Expression(p.Instance, c);
                    Expressions(p.Indices, c);
                    return;
                case H.ElementExpr x:
                    Expression(x.Array, c);
                    Expressions(x.Indices, c);
                    return;
                case H.CallExpr call:
                    Expression(call.Receiver, c);
                    Expressions(call.Arguments, c);
                    return;
                case H.InvokeExpr i:
                    Expression(i.Function, c);
                    Expressions(i.Arguments, c);
                    return;
                case H.NewExpr n:
                    Expressions(n.Arguments, c);
                    foreach (var init in n.Initializers)
                        Expression(init.Value, c);
                    return;
                case H.NewArrayExpr n:
                    Expressions(n.Lengths, c);
                    return;
                case H.ArrayLiteralExpr a:
                    Expressions(a.Items, c);
                    return;
                case H.UnaryExpr u:
                    Expression(u.Operand, c);
                    return;
                case H.BinaryExpr bin:
                    Expression(bin.Left, c);
                    Expression(bin.Right, c);
                    return;
                case H.AssignExpr a:
                    Expression(a.Target, c);
                    Expression(a.Value, c);
                    return;
                case H.ConvertExpr cv:
                    Expression(cv.Value, c);
                    return;
                case H.IsExpr i:
                    Expression(i.Value, c);
                    Pattern(i.Pattern, c);
                    return;
                case H.AsExpr a:
                    Expression(a.Value, c);
                    return;
                case H.SwitchExpr sw:
                    Expression(sw.Value, c);
                    foreach (var arm in sw.Arms)
                    {
                        Pattern(arm.Pattern, c);
                        Expression(arm.Guard, c);
                        Expression(arm.Value, c);
                    }
                    return;
                case H.ConditionalExpr ce:
                    Expression(ce.Condition, c);
                    Expression(ce.Then, c);
                    Expression(ce.Else, c);
                    return;
                case H.CoalesceExpr co:
                    Expression(co.Left, c);
                    Expression(co.Right, c);
                    return;
                case H.ConditionalAccessExpr ca:
                    Expression(ca.Receiver, c);
                    Expression(ca.Access, c);
                    return;
                case H.MethodRefExpr m:
                    Expression(m.Target, c);
                    return;
                case H.AwaitExpr a:
                    Expression(a.Task, c);
                    return;
                case H.ArrayLengthExpr a:
                    Expression(a.Array, c);
                    return;
                case H.WithExpr w:
                    Expression(w.Value, c);
                    foreach (var change in w.Changes)
                        Expression(change.Value, c);
                    return;
                case H.RangeExpr r:
                    Expression(r.Low, c);
                    Expression(r.High, c);
                    return;
                case H.InterpolatedExpr i:
                    foreach (var part in i.Parts)
                        Expression(part.Value, c);
                    return;
                case H.BorrowExpr bw:
                    Expression(bw.Place, c);
                    return;
                case H.AddressOfExpr ad:
                    Expression(ad.Place, c);
                    return;
                case H.DerefExpr d:
                    Expression(d.Pointer, c);
                    return;
                case H.BlockExpr block:
                    foreach (var statement in block.Statements)
                        Statement(statement, c);
                    Expression(block.Value, c);
                    return;
                case H.CheckedExpr k:
                    Expression(k.Value, c);
                    return;
            }
        }

        private void Pattern(H.Pattern? p, int c)
        {
            switch (p)
            {
                case H.BindingPattern bind:
                    Declare(bind.Local, c);
                    Pattern(bind.Inner, c);
                    return;
                case H.AndPattern a:
                    Pattern(a.Left, c);
                    Pattern(a.Right, c);
                    return;
                case H.OrPattern o:
                    Pattern(o.Left, c);
                    Pattern(o.Right, c);
                    return;
                case H.NotPattern n:
                    Pattern(n.Operand, c);
                    return;
                case H.PropertyPattern pp:
                    foreach (var member in pp.Members)
                        Pattern(member.Pattern, c);
                    return;
                case H.PositionalPattern pos:
                    foreach (var item in pos.Items)
                        Pattern(item, c);
                    return;
                case H.ListPattern list:
                    foreach (var item in list.Items)
                        Pattern(item, c);
                    Declare(list.SliceLocal, c);
                    return;
            }
        }

        // ── Whether a body has lambdas at all (most do not: one quick walk) ──

        private static bool HasLambda(H.Stmt? s) => s switch
        {
            null => false,
            H.BlockStmt block => block.Statements.Any(HasLambda),
            H.LocalStmt local => HasLambda(local.Value),
            H.ExprStmt e => HasLambda(e.Expression),
            H.IfStmt i => HasLambda(i.Condition) || HasLambda(i.Then) || HasLambda(i.Else),
            H.WhileStmt w => HasLambda(w.Condition) || HasLambda(w.Body),
            H.DoWhileStmt d => HasLambda(d.Body) || HasLambda(d.Condition),
            H.ForStmt f => f.Initializers.Any(HasLambda) || HasLambda(f.Condition) || f.Steps.Any(HasLambda) || HasLambda(f.Body),
            H.ForeachStmt f => HasLambda(f.Collection) || HasLambda(f.Body),
            H.SwitchStmt sw => HasLambda(sw.Value) || sw.Sections.Any(x => HasLambda(x.Guard) || HasLambda(x.Body)),
            H.ReturnStmt r => HasLambda(r.Value),
            H.ThrowStmt t => HasLambda(t.Value),
            H.TryStmt t => HasLambda(t.Body) || t.Catches.Any(x => HasLambda(x.Filter) || HasLambda(x.Body)) || HasLambda(t.Finally),
            H.UsingStmt u => HasLambda(u.Resource) || HasLambda(u.Body),
            H.DeleteStmt d => HasLambda(d.Value),
            H.RegionStmt r => HasLambda(r.Body),
            H.UnsafeStmt u => HasLambda(u.Body),
            H.CheckedStmt k => HasLambda(k.Body),
            H.YieldStmt y => HasLambda(y.Value),
            _ => false,
        };

        private static bool HasLambda(H.Expr? e) => e switch
        {
            null => false,
            H.LambdaExpr => true,
            H.LiteralExpr or H.LocalExpr or H.ParamExpr or H.ThisExpr or H.DefaultExpr or H.TypeOfExpr or H.SizeOfExpr or H.ReceiverExpr or H.ResourceExpr => false,
            H.FieldExpr f => HasLambda(f.Instance),
            H.PropertyExpr p => HasLambda(p.Instance) || p.Indices.Any(HasLambda),
            H.ElementExpr x => HasLambda(x.Array) || x.Indices.Any(HasLambda),
            H.CallExpr call => HasLambda(call.Receiver) || call.Arguments.Any(HasLambda),
            H.InvokeExpr i => HasLambda(i.Function) || i.Arguments.Any(HasLambda),
            H.NewExpr n => n.Arguments.Any(HasLambda) || n.Initializers.Any(x => HasLambda(x.Value)),
            H.NewArrayExpr n => n.Lengths.Any(HasLambda),
            H.ArrayLiteralExpr a => a.Items.Any(HasLambda),
            H.UnaryExpr u => HasLambda(u.Operand),
            H.BinaryExpr bin => HasLambda(bin.Left) || HasLambda(bin.Right),
            H.AssignExpr a => HasLambda(a.Target) || HasLambda(a.Value),
            H.ConvertExpr cv => HasLambda(cv.Value),
            H.IsExpr i => HasLambda(i.Value),
            H.AsExpr a => HasLambda(a.Value),
            H.SwitchExpr sw => HasLambda(sw.Value) || sw.Arms.Any(x => HasLambda(x.Guard) || HasLambda(x.Value)),
            H.ConditionalExpr ce => HasLambda(ce.Condition) || HasLambda(ce.Then) || HasLambda(ce.Else),
            H.CoalesceExpr co => HasLambda(co.Left) || HasLambda(co.Right),
            H.ConditionalAccessExpr ca => HasLambda(ca.Receiver) || HasLambda(ca.Access),
            H.MethodRefExpr m => HasLambda(m.Target),
            H.AwaitExpr a => HasLambda(a.Task),
            H.ArrayLengthExpr a => HasLambda(a.Array),
            H.WithExpr w => HasLambda(w.Value) || w.Changes.Any(x => HasLambda(x.Value)),
            H.RangeExpr r => HasLambda(r.Low) || HasLambda(r.High),
            H.InterpolatedExpr i => i.Parts.Any(x => HasLambda(x.Value)),
            H.BorrowExpr bw => HasLambda(bw.Place),
            H.AddressOfExpr ad => HasLambda(ad.Place),
            H.DerefExpr d => HasLambda(d.Pointer),
            H.BlockExpr block => block.Statements.Any(HasLambda) || HasLambda(block.Value),
            H.CheckedExpr k => HasLambda(k.Value),
            _ => false,
        };
    }

    /// <summary>A method's closures: its scan, its display object's layout, and the function of each lambda (once made).</summary>
    internal sealed class Closures(CaptureScan scan, int locals, int parameters)
    {
        public readonly CaptureScan Scan = scan;

        /// <summary>The display object's handle type of each scope (null: the scope keeps nothing).</summary>
        public readonly IrType?[] DisplayHandle = new IrType?[scan.Parent.Count];

        /// <summary>Each scope's display: the field holding the display around it (-1: none), and which scope that is.</summary>
        public readonly int[] ParentField = Filled(scan.Parent.Count);
        public readonly int[] ParentDisplay = Filled(scan.Parent.Count);

        /// <summary>The nearest scope at or around s that has a display (-1: none).</summary>
        public int DisplayScope(int s)
        {
            while (s >= 0 && DisplayHandle[s] is null)
                s = Scan.Parent[s];
            return s;
        }

        /// <summary>The display object's field of each captured local, parameter and <c>this</c> (-1: not captured).</summary>
        public readonly int[] LocalField = Filled(locals);
        public readonly int[] ParameterField = Filled(parameters);
        public int ThisField = -1;

        public readonly int[] LambdaFunction = Filled(scan.Lambdas.Count);

        private static int[] Filled(int count)
        {
            var array = new int[count];
            Array.Fill(array, -1);
            return array;
        }
    }

    /// <summary>
    /// Lambdas. Each becomes a function of its own, <c>{method}$lambda{k}(env, parameters…)</c>, with the method's type
    /// parameters (and their requirements); a lambda value is a <c>Larva.Func{n}</c> object holding that function and the
    /// environment. What lambdas capture (locals, parameters, <c>this</c>) lives in display objects the method and its
    /// lambdas share: one per method call (made at the method's entry) for what is declared outside loops, and one per
    /// pass of a loop for what its body declares (made at the start of each pass, pointing to the display around it), so
    /// a lambda made in a pass keeps that pass's variables. The environment of a lambda is the innermost display.
    /// </summary>
    internal sealed partial class BodyLowerer
    {
        private Closures? closures;

        /// <summary>The lambda whose function this lowerer makes (-1: the method itself).</summary>
        private readonly int lambda = -1;

        /// <summary>The local holding the display object (-1: none): in a lambda, its environment.</summary>
        private int display = -1;

        /// <summary>In a lambda: the scope of its environment display. In the method: the local holding each scope's display.</summary>
        private int displayScope = -1;
        private int[] displays = [];

        /// <summary>The function's result type.</summary>
        private readonly IrType result;

        private IrType[] OwnTypeArguments => [.. Enumerable.Range(0, plan.Generics.Length).Select(i => (IrType)new IR.GenericParamType(i))];

        /// <summary>Scans the body for lambdas; when they capture anything, lays out the display object and makes it.</summary>
        private void PrepareClosures(H.HirBody body)
        {
            var scan = CaptureScan.Run(body, parameters.Length);
            if (scan is null)
                return;
            var c = closures = new Closures(scan, body.Locals.Length, parameters.Length);
            // Scopes in order: a scope's parent comes before it, so its display type is known.
            for (int s = 0; s < scan.Parent.Count; s++)
            {
                if (!scan.ScopeNeedsDisplay(s))
                    continue;
                var fields = new List<IrType> { IrType.I32, HirLowerer.VtablePointer(IrType.Struct(lowerer.ObjectVtable)) };
                var names = new List<string> { "$class", "$vt" };
                int around = s == 0 ? -1 : c.DisplayScope(scan.Parent[s]);
                if (around >= 0)
                {
                    c.ParentDisplay[s] = around;
                    c.ParentField[s] = fields.Count;
                    fields.Add(c.DisplayHandle[around]!);
                    names.Add("$parent");
                }
                for (int l = 0; l < body.Locals.Length; l++)
                {
                    if (!scan.Captured[l] || scan.Scope[l] != s)
                        continue;
                    c.LocalField[l] = fields.Count;
                    fields.Add(Lower(body.Locals[l].Type));
                    names.Add(body.Locals[l].Name);
                }
                if (s == 0)
                {
                    for (int p = 0; p < parameters.Length; p++)
                    {
                        if (!scan.CapturedParameters[p])
                            continue;
                        c.ParameterField[p] = fields.Count;
                        fields.Add(plan.Parameters[p]);
                        names.Add(method.Parameters[p].Name);
                    }
                    if (scan.This)
                    {
                        if (self < 0 || !owner.IsClass)
                            throw new LoweringException("a lambda cannot use `this` of a struct (copy what it needs into a local first)");
                        c.ThisField = fields.Count;
                        fields.Add(OwnHandle);
                        names.Add("this");
                    }
                }
                int id;
                lock (lowerer.Structs)
                {
                    id = lowerer.Structs.Count;
                    lowerer.Structs.Add(new StructDef(s == 0 ? $"{plan.Name}$display" : $"{plan.Name}$display{s}", [.. fields])
                    {
                        FieldNames = [.. names],
                        Mutable = true,
                        Base = (StructType)IrType.Struct(lowerer.ObjectStruct),
                        Linkage = Linkage.Internal,
                        // The method's type parameters with their constraints (a field may be a type that requires them).
                        Generics = [.. plan.Generics.Select(g => new GenericParam(g.Name, g.Constraint))],
                    });
                }
                moduleView = null;
                c.DisplayHandle[s] = lowerer.Handle(IrType.Struct(id, OwnTypeArguments));
            }
            displays = new int[scan.Parent.Count];
            Array.Fill(displays, -1);
            for (int s = 0; s < displays.Length; s++)
            {
                if (c.DisplayHandle[s] is { } handle)
                    displays[s] = b.Local(handle, s == 0 ? "$display" : $"$display{s}", mutable: true);
            }
            if (displays[0] < 0)
                return;
            NewDisplay(0);
            for (int p = 0; p < parameters.Length; p++)
            {
                if (c.ParameterField[p] >= 0)
                    b.Assign(DisplayPlace(0, c.ParameterField[p]), Copied(new Place(parameters[p]), method.Parameters[p].Type));
            }
            if (c.ThisField >= 0)
                b.Assign(DisplayPlace(0, c.ThisField), Operand.Copy(self));
        }

        /// <summary>A new display object for scope s (in the method), pointing to the display around it.</summary>
        private void NewDisplay(int s)
        {
            var c = closures!;
            var handle = c.DisplayHandle[s]!;
            var structType = handle switch
            {
                GcType { Pointee: StructType st } => st,
                IR.PointerType { Pointee: StructType st } => st,
                _ => throw new LoweringException($"a display of type {handle}"),
            };
            int value = Temp(structType);
            b.Assign(value, Rvalue.ZeroFiller(structType));
            b.Assign(new Place(value).Field(0), Rvalue.ClassId(structType));
            if (c.ParentField[s] >= 0)
                b.Assign(new Place(value).Field(c.ParentField[s]), Operand.Copy(displays[c.ParentDisplay[s]]));
            b.Assign(displays[s], new AllocRvalue(lowerer.ObjectAlloc, Operand.Move(value)));
        }

        /// <summary>At the start of each pass of a loop: its body's display, when it keeps captured variables.</summary>
        private void EnterIteration(H.Stmt body)
        {
            if (lambda < 0 && closures is { } c && c.Scan.BodyScope.TryGetValue(body, out int s) && c.DisplayHandle[s] is not null)
                NewDisplay(s);
        }

        /// <summary>Field <paramref name="field"/> of scope <paramref name="s"/>'s display, as seen from here.</summary>
        private Place DisplayPlace(int s, int field)
        {
            if (lambda < 0)
                return new Place(displays[s]).Deref().Field(field);
            // In a lambda: from its environment out through the parents.
            var c = closures!;
            int at = display, scope = displayScope;
            while (scope != s)
            {
                if (scope < 0 || c.ParentField[scope] < 0)
                    throw new LoweringException("a lambda reaches a variable outside its environment");
                int parent = Temp(c.DisplayHandle[c.ParentDisplay[scope]]!);
                b.Assign(parent, Operand.Copy(new Place(at).Deref().Field(c.ParentField[scope])));
                at = parent;
                scope = c.ParentDisplay[scope];
            }
            return new Place(at).Deref().Field(field);
        }

        private Place LocalPlace(int local)
        {
            if (locals[local] >= 0)
                return new Place(locals[local]);
            if (closures is { } c && c.LocalField[local] >= 0)
                return DisplayPlace(c.Scan.Scope[local], c.LocalField[local]);
            throw new LoweringException($"the local {hirLocals[local].Name} is not reachable here");
        }

        private Place ParamPlace(int parameter)
        {
            if (IsCoroutine)
                return IteratorField(iteratorParameters[parameter]);
            if ((method.Flags & (H.MethodFlags.Iterator | H.MethodFlags.Async)) != 0)
                throw new LoweringException("a lambda in a function with `yield` or `await` uses one of its parameters (copy it into a local first)");
            if (closures is { } c && c.ParameterField[parameter] >= 0)
                return DisplayPlace(0, c.ParameterField[parameter]);
            if (lambda >= 0)
                throw new LoweringException($"a lambda uses the parameter {method.Parameters[parameter].Name}, which is not captured");
            return new Place(parameters[parameter]);
        }

        private Place ThisInLambda()
        {
            if (closures is not { ThisField: >= 0 } c)
                throw new LoweringException("a lambda uses `this`, which is not captured");
            return DisplayPlace(0, c.ThisField);
        }

        /// <summary>The lowerer of lambda <paramref name="index"/>'s function, inside <paramref name="outer"/>'s method.</summary>
        private BodyLowerer(BodyLowerer outer, int index)
        {
            lowerer = outer.lowerer;
            plan = outer.plan;
            owner = outer.owner;
            method = outer.method;
            context = outer.context;
            closures = outer.closures!;
            lambda = index;
            var scan = closures.Scan;
            var hir = scan.Lambdas[index];
            var type = (H.FuncType)hir.Type;
            result = Lower(type.Result);
            b = new FunctionBuilder($"{plan.Name}$lambda{index}", result) { Linkage = Linkage.Internal };
            HirLowerer.DeclareGenerics(b, plan.Generics);
            int environment = b.Argument(lowerer.RootHandle, "$env", mutable: false);
            parameters = [];
            hirLocals = outer.hirLocals;
            locals = new int[hirLocals.Length];
            Array.Fill(locals, -1);
            var arguments = new int[hir.Parameters.Length];
            for (int p = 0; p < arguments.Length; p++)
            {
                int local = hir.Parameters[p];
                arguments[p] = b.Argument(Lower(hirLocals[local].Type), hirLocals[local].Name, mutable: true);
                if (!scan.Captured[local])
                    locals[local] = arguments[p];
            }
            for (int l = 0; l < locals.Length; l++)
            {
                if (scan.Owner[l] == index + 1 && !scan.Captured[l] && locals[l] < 0)
                    locals[l] = b.Local(Lower(hirLocals[l].Type), hirLocals[l].Name, mutable: true);
            }
            displayScope = closures.DisplayScope(scan.LambdaScope[index]);
            if (displayScope >= 0)
            {
                var handle = closures.DisplayHandle[displayScope]!;
                display = b.Local(handle, "$display", mutable: true);
                b.Unsafe = true;
                b.Assign(display, Rvalue.HandleCast(Operand.Copy(environment), handle));
                for (int p = 0; p < arguments.Length; p++)
                {
                    int local = hir.Parameters[p];
                    if (scan.Captured[local])
                        b.Assign(LocalPlace(local), Copied(new Place(arguments[p]), hirLocals[local].Type));
                }
            }
        }

        private IrFunction LowerLambda()
        {
            Statement(closures!.Scan.Lambdas[lambda].Body);
            if (open)
            {
                if (result.IsUnit)
                    b.Return();
                else
                    b.Unreachable();
            }
            return b.Build();
        }

        /// <summary>A lambda's value: a new function object with the lambda's function and the display object.</summary>
        private void Closure(H.LambdaExpr l, Place target, IrType type)
        {
            var c = closures ?? throw new LoweringException("a lambda outside the scanned body");
            int k = c.Scan.Index[l];
            if (c.LambdaFunction[k] < 0)
            {
                var function = new BodyLowerer(this, k).LowerLambda();
                lock (lowerer.Functions)
                {
                    c.LambdaFunction[k] = lowerer.Functions.Count;
                    lowerer.Functions.Add(function);
                }
            }
            var structType = type switch
            {
                GcType { Pointee: StructType s } => s,
                IR.PointerType { Pointee: StructType s } => s,
                _ => throw new LoweringException($"a lambda of type {type}"),
            };
            int value = Temp(structType);
            b.Assign(value, Rvalue.ZeroFiller(structType));
            b.Assign(new Place(value).Field(0), Rvalue.ClassId(structType));
            b.Assign(new Place(value).Field(2), Rvalue.FunctionRef(c.LambdaFunction[k], OwnTypeArguments));
            // The environment: the innermost display around the lambda (in a lambda: its own environment).
            int environment = lambda >= 0 ? display : c.DisplayScope(c.Scan.LambdaScope[k]) is var around && around >= 0 ? displays[around] : -1;
            if (environment >= 0)
                b.Assign(new Place(value).Field(3), Rvalue.HandleCast(Operand.Copy(environment), lowerer.RootHandle));
            int made = Temp(type);
            b.Assign(made, new AllocRvalue(lowerer.ObjectAlloc, Operand.Move(value)));
            b.Assign(target, Operand.Copy(made));
        }

        /// <summary>A call of a function value: its function, with its environment and the arguments.</summary>
        private void Invoke(H.InvokeExpr invoke, Place target)
        {
            var handleType = Lower(invoke.Function.Type);
            var structType = handleType switch
            {
                GcType { Pointee: StructType s } => s,
                IR.PointerType { Pointee: StructType s } => s,
                _ => throw new LoweringException($"calls a {handleType}"),
            };
            int n = structType.Arguments.Length - 1;
            var functionType = new FunctionType([lowerer.RootHandle, .. structType.Arguments[..n]], structType.Arguments[n]);
            var handle = HandleLocal(invoke.Function);
            int function = Temp(functionType);
            int environment = Temp(lowerer.RootHandle);
            b.Assign(function, Operand.Copy(handle.Deref().Field(2)))
             .Assign(environment, Operand.Copy(handle.Deref().Field(3)));
            var arguments = new List<Operand> { Operand.Copy(environment) };
            foreach (var argument in invoke.Arguments)
                arguments.Add(Value(argument));
            int next = NewBlock();
            b.CallIndirect(Operand.Copy(function), [.. arguments], target, next, Handlers());
            At(next);
        }
    }
}

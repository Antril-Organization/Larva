using System.Numerics;

namespace Larva.Hir.Lowering
{
    using Larva.IR;
    using H = Larva.Hir;

    public sealed partial class HirLowerer
    {
        private void LowerBodies()
        {
            if (Options.Parallel)
                System.Threading.Tasks.Parallel.ForEach(Plans, LowerPlan);
            else
                Plans.ForEach(LowerPlan);
            foreach (var info in Types.Where(i => i.IsClass))
                Functions[info.VtableMaker] = VtableMaker(info);
        }

        /// <summary>Declares a function's type parameters (with their requirements) on its builder.</summary>
        internal static void DeclareGenerics(FunctionBuilder b, GenericParam[] generics)
        {
            foreach (var g in generics)
                b.Generic(g.Name, g.Constraint);
            for (int g = 0; g < generics.Length; g++)
            {
                foreach (var requirement in generics[g].Requirements)
                    b.Require(g, requirement);
            }
        }

        private void LowerPlan(MethodPlan plan)
        {
            var info = Types[plan.Type];
            var method = plan.Synthetic ?? info.Definition.Methods[plan.Method];
            string where = $"{info.Name}.{method.Name}";
            try
            {
                var function = new BodyLowerer(this, plan, info, method).Lower();
                lock (Functions)
                    Functions[plan.Function] = function;
            }
            catch (LoweringException e)
            {
                Error(where, e.Message);
                var empty = new FunctionBuilder(plan.Name, plan.Result);
                DeclareGenerics(empty, plan.Generics);
                empty.Unreachable();
                var built = empty.Build();
                lock (Functions)
                    Functions[plan.Function] = built;
            }
        }

        /// <summary><c>C.$vtable()</c>: the class's vtable, made (and kept in its global) the first time.</summary>
        private IrFunction VtableMaker(TypeInfo info)
        {
            var own = info.OwnArguments;
            var vtType = IrType.Struct(info.Vtable, own);
            var handle = VtablePointer(vtType);
            var b = new FunctionBuilder(info.Name + ".$vtable", handle) { Unsafe = true };
            DeclareGenerics(b, FunctionGenerics(info, null, info.Name));
            // The global holds the vtable's address (0 until made): plain data, read with one load.
            int current = b.Local(IrType.U64, null, mutable: true);
            int isNull = b.Local(IrType.Bool, null, mutable: true);
            int make = b.NewBlock(), done = b.NewBlock();
            b.Assign(current, Rvalue.ReadGlobal(info.VtableGlobal, own))
             .Assign(isNull, Rvalue.Binary(BinaryOp.Eq, Operand.Copy(current), Operand.Const(0, IrType.U64)))
             .If(Operand.Copy(isNull), make, done);
            b.At(make);
            int table = b.Local(vtType, null, mutable: true);
            b.Assign(table, Rvalue.ZeroFiller(vtType));
            FillInterfaces(b, new Place(table), ClassInterfaces(info));
            for (int s = 0; s < info.Slots.Count; s++)
                b.Assign(new Place(table).Field(s + SlotBase), Rvalue.FunctionRef(MethodFunctions[H.MemberToken.Def(info.Slots[s].Type, info.Slots[s].Method)], AncestorIrArguments(info, info.Slots[s].Type)));
            int made = b.Local(handle, null, mutable: true);
            b.Assign(made, new AllocRvalue(AllocKind.Raw, Operand.Move(table)));
            int address = b.Local(IrType.U64, null, mutable: true);
            b.Assign(address, Rvalue.Cast(Operand.Copy(made), IrType.U64))
             .StoreGlobal(info.VtableGlobal, own, Operand.Copy(address))
             .Assign(FunctionBuilder.ReturnPlace, Operand.Copy(made))
             .Return();
            b.At(done);
            b.Assign(FunctionBuilder.ReturnPlace, Rvalue.Cast(Operand.Copy(current), handle)).Return();
            return b.Build();
        }
    }

    internal sealed class LoweringException(string message) : Exception(message);

    /// <summary>One method body: HIR statements and expressions into IR blocks.</summary>
    internal sealed partial class BodyLowerer
    {
        private readonly HirLowerer lowerer;
        private readonly HirLowerer.MethodPlan plan;
        private readonly HirLowerer.TypeInfo owner;
        private readonly H.MethodDef method;
        private readonly FunctionBuilder b;
        private readonly int[] parameters;
        private int[] locals = [];
        private H.HirLocal[] hirLocals = [];

        /// <summary>The receiver as its own class's handle (classes), or the <c>&amp;mut</c> (structs); -1 when static.</summary>
        private int self = -1;

        /// <summary>Whether the current block can still take statements (false after a return, throw, break…).</summary>
        private bool open = true;

        /// <summary>A try (its handlers, its finally) or a region (<see cref="Region"/> ≥ 0: closed on every way out).</summary>
        private sealed record TryFrame(CatchClause[] Handlers, H.BlockStmt? Finally, int Region = -1);

        private int regionCount;

        private sealed record LoopFrame(int Label, int Break, int Continue, int TryDepth);

        private readonly List<TryFrame> tries = [];

        /// <summary>The receivers of the <c>?.</c> accesses being lowered (non-null values in locals), innermost last.</summary>
        private readonly List<int> receivers = [];
        private readonly List<LoopFrame> loops = [];

        public BodyLowerer(HirLowerer lowerer, HirLowerer.MethodPlan plan, HirLowerer.TypeInfo owner, H.MethodDef method)
        {
            this.lowerer = lowerer;
            this.plan = plan;
            this.owner = owner;
            this.method = method;
            result = plan.Result;
            b = new FunctionBuilder(plan.Name, plan.Result) { Linkage = method.Access == H.Access.Public && HirLowerer.LinkageOf(owner) == Linkage.Export ? Linkage.Export : Linkage.Internal };
            HirLowerer.DeclareGenerics(b, plan.Generics);
            if ((method.Flags & H.MethodFlags.Unsafe) != 0 || (owner.Definition.Flags & H.TypeFlags.Unsafe) != 0)
                b.Unsafe = true;
            context = ContextOf(lowerer, owner, method);
            int thisArgument = plan.This is { } t ? b.Argument(t, "this", mutable: false) : -1;
            parameters = new int[plan.Parameters.Length];
            for (int p = 0; p < parameters.Length; p++)
                parameters[p] = b.Argument(plan.Parameters[p], method.Parameters[p].Name, mutable: true);
            if (thisArgument >= 0)
            {
                self = thisArgument;
                if (owner.IsClass && plan.This != OwnHandle)
                {
                    // An implementation of a virtual method introduced by a base: `this` comes as the base; it is this class.
                    self = b.Local(OwnHandle, "self");
                    b.Unsafe = true;
                    b.Assign(self, Rvalue.HandleCast(Operand.Copy(thisArgument), OwnHandle));
                }
            }
        }

        private IrType OwnHandle => lowerer.Handle(lowerer.StructOf(owner, owner.OwnArguments));

        private readonly HirLowerer.GenericContext context;

        private string Where => plan.Name;

        private LoweringException Unsupported(string what) => new($"{what} is lowered in a later phase");

        public IrFunction Lower()
        {
            var function = LowerBody();
            if (IsCoroutine)
                function = MarkCoroutine(function);
            if ((method.Flags & H.MethodFlags.Export) != 0 && method.ExternName is { } exported)
                function = function with { Linkage = Linkage.Export, Attributes = [.. function.Attributes, new IrAttribute("Larva.Export", [new TextArgument(exported)])] };
            if ((method.Flags & H.MethodFlags.Cold) != 0)
                function = function with { Attributes = [.. function.Attributes, new IrAttribute(IrFunction.ColdAttribute, [])] };
            if ((method.Flags & (H.MethodFlags.Inline | H.MethodFlags.NoInline)) == 0)
                return function;
            var name = (method.Flags & H.MethodFlags.Inline) != 0 ? IrFunction.InlineAttribute : IrFunction.NoInlineAttribute;
            return function with { Attributes = [.. function.Attributes, new IrAttribute(name, [])] };
        }

        private IrFunction LowerBody()
        {
            if ((method.Flags & H.MethodFlags.Import) != 0)
                return Import();
            if ((method.Flags & H.MethodFlags.Extern) != 0)
                return Extern();
            if (method.Body is not { } body)
            {
                if ((method.Flags & H.MethodFlags.Abstract) == 0)
                    throw new LoweringException("has no body");
                // An abstract method's slot in its own class's vtable: never called (the class is never made).
                Panic();
                return b.Build();
            }
            hirLocals = body.Locals;
            if (IsCoroutine)
                PrepareCoroutine();
            PrepareClosures(body);
            locals = new int[body.Locals.Length];
            for (int l = 0; l < locals.Length; l++)
            {
                // Captured locals live in the display object; a lambda's own locals in the lambda's function.
                locals[l] = closures is { } c && (c.Scan.Captured[l] || c.Scan.Owner[l] > 0) ? -1
                    : b.Local(Lower(body.Locals[l].Type), body.Locals[l].Name, mutable: true);
            }
            if (plan.Synthetic is null && lowerer.StaticInitFunction >= 0
                && (lowerer.Assembly.EntryPoint == H.MemberToken.Def(plan.Type, plan.Method) || (method.Flags & H.MethodFlags.Export) != 0))
            {
                // The program's entry runs every static constructor first.
                Emit(lowerer.StaticInitFunction, [], new Place(Temp(IrType.Unit)));
            }
            Block(body.Block);
            if (open && IsIterator)
                EndIterator();
            else if (open && IsAsync)
                AsyncReturn(null);
            if (open)
            {
                if (result.IsUnit)
                    b.Return();
                else
                    b.Unreachable();
            }
            return b.Build();
        }

        private IrType Lower(H.HirType type) => lowerer.Lower(type, Where, context);

        /// <summary>
        /// The compiler's resource services (<c>extern("$resources.…")</c> in System.Resources), written from the assembly's
        /// resources: <c>count() : i32</c>; by index, <c>name(i) : u8[]</c> (UTF-8), <c>bytes(i) : u8[]</c> (a copy),
        /// <c>length(i) : i32</c> and <c>data(i) : *u8</c> (the static data itself). All the bytes are one static blob and
        /// the starts one table of <c>u64</c> (count + 1 entries: where each one starts, then the end), so every service is
        /// two table reads whatever the number of resources. An index out of range gives an empty array, 0, or a pointer
        /// to the blob's filler byte.
        /// </summary>
        private IrFunction ResourceService(string name)
        {
            var resources = lowerer.Assembly.Resources;
            string kind = name["$resources.".Length..];
            if (kind == "count")
            {
                if (parameters.Length != 0)
                    throw new LoweringException($"\"{name}\" takes no value");
                b.Assign(FunctionBuilder.ReturnPlace, Operand.Const(resources.Count, plan.Result)).Return();
                return b.Build();
            }
            if (kind is not ("name" or "bytes" or "length" or "data") || parameters.Length != 1 || !plan.Parameters[0].IsInteger)
                throw new LoweringException($"there is no resource service \"{name}\" taking these values");
            var indexType = plan.Parameters[0];
            b.Unsafe = true;
            // The blob (every resource's bytes, then one filler byte: pointers are never null and the end is a valid index)
            // and the starts.
            var blob = new List<byte>();
            var starts = new List<System.Numerics.BigInteger>();
            foreach (var resource in resources)
            {
                starts.Add(blob.Count);
                blob.AddRange(kind == "name" ? System.Text.Encoding.UTF8.GetBytes(resource.Name) : resource.Data);
            }
            starts.Add(blob.Count);
            blob.Add(0);
            var arrayOfBytes = new IR.ArrayType(IrType.U8);
            int bytes = Temp(IrType.Ref(arrayOfBytes)), table = Temp(IrType.Ref(new IR.ArrayType(IrType.U64)));
            b.Assign(bytes, new StaticRvalue(lowerer.ConstantData(H.Primitive.U8, blob.Select(x => (System.Numerics.BigInteger)x))))
             .Assign(table, new StaticRvalue(lowerer.ConstantData(H.Primitive.U64, starts)));
            // 0 <= i < count, else the defaults.
            int low = Temp(IrType.Bool), high = Temp(IrType.Bool);
            int checkHigh = NewBlock(), found = NewBlock(), missing = NewBlock();
            b.Assign(low, Rvalue.Binary(BinaryOp.Ge, Operand.Copy(parameters[0]), Operand.Const(0, indexType))).If(Operand.Copy(low), checkHigh, missing);
            b.At(checkHigh).Assign(high, Rvalue.Binary(BinaryOp.Lt, Operand.Copy(parameters[0]), Operand.Const(resources.Count, indexType))).If(Operand.Copy(high), found, missing);
            b.At(found);
            int index = Temp(IrType.U64), next = Temp(IrType.U64), first = Temp(IrType.U64), end = Temp(IrType.U64), length = Temp(IrType.U64);
            b.Assign(index, Rvalue.Cast(Operand.Copy(parameters[0]), IrType.U64))
             .Assign(next, Rvalue.Binary(BinaryOp.Add, Operand.Copy(index), Operand.Const(1, IrType.U64)))
             .Assign(first, Operand.Copy(new Place(table).Deref().Index(index)))
             .Assign(end, Operand.Copy(new Place(table).Deref().Index(next)))
             .Assign(length, Rvalue.Binary(BinaryOp.Sub, Operand.Copy(end), Operand.Copy(first)));
            switch (kind)
            {
                case "length":
                    b.Assign(FunctionBuilder.ReturnPlace, Rvalue.Cast(Operand.Copy(length), plan.Result)).Return();
                    break;
                case "data":
                    PointTo(first);
                    break;
                default:
                {
                    // A new array with the bytes from `first`, `length` of them: one mem.copy (none for an empty one).
                    int made = Temp(plan.Result), some = Temp(IrType.Bool);
                    int copy = NewBlock(), done = NewBlock();
                    b.Assign(made, new ArrayRvalue(lowerer.ObjectAlloc, IrType.U8, Operand.Copy(length)))
                     .Assign(some, Rvalue.Binary(BinaryOp.Gt, Operand.Copy(length), Operand.Const(0, IrType.U64)))
                     .If(Operand.Copy(some), copy, done);
                    var pointer = IrType.PtrMut(IrType.U8);
                    int zero = Temp(IrType.U64), targetRef = Temp(IrType.RefMut(IrType.U8)), sourceRef = Temp(IrType.Ref(IrType.U8));
                    int target = Temp(pointer), source = Temp(pointer), count = Temp(IrType.I64), moved = Temp(IrType.Unit);
                    b.At(copy)
                     .Assign(zero, Operand.Const(0, IrType.U64))
                     .Assign(targetRef, Rvalue.RefMut(new Place(made).Deref().Index(zero)))
                     .Assign(target, Rvalue.Cast(Operand.Move(targetRef), pointer))
                     .Assign(sourceRef, Rvalue.Ref(new Place(bytes).Deref().Index(first)))
                     .Assign(source, Rvalue.Cast(Operand.Move(sourceRef), pointer))
                     .Assign(count, Rvalue.Cast(Operand.Copy(length), IrType.I64))
                     .Assign(moved, new IntrinsicRvalue("mem.copy", [Operand.Copy(target), Operand.Copy(source), Operand.Copy(count)]))
                     .Goto(done);
                    b.At(done).Assign(FunctionBuilder.ReturnPlace, Operand.Copy(made)).Return();
                    break;
                }
            }
            b.At(missing);
            switch (kind)
            {
                case "name" or "bytes":
                    b.Assign(FunctionBuilder.ReturnPlace, new ArrayRvalue(lowerer.ObjectAlloc, IrType.U8, Operand.Const(0, IrType.U64))).Return();
                    break;
                case "length":
                    b.Assign(FunctionBuilder.ReturnPlace, Operand.Const(0, plan.Result)).Return();
                    break;
                default:
                    int filler = Temp(IrType.U64);
                    b.Assign(filler, Operand.Const(blob.Count - 1, IrType.U64));
                    PointTo(filler);
                    break;
            }
            return b.Build();

            // A pointer to the blob's byte at `offset` (static data: it lives as long as the program).
            void PointTo(int offset)
            {
                int reference = Temp(IrType.Ref(IrType.U8));
                b.Assign(reference, Rvalue.Ref(new Place(bytes).Deref().Index(offset)))
                 .Assign(FunctionBuilder.ReturnPlace, Rvalue.Cast(Operand.Move(reference), plan.Result))
                 .Return();
            }
        }

        /// <summary>
        /// An <c>extern</c> method: a call to the runtime service it names. Arrays go to the service as borrows of their
        /// elements (<c>&amp;[T]</c>), numbers are converted to the service's <c>i64</c> and back.
        /// </summary>
        private IrFunction Extern()
        {
            string name = method.ExternName ?? method.Name;
            if (name.StartsWith("$resources.", StringComparison.Ordinal))
                return ResourceService(name);
            var service = Intrinsics.Find(name) ?? throw new LoweringException($"there is no runtime service \"{name}\"");
            int callbacks = service.Parameters.Count(t => ReferenceEquals(t, Intrinsics.Callback));
            if (service.Parameters.Length - callbacks != parameters.Length || self >= 0)
                throw new LoweringException($"the runtime service \"{name}\" takes {service.Parameters.Length - callbacks} values");
            var arguments = new List<Operand>();
            for (int p = 0; p < parameters.Length; p++)
            {
                var want = service.Parameters[arguments.Count];
                var have = plan.Parameters[p];
                if (ReferenceEquals(want, Intrinsics.Callback))
                {
                    // A function value (a Larva.Func object): its function and its environment.
                    var structType = have switch
                    {
                        GcType { Pointee: StructType s } => s,
                        IR.PointerType { Pointee: StructType s } => s,
                        _ => throw new LoweringException($"the runtime service \"{name}\" takes a function, not a {have}"),
                    };
                    int n = structType.Arguments.Length - 1;
                    var functionType = new FunctionType([lowerer.RootHandle, .. structType.Arguments[..n]], structType.Arguments[n]);
                    int code = Temp(functionType);
                    int environment = Temp(lowerer.RootHandle);
                    var handle = new Place(parameters[p]);
                    b.Assign(code, Operand.Copy(handle.Deref().Field(2)))
                     .Assign(environment, Operand.Copy(handle.Deref().Field(3)));
                    arguments.Add(Operand.Copy(code));
                    arguments.Add(Operand.Copy(environment));
                    continue;
                }
                if (want == have)
                {
                    arguments.Add(Operand.Copy(parameters[p]));
                }
                else if (want is IR.ReferenceType { Pointee: IR.ArrayType } r && have is GcType or IR.PointerType)
                {
                    b.Unsafe = true;
                    int borrow = Temp(r);
                    b.Assign(borrow, r.Mutable ? Rvalue.RefMut(new Place(parameters[p]).Deref()) : Rvalue.Ref(new Place(parameters[p]).Deref()));
                    arguments.Add(r.Mutable ? Operand.Move(borrow) : Operand.Copy(borrow));
                }
                else if (have == IrType.Bool && want.IsInteger)
                {
                    int converted = Temp(want);
                    int one = NewBlock(), zero = NewBlock(), next = NewBlock();
                    b.If(Operand.Copy(parameters[p]), one, zero);
                    b.At(one).Assign(converted, Operand.Const(1, want)).Goto(next);
                    b.At(zero).Assign(converted, Operand.Const(0, want)).Goto(next);
                    b.At(next);
                    arguments.Add(Operand.Copy(converted));
                }
                else
                {
                    int converted = Temp(want);
                    b.Assign(converted, Rvalue.Cast(Operand.Copy(parameters[p]), want));
                    arguments.Add(Operand.Copy(converted));
                }
            }
            var call = new IntrinsicRvalue(name, [.. arguments]);
            if (plan.Result == service.Result)
            {
                b.Assign(FunctionBuilder.ReturnPlace, call);
            }
            else
            {
                int raw = Temp(service.Result);
                b.Assign(raw, call);
                if (plan.Result == IrType.Bool)
                    b.Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Ne, Operand.Copy(raw), Operand.Const(0, service.Result)));
                else if (!plan.Result.IsUnit)
                    b.Assign(FunctionBuilder.ReturnPlace, Rvalue.Cast(Operand.Copy(raw), plan.Result));
            }
            b.Return();
            return b.Build();
        }

        // ── Blocks ─────────────────────────────────────────

        private int NewBlock() => b.NewBlock();

        private void At(int block)
        {
            b.At(block);
            open = true;
        }

        private void Goto(int block)
        {
            if (open)
                b.Goto(block);
            open = false;
        }

        /// <summary>After a terminator: what follows is unreachable, but still lowered into a block of its own.</summary>
        private void Dead()
        {
            open = false;
            int dead = NewBlock();
            b.At(dead);
            open = true;
            deadBlocks.Add(dead);
        }

        private readonly HashSet<int> deadBlocks = [];

        /// <summary>The handlers of the try frames around, innermost first; each one handed to a terminator is marked used.</summary>
        private CatchClause[] Handlers()
        {
            CatchClause[] handlers = [.. Enumerable.Reverse(tries).SelectMany(t => t.Handlers)];
            foreach (var handler in handlers)
                usedHandlers.Add(handler.Block);
            return handlers;
        }

        /// <summary>Handler blocks some call, throw or resume can reach (the others are never entered).</summary>
        private readonly HashSet<int> usedHandlers = [];

        private int Temp(IrType type) => b.Local(type, null, mutable: true);

        private void Panic()
        {
            int none = Temp(new OptionalType(lowerer.RootHandle));
            int value = Temp(lowerer.RootHandle);
            b.Assign(none, Rvalue.Zero(new OptionalType(lowerer.RootHandle)))
             .Assign(value, Rvalue.Unwrap(Operand.Copy(none)));
            b.Unreachable();
            open = false;
        }

        // ── Statements ─────────────────────────────────────

        private void Block(H.BlockStmt block)
        {
            foreach (var statement in block.Statements)
                Statement(statement);
        }

        private void Statement(H.Stmt statement)
        {
            if (!open)
                Dead();
            // Where it came from, for the IR's diagnostics (a borrow error names its line).
            if (statement.At.IsKnown && statement.At.File < lowerer.Assembly.Files.Count)
                b.Position = new SourcePosition(lowerer.Assembly.Files[statement.At.File], statement.At.Line, statement.At.Column);
            switch (statement)
            {
                case H.BlockStmt block:
                    Block(block);
                    break;
                case H.LocalStmt local:
                    if (local.Value is { } initial)
                        Into(LocalPlace(local.Local), initial);
                    break;
                case H.ExprStmt e:
                    Discard(e.Expression);
                    break;
                case H.IfStmt s:
                {
                    int then = NewBlock(), after = NewBlock();
                    int otherwise = s.Else is null ? after : NewBlock();
                    Condition(s.Condition, then, otherwise);
                    At(then);
                    Statement(s.Then);
                    Goto(after);
                    if (s.Else is { } e)
                    {
                        At(otherwise);
                        Statement(e);
                        Goto(after);
                    }
                    At(after);
                    break;
                }
                case H.WhileStmt s:
                {
                    int head = NewBlock(), body = NewBlock(), after = NewBlock();
                    Goto(head);
                    At(head);
                    Condition(s.Condition, body, after);
                    At(body);
                    Loop(s.Label, after, head, s.Body);
                    Goto(head);
                    At(after);
                    break;
                }
                case H.DoWhileStmt s:
                {
                    int body = NewBlock(), check = NewBlock(), after = NewBlock();
                    Goto(body);
                    At(body);
                    Loop(s.Label, after, check, s.Body);
                    Goto(check);
                    At(check);
                    Condition(s.Condition, body, after);
                    At(after);
                    break;
                }
                case H.ForStmt s:
                {
                    foreach (var init in s.Initializers)
                        Statement(init);
                    int head = NewBlock(), body = NewBlock(), step = NewBlock(), after = NewBlock();
                    Goto(head);
                    At(head);
                    if (s.Condition is { } c)
                        Condition(c, body, after);
                    else
                        Goto(body);
                    At(body);
                    Loop(s.Label, after, step, s.Body);
                    Goto(step);
                    At(step);
                    foreach (var e in s.Steps)
                        Discard(e);
                    Goto(head);
                    At(after);
                    break;
                }
                case H.ForeachStmt s:
                    Foreach(s);
                    break;
                case H.SwitchStmt s:
                    Switch(s);
                    break;
                case H.BreakStmt s:
                {
                    var loop = FindLoop(s.Label);
                    RunFinally(loop.TryDepth);
                    Goto(loop.Break);
                    break;
                }
                case H.ContinueStmt s:
                {
                    var loop = FindLoop(s.Label);
                    if (loop.Continue < 0)
                        throw new LoweringException("`continue` inside a switch that is not in a loop");
                    RunFinally(loop.TryDepth);
                    Goto(loop.Continue);
                    break;
                }
                case H.ReturnStmt s when IsAsync:
                    AsyncReturn(s.Value);
                    break;
                case H.ReturnStmt s:
                    if (s.Value is { } value)
                        Into(FunctionBuilder.ReturnPlace, value);
                    RunFinally(0);
                    if (open)
                        b.Return();
                    open = false;
                    break;
                case H.ThrowStmt s:
                    Throw(s.Value);
                    break;
                case H.TryStmt s:
                    Try(s);
                    break;
                case H.DeleteStmt s:
                    if (!lowerer.Options.Gc.ManagesObjects)
                    {
                        int handle = Temp(Lower(s.Value.Type));
                        Into(new Place(handle), s.Value);
                        // Manual: the object's destructor (dispatched: the object's own class's), then the memory goes.
                        if (s.Value.Type is H.NamedType { Type.IsDefinition: true } deleted && lowerer.Types[deleted.Type.Index] is { IsClass: true } deletedInfo
                            && lowerer.DestructorOf(deletedInfo) is var (declaring, destructor) && declaring >= 0 && lowerer.Types[declaring].Base >= 0)
                        {
                            var call = new H.CallExpr(new HeldExpr(handle, s.Value.Type), H.MemberToken.Def(declaring, destructor), lowerer.AncestorArguments(deleted.Type.Index, deleted.Arguments, declaring), [], H.HirType.Void);
                            Call(call, new Place(Temp(IrType.Unit)));
                        }
                        b.Free(new Place(handle));
                    }
                    else
                    {
                        Discard(s.Value);
                    }
                    break;
                case H.RegionStmt s when (lowerer.Options.Gc.Capabilities & GcCapabilities.Regions) == 0:
                    // No regions in this GC mode: the block runs as any other (its objects live as the collector decides).
                    Block(s.Body);
                    break;
                case H.RegionStmt s:
                {
                    // An arena region: what the block allocates goes (finalizers first) at region_exit, which every way out
                    // of the block passes (return, break, continue: RunFinally).
                    int region = regionCount++;
                    b.RegionEnter(region);
                    tries.Add(new TryFrame([], null, region));
                    Block(s.Body);
                    tries.RemoveAt(tries.Count - 1);
                    if (open)
                        b.RegionExit(region);
                    break;
                }
                case H.UnsafeStmt s:
                    b.Unsafe = true;
                    Block(s.Body);
                    break;
                case H.UsingStmt:
                    throw Unsupported("`using`");
                case H.YieldStmt s:
                    Yield(s);
                    break;
                case H.CheckedStmt s:
                    CheckedStatement(s);
                    break;
                default:
                    throw new LoweringException($"cannot lower {statement.GetType().Name}");
            }
        }

        private void Loop(int label, int breakTarget, int continueTarget, H.Stmt body, bool entered = false)
        {
            if (!entered)
                EnterIteration(body);
            loops.Add(new LoopFrame(label, breakTarget, continueTarget, tries.Count));
            Statement(body);
            loops.RemoveAt(loops.Count - 1);
        }

        private LoopFrame FindLoop(int label)
        {
            for (int i = loops.Count - 1; i >= 0; i--)
            {
                if (label < 0 || loops[i].Label == label)
                    return loops[i];
            }
            throw new LoweringException(label < 0 ? "`break` outside a loop or switch" : $"no loop labelled L{label}");
        }

        /// <summary>
        /// Runs the <c>finally</c> blocks of the try frames above <paramref name="depth"/>, innermost first, and closes
        /// the regions among them.
        /// </summary>
        private void RunFinally(int depth)
        {
            for (int t = tries.Count - 1; t >= depth && open; t--)
            {
                if (tries[t].Region >= 0)
                {
                    b.RegionExit(tries[t].Region);
                    continue;
                }
                if (tries[t].Finally is not { } block)
                    continue;
                // The finally block runs with the handlers outside its own try.
                var saved = tries.GetRange(t, tries.Count - t);
                tries.RemoveRange(t, tries.Count - t);
                Block(block);
                tries.AddRange(saved);
            }
        }

        private void Foreach(H.ForeachStmt s)
        {
            if (s.Collection is not H.RangeExpr { Low: { } low, High: { } high } range)
                throw Unsupported("`foreach` over anything but a range");
            // A captured variable is counted in a local and copied to the display object each iteration.
            int variable = locals[s.Local] >= 0 ? locals[s.Local] : Temp(Lower(hirLocals[s.Local].Type));
            var type = b.TypeOf(new Place(variable), Module);
            int end = Temp(type);
            Into(new Place(variable), low);
            Into(new Place(end), high);
            int head = NewBlock(), body = NewBlock(), step = NewBlock(), after = NewBlock();
            Goto(head);
            At(head);
            int test = Temp(IrType.Bool);
            b.Assign(test, Rvalue.Binary(range.Inclusive ? BinaryOp.Le : BinaryOp.Lt, Operand.Copy(variable), Operand.Copy(end)));
            b.If(Operand.Copy(test), body, after);
            open = false;
            At(body);
            EnterIteration(s.Body);
            if (locals[s.Local] < 0)
                b.Assign(LocalPlace(s.Local), Operand.Copy(variable));
            Loop(s.Label, after, step, s.Body, entered: true);
            Goto(step);
            At(step);
            b.Assign(variable, Rvalue.Binary(BinaryOp.Add, Operand.Copy(variable), One(type)));
            Goto(head);
            At(after);
        }

        // ── N-dimensional arrays ───────────────────────────

        private IrType GridItems(H.Expr grid) => lowerer.ElementsOf(Lower(((H.ArrayType)grid.Type).Element));

        /// <summary><c>new T[a, b, …]</c>: the elements (a·b·… of them, zeroed) and the lengths, in a new grid object.</summary>
        private void NewGrid(H.NewArrayExpr n, Place target, IrType type)
        {
            var structType = (StructType)(type is GcType g ? g.Pointee : ((IR.PointerType)type).Pointee);
            var element = Lower(n.Element);
            var lengths = new int[n.Lengths.Length];
            int total = Temp(IrType.U64), length = Temp(IrType.U64), negative = Temp(IrType.Bool), bad = Temp(IrType.Bool);
            b.Assign(bad, Operand.Bool(false));
            for (int d = 0; d < lengths.Length; d++)
            {
                lengths[d] = IndexLocal(n.Lengths[d]);
                b.Assign(negative, Rvalue.Binary(BinaryOp.Lt, Operand.Copy(lengths[d]), Operand.Const(0, IrType.I32)))
                 .Assign(bad, Rvalue.Binary(BinaryOp.Or, Operand.Copy(bad), Operand.Copy(negative)))
                 .Assign(length, Rvalue.Cast(Operand.Copy(lengths[d]), IrType.U64));
                b.Assign(total, d == 0 ? Rvalue.Use(Operand.Copy(length)) : Rvalue.Binary(BinaryOp.Mul, Operand.Copy(total), Operand.Copy(length)));
            }
            // A negative length: an impossible size, which the allocation refuses.
            int refuse = NewBlock(), make = NewBlock();
            b.If(Operand.Copy(bad), refuse, make);
            At(refuse);
            b.Assign(total, Operand.Const(ulong.MaxValue, IrType.U64));
            Goto(make);
            At(make);
            int items = Temp(lowerer.ElementsOf(element));
            b.Assign(items, new ArrayRvalue(lowerer.ObjectAlloc, element, Operand.Copy(total)));
            int value = Temp(structType);
            b.Assign(value, Rvalue.ZeroFiller(structType));
            b.Assign(new Place(value).Field(0), Rvalue.ClassId(structType));
            b.Assign(new Place(value).Field(2), Operand.Move(items));
            for (int d = 0; d < lengths.Length; d++)
                b.Assign(new Place(value).Field(3 + d), Operand.Copy(lengths[d]));
            b.Assign(target, new AllocRvalue(lowerer.ObjectAlloc, Operand.Move(value)));
        }

        /// <summary>
        /// <c>grid[i, j, …]</c>: element ((i·b + j)·c + …) of the elements. An index out of its dimension (other than the
        /// first, which the elements' own bounds check covers) makes the place one past any array, so the same check fails.
        /// One index: the element at that place in row order.
        /// </summary>
        private Place GridElement(H.ElementExpr x)
        {
            var grid = HandleLocal(x.Array);
            int items = Temp(GridItems(x.Array));
            b.Assign(items, Operand.Copy(grid.Deref().Field(2)));
            int flat = Temp(IrType.U64);
            b.Assign(flat, Rvalue.Cast(Value(x.Indices[0]), IrType.U64));
            if (x.Indices.Length > 1)
            {
                int index = Temp(IrType.U64), length = Temp(IrType.U64), outside = Temp(IrType.Bool), bad = Temp(IrType.Bool);
                b.Assign(bad, Operand.Bool(false));
                for (int d = 1; d < x.Indices.Length; d++)
                {
                    b.Assign(index, Rvalue.Cast(Value(x.Indices[d]), IrType.U64))
                     .Assign(length, Rvalue.Cast(Operand.Copy(grid.Deref().Field(3 + d)), IrType.U64))
                     .Assign(outside, Rvalue.Binary(BinaryOp.Ge, Operand.Copy(index), Operand.Copy(length)))
                     .Assign(bad, Rvalue.Binary(BinaryOp.Or, Operand.Copy(bad), Operand.Copy(outside)))
                     .Assign(flat, Rvalue.Binary(BinaryOp.Mul, Operand.Copy(flat), Operand.Copy(length)))
                     .Assign(flat, Rvalue.Binary(BinaryOp.Add, Operand.Copy(flat), Operand.Copy(index)));
                }
                int fix = NewBlock(), next = NewBlock();
                b.If(Operand.Copy(bad), fix, next);
                At(fix);
                b.Assign(flat, Operand.Const(ulong.MaxValue, IrType.U64));
                Goto(next);
                At(next);
            }
            b.Unsafe = true; // the elements are written through the grid's handle
            return new Place(items).Deref().Index(flat);
        }

        /// <summary><c>grid.Length</c> (all the elements) and <c>grid.GetLength(d)</c>.</summary>
        private void GridLength(H.ArrayLengthExpr length, Place target, IrType type)
        {
            var array = (H.ArrayType)length.Array.Type;
            var grid = HandleLocal(length.Array);
            if (length.Dimension is not { } dimension)
            {
                int items = Temp(GridItems(length.Array));
                int count = Temp(IrType.U64);
                b.Assign(items, Operand.Copy(grid.Deref().Field(2)))
                 .Assign(count, Rvalue.Len(new Place(items).Deref()))
                 .Assign(target, type == IrType.U64 ? Rvalue.Use(Operand.Copy(count)) : Rvalue.Cast(Operand.Copy(count), type));
                return;
            }
            if (array.Rank == 1)
            {
                // A plain array has dimension 0 only.
                int count = Temp(IrType.U64);
                b.Assign(count, Rvalue.Len(grid.Deref()));
                if (dimension is H.LiteralExpr { Value: H.IntegerConstant { Value: var only } } && only == 0)
                {
                    b.Assign(target, Rvalue.Cast(Operand.Copy(count), type));
                    return;
                }
            }
            if (dimension is H.LiteralExpr { Value: H.IntegerConstant { Value: var constant } } && constant >= 0 && constant < array.Rank && array.Rank > 1)
            {
                b.Assign(target, Operand.Copy(grid.Deref().Field(3 + (int)constant)));
                return;
            }
            // A dimension known only at run time: a test for each, and an index failure past the last.
            int d = IndexLocal(dimension);
            int test = Temp(IrType.Bool), after = NewBlock();
            for (int k = 0; k < array.Rank; k++)
            {
                int yes = NewBlock(), no = NewBlock();
                b.Assign(test, Rvalue.Binary(BinaryOp.Eq, Operand.Copy(d), Operand.Const(k, IrType.I32))).If(Operand.Copy(test), yes, no);
                At(yes);
                if (array.Rank == 1)
                {
                    int count = Temp(IrType.U64);
                    b.Assign(count, Rvalue.Len(grid.Deref())).Assign(target, Rvalue.Cast(Operand.Copy(count), type));
                }
                else
                {
                    b.Assign(target, Operand.Copy(grid.Deref().Field(3 + k)));
                }
                Goto(after);
                At(no);
            }
            // No such dimension: read past an empty array (the index check fails).
            int empty = Temp(lowerer.ElementsOf(IrType.I32)), past = Temp(IrType.U64);
            b.Assign(empty, new ArrayRvalue(lowerer.ObjectAlloc, IrType.I32, Operand.Const(0, IrType.U64)))
             .Assign(past, Operand.Const(0, IrType.U64));
            b.Unsafe = true;
            b.Assign(target, Operand.Copy(new Place(empty).Deref().Index(past)));
            Goto(after);
            At(after);
        }

        private void Switch(H.SwitchStmt s)
        {
            var type = Lower(s.Value.Type);
            int value = Temp(type);
            Into(new Place(value), s.Value);
            int after = NewBlock();
            loops.Add(new LoopFrame(s.Label, after, loops.Count > 0 ? loops[^1].Continue : -1, tries.Count));
            foreach (var section in s.Sections)
            {
                int body = NewBlock(), next = NewBlock();
                foreach (var pattern in section.Patterns)
                {
                    int failed = NewBlock();
                    int matched = section.Guard is null ? body : NewBlock();
                    Match(new Place(value), s.Value.Type, pattern, matched, failed);
                    if (section.Guard is { } guard)
                    {
                        At(matched);
                        Condition(guard, body, failed);
                    }
                    At(failed);
                }
                Goto(next);
                At(body);
                Statement(section.Body);
                Goto(after);
                At(next);
            }
            Goto(after);
            loops.RemoveAt(loops.Count - 1);
            At(after);
        }

        // ── Exceptions ─────────────────────────────────────

        private void Throw(H.Expr? value)
        {
            if (value is null)
            {
                b.Resume(Handlers());
                open = false;
                return;
            }
            int thrown = Temp(lowerer.RootHandle);
            var operand = Value(value);
            b.Assign(thrown, ToRoot(operand, Lower(value.Type)));
            b.Throw(Operand.Copy(thrown), Handlers());
            open = false;
        }

        /// <summary>A handle (or a nullable one, checked) as the root object handle.</summary>
        private Rvalue ToRoot(Operand operand, IrType type)
        {
            if (type is OptionalType)
            {
                int unwrapped = Temp(((OptionalType)type).Inner);
                b.Assign(unwrapped, Rvalue.Unwrap(operand));
                return Rvalue.HandleCast(Operand.Copy(unwrapped), lowerer.RootHandle);
            }
            return type == lowerer.RootHandle ? Rvalue.Use(operand) : Rvalue.HandleCast(operand, lowerer.RootHandle);
        }

        /// <summary>
        /// <c>try</c>: one handler takes every exception (all are objects, thrown as the root) and tests the clauses in
        /// order; none matching, it goes on outward. <c>finally</c> is a catch-all that runs the block and re-raises, plus a
        /// copy of the block on every way out of the try.
        /// </summary>
        private void Try(H.TryStmt s)
        {
            var root = lowerer.RootHandle;
            int after = NewBlock();
            int finallyHandler = s.Finally is null ? -1 : NewBlock();
            int normalExit = s.Finally is null ? after : NewBlock();
            int handler = s.Catches.Length > 0 ? NewBlock() : -1;
            CatchClause[] finallyClause = finallyHandler < 0 ? [] : [new CatchClause(null, finallyHandler)];
            CatchClause[] clauses = [.. handler < 0 ? [] : new[] { new CatchClause(root, handler) }, .. finallyClause];

            tries.Add(new TryFrame(clauses, s.Finally));
            Block(s.Body);
            tries.RemoveAt(tries.Count - 1);
            Goto(normalExit);

            // Catch bodies are still protected by the finally (its catch-all handler).
            tries.Add(new TryFrame(finallyClause, s.Finally));
            if (handler >= 0 && !usedHandlers.Contains(handler))
            {
                // Nothing in the body can throw: the catches never run.
                At(handler);
                b.Unreachable();
                open = false;
            }
            else if (handler >= 0)
            {
                At(handler);
                int caught = Temp(root);
                b.Assign(caught, Rvalue.Caught);
                foreach (var clause in s.Catches)
                {
                    int body = NewBlock(), next = NewBlock();
                    if (clause.Type is { } type)
                        TypeTest(Operand.Copy(caught), root, Lower(type), body, next);
                    else
                        Goto(body);
                    At(body);
                    if (clause.Local >= 0)
                        b.Assign(LocalPlace(clause.Local), Convert(Operand.Copy(caught), root, b.TypeOf(LocalPlace(clause.Local), Module)));
                    if (clause.Filter is { } filter)
                    {
                        int pass = NewBlock();
                        Condition(filter, pass, next);
                        At(pass);
                    }
                    Block(clause.Body);
                    Goto(normalExit);
                    At(next);
                }
                b.Resume(Handlers());
                open = false;
            }
            tries.RemoveAt(tries.Count - 1);

            if (s.Finally is { } finallyBlock)
            {
                At(normalExit);
                Block(finallyBlock);
                Goto(after);
                At(finallyHandler);
                if (!usedHandlers.Contains(finallyHandler))
                {
                    b.Unreachable();
                }
                else
                {
                    Block(finallyBlock);
                    if (open)
                        b.Resume(Handlers());
                }
                open = false;
            }
            At(after);
        }

        // ── Patterns and type tests ────────────────────────

        /// <summary>Jumps to <paramref name="yes"/> when the object behind <paramref name="handle"/> is a <paramref name="target"/>.</summary>
        private void TypeTest(Operand handle, IrType handleType, IrType target, int yes, int no)
        {
            var targetStruct = target switch
            {
                GcType { Pointee: StructType s } => s,
                IR.PointerType { Pointee: StructType s } => s,
                OptionalType { Inner: GcType { Pointee: StructType s } } => s,
                OptionalType { Inner: IR.PointerType { Pointee: StructType s } } => s,
                _ => throw Unsupported($"type tests for {target}"),
            };
            int obj = Temp(handleType is OptionalType o ? o.Inner : handleType);
            if (handleType is OptionalType)
            {
                int isNull = Temp(IrType.Bool);
                int present = NewBlock();
                int value = Temp(handleType);
                b.Assign(value, handle).Assign(isNull, Rvalue.IsNull(value)).If(Operand.Copy(isNull), no, present);
                At(present);
                b.Assign(obj, Rvalue.Unwrap(Operand.Copy(value)));
            }
            else
            {
                b.Assign(obj, handle);
            }
            int root = Temp(lowerer.RootHandle);
            b.Assign(root, ToRoot(Operand.Copy(obj), b.TypeOf(new Place(obj), Module)));
            int id = Temp(IrType.I32), low = Temp(IrType.I32), high = Temp(IrType.I32), test = Temp(IrType.Bool);
            int second = NewBlock();
            b.Assign(id, Operand.Copy(new Place(root).Deref().Field(0)))
             .Assign(low, Rvalue.ClassId(targetStruct))
             .Assign(test, Rvalue.Binary(BinaryOp.Ge, Operand.Copy(id), Operand.Copy(low)))
             .If(Operand.Copy(test), second, no);
            At(second);
            int test2 = Temp(IrType.Bool);
            b.Assign(high, Rvalue.ClassId(targetStruct, end: true))
             .Assign(test2, Rvalue.Binary(BinaryOp.Lt, Operand.Copy(id), Operand.Copy(high)))
             .If(Operand.Copy(test2), yes, no);
            open = false;
        }

        /// <summary>Jumps to <paramref name="yes"/> when the value at <paramref name="value"/> matches.</summary>
        private void Match(Place value, H.HirType hirType, H.Pattern pattern, int yes, int no)
        {
            var type = Lower(hirType);
            switch (pattern)
            {
                case H.DiscardPattern:
                    Goto(yes);
                    break;
                case H.ConstantPattern { Value: H.NullConstant }:
                {
                    int test = Temp(IrType.Bool);
                    b.Assign(test, NullTest(value, type)).If(Operand.Copy(test), yes, no);
                    open = false;
                    break;
                }
                case H.ConstantPattern { Value: H.StringConstant text }:
                    MatchString(value, hirType, text, yes, no);
                    break;
                case H.ConstantPattern c:
                    Compare(BinaryOp.Eq, value, type, c.Value, yes, no);
                    break;
                case H.RelationalPattern r:
                    Compare(Op(r.Op), value, type, r.Value, yes, no);
                    break;
                case H.RangePattern r:
                {
                    int mid = NewBlock();
                    Compare(BinaryOp.Ge, value, type, r.Low, mid, no);
                    At(mid);
                    Compare(r.Inclusive ? BinaryOp.Le : BinaryOp.Lt, value, type, r.High, yes, no);
                    break;
                }
                case H.TypePattern t:
                    TestType(Operand.Copy(value), type, t.Type, yes, no);
                    break;
                case H.BindingPattern bind:
                {
                    var target = b.TypeOf(LocalPlace(bind.Local), Module);
                    if (bind.Inner is null)
                    {
                        b.Assign(LocalPlace(bind.Local), Convert(Operand.Copy(value), type, target));
                        Goto(yes);
                        break;
                    }
                    int bound = NewBlock();
                    Match(value, hirType, bind.Inner, bound, no);
                    At(bound);
                    if (IsBoxable(hirLocals[bind.Local].Type) && IsHandle(type))
                        UnboxInto(Operand.Copy(value), type, hirLocals[bind.Local].Type, LocalPlace(bind.Local));
                    else
                        b.Assign(LocalPlace(bind.Local), Convert(Operand.Copy(value), type, target));
                    Goto(yes);
                    break;
                }
                case H.AndPattern a:
                {
                    int mid = NewBlock();
                    Match(value, hirType, a.Left, mid, no);
                    At(mid);
                    Match(value, hirType, a.Right, yes, no);
                    break;
                }
                case H.OrPattern o:
                {
                    int mid = NewBlock();
                    Match(value, hirType, o.Left, yes, mid);
                    At(mid);
                    Match(value, hirType, o.Right, yes, no);
                    break;
                }
                case H.NotPattern n:
                    Match(value, hirType, n.Operand, no, yes);
                    break;
                case H.PositionalPattern pos:
                    Positional(value, hirType, pos, yes, no);
                    break;
                case H.PropertyPattern pp:
                    Property(value, hirType, pp, yes, no);
                    break;
                default:
                    throw Unsupported($"{pattern.GetType().Name.Replace("Pattern", "").ToLowerInvariant()} patterns");
            }
        }

        /// <summary>The value as the type a pattern names (tested first, failing to <paramref name="no"/>), or as it is.</summary>
        private (Place Subject, H.HirType Type) Narrow(Place value, H.HirType hirType, H.HirType? named, int no)
        {
            if (named is null)
                return (value, hirType);
            var type = Lower(hirType);
            int ok = NewBlock();
            TestType(Operand.Copy(value), type, named, ok, no);
            At(ok);
            var target = Lower(named);
            int cast = Temp(target);
            if (IsBoxable(named) && IsHandle(type))
                UnboxInto(Operand.Copy(value), type, named, new Place(cast));
            else
                b.Assign(cast, Convert(Operand.Copy(value), type, target));
            return (new Place(cast), named);
        }

        /// <summary>Matches each of <paramref name="items"/> against its place in turn.</summary>
        private void MatchAll(List<(Place Place, H.HirType Type)> places, H.Pattern[] items, int yes, int no)
        {
            if (places.Count != items.Length)
                throw new LoweringException($"a pattern with {items.Length} items over {places.Count} positions");
            if (items.Length == 0)
            {
                Goto(yes);
                return;
            }
            for (int i = 0; i < items.Length; i++)
            {
                int next = i + 1 < items.Length ? NewBlock() : yes;
                Match(places[i].Place, places[i].Type, items[i], next, no);
                if (i + 1 < items.Length)
                    At(next);
            }
        }

        /// <summary><c>T(p, q)</c>: through <c>Deconstruct(&amp;mut …)</c> into temporaries, or the fields in order.</summary>
        private void Positional(Place value, H.HirType hirType, H.PositionalPattern pos, int yes, int no)
        {
            var (subject, subjectType) = Narrow(value, hirType, pos.Type, no);
            if (subjectType is not H.NamedType named)
                throw Unsupported("positional patterns on this type");
            var info = lowerer.Types[named.Type.Index];
            var places = new List<(Place, H.HirType)>();
            if (pos.Deconstructor is { } method)
            {
                var definition = lowerer.Assembly.Types[method.Type].Methods[method.Member];
                var arguments = new List<H.Expr>();
                foreach (var parameter in definition.Parameters)
                {
                    var itemType = ((H.ReferenceType)parameter.Type).Target.Substitute(named.Arguments);
                    var slotType = Lower(itemType);
                    int slot = Temp(slotType);
                    b.Assign(slot, Zeroable(slotType) ? Rvalue.Zero(slotType) : Rvalue.ZeroFiller(slotType)); // Deconstruct fills it
                    arguments.Add(new H.BorrowExpr(new HeldExpr(slot, itemType), true, new H.ReferenceType(itemType, true)));
                    places.Add((new Place(slot), itemType));
                }
                int self = Temp(Lower(subjectType));
                b.Assign(self, Operand.Copy(subject));
                Call(new H.CallExpr(new HeldExpr(self, subjectType), method, [], [.. arguments], H.HirType.Void), new Place(Temp(IrType.Unit)));
            }
            else
            {
                var fields = info.Definition.Fields;
                for (int f = 0; f < fields.Count; f++)
                {
                    if ((fields[f].Flags & (H.FieldFlags.Static | H.FieldFlags.Const)) != 0)
                        continue;
                    var place = info.IsClass ? subject.Deref().Field(info.FieldIndex[f]) : FieldPlace(info, subject, f);
                    places.Add((place, fields[f].Type.Substitute(named.Arguments)));
                }
            }
            MatchAll(places, pos.Items, yes, no);
        }

        /// <summary><c>T { A: p }</c>: each named field's place, or a property's getter into a temporary.</summary>
        private void Property(Place value, H.HirType hirType, H.PropertyPattern pp, int yes, int no)
        {
            var (subject, subjectType) = Narrow(value, hirType, pp.Type, no);
            if (subjectType is not H.NamedType named)
                throw Unsupported("property patterns on this type");
            var places = new List<(Place, H.HirType)>();
            int self = -1;
            foreach (var member in pp.Members)
            {
                if (member.Kind == H.MemberKind.Field)
                {
                    var (declaring, index) = Field(member.Member);
                    var place = declaring.IsClass ? subject.Deref().Field(declaring.FieldIndex[index]) : FieldPlace(declaring, subject, index);
                    places.Add((place, declaring.Definition.Fields[index].Type.Substitute(named.Arguments)));
                    continue;
                }
                var property = lowerer.Assembly.Types[member.Member.Type].Properties[member.Member.Member];
                if (property.Getter < 0)
                    throw new LoweringException($"{property.Name} has no getter");
                if (self < 0)
                {
                    self = Temp(Lower(subjectType));
                    b.Assign(self, Operand.Copy(subject));
                }
                var propertyType = property.Type.Substitute(named.Arguments);
                int slot = Temp(Lower(propertyType));
                var typeArguments = lowerer.AncestorArguments(named.Type.Index, named.Arguments, member.Member.Type);
                Call(new H.CallExpr(new HeldExpr(self, subjectType), H.MemberToken.Def(member.Member.Type, property.Getter), typeArguments, [], propertyType), new Place(slot));
                places.Add((new Place(slot), propertyType));
            }
            MatchAll(places, [.. pp.Members.Select(m => m.Pattern)], yes, no);
        }

        /// <summary>A string constant pattern: null never matches; the rest is <c>String.Equals</c>.</summary>
        private void MatchString(Place value, H.HirType hirType, H.StringConstant text, int yes, int no)
        {
            var type = Lower(hirType);
            var stringType = hirType is H.NullableType n ? n.Inner : hirType;
            if (hirType is H.NullableType)
            {
                int present = NewBlock();
                int isNull = Temp(IrType.Bool);
                b.Assign(isNull, NullTest(value, type)).If(Operand.Copy(isNull), no, present);
                At(present);
            }
            var info = lowerer.StringType()?.Type ?? throw new LoweringException("string patterns need System.String");
            int equals = info.Definition.Methods.FindIndex(m => m.Name == "Equals" && m.Parameters.Count == 1 && m.Kind == H.MethodKind.Normal && (m.Flags & H.MethodFlags.Static) == 0);
            if (equals < 0)
                throw new LoweringException("string patterns need String.Equals(str)");
            int self = Temp(Lower(stringType));
            b.Assign(self, Convert(Operand.Copy(value), type, Lower(stringType)));
            int same = Temp(IrType.Bool);
            Call(new H.CallExpr(new HeldExpr(self, stringType), H.MemberToken.Def(info.Index, equals), stringType is H.NamedType sn ? sn.Arguments : [], [new H.LiteralExpr(text, stringType)], H.HirType.Bool), new Place(same));
            b.If(Operand.Copy(same), yes, no);
            open = false;
        }

        private void Compare(BinaryOp op, Place value, IrType type, H.HirConstant constant, int yes, int no)
        {
            int test = Temp(IrType.Bool);
            b.Assign(test, Rvalue.Binary(op, Operand.Copy(value), Constant(constant, type)))
             .If(Operand.Copy(test), yes, no);
            open = false;
        }

        // ── Conditions ─────────────────────────────────────

        /// <summary>Jumps on a boolean expression; <c>&amp;&amp;</c>, <c>||</c> and <c>!</c> become branches.</summary>
        private void Condition(H.Expr condition, int yes, int no)
        {
            switch (condition)
            {
                case H.BinaryExpr { Op: H.BinaryOp.And } and:
                {
                    int mid = NewBlock();
                    Condition(and.Left, mid, no);
                    At(mid);
                    Condition(and.Right, yes, no);
                    return;
                }
                case H.BinaryExpr { Op: H.BinaryOp.Or } or:
                {
                    int mid = NewBlock();
                    Condition(or.Left, yes, mid);
                    At(mid);
                    Condition(or.Right, yes, no);
                    return;
                }
                case H.UnaryExpr { Op: H.UnaryOp.Not } not:
                    Condition(not.Operand, no, yes);
                    return;
                case H.IsExpr test:
                {
                    var place = PlaceOrTemp(test.Value);
                    Match(place, test.Value.Type, test.Pattern, yes, no);
                    return;
                }
            }
            var value = Value(condition);
            if (open)
                b.If(value, yes, no);
            open = false;
        }

        // ── Expressions ────────────────────────────────────

        private IrModule Module => moduleView ??= new IrModule([.. lowerer.Structs], []);
        private IrModule? moduleView;

        /// <summary>Evaluates for its effects only.</summary>
        private void Discard(H.Expr e)
        {
            switch (e)
            {
                case H.AssignExpr a:
                    Assign(a);
                    return;
                case H.UnaryExpr { Op: H.UnaryOp.PreIncrement or H.UnaryOp.PostIncrement or H.UnaryOp.PreDecrement or H.UnaryOp.PostDecrement } u:
                    Step(u);
                    return;
                case H.LiteralExpr or H.LocalExpr or H.ParamExpr or H.ThisExpr or H.DefaultExpr:
                    return;
            }
            var type = Lower(e.Type);
            int sink = Temp(type);
            Into(new Place(sink), e);
        }

        /// <summary>The value of an expression as an operand (a constant, a place read, or a temporary).</summary>
        private Operand Value(H.Expr e)
        {
            if (e is H.LiteralExpr literal && literal.Value is not (H.NullConstant or H.StringConstant))
                return Constant(literal.Value, Lower(e.Type));
            if (AsPlace(e) is { } place)
                return Copied(place, e.Type);
            var type = Lower(e.Type);
            int temp = Temp(type);
            Into(new Place(temp), e);
            // A value made for the call moves in; a reference (a borrow for a `&mut` parameter) is not Copy.
            return FieldCopied(type) || type is ReferenceType ? Operand.Move(temp) : Operand.Copy(temp);
        }

        /// <summary>
        /// The value at <paramref name="place"/>, copied. A value type holding objects is not copied bit for bit in IR (a
        /// reference count would not see the new owner): it is made again field by field.
        /// </summary>
        private Operand Copied(Place place, H.HirType hirType)
        {
            if (hirType is not H.NamedType { Type.IsDefinition: true } named || !lowerer.Types[named.Type.Index].IsStruct)
                return Operand.Copy(place);
            var type = Lower(hirType);
            if (!FieldCopied(type))
                return Operand.Copy(place);
            int temp = Temp(type);
            CopyFields(new Place(temp), place, (StructType)type);
            return Operand.Move(temp);
        }

        private void CopyFields(Place target, Place source, StructType type)
        {
            b.Assign(target, Rvalue.ZeroFiller(type));
            var fields = lowerer.Structs[type.Id].Fields;
            for (int f = 0; f < fields.Length; f++)
            {
                var fieldType = fields[f].Substitute(type.Arguments);
                if (FieldCopied(fieldType))
                    CopyFields(target.Field(f), source.Field(f), (StructType)fieldType);
                else
                    b.Assign(target.Field(f), Operand.Copy(source.Field(f)));
            }
        }

        /// <summary>Whether values of <paramref name="type"/> are copied field by field (value types holding objects).</summary>
        private bool FieldCopied(IrType type)
        {
            if (type is not StructType st || lowerer.Structs[st.Id].Mutable)
                return false;
            if (copied.TryGetValue(type, out bool known))
                return known;
            bool result = !Constraints.Satisfies(type, TypeConstraint.Copy, new TypeContext(Module, plan.Generics));
            copied[type] = result;
            return result;
        }

        private readonly Dictionary<IrType, bool> copied = [];

        private Place PlaceOrTemp(H.Expr e)
        {
            if (AsPlace(e) is { } place)
                return place;
            int temp = Temp(Lower(e.Type));
            Into(new Place(temp), e);
            return new Place(temp);
        }

        /// <summary>The place an expression names, when it names one (locals, parameters, fields, elements, *p).</summary>
        private Place? AsPlace(H.Expr e)
        {
            switch (e)
            {
                case H.LocalExpr l:
                    return LocalPlace(l.Local);
                case H.ParamExpr p:
                    return ParamPlace(p.Parameter);
                case H.ReceiverExpr:
                    return new Place(receivers[^1]);
                case H.ThisExpr when IsCoroutine:
                    if (iteratorThis < 0)
                        throw new LoweringException("`this` in an iterator of a static function");
                    return IteratorField(iteratorThis);
                case H.ThisExpr when lambda >= 0:
                    return ThisInLambda();
                case H.ThisExpr:
                    if (self < 0)
                        throw new LoweringException("`this` in a static method");
                    return owner.IsStruct ? new Place(self).Deref() : new Place(self);
                case H.FieldExpr f when f.Instance is { } instance && Field(f.Field) is var (laid, at) && laid.FieldOffset is { } offsets && offsets[at] >= 0:
                    return FieldPlace(laid, PlaceOrTemp(instance), at);
                case H.FieldExpr f when f.Instance is { } instance && Field(f.Field) is var (info, index) && info.FieldIndex[index] >= 0:
                {
                    int field = info.FieldIndex[index];
                    if (info.IsClass)
                    {
                        var handle = HandleLocal(instance);
                        return handle.Deref().Field(field);
                    }
                    return PlaceOrTemp(instance).Field(field);
                }
                case H.ElementExpr x when x.Array.Type is H.ArrayType { Rank: > 1 }:
                    return GridElement(x);
                case H.ElementExpr x when x.Indices.Length == 1 && IsInPlace(x.Array):
                {
                    // An array kept in its object: the element is a projection of the object's last field.
                    var buffer = AsPlace(x.Array)!;
                    int index = IndexLocal(x.Indices[0]);
                    return buffer.Index(index);
                }
                case H.ElementExpr x when x.Array.Type is H.InlineArrayType && x.Indices.Length == 1:
                {
                    // An inline array lives in its holder (a field, a local): the element is a projection of that place.
                    var buffer = AsPlace(x.Array);
                    if (buffer is null)
                    {
                        int temp = Temp(Lower(x.Array.Type));
                        Into(new Place(temp), x.Array);
                        buffer = new Place(temp);
                    }
                    int index = IndexLocal(x.Indices[0]);
                    return buffer.Index(index);
                }
                case H.ElementExpr x when x.Indices.Length == 1:
                {
                    var array = HandleLocal(x.Array);
                    int index = IndexLocal(x.Indices[0]);
                    return array.Deref().Index(index);
                }
                case H.DerefExpr d:
                    if (d.Pointer.Type is H.PointerType)
                        b.Unsafe = true;
                    return HandleLocal(d.Pointer).Deref();
                case PendingExpr:
                    return new Place(pending);
                case HeldExpr held:
                    return new Place(held.Local);
            }
            return null;
        }

        /// <summary>A handle (object, array, pointer, reference) in a local, to project through.</summary>
        private Place HandleLocal(H.Expr e)
        {
            if (AsPlace(e) is { IsLocal: true } local)
                return local;
            int temp = Temp(Lower(e.Type));
            Into(new Place(temp), e);
            return new Place(temp);
        }

        private int IndexLocal(H.Expr e)
        {
            if (AsPlace(e) is { IsLocal: true } local)
                return local.Local;
            int temp = Temp(Lower(e.Type));
            Into(new Place(temp), e);
            return temp;
        }

        /// <summary>Whether <paramref name="e"/> is an object's array kept in place (<see cref="H.FieldFlags.InPlace"/>).</summary>
        private bool IsInPlace(H.Expr e) =>
            e is H.FieldExpr { Instance: not null } f && Field(f.Field) is var (info, index) && (info.Definition.Fields[index].Flags & H.FieldFlags.InPlace) != 0;

        private (HirLowerer.TypeInfo Info, int Index) Field(H.MemberToken token)
        {
            if (!token.IsDefinition)
                throw Unsupported("fields of other assemblies");
            var owner = lowerer.Types[token.Type];
            if (lowerer.ExternalOrigin(owner) is { } externalOrigin)
                throw new LoweringException(HirLowerer.CrossingError($"the field `{owner.Name}.{owner.Definition.Fields[token.Member].Name}`", externalOrigin,
                    "its memory belongs to that domain and cannot be read or written from this one"));
            return (owner, token.Member);
        }

        private Operand Constant(H.HirConstant constant, IrType type) => constant switch
        {
            H.IntegerConstant i when type.IsFloat => throw new LoweringException($"integer constant for {type}"),
            H.IntegerConstant i => Operand.Const(i.Value, type),
            H.FloatConstant f => Operand.FloatBits(f.Bits, type),
            H.BoolConstant x => Operand.Bool(x.Value),
            H.CharConstant c => Operand.Const(c.Value, type),
            H.EnumConstant e => Operand.Const(e.Value, type),
            _ => throw Unsupported($"constants like {constant}"),
        };

        private static Operand One(IrType type) => type is IR.PointerType ? Operand.Const(1, IrType.I64) : type.IsFloat
            ? type == IrType.F32 ? Operand.Float32(1) : Operand.Float(1)
            : Operand.Const(1, type);

        private static BinaryOp Op(H.BinaryOp op) => op switch
        {
            H.BinaryOp.Add => BinaryOp.Add,
            H.BinaryOp.Subtract => BinaryOp.Sub,
            H.BinaryOp.Multiply => BinaryOp.Mul,
            H.BinaryOp.Divide => BinaryOp.Div,
            H.BinaryOp.Remainder => BinaryOp.Rem,
            H.BinaryOp.BitAnd => BinaryOp.And,
            H.BinaryOp.BitOr => BinaryOp.Or,
            H.BinaryOp.BitXor => BinaryOp.Xor,
            H.BinaryOp.ShiftLeft => BinaryOp.Shl,
            H.BinaryOp.ShiftRight => BinaryOp.Shr,
            H.BinaryOp.Equal => BinaryOp.Eq,
            H.BinaryOp.NotEqual => BinaryOp.Ne,
            H.BinaryOp.Less => BinaryOp.Lt,
            H.BinaryOp.LessEqual => BinaryOp.Le,
            H.BinaryOp.Greater => BinaryOp.Gt,
            H.BinaryOp.GreaterEqual => BinaryOp.Ge,
            _ => throw new LoweringException($"the operator {op} is lowered in a later phase"),
        };

        /// <summary>Evaluates <paramref name="e"/> into <paramref name="target"/>.</summary>
        private void Into(Place target, H.Expr e)
        {
            var type = Lower(e.Type);
            switch (e)
            {
                case H.LiteralExpr { Value: H.NullConstant }:
                    b.Assign(target, lowerer.IsNullableValue(type) ? Rvalue.ZeroFiller(type) : Rvalue.Zero(type));
                    return;
                case H.LiteralExpr { Value: H.StringConstant text }:
                    StringLiteral(text.Value, target, e.Type);
                    return;
                case H.ArrayLengthExpr length when length.Dimension is not null || length.Array.Type is H.ArrayType { Rank: > 1 }:
                    GridLength(length, target, type);
                    return;
                case H.ArrayLengthExpr length when IsInPlace(length.Array):
                {
                    int count = Temp(IrType.U64);
                    b.Assign(count, Rvalue.Len(AsPlace(length.Array)!));
                    b.Assign(target, type == IrType.U64 ? Rvalue.Use(Operand.Copy(count)) : Rvalue.Cast(Operand.Copy(count), type));
                    return;
                }
                case H.FieldExpr inPlace when IsInPlace(inPlace):
                    throw new LoweringException("an array kept in its object is reached by index and Length, not as an array");
                case H.ArrayLengthExpr length:
                {
                    var array = HandleLocal(length.Array);
                    int count = Temp(IrType.U64);
                    b.Assign(count, Rvalue.Len(array.Deref()));
                    b.Assign(target, type == IrType.U64 ? Rvalue.Use(Operand.Copy(count)) : Rvalue.Cast(Operand.Copy(count), type));
                    return;
                }
                case H.LiteralExpr literal:
                    b.Assign(target, Constant(literal.Value, type));
                    return;
                case H.LocalExpr or H.ParamExpr or H.ThisExpr or H.ReceiverExpr or H.FieldExpr { Instance: not null } or H.ElementExpr or H.DerefExpr or PendingExpr when AsPlace(e) is { } place:
                    b.Assign(target, Copied(place, e.Type));
                    return;
                case H.FieldExpr f:
                {
                    var (info, index) = Field(f.Field);
                    var field = info.Definition.Fields[index];
                    if (field.Value is { } constant && (field.Flags & H.FieldFlags.Const) != 0)
                        b.Assign(target, Constant(constant, type));
                    else if (info.FieldGlobal[index] >= 0)
                        b.Assign(target, Rvalue.ReadGlobal(info.FieldGlobal[index], StaticArguments(info, f.TypeArguments)));
                    else
                        throw new LoweringException($"the instance field {field.Name} needs an object");
                    return;
                }
                case H.CallExpr call:
                    Call(call, target);
                    return;
                case H.LambdaExpr l:
                    Closure(l, target, type);
                    return;
                case H.InvokeExpr i when i.Function.Type is H.NativeFuncType:
                    NativeInvoke(i, target);
                    return;
                case H.InvokeExpr i:
                    Invoke(i, target);
                    return;
                case H.MethodRefExpr { Type: H.NativeFuncType } reference:
                    NativeFunctionAddress(reference, target);
                    return;
                case H.TypeOfExpr typeOf:
                    TypeOf(typeOf, target);
                    return;
                case H.PropertyExpr p:
                    Accessor(p, getter: true, null, target);
                    return;
                case H.NewExpr n:
                    New(n, target, type);
                    return;
                case H.WithExpr w:
                    With(w, target, type);
                    return;
                case H.NewArrayExpr n when n.Lengths.Length > 1:
                    NewGrid(n, target, type);
                    return;
                case H.NewArrayExpr n:
                {
                    var element = Lower(n.Element);
                    b.Assign(target, new ArrayRvalue(lowerer.ObjectAlloc, element, Value(n.Lengths[0])));
                    return;
                }
                case H.ArrayLiteralExpr a when a.Items.Length >= 16 && a.Element is H.PrimitiveType { Kind: H.Primitive.U8 or H.Primitive.U16 or H.Primitive.U32 } unitType
                    && a.Items.All(i => i is H.LiteralExpr { Value: H.IntegerConstant }):
                {
                    // A constant array (a resource's bytes): static data copied into a new array, not one store per element.
                    var element = Lower(a.Element);
                    int data = Temp(IrType.Ref(new IR.ArrayType(element)));
                    b.Assign(data, new StaticRvalue(lowerer.ConstantData(unitType.Kind, a.Items.Select(i => ((H.IntegerConstant)((H.LiteralExpr)i).Value).Value))));
                    Emit(lowerer.StringUnits(unitType.Kind), [Operand.Copy(data)], target);
                    return;
                }
                case H.ArrayLiteralExpr a:
                {
                    var element = Lower(a.Element);
                    int array = Temp(type);
                    b.Assign(array, new ArrayRvalue(lowerer.ObjectAlloc, element, Operand.Const(a.Items.Length, IrType.U64)) { Filled = true });
                    int index = Temp(IrType.U64);
                    b.Unsafe = true; // the new array's elements are written through its handle
                    for (int i = 0; i < a.Items.Length; i++)
                    {
                        b.Assign(index, Operand.Const(i, IrType.U64));
                        b.Assign(new Place(array).Deref().Index(index), Value(a.Items[i]));
                    }
                    b.Assign(target, Operand.Copy(array));
                    return;
                }
                case H.UnaryExpr u:
                    Unary(u, target, type);
                    return;
                case H.BinaryExpr { Op: H.BinaryOp.And or H.BinaryOp.Or }:
                case H.IsExpr:
                    Boolean(e, target);
                    return;
                case H.BinaryExpr bin:
                    Binary(bin, target);
                    return;
                case H.AssignExpr a:
                {
                    var place = Assign(a);
                    b.Assign(target, Operand.Copy(place));
                    return;
                }
                case H.ConvertExpr { Kind: H.ConversionKind.User, Method: null } c when c.Value.Type is H.GenericParamType || c.Type is H.GenericParamType:
                {
                    // `(i64)x` with `T : (i64)T`: the requirement, met per type argument.
                    if (!RequiredOperator(Requirement.Conversion, [c.Value], c.Type, target))
                        throw new LoweringException($"no conversion requirement from {c.Value.Type} to {c.Type}");
                    return;
                }
                case H.ConvertExpr c:
                {
                    var from = Lower(c.Value.Type);
                    var value = Value(c.Value);
                    b.Assign(target, Conversion(c, value, from, type));
                    return;
                }
                case H.AsExpr a:
                {
                    var from = Lower(a.Value.Type);
                    int value = Temp(from);
                    Into(new Place(value), a.Value);
                    int yes = NewBlock(), no = NewBlock(), after = NewBlock();
                    TestType(Operand.Copy(value), from, a.Type is H.NullableType { Inner: var asInner } ? asInner : a.Type, yes, no);
                    At(yes);
                    b.Unsafe = true;
                    b.Assign(target, Convert(Operand.Copy(value), from, type));
                    Goto(after);
                    At(no);
                    b.Assign(target, Rvalue.Zero(type));
                    Goto(after);
                    At(after);
                    return;
                }
                case H.ConditionalExpr c:
                {
                    int yes = NewBlock(), no = NewBlock(), after = NewBlock();
                    Condition(c.Condition, yes, no);
                    At(yes);
                    Into(target, c.Then);
                    Goto(after);
                    At(no);
                    Into(target, c.Else);
                    Goto(after);
                    At(after);
                    return;
                }
                case H.AwaitExpr a:
                    Await(a, target);
                    return;
                case H.ResourceExpr r:
                {
                    // A resource's bytes: static data copied into a new u8[] (the last resource of that name, as Install does).
                    var resource = lowerer.Assembly.Resources.LastOrDefault(x => x.Name == r.Name)
                        ?? throw new LoweringException($"the program has no resource \"{r.Name}\"");
                    int data = Temp(IrType.Ref(new IR.ArrayType(IrType.U8)));
                    b.Assign(data, new StaticRvalue(lowerer.ConstantData(H.Primitive.U8, resource.Data.Select(x => (System.Numerics.BigInteger)x))));
                    Emit(lowerer.StringUnits(H.Primitive.U8), [Operand.Copy(data)], target);
                    return;
                }
                case H.CheckedExpr c:
                    CheckedInto(c, target);
                    return;
                case H.ConditionalAccessExpr ca:
                {
                    // `a?.x`: x of a's value when a is not null (the receiver in a local while x runs), else null.
                    var receiverType = Lower(ca.Receiver.Type);
                    int receiver = Temp(receiverType);
                    Into(new Place(receiver), ca.Receiver);
                    int isNull = Temp(IrType.Bool);
                    int present = NewBlock(), missing = NewBlock(), after = NewBlock();
                    b.Assign(isNull, NullTest(new Place(receiver), receiverType)).If(Operand.Copy(isNull), missing, present);
                    At(present);
                    var innerType = ((H.NullableType)ca.Receiver.Type).Inner;
                    int inner = Temp(Lower(innerType));
                    if (lowerer.IsNullableValue(receiverType))
                        b.Assign(inner, Copied(new Place(receiver).Field(1), innerType));
                    else
                        b.Assign(inner, Rvalue.Unwrap(Operand.Copy(receiver)));
                    receivers.Add(inner);
                    Into(target, ca.Access);
                    receivers.RemoveAt(receivers.Count - 1);
                    Goto(after);
                    At(missing);
                    if (ca.Type != H.HirType.Void)
                        b.Assign(target, lowerer.IsNullableValue(type) ? Rvalue.ZeroFiller(type) : Rvalue.Zero(type));
                    Goto(after);
                    At(after);
                    return;
                }
                case H.CoalesceExpr c:
                {
                    var leftType = Lower(c.Left.Type);
                    int left = Temp(leftType);
                    Into(new Place(left), c.Left);
                    int isNull = Temp(IrType.Bool);
                    int present = NewBlock(), missing = NewBlock(), after = NewBlock();
                    b.Assign(isNull, NullTest(new Place(left), leftType)).If(Operand.Copy(isNull), missing, present);
                    At(present);
                    if (type == leftType)
                        b.Assign(target, Copied(new Place(left), c.Left.Type));
                    else if (lowerer.IsNullableValue(leftType))
                        b.Assign(target, Copied(new Place(left).Field(1), ((H.NullableType)c.Left.Type).Inner));
                    else
                        b.Assign(target, Rvalue.Unwrap(Operand.Copy(left)));
                    Goto(after);
                    At(missing);
                    Into(target, c.Right);
                    Goto(after);
                    At(after);
                    return;
                }
                case H.SwitchExpr s:
                {
                    var valueType = s.Value.Type;
                    int value = Temp(Lower(valueType));
                    Into(new Place(value), s.Value);
                    int after = NewBlock();
                    foreach (var arm in s.Arms)
                    {
                        int body = NewBlock(), next = NewBlock();
                        int matched = arm.Guard is null ? body : NewBlock();
                        Match(new Place(value), valueType, arm.Pattern, matched, next);
                        if (arm.Guard is { } guard)
                        {
                            At(matched);
                            Condition(guard, body, next);
                        }
                        At(body);
                        Into(target, arm.Value);
                        Goto(after);
                        At(next);
                    }
                    Panic();
                    At(after);
                    return;
                }
                case H.DefaultExpr:
                    b.Assign(target, Zeroable(type) ? Rvalue.Zero(type) : Rvalue.ZeroFiller(type));
                    return;
                case H.SizeOfExpr s:
                {
                    int size = Temp(IrType.U64);
                    b.Assign(size, Rvalue.SizeOf(Lower(s.Operand)));
                    b.Assign(target, type == IrType.U64 ? Rvalue.Use(Operand.Copy(size)) : Rvalue.Cast(Operand.Copy(size), type));
                    return;
                }
                case H.BlockExpr block:
                    foreach (var statement in block.Statements)
                        Statement(statement);
                    Into(target, block.Value);
                    return;
                case H.BorrowExpr borrow:
                {
                    var place = AsPlace(borrow.Place) ?? throw new LoweringException("borrows a value that is not a place");
                    b.Assign(target, borrow.Mutable ? Rvalue.RefMut(place) : Rvalue.Ref(place));
                    return;
                }
                case H.AddressOfExpr address:
                {
                    var place = AsPlace(address.Place) ?? throw new LoweringException("takes the address of a value that is not a place");
                    int reference = Temp(IrType.RefMut(Lower(address.Place.Type)));
                    b.Unsafe = true;
                    b.Assign(reference, Rvalue.RefMut(place));
                    b.Assign(target, Rvalue.Cast(Operand.Move(reference), type));
                    return;
                }
            }
            throw Unsupported($"{e.GetType().Name.Replace("Expr", "")} expressions");
        }

        /// <summary>
        /// Arrays are shared objects whose elements are written in place (as in .NET): the IR allows writing through a
        /// <c>gc</c> array only in unsafe functions, so a function that does is one.
        /// </summary>
        private void WritesElement(H.Expr target)
        {
            if (target is H.ElementExpr)
                b.Unsafe = true;
        }

        /// <summary>Whether the nullable at <paramref name="value"/> is null (a handle, or a nullable value).</summary>
        private Rvalue NullTest(Place value, IrType type) =>
            lowerer.IsNullableValue(type) ? Rvalue.Unary(UnaryOp.Not, Operand.Copy(value.Field(0))) : Rvalue.IsNull(value);

        private static bool Zeroable(IrType type) => type is PrimitiveType or OptionalType or FixedArrayType { Element: PrimitiveType };

        // ── Operators ──────────────────────────────────────

        private void Unary(H.UnaryExpr u, Place target, IrType type)
        {
            if (u.Operand.Type is H.GenericParamType && u.Op is H.UnaryOp.Negate or H.UnaryOp.Not or H.UnaryOp.BitNot
                && RequiredOperator(HirLowerer.OperatorRequirementName(u.Op == H.UnaryOp.Negate ? "-" : u.Op == H.UnaryOp.Not ? "!" : "~", false), [u.Operand], u.Type, target))
                return;
            switch (u.Op)
            {
                case H.UnaryOp.Negate:
                    b.Assign(target, Negation(Value(u.Operand), type));
                    return;
                case H.UnaryOp.Plus:
                    b.Assign(target, Value(u.Operand));
                    return;
                case H.UnaryOp.Not:
                case H.UnaryOp.BitNot:
                    b.Assign(target, Rvalue.Unary(UnaryOp.Not, Value(u.Operand)));
                    return;
                default:
                {
                    var place = Step(u);
                    b.Assign(target, Operand.Copy(place.Result));
                    return;
                }
            }
        }

        /// <summary>
        /// The type arguments of a static field's global: one per type parameter of a generic type (the global of each
        /// instance is its own), which is used inside the type (or a type nested in it): its own parameters.
        /// </summary>
        /// <summary>The enclosing generic type's own parameters, for a static member of it reached by its bare name.</summary>
        private H.HirType[] OwnStaticArguments(HirLowerer.TypeInfo info, string member)
        {
            for (var type = owner; ; type = lowerer.Types[type.Definition.DeclaringType])
            {
                if (type == info)
                    return [.. Enumerable.Range(0, info.GenericCount).Select(i => (H.HirType)new H.GenericParamType(H.GenericOwner.Type, i))];
                if (type.Definition.DeclaringType < 0)
                    throw new LoweringException($"the static member {member} of the generic type {info.Name} is used outside it without its arguments");
            }
        }

        /// <summary>A generic type's static global: the instantiation written (<c>Box&lt;i32&gt;.count</c>), else the enclosing type's own parameters.</summary>
        private IrType[] StaticArguments(HirLowerer.TypeInfo info, H.HirType[]? written = null)
        {
            if (info.GenericCount == 0)
                return [];
            if (written is { Length: > 0 })
                return [.. written.Select(Lower)];
            for (var type = owner; ; type = lowerer.Types[type.Definition.DeclaringType])
            {
                if (type == info)
                    return [.. Enumerable.Range(0, info.GenericCount).Select(i => (IrType)new IR.GenericParamType(i))];
                if (type.Definition.DeclaringType < 0)
                    throw new LoweringException($"a static field of the generic type {info.Name} is used outside it");
            }
        }

        /// <summary><c>++x</c>, <c>x++</c>, <c>--x</c>, <c>x--</c>: the place, and where the expression's value is.</summary>
        private (Place Place, Place Result) Step(H.UnaryExpr u)
        {
            if (u.Operand is H.FieldExpr { Instance: null } f && Field(f.Field) is var (info, index) && info.FieldGlobal[index] is var global && global >= 0)
            {
                // A static field: read its global, store the new value back.
                var fieldType = Lower(f.Type);
                int old = Temp(fieldType), updated = Temp(fieldType);
                var step = u.Op is H.UnaryOp.PreIncrement or H.UnaryOp.PostIncrement ? BinaryOp.Add : BinaryOp.Sub;
                b.Assign(old, Rvalue.ReadGlobal(global, StaticArguments(info, f.TypeArguments)))
                 .Assign(updated, Arithmetic(step, Operand.Copy(old), One(fieldType), fieldType))
                 .StoreGlobal(global, StaticArguments(info, f.TypeArguments), Operand.Copy(updated));
                return (new Place(updated), u.Op is H.UnaryOp.PostIncrement or H.UnaryOp.PostDecrement ? new Place(old) : new Place(updated));
            }
            var place = AsPlace(u.Operand) ?? throw new LoweringException("increments a value that is not a place");
            WritesElement(u.Operand);
            var type = Lower(u.Operand.Type);
            bool post = u.Op is H.UnaryOp.PostIncrement or H.UnaryOp.PostDecrement;
            var op = u.Op is H.UnaryOp.PreIncrement or H.UnaryOp.PostIncrement ? BinaryOp.Add : BinaryOp.Sub;
            Place result = place;
            if (post)
            {
                int old = Temp(type);
                b.Assign(old, Operand.Copy(place));
                result = new Place(old);
            }
            b.Assign(place, Arithmetic(op, Operand.Copy(place), One(type), type));
            return (place, result);
        }

        private void Binary(H.BinaryExpr bin, Place target)
        {
            if (bin.Op is H.BinaryOp.Equal or H.BinaryOp.NotEqual && bin.Left is H.TypeOfExpr leftType && bin.Right is H.TypeOfExpr rightType
                && TypeIdentifies(leftType.Operand) && TypeIdentifies(rightType.Operand))
            {
                // `typeof(A) == typeof(B)`: the same type or not, known once generics are resolved (no Type is made).
                int leftId = Temp(IrType.I32), rightId = Temp(IrType.I32);
                b.Assign(leftId, Rvalue.TypeId(Lower(leftType.Operand)))
                 .Assign(rightId, Rvalue.TypeId(Lower(rightType.Operand)))
                 .Assign(target, Rvalue.Binary(Op(bin.Op), Operand.Copy(leftId), Operand.Copy(rightId)));
                return;
            }
            if (bin.Op is H.BinaryOp.Equal or H.BinaryOp.NotEqual
                && (bin.Right is H.LiteralExpr { Value: H.NullConstant } || bin.Left is H.LiteralExpr { Value: H.NullConstant }))
            {
                var other = bin.Right is H.LiteralExpr { Value: H.NullConstant } ? bin.Left : bin.Right;
                var place = PlaceOrTemp(other);
                var otherType = Lower(other.Type);
                if (lowerer.IsNullableValue(otherType))
                {
                    b.Assign(target, bin.Op == H.BinaryOp.Equal ? Rvalue.Unary(UnaryOp.Not, Operand.Copy(place.Field(0))) : Rvalue.Use(Operand.Copy(place.Field(0))));
                }
                else if (bin.Op == H.BinaryOp.Equal)
                {
                    b.Assign(target, Rvalue.IsNull(place));
                }
                else
                {
                    int isNull = Temp(IrType.Bool);
                    b.Assign(isNull, Rvalue.IsNull(place)).Assign(target, Rvalue.Unary(UnaryOp.Not, Operand.Copy(isNull)));
                }
                return;
            }
            if (bin.Left.Type is H.PointerType || bin.Right.Type is H.PointerType)
            {
                PointerBinary(bin, target);
                return;
            }
            if ((bin.Left.Type is H.GenericParamType || bin.Right.Type is H.GenericParamType)
                && RequiredOperator(HirLowerer.OperatorRequirementName(OperatorSymbol(bin.Op), false), [bin.Left, bin.Right], bin.Type, target))
                return;
            var left = Value(bin.Left);
            var right = Value(bin.Right);
            b.Assign(target, Arithmetic(Op(bin.Op), left, right, Lower(bin.Left.Type)));
        }

        /// <summary>
        /// Whether a type's IR type tells it apart as its Type does (so comparing ids is comparing Types): not enums and
        /// interfaces (an integer, an <c>any</c> once lowered), nor a nullable handle (its Type is the class's).
        /// </summary>
        private bool TypeIdentifies(H.HirType type) => type switch
        {
            H.PrimitiveType or H.GenericParamType => true,
            H.NamedType { Type.IsDefinition: true } n => !lowerer.Types[n.Type.Index].IsEnum && !lowerer.Types[n.Type.Index].IsInterface && n.Arguments.All(TypeIdentifies),
            H.ArrayType a => TypeIdentifies(a.Element),
            H.PointerType p => TypeIdentifies(p.Target),
            H.NullableType { Inner: var inner } => inner is H.PrimitiveType || IsBoxable(inner) && TypeIdentifies(inner),
            H.FuncType f => f.Parameters.All(TypeIdentifies) && TypeIdentifies(f.Result),
            _ => false,
        };

        /// <summary>A boolean made of branches (<c>&amp;&amp;</c>, <c>||</c>, <c>is</c>).</summary>
        private void Boolean(H.Expr e, Place target)
        {
            int yes = NewBlock(), no = NewBlock(), after = NewBlock();
            Condition(e, yes, no);
            At(yes);
            b.Assign(target, Operand.Bool(true));
            Goto(after);
            At(no);
            b.Assign(target, Operand.Bool(false));
            Goto(after);
            At(after);
        }

        /// <summary>An assignment (plain or compound); returns the place assigned.</summary>
        private Place Assign(H.AssignExpr a)
        {
            switch (a.Target)
            {
                case H.FieldExpr { Instance: null } f:
                {
                    var (info, index) = Field(f.Field);
                    int global = info.FieldGlobal[index];
                    if (global < 0)
                        throw new LoweringException("assigns a constant");
                    var type = Lower(f.Type);
                    int value = Temp(type);
                    if (a.Compound is { } op)
                    {
                        int old = Temp(type);
                        b.Assign(old, Rvalue.ReadGlobal(global, StaticArguments(info, f.TypeArguments)));
                        b.Assign(value, Arithmetic(Op(op), Operand.Copy(old), Value(a.Value), type));
                    }
                    else
                    {
                        Into(new Place(value), a.Value);
                    }
                    b.StoreGlobal(global, StaticArguments(info, f.TypeArguments), Operand.Copy(value));
                    return new Place(value);
                }
                case H.PropertyExpr p:
                {
                    int value = Temp(Lower(p.Type));
                    if (a.Compound is { } op)
                    {
                        int old = Temp(Lower(p.Type));
                        Accessor(p, getter: true, null, new Place(old));
                        b.Assign(value, Arithmetic(Op(op), Operand.Copy(old), Value(a.Value), Lower(p.Type)));
                    }
                    else
                    {
                        Into(new Place(value), a.Value);
                    }
                    Accessor(p, getter: false, Operand.Copy(value), new Place(Temp(IrType.Unit)));
                    return new Place(value);
                }
            }
            var place = AsPlace(a.Target) ?? throw new LoweringException("assigns a value that is not a place");
            WritesElement(a.Target);
            if (a.Compound is { } compound)
                b.Assign(place, Arithmetic(Op(compound), Operand.Copy(place), Value(a.Value), Lower(a.Target.Type)));
            else
                Into(place, a.Value);
            return place;
        }

        // ── Conversions ────────────────────────────────────

        private Rvalue Conversion(H.ConvertExpr c, Operand value, IrType from, IrType to)
        {
            switch (c.Kind)
            {
                case H.ConversionKind.Numeric:
                    return NumericCast(value, from, to);
                case H.ConversionKind.Enum:
                case H.ConversionKind.Pointer:
                    return from == to ? Rvalue.Use(value) : Rvalue.Cast(value, to);
                case H.ConversionKind.Upcast:
                case H.ConversionKind.Wrap:
                case H.ConversionKind.Unwrap:
                    return Convert(value, from, to);
                case H.ConversionKind.Box when c.Value.Type is H.GenericParamType:
                    return GenericBox(value, from, to);
                case H.ConversionKind.Unbox when c.Type is H.GenericParamType:
                    return GenericUnbox(value, from, to);
                case H.ConversionKind.Box:
                {
                    int boxed = Temp(to);
                    BoxInto(value, c.Value.Type, new Place(boxed), to);
                    return Rvalue.Use(Operand.Copy(boxed));
                }
                case H.ConversionKind.Unbox:
                {
                    int handle = Temp(from);
                    b.Assign(handle, value);
                    int yes = NewBlock(), no = NewBlock();
                    TestType(Operand.Copy(handle), from, c.Type, yes, no);
                    At(no);
                    Panic();
                    At(yes);
                    int unboxed = Temp(to);
                    UnboxInto(Operand.Copy(handle), from, c.Type, new Place(unboxed));
                    return FieldCopied(to) ? Operand.Move(unboxed) : Operand.Copy(unboxed);
                }
                case H.ConversionKind.Downcast:
                {
                    int handle = Temp(from);
                    b.Assign(handle, value);
                    int yes = NewBlock(), no = NewBlock();
                    TestType(Operand.Copy(handle), from, c.Type, yes, no);
                    At(no);
                    Panic();
                    At(yes);
                    b.Unsafe = true;
                    return Convert(Operand.Copy(handle), from, to);
                }
            }
            throw Unsupported($"{c.Kind.ToString().ToLowerInvariant()} conversions");
        }

        /// <summary>A handle as another handle type of the same object: up or down the class tree, wrapped or unwrapped.</summary>
        private Rvalue Convert(Operand value, IrType from, IrType to)
        {
            if (from == to)
                return Rvalue.Use(value);
            if (lowerer.IsNullableValue(to) && !lowerer.IsNullableValue(from))
            {
                // T to T?: a nullable that has the value.
                int made = Temp(to);
                b.Assign(made, Rvalue.ZeroFiller(to))
                 .Assign(new Place(made).Field(0), Operand.Bool(true))
                 .Assign(new Place(made).Field(1), value);
                return FieldCopied(to) ? Operand.Move(made) : Operand.Copy(made);
            }
            if (lowerer.IsNullableValue(from) && !lowerer.IsNullableValue(to))
            {
                // T? to T: its value; none stops the program.
                int held = Temp(from);
                b.Assign(held, value);
                int present = NewBlock(), missing = NewBlock();
                b.If(Operand.Copy(new Place(held).Field(0)), present, missing);
                At(missing);
                Panic();
                At(present);
                if (!FieldCopied(to))
                    return Operand.Copy(new Place(held).Field(1));
                int copy = Temp(to);
                CopyFields(new Place(copy), new Place(held).Field(1), (StructType)to);
                return Operand.Move(copy);
            }
            if (IsDowncast(from, to))
                b.Unsafe = true;
            if (to is OptionalType { Inner: var inner } && from is not OptionalType)
            {
                if (from == inner)
                    return Rvalue.Wrap(value);
                int cast = Temp(inner);
                b.Assign(cast, Rvalue.HandleCast(value, inner));
                return Rvalue.Wrap(Operand.Copy(cast));
            }
            if (from is OptionalType { Inner: var fromInner } && to is not OptionalType)
            {
                if (fromInner == to)
                    return Rvalue.Unwrap(value);
                int unwrapped = Temp(fromInner);
                b.Assign(unwrapped, Rvalue.Unwrap(value));
                return Rvalue.HandleCast(Operand.Copy(unwrapped), to);
            }
            return Rvalue.HandleCast(value, to);
        }

        /// <summary>Whether a handle conversion goes down the class tree (allowed in unsafe code only).</summary>
        private bool IsDowncast(IrType from, IrType to)
        {
            static StructType? Of(IrType t) => t switch
            {
                GcType { Pointee: StructType s } => s,
                IR.PointerType { Pointee: StructType s } => s,
                OptionalType { Inner: var i } => Of(i),
                _ => null,
            };
            var source = Of(from);
            var target = Of(to);
            if (source is null || target is null)
                return false;
            for (StructType? s = source; s is not null; s = lowerer.Structs[s.Id].Base)
            {
                if (s.Id == target.Id)
                    return false;
            }
            return true;
        }

        // ── Calls and objects ──────────────────────────────

        private void Call(H.CallExpr call, Place target)
        {
            if (!call.Method.IsDefinition)
                throw Unsupported("methods of other assemblies");
            var info = lowerer.Types[call.Method.Type];
            int m = call.Method.Member;
            var callee = info.Definition.Methods[m];
            if (lowerer.ExternalOrigin(info) is { } externalOrigin
                && lowerer.NotCrossable(info, m, lowerer.PlanOf.TryGetValue((info.Index, m), out var externalPlan) ? externalPlan : null) is { } reason)
                throw new LoweringException(HirLowerer.CrossingError($"`{info.Name}.{callee.Name}`", externalOrigin, reason));
            if (call.Receiver is null && HirLowerer.StaticDefault(info, callee) && !call.NonVirtual)
            {
                // A static property's default (`T.Precision`): the type's own when it has one, else the default below.
                if (ConcreteStatic(call, callee, target))
                    return;
                if (call.TypeArguments.Any(t => t is H.GenericParamType))
                {
                    RequiredCall(call, info, callee, target);
                    return;
                }
            }
            else if (info.IsInterface && callee.Body is null || info.IsInterface && !callee.IsStatic && !call.NonVirtual)
            {
                if (call.Receiver is { Type: H.NamedType receiverType } && InterfaceOf(receiverType) is not null)
                    InterfaceCall(call, receiverType, info, callee, target);
                else if (call.Receiver is null && ConcreteStatic(call, callee, target))
                    return;
                else
                    RequiredCall(call, info, callee, target);
                return;
            }
            var calleePlan = lowerer.PlanOf[(info.Index, m)];
            var typeArguments = call.TypeArguments.Select(Lower).ToArray();
            if (typeArguments.Length != calleePlan.Generics.Length)
                throw new LoweringException($"calls {callee.Name} with {typeArguments.Length} type arguments instead of {calleePlan.Generics.Length}");
            var classArguments = typeArguments[..info.GenericCount];
            var arguments = new List<Operand>();
            (Place Temp, Place Back)? writeBack = null;
            if (!callee.IsStatic)
            {
                if (call.Receiver is null)
                    throw new LoweringException($"calls the instance method {callee.Name} without a receiver");
                if (info.IsStruct)
                {
                    // The arguments first (into temporaries): they may read the receiver, which the call then borrows.
                    var evaluated = new List<Operand>();
                    foreach (var argument in call.Arguments)
                        evaluated.Add(Settled(argument));
                    arguments.Add(StructReceiver(call.Receiver, out writeBack));
                    arguments.AddRange(evaluated);
                    Emit(lowerer.MethodFunctions[call.Method], typeArguments, arguments, target);
                    if (writeBack is { } written)
                        b.Assign(written.Back, Copied(written.Temp, call.Receiver!.Type));
                    return;
                }
                else if (info.IsPrimitive)
                {
                    arguments.Add(Value(call.Receiver));
                }
                else
                {
                    var receiverType = Lower(call.Receiver.Type);
                    var receiver = Value(call.Receiver);
                    var thisType = calleePlan.This!.Substitute(classArguments);
                    int handle = Temp(thisType);
                    b.Assign(handle, Convert(receiver, receiverType, thisType));
                    if (info.MethodSlot[m] >= 0 && !call.NonVirtual)
                    {
                        if (Devirtualized(call.Receiver.Type, info, m) is { } direct)
                        {
                            // The receiver's class is final: its slot's implementation is the one that runs.
                            int converted = Temp(direct.This);
                            b.Assign(converted, Convert(Operand.Copy(handle), thisType, direct.This));
                            var directArguments = new List<Operand> { Operand.Copy(converted) };
                            foreach (var argument in call.Arguments)
                                directArguments.Add(Value(argument));
                            Emit(direct.Function, direct.TypeArguments, directArguments, target);
                            return;
                        }
                        foreach (var argument in call.Arguments)
                            arguments.Add(Value(argument));
                        VirtualCall(info, m, classArguments, handle, arguments, target);
                        return;
                    }
                    arguments.Add(Operand.Copy(handle));
                }
            }
            foreach (var argument in call.Arguments)
                arguments.Add(Value(argument));
            Emit(lowerer.MethodFunctions[call.Method], typeArguments, arguments, target);
            if (writeBack is { } back)
                b.Assign(back.Back, Copied(back.Temp, call.Receiver!.Type));
        }

        /// <summary>
        /// The function a virtual call runs when it is known without the vtable: the receiver's class is final (or the
        /// method is), so the implementation in that class's slot is the one. Null otherwise.
        /// </summary>
        private (int Function, IrType[] TypeArguments, IrType This)? Devirtualized(H.HirType receiverType, HirLowerer.TypeInfo info, int method)
        {
            if (receiverType is not H.NamedType { Type.IsDefinition: true } named)
                return null;
            var receiver = lowerer.Types[named.Type.Index];
            bool final = (receiver.Definition.Flags & H.TypeFlags.Final) != 0
                || (info.Definition.Methods[method].Flags & H.MethodFlags.Final) != 0 && receiver.Index == info.Index;
            if (!receiver.IsClass || info.MethodSlot[method] >= receiver.Slots.Count)
                return null;
            // The whole program is known: no class below the receiver's runs another implementation in this slot. (Not
            // for the root class: lowering makes classes of its own below it, boxes of values.)
            if (!final && !(lowerer.Options.ClosedWorld && receiver.Base >= 0 && !receiver.SlotVaries[info.MethodSlot[method]]))
                return null;
            var (type, implementation) = receiver.Slots[info.MethodSlot[method]];
            if (implementation < 0 || lowerer.Types[type].Definition.Methods[implementation].Flags.HasFlag(H.MethodFlags.Abstract))
                return null;
            var plan = lowerer.PlanOf[(type, implementation)];
            var typeArguments = lowerer.AncestorArguments(receiver.Index, named.Arguments, type).Select(Lower).ToArray();
            return (plan.Function, typeArguments, plan.This!.Substitute(typeArguments));
        }

        /// <summary>
        /// A call of an interface method on a value of a type parameter constrained by the interface: through the
        /// parameter's requirement, met for each type argument by the type's own method.
        /// </summary>
        /// <summary>
        /// <c>INumber&lt;i32&gt;.Zero</c>: a static abstract member of an interface whose type arguments are written: the
        /// implementation of the first type argument that has one (a type parameter goes through its requirement instead).
        /// </summary>
        private bool ConcreteStatic(H.CallExpr call, H.MethodDef callee, Place target)
        {
            if (!callee.IsStatic || call.TypeArguments.Any(t => t is H.GenericParamType))
                return false;
            var parameters = callee.Parameters.Select(p => HirLowerer.Substitute(p.Type, call.TypeArguments)).ToArray();
            var result = HirLowerer.Substitute(callee.Result, call.TypeArguments);
            foreach (var argument in call.TypeArguments)
            {
                if (lowerer.ValueInfo(argument) is not { } info || lowerer.FindImplementation(info, callee.Name, parameters, result) is not { } found)
                    continue;
                var ownArguments = argument is H.NamedType named ? lowerer.AncestorArguments(named.Type.Index, named.Arguments, found.Type) : [];
                var arguments = call.Arguments.Select(Value).ToList();
                Emit(lowerer.MethodFunctions[H.MemberToken.Def(found.Type, found.Method)], [.. ownArguments.Select(Lower)], arguments, target);
                return true;
            }
            return false;
        }

        private void RequiredCall(H.CallExpr call, HirLowerer.TypeInfo iface, H.MethodDef callee, Place target)
        {
            // The parameter the requirement belongs to: the receiver's type, or, for a static member (`T.Zero`), the type
            // argument of the interface that is a parameter with that requirement.
            var receiverParam = call.Receiver?.Type as H.GenericParamType;
            if (receiverParam is null && !callee.IsStatic)
                throw Unsupported($"calls of {iface.Name}.{callee.Name} on values of interface types");
            var candidates = receiverParam is not null ? [receiverParam] : call.TypeArguments.OfType<H.GenericParamType>().Distinct();
            foreach (var param in candidates)
            {
                var requirement = lowerer.InterfaceRequirement(iface, callee, call.TypeArguments, param, Where, context);
                int index = param.Owner == H.GenericOwner.Type ? param.Index : owner.GenericCount + param.Index;
                if (index >= plan.Generics.Length)
                    continue;
                int which = Array.FindIndex(plan.Generics[index].Requirements, r => r.SameAs(requirement));
                if (which < 0)
                    continue;
                var arguments = new List<Operand>();
                if (call.Receiver is { } receiver)
                    arguments.Add(Value(receiver));
                foreach (var argument in call.Arguments)
                    arguments.Add(Value(argument));
                int next = NewBlock();
                b.CallRequired(index, which, [.. arguments], target, next, Handlers());
                At(next);
                return;
            }
            throw new LoweringException($"no type parameter of {Where} is constrained by {iface.Name} for {callee.Name}");
        }

        /// <summary>
        /// <c>a + b</c>, <c>-a</c> where an operand is a type parameter constrained by the operator: the call through the
        /// requirement (met by the primitive's own operation, or a type's declared operator). False when no requirement
        /// of the parameter is that operator.
        /// </summary>
        private bool RequiredOperator(string name, H.Expr[] operands, H.HirType result, Place target)
        {
            var types = operands.Select(o => Lower(o.Type)).ToArray();
            var resultType = Lower(result);
            foreach (var operand in operands)
            {
                if (operand.Type is not H.GenericParamType param)
                    continue;
                int index = param.Owner == H.GenericOwner.Type ? param.Index : owner.GenericCount + param.Index;
                if (index >= plan.Generics.Length)
                    continue;
                int which = Array.FindIndex(plan.Generics[index].Requirements, r => r.Name == name && r.Result == resultType && r.Parameters.SequenceEqual(types));
                if (which < 0)
                    continue;
                var arguments = operands.Select(Value).ToArray();
                int next = NewBlock();
                b.CallRequired(index, which, arguments, target, next, Handlers());
                At(next);
                return true;
            }
            // A conversion into a type parameter (`(T)n`): the requirement is the result's.
            if (result is H.GenericParamType resultParam)
            {
                int index = resultParam.Owner == H.GenericOwner.Type ? resultParam.Index : owner.GenericCount + resultParam.Index;
                int which = index < plan.Generics.Length ? Array.FindIndex(plan.Generics[index].Requirements, r => r.Name == name && r.Result == resultType && r.Parameters.SequenceEqual(types)) : -1;
                if (which >= 0)
                {
                    var arguments = operands.Select(Value).ToArray();
                    int next = NewBlock();
                    b.CallRequired(index, which, arguments, target, next, Handlers());
                    At(next);
                    return true;
                }
            }
            return false;
        }

        private static string OperatorSymbol(H.BinaryOp op) => op switch
        {
            H.BinaryOp.Add => "+", H.BinaryOp.Subtract => "-", H.BinaryOp.Multiply => "*", H.BinaryOp.Divide => "/", H.BinaryOp.Remainder => "%",
            H.BinaryOp.BitAnd => "&", H.BinaryOp.BitOr => "|", H.BinaryOp.BitXor => "^", H.BinaryOp.ShiftLeft => "<<", H.BinaryOp.ShiftRight => ">>",
            H.BinaryOp.Equal => "==", H.BinaryOp.NotEqual => "!=", H.BinaryOp.Less => "<", H.BinaryOp.LessEqual => "<=",
            H.BinaryOp.Greater => ">", H.BinaryOp.GreaterEqual => ">=",
            _ => op.ToString(),
        };

        /// <summary>
        /// The <c>&amp;mut</c> a struct method takes: the struct's own place when it can be borrowed (a local, a field of a
        /// local, <c>this</c>), else a copy that is written back after the call (fields of objects cannot be borrowed).
        /// </summary>
        /// <summary>An argument's value in a temporary (or a constant): read now, not when the call reads its operands.</summary>
        private Operand Settled(H.Expr e)
        {
            if (e is H.LiteralExpr literal && literal.Value is not (H.NullConstant or H.StringConstant))
                return Constant(literal.Value, Lower(e.Type));
            int temp = Temp(Lower(e.Type));
            Into(new Place(temp), e);
            return Copied(new Place(temp), e.Type);
        }

        private Operand StructReceiver(H.Expr receiver, out (Place Temp, Place Back)? writeBack)
        {
            writeBack = null;
            var structType = Lower(receiver.Type);
            Place place;
            var named = AsPlace(receiver);
            if (named is { } p && Borrowable(p))
            {
                place = p;
            }
            else
            {
                int copy = Temp(structType);
                if (named is { } back)
                {
                    b.Assign(copy, Copied(back, receiver.Type));
                    writeBack = (new Place(copy), back);
                }
                else
                {
                    Into(new Place(copy), receiver);
                }
                place = new Place(copy);
            }
            int reference = Temp(IrType.RefMut(structType));
            b.Assign(reference, Rvalue.RefMut(place));
            return Operand.Move(reference);
        }

        /// <summary>
        /// Whether a struct method may borrow <paramref name="place"/> itself: a local or a field of one, or through the
        /// <c>&amp;mut this</c> of a struct method; not through an object or array handle (those cannot be borrowed into).
        /// </summary>
        private bool Borrowable(Place place)
        {
            var projections = place.Projections.AsSpan();
            if (owner.IsStruct && place.Local == self && projections.Length > 0 && projections[0] is DerefProjection)
                projections = projections[1..];
            foreach (var projection in projections)
            {
                if (projection is not FieldProjection)
                    return false;
            }
            return true;
        }

        private void Emit(int function, List<Operand> arguments, Place target) => Emit(function, [], arguments, target);

        private void Emit(int function, IrType[] typeArguments, List<Operand> arguments, Place target)
        {
            int next = NewBlock();
            b.Call(function, typeArguments, [.. arguments], target, next, Handlers());
            At(next);
        }

        /// <summary>
        /// A string literal: a new <c>System.String</c> over a copy of the literal's code units (kept once in static
        /// data).
        /// </summary>
        private void StringLiteral(string text, Place target, H.HirType stringType)
        {
            if (lowerer.StringType() is not var (info, constructor))
                throw new LoweringException("string literals need System.String (with a constructor from u16[])");
            // The literal's code units: those of the string's unit type (String<u8>: UTF-8), u16 for a plain string class.
            var unit = stringType is H.NamedType { Arguments: [H.PrimitiveType { Kind: var kind }] } ? kind : H.Primitive.U16;
            var element = HirLowerer.Primitive(unit);
            var unitsType = lowerer.Options.Gc.ManagesObjects ? new GcType(new IR.ArrayType(element)) : (IrType)new IR.PointerType(new IR.ArrayType(element), Mutable: true);
            // Made once, the first time it is reached, then kept in a global (strings do not change; .NET interns literals).
            // Not under arena collection: a literal first reached inside a region would die with it, cached.
            var handleType = Lower(stringType);
            var optional = new OptionalType(handleType);
            bool cache = (lowerer.Options.Gc.Capabilities & GcCapabilities.Regions) == 0;
            int global = cache ? lowerer.LiteralGlobal(text, unit, optional) : -1;
            int after = NewBlock();
            if (cache)
            {
                int cached = Temp(optional);
                int missing = Temp(IrType.Bool);
                int make = NewBlock(), have = NewBlock();
                b.Assign(cached, Rvalue.ReadGlobal(global))
                 .Assign(missing, Rvalue.IsNull(cached))
                 .If(Operand.Copy(missing), make, have);
                At(have);
                b.Assign(target, Rvalue.Unwrap(Operand.Copy(cached)));
                Goto(after);
                At(make);
            }
            int data = Temp(IrType.Ref(new IR.ArrayType(element)));
            b.Assign(data, new StaticRvalue(lowerer.StringData(text, unit)));
            int units = Temp(unitsType);
            Emit(lowerer.StringUnits(unit), [Operand.Copy(data)], new Place(units));
            pending = units;
            var unitArray = new H.ArrayType(H.HirType.Of(unit), 1);
            int made = Temp(handleType);
            New(new H.NewExpr(H.MemberToken.Def(info.Index, constructor), [new PendingExpr(unitArray)], [], stringType), new Place(made), handleType);
            if (cache)
            {
                int wrapped = Temp(optional);
                b.Assign(wrapped, Rvalue.Wrap(Operand.Copy(made))).StoreGlobal(global, Operand.Copy(wrapped));
            }
            b.Assign(target, Operand.Copy(made));
            Goto(after);
            At(after);
        }

        /// <summary>A call through the vtable: the slot of the class that introduced the method.</summary>
        private void VirtualCall(HirLowerer.TypeInfo info, int method, IrType[] classArguments, int handle, List<Operand> arguments, Place target)
        {
            int slot = info.MethodSlot[method];
            var introducer = lowerer.Types[info.MethodIntroducer[method]];
            var introducerArguments = lowerer.AncestorIrArguments(info, introducer.Index).Select(a => a.Substitute(classArguments)).ToArray();
            var vtType = HirLowerer.VtablePointer(IrType.Struct(introducer.Vtable, introducerArguments));
            int rootVt = Temp(HirLowerer.VtablePointer(IrType.Struct(lowerer.ObjectVtable)));
            int vt = Temp(vtType);
            int function = Temp(lowerer.SlotType(info.SlotIntroducers[slot]).Substitute(introducerArguments));
            b.Unsafe = true;
            b.Assign(rootVt, Operand.Copy(new Place(handle).Deref().Field(1)))
             .Assign(vt, Rvalue.HandleCast(Operand.Copy(rootVt), vtType))
             .Assign(function, Operand.Copy(new Place(vt).Deref().Field(slot + HirLowerer.SlotBase)));
            int next = NewBlock();
            b.CallIndirect(Operand.Copy(function), [Operand.Copy(handle), .. arguments], target, next, Handlers());
            At(next);
        }

        private void Accessor(H.PropertyExpr p, bool getter, Operand? value, Place target)
        {
            if (!p.Property.IsDefinition)
                throw Unsupported("properties of other assemblies");
            var info = lowerer.Types[p.Property.Type];
            var property = info.Definition.Properties[p.Property.Member];
            int accessor = getter ? property.Getter : property.Setter;
            if (accessor < 0)
                throw new LoweringException($"the property {property.Name} has no {(getter ? "getter" : "setter")}");
            var accessorMethod = info.Definition.Methods[accessor];
            var arguments = p.Indices.ToList();
            // A static accessor of a generic type reached by its bare name (`One + One` inside Vector3<T>): the type's own
            // parameters (from outside, the binder names the instantiation and calls the getter itself).
            var typeArguments = p.Instance is { Type: H.NamedType { Type.IsDefinition: true } receiver } && !accessorMethod.IsStatic
                ? lowerer.AncestorArguments(receiver.Type.Index, receiver.Arguments, info.Index)
                : accessorMethod.IsStatic && info.GenericCount > 0 ? OwnStaticArguments(info, property.Name) : [];
            var call = new H.CallExpr(accessorMethod.IsStatic ? null : p.Instance, H.MemberToken.Def(info.Index, accessor), typeArguments, [.. arguments], getter ? p.Type : H.HirType.Void);
            if (value is { } v)
            {
                // The setter's value comes last: an already evaluated operand, passed through a local.
                int holder = Temp(Lower(p.Type));
                b.Assign(holder, v);
                pending = holder;
                call = call with { Arguments = [.. arguments, new PendingExpr(p.Type)] };
            }
            Call(call, target);
        }

        /// <summary>A value already in a local (the setter's argument), as an expression.</summary>
        private sealed record PendingExpr(H.HirType Type) : H.Expr(Type);

        private int pending = -1;

        private void New(H.NewExpr n, Place target, IrType type)
        {
            if (n.Type is H.GenericParamType param)
            {
                // `new T(args)` with `T : new(…)`: the requirement, met per type argument by a `$new` wrapper.
                int index = param.Owner == H.GenericOwner.Type ? param.Index : owner.GenericCount + param.Index;
                var types = n.Arguments.Select(a => Lower(a.Type)).ToArray();
                int which = index < plan.Generics.Length ? Array.FindIndex(plan.Generics[index].Requirements, r => r.Name == Requirement.Constructor && r.Result == type && r.Parameters.SequenceEqual(types)) : -1;
                if (which < 0)
                    throw new LoweringException($"no constructor requirement new({string.Join(", ", types.Select(t => t.ToString()))}) on {n.Type}");
                var arguments = n.Arguments.Select(Value).ToArray();
                int next = NewBlock();
                b.CallRequired(index, which, arguments, target, next, Handlers());
                At(next);
                return;
            }
            if (n.Type is not H.NamedType { Type.IsDefinition: true } named)
                throw Unsupported("new of this type");
            var info = lowerer.Types[named.Type.Index];
            var typeArguments = named.Arguments.Select(Lower).ToArray();
            if (info.IsClass)
            {
                if ((info.Definition.Flags & H.TypeFlags.Abstract) != 0)
                    throw new LoweringException($"{info.Name} is abstract");
                var structType = lowerer.StructOf(info, typeArguments);
                if (info.InPlaceField >= 0)
                {
                    NewInPlace(n, info, typeArguments, (StructType)structType, target, type);
                    return;
                }
                int value = Temp(structType);
                int vt = Temp(HirLowerer.VtablePointer(IrType.Struct(info.Vtable, typeArguments)));
                b.Assign(value, Rvalue.ZeroFiller(structType));
                b.Assign(new Place(value).Field(0), Rvalue.ClassId(structType));
                Emit(info.VtableMaker, typeArguments, [], new Place(vt));
                b.Assign(new Place(value).Field(1), Rvalue.HandleCast(Operand.Copy(vt), HirLowerer.VtablePointer(IrType.Struct(lowerer.ObjectVtable))));
                int handle = Temp(type);
                Operand? finalizer = null;
                if (info.Finalizer >= 0 && lowerer.ObjectAlloc == AllocKind.Gc)
                {
                    // The class has a destructor: the runtime runs `$destroy(self)` when the object dies.
                    int fn = Temp(new FunctionType([type], IrType.Unit));
                    b.Assign(fn, Rvalue.FunctionRef(info.Finalizer, typeArguments));
                    finalizer = Operand.Move(fn);
                }
                if (lowerer.ExternalOrigin(info) is { } externalOrigin)
                    throw new LoweringException(HirLowerer.CrossingError($"`{info.Name}`", externalOrigin, "its objects live in that domain's collector and cannot be made here"));
                b.Assign(handle, new AllocRvalue(lowerer.ObjectAlloc, Operand.Move(value), finalizer));
                if (n.Constructor is { } constructor)
                {
                    var arguments = new List<Operand> { Operand.Copy(handle) };
                    foreach (var argument in n.Arguments)
                        arguments.Add(Value(argument));
                    Emit(lowerer.MethodFunctions[constructor], typeArguments, arguments, new Place(Temp(IrType.Unit)));
                }
                foreach (var init in n.Initializers)
                    Initialize(handle, n.Type, new Place(handle), info, init);
                b.Assign(target, Operand.Copy(handle));
                return;
            }
            if (info.IsStruct)
            {
                var structType = lowerer.StructOf(info, typeArguments);
                int value = Temp(structType);
                b.Assign(value, Rvalue.ZeroFiller(structType));
                if (n.Constructor is { } constructor)
                {
                    int reference = Temp(IrType.RefMut(structType));
                    b.Assign(reference, Rvalue.RefMut(value));
                    var arguments = new List<Operand> { Operand.Move(reference) };
                    foreach (var argument in n.Arguments)
                        arguments.Add(Value(argument));
                    Emit(lowerer.MethodFunctions[constructor], typeArguments, arguments, new Place(Temp(IrType.Unit)));
                }
                foreach (var init in n.Initializers)
                    Initialize(value, n.Type, new Place(value), info, init);
                b.Assign(target, FieldCopied(structType) ? Operand.Move(value) : Operand.Copy(value));
                return;
            }
            throw Unsupported($"new {info.Definition.Kind.ToString().ToLowerInvariant()}");
        }

        /// <summary>
        /// <c>new C(args)</c> of a class with an array kept in place: the arguments once, the constructor's length (its
        /// <c>$fixed{n}</c> method) from them, one allocation of the object with that many elements, then the constructor.
        /// </summary>
        private void NewInPlace(H.NewExpr n, HirLowerer.TypeInfo info, IrType[] typeArguments, StructType structType, Place target, IrType type)
        {
            if (n.Constructor is not { IsDefinition: true } constructor)
                throw new LoweringException($"{info.Name} keeps an array in place: it is made by one of its constructors");
            var methods = info.Definition.Methods;
            int ordinal = methods.Take(constructor.Member).Count(m => m.Kind == H.MethodKind.Constructor);
            int lengthMethod = methods.FindIndex(m => m.Name == $"$fixed{ordinal}" && m.IsStatic);
            if (lengthMethod < 0)
                throw new LoweringException($"{info.Name}'s constructor gives no length for its array kept in place (`: fixed(n)`)");
            var held = new List<int>();
            foreach (var argument in n.Arguments)
            {
                int temp = Temp(Lower(argument.Type));
                Into(new Place(temp), argument);
                held.Add(temp);
            }
            var lengthType = Lower(methods[lengthMethod].Result);
            int length = Temp(lengthType);
            Emit(lowerer.MethodFunctions[H.MemberToken.Def(info.Index, lengthMethod)], typeArguments, [.. held.Select(h => Operand.Copy(h))], new Place(length));
            if (lowerer.ExternalOrigin(info) is { } externalOrigin)
                throw new LoweringException(HirLowerer.CrossingError($"`{info.Name}`", externalOrigin, "its objects live in that domain's collector and cannot be made here"));
            var element = ((IR.ArrayType)lowerer.Structs[structType.Id].Fields[^1]).Element.Substitute(typeArguments);
            int handle = Temp(type);
            int vt = Temp(HirLowerer.VtablePointer(IrType.Struct(info.Vtable, typeArguments)));
            b.Assign(handle, new ArrayRvalue(lowerer.ObjectAlloc, element, Operand.Copy(length)) { Owner = structType });
            b.Assign(new Place(handle).Deref().Field(0), Rvalue.ClassId(structType));
            Emit(info.VtableMaker, typeArguments, [], new Place(vt));
            b.Assign(new Place(handle).Deref().Field(1), Rvalue.HandleCast(Operand.Copy(vt), HirLowerer.VtablePointer(IrType.Struct(lowerer.ObjectVtable))));
            var arguments = new List<Operand> { Operand.Copy(handle) };
            arguments.AddRange(held.Select(h => Operand.Copy(h)));
            Emit(lowerer.MethodFunctions[constructor], typeArguments, arguments, new Place(Temp(IrType.Unit)));
            foreach (var init in n.Initializers)
                Initialize(handle, n.Type, new Place(handle), info, init);
            b.Assign(target, Operand.Copy(handle));
        }

        /// <summary>A local of this function as an expression (the object a <c>new</c> is making).</summary>
        private sealed record HeldExpr(int Local, H.HirType Type) : H.Expr(Type);

        /// <summary><c>x with { A = v }</c>: a fresh object or value with the source's fields, the changed ones set instead.</summary>
        private void With(H.WithExpr w, Place target, IrType type)
        {
            if (w.Value.Type is not H.NamedType { Type.IsDefinition: true } named)
                throw Unsupported("`with` on this type");
            var info = lowerer.Types[named.Type.Index];
            var typeArguments = named.Arguments.Select(Lower).ToArray();
            var structType = lowerer.StructOf(info, typeArguments);
            int source = Temp(Lower(w.Value.Type));
            Into(new Place(source), w.Value);
            var changes = new Dictionary<int, H.MemberInit>();
            foreach (var change in w.Changes)
            {
                var (declaring, index) = Field(change.Member);
                if (declaring != info)
                    throw new LoweringException($"`with` sets a field of {declaring.Name}, not of {info.Name}");
                changes[index] = change;
            }
            int made;
            Place from, to;
            if (info.IsClass)
            {
                int value = Temp(structType);
                int vt = Temp(HirLowerer.VtablePointer(IrType.Struct(info.Vtable, typeArguments)));
                b.Assign(value, Rvalue.ZeroFiller(structType));
                b.Assign(new Place(value).Field(0), Rvalue.ClassId(structType));
                Emit(info.VtableMaker, typeArguments, [], new Place(vt));
                b.Assign(new Place(value).Field(1), Rvalue.HandleCast(Operand.Copy(vt), HirLowerer.VtablePointer(IrType.Struct(lowerer.ObjectVtable))));
                made = Temp(type);
                b.Assign(made, new AllocRvalue(lowerer.ObjectAlloc, Operand.Move(value)));
                from = new Place(source).Deref();
                to = new Place(made).Deref();
            }
            else if (info.IsStruct)
            {
                made = Temp(structType);
                b.Assign(made, Rvalue.ZeroFiller(structType));
                from = new Place(source);
                to = new Place(made);
            }
            else
            {
                throw Unsupported("`with` on this type");
            }
            var fields = info.Definition.Fields;
            for (int f = 0; f < fields.Count; f++)
            {
                if ((fields[f].Flags & (H.FieldFlags.Static | H.FieldFlags.Const)) != 0)
                    continue;
                if (changes.TryGetValue(f, out var change))
                    Initialize(made, w.Value.Type, new Place(made), info, change);
                else if (info.FieldOffset is null)
                    b.Assign(to.Field(info.FieldIndex[f]), Operand.Copy(from.Field(info.FieldIndex[f])));
                else
                    b.Assign(FieldPlace(info, to, f), Operand.Copy(FieldPlace(info, from, f)));
            }
            b.Assign(target, info.IsClass || !FieldCopied(structType) ? Operand.Copy(made) : Operand.Move(made));
        }

        private void Initialize(int local, H.HirType objectType, Place obj, HirLowerer.TypeInfo info, H.MemberInit init)
        {
            if (init.Kind == H.MemberKind.Property)
            {
                // `new T { P = v }`: P's setter, on the new object.
                var property = lowerer.Assembly.Types[init.Member.Type].Properties[init.Member.Member];
                if (property.Setter < 0)
                    throw new LoweringException($"{property.Name} has no setter");
                var typeArguments = objectType is H.NamedType named ? lowerer.AncestorArguments(named.Type.Index, named.Arguments, init.Member.Type) : [];
                var call = new H.CallExpr(new HeldExpr(local, objectType), H.MemberToken.Def(init.Member.Type, property.Setter), typeArguments, [init.Value], H.HirType.Void);
                Call(call, new Place(Temp(IrType.Unit)));
                return;
            }
            if (init.Kind != H.MemberKind.Field)
                throw Unsupported("event initializers");
            var (declaring, index) = Field(init.Member);
            int field = declaring.FieldIndex[index];
            var place = info.IsClass ? obj.Deref().Field(field) : FieldPlace(declaring, obj, index);
            Into(place, init.Value);
        }

        /// <summary>
        /// The place of field <paramref name="index"/> of a value at <paramref name="value"/>: its IR field, or, for a
        /// value with `[ValueLayout]`, the memory at its offset read as its type (<c>*(T*)((u8*)&amp;value + offset)</c>).
        /// </summary>
        private Place FieldPlace(HirLowerer.TypeInfo info, Place value, int index)
        {
            if (info.FieldOffset is not { } offsets || offsets[index] < 0)
                return value.Field(info.FieldIndex[index]);
            b.Unsafe = true;
            var fieldType = Lower(info.Definition.Fields[index].Type);
            int reference = Temp(IrType.RefMut(IrType.Struct(info.Struct)));
            b.Assign(reference, Rvalue.RefMut(value));
            int bytes = Temp(new IR.PointerType(IrType.U8, Mutable: true));
            b.Assign(bytes, Rvalue.Cast(Operand.Move(reference), new IR.PointerType(IrType.U8, Mutable: true)));
            int raw = Temp(IrType.U64);
            b.Assign(raw, Rvalue.Cast(Operand.Copy(bytes), IrType.U64));
            if (offsets[index] > 0)
                b.Assign(raw, Rvalue.Binary(BinaryOp.Add, Operand.Copy(raw), Operand.Const(offsets[index], IrType.U64)));
            var pointerType = new IR.PointerType(fieldType, Mutable: true);
            int pointer = Temp(pointerType);
            b.Assign(pointer, Rvalue.Cast(Operand.Copy(raw), pointerType));
            return new Place(pointer).Deref();
        }
    }
}

namespace Larva.Hir.Lowering
{
    using Larva.IR;
    using Larva.IR.Lowering;
    using H = Larva.Hir;

    /// <summary>
    /// Resumable bodies: iterator steps (<c>MethodFlags.Iterator</c>) and async steps (<c>MethodFlags.Async</c>), each the
    /// body of a function in the class the frontend made for it (the function's arguments and <c>this</c> in fields
    /// <c>$p{i}</c> and <c>$this</c>). Each is a coroutine whose frame is that object: the IR pass
    /// (<see cref="Coroutines"/>) moves its locals into the object and resumes it after its last pause.
    /// <list type="bullet">
    /// <item>Iterators derive from <c>System.Iterator&lt;T&gt;</c>: each <c>yield</c> stores the value in <c>current</c>
    /// and pauses giving true; the end pauses for good giving false (every later step gives false again).</item>
    /// <item>Async functions derive from <c>Task</c> / <c>Task&lt;T&gt;</c> (the frame is the task): at an <c>await</c>
    /// the step asks <c>Task.Pause(awaited)</c>, which registers it to run again once the awaited task is done and says to
    /// pause (or says to go on when it is already done); after the pause, <c>GetResult()</c> / <c>Check()</c> gives the
    /// value or throws the error. A <c>return</c> completes the task (<c>SetResult</c> / <c>Complete</c>).</item>
    /// </list>
    /// </summary>
    internal sealed partial class BodyLowerer
    {
        private bool IsIterator => (method.Flags & H.MethodFlags.Iterator) != 0 && lambda < 0;
        private bool IsAsync => (method.Flags & H.MethodFlags.Async) != 0 && lambda < 0;
        private bool IsCoroutine => (method.Flags & (H.MethodFlags.Iterator | H.MethodFlags.Async)) != 0 && lambda < 0;

        /// <summary>A step's fields: each parameter's, <c>this</c>'s, and (iterators) the base's <c>current</c>.</summary>
        private int[] iteratorParameters = [];
        private int iteratorThis = -1, iteratorCurrent = -1;

        /// <summary>The pause function a yield calls (the coroutine pass's await).</summary>
        private int iteratorPause = -1;

        /// <summary>Async: Task's and Task&lt;T&gt;'s types, and the await functions made so far.</summary>
        private HirLowerer.TypeInfo? task, taskOf;
        private int awaits;

        /// <summary>A step's type parameters: its class's are the function's owner's, then the function's own.</summary>
        private static HirLowerer.GenericContext ContextOf(HirLowerer lowerer, HirLowerer.TypeInfo owner, H.MethodDef method)
        {
            if ((method.Flags & (H.MethodFlags.Iterator | H.MethodFlags.Async)) != 0 && owner.Definition.DeclaringType >= 0)
                return new HirLowerer.GenericContext(lowerer.Types[owner.Definition.DeclaringType].GenericCount);
            return new HirLowerer.GenericContext(owner.GenericCount);
        }

        private void PrepareCoroutine()
        {
            var fields = owner.Definition.Fields;
            int Own(string name)
            {
                int f = fields.FindIndex(x => x.Name == name);
                return f >= 0 ? owner.FieldIndex[f] : -1;
            }
            iteratorParameters = new int[fields.Count(f => f.Name.StartsWith("$p", StringComparison.Ordinal))];
            for (int p = 0; p < iteratorParameters.Length; p++)
                iteratorParameters[p] = Own($"$p{p}");
            iteratorThis = Own("$this");
            if (self < 0)
                throw new LoweringException("a resumable body belongs to an instance of its class");
            // Its values and locals are written into the object (the coroutine's frame), past any borrow.
            b.Unsafe = true;
            var handle = OwnHandle;
            var suspend = new FunctionBuilder(plan.Name + "$suspend", IrType.Bool) { Linkage = Linkage.Internal, Unsafe = true };
            HirLowerer.DeclareGenerics(suspend, plan.Generics);
            int frame = suspend.Argument(handle, "frame", mutable: false);
            if (IsIterator)
            {
                for (var info = owner; info.Base >= 0;)
                {
                    info = lowerer.Types[info.Base];
                    int f = info.Definition.Fields.FindIndex(x => x.Name == "current");
                    if (f >= 0 && info.Definition.FullName == "System.Iterator")
                    {
                        iteratorCurrent = info.FieldIndex[f];
                        break;
                    }
                }
                if (iteratorCurrent < 0)
                    throw new LoweringException("an iterator step belongs to a class deriving from System.Iterator<T>");
                // A yield always pauses.
                suspend.Argument(handle, "task", mutable: false);
                suspend.Assign(FunctionBuilder.ReturnPlace, Operand.Bool(true)).Return();
                var pause = new FunctionBuilder(plan.Name + "$pause", IrType.Unit) { Linkage = Linkage.Internal };
                HirLowerer.DeclareGenerics(pause, plan.Generics);
                pause.Argument(handle, "task", mutable: false);
                pause.Return();
                lock (lowerer.Functions)
                {
                    lowerer.Functions.Add(suspend.Build());
                    iteratorPause = lowerer.Functions.Count;
                    lowerer.Functions.Add(Awaiting(pause.Build()));
                }
                return;
            }
            // Async: pause unless the awaited task is done (Task.Pause registers the frame to run again).
            task = lowerer.Types.FirstOrDefault(i => i.Definition.FullName == "System.Threading.Tasks.Task" && i.GenericCount == 0)
                ?? throw new LoweringException("async functions need System.Threading.Tasks.Task");
            taskOf = lowerer.Types.FirstOrDefault(i => i.Definition.FullName == "System.Threading.Tasks.Task" && i.GenericCount == 1);
            var (pauseFunction, pauseThis) = TaskMethod(task, "Pause");
            int awaited = suspend.Argument(pauseThis, "task", mutable: false);
            int asTask = suspend.Local(pauseThis, null, mutable: true);
            int done = suspend.NewBlock();
            suspend.Assign(asTask, Rvalue.HandleCast(Operand.Copy(frame), pauseThis))
                   .Call(pauseFunction, [Operand.Copy(asTask), Operand.Copy(awaited)], FunctionBuilder.ReturnPlace, done);
            suspend.At(done).Return();
            lock (lowerer.Functions)
                lowerer.Functions.Add(suspend.Build());
        }

        private static IrFunction Awaiting(IrFunction function) =>
            function with { Attributes = [.. function.Attributes, new IrAttribute(Coroutines.AwaitAttribute, [])] };

        /// <summary>A method of Task / Task&lt;T&gt; by name: its function and its <c>this</c> type (with T as given).</summary>
        private (int Function, IrType This) TaskMethod(HirLowerer.TypeInfo info, string name, IrType? argument = null)
        {
            int m = info.Definition.Methods.FindIndex(x => x.Name == name);
            if (m < 0)
                throw new LoweringException($"{info.Name} has no {name}");
            var calleePlan = lowerer.PlanOf[(info.Index, m)];
            var thisType = argument is null ? calleePlan.This! : calleePlan.This!.Substitute([argument]);
            return (lowerer.MethodFunctions[H.MemberToken.Def(info.Index, m)], thisType);
        }

        private Place IteratorField(int field) => new Place(self).Deref().Field(field);

        /// <summary>A pause: the step returns what the return place holds; the next step goes on from here.</summary>
        private void Pause() => Emit(iteratorPause, OwnTypeArguments, [Operand.Copy(new Place(self))], new Place(Temp(IrType.Unit)));

        /// <summary><c>yield value</c>: the value into <c>current</c>, and a pause that gives true.</summary>
        private void Yield(H.YieldStmt s)
        {
            if (!IsIterator)
                throw new LoweringException("`yield` outside an iterator");
            if (s.Value is not { } value)
            {
                EndIterator();
                return;
            }
            Into(IteratorField(iteratorCurrent), value);
            b.Assign(FunctionBuilder.ReturnPlace, Operand.Bool(true));
            Pause();
        }

        /// <summary>The end (or <c>yield break</c>): false, now and on every later step.</summary>
        private void EndIterator()
        {
            int end = NewBlock();
            Goto(end);
            At(end);
            b.Assign(FunctionBuilder.ReturnPlace, Operand.Bool(false));
            Pause();
            Goto(end);
            open = false;
        }

        /// <summary>
        /// <c>await t</c>: a call to an await function made for this place (the coroutine pass first asks the frame's
        /// suspend whether to pause); it gives t's value, or throws t's error.
        /// </summary>
        private void Await(H.AwaitExpr a, Place target)
        {
            if (!IsAsync)
                throw new LoweringException("`await` outside an async function");
            var taskType = Lower(a.Task.Type);
            int awaitedTask = Temp(taskType);
            Into(new Place(awaitedTask), a.Task);
            var resultType = Lower(a.Type);
            var f = new FunctionBuilder($"{plan.Name}$await{awaits++}", resultType) { Linkage = Linkage.Internal, Unsafe = true };
            HirLowerer.DeclareGenerics(f, plan.Generics);
            int argument = f.Argument(taskType, "task", mutable: false);
            var (function, thisType) = a.Type == H.HirType.Void ? TaskMethod(task!, "Check") : TaskMethod(taskOf!, "GetResult", resultType);
            int receiver = f.Local(thisType, null, mutable: true);
            int done = f.NewBlock();
            f.Assign(receiver, Rvalue.HandleCast(Operand.Copy(argument), thisType))
             .Call(function, a.Type == H.HirType.Void ? [] : [resultType], [Operand.Copy(receiver)], FunctionBuilder.ReturnPlace, done);
            f.At(done).Return();
            int index;
            lock (lowerer.Functions)
            {
                index = lowerer.Functions.Count;
                lowerer.Functions.Add(Awaiting(f.Build()));
            }
            Emit(index, OwnTypeArguments, [Operand.Copy(awaitedTask)], target);
        }

        /// <summary>An async function's <c>return</c> (or its end): the task is done, with the value if it has one.</summary>
        private void AsyncReturn(H.Expr? value)
        {
            if (value is not null)
            {
                var valueType = Lower(value.Type);
                int result = Temp(valueType);
                Into(new Place(result), value);
                RunFinally(0);
                var (setResult, thisType) = TaskMethod(taskOf!, "SetResult", valueType);
                int receiver = Temp(thisType);
                b.Assign(receiver, Rvalue.HandleCast(Operand.Copy(self), thisType));
                Emit(setResult, [valueType], [Operand.Copy(receiver), Operand.Copy(result)], new Place(Temp(IrType.Unit)));
            }
            else
            {
                RunFinally(0);
                var (complete, thisType) = TaskMethod(task!, "Complete");
                int receiver = Temp(thisType);
                b.Assign(receiver, Rvalue.HandleCast(Operand.Copy(self), thisType));
                Emit(complete, [], [Operand.Copy(receiver)], new Place(Temp(IrType.Unit)));
            }
            if (open)
                b.Return();
            open = false;
        }

        private IrFunction MarkCoroutine(IrFunction function) =>
            function with { Attributes = [.. function.Attributes, new IrAttribute(Coroutines.CoroutineAttribute, [new TextArgument(self.ToString(System.Globalization.CultureInfo.InvariantCulture)), new TextArgument(plan.Name + "$suspend")])] };
    }
}

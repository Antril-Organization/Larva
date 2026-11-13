using System.Reflection;

namespace Larva.IR.Lowering
{
    /// <summary>
    /// Makes functions resumable (coroutines). A coroutine is marked <c>[Larva.Coroutine(frame, suspend)]</c>: argument
    /// <c>frame</c> is a handle to an object (its frame) and <c>suspend</c> names a method of that object,
    /// <c>suspend(frame, task) -&gt; bool</c>. Its body was checked like any function, with each pause written as a call
    /// to a function marked <c>[Larva.Await]</c> (which, as written, waits where it is). This pass, run before code is
    /// generated for any back end, rewrites each coroutine so that:
    /// <list type="bullet">
    /// <item>its locals live in the frame: fields are added to the frame's struct (zeroed when the frame is made), and
    /// every place rooted at a local is rooted at that field instead (the arguments are stored there on the first run);</item>
    /// <item>it starts by looking at the frame's state: 0 runs from the start, k resumes after the k-th pause;</item>
    /// <item>at each await it first calls <c>suspend(frame, task)</c>: when that says the task is not done (and has
    /// registered the frame to be resumed), it records the state and returns; otherwise, and when resumed, it calls the
    /// await function, which then finds the task done.</item>
    /// </list>
    /// The checks (types, borrows) ran before: the rewritten code is what they approved, with its storage moved.
    /// </summary>
    public static class Coroutines
    {
        public const string CoroutineAttribute = "Larva.Coroutine";
        public const string AwaitAttribute = "Larva.Await";

        public static IrModule Lower(IrModule module)
        {
            if (!module.Functions.Any(f => !f.IsDeclaration && f.Attributes.Any(a => a.Name == CoroutineAttribute)))
                return module;
            var awaits = new HashSet<int>(Enumerable.Range(0, module.Functions.Length).Where(i => module.Functions[i].Attributes.Any(a => a.Name == AwaitAttribute)));
            var structs = module.Structs.ToArray();
            var functions = module.Functions.ToArray();
            var appended = new Dictionary<int, int>(); // frame struct → its field count before this pass
            for (int f = 0; f < functions.Length; f++)
            {
                var function = functions[f];
                if (function.IsDeclaration || function.Attributes.FirstOrDefault(a => a.Name == CoroutineAttribute) is not { Arguments: [TextArgument frameText, TextArgument suspendName] })
                    continue;
                int frame = int.Parse(frameText.Value, System.Globalization.CultureInfo.InvariantCulture);
                // Linked modules qualify names (`module::name`): the frame's Suspend is in the step's module.
                string prefix = function.Name.Contains("::", StringComparison.Ordinal) ? function.Name[..(function.Name.IndexOf("::", StringComparison.Ordinal) + 2)] : "";
                int suspend = Array.FindIndex(functions, g => g.Name == prefix + suspendName.Value);
                if (suspend < 0)
                    suspend = Array.FindIndex(functions, g => g.Name == suspendName.Value || g.Name.EndsWith("::" + suspendName.Value, StringComparison.Ordinal));
                if (suspend < 0 || function.Locals[frame].Type.Pointee() is not StructType frameStruct)
                    throw new InvalidOperationException($"coroutine {function.Name}: no frame or no `{suspendName.Value}`");
                if (appended.ContainsKey(frameStruct.Id))
                    throw new InvalidOperationException($"coroutine {function.Name}: its frame belongs to another coroutine");
                appended[frameStruct.Id] = structs[frameStruct.Id].Fields.Length;
                functions[f] = Rewrite(function, frame, frameStruct, suspend, functions[suspend], awaits, ref structs[frameStruct.Id]);
            }
            // A frame starts with its new fields zeroed: its initializer (the function that makes its struct) sets them.
            for (int f = 0; f < functions.Length; f++)
            {
                if (functions[f] is { IsDeclaration: false, ReturnType: StructType { Id: var id } } initializer && appended.TryGetValue(id, out int before))
                    functions[f] = ZeroNewFields(initializer, structs[id], before);
            }
            return module with { Structs = structs, Functions = functions };
        }

        private static IrFunction ZeroNewFields(IrFunction initializer, StructDef frame, int before)
        {
            var zeroes = Enumerable.Range(before, frame.Fields.Length - before)
                .Select(f => (Statement)new AssignStatement(new Place(0).Field(f), Rvalue.ZeroFiller(frame.Fields[f]))).ToArray();
            var blocks = initializer.Blocks.Select(b => b.Terminator is ReturnTerminator ? b with { Statements = [.. b.Statements, .. zeroes] } : b).ToArray();
            return initializer with { Blocks = blocks };
        }

        private static IrFunction Rewrite(IrFunction function, int frame, StructType frameStruct, int suspend, IrFunction suspendFunction,
            HashSet<int> awaits, ref StructDef frameDef)
        {
            // The frame's new fields: the state, then one per argument and per local that lives across a pause (but the
            // result and the frame itself); the other locals stay locals.
            int stateField = frameDef.Fields.Length;
            var fieldOf = new Dictionary<int, int>();
            var fields = new List<IrType>(frameDef.Fields) { IrType.I32 };
            var names = new List<string>(frameDef.FieldNames.Length == frameDef.Fields.Length ? frameDef.FieldNames : [.. Enumerable.Range(0, frameDef.Fields.Length).Select(i => $"${i}")]) { "$state" };
            var acrossPauses = LiveAcrossPauses(function, awaits);
            for (int local = 1; local < function.Locals.Length; local++)
            {
                if (local == frame || local > function.ArgumentCount && !acrossPauses.Contains(local))
                    continue;
                fieldOf[local] = fields.Count;
                fields.Add(function.Locals[local].Type);
                names.Add($"${function.Locals[local].Name ?? "l"}{local}");
            }
            frameDef = frameDef with { Fields = [.. fields], FieldNames = [.. names] };

            var locals = function.Locals.ToList();
            int NewLocal(IrType type, string name)
            {
                locals.Add(new LocalDecl(type, name, Mutable: true));
                return locals.Count - 1;
            }
            Place FrameField(int field) => new Place(frame).Deref().Field(field);

            // Places rooted at a moved local are rooted at its field; an index taken from one is first loaded into a local.
            var prelude = new List<Statement>();
            Place MapPlace(Place place)
            {
                var projections = place.Projections.Select(p =>
                {
                    if (p is IndexProjection { Local: var index } && fieldOf.TryGetValue(index, out int indexField))
                    {
                        int loaded = NewLocal(function.Locals[index].Type, $"index{index}");
                        prelude.Add(new AssignStatement(new Place(loaded), Rvalue.Use(Operand.Copy(FrameField(indexField)))));
                        return Projection.Index(loaded);
                    }
                    return p;
                }).ToArray();
                return fieldOf.TryGetValue(place.Local, out int field)
                    ? new Place(frame, [Projection.Deref, Projection.Field(field), .. projections])
                    : new Place(place.Local, projections);
            }
            int Shift(int block) => block + 2; // after the dispatch and the first run's entry
            CatchClause[] Handlers(CatchClause[] handlers) => [.. handlers.Select(h => h with { Block = Shift(h.Block) })];

            var blocks = new List<BasicBlock>();
            var resumes = new List<int>();
            var extra = new List<BasicBlock>();
            int firstExtra = function.Blocks.Length + 2;
            foreach (var block in function.Blocks)
            {
                var statements = new List<Statement>();
                foreach (var statement in block.Statements)
                {
                    if (statement is StorageLiveStatement { Local: var live } && fieldOf.ContainsKey(live)
                        || statement is StorageDeadStatement { Local: var dead } && fieldOf.ContainsKey(dead))
                        continue;
                    prelude.Clear();
                    // An exception is taken into a local (where back ends expect it), then moved to the frame.
                    if (statement is AssignStatement { Value: CaughtRvalue } caught && fieldOf.ContainsKey(caught.Target.Local))
                    {
                        int taken = NewLocal(function.Locals[caught.Target.Local].Type, "caught");
                        statements.Add(new AssignStatement(new Place(taken), caught.Value));
                        var into = MapPlace(caught.Target);
                        statements.AddRange(prelude);
                        statements.Add(new AssignStatement(into, Rvalue.Use(Operand.Move(new Place(taken)))));
                        continue;
                    }
                    var mapped = (Statement)Map(statement, MapPlace);
                    statements.AddRange(prelude);
                    statements.Add(mapped);
                }
                prelude.Clear();
                Terminator terminator;
                if (block.Terminator is CallTerminator { Function: var callee, Arguments: [var awaited], Required: null, Callee: null } call && awaits.Contains(callee))
                {
                    // await: ask the frame to wait for the task; pause when it does, else (and on resuming) call the await.
                    var task = (Operand)Map(awaited, MapPlace);
                    var taskPlace = task switch
                    {
                        MoveOperand move => move.Place,
                        CopyOperand copy => copy.Place,
                        _ => throw new InvalidOperationException($"coroutine {function.Name}: an awaited constant"),
                    };
                    var parameter = suspendFunction.Locals[2].Type.Substitute(frameStruct.Arguments);
                    int upcast = NewLocal(parameter, "awaited");
                    int paused = NewLocal(IrType.Bool, "paused");
                    statements.AddRange(prelude);
                    statements.Add(new AssignStatement(new Place(upcast), Rvalue.HandleCast(Operand.Copy(taskPlace), parameter)));
                    int decide = firstExtra + extra.Count, pause = decide + 1, resume = decide + 2;
                    terminator = new CallTerminator(suspend, [Operand.Copy(new Place(frame)), Operand.Move(new Place(upcast))], new Place(paused), decide)
                    {
                        TypeArguments = frameStruct.Arguments,
                        Handlers = Handlers(call.Handlers),
                    };
                    resumes.Add(resume);
                    extra.Add(new BasicBlock([], new IfTerminator(Operand.Copy(new Place(paused)), pause, resume)));
                    extra.Add(new BasicBlock([new AssignStatement(FrameField(stateField), Rvalue.Use(Operand.Int(resumes.Count, IrType.I32)))], new ReturnTerminator()));
                    prelude.Clear();
                    var destination = MapPlace(call.Destination);
                    var resumed = call with { Arguments = [task], Destination = destination, Target = Shift(call.Target), Handlers = Handlers(call.Handlers) };
                    extra.Add(new BasicBlock([.. prelude], resumed));
                }
                else
                {
                    terminator = (Terminator)Map(block.Terminator, MapPlace);
                    statements.AddRange(prelude);
                    terminator = ShiftTargets(terminator, Shift);
                }
                blocks.Add(new BasicBlock([.. statements], terminator));
            }

            // Block 0: where to go; block 1: the first run stores the arguments in the frame, then starts.
            int state = NewLocal(IrType.I32, "state");
            var dispatch = new BasicBlock([new AssignStatement(new Place(state), Rvalue.Use(Operand.Copy(FrameField(stateField))))],
                new SwitchTerminator(Operand.Copy(new Place(state)), [1, .. resumes], firstExtra + extra.Count));
            var spill = Enumerable.Range(1, function.ArgumentCount).Where(a => a != frame)
                .Select(a => (Statement)new AssignStatement(FrameField(fieldOf[a]), Rvalue.Use(Operand.Move(new Place(a))))).ToArray();
            var start = new BasicBlock(spill, new GotoTerminator(2));
            var unreachable = new BasicBlock([], new UnreachableTerminator());
            return function with
            {
                Locals = [.. locals],
                Blocks = [dispatch, start, .. blocks, .. extra, unreachable],
            };
        }

        /// <summary>
        /// The locals a coroutine needs in its frame: those live across a pause (used by an await call, or live after it,
        /// on its way on or into its handlers), and every local borrowed anywhere (a reference to it may outlive a pause).
        /// Liveness is conservative: every mention of a local is a use, and only an assignment to the whole local ends
        /// it; exceptions reach a call's or a throw's handlers.
        /// </summary>
        private static HashSet<int> LiveAcrossPauses(IrFunction function, HashSet<int> awaits)
        {
            var blocks = function.Blocks;
            int count = blocks.Length;
            var gen = new HashSet<int>[count];
            var kill = new HashSet<int>[count];
            var mentioned = new HashSet<int>();
            var borrowed = new HashSet<int>();
            Place Mention(Place place)
            {
                mentioned.Add(place.Local);
                foreach (var projection in place.Projections)
                {
                    if (projection is IndexProjection index)
                        mentioned.Add(index.Local);
                }
                return place;
            }
            HashSet<int> Mentions(object value)
            {
                mentioned.Clear();
                Map(value, Mention);
                return mentioned;
            }
            for (int b = 0; b < count; b++)
            {
                var used = gen[b] = [];
                var defined = kill[b] = [];
                foreach (var statement in blocks[b].Statements)
                {
                    if (statement is AssignStatement { Value: RefRvalue reference })
                        borrowed.Add(reference.Place.Local);
                    bool whole = statement is AssignStatement { Target.Projections.Length: 0 };
                    foreach (int local in Mentions(whole ? ((AssignStatement)statement).Value : statement))
                    {
                        if (!defined.Contains(local))
                            used.Add(local);
                    }
                    if (whole)
                        defined.Add(((AssignStatement)statement).Target.Local);
                }
                foreach (int local in Mentions(blocks[b].Terminator))
                {
                    if (!defined.Contains(local))
                        used.Add(local);
                }
            }
            static IEnumerable<int> Successors(Terminator terminator)
            {
                IEnumerable<int> targets = terminator switch
                {
                    GotoTerminator g => [g.Target],
                    IfTerminator i => [i.Then, i.Else],
                    SwitchTerminator s => [.. s.Targets, s.Otherwise],
                    CallTerminator c => [c.Target],
                    DropTerminator d => [d.Target],
                    _ => [],
                };
                return terminator is IUnwinding unwinding ? targets.Concat(unwinding.Handlers.Select(h => h.Block)) : targets;
            }
            var successors = blocks.Select(block => Successors(block.Terminator).Where(t => t >= 0 && t < count).ToArray()).ToArray();
            var liveIn = new HashSet<int>[count];
            for (int b = 0; b < count; b++)
                liveIn[b] = [.. gen[b]];
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int b = count - 1; b >= 0; b--)
                {
                    foreach (int next in successors[b])
                    {
                        foreach (int local in liveIn[next])
                        {
                            if (!kill[b].Contains(local) && liveIn[b].Add(local))
                                changed = true;
                        }
                    }
                }
            }
            var result = new HashSet<int>(borrowed);
            for (int b = 0; b < count; b++)
            {
                if (blocks[b].Terminator is not CallTerminator { Required: null, Callee: null } call || !awaits.Contains(call.Function))
                    continue;
                // The await is called again on resuming: what it mentions, and what is live after it, crosses the pause.
                result.UnionWith(Mentions(call));
                foreach (int next in successors[b])
                    result.UnionWith(liveIn[next]);
            }
            return result;
        }

        private static Terminator ShiftTargets(Terminator terminator, Func<int, int> shift)
        {
            CatchClause[] Handlers(CatchClause[] handlers) => [.. handlers.Select(h => h with { Block = shift(h.Block) })];
            return terminator switch
            {
                GotoTerminator g => new GotoTerminator(shift(g.Target)),
                IfTerminator i => i with { Then = shift(i.Then), Else = shift(i.Else) },
                SwitchTerminator s => s with { Targets = [.. s.Targets.Select(shift)], Otherwise = shift(s.Otherwise) },
                CallTerminator c => c with { Target = shift(c.Target), Handlers = Handlers(c.Handlers) },
                ThrowTerminator t => t with { Handlers = Handlers(t.Handlers) },
                ResumeTerminator r => r with { Handlers = Handlers(r.Handlers) },
                DropTerminator d => d with { Target = shift(d.Target) },
                ReturnTerminator or UnreachableTerminator => terminator,
                _ => throw new InvalidOperationException($"unknown terminator {terminator.GetType().Name}"),
            };
        }

        // ── Rewriting every place in a statement, terminator, rvalue or operand ──

        private static readonly MethodInfo Clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly Dictionary<Type, PropertyInfo[]> Properties = [];

        /// <summary>A copy of an IR record with <paramref name="map"/> applied to every <see cref="Place"/> in it.</summary>
        private static object Map(object value, Func<Place, Place> map)
        {
            switch (value)
            {
                case Place place:
                    return map(place);
                case IrType or string or CatchClause or RequirementRef:
                    return value;
                case Array array:
                {
                    var element = array.GetType().GetElementType()!;
                    if (element.IsPrimitive || element == typeof(string) || typeof(IrType).IsAssignableFrom(element) || element == typeof(CatchClause))
                        return value;
                    Array? copy = null;
                    for (int i = 0; i < array.Length; i++)
                    {
                        object? before = array.GetValue(i);
                        object? after = before is null ? null : Map(before, map);
                        if (ReferenceEquals(before, after))
                            continue;
                        copy ??= (Array)array.Clone();
                        copy.SetValue(after, i);
                    }
                    return copy ?? array;
                }
                case Statement or Terminator or Rvalue or Operand:
                {
                    object? copy = null;
                    foreach (var property in PropertiesOf(value.GetType()))
                    {
                        object? before = property.GetValue(value);
                        if (before is null || before.GetType().IsPrimitive || before is Enum)
                            continue;
                        object after = Map(before, map);
                        if (ReferenceEquals(before, after))
                            continue;
                        copy ??= Clone.Invoke(value, null);
                        property.SetValue(copy, after);
                    }
                    return copy ?? value;
                }
                default:
                    return value;
            }
        }

        private static PropertyInfo[] PropertiesOf(Type type)
        {
            lock (Properties)
            {
                if (!Properties.TryGetValue(type, out var found))
                {
                    found = [.. type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)];
                    Properties[type] = found;
                }
                return found;
            }
        }
    }
}

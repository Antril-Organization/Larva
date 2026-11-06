using System.Numerics;
using Larva.IR.Analysis;
using Larva.IR.Text;

namespace Larva.IR.Lowering
{
    /// <summary>
    /// Resolves generics before lowering. Starting from every non-generic function, each reachable call
    /// <c>f::&lt;args&gt;</c> becomes either
    /// <list type="bullet">
    /// <item>a <b>shared</b> instance — the generic body kept as is, compiled once and called with one witness per
    /// type argument (see <see cref="Shareable"/>), or</item>
    /// <item>a <b>specialized</b> copy (<c>max&lt;i32&gt;</c>) with every type parameter substituted.</item>
    /// </list>
    /// Closed generic struct instantiations become their own structs; open ones stay generic inside shared code.
    /// Run after the type checker, so every instantiation already satisfies its constraints.
    /// </summary>
    public static class Monomorphizer
    {
        /// <param name="roots">
        /// Non-generic functions to start from (the linker passes the entry, exports and reflected functions, dropping dead
        /// code); every non-generic function when <c>null</c>.
        /// </param>
        /// <param name="slots">
        /// For code linked into a native library: <c>typeid</c> and <c>classid</c> read globals (slots) the program fills in
        /// at start-up, since only the whole program numbers types and classes; the slots asked for are recorded there.
        /// </param>
        public static IrModule Run(IrModule module, GenericsMode mode = GenericsMode.Shared, IReadOnlySet<int>? roots = null, IdSlots? slots = null)
        {
            // Every back end comes through here: coroutines (async functions) are made resumable first.
            module = Coroutines.Lower(module);
            return new Instantiator(module, mode == GenericsMode.Shared ? Shareable(module) : new bool[module.Functions.Length], roots, slots).Run();
        }

        /// <summary>
        /// For each function, whether it can be compiled once for every instantiation. It cannot when its body needs the
        /// concrete type to pick an instruction (arithmetic, comparison, cast on a generic value — e.g. <c>add</c> vs
        /// <c>fpu.add</c>), or when it passes its own type parameters to a function that cannot be shared.
        /// </summary>
        public static bool[] Shareable(IrModule module)
        {
            var shareable = module.Functions.Select(f => f.IsGeneric && !UsesTypeDependentOps(f, module)).ToArray();
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int f = 0; f < shareable.Length; f++)
                {
                    if (!shareable[f])
                        continue;
                    foreach (var call in module.Functions[f].Blocks.Select(b => b.Terminator).OfType<CallTerminator>())
                    {
                        if (call.Required is null && call.Callee is null && call.TypeArguments.Any(t => t.IsGeneric()) && !shareable[call.Function])
                        {
                            shareable[f] = false;
                            changed = true;
                            break;
                        }
                    }
                }
            }
            return shareable;
        }

        private static bool UsesTypeDependentOps(IrFunction function, IrModule module) =>
            function.Blocks.Any(b => b.Terminator is CallTerminator { Required: not null })
            || function.Blocks.SelectMany(b => b.Statements).OfType<AssignStatement>().Any(a => a.Value is FunctionRefRvalue or HandleCastRvalue or TypeIdRvalue or ImplementationRvalue
                || a.Value is GlobalRvalue { TypeArguments.Length: > 0 })
            || function.Blocks.SelectMany(b => b.Statements).OfType<StoreGlobalStatement>().Any(s => s.TypeArguments.Length > 0)
            || function.Blocks.SelectMany(b => b.Statements).OfType<AssignStatement>().Any(assign => assign.Value switch
            {
                BinaryRvalue binary => function.TypeOf(binary.Left, module).IsGeneric(),
                UnaryRvalue unary => function.TypeOf(unary.Operand, module).IsGeneric(),
                CastRvalue cast => function.TypeOf(cast.Operand, module).IsGeneric() || cast.Type.IsGeneric(),
                _ => false,
            });

        private sealed class Instantiator(IrModule module, bool[] shared, IReadOnlySet<int>? roots, IdSlots? slots)
        {
            private readonly List<StructDef> structs = [.. module.Structs];
            private readonly Dictionary<StructType, int> structInstances = [];
            private readonly Dictionary<int, StructType> structOrigins = [];
            private readonly List<IrFunction?> functions = [];
            private readonly Dictionary<string, int> functionInstances = [];
            private readonly Queue<(int Source, IrType[]? Arguments, int Target)> pending = [];
            // Generic globals stay as placeholders (nothing reads them); each instantiation is a new global.
            private readonly List<GlobalDef> globals = [.. module.Globals.Select(g => g.GenericCount > 0 ? new GlobalDef(g.Name, IrType.U8) : g)];
            private readonly Dictionary<string, int> globalInstances = [];
            private readonly Dictionary<IrType, int> typeIds = [];

            public IrModule Run()
            {
                // Roots: every non-generic function, plus shared generics reflected in full (callable via MakeGeneric at run time).
                for (int f = 0; f < module.Functions.Length; f++)
                {
                    bool reflectedShared = shared[f] && module.Functions[f].Reflect == ReflectionLevel.Full;
                    if (reflectedShared || !module.Functions[f].IsGeneric && (roots is null || roots.Contains(f)))
                        Instantiate(f, []);
                }

                while (pending.TryDequeue(out var work))
                    functions[work.Target] = Specialize(module.Functions[work.Source], work.Arguments) with
                    {
                        InstanceOf = new FunctionInstance(work.Source, work.Arguments ?? []),
                    };

                // `instances f`: once every instance exists, the arrays of their addresses.
                for (int f = 0; f < functions.Count; f++)
                {
                    if (functions[f] is { } function && function.Blocks.Any(b => b.Statements.Any(s => s is AssignStatement { Value: InstancesRvalue })))
                        functions[f] = ExpandInstances(function);
                }

                for (int s = 0; s < module.Structs.Length; s++)
                {
                    if (structs[s].Base is { } baseType && structs[s].Generics.Length == 0)
                        structs[s] = structs[s] with { Base = MapType(baseType) as StructType, Fields = [.. structs[s].Fields.Select(MapType)] };
                }
                return new IrModule([.. structs], [.. functions.Select(f => f!)]) { Name = module.Name, Domains = module.Domains, Data = module.Data, Globals = [.. globals.Select(g => g with { Type = MapType(g.Type) })] };
            }

            /// <summary>The specialized instances of a generic function made so far.</summary>
            private List<int> InstancesOf(int function)
            {
                string prefix = $"{function}<";
                return [.. functionInstances.Where(i => i.Key.StartsWith(prefix, StringComparison.Ordinal) && i.Key != $"{function}<shared>")
                    .OrderBy(i => i.Value).Select(i => i.Value)];
            }

            /// <summary>Each <c>x = instances f</c> becomes a new array filled with the addresses of f's instances.</summary>
            private IrFunction ExpandInstances(IrFunction function)
            {
                var locals = function.Locals.ToList();
                int index = locals.Count;
                locals.Add(new LocalDecl(IrType.U64, "$instance", Mutable: true));
                var shape = new IrModule([.. structs], []);
                var blocks = function.Blocks.Select(block => block with
                {
                    Statements = [.. block.Statements.SelectMany(statement =>
                    {
                        if (statement is not AssignStatement { Value: InstancesRvalue instances } assign)
                            return (IEnumerable<Statement>)[statement];
                        var found = InstancesOf(instances.Function);
                        var element = ((ArrayType)((BoxType)function.TypeOf(assign.Target, shape)).Pointee).Element;
                        var expanded = new List<Statement>
                        {
                            new AssignStatement(assign.Target, Rvalue.NewArray(AllocKind.Box, element, Operand.Int(found.Count, IrType.U64))),
                        };
                        for (int i = 0; i < found.Count; i++)
                        {
                            expanded.Add(new AssignStatement(new Place(index), Rvalue.Use(Operand.Int(i, IrType.U64))));
                            expanded.Add(new AssignStatement(assign.Target.Deref().Index(index), new FunctionRefRvalue(found[i], [])));
                        }
                        return expanded;
                    })],
                }).ToArray();
                return function with { Locals = [.. locals], Blocks = blocks };
            }

            /// <summary>Index of the instance that serves <c>function::&lt;arguments&gt;</c>, creating it on first use.</summary>
            /// <param name="specialize">Always a specialized copy (an address taken with <c>fn</c> is called without witnesses).</param>
            private int Instantiate(int function, IrType[] arguments, bool specialize = false)
            {
                bool isShared = shared[function] && !specialize;
                string key = isShared ? $"{function}<shared>" : $"{function}<{string.Join(",", arguments.Select(a => a.ToString()))}>";
                if (functionInstances.TryGetValue(key, out int index))
                    return index;

                index = functions.Count;
                functions.Add(null);
                functionInstances.Add(key, index);
                pending.Enqueue((function, isShared ? null : arguments, index));
                return index;
            }

            /// <param name="arguments">Type arguments to substitute, or <c>null</c> to build the shared instance.</param>
            private IrFunction Specialize(IrFunction function, IrType[]? arguments)
            {
                IrType Map(IrType type) => MapType(arguments is null ? type : type.Substitute(arguments));

                var blocks = function.Blocks.Select(block => block.Terminator is CallTerminator { Required: not null } required
                    ? Resolve(function, block, required, arguments!, Map)
                    : new BasicBlock(
                    [.. block.Statements.Select(s => MapStatement(s, Map))],
                    block.Terminator switch
                    {
                        CallTerminator { Callee: not null } indirect => indirect with { Handlers = MapHandlers(indirect.Handlers, Map) },
                        CallTerminator call => MapCall(call, Map) with { Handlers = MapHandlers(call.Handlers, Map) },
                        ThrowTerminator @throw => @throw with { Handlers = MapHandlers(@throw.Handlers, Map) },
                        ResumeTerminator resume => resume with { Handlers = MapHandlers(resume.Handlers, Map) },
                        var other => other,
                    })).ToArray();
                // A requirement met by an operation leaves its block split by a jump: joined again, the instance is the
                // code a function written for the type would be (one block per straight run), which every back end needs
                // (x64 and the VM do not join blocks themselves).
                if (function.Blocks.Any(b => b.Terminator is CallTerminator { Required: not null }))
                {
                    // Again while folding finds more constants (each fold makes the next operation's operands known).
                    for (int pass = 0; pass < 8; pass++)
                    {
                        blocks = PropagateConstants(blocks, function.ArgumentCount, out bool changed);
                        if (!changed)
                            break;
                    }
                    blocks = ForwardCopies(JoinJumps(RemoveUnreachable(blocks)), function.ArgumentCount);
                }

                string name = arguments is null or { Length: 0 }
                    ? function.Name
                    : $"{function.Name}<{string.Join(", ", arguments.Select(a => IrPrinter.FormatType(Unmap(a), module)))}>";

                return new IrFunction(name, function.ArgumentCount, [.. function.Locals.Select(l => l with { Type = Map(l.Type) })], blocks)
                {
                    Unsafe = function.Unsafe,
                    Domain = function.Domain,
                    Generics = arguments is null ? function.Generics : [],
                    RegionParams = function.RegionParams,
                    Reflect = function.Reflect,
                    // A function defined in another native image stays a declaration of that symbol.
                    Linkage = function.Linkage,
                    Attributes = function.Attributes,
                    Mangled = function.Mangled,
                    CallerRegion = function.CallerRegion,
                    Symbol = function.IsDeclaration ? function.Symbol : null,
                };
            }

            /// <summary>
            /// A block ending in a call through a requirement, for one instantiation: the call becomes the built-in operation
            /// (an assignment, then a jump) or a call to the implementing function.
            /// </summary>
            private BasicBlock Resolve(IrFunction function, BasicBlock block, CallTerminator call, IrType[] arguments, Func<IrType, IrType> map)
            {
                var statements = block.Statements.Select(s => MapStatement(s, map)).ToList();
                // Type arguments are already in instance numbering (Pair<i32> = struct#N): back to source types to resolve.
                var requirement = function.Generics[call.Required!.Param].Requirements[call.Required.Index].Substitute(arguments).Map(Unmap);
                var resolution = Requirements.Resolve(requirement, new TypeContext(module, []), out string? problem)
                    ?? throw new InvalidOperationException($"{function.Name}: `{IrPrinter.FormatRequirement(requirement, module)}` {problem ?? "is not provided"}");
                switch (resolution)
                {
                    case BuiltinBinary binary:
                        statements.Add(new AssignStatement(call.Destination, Rvalue.Binary(binary.Op, call.Arguments[0], call.Arguments[1])));
                        return new BasicBlock([.. statements], new GotoTerminator(call.Target));
                    case BuiltinUnary unary:
                        statements.Add(new AssignStatement(call.Destination, Rvalue.Unary(unary.Op, call.Arguments[0])));
                        return new BasicBlock([.. statements], new GotoTerminator(call.Target));
                    case BuiltinCast cast:
                        statements.Add(new AssignStatement(call.Destination, Rvalue.Cast(call.Arguments[0], map(cast.Target))));
                        return new BasicBlock([.. statements], new GotoTerminator(call.Target));
                    case FunctionResolution { TypeArguments.Length: 0 } constantGetter when ConstantOf(module, constantGetter.Function) is { } constant:
                        // `T.Zero` on a number whose Zero is `get => 0`: the constant itself, no call (every back end).
                        statements.Add(new AssignStatement(call.Destination, new UseRvalue(constant)));
                        return new BasicBlock([.. statements], new GotoTerminator(call.Target));
                    case FunctionResolution { TypeArguments.Length: 0 } converter when call.Arguments.Length == 1 && ConversionOf(module, converter.Function) is { } conversion:
                        // `T.ToF64(x)` on a number whose ToF64 is `=> (f64)value` (or `=> value`): the cast itself, no call.
                        statements.Add(new AssignStatement(call.Destination, conversion.Type is { } target
                            ? Rvalue.Cast(call.Arguments[0], map(target))
                            : new UseRvalue(call.Arguments[0])));
                        return new BasicBlock([.. statements], new GotoTerminator(call.Target));
                    default:
                        var implementation = (FunctionResolution)resolution;
                        var direct = new CallTerminator(implementation.Function, call.Arguments, call.Destination, call.Target)
                        {
                            TypeArguments = implementation.TypeArguments,
                            Handlers = call.Handlers,
                        };
                        return new BasicBlock([.. statements], MapCall(direct, map) with { Handlers = MapHandlers(call.Handlers, map) });
                }
            }

            /// <summary>
            /// A local given a constant once and never otherwise (a requirement met by a constant: `T.Zero`) is read as
            /// that constant wherever an operation, a cast or a condition reads it: `x < 0`, not `t = 0; x < t`. When
            /// nothing reads the local any more, its assignment goes too.
            /// </summary>
            internal static BasicBlock[] PropagateConstants(BasicBlock[] blocks, int arguments) => PropagateConstants(blocks, arguments, out _);

            /// <param name="changed">Whether a constant was read in (and so an operation or a branch maybe worked out), or a store dropped.</param>
            internal static BasicBlock[] PropagateConstants(BasicBlock[] blocks, int arguments, out bool changed)
            {
                changed = false;
                bool substituted = false;
                var assigned = new Dictionary<int, int>();
                var constants = new Dictionary<int, ConstOperand>();
                void Count(Place target)
                {
                    if (target.IsLocal)
                        assigned[target.Local] = assigned.GetValueOrDefault(target.Local) + 1;
                }
                foreach (var block in blocks)
                {
                    foreach (var statement in block.Statements)
                    {
                        if (statement is AssignStatement assign)
                        {
                            Count(assign.Target);
                            if (assign is { Target.IsLocal: true, Value: UseRvalue { Operand: ConstOperand constant } })
                                constants[assign.Target.Local] = constant;
                        }
                    }
                    if (block.Terminator is CallTerminator call)
                        Count(call.Destination);
                }
                foreach (int local in constants.Keys.ToList())
                {
                    // Only temporaries given that one constant: not the result, not parameters, nothing whose address is taken.
                    if (local <= arguments || assigned.GetValueOrDefault(local) != 1)
                        constants.Remove(local);
                }
                foreach (var block in blocks)
                {
                    foreach (var statement in block.Statements)
                    {
                        if (statement is AssignStatement { Value: RefRvalue { Place: var place } } && constants.ContainsKey(place.Local))
                            constants.Remove(place.Local);
                    }
                }
                if (constants.Count == 0)
                    return blocks;
                Operand Read(Operand operand)
                {
                    if (operand is CopyOperand { Place: { IsLocal: true } place } && constants.TryGetValue(place.Local, out var constant))
                    {
                        substituted = true;
                        return constant;
                    }
                    return operand;
                }
                Rvalue Value(Rvalue value) => value switch
                {
                    // Worked out when both sides are known: `T.One / (T.One + T.One) == T.Zero` is a constant per type.
                    BinaryRvalue binary when Read(binary.Left) is ConstOperand left && Read(binary.Right) is ConstOperand right && Fold(binary.Op, left, right) is { } folded
                        => new UseRvalue(folded),
                    BinaryRvalue binary => binary with { Left = Read(binary.Left), Right = Read(binary.Right) },
                    UnaryRvalue unary => unary with { Operand = Read(unary.Operand) },
                    CastRvalue cast => cast with { Operand = Read(cast.Operand) },
                    UseRvalue use => use with { Operand = Read(use.Operand) },
                    _ => value,
                };
                var rewritten = blocks.Select(block => new BasicBlock(
                    [.. block.Statements.Select(s => s is AssignStatement assign ? assign with { Value = Value(assign.Value) } : s)],
                    block.Terminator switch
                    {
                        // A known condition: only the branch it takes.
                        IfTerminator branch when Read(branch.Condition) is ConstOperand known => new GotoTerminator(known.Bits.IsZero ? branch.Else : branch.Then),
                        IfTerminator branch => branch with { Condition = Read(branch.Condition) },
                        _ => block.Terminator,
                    })).ToArray();
                // The locals still read somewhere (anything not known to read only operands counts as reading them all).
                var read = new HashSet<int>();
                bool unknown = false;
                void Use(Operand? operand)
                {
                    if (operand is CopyOperand { Place: var copied })
                        read.Add(copied.Local);
                    else if (operand is MoveOperand { Place: var moved })
                        read.Add(moved.Local);
                }
                foreach (var block in rewritten)
                {
                    foreach (var statement in block.Statements)
                    {
                        switch (statement)
                        {
                            case AssignStatement assign:
                                if (!assign.Target.IsLocal)
                                    read.Add(assign.Target.Local);
                                switch (assign.Value)
                                {
                                    case UseRvalue use: Use(use.Operand); break;
                                    case BinaryRvalue binary: Use(binary.Left); Use(binary.Right); break;
                                    case UnaryRvalue unary: Use(unary.Operand); break;
                                    case CastRvalue cast: Use(cast.Operand); break;
                                    case ZeroRvalue or SizeOfRvalue or GlobalRvalue or TypeIdRvalue or StaticRvalue: break;
                                    default: unknown = true; break;
                                }
                                break;
                            case StorageLiveStatement or StorageDeadStatement or NopStatement:
                                break;
                            case StoreGlobalStatement store:
                                Use(store.Value);
                                break;
                            default:
                                unknown = true;
                                break;
                        }
                    }
                    switch (block.Terminator)
                    {
                        case IfTerminator branch: Use(branch.Condition); break;
                        case SwitchTerminator choice: Use(choice.Value); break;
                        case CallTerminator call:
                            foreach (var argument in call.Arguments)
                                Use(argument);
                            Use(call.Callee);
                            if (!call.Destination.IsLocal)
                                read.Add(call.Destination.Local);
                            break;
                        case ThrowTerminator raise: Use(raise.Value); break;
                        case GotoTerminator or ReturnTerminator or UnreachableTerminator: break;
                        default: unknown = true; break;
                    }
                }
                changed = substituted;
                if (unknown)
                    return rewritten;
                bool dropped = false;
                var result = rewritten.Select(block =>
                {
                    var kept = block.Statements.Where(s => s is not AssignStatement { Target.IsLocal: true } assign || !constants.ContainsKey(assign.Target.Local) || read.Contains(assign.Target.Local)).ToArray();
                    dropped |= kept.Length != block.Statements.Length;
                    return block with { Statements = kept };
                }).ToArray();
                changed |= dropped;
                return result;
            }

            /// <summary>
            /// Blocks ending in a jump to a block nothing else reaches are joined with it, and the blocks renumbered (the
            /// entry stays first): the same flow with fewer edges.
            /// </summary>
            internal static BasicBlock[] JoinJumps(BasicBlock[] blocks)
            {
                var predecessors = new int[blocks.Length];
                foreach (var block in blocks)
                {
                    foreach (int successor in block.Terminator.Successors)
                        predecessors[successor]++;
                }
                var joined = blocks.ToArray();
                var gone = new bool[blocks.Length];
                bool changed = false;
                for (int b = 0; b < joined.Length; b++)
                {
                    if (gone[b])
                        continue;
                    while (joined[b].Terminator is GotoTerminator { Target: var target } && target != b && target != 0 && predecessors[target] == 1 && !gone[target])
                    {
                        joined[b] = new BasicBlock([.. joined[b].Statements, .. joined[target].Statements], joined[target].Terminator);
                        gone[target] = true;
                        changed = true;
                    }
                }
                return changed ? Renumber(joined, gone) : blocks;
            }

            /// <summary>
            /// The copies a requirement met by a conversion leaves are taken out: an argument copied to a temporary that is
            /// only read (`t = copy _1; f(copy t)` is `f(copy _1)`), and a call's result copied on at once (`t = f() -> bb;
            /// bb: x = copy t` is `x = f() -> bb`). Only whole locals no reference points at; anything not understood leaves
            /// the function as it is.
            /// </summary>
            internal static BasicBlock[] ForwardCopies(BasicBlock[] blocks, int arguments)
            {
                var assigned = new Dictionary<int, int>();
                var copies = new Dictionary<int, int>(); // reads as `copy local`
                var otherReads = new HashSet<int>();      // moved, read through a projection, or borrowed
                var predecessors = new int[blocks.Length];
                void Assign(Place target)
                {
                    if (target.IsLocal)
                        assigned[target.Local] = assigned.GetValueOrDefault(target.Local) + 1;
                    else
                        Other(target);
                }
                void Other(Place place)
                {
                    otherReads.Add(place.Local);
                    foreach (int index in place.IndexLocals())
                        otherReads.Add(index);
                }
                void Read(Operand? operand)
                {
                    if (operand is CopyOperand { Place: { IsLocal: true } whole })
                        copies[whole.Local] = copies.GetValueOrDefault(whole.Local) + 1;
                    else if (operand is CopyOperand { Place: var part })
                        Other(part);
                    else if (operand is MoveOperand { Place: var moved })
                        Other(moved);
                }
                foreach (var block in blocks)
                {
                    foreach (var statement in block.Statements)
                    {
                        switch (statement)
                        {
                            case AssignStatement assign:
                                Assign(assign.Target);
                                switch (assign.Value)
                                {
                                    case UseRvalue use: Read(use.Operand); break;
                                    case BinaryRvalue binary: Read(binary.Left); Read(binary.Right); break;
                                    case UnaryRvalue unary: Read(unary.Operand); break;
                                    case CastRvalue cast: Read(cast.Operand); break;
                                    case ZeroRvalue or SizeOfRvalue or GlobalRvalue or TypeIdRvalue or StaticRvalue: break;
                                    default: return blocks;
                                }
                                break;
                            case StorageLiveStatement or StorageDeadStatement or NopStatement:
                                break;
                            case StoreGlobalStatement store:
                                Read(store.Value);
                                break;
                            default:
                                return blocks;
                        }
                    }
                    switch (block.Terminator)
                    {
                        case IfTerminator branch: Read(branch.Condition); break;
                        case SwitchTerminator choice: Read(choice.Value); break;
                        case CallTerminator call:
                            if (call.Callee is not null)
                                return blocks;
                            foreach (var argument in call.Arguments)
                                Read(argument);
                            Assign(call.Destination);
                            foreach (var handler in call.Handlers)
                                predecessors[handler.Block]++;
                            break;
                        case ReturnTerminator: copies[0] = copies.GetValueOrDefault(0) + 1; break;
                        case GotoTerminator or UnreachableTerminator: break;
                        default: return blocks;
                    }
                    foreach (int successor in block.Terminator.Successors)
                        predecessors[successor]++;
                }
                bool Plain(int local) => local > arguments && !otherReads.Contains(local);

                // An argument never assigned, copied once into a temporary assigned nowhere else: read the argument.
                var replace = new Dictionary<int, int>();
                foreach (var block in blocks)
                {
                    foreach (var statement in block.Statements)
                    {
                        if (statement is AssignStatement { Target: { IsLocal: true } target, Value: UseRvalue { Operand: CopyOperand { Place: { IsLocal: true } source } } }
                            && Plain(target.Local) && source.Local is > 0 && source.Local <= arguments && !otherReads.Contains(source.Local)
                            && assigned.GetValueOrDefault(source.Local) == 0 && assigned.GetValueOrDefault(target.Local) == 1)
                            replace[target.Local] = source.Local;
                    }
                }
                // A call's result copied on as the first thing its only successor does: the call writes it there.
                var retarget = new Dictionary<int, Place>(); // block of the call -> new destination
                for (int b = 0; b < blocks.Length; b++)
                {
                    if (blocks[b].Terminator is CallTerminator { Destination: { IsLocal: true } result } call && Plain(result.Local)
                        && assigned.GetValueOrDefault(result.Local) == 1 && copies.GetValueOrDefault(result.Local) == 1
                        && call.Target != b && predecessors[call.Target] == 1
                        && blocks[call.Target].Statements is [AssignStatement { Target: { IsLocal: true } next, Value: UseRvalue { Operand: CopyOperand { Place: { IsLocal: true } copied } } }, ..]
                        && copied.Local == result.Local && !otherReads.Contains(next.Local) && !call.Arguments.Any(a => a is CopyOperand { Place.Local: var l } && l == next.Local))
                        retarget[b] = next;
                }
                if (replace.Count == 0 && retarget.Count == 0)
                    return blocks;
                Operand Forward(Operand operand) =>
                    operand is CopyOperand { Place: { IsLocal: true } place } && replace.TryGetValue(place.Local, out int source) ? Operand.Copy(new Place(source)) : operand;
                Rvalue Value(Rvalue value) => value switch
                {
                    UseRvalue use => use with { Operand = Forward(use.Operand) },
                    BinaryRvalue binary => binary with { Left = Forward(binary.Left), Right = Forward(binary.Right) },
                    UnaryRvalue unary => unary with { Operand = Forward(unary.Operand) },
                    CastRvalue cast => cast with { Operand = Forward(cast.Operand) },
                    _ => value,
                };
                var droppedFirst = new HashSet<int>(retarget.Keys.Select(b => ((CallTerminator)blocks[b].Terminator).Target));
                return [.. blocks.Select((block, b) => new BasicBlock(
                    [.. block.Statements
                        .Where((statement, i) => !(i == 0 && droppedFirst.Contains(b)))
                        .Where(statement => statement is not AssignStatement { Target: { IsLocal: true } target } || !replace.ContainsKey(target.Local))
                        .Select(statement => statement switch
                        {
                            AssignStatement assign => assign with { Value = Value(assign.Value) },
                            StoreGlobalStatement store => store with { Value = Forward(store.Value) },
                            _ => statement,
                        })],
                    block.Terminator switch
                    {
                        IfTerminator branch => branch with { Condition = Forward(branch.Condition) },
                        SwitchTerminator choice => choice with { Value = Forward(choice.Value) },
                        CallTerminator call => call with
                        {
                            Arguments = [.. call.Arguments.Select(Forward)],
                            Destination = retarget.TryGetValue(b, out var destination) ? destination : call.Destination,
                        },
                        var other => other,
                    }))];
            }

            /// <summary>
            /// The blocks the entry cannot reach (through jumps, branches, calls or their handlers) dropped: a branch
            /// worked out at compile time leaves the other side behind.
            /// </summary>
            internal static BasicBlock[] RemoveUnreachable(BasicBlock[] blocks)
            {
                var reached = new bool[blocks.Length];
                var work = new Stack<int>([0]);
                reached[0] = true;
                while (work.Count > 0)
                {
                    var terminator = blocks[work.Pop()].Terminator;
                    var handlers = terminator switch
                    {
                        CallTerminator call => call.Handlers,
                        ThrowTerminator raise => raise.Handlers,
                        ResumeTerminator resume => resume.Handlers,
                        _ => [],
                    };
                    foreach (int next in terminator.Successors.Concat(handlers.Select(h => h.Block)))
                    {
                        if (!reached[next])
                        {
                            reached[next] = true;
                            work.Push(next);
                        }
                    }
                }
                return reached.All(r => r) ? blocks : Renumber(blocks, [.. reached.Select(r => !r)]);
            }

            /// <summary>The blocks without the gone ones, and every block number pointing at the new places.</summary>
            private static BasicBlock[] Renumber(BasicBlock[] joined, bool[] gone)
            {
                var index = new int[joined.Length];
                var kept = new List<BasicBlock>();
                for (int b = 0; b < joined.Length; b++)
                {
                    index[b] = gone[b] ? -1 : kept.Count;
                    if (!gone[b])
                        kept.Add(joined[b]);
                }
                CatchClause[] Handlers(CatchClause[] handlers) => [.. handlers.Select(h => h with { Block = index[h.Block] })];
                return [.. kept.Select(block => block with
                {
                    Terminator = block.Terminator switch
                    {
                        GotoTerminator jump => jump with { Target = index[jump.Target] },
                        IfTerminator branch => branch with { Then = index[branch.Then], Else = index[branch.Else] },
                        SwitchTerminator choice => choice with { Targets = [.. choice.Targets.Select(t => index[t])], Otherwise = index[choice.Otherwise] },
                        CallTerminator call => call with { Target = index[call.Target], Handlers = Handlers(call.Handlers) },
                        ThrowTerminator raise => raise with { Handlers = Handlers(raise.Handlers) },
                        ResumeTerminator resume => resume with { Handlers = Handlers(resume.Handlers) },
                        DropTerminator drop => drop with { Target = index[drop.Target] },
                        var other => other,
                    },
                })];
            }

            /// <summary>
            /// The constant a function gives when that is all it does (no arguments; one assignment of a constant to its
            /// result, or a call without arguments of another such function into it; then return, through gotos): a
            /// primitive's `Zero`, `One`, `MinValue`, and the adapters that implement an interface's static with them.
            /// </summary>
            private static ConstOperand? ConstantOf(IrModule module, int index, int depth = 0)
            {
                var function = module.Functions[index];
                if (depth > 4 || function.ArgumentCount != 0 || function.IsGeneric || function.IsDeclaration)
                    return null;
                ConstOperand? found = null;
                int block = 0;
                for (int steps = 0; steps < 6; steps++)
                {
                    foreach (var statement in function.Blocks[block].Statements)
                    {
                        if (found is not null || statement is not AssignStatement { Target: { Local: 0, IsLocal: true }, Value: UseRvalue { Operand: ConstOperand constant } })
                            return null;
                        found = constant;
                    }
                    switch (function.Blocks[block].Terminator)
                    {
                        case ReturnTerminator:
                            return found;
                        case GotoTerminator jump:
                            block = jump.Target;
                            continue;
                        case CallTerminator { Arguments.Length: 0, Required: null, Callee: null, TypeArguments.Length: 0, Handlers.Length: 0, Destination: { Local: 0, IsLocal: true } } call
                            when found is null && call.Function >= 0:
                            found = ConstantOf(module, call.Function, depth + 1);
                            if (found is null)
                                return null;
                            block = call.Target;
                            continue;
                        default:
                            return null;
                    }
                }
                return null;
            }

            /// <summary>What a one-argument function does when all it does is give back its argument: as it is (Type null)
            /// or cast to Type. A primitive's `FromF64`/`ToF64`, and the adapters that implement an interface's static with
            /// them. Null for anything else.</summary>
            private sealed record Conversion(IrType? Type);

            private static Conversion? ConversionOf(IrModule module, int index, int depth = 0)
            {
                var function = module.Functions[index];
                if (depth > 4 || function.ArgumentCount != 1 || function.IsGeneric || function.IsDeclaration)
                    return null;
                // Each local holding the argument: as it is (null) or cast (the type).
                var holds = new Dictionary<int, IrType?> { [1] = null };
                bool Known(Operand operand, out int local)
                {
                    local = operand switch { CopyOperand { Place: { IsLocal: true } p } => p.Local, MoveOperand { Place: { IsLocal: true } p } => p.Local, _ => -1 };
                    return local >= 0 && holds.ContainsKey(local);
                }
                int block = 0;
                for (int steps = 0; steps < 8; steps++)
                {
                    foreach (var statement in function.Blocks[block].Statements)
                    {
                        switch (statement)
                        {
                            case StorageLiveStatement or StorageDeadStatement or NopStatement:
                                break;
                            case AssignStatement { Target.IsLocal: true, Value: UseRvalue { Operand: var operand } } assign when Known(operand, out int from):
                                holds[assign.Target.Local] = holds[from];
                                break;
                            case AssignStatement { Target.IsLocal: true, Value: CastRvalue { Operand: var operand, Type: var type } } assign
                                when Known(operand, out int from) && holds[from] is null:
                                holds[assign.Target.Local] = type;
                                break;
                            default:
                                return null;
                        }
                    }
                    switch (function.Blocks[block].Terminator)
                    {
                        case ReturnTerminator:
                            return holds.TryGetValue(0, out var result) ? new Conversion(result) : null;
                        case GotoTerminator jump:
                            block = jump.Target;
                            continue;
                        case CallTerminator { Arguments.Length: 1, Required: null, Callee: null, TypeArguments.Length: 0, Handlers.Length: 0, Destination.IsLocal: true } call
                            when call.Function >= 0 && Known(call.Arguments[0], out int from) && holds[from] is null
                                && ConversionOf(module, call.Function, depth + 1) is { } inner:
                            holds[call.Destination.Local] = inner.Type;
                            block = call.Target;
                            continue;
                        default:
                            return null;
                    }
                }
                return null;
            }

            /// <summary>An operation on two constants of a primitive integer, bool or f32/f64 type, worked out; or null.</summary>
            internal static ConstOperand? Fold(BinaryOp op, ConstOperand left, ConstOperand right)
            {
                if (left.Type != right.Type)
                    return null;
                var type = left.Type;
                bool compare = op is BinaryOp.Eq or BinaryOp.Ne or BinaryOp.Lt or BinaryOp.Le or BinaryOp.Gt or BinaryOp.Ge;
                ConstOperand Truth(bool value) => new(value ? 1 : 0, IrType.Bool);
                if (type == IrType.F64 || type == IrType.F32)
                {
                    bool wide = type == IrType.F64;
                    double a = wide ? BitConverter.UInt64BitsToDouble((ulong)left.Bits) : BitConverter.UInt32BitsToSingle((uint)left.Bits);
                    double b = wide ? BitConverter.UInt64BitsToDouble((ulong)right.Bits) : BitConverter.UInt32BitsToSingle((uint)right.Bits);
                    if (compare)
                        return Truth(op switch { BinaryOp.Eq => a == b, BinaryOp.Ne => a != b, BinaryOp.Lt => a < b, BinaryOp.Le => a <= b, BinaryOp.Gt => a > b, _ => a >= b });
                    double? value = op switch { BinaryOp.Add => a + b, BinaryOp.Sub => a - b, BinaryOp.Mul => a * b, BinaryOp.Div => a / b, _ => null };
                    if (value is not { } v)
                        return null;
                    return wide ? new ConstOperand(BitConverter.DoubleToUInt64Bits(v), type) : new ConstOperand(BitConverter.SingleToUInt32Bits((float)v), type);
                }
                if (type.BitWidth is not { } width || type.IsFloat)
                    return null;
                BigInteger x = left.Bits, y = right.Bits;
                if (compare)
                    return Truth(op switch { BinaryOp.Eq => x == y, BinaryOp.Ne => x != y, BinaryOp.Lt => x < y, BinaryOp.Le => x <= y, BinaryOp.Gt => x > y, _ => x >= y });
                if (type == IrType.Bool)
                    return null;
                BigInteger? exact = op switch
                {
                    BinaryOp.Add => x + y,
                    BinaryOp.Sub => x - y,
                    BinaryOp.Mul => x * y,
                    BinaryOp.Div when !y.IsZero => BigInteger.Divide(x, y),
                    BinaryOp.Rem when !y.IsZero => BigInteger.Remainder(x, y),
                    _ => null,
                };
                if (exact is not { } r)
                    return null;
                // Wrapped to the type, as the operation would at run time.
                var modulus = BigInteger.One << width;
                r = ((r % modulus) + modulus) % modulus;
                if (type.IsSigned && r >= modulus >> 1)
                    r -= modulus;
                return new ConstOperand(r, type);
            }

            /// <summary>Shared callees keep their type arguments (lowered to witnesses); specialized ones drop them.</summary>
            private CallTerminator MapCall(CallTerminator call, Func<IrType, IrType> map)
            {
                IrType[] typeArguments = [.. call.TypeArguments.Select(map)];
                return call with
                {
                    Function = Instantiate(call.Function, typeArguments),
                    TypeArguments = shared[call.Function] ? typeArguments : [],
                };
            }

            private static CatchClause[] MapHandlers(CatchClause[] handlers, Func<IrType, IrType> map) =>
                [.. handlers.Select(h => h with { Type = h.Type is null ? null : map(h.Type) })];

            private Statement MapStatement(Statement statement, Func<IrType, IrType> map) => statement switch
            {
                AssignStatement assign => assign with { Value = MapRvalue(assign.Value, map) },
                StoreGlobalStatement { TypeArguments.Length: > 0 } store => store with { Global = InstantiateGlobal(store.Global, [.. store.TypeArguments.Select(map)]), TypeArguments = [] },
                _ => statement,
            };

            /// <summary>The global of one instantiation of a generic global, created on first use.</summary>
            private int InstantiateGlobal(int global, IrType[] arguments)
            {
                string key = $"{global}<{string.Join(",", arguments.Select(a => a.ToString()))}>";
                if (globalInstances.TryGetValue(key, out int index))
                    return index;
                var def = module.Globals[global];
                index = globals.Count;
                globals.Add(new GlobalDef($"{def.Name}<{string.Join(", ", arguments.Select(a => IrPrinter.FormatType(Unmap(a), module)))}>",
                    def.Type.Substitute([.. arguments.Select(Unmap)]), def.Initial));
                globalInstances.Add(key, index);
                return index;
            }

            private Dictionary<int, (int Id, int End)>? classIds;

            /// <summary>
            /// Class ids: a preorder over the program's classes (mutable structs) by their bases, from 1, so a class's
            /// subtree is one range. Per generic definition (its instances share it; <c>typeid</c> tells them apart).
            /// </summary>
            private int ClassIdOf(int structId, bool end)
            {
                if (classIds is null)
                {
                    classIds = [];
                    var classes = Enumerable.Range(0, module.Structs.Length).Where(s => module.Structs[s].Mutable).ToList();
                    var children = classes.ToLookup(s => module.Structs[s].Base?.Id ?? -1);
                    int next = 1;
                    void Visit(int s)
                    {
                        int id = next++;
                        foreach (int child in children[s])
                            Visit(child);
                        classIds[s] = (id, next);
                    }
                    foreach (int rootClass in children[-1])
                        Visit(rootClass);
                }
                return classIds.TryGetValue(structId, out var range) ? end ? range.End : range.Id : 0;
            }

            /// <summary>The global that will hold an id this native library cannot know (see <see cref="IdSlots"/>).</summary>
            private int Slot(IdKind kind, IrType type)
            {
                if (slots!.Globals.TryGetValue((kind, type), out int global))
                    return global;
                global = globals.Count;
                globals.Add(new GlobalDef($"$id.{slots.Entries.Count}", IrType.I32));
                slots.Globals.Add((kind, type), global);
                slots.Entries.Add((kind, type, global));
                return global;
            }

            /// <summary>The id of a concrete type: the order in which this program first asks for it, from 1.</summary>
            private int TypeIdOf(IrType type)
            {
                if (!typeIds.TryGetValue(type, out int id))
                    typeIds.Add(type, id = typeIds.Count + 1);
                return id;
            }

            private Rvalue MapRvalue(Rvalue rvalue, Func<IrType, IrType> map) => rvalue switch
            {
                GlobalRvalue { TypeArguments.Length: > 0 } global => new GlobalRvalue(InstantiateGlobal(global.Global, [.. global.TypeArguments.Select(map)])),
                TypeIdRvalue typeId when slots is not null => Rvalue.ReadGlobal(Slot(IdKind.Type, Unmap(map(typeId.Type)))),
                ClassIdRvalue classId when slots is not null => Rvalue.ReadGlobal(Slot(classId.End ? IdKind.ClassEnd : IdKind.Class,
                    new StructType(((StructType)Unmap(map(classId.Class))).Id))),
                TypeIdRvalue typeId => Rvalue.Use(Operand.Int(TypeIdOf(map(typeId.Type)), IrType.I32)),
                ImplementationRvalue implementation => ResolveImplementation(implementation.Requirement.Map(t => Unmap(map(t)))),
                ClassIdRvalue classId => Rvalue.Use(Operand.Int(ClassIdOf(((StructType)Unmap(map(classId.Class))).Id, classId.End), IrType.I32)),
                FunctionRefRvalue reference => new FunctionRefRvalue(Instantiate(reference.Function, [.. reference.TypeArguments.Select(map)], specialize: true), []),
                HandleCastRvalue cast => cast with { Target = map(cast.Target) },
                CastRvalue cast => cast with { Type = map(cast.Type) },
                SizeOfRvalue size => size with { Type = map(size.Type) },
                ZeroRvalue zero => zero with { Type = map(zero.Type) },
                ArrayRvalue array => array with { Element = map(array.Element), Owner = array.Owner is { } owner ? (StructType)map(owner) : null },
                _ => rvalue,
            };

            /// <summary>
            /// <c>impl</c> for concrete types: the address of the (specialized) function that meets the requirement.
            /// </summary>
            private Rvalue ResolveImplementation(Requirement requirement)
            {
                var resolution = Requirements.Resolve(requirement, new TypeContext(module, []), out string? problem) as FunctionResolution
                    ?? throw new InvalidOperationException($"`impl {IrPrinter.FormatRequirement(requirement, module)}` {problem ?? "is not provided"}");
                return new FunctionRefRvalue(Instantiate(resolution.Function, [.. resolution.TypeArguments.Select(MapType)], specialize: true), []);
            }

            /// <summary>The source-module type of an instance type (inverse of <see cref="MapType"/>).</summary>
            private IrType Unmap(IrType type) => type switch
            {
                StructType st when structOrigins.TryGetValue(st.Id, out var origin) => origin with { Arguments = [.. origin.Arguments.Select(Unmap)] },
                StructType st => new StructType(st.Id, [.. st.Arguments.Select(Unmap)]),
                ReferenceType r => r with { Pointee = Unmap(r.Pointee) },
                GcType g => new GcType(Unmap(g.Pointee)),
                BoxType b => new BoxType(Unmap(b.Pointee)),
                PointerType p => p with { Pointee = Unmap(p.Pointee) },
                ArrayType a => new ArrayType(Unmap(a.Element)),
                FixedArrayType f => f with { Element = Unmap(f.Element), SizeOf = f.SizeOf is null ? null : Unmap(f.SizeOf) },
                FunctionType f => new FunctionType([.. f.Parameters.Select(Unmap)], Unmap(f.Result)),
                OptionalType o => new OptionalType(Unmap(o.Inner)),
                _ => type,
            };

            /// <summary>Replaces closed generic struct instantiations with concrete struct ids; open ones stay generic.</summary>
            private IrType MapType(IrType type) => type switch
            {
                StructType { Arguments.Length: > 0 } st when st.IsGeneric() => new StructType(st.Id, [.. st.Arguments.Select(MapType)]),
                StructType { Arguments.Length: > 0 } st => new StructType(InstantiateStruct(st)),
                ReferenceType r => r with { Pointee = MapType(r.Pointee) },
                GcType g => new GcType(MapType(g.Pointee)),
                BoxType b => new BoxType(MapType(b.Pointee)),
                PointerType p => p with { Pointee = MapType(p.Pointee) },
                ArrayType a => new ArrayType(MapType(a.Element)),
                FixedArrayType f => f with { Element = MapType(f.Element), SizeOf = f.SizeOf is null ? null : MapType(f.SizeOf) },
                FunctionType f => new FunctionType([.. f.Parameters.Select(MapType)], MapType(f.Result)),
                OptionalType o => new OptionalType(MapType(o.Inner)),
                _ => type,
            };

            private int InstantiateStruct(StructType generic)
            {
                // Arguments may already be instances (`gc struct#N`): one key per instantiation, in source numbering.
                generic = (StructType)Unmap(generic);
                if (structInstances.TryGetValue(generic, out int id))
                    return id;

                // Reserve the id first so recursive structs (through box/gc) terminate.
                id = structs.Count;
                structInstances.Add(generic, id);
                structOrigins.Add(id, generic);
                var def = module.Structs[generic.Id];
                structs.Add(def);
                structs[id] = new StructDef(IrPrinter.FormatType(generic, module),
                    [.. def.Fields.Select(f => MapType(f.Substitute(generic.Arguments)))])
                {
                    Mutable = def.Mutable,
                    Domain = def.Domain,
                    Base = def.Base is { } baseType ? MapType(baseType.Substitute(generic.Arguments)) as StructType : null,
                    FieldNames = def.FieldNames,
                    Reflect = def.Reflect,
                    InstanceOf = generic,
                    Attributes = def.Attributes,
                    FieldAttributes = def.FieldAttributes,
                };
                return id;
            }
        }
    }
}

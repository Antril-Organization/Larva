using Larva.IR.Text;

namespace Larva.IR.Analysis
{
    /// <summary>
    /// Arena regions are freed as a block, so nothing allocated in a region — nor any borrow into it — may outlive it.
    /// Runs only for GC modes with <see cref="GcCapabilities.Regions"/> (elsewhere annotations are ignored), always on:
    /// <list type="bullet">
    /// <item><b>Nesting</b>: regions close innermost-first, are never re-entered while open, are all closed at
    /// <c>return</c>, and every path into a block sees the same open regions.</item>
    /// <item><b>Tags</b>: every value that can point into a region carries tags saying which regions it may point into:
    /// a body region (<c>r0</c>), a region parameter of the signature (<c>'a</c>), an argument that is private to the call,
    /// or the caller's region (allocations made outside the function's own regions land in whatever region the caller
    /// has open). Tags follow copies, moves, borrows, struct fields and calls.</item>
    /// <item><b>Escapes inside a function</b>: a local tagged with <c>rN</c> must be dead after <c>region_exit(rN)</c>;
    /// a value stored behind a pointer must not outlive that memory (region values only into the same region, anything
    /// into an argument's memory only if the signature puts both in the same region parameter); the result may only
    /// carry what its signature allows (<c>-&gt; T in 'a</c>) plus the caller's region.</item>
    /// <item><b>Calls</b> are checked against the callee's signature instead of trusted: arguments sharing a region
    /// parameter may be stored into each other, so the memory of each must already belong to every region the others
    /// carry; the result carries the regions of the arguments in its region parameter plus the caller's innermost open region.</item>
    /// </list>
    /// </summary>
    public static class RegionChecker
    {
        public static IReadOnlyList<TypeError> Check(IrModule module, GcConfig gc)
        {
            if ((gc.Capabilities & GcCapabilities.Regions) == 0)
                return [];

            List<TypeError> errors = [];
            foreach (var function in module.Functions.Where(f => !f.IsDeclaration))
                new FunctionChecker(function, module, errors).Run();
            return errors;
        }

        /// <summary>
        /// The linked program's cross-domain rules, for the functions of the domains with regions: a value carrying any
        /// region may not be passed to a function of another GC domain (nothing checks what that function stores it into),
        /// and what such a function returns carries no region. The per-object checks ran already, so only a mixed program
        /// (<see cref="IrModule.MixedDomains"/>) adds errors here.
        /// </summary>
        public static IReadOnlyList<TypeError> CheckMixed(IrModule module, GcConfig program)
        {
            if (!module.MixedDomains)
                return [];
            List<TypeError> errors = [];
            foreach (var function in module.Functions.Where(f => !f.IsDeclaration && (module.GcOf(f.Domain, program).Capabilities & GcCapabilities.Regions) != 0))
                new FunctionChecker(function, module, errors).Run();
            return errors;
        }

        private enum TagKind : byte
        {
            /// <summary>Region opened by this function's body (<c>rN</c>).</summary>
            Body,
            /// <summary>Region parameter of the signature (<c>'a</c>).</summary>
            Param,
            /// <summary>Whatever an unannotated argument points into: private to this call.</summary>
            Argument,
            /// <summary>The region the caller had open (where allocations outside the function's own regions go).</summary>
            Caller,
        }

        private readonly record struct RegionTag(TagKind Kind, int Id)
        {
            public static readonly RegionTag Caller = new(TagKind.Caller, 0);
        }

        private sealed class TaintState
        {
            public readonly Dictionary<int, HashSet<RegionTag>> Held = [];

            public IEnumerable<RegionTag> TagsOf(int local) => Held.TryGetValue(local, out var tags) ? tags : [];
        }

        private sealed class FunctionChecker(IrFunction function, IrModule module, List<TypeError> errors) : IForwardAnalysis<TaintState>
        {
            private readonly TypeContext context = TypeContext.For(function, module);
            private readonly Dictionary<Location, int> innermost = [];
            private readonly HashSet<(Location?, string)> reported = [];

            public void Run()
            {
                if (!CheckNesting())
                    return; // tag analysis needs a consistent region structure

                var entries = Dataflow.Forward(function, this);
                var liveness = Liveness.Compute(function);
                for (int b = 0; b < function.Blocks.Length; b++)
                {
                    if (entries[b] is not { } entry)
                        continue;
                    var state = Clone(entry);
                    var block = function.Blocks[b];
                    for (int i = 0; i < block.Statements.Length; i++)
                    {
                        Location at = new(b, i);
                        CheckStatement(block.Statements[i], at, state, liveness);
                        Statement(state, block.Statements[i], at);
                    }

                    Location end = new(b, block.Statements.Length);
                    switch (block.Terminator)
                    {
                        case CallTerminator call:
                            CheckCall(call, state, end);
                            break;
                        case ReturnTerminator:
                            CheckReturn(state, end);
                            break;
                        case ThrowTerminator @throw:
                            foreach (var tag in OperandTags(state, @throw.Value).Distinct())
                            {
                                // The caller's region outlives the call, under its own name too (the CallerRegion parameter).
                                bool outlives = tag.Kind == TagKind.Caller || tag.Kind == TagKind.Param && tag.Id == function.CallerRegion;
                                if (!outlives)
                                    Report(end, TypeErrorKind.RegionEscape, $"throws {DescribeTag(tag)}; exceptions may leave the function, so they must not point into its regions or arguments");
                            }
                            break;
                    }
                }
            }

            // ── Nesting ────────────────────────────────────────────

            /// <summary>Walks the CFG with the stack of open regions; records the innermost region at every location.</summary>
            private bool CheckNesting()
            {
                int errorsBefore = errors.Count;
                var entryStacks = new int[]?[function.Blocks.Length];
                Stack<(int Block, int[] Open)> work = new([(0, [])]);

                while (work.TryPop(out var item))
                {
                    var (b, open) = item;
                    if (entryStacks[b] is { } seen)
                    {
                        if (!seen.SequenceEqual(open))
                            Report(new Location(b, 0), TypeErrorKind.RegionNesting,
                                $"paths into bb{b} disagree on open regions: [{Regions(seen)}] vs [{Regions(open)}]");
                        continue;
                    }
                    entryStacks[b] = open;

                    List<int> stack = [.. open];
                    var block = function.Blocks[b];
                    for (int i = 0; i <= block.Statements.Length; i++)
                    {
                        Location at = new(b, i);
                        if (stack.Count > 0)
                            innermost[at] = stack[^1];
                        if (i == block.Statements.Length)
                            break;

                        switch (block.Statements[i])
                        {
                            case RegionEnterStatement enter when stack.Contains(enter.Region):
                                Report(at, TypeErrorKind.RegionNesting, $"region r{enter.Region} is already open");
                                break;
                            case RegionEnterStatement enter:
                                stack.Add(enter.Region);
                                break;
                            case RegionExitStatement exit when stack.Count == 0 || stack[^1] != exit.Region:
                                Report(at, TypeErrorKind.RegionNesting, stack.Count == 0
                                    ? $"region_exit(r{exit.Region}) with no open region"
                                    : $"region_exit(r{exit.Region}) while r{stack[^1]} is the innermost open region");
                                stack.Remove(exit.Region);
                                break;
                            case RegionExitStatement:
                                stack.RemoveAt(stack.Count - 1);
                                break;
                        }
                    }

                    if (block.Terminator is ReturnTerminator && stack.Count > 0)
                        Report(new Location(b, block.Statements.Length), TypeErrorKind.RegionNesting,
                            $"returns with open region(s) [{Regions(stack)}]");
                    foreach (int successor in block.Terminator.Successors)
                        work.Push((successor, [.. stack]));
                }
                return errors.Count == errorsBefore;
            }

            private static string Regions(IEnumerable<int> regions) => string.Join(", ", regions.Select(r => $"r{r}"));

            // ── Escape checks ──────────────────────────────────────

            private void CheckStatement(Statement statement, Location at, TaintState state, Liveness liveness)
            {
                switch (statement)
                {
                    case StoreGlobalStatement store:
                        CheckGlobalStore(store, state, at);
                        break;
                    case RegionExitStatement exit:
                        var tag = new RegionTag(TagKind.Body, exit.Region);
                        foreach (int local in liveness.LiveAfter(at).Order())
                        {
                            if (state.TagsOf(local).Contains(tag))
                                Report(at, TypeErrorKind.RegionEscape,
                                    $"{DescribeLocal(local)} holds a value from region r{exit.Region} and is still used after region_exit(r{exit.Region})");
                        }
                        break;
                    case AssignStatement assign:
                        CheckStore(assign.Target, Incoming(state, assign.Value, at), state, at);
                        break;
                }
            }

            /// <summary>
            /// Storing behind a pointer: a body region's values only into memory of that region; values of the signature's
            /// regions or private arguments only into an argument's memory when it is in the same region.
            /// </summary>
            private void CheckStore(Place target, IEnumerable<RegionTag> incoming, TaintState state, Location at)
            {
                if (!target.HasDeref())
                    return;
                var owner = state.TagsOf(target.Local).ToHashSet();
                foreach (var tag in incoming.Distinct())
                {
                    bool outlives = tag.Kind == TagKind.Body || function.IsArgument(target.Local);
                    if (outlives && !owner.Contains(tag))
                        Report(at, TypeErrorKind.RegionEscape,
                            $"stores {DescribeTag(tag)} into `{Describe(target)}`, whose memory {OwnerText(target.Local, owner)}");
                }
            }

            /// <summary>Arguments are checked against the callee's region parameters; see the class summary.</summary>
            /// <summary>
            /// The signature a call is checked against: the callee, or for an indirect call a conservative stand-in whose
            /// arguments and result all share one region (anything may flow anywhere).
            /// </summary>
            private IrFunction? SignatureOf(CallTerminator call)
            {
                if (call.Required is not null)
                    return null;
                if (call.Callee is not { } target)
                    return module.Functions[call.Function];
                var type = (FunctionType)function.TypeOf(target, module);
                return new IrFunction("(indirect call)", type.Parameters.Length,
                    [new LocalDecl(type.Result, Region: 0), .. type.Parameters.Select(p => new LocalDecl(p, Region: 0))], []) { RegionParams = ["a"] };
            }

            /// <summary>A direct callee of another GC domain (in a linked, mixed program), or null.</summary>
            private IrFunction? ForeignCallee(CallTerminator call) =>
                module.MixedDomains && call.Required is null && call.Callee is null && module.Functions[call.Function] is { } callee && callee.Domain != function.Domain
                    ? callee : null;

            private void CheckCall(CallTerminator call, TaintState state, Location at)
            {
                if (ForeignCallee(call) is { } foreign)
                {
                    // Another domain's code may store an argument anywhere: only values of no region may go.
                    foreach (var argument in call.Arguments)
                    {
                        foreach (var tag in OperandTags(state, argument).Distinct())
                            Report(at, TypeErrorKind.RegionEscape,
                                $"`{foreign.Name}` belongs to another GC domain ({module.Domains[foreign.Domain].Mode}): {DescribeTag(tag)} passed to it may be stored there and outlive its region");
                    }
                    return;
                }
                if (SignatureOf(call) is not { } callee)
                    return; // no region parameters: arguments are private to the call
                foreach (var (region, members) in RegionGroups(callee, call))
                {
                    var carried = members.SelectMany(m => OperandTags(state, m.Operand)).ToHashSet();
                    // A callee that allocates in this region, called inside one of our regions, puts its new objects there.
                    if (callee.CallerRegion == region && innermost.TryGetValue(at, out int body))
                        carried.Add(new RegionTag(TagKind.Body, body));
                    foreach (var (_, operand, argument) in members)
                    {
                        // Stores land in the argument's memory: through a borrow, in what it points at (`&[u8]` holds no handle).
                        if (OperandPlace(operand) is not { } place || !CarriesHandles(Pointee(function.TypeOf(place, module))))
                            continue;
                        var owner = state.TagsOf(place.Local).ToHashSet();
                        foreach (var tag in carried)
                        {
                            bool outlives = tag.Kind == TagKind.Body || function.IsArgument(place.Local);
                            if (outlives && !owner.Contains(tag))
                                Report(at, TypeErrorKind.RegionEscape,
                                    $"`{callee.Name}` may store {DescribeTag(tag)} into `{Describe(place)}` (both in '{callee.RegionParams[region]}), " +
                                    $"whose memory {OwnerText(place.Local, owner)}");
                        }
                    }
                }
            }

            /// <summary>The result may carry the caller's region and, if annotated, its region parameter.</summary>
            private void CheckReturn(TaintState state, Location at)
            {
                int? allowed = function.Locals[0].Region;
                foreach (var tag in state.TagsOf(0).Distinct())
                {
                    bool ok = tag.Kind == TagKind.Caller || tag.Kind == TagKind.Param && tag.Id == allowed;
                    if (!ok && tag.Kind != TagKind.Body) // body regions are reported at their region_exit
                        Report(at, TypeErrorKind.RegionEscape, allowed is int region
                            ? $"returns {DescribeTag(tag)}, but the result is declared `in '{function.RegionParams[region]}`"
                            : $"returns {DescribeTag(tag)}, but the result has no region annotation (add `in 'a` to it and the argument)");
                }
            }

            // ── Tag analysis ───────────────────────────────────────

            /// <summary>Arguments start tagged with their signature region, or as private to the call.</summary>
            public TaintState Entry()
            {
                TaintState state = new();
                for (int local = 1; local <= function.ArgumentCount; local++)
                {
                    if (!CarriesHandles(function.Locals[local].Type))
                        continue;
                    state.Held[local] = function.Locals[local].Region is int region
                        ? region == function.CallerRegion
                            ? [new RegionTag(TagKind.Param, region), RegionTag.Caller] // the caller allocates there
                            : [new RegionTag(TagKind.Param, region)]
                        : [new RegionTag(TagKind.Argument, local)];
                }
                return state;
            }

            public TaintState Clone(TaintState state)
            {
                TaintState clone = new();
                foreach (var (local, tags) in state.Held)
                    clone.Held[local] = [.. tags];
                return clone;
            }

            public bool Join(TaintState into, TaintState from)
            {
                bool changed = false;
                foreach (var (local, tags) in from.Held)
                {
                    if (!into.Held.TryGetValue(local, out var existing))
                        into.Held[local] = existing = [];
                    foreach (var tag in tags)
                        changed |= existing.Add(tag);
                }
                return changed;
            }

            public void Statement(TaintState state, Statement statement, Location location)
            {
                switch (statement)
                {
                    case AssignStatement assign:
                        Store(state, assign.Target, Incoming(state, assign.Value, location));
                        break;
                    case StorageDeadStatement dead:
                        state.Held.Remove(dead.Local);
                        break;
                }
            }

            public void Terminator(TaintState state, Terminator terminator, Location location)
            {
                if (terminator is CallTerminator foreignCall && ForeignCallee(foreignCall) is not null)
                {
                    // What another domain returns is not in any of our regions.
                    Store(state, foreignCall.Destination, new HashSet<RegionTag>());
                    return;
                }
                if (terminator is not CallTerminator call || SignatureOf(call) is not { } callee)
                    return;
                // Arguments sharing a region parameter may now point at each other's values.
                foreach (var (_, members) in RegionGroups(callee, call))
                {
                    var carried = members.SelectMany(m => OperandTags(state, m.Operand)).ToHashSet();
                    foreach (var (_, operand, _) in members)
                    {
                        if (OperandPlace(operand) is { } place && CarriesHandles(function.TypeOf(place, module)) && carried.Count > 0)
                            Store(state, new Place(place.Local, [Projection.Deref]), carried);
                    }
                }

                HashSet<RegionTag> result = [];
                if (CarriesHandles(function.TypeOf(call.Destination, module)))
                {
                    result.Add(innermost.TryGetValue(location, out int region) ? new RegionTag(TagKind.Body, region) : RegionTag.Caller);
                    if (callee.Locals[0].Region is int returned)
                    {
                        for (int a = 0; a < call.Arguments.Length; a++)
                        {
                            if (callee.Locals[a + 1].Region == returned)
                                result.UnionWith(OperandTags(state, call.Arguments[a]));
                        }
                    }
                }
                Store(state, call.Destination, result);
            }

            /// <summary>Call arguments grouped by the callee region parameter they are annotated with.</summary>
            private static IEnumerable<(int Region, List<(int Index, Operand Operand, LocalDecl Argument)> Members)> RegionGroups(IrFunction callee, CallTerminator call) =>
                Enumerable.Range(0, call.Arguments.Length)
                    .Where(a => callee.Locals[a + 1].Region is not null)
                    .GroupBy(a => callee.Locals[a + 1].Region!.Value)
                    .Select(g => (g.Key, g.Select(a => (a, call.Arguments[a], callee.Locals[a + 1])).ToList()));

            /// <summary>A global outlives every region: no handle allocated inside one may be stored there.</summary>
            private void CheckGlobalStore(StoreGlobalStatement store, TaintState state, Location at)
            {
                foreach (var tag in OperandTags(state, store.Value).Where(t => t.Kind == TagKind.Body).Take(1))
                    Report(at, TypeErrorKind.RegionEscape, $"stores {DescribeTag(tag)} into a global, which outlives every region");
            }

            private IEnumerable<RegionTag> Incoming(TaintState state, Rvalue value, Location at)
            {
                switch (value)
                {
                    case RefRvalue reference:
                        return state.TagsOf(reference.Place.Local);
                    case AllocRvalue { Kind: AllocKind.Gc } alloc:
                        var where = innermost.TryGetValue(at, out int region) ? new RegionTag(TagKind.Body, region) : RegionTag.Caller;
                        return OperandTags(state, alloc.Value).Append(where);
                    case ArrayRvalue { Kind: AllocKind.Gc }:
                        return [innermost.TryGetValue(at, out int arrayRegion) ? new RegionTag(TagKind.Body, arrayRegion) : RegionTag.Caller];
                    case LenRvalue:
                        return [];
                    default:
                        return MoveAnalysis.Operands(value).SelectMany(o => OperandTags(state, o));
                }
            }

            private IEnumerable<RegionTag> OperandTags(TaintState state, Operand operand) =>
                OperandPlace(operand) is { } place && CarriesHandles(function.TypeOf(place, module)) ? state.TagsOf(place.Local) : [];

            private static Place? OperandPlace(Operand operand) => operand switch
            {
                CopyOperand copy => copy.Place,
                MoveOperand move => move.Place,
                _ => null,
            };

            /// <summary>Values that can point into a region: anything owning <c>gc</c> handles or holding borrows.</summary>
            private static IrType Pointee(IrType type) => type is ReferenceType reference ? reference.Pointee : type;

            private bool CarriesHandles(IrType type) =>
                !Constraints.Satisfies(type, TypeConstraint.Unmanaged, context) || type.ContainsReference(context);

            private static void Store(TaintState state, Place target, IEnumerable<RegionTag> incoming)
            {
                HashSet<RegionTag> tags = [.. incoming];
                if (target.IsLocal)
                {
                    if (tags.Count == 0)
                        state.Held.Remove(target.Local);
                    else
                        state.Held[target.Local] = tags;
                }
                else if (tags.Count > 0)
                {
                    if (!state.Held.TryGetValue(target.Local, out var existing))
                        state.Held[target.Local] = existing = [];
                    existing.UnionWith(tags);
                }
            }

            // ── Reporting ──────────────────────────────────────────

            private string DescribeTag(RegionTag tag) => tag.Kind switch
            {
                TagKind.Body => $"a value from region r{tag.Id}",
                TagKind.Param => $"a value from region '{function.RegionParams[tag.Id]}",
                TagKind.Argument => $"a value from argument {DescribeLocal(tag.Id)} (private to the call)",
                _ => "a value from the caller's region",
            };

            private string OwnerText(int local, HashSet<RegionTag> owner) =>
                function.IsArgument(local) && function.Locals[local].Region is int region
                    ? $"is in '{function.RegionParams[region]}"
                    : function.IsArgument(local)
                        ? "belongs to the caller"
                        : owner.Count == 0 ? "is outside every region" : $"is in {string.Join(", ", owner.Select(DescribeTagShort))}";

            private string DescribeTagShort(RegionTag tag) => tag.Kind switch
            {
                TagKind.Body => $"r{tag.Id}",
                TagKind.Param => $"'{function.RegionParams[tag.Id]}",
                TagKind.Argument => $"argument {DescribeLocal(tag.Id)}",
                _ => "the caller's region",
            };

            private string DescribeLocal(int local) =>
                function.Locals[local].Name is { } name ? $"`{name}`" : $"`_{local}`";

            private string Describe(Place place)
            {
                string text = IrPrinter.Format(place);
                return function.Locals[place.Local].Name is { } name
                    ? System.Text.RegularExpressions.Regex.Replace(text, $@"\b_{place.Local}\b", name)
                    : text;
            }

            private void Report(Location at, TypeErrorKind kind, string message)
            {
                if (reported.Add((at, message)))
                    errors.Add(new TypeError(function.Name, at, kind, message));
            }
        }
    }
}

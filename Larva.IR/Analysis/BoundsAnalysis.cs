namespace Larva.IR.Analysis
{
    /// <summary>
    /// Bounds-check elimination: finds the array accesses <c>(*a)[i]</c> that are provably in bounds, so back ends can drop
    /// their checks.
    /// <para>
    /// A forward must-analysis over facts about locals:
    /// <list type="bullet">
    /// <item><c>len(*a) == n</c> / <c>len(*a) == k</c>: from <c>a = new [T; n]</c> and <c>n = len(*a)</c>. Array lengths never
    /// change, so the fact lives as long as neither local is reassigned.</item>
    /// <item><c>i &lt; n</c> / <c>i &lt; k</c>: on the then edge of <c>if c</c>, where <c>c = i &lt; n</c> (or <c>n &gt; i</c>).</item>
    /// <item><c>i ≥ 0</c>: from non-negative constants, lengths, and <c>i = i + 1</c> while <c>i</c> was bounded above (so it
    /// cannot overflow); unsigned locals are non-negative by type.</item>
    /// </list>
    /// An access is safe when <c>0 ≤ i &lt; n == len(*a)</c>. Assigning a local kills every fact that mentions it; locals
    /// that are ever borrowed mutably can change behind the analysis' back, so they never take part.
    /// </para>
    /// </summary>
    public sealed class BoundsAnalysis
    {
        private readonly HashSet<(Location Location, int Array, int Index)> safe = [];

        /// <summary>Accesses examined (one per index projection on <c>*local</c>).</summary>
        public int Accesses { get; private set; }

        public int Eliminated => safe.Count;

        /// <summary>Whether <c>(*array)[index]</c> at <paramref name="location"/> needs no bounds check.</summary>
        public bool IsSafe(Location location, int array, int index) => safe.Contains((location, array, index));

        public static BoundsAnalysis Run(IrFunction function, IrModule module)
        {
            var result = new BoundsAnalysis();
            var analysis = new Facts(function, module);
            var entries = Dataflow.Forward(function, analysis);
            for (int b = 0; b < function.Blocks.Length; b++)
            {
                if (entries[b] is not { } entry)
                    continue;
                var state = analysis.Clone(entry);
                var block = function.Blocks[b];
                for (int s = 0; s <= block.Statements.Length; s++)
                {
                    var location = new Location(b, s);
                    var places = s < block.Statements.Length ? Places(block.Statements[s]) : Places(block.Terminator);
                    foreach (var place in places)
                        result.Examine(analysis, state, place, location);
                    if (s < block.Statements.Length)
                        analysis.Statement(state, block.Statements[s], location);
                }
            }
            return result;
        }

        private void Examine(Facts analysis, HashSet<Fact> state, Place place, Location location)
        {
            for (int p = 0; p < place.Projections.Length; p++)
            {
                if (place.Projections[p] is not IndexProjection index)
                    continue;
                Accesses++;
                if ((p == 1 && place.Projections[0] is DerefProjection || InPlace(analysis.Function, analysis.Module, place, p))
                    && analysis.InBounds(state, place.Local, index.Local))
                    safe.Add((location, place.Local, index.Local));
            }
        }

        /// <summary>
        /// Whether projection <paramref name="p"/> indexes an object's array kept in place (<c>(*obj).items[i]</c>): its length
        /// is the object's for good, so the facts about the handle's local hold for it as for an array's.
        /// </summary>
        public static bool InPlace(IrFunction function, IrModule module, Place place, int p) =>
            p == 2 && place.Projections[0] is DerefProjection && place.Projections[1] is FieldProjection
            && function.TypeOf(new Place(place.Local, place.Projections[..2]), module) is ArrayType;

        // ── Facts ──────────────────────────────────────────────────

        private enum Kind : byte { NonNegative, Less, LessConst, Length, LengthConst, Condition, ConditionConst }

        /// <summary>
        /// NonNegative(A); Less(A, B): A &lt; B; LessConst(A, K); Length(A, B): len(*A) == B; LengthConst(A, K);
        /// Condition(C, A, B): C == (A &lt; B); ConditionConst(C, A, K): C == (A &lt; K).
        /// </summary>
        private readonly record struct Fact(Kind Kind, int A, int B = -1, long K = 0, int C = -1)
        {
            public bool Mentions(int local) => A == local || B == local || C == local;
        }

        private sealed class Facts(IrFunction function, IrModule module) : IForwardAnalysis<HashSet<Fact>>
        {
            public IrFunction Function => function;
            public IrModule Module => module;

            private readonly HashSet<int> borrowed = BorrowedMutably(function);

            public HashSet<Fact> Entry() => [];

            public HashSet<Fact> Clone(HashSet<Fact> state) => [.. state];

            public bool Join(HashSet<Fact> into, HashSet<Fact> from)
            {
                int before = into.Count;
                into.IntersectWith(from);
                return into.Count != before;
            }

            public HashSet<Fact> Refine(HashSet<Fact> state, Terminator terminator, int successor)
            {
                if (terminator is not IfTerminator { Condition: CopyOperand or MoveOperand } branch || successor != branch.Then || branch.Then == branch.Else)
                    return state;
                int condition = Local(branch.Condition);
                var refined = Clone(state);
                foreach (var fact in state)
                {
                    if (fact.Kind == Kind.Condition && fact.C == condition)
                        refined.Add(new Fact(Kind.Less, fact.A, fact.B));
                    else if (fact.Kind == Kind.ConditionConst && fact.C == condition)
                        refined.Add(new Fact(Kind.LessConst, fact.A, K: fact.K));
                }
                return refined;
            }

            public void Statement(HashSet<Fact> state, Statement statement, Location location)
            {
                switch (statement)
                {
                    case AssignStatement { Target: var target, Value: var value }:
                        if (target.IsLocal)
                            Assign(state, target.Local, value);
                        else if (!target.HasDeref())
                            Kill(state, target.Local);
                        break;
                    case StorageLiveStatement live:
                        Kill(state, live.Local);
                        break;
                    case StorageDeadStatement dead:
                        Kill(state, dead.Local);
                        break;
                }
            }

            public void Terminator(HashSet<Fact> state, Terminator terminator, Location location)
            {
                if (terminator is CallTerminator call && !call.Destination.HasDeref())
                    Kill(state, call.Destination.Local);
            }

            private void Assign(HashSet<Fact> state, int target, Rvalue value)
            {
                var generated = new List<Fact>();
                switch (value)
                {
                    case UseRvalue { Operand: ConstOperand { Bits: var bits } } when bits >= 0:
                        generated.Add(new Fact(Kind.NonNegative, target));
                        break;
                    case UseRvalue { Operand: var operand } when Local(operand) is int source and >= 0 && Tracked(source):
                        // A copy inherits what is known about its source.
                        foreach (var fact in state.Where(f => f.Mentions(source)))
                            generated.Add(fact with
                            {
                                A = fact.A == source ? target : fact.A,
                                B = fact.B == source ? target : fact.B,
                                C = fact.C == source ? target : fact.C,
                            });
                        break;
                    case BinaryRvalue { Op: BinaryOp.Add, Left: var left, Right: ConstOperand { Bits: var one } } when one == 1 && Local(left) is int x and >= 0
                        && state.Contains(new Fact(Kind.NonNegative, x)) && state.Any(f => f.A == x && f.Kind is Kind.Less or Kind.LessConst):
                        generated.Add(new Fact(Kind.NonNegative, target));
                        break;
                    case BinaryRvalue { Op: BinaryOp.Lt or BinaryOp.Gt } compare:
                    {
                        var (low, high) = compare.Op == BinaryOp.Lt ? (compare.Left, compare.Right) : (compare.Right, compare.Left);
                        if (Local(low) is not (int a and >= 0) || !Tracked(a) || !function.TypeOf(low, module).IsInteger)
                            break;
                        if (Local(high) is int b and >= 0 && Tracked(b))
                            generated.Add(new Fact(Kind.Condition, a, b, C: target));
                        else if (high is ConstOperand { FitsInt64: true } k)
                            generated.Add(new Fact(Kind.ConditionConst, a, K: (long)k.Bits, C: target));
                        break;
                    }
                    // An object with its array in place: the facts of the handle are its array's.
                    case ArrayRvalue { Length: var length }:
                        if (Local(length) is int n and >= 0 && Tracked(n))
                            generated.Add(new Fact(Kind.Length, target, n));
                        else if (length is ConstOperand { FitsInt64: true } k)
                            generated.Add(new Fact(Kind.LengthConst, target, K: (long)k.Bits));
                        break;
                    case LenRvalue { Array: { Projections: [DerefProjection] } array } when Tracked(array.Local):
                        generated.Add(new Fact(Kind.Length, array.Local, target));
                        generated.Add(new Fact(Kind.NonNegative, target));
                        break;
                    case LenRvalue { Array: { Projections: [DerefProjection, FieldProjection] } kept } when Tracked(kept.Local) && function.TypeOf(kept, module) is ArrayType:
                        generated.Add(new Fact(Kind.Length, kept.Local, target));
                        generated.Add(new Fact(Kind.NonNegative, target));
                        break;
                }
                Kill(state, target);
                if (Tracked(target))
                    state.UnionWith(generated);
            }

            private static void Kill(HashSet<Fact> state, int local) => state.RemoveWhere(f => f.Mentions(local));

            private bool Tracked(int local) => !borrowed.Contains(local);

            public bool InBounds(HashSet<Fact> state, int array, int index)
            {
                var type = function.Locals[index].Type;
                if (!Tracked(array) || !Tracked(index) || !type.IsInteger || (type.BitWidth ?? 128) > 64)
                    return false;
                if (!type.IsUnsigned && !state.Contains(new Fact(Kind.NonNegative, index)))
                    return false;
                foreach (var fact in state)
                {
                    if (fact.Kind == Kind.Less && fact.A == index && state.Contains(new Fact(Kind.Length, array, fact.B)))
                        return true;
                    if (fact.Kind == Kind.LessConst && fact.A == index
                        && state.Any(l => l.Kind == Kind.LengthConst && l.A == array && fact.K <= l.K))
                        return true;
                }
                return false;
            }

            private static int Local(Operand operand) => operand switch
            {
                CopyOperand { Place.IsLocal: true } copy => copy.Place.Local,
                MoveOperand { Place.IsLocal: true } move => move.Place.Local,
                _ => -1,
            };

            private static HashSet<int> BorrowedMutably(IrFunction function) =>
            [
                .. function.Blocks.SelectMany(b => b.Statements)
                    .OfType<AssignStatement>()
                    .Select(a => a.Value)
                    .OfType<RefRvalue>()
                    .Where(r => r.Mutable && !r.Place.HasDeref())
                    .Select(r => r.Place.Local),
            ];
        }

        // ── Places read or written ─────────────────────────────────

        private static IEnumerable<Place> Places(Statement statement)
        {
            if (statement is AssignStatement assign)
            {
                yield return assign.Target;
                foreach (var place in Places(assign.Value))
                    yield return place;
            }
            else if (statement is FreeStatement free)
            {
                yield return free.Pointer;
            }
        }

        private static IEnumerable<Place> Places(Rvalue value) => value switch
        {
            UseRvalue use => Places(use.Operand),
            BinaryRvalue binary => [.. Places(binary.Left), .. Places(binary.Right)],
            UnaryRvalue unary => Places(unary.Operand),
            RefRvalue reference => [reference.Place],
            CastRvalue cast => Places(cast.Operand),
            AllocRvalue alloc => alloc.Finalizer is { } finalizer ? [.. Places(alloc.Value), .. Places(finalizer)] : Places(alloc.Value),
            IntrinsicRvalue intrinsic => [.. intrinsic.Arguments.SelectMany(Places)],
            ArrayRvalue array => Places(array.Length),
            LenRvalue len => [len.Array],
            HandleCastRvalue cast => Places(cast.Handle),
            WrapRvalue wrap => Places(wrap.Value),
            UnwrapRvalue unwrap => Places(unwrap.Value),
            IsNullRvalue test => [test.Value],
            _ => [],
        };

        private static IEnumerable<Place> Places(Operand operand) => operand switch
        {
            CopyOperand copy => [copy.Place],
            MoveOperand move => [move.Place],
            _ => [],
        };

        private static IEnumerable<Place> Places(Terminator terminator) => terminator switch
        {
            IfTerminator branch => Places(branch.Condition),
            SwitchTerminator @switch => Places(@switch.Value),
            CallTerminator call => [.. call.Arguments.SelectMany(Places), call.Destination],
            ThrowTerminator @throw => Places(@throw.Value),
            DropTerminator drop => [drop.Place],
            _ => [],
        };
    }
}

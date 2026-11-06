namespace Larva.IR.Analysis
{
    internal enum UninitKind : byte
    {
        NeverInitialized,
        Moved,
        StorageDead,
        Dropped,
    }

    /// <summary>Why a place may hold no value, and where that happened.</summary>
    internal readonly record struct UninitOrigin(UninitKind Kind, Location At);

    /// <summary>
    /// Set of places that <i>may</i> be uninitialized (never written, moved out, dropped or out of storage).
    /// Children of a recorded place are implied. Writing a field of an uninitialized struct splits the
    /// entry into its sibling fields, so structs can be initialized field by field.
    /// </summary>
    internal sealed class MoveState
    {
        // Whole locals (by far the common entry: every local is uninitialized at entry) live in an array indexed by local;
        // places with projections in a small map per local. Both are copied on write, so cloning a state (once per block
        // and per program point while lowering) costs nothing until one of the copies changes.
        private UninitOrigin?[] whole = [];
        private bool wholeShared;
        private Dictionary<int, Dictionary<Place, UninitOrigin>> partial = [];
        private bool partialShared;

        public IEnumerable<KeyValuePair<Place, UninitOrigin>> Entries
        {
            get
            {
                for (int local = 0; local < whole.Length; local++)
                {
                    if (whole[local] is { } origin)
                        yield return new(new Place(local), origin);
                }
                foreach (var places in partial.Values)
                {
                    foreach (var entry in places)
                        yield return entry;
                }
            }
        }

        private UninitOrigin? WholeOf(int local) => local < whole.Length ? whole[local] : null;

        private void SetWhole(int local, UninitOrigin? origin)
        {
            if (WholeOf(local) == origin)
                return;
            if (wholeShared || local >= whole.Length)
            {
                var copy = new UninitOrigin?[Math.Max(local + 1, whole.Length)];
                Array.Copy(whole, copy, whole.Length);
                whole = copy;
                wholeShared = false;
            }
            whole[local] = origin;
        }

        private Dictionary<Place, UninitOrigin>? PartialOf(int local) => partial.GetValueOrDefault(local);

        /// <summary>The map of one local's places, owned by this state (copied first if shared).</summary>
        private Dictionary<Place, UninitOrigin> OwnPartial(int local)
        {
            if (partialShared)
            {
                partial = partial.ToDictionary(kv => kv.Key, kv => new Dictionary<Place, UninitOrigin>(kv.Value));
                partialShared = false;
            }
            if (!partial.TryGetValue(local, out var places))
                partial[local] = places = [];
            return places;
        }

        private void DropPartial(int local)
        {
            if (!partial.ContainsKey(local))
                return;
            OwnPartial(local);
            partial.Remove(local);
        }

        /// <summary>Records <paramref name="place"/> as it is (no merging with overlapping entries).</summary>
        public void Set(Place place, UninitOrigin origin)
        {
            if (place.Projections.Length == 0)
                SetWhole(place.Local, origin);
            else
                OwnPartial(place.Local)[place] = origin;
        }

        /// <summary>An entry overlapping <paramref name="place"/>, if any part of it may be uninitialized.</summary>
        public UninitOrigin? Find(Place place)
        {
            if (WholeOf(place.Local) is { } origin)
                return origin;
            if (PartialOf(place.Local) is not { } places)
                return null;
            foreach (var (key, found) in places)
            {
                if (key.IsPrefixOf(place) || place.IsPrefixOf(key))
                    return found;
            }
            return null;
        }

        /// <summary>Whether a recorded place contains <paramref name="place"/> (it or a prefix of it).</summary>
        public bool Covers(Place place)
        {
            if (WholeOf(place.Local) is not null)
                return true;
            if (place.Projections.Length == 0 || PartialOf(place.Local) is not { } places)
                return false;
            foreach (var key in places.Keys)
            {
                if (key.IsPrefixOf(place))
                    return true;
            }
            return false;
        }

        public void MarkUninit(Place place, UninitOrigin origin)
        {
            if (place.Projections.Length == 0)
            {
                DropPartial(place.Local);
                SetWhole(place.Local, origin);
                return;
            }
            if (PartialOf(place.Local) is { } existing && existing.Keys.Any(place.IsPrefixOf))
            {
                var places = OwnPartial(place.Local);
                foreach (var key in places.Keys.Where(place.IsPrefixOf).ToList())
                    places.Remove(key);
            }
            OwnPartial(place.Local)[place] = origin;
        }

        public void Initialize(Place target, IrFunction function, IrModule module)
        {
            int local = target.Local;
            if (target.Projections.Length == 0)
            {
                SetWhole(local, null);
                DropPartial(local);
                return;
            }
            // Parents of the target (the whole local, or a shorter place) are split into the siblings along the path.
            var parents = new List<(Place Place, UninitOrigin Origin)>();
            if (WholeOf(local) is { } wholeOrigin)
            {
                parents.Add((new Place(local), wholeOrigin));
                SetWhole(local, null);
            }
            if (PartialOf(local) is { } existing)
            {
                if (existing.Keys.Any(k => target.IsPrefixOf(k) || k.IsPrefixOf(target)))
                {
                    var places = OwnPartial(local);
                    foreach (var key in places.Keys.Where(target.IsPrefixOf).ToList())
                        places.Remove(key);
                    foreach (var key in places.Keys.Where(k => k.IsPrefixOf(target)).ToList())
                    {
                        parents.Add((key, places[key]));
                        places.Remove(key);
                    }
                }
            }
            foreach (var (parent, origin) in parents)
            {
                // Only a path of field projections can be split into its siblings.
                var path = target.Projections[parent.Projections.Length..];
                if (!path.All(p => p is FieldProjection))
                    continue;
                var current = parent;
                foreach (FieldProjection field in path)
                {
                    if (function.TypeOf(current, module) is not StructType st)
                        break;
                    for (int f = 0; f < module.Structs[st.Id].Fields.Length; f++)
                    {
                        if (f != field.FieldIndex)
                            OwnPartial(local)[current.Field(f)] = origin;
                    }
                    current = current.Field(field.FieldIndex);
                }
            }
            if (partial.TryGetValue(local, out var left) && left.Count == 0)
                DropPartial(local);
        }

        public MoveState Clone()
        {
            wholeShared = partialShared = true;
            return new MoveState { whole = whole, wholeShared = true, partial = partial, partialShared = true };
        }

        /// <summary>Adds what <paramref name="from"/> records and this state does not cover (a union); whether it grew.</summary>
        public bool Union(MoveState from)
        {
            bool changed = false;
            for (int local = 0; local < from.whole.Length; local++)
            {
                if (from.whole[local] is { } origin && WholeOf(local) is null)
                {
                    SetWhole(local, origin);
                    changed = true;
                }
            }
            foreach (var (local, places) in from.partial)
            {
                foreach (var (place, origin) in places)
                {
                    if (Covers(place))
                        continue;
                    OwnPartial(local)[place] = origin;
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>Keeps only what is uninitialized in both states; returns whether this state shrank.</summary>
        public bool Intersect(MoveState from)
        {
            // Shrinks when something here is not covered there.
            bool shrinks = false;
            for (int local = 0; local < whole.Length && !shrinks; local++)
                shrinks = whole[local] is not null && from.WholeOf(local) is null;
            foreach (var places in partial.Values)
            {
                if (shrinks)
                    break;
                foreach (var key in places.Keys)
                {
                    if (!from.Covers(key))
                    {
                        shrinks = true;
                        break;
                    }
                }
            }
            if (!shrinks)
                return false;

            // Kept: what here is covered there, plus what there is covered here (first one wins on the same place).
            int size = Math.Max(whole.Length, from.whole.Length);
            var keptWhole = new UninitOrigin?[size];
            var keptPartial = new Dictionary<int, Dictionary<Place, UninitOrigin>>();
            void Keep(Place place, UninitOrigin origin)
            {
                if (place.Projections.Length == 0)
                {
                    keptWhole[place.Local] ??= origin;
                    return;
                }
                if (!keptPartial.TryGetValue(place.Local, out var places))
                    keptPartial[place.Local] = places = [];
                places.TryAdd(place, origin);
            }
            for (int local = 0; local < size; local++)
            {
                if (WholeOf(local) is { } mine && from.WholeOf(local) is not null)
                    Keep(new Place(local), mine);
            }
            foreach (var places in partial.Values)
            {
                foreach (var (place, origin) in places)
                {
                    if (from.Covers(place))
                        Keep(place, origin);
                }
            }
            foreach (var places in from.partial.Values)
            {
                foreach (var (place, origin) in places)
                {
                    if (Covers(place))
                        Keep(place, origin);
                }
            }
            // A whole local kept makes its places redundant.
            foreach (int local in keptPartial.Keys.Where(l => keptWhole[l] is not null).ToList())
                keptPartial.Remove(local);
            whole = keptWhole;
            wholeShared = false;
            partial = keptPartial;
            partialShared = false;
            return true;
        }
    }

    /// <summary>
    /// Forward uninitialization analysis. By default it computes places that <i>may</i> be uninitialized (union at
    /// joins); with <c>must: true</c> it computes places that are uninitialized on <i>every</i> path (intersection).
    /// The lowering combines both to decide whether a release is unconditional, skipped, or guarded by a drop flag.
    /// </summary>
    /// <param name="transfers">
    /// Extra operands that give up their value at a location even though their type is Copy (reference-counted
    /// handles at their last use). The borrow checker passes none.
    /// </param>
    internal sealed class MoveAnalysis(IrFunction function, IrModule module, Func<Operand, Location, bool>? transfers = null, bool must = false)
        : IForwardAnalysis<MoveState>
    {
        public MoveState Entry()
        {
            MoveState state = new();
            for (int local = 0; local < function.Locals.Length; local++)
            {
                if (!function.IsArgument(local))
                    state.Set(new Place(local), new UninitOrigin(UninitKind.NeverInitialized, new Location(0, 0)));
            }
            return state;
        }

        public MoveState Clone(MoveState state) => state.Clone();

        public bool Join(MoveState into, MoveState from)
        {
            return must ? into.Intersect(from) : into.Union(from);
        }

        /// <summary>True when every path leaves <paramref name="place"/> uninitialized (meaningful for a <c>must</c> state).</summary>
        public static bool Covers(MoveState state, Place place) => state.Covers(place);

        /// <summary>
        /// How many times <paramref name="local"/> is read by the statement or terminator at <paramref name="at"/> (as an
        /// operand, an index, a borrowed place or inside a non-local target): the transfer rule hands a handle over only
        /// when this is 1 and the local is dead after.
        /// </summary>
        public static int Occurrences(IrFunction function, int local, Location at)
        {
            var block = function.Blocks[at.Block];
            (IEnumerable<Operand> Operands, Place? Target, Place? Borrowed) parts = at.Index < block.Statements.Length
                ? block.Statements[at.Index] switch
                {
                    AssignStatement assign => (Operands(assign.Value), assign.Target, (assign.Value as RefRvalue)?.Place),
                    _ => ([], null, null),
                }
                : block.Terminator switch
                {
                    CallTerminator call => (call.Arguments, call.Destination, null),
                    IfTerminator branch => ([branch.Condition], null, null),
                    SwitchTerminator @switch => ([@switch.Value], null, null),
                    _ => ([], null, null),
                };
            static int Count(Place place, int local) => (place.Local == local ? 1 : 0) + place.IndexLocals().Count(i => i == local);
            int count = parts.Operands.Sum(o => o switch
            {
                CopyOperand copy => Count(copy.Place, local),
                MoveOperand move => Count(move.Place, local),
                _ => 0,
            });
            if (parts.Borrowed is { } borrowed)
                count += Count(borrowed, local);
            if (parts.Target is { IsLocal: false } target)
                count += Count(target, local);
            return count;
        }

        public void Statement(MoveState state, Statement statement, Location location)
        {
            switch (statement)
            {
                case AssignStatement assign:
                    MoveOperands(state, Operands(assign.Value), location);
                    state.Initialize(assign.Target, function, module);
                    break;
                case StoreGlobalStatement store:
                    MoveOperands(state, [store.Value], location);
                    break;
                case StorageLiveStatement live:
                    state.MarkUninit(new Place(live.Local), new UninitOrigin(UninitKind.NeverInitialized, location));
                    break;
                case StorageDeadStatement dead:
                    state.MarkUninit(new Place(dead.Local), new UninitOrigin(UninitKind.StorageDead, location));
                    break;
            }
        }

        public void Terminator(MoveState state, Terminator terminator, Location location)
        {
            switch (terminator)
            {
                case IfTerminator branch:
                    MoveOperands(state, [branch.Condition], location);
                    break;
                case SwitchTerminator @switch:
                    MoveOperands(state, [@switch.Value], location);
                    break;
                case CallTerminator call:
                    MoveOperands(state, [.. call.Operands], location);
                    state.Initialize(call.Destination, function, module);
                    break;
                case DropTerminator drop:
                    state.MarkUninit(drop.Place, new UninitOrigin(UninitKind.Dropped, location));
                    break;
                case ThrowTerminator @throw:
                    MoveOperands(state, [@throw.Value], location);
                    break;
            }
        }

        /// <summary>On an exception edge the arguments (or thrown value) are gone, but nothing was written.</summary>
        public void Unwind(MoveState state, Terminator terminator, Location location)
        {
            switch (terminator)
            {
                case CallTerminator call:
                    MoveOperands(state, [.. call.Operands], location);
                    break;
                case ThrowTerminator @throw:
                    MoveOperands(state, [@throw.Value], location);
                    break;
            }
        }

        /// <summary>
        /// Records real moves: non-Copy values out of a place reachable without dereferencing or indexing, plus the
        /// configured <c>transfers</c>. Illegal moves are reported by the checker and do not change the state.
        /// </summary>
        private void MoveOperands(MoveState state, IEnumerable<Operand> operands, Location location)
        {
            foreach (var place in MovedBy(operands, location))
                state.MarkUninit(place, new UninitOrigin(UninitKind.Moved, location));
        }

        /// <summary>Places whose value the operands give up at <paramref name="location"/>.</summary>
        public IEnumerable<Place> MovedBy(IEnumerable<Operand> operands, Location location)
        {
            foreach (var operand in operands)
            {
                if (operand is MoveOperand { Place: var place } && IsRealMove(place))
                    yield return place;
                else if (transfers?.Invoke(operand, location) == true)
                    yield return operand is CopyOperand copy ? copy.Place : ((MoveOperand)operand).Place;
            }
        }

        internal bool IsRealMove(Place place) =>
            !function.TypeOf(place, module).IsCopy(context) && !place.HasDeref() && !place.HasIndex();

        private readonly TypeContext context = TypeContext.For(function, module);

        internal static IEnumerable<Operand> Operands(Rvalue rvalue) => rvalue switch
        {
            WrapRvalue wrap => [wrap.Value],
            UnwrapRvalue unwrap => [unwrap.Value],
            HandleCastRvalue cast => [cast.Handle],
            UseRvalue use => [use.Operand],
            BinaryRvalue binary => [binary.Left, binary.Right],
            UnaryRvalue unary => [unary.Operand],
            CastRvalue cast => [cast.Operand],
            AllocRvalue alloc => alloc.Finalizer is { } finalizer ? [alloc.Value, finalizer] : [alloc.Value],
            IntrinsicRvalue intrinsic => intrinsic.Arguments,
            ArrayRvalue array => [array.Length],
            _ => [],
        };
    }
}

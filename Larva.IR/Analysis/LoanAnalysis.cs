namespace Larva.IR.Analysis
{
    /// <summary>A borrow created by <c>&amp;place</c> / <c>&amp;mut place</c> at <see cref="At"/>.</summary>
    internal sealed record Loan(int Id, Location At, Place Place, bool Mutable);

    /// <summary>For each local, the loans its current value may carry (directly or through copies/reborrows).</summary>
    internal sealed class LoanState
    {
        public readonly Dictionary<int, HashSet<int>> Held = [];

        public IEnumerable<int> LoansOf(int local) => Held.TryGetValue(local, out var loans) ? loans : [];
    }

    /// <summary>
    /// Forward analysis tracking which locals hold which loans. A loan is <i>active</i> at a point when a
    /// local holding it is live there (non-lexical lifetimes): see <see cref="BorrowChecker"/>.
    /// </summary>
    internal sealed class LoanAnalysis : IForwardAnalysis<LoanState>
    {
        private readonly IrFunction function;
        private readonly IrModule module;
        private readonly TypeContext context;
        private readonly Dictionary<Location, Loan> loansByLocation = [];

        public IReadOnlyList<Loan> Loans { get; }

        public LoanAnalysis(IrFunction function, IrModule module)
        {
            this.function = function;
            this.module = module;
            context = TypeContext.For(function, module);

            List<Loan> loans = [];
            for (int b = 0; b < function.Blocks.Length; b++)
            {
                var statements = function.Blocks[b].Statements;
                for (int i = 0; i < statements.Length; i++)
                {
                    if (statements[i] is AssignStatement { Value: RefRvalue reference })
                    {
                        Loan loan = new(loans.Count, new Location(b, i), reference.Place, reference.Mutable);
                        loans.Add(loan);
                        loansByLocation[loan.At] = loan;
                    }
                }
            }
            Loans = loans;
        }

        public LoanState Entry() => new();

        public LoanState Clone(LoanState state)
        {
            LoanState clone = new();
            foreach (var (local, loans) in state.Held)
                clone.Held[local] = [.. loans];
            return clone;
        }

        public bool Join(LoanState into, LoanState from)
        {
            bool changed = false;
            foreach (var (local, loans) in from.Held)
            {
                if (!into.Held.TryGetValue(local, out var existing))
                    into.Held[local] = existing = [];
                foreach (int loan in loans)
                    changed |= existing.Add(loan);
            }
            return changed;
        }

        public void Statement(LoanState state, Statement statement, Location location)
        {
            switch (statement)
            {
                case AssignStatement assign:
                    HashSet<int> incoming = [];
                    if (assign.Value is RefRvalue reference)
                    {
                        incoming.Add(loansByLocation[location].Id);
                        // Reborrowing through a reference keeps the original loans alive too.
                        if (reference.Place.HasDeref())
                            incoming.UnionWith(state.LoansOf(reference.Place.Local));
                    }
                    else if (assign.Value is not IntrinsicRvalue)
                    {
                        // (The runtime's operations give numbers, raw pointers or nothing: they borrow from no argument.)
                        foreach (var operand in MoveAnalysis.Operands(assign.Value))
                            incoming.UnionWith(LoansFlowingFrom(state, operand));
                    }
                    Store(state, assign.Target, incoming);
                    break;

                case StorageDeadStatement dead:
                    state.Held.Remove(dead.Local);
                    break;
            }
        }

        public void Terminator(LoanState state, Terminator terminator, Location location)
        {
            if (terminator is not CallTerminator call)
                return;

            // A returned reference may come from the reference arguments: it may point into what one of their loans
            // borrowed (when that place's type can hold what it points to), or be a copy of a reference stored there (and
            // carry the loans that place holds). Types decide which: a `&[u16]` cannot point into a struct of numbers.
            HashSet<int> incoming = [];
            var result = function.TypeOf(call.Destination, module);
            if (result.ContainsReference(context))
            {
                var targets = Pointees(result, []);
                foreach (var argument in call.Arguments)
                {
                    foreach (int id in LoansFlowingFrom(state, argument))
                    {
                        var borrowed = function.TypeOf(Loans[id].Place, module);
                        if (targets is null || targets.Any(t => MayHold(borrowed, t, [])))
                            incoming.Add(id);
                        if (borrowed.ContainsReference(context))
                            incoming.UnionWith(state.LoansOf(Loans[id].Place.Local));
                    }
                }
            }
            Store(state, call.Destination, incoming);
        }

        /// <summary>What the references in <paramref name="type"/> point to; null when a type parameter hides it.</summary>
        private List<IrType>? Pointees(IrType type, HashSet<StructType> visiting)
        {
            switch (type)
            {
                case ReferenceType reference:
                    return [reference.Pointee];
                case GenericParamType:
                    return type.ContainsReference(context) ? null : [];
                case StructType st when visiting.Add(st):
                {
                    var all = new List<IrType>();
                    foreach (var field in module.Structs[st.Id].Fields)
                    {
                        if (Pointees(field.Substitute(st.Arguments), visiting) is not { } inner)
                            return null;
                        all.AddRange(inner);
                    }
                    return all;
                }
                case ArrayType array:
                    return Pointees(array.Element, visiting);
                case FixedArrayType inline:
                    return Pointees(inline.Element, visiting);
                case OptionalType optional:
                    return Pointees(optional.Inner, visiting);
                case BoxType box:
                    return Pointees(box.Pointee, visiting);
                case GcType gc:
                    return Pointees(gc.Pointee, visiting);
                default:
                    return [];
            }
        }

        /// <summary>Whether a place of type <paramref name="container"/> may store a <paramref name="target"/> inside it (so a reference may point there).</summary>
        private bool MayHold(IrType container, IrType target, HashSet<StructType> visiting)
        {
            if (container == target || container.IsGeneric() || target.IsGeneric())
                return true;
            if (target is ArrayType wanted && (container is ArrayType sameArray && sameArray.Element == wanted.Element
                || container is FixedArrayType sameInline && sameInline.Element == wanted.Element))
                return true;
            return container switch
            {
                StructType st when visiting.Add(st) => module.Structs[st.Id].Fields.Any(f => MayHold(f.Substitute(st.Arguments), target, visiting)),
                ArrayType array => MayHold(array.Element, target, visiting),
                FixedArrayType inline => MayHold(inline.Element, target, visiting),
                OptionalType optional => MayHold(optional.Inner, target, visiting),
                BoxType box => MayHold(box.Pointee, target, visiting),
                GcType gc => MayHold(gc.Pointee, target, visiting),
                _ => false,
            };
        }

        private IEnumerable<int> LoansFlowingFrom(LoanState state, Operand operand) => operand switch
        {
            CopyOperand { Place: var place } when function.TypeOf(place, module).ContainsReference(context) => state.LoansOf(place.Local),
            MoveOperand { Place: var place } when function.TypeOf(place, module).ContainsReference(context) => state.LoansOf(place.Local),
            _ => [],
        };

        /// <summary>Whole-local writes replace the held loans; partial writes add to them.</summary>
        private static void Store(LoanState state, Place target, HashSet<int> incoming)
        {
            if (target.IsLocal)
            {
                if (incoming.Count == 0)
                    state.Held.Remove(target.Local);
                else
                    state.Held[target.Local] = incoming;
            }
            else if (incoming.Count > 0)
            {
                if (!state.Held.TryGetValue(target.Local, out var existing))
                    state.Held[target.Local] = existing = [];
                existing.UnionWith(incoming);
            }
        }
    }
}

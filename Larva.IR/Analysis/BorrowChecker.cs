using System.Text.RegularExpressions;
using Larva.IR.Text;

namespace Larva.IR.Analysis
{
    /// <summary>
    /// Ownership and borrowing rules over the CFG IR, in the style of Rust's NLL borrow checker:
    /// <list type="bullet">
    /// <item>Initialization: no reads of moved, uninitialized, dead or dropped places (<see cref="MoveAnalysis"/>).</item>
    /// <item>Moves: only out of places reachable without <c>*</c> or <c>[i]</c>; <c>copy</c> only of Copy types.</item>
    /// <item>Mutability: writes and <c>&amp;mut</c> need a <c>mut</c> local or a path through <c>&amp;mut</c> only.</item>
    /// <item>Borrows: a loan is alive while any live local holds it (<see cref="LoanAnalysis"/> + <see cref="Liveness"/>);
    /// alive <c>&amp;mut</c> loans forbid any other access to the place, alive <c>&amp;</c> loans forbid writes.</item>
    /// <item>No returning references to the function's own locals.</item>
    /// </list>
    /// </summary>
    public static class BorrowChecker
    {
        /// <summary>Every function checked on its own, on as many threads as there are processors (errors in function order).</summary>
        public static IReadOnlyList<BorrowError> Check(IrModule module)
        {
            var results = new List<BorrowError>[module.Functions.Length];
            Parallel.For(0, module.Functions.Length, f => results[f] = module.Functions[f].IsDeclaration ? [] : Check(module.Functions[f], module).ToList());
            return [.. results.SelectMany(r => r)];
        }

        /// <summary>A function's borrow errors, each with the source of its statement when the frontend gave one.</summary>
        public static IReadOnlyList<BorrowError> Check(IrFunction function, IrModule module)
        {
            var errors = new FunctionChecker(function, module).Run();
            if (function.Positions is null || errors.Count == 0)
                return errors;
            // `at bb3[0]` in a message: the line of that statement, when known.
            string Lines(string message) => System.Text.RegularExpressions.Regex.Replace(message, @"at bb(\d+)\[(\d+)\]",
                m => function.PositionOf(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) is { } at ? $"on line {at.Line}" : m.Value);
            return [.. errors.Select(e => e with { Source = function.PositionOf(e.At.Block, e.At.Index), Message = Lines(e.Message) })];
        }

        private enum Access : byte
        {
            Read,
            Move,
            BorrowShared,
            BorrowMut,
            Write,
            StorageDead,
            Drop,
        }

        private sealed class FunctionChecker(IrFunction function, IrModule module)
        {
            private readonly TypeContext context = TypeContext.For(function, module);
            private readonly MoveAnalysis moves = new(function, module);
            private readonly LoanAnalysis loans = new(function, module);
            private readonly Liveness liveness = Liveness.Compute(function);
            private readonly List<BorrowError> errors = [];
            private readonly HashSet<(Location, BorrowErrorKind, string)> reported = [];

            public List<BorrowError> Run()
            {
                var moveEntries = Dataflow.Forward(function, moves);
                var loanEntries = Dataflow.Forward(function, loans);

                for (int b = 0; b < function.Blocks.Length; b++)
                {
                    if (moveEntries[b] is not { } moveEntry || loanEntries[b] is not { } loanEntry)
                        continue; // unreachable block

                    var moveState = moves.Clone(moveEntry);
                    var loanState = loans.Clone(loanEntry);
                    var block = function.Blocks[b];
                    for (int i = 0; i < block.Statements.Length; i++)
                    {
                        Location location = new(b, i);
                        CheckStatement(block.Statements[i], location, moveState, loanState);
                        moves.Statement(moveState, block.Statements[i], location);
                        loans.Statement(loanState, block.Statements[i], location);
                    }

                    Location end = new(b, block.Statements.Length);
                    CheckTerminator(block.Terminator, end, moveState, loanState);
                }
                return errors;
            }

            // ── Per-point checks ───────────────────────────────────

            private void CheckStatement(Statement statement, Location location, MoveState moveState, LoanState loanState)
            {
                var active = ActiveLoans(location, loanState);
                switch (statement)
                {
                    case AssignStatement assign:
                        if (assign.Value is RefRvalue reference)
                        {
                            CheckAccess(reference.Place, reference.Mutable ? Access.BorrowMut : Access.BorrowShared, location, moveState, active);
                            if (!reference.Mutable && !function.Unsafe && IntoMutableObject(reference.Place) && !HandedToCall(assign.Target, location))
                                Report(location, BorrowErrorKind.MutationThroughGc,
                                    $"cannot borrow {Describe(reference.Place)}: fields of objects can change through other handles, so they are read by value, never borrowed");
                        }
                        else if (assign.Value is LenRvalue len)
                            CheckAccess(len.Array, Access.Read, location, moveState, active);
                        else if (assign.Value is IsNullRvalue test)
                            CheckAccess(test.Value, Access.Read, location, moveState, active);
                        else
                            CheckOperands(MoveAnalysis.Operands(assign.Value), location, moveState, active);
                        CheckAccess(assign.Target, Access.Write, location, moveState, active);
                        break;

                    case StorageDeadStatement dead:
                        CheckAccess(new Place(dead.Local), Access.StorageDead, location, moveState, active);
                        break;

                    case FreeStatement free:
                        CheckAccess(free.Pointer, Access.Read, location, moveState, active);
                        break;

                    case StoreGlobalStatement store:
                        CheckOperands([store.Value], location, moveState, active);
                        break;
                }
            }

            private void CheckTerminator(Terminator terminator, Location location, MoveState moveState, LoanState loanState)
            {
                var active = ActiveLoans(location, loanState);
                switch (terminator)
                {
                    case IfTerminator branch:
                        CheckOperands([branch.Condition], location, moveState, active);
                        break;
                    case SwitchTerminator @switch:
                        CheckOperands([@switch.Value], location, moveState, active);
                        break;
                    case CallTerminator call:
                        CheckOperands([.. call.Operands], location, moveState, active);
                        CheckAccess(call.Destination, Access.Write, location, moveState, active);
                        break;
                    case DropTerminator drop:
                        CheckAccess(drop.Place, Access.Drop, location, moveState, active);
                        break;
                    case ThrowTerminator @throw:
                        CheckOperands([@throw.Value], location, moveState, active);
                        // The exception may leave the function: it must not borrow this frame.
                        if (@throw.Value is CopyOperand or MoveOperand)
                        {
                            var thrown = @throw.Value is CopyOperand c ? c.Place : ((MoveOperand)@throw.Value).Place;
                            foreach (int id in loanState.LoansOf(thrown.Local))
                            {
                                var loan = loans.Loans[id];
                                if (!loan.Place.IsBehindNonOwningPointer(function, module))
                                    Report(location, BorrowErrorKind.ReturnsLocalReference,
                                        $"throws a reference to local {Describe(loan.Place)} (borrowed at {loan.At})");
                            }
                        }
                        break;
                    case ReturnTerminator:
                        CheckAccess(FunctionBuilder.ReturnPlace, Access.Read, location, moveState, active);
                        foreach (int id in loanState.LoansOf(0))
                        {
                            var loan = loans.Loans[id];
                            if (!loan.Place.IsBehindNonOwningPointer(function, module))
                                Report(location, BorrowErrorKind.ReturnsLocalReference,
                                    $"returns a reference to local {Describe(loan.Place)} (borrowed at {loan.At})");
                        }
                        break;
                }
            }

            private void CheckOperands(IEnumerable<Operand> operands, Location location, MoveState moveState, IReadOnlyList<Loan> active)
            {
                foreach (var operand in operands)
                {
                    switch (operand)
                    {
                        case CopyOperand { Place: var place }:
                            if (!function.TypeOf(place, module).IsCopy(context))
                                Report(location, BorrowErrorKind.CopyOfNonCopy,
                                    $"cannot copy {Describe(place)} of non-Copy type `{IrPrinter.FormatType(function.TypeOf(place, module), module, function.Generics)}`; use `move`");
                            CheckAccess(place, Access.Read, location, moveState, active);
                            break;
                        case MoveOperand { Place: var place }:
                            CheckAccess(place, function.TypeOf(place, module).IsCopy(context) ? Access.Read : Access.Move, location, moveState, active);
                            break;
                    }
                }
            }

            private void CheckAccess(Place place, Access access, Location location, MoveState moveState, IReadOnlyList<Loan> active)
            {
                // Evaluating the place reads every reference it goes through and every index local.
                for (int j = 0; j < place.Projections.Length; j++)
                {
                    if (place.Projections[j] is DerefProjection)
                        CheckInitialized(new Place(place.Local, place.Projections[..j]), location, moveState);
                }
                foreach (int index in place.IndexLocals())
                    CheckInitialized(new Place(index), location, moveState);

                if (access is Access.Read or Access.Move or Access.BorrowShared or Access.BorrowMut)
                    CheckInitialized(place, location, moveState);

                if (access == Access.Move)
                {
                    if (place.HasDeref())
                        Report(location, BorrowErrorKind.MoveOutOfReference, $"cannot move out of {Describe(place)}, which is behind a reference");
                    else if (place.HasIndex())
                        Report(location, BorrowErrorKind.MoveOutOfIndex, $"cannot move out of index of {Describe(new Place(place.Local))}");
                }

                if (access is Access.Write or Access.BorrowMut)
                    CheckMutability(place, access, location, moveState);

                foreach (var loan in active)
                    CheckConflict(place, access, loan, location);
            }

            private void CheckInitialized(Place place, Location location, MoveState moveState)
            {
                // Unit values are zero-sized: there is nothing to initialize.
                if (moveState.Find(place) is not { } origin || function.TypeOf(place, module).IsUnit)
                    return;

                var (kind, message) = origin.Kind switch
                {
                    UninitKind.Moved => (BorrowErrorKind.UseOfMoved, $"use of moved value {Describe(place)} (moved at {origin.At})"),
                    UninitKind.StorageDead => (BorrowErrorKind.UseAfterFree, $"use of {Describe(place)} after its storage ended at {origin.At}"),
                    UninitKind.Dropped => (BorrowErrorKind.UseAfterFree, $"use of {Describe(place)} after it was dropped at {origin.At}"),
                    _ => (BorrowErrorKind.UseOfUninitialized, $"use of possibly-uninitialized {Describe(place)}"),
                };
                Report(location, kind, message);
            }

            /// <summary>Whether <paramref name="place"/> is inside an object reached through a <c>gc</c> handle of a mutable struct.</summary>
            /// <summary>
            /// Whether a shared borrow (into <paramref name="target"/>, a plain local) is moved into the call that ends its
            /// block: it lives only for that call, like reading the field (how generic code boxes a field of type T).
            /// </summary>
            private bool HandedToCall(Place target, Location location) =>
                target.Projections.Length == 0 && function.Blocks[location.Block].Terminator is CallTerminator call
                && call.Arguments.Any(a => ((a as MoveOperand)?.Place ?? (a as CopyOperand)?.Place) is { Projections.Length: 0 } passed && passed.Local == target.Local);

            private bool IntoMutableObject(Place place)
            {
                for (int j = 0; j < place.Projections.Length; j++)
                {
                    if (place.Projections[j] is DerefProjection
                        && function.TypeOf(new Place(place.Local, place.Projections[..j]), module).Unoptional() is GcType { Pointee: StructType st }
                        && module.Structs[st.Id].Mutable)
                        return true;
                }
                return false;
            }

            private void CheckMutability(Place place, Access access, Location location, MoveState moveState)
            {
                // Through &mut / *mut / gc (unsafe) the pointer decides; through box the owner's mutability still applies.
                bool throughPointer = false;
                string verb = access == Access.Write ? "assign to" : "borrow as mutable";
                for (int j = 0; j < place.Projections.Length; j++)
                {
                    if (place.Projections[j] is not DerefProjection)
                        continue;
                    switch (function.TypeOf(new Place(place.Local, place.Projections[..j]), module).Unoptional())
                    {
                        case ReferenceType { Mutable: false }:
                            Report(location, BorrowErrorKind.MutationThroughSharedReference, $"cannot {verb} {Describe(place)}, which is behind a `&` reference");
                            return;
                        case PointerType { Mutable: false }:
                            Report(location, BorrowErrorKind.MutationThroughSharedReference, $"cannot {verb} {Describe(place)}, which is behind a `*` (const) pointer");
                            return;
                        case GcType { Pointee: StructType st } when !function.Unsafe && module.Structs[st.Id].Mutable:
                            if (access == Access.BorrowMut)
                            {
                                Report(location, BorrowErrorKind.MutationThroughGc,
                                    $"cannot borrow {Describe(place)} as mutable: fields of objects are read and written by value, never borrowed");
                                return;
                            }
                            throughPointer = true;
                            break;
                        case GcType when !function.Unsafe:
                            Report(location, BorrowErrorKind.MutationThroughGc,
                                $"cannot {verb} {Describe(place)}: `gc` objects are immutable outside an `unsafe fn`");
                            return;
                        case ReferenceType or PointerType or GcType:
                            throughPointer = true;
                            break;
                    }
                }
                if (throughPointer || function.Locals[place.Local].Mutable)
                    return;

                if (access == Access.BorrowMut)
                    Report(location, BorrowErrorKind.BorrowMutOfImmutable,
                        $"cannot borrow {Describe(place)} as mutable, as {Describe(new Place(place.Local))} is not declared as mutable");
                else if (moveState.Find(place) is null) // writing an uninitialized place is its initialization
                    Report(location, BorrowErrorKind.AssignToImmutable, place.IsLocal
                        ? $"cannot assign twice to immutable variable {Describe(place)}"
                        : $"cannot assign to {Describe(place)}, as {Describe(new Place(place.Local))} is not declared as mutable");
            }

            private void CheckConflict(Place place, Access access, Loan loan, Location location)
            {
                if (access == Access.StorageDead)
                {
                    if (loan.Place.Local == place.Local && !loan.Place.IsBehindNonOwningPointer(function, module))
                        Report(location, BorrowErrorKind.DroppedWhileBorrowed,
                            $"{Describe(place)} does not live long enough: still borrowed (borrow at {loan.At})");
                    return;
                }

                if (!loan.Place.Overlaps(place))
                    return;

                // Overwriting a reference does not touch the memory a reborrow through it points at.
                if (access == Access.Write && place.IsPrefixOf(loan.Place)
                    && loan.Place.Projections[place.Projections.Length..].Any(p => p is DerefProjection))
                    return;

                bool readOnly = access is Access.Read or Access.BorrowShared;
                if (readOnly && !loan.Mutable)
                    return;

                string borrow = $"{(loan.Mutable ? "mutable" : "shared")} borrow of {Describe(loan.Place)} at {loan.At}";
                var (kind, message) = access switch
                {
                    Access.Read => (BorrowErrorKind.ConflictingBorrow, $"cannot use {Describe(place)} while it is mutably borrowed ({borrow})"),
                    Access.BorrowShared => (BorrowErrorKind.ConflictingBorrow, $"cannot borrow {Describe(place)} as shared while it is mutably borrowed ({borrow})"),
                    Access.BorrowMut => (BorrowErrorKind.ConflictingBorrow, $"cannot borrow {Describe(place)} as mutable while it is borrowed ({borrow})"),
                    Access.Write => (BorrowErrorKind.ConflictingBorrow, $"cannot assign to {Describe(place)} while it is borrowed ({borrow})"),
                    Access.Move => (BorrowErrorKind.ConflictingBorrow, $"cannot move out of {Describe(place)} while it is borrowed ({borrow})"),
                    _ => (BorrowErrorKind.DroppedWhileBorrowed, $"{Describe(place)} dropped while still borrowed ({borrow})"),
                };
                Report(location, kind, message);
            }

            // ── Helpers ────────────────────────────────────────────

            /// <summary>Loans held by locals that are live before <paramref name="location"/>.</summary>
            private IReadOnlyList<Loan> ActiveLoans(Location location, LoanState state) =>
                [.. liveness.LiveBefore(location)
                    .SelectMany(state.LoansOf)
                    .Distinct()
                    .Order()
                    .Select(id => loans.Loans[id])];

            /// <summary>Place text with the local's source name when it has one, e.g. <c>`pt.1`</c>.</summary>
            private string Describe(Place place)
            {
                string text = IrPrinter.Format(place);
                return function.Locals[place.Local].Name is { } name
                    ? $"`{Regex.Replace(text, $@"\b_{place.Local}\b", name)}`"
                    : $"`{text}`";
            }

            private void Report(Location location, BorrowErrorKind kind, string message)
            {
                if (reported.Add((location, kind, message)))
                    errors.Add(new BorrowError(function.Name, location, kind, message));
            }
        }
    }
}

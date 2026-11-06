namespace Larva.IR.Analysis
{
    public enum BorrowErrorKind : byte
    {
        /// <summary>Reading a value that was moved out.</summary>
        UseOfMoved,
        /// <summary>Reading a place that may never have been written.</summary>
        UseOfUninitialized,
        /// <summary>Reading a local after <c>StorageDead</c> or a place after <c>drop</c>.</summary>
        UseAfterFree,
        /// <summary><c>copy</c> of a type that is not Copy (structs, arrays, <c>&amp;mut</c>).</summary>
        CopyOfNonCopy,
        /// <summary><c>move</c> of a non-Copy value from behind a reference.</summary>
        MoveOutOfReference,
        /// <summary><c>move</c> of a non-Copy value out of an array element.</summary>
        MoveOutOfIndex,
        /// <summary>Second assignment to (or field assignment of) an immutable local.</summary>
        AssignToImmutable,
        /// <summary>Writing or <c>&amp;mut</c>-borrowing through a shared reference or a <c>*T</c> pointer.</summary>
        MutationThroughSharedReference,
        /// <summary>Writing or <c>&amp;mut</c>-borrowing inside a <c>gc</c> object outside an <c>unsafe</c> function.</summary>
        MutationThroughGc,
        /// <summary><c>&amp;mut</c> of a local not declared mutable.</summary>
        BorrowMutOfImmutable,
        /// <summary>Using a place while a conflicting borrow of it is alive.</summary>
        ConflictingBorrow,
        /// <summary>Ending a local's storage or dropping a place while it is still borrowed.</summary>
        DroppedWhileBorrowed,
        /// <summary>Returning a reference to a local of the returning function.</summary>
        ReturnsLocalReference,
    }

    public enum Severity : byte
    {
        Error,
        Warning,
    }

    public sealed record BorrowError(string Function, Location At, BorrowErrorKind Kind, string Message)
    {
        public Severity Severity { get; init; } = Severity.Error;

        /// <summary>Where in the source the statement came from, when the frontend said (<see cref="IrFunction.Positions"/>).</summary>
        public SourcePosition? Source { get; init; }

        public override string ToString() => Source is { } source
            ? $"{source}: {Severity.ToString().ToLowerInvariant()}[{Kind}]: {Message}"
            : $"{Severity.ToString().ToLowerInvariant()}[{Kind}]: {Message}\n   --> {Function} {At}";
    }
}

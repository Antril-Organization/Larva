namespace Larva.IR
{
    // ── Statements: run in order inside a block ────────────────────

    public abstract record Statement
    {
        public sealed override string ToString() => Text.IrPrinter.Format(this);
    }

    /// <summary><c>place = rvalue</c></summary>
    public sealed record AssignStatement(Place Target, Rvalue Value) : Statement;

    /// <summary>Marks the start of a local's storage (scope entry). No IL is emitted.</summary>
    public sealed record StorageLiveStatement(int Local) : Statement;

    /// <summary>Marks the end of a local's storage (scope exit). No IL is emitted.</summary>
    public sealed record StorageDeadStatement(int Local) : Statement;

    public sealed record NopStatement : Statement;

    /// <summary><c>free(p)</c>: releases the allocation behind a <c>*mut T</c> from <c>alloc</c>. Requires <c>unsafe</c>.</summary>
    public sealed record FreeStatement(Place Pointer) : Statement;

    /// <summary><c>global#N = value</c>: replaces the whole value of a global.</summary>
    public sealed record StoreGlobalStatement(int Global, Operand Value) : Statement
    {
        /// <summary>The instantiation of a generic global.</summary>
        public IrType[] TypeArguments { get; init; } = [];
    }

    /// <summary>
    /// <c>region_enter(rN)</c>: opens arena region <see cref="Region"/>; <c>gc</c> allocations go there until the matching
    /// <see cref="RegionExitStatement"/>, which frees them all. Regions nest and must be closed on every path. Requires a
    /// GC mode with <see cref="GcCapabilities.Regions"/>; values from a region may not outlive it (see <c>RegionChecker</c>).
    /// </summary>
    public sealed record RegionEnterStatement(int Region) : Statement;

    /// <summary><c>region_exit(rN)</c>: closes the innermost region, which must be <see cref="Region"/>.</summary>
    public sealed record RegionExitStatement(int Region) : Statement;

    // ── Terminators: end a block and pick the successor(s) ──────────

    public abstract record Terminator
    {
        /// <summary>Blocks control may continue to.</summary>
        public abstract IEnumerable<int> Successors { get; }

        public sealed override string ToString() => Text.IrPrinter.Format(this);
    }

    public sealed record GotoTerminator(int Target) : Terminator
    {
        public override IEnumerable<int> Successors => [Target];
    }

    public sealed record IfTerminator(Operand Condition, int Then, int Else) : Terminator
    {
        public override IEnumerable<int> Successors => [Then, Else];
    }

    /// <summary>Jumps to <c>Targets[value]</c> for <c>0 &lt;= value &lt; Targets.Length</c>, otherwise to <see cref="Otherwise"/>.</summary>
    public sealed record SwitchTerminator(Operand Value, int[] Targets, int Otherwise) : Terminator
    {
        public override IEnumerable<int> Successors => [.. Targets, Otherwise];
    }

    /// <summary>Returns the value in local <c>_0</c>.</summary>
    public sealed record ReturnTerminator : Terminator
    {
        public override IEnumerable<int> Successors => [];
    }

    /// <summary>
    /// <c>catch T -> bbN</c> (or <c>catch _</c> when <see cref="Type"/> is <c>null</c>): where an exception continues when
    /// its type matches. The handler block starts with the exception available through <see cref="CaughtRvalue"/>.
    /// </summary>
    public sealed record CatchClause(IrType? Type, int Block);

    /// <summary>A terminator that may raise an exception, with the clauses that handle it locally (tried in order).</summary>
    public interface IUnwinding
    {
        CatchClause[] Handlers { get; }
    }

    /// <summary><c>destination = call fn::&lt;type args&gt;(args) -> target unwind [catch T -> bbN, …]</c></summary>
    public sealed record CallTerminator(int Function, Operand[] Arguments, Place Destination, int Target) : Terminator, IUnwinding
    {
        /// <summary>Instantiation of the callee's generic parameters (empty for non-generic callees).</summary>
        public IrType[] TypeArguments { get; init; } = [];

        /// <summary>Where an exception escaping the callee is caught in this function; unmatched ones propagate to the caller.</summary>
        public CatchClause[] Handlers { get; init; } = [];

        /// <summary>
        /// A call through a requirement of this function's generic parameters (<see cref="Function"/> is -1): the
        /// monomorphizer replaces it with the built-in operation or the implementing function of each instantiation.
        /// </summary>
        public RequirementRef? Required { get; init; }

        /// <summary>An indirect call through a <see cref="FunctionType"/> value (<see cref="Function"/> is -1).</summary>
        public Operand? Callee { get; init; }

        /// <summary>Every operand the call reads: the arguments, and the callee of an indirect call.</summary>
        public IEnumerable<Operand> Operands => Callee is null ? Arguments : [.. Arguments, Callee];

        public override IEnumerable<int> Successors => [Target, .. Handlers.Select(h => h.Block)];
    }

    /// <summary><c>throw value</c>: raises <see cref="Value"/> as an exception (moved), caught by <see cref="Handlers"/> or a caller.</summary>
    public sealed record ThrowTerminator(Operand Value) : Terminator, IUnwinding
    {
        public CatchClause[] Handlers { get; init; } = [];

        public override IEnumerable<int> Successors => Handlers.Select(h => h.Block);
    }

    /// <summary><c>resume</c>: re-raises the exception being handled (inside a handler).</summary>
    public sealed record ResumeTerminator : Terminator, IUnwinding
    {
        public CatchClause[] Handlers { get; init; } = [];

        public override IEnumerable<int> Successors => Handlers.Select(h => h.Block);
    }

    /// <summary>Ends the lifetime of the value in <see cref="Place"/>. No IL is emitted yet (future destructors).</summary>
    public sealed record DropTerminator(Place Place, int Target) : Terminator
    {
        public override IEnumerable<int> Successors => [Target];
    }

    public sealed record UnreachableTerminator : Terminator
    {
        public override IEnumerable<int> Successors => [];
    }
}

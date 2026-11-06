namespace Larva.Hir
{
    /// <summary>A local of a body: parameters are not locals (see <see cref="ParamExpr"/>); lambda parameters are.</summary>
    public sealed record HirLocal(string Name, HirType Type, bool Mutable = true);

    /// <summary>
    /// A method body: its locals and a block. Everything is typed and resolved; lowering (Larva) decides how it runs:
    /// dispatch, closures, state machines, patterns, cleanup.
    /// </summary>
    public sealed record HirBody(HirLocal[] Locals, BlockStmt Block);

    // ── Statements ─────────────────────────────────────────

    public abstract record Stmt
    {
        public SourceLocation At { get; init; } = SourceLocation.None;
    }

    public sealed record BlockStmt(Stmt[] Statements) : Stmt;

    /// <summary>Declares (makes live) a local, with its first value.</summary>
    public sealed record LocalStmt(int Local, Expr? Value) : Stmt;

    public sealed record ExprStmt(Expr Expression) : Stmt;

    public sealed record IfStmt(Expr Condition, Stmt Then, Stmt? Else) : Stmt;

    /// <summary>Loops carry a label (-1: none) that <see cref="BreakStmt"/> and <see cref="ContinueStmt"/> can name.</summary>
    public sealed record WhileStmt(Expr Condition, Stmt Body, int Label = -1) : Stmt;

    public sealed record DoWhileStmt(Stmt Body, Expr Condition, int Label = -1) : Stmt;

    public sealed record ForStmt(Stmt[] Initializers, Expr? Condition, Expr[] Steps, Stmt Body, int Label = -1) : Stmt;

    /// <summary>
    /// Each element of <c>Collection</c> into <c>Local</c>: arrays and ranges directly, anything else through the core
    /// enumerable contract (<c>[Core("enumerable")]</c>).
    /// </summary>
    public sealed record ForeachStmt(int Local, Expr Collection, Stmt Body, int Label = -1) : Stmt;

    public sealed record SwitchSection(Pattern[] Patterns, Expr? Guard, Stmt Body);

    /// <summary>The first section with a matching pattern (and a true guard) runs; none: nothing.</summary>
    public sealed record SwitchStmt(Expr Value, SwitchSection[] Sections, int Label = -1) : Stmt;

    /// <summary>Leaves the loop or switch labelled <c>Label</c> (-1: the innermost).</summary>
    public sealed record BreakStmt(int Label = -1) : Stmt;

    public sealed record ContinueStmt(int Label = -1) : Stmt;

    public sealed record ReturnStmt(Expr? Value) : Stmt;

    /// <summary>Throws <c>Value</c>; null rethrows the exception being handled.</summary>
    public sealed record ThrowStmt(Expr? Value) : Stmt;

    /// <summary>Catches exceptions of <c>Type</c> (null: any), binding it to <c>Local</c> (-1: none) when <c>Filter</c> holds.</summary>
    public sealed record CatchClause(HirType? Type, int Local, Expr? Filter, BlockStmt Body);

    public sealed record TryStmt(BlockStmt Body, CatchClause[] Catches, BlockStmt? Finally) : Stmt;

    /// <summary>Runs <c>Body</c>, then disposes <c>Local</c> (initialized to the resource) on every way out.</summary>
    public sealed record UsingStmt(int Local, Expr Resource, Stmt Body) : Stmt;

    /// <summary>Frees an object now (manual and arena modes).</summary>
    public sealed record DeleteStmt(Expr Value) : Stmt;

    /// <summary>An arena region: objects made inside are freed together at its end.</summary>
    public sealed record RegionStmt(BlockStmt Body) : Stmt;

    public sealed record UnsafeStmt(BlockStmt Body) : Stmt;

    /// <summary>Iterators: produces <c>Value</c>; null ends the sequence (<c>yield break</c>).</summary>
    public sealed record YieldStmt(Expr? Value) : Stmt;

    /// <summary>
    /// <c>checked { … }</c> / <c>unchecked { … }</c>: in Body (not in the functions it calls), integer arithmetic and
    /// numeric conversions that overflow fail (Checked) or wrap.
    /// </summary>
    public sealed record CheckedStmt(BlockStmt Body, bool Checked) : Stmt;

    // ── Expressions ────────────────────────────────────────

    public abstract record Expr(HirType Type);

    public sealed record LiteralExpr(HirConstant Value, HirType Type) : Expr(Type);

    public sealed record LocalExpr(int Local, HirType Type) : Expr(Type);

    public sealed record ParamExpr(int Parameter, HirType Type) : Expr(Type);

    public sealed record ThisExpr(HirType Type) : Expr(Type);

    /// <summary>A field; <c>Instance</c> null for a static field.</summary>
    /// <summary>
    /// A field: of <c>Instance</c>, or static (<c>Instance</c> null). A static field of a generic type names the instantiation
    /// in <c>TypeArguments</c> (<c>Box&lt;i32&gt;.count</c>: one global per instantiation); null inside the type itself.
    /// </summary>
    public sealed record FieldExpr(Expr? Instance, MemberToken Field, HirType Type, HirType[]? TypeArguments = null) : Expr(Type);

    /// <summary>A property or indexer: reading calls the getter, assigning calls the setter.</summary>
    public sealed record PropertyExpr(Expr? Instance, MemberToken Property, Expr[] Indices, HirType Type) : Expr(Type);

    public sealed record ElementExpr(Expr Array, Expr[] Indices, HirType Type) : Expr(Type);

    /// <summary>
    /// A call. Virtual and interface methods dispatch on <c>Receiver</c> unless <c>NonVirtual</c> (<c>base.M()</c>);
    /// <c>Receiver</c> is null for static methods.
    /// </summary>
    public sealed record CallExpr(Expr? Receiver, MemberToken Method, HirType[] TypeArguments, Expr[] Arguments, HirType Type) : Expr(Type)
    {
        public bool NonVirtual { get; init; }
    }

    /// <summary>Calls a function value.</summary>
    public sealed record InvokeExpr(Expr Function, Expr[] Arguments, HirType Type) : Expr(Type);

    /// <summary>A field (or, with <c>Kind</c> Property, a property) set to a value.</summary>
    public sealed record MemberInit(MemberToken Member, Expr Value, MemberKind Kind = MemberKind.Field);

    /// <summary><c>new T(args) { member = value }</c>: <c>Constructor</c> null for a struct's zero value.</summary>
    public sealed record NewExpr(MemberToken? Constructor, Expr[] Arguments, MemberInit[] Initializers, HirType Type) : Expr(Type);

    /// <summary><c>new T[n]</c>, <c>new T[a, b]</c>: one length per dimension.</summary>
    public sealed record NewArrayExpr(HirType Element, Expr[] Lengths, HirType Type) : Expr(Type);

    public sealed record ArrayLiteralExpr(HirType Element, Expr[] Items, HirType Type) : Expr(Type);

    /// <summary>The bytes of the assembly's resource <see cref="Name"/> (<see cref="HirAssembly.Resources"/>), as a new <c>u8[]</c>.</summary>
    public sealed record ResourceExpr(string Name, HirType Type) : Expr(Type);

    public enum UnaryOp : byte { Negate, Plus, Not, BitNot, PreIncrement, PreDecrement, PostIncrement, PostDecrement }

    public enum BinaryOp : byte
    {
        Add, Subtract, Multiply, Divide, Remainder, Power,
        BitAnd, BitOr, BitXor, ShiftLeft, ShiftRight,
        Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual,
        /// <summary>Short-circuit.</summary>
        And, Or,
    }

    /// <summary>A built-in operator on primitives (user operators are calls of their methods).</summary>
    public sealed record UnaryExpr(UnaryOp Op, Expr Operand, HirType Type) : Expr(Type);

    public sealed record BinaryExpr(BinaryOp Op, Expr Left, Expr Right, HirType Type) : Expr(Type);

    /// <summary><c>Target = Value</c>, or <c>Target op= Value</c> when <c>Compound</c> is set (built-in operator).</summary>
    public sealed record AssignExpr(Expr Target, Expr Value, HirType Type) : Expr(Type)
    {
        public BinaryOp? Compound { get; init; }
    }

    public enum ConversionKind : byte
    {
        /// <summary>Between primitives (widening, narrowing, int and float).</summary>
        Numeric,
        /// <summary>To a base class or interface.</summary>
        Upcast,
        /// <summary>To a derived class or another interface: checked, throws when it does not hold.</summary>
        Downcast,
        Box, Unbox,
        /// <summary>A user conversion: <see cref="ConvertExpr.Method"/>.</summary>
        User,
        /// <summary><c>T</c> to <c>T?</c>.</summary>
        Wrap,
        /// <summary><c>T?</c> to <c>T</c>: throws on null.</summary>
        Unwrap,
        /// <summary>Between pointers, or pointers and integers.</summary>
        Pointer,
        /// <summary>An enum to or from its underlying type.</summary>
        Enum,
        /// <summary>A method group or lambda to a func type.</summary>
        Function,
    }

    public sealed record ConvertExpr(Expr Value, ConversionKind Kind, HirType Type) : Expr(Type)
    {
        public MemberToken? Method { get; init; }
    }

    /// <summary><c>Value is Pattern</c> (bool).</summary>
    public sealed record IsExpr(Expr Value, Pattern Pattern, HirType Type) : Expr(Type);

    /// <summary><c>Value as T</c>: null when it is not one.</summary>
    public sealed record AsExpr(Expr Value, HirType Type) : Expr(Type);

    public sealed record SwitchArm(Pattern Pattern, Expr? Guard, Expr Value);

    /// <summary>The first arm that matches; none throws.</summary>
    public sealed record SwitchExpr(Expr Value, SwitchArm[] Arms, HirType Type) : Expr(Type);

    public sealed record ConditionalExpr(Expr Condition, Expr Then, Expr Else, HirType Type) : Expr(Type);

    /// <summary><c>Left ?? Right</c>.</summary>
    public sealed record CoalesceExpr(Expr Left, Expr Right, HirType Type) : Expr(Type);

    /// <summary><c>Receiver?.…</c>: null when <c>Receiver</c> is, else <c>Access</c>, in which <see cref="ReceiverExpr"/> is it.</summary>
    public sealed record ConditionalAccessExpr(Expr Receiver, Expr Access, HirType Type) : Expr(Type);

    /// <summary>The non-null receiver inside <see cref="ConditionalAccessExpr.Access"/>.</summary>
    public sealed record ReceiverExpr(HirType Type) : Expr(Type);

    /// <summary>
    /// A lambda: its parameters and the locals it declares are locals of the enclosing body (<c>Parameters</c> lists them);
    /// it may use the enclosing locals, parameters and <c>this</c>: lowering finds what it captures.
    /// </summary>
    public sealed record LambdaExpr(int[] Parameters, Stmt Body, HirType Type) : Expr(Type)
    {
        public bool Async { get; init; }
    }

    /// <summary>A method as a function value (bound to <c>Target</c> for instance methods).</summary>
    public sealed record MethodRefExpr(Expr? Target, MemberToken Method, HirType[] TypeArguments, HirType Type) : Expr(Type);

    public sealed record AwaitExpr(Expr Task, HirType Type) : Expr(Type);

    /// <summary><c>checked(e)</c> / <c>unchecked(e)</c>: as <see cref="CheckedStmt"/>, for one expression.</summary>
    public sealed record CheckedExpr(Expr Value, bool Checked, HirType Type) : Expr(Type);

    public sealed record TypeOfExpr(HirType Operand, HirType Type) : Expr(Type);

    public sealed record SizeOfExpr(HirType Operand, HirType Type) : Expr(Type);

    /// <summary>How many elements an array has (of a one-dimensional array; <c>Type</c> is an integer type).</summary>
    public sealed record ArrayLengthExpr(Expr Array, HirType Type) : Expr(Type)
    {
        /// <summary>The length of this dimension (<c>GetLength(d)</c>); null: all the elements.</summary>
        public Expr? Dimension { get; init; }
    }

    public sealed record DefaultExpr(HirType Type) : Expr(Type);

    /// <summary><c>Value with { member = value }</c>: a copy of a record with some members changed.</summary>
    public sealed record WithExpr(Expr Value, MemberInit[] Changes, HirType Type) : Expr(Type);

    /// <summary><c>Low..High</c> (<c>..=</c> when inclusive); either end may be missing.</summary>
    public sealed record RangeExpr(Expr? Low, Expr? High, bool Inclusive, HirType Type) : Expr(Type);

    /// <summary>A string built from parts: literals and values, each with its format (null: the default).</summary>
    public sealed record InterpolatedExpr(InterpolationPart[] Parts, HirType Type) : Expr(Type);

    public sealed record InterpolationPart(Expr Value, string? Format = null, int Alignment = 0);

    /// <summary><c>&amp;x</c> / <c>&amp;mut x</c>: a borrow.</summary>
    public sealed record BorrowExpr(Expr Place, bool Mutable, HirType Type) : Expr(Type);

    /// <summary><c>&amp;x</c> in unsafe code: an address (a pointer).</summary>
    public sealed record AddressOfExpr(Expr Place, HirType Type) : Expr(Type);

    /// <summary><c>*p</c>: what a pointer or a reference points to.</summary>
    public sealed record DerefExpr(Expr Pointer, HirType Type) : Expr(Type);

    /// <summary>Statements, then a value: what frontends expand sugar into.</summary>
    public sealed record BlockExpr(Stmt[] Statements, Expr Value, HirType Type) : Expr(Type);

    // ── Patterns ───────────────────────────────────────────

    public abstract record Pattern;

    public sealed record DiscardPattern : Pattern;

    public sealed record ConstantPattern(HirConstant Value) : Pattern;

    public sealed record RelationalPattern(BinaryOp Op, HirConstant Value) : Pattern;

    public sealed record RangePattern(HirConstant Low, HirConstant High, bool Inclusive) : Pattern;

    /// <summary>The value is a <c>Type</c> (and not null).</summary>
    public sealed record TypePattern(HirType Type) : Pattern;

    /// <summary>Binds the value (converted to the local's type) to <c>Local</c> when <c>Inner</c> matches (null: always).</summary>
    public sealed record BindingPattern(int Local, Pattern? Inner) : Pattern;

    public sealed record AndPattern(Pattern Left, Pattern Right) : Pattern;

    public sealed record OrPattern(Pattern Left, Pattern Right) : Pattern;

    public sealed record NotPattern(Pattern Operand) : Pattern;

    public sealed record PropertySubpattern(MemberToken Member, Pattern Pattern, MemberKind Kind = MemberKind.Field);

    /// <summary><c>T { A: p, B: q }</c>: the value is a <c>Type</c> (null: as it is) whose members match.</summary>
    public sealed record PropertyPattern(HirType? Type, PropertySubpattern[] Members) : Pattern;

    /// <summary><c>T(p, q)</c>: matched through the type's deconstruction (records: their positional members).</summary>
    public sealed record PositionalPattern(HirType? Type, MemberToken? Deconstructor, Pattern[] Items) : Pattern;

    /// <summary>
    /// <c>[p, q, .. rest]</c>: <c>Slice</c> is where <c>..</c> stands among the items (0 to <c>Items.Length</c>; -1: none),
    /// and <c>SliceLocal</c> takes what it covers (-1: nothing).
    /// </summary>
    public sealed record ListPattern(Pattern[] Items, int Slice = -1, int SliceLocal = -1) : Pattern;
}

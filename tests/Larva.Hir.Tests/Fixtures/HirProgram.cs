using Larva.Hir;

namespace Larva.Hir.Tests;

/// <summary>
/// Builds HIR by hand for the lowering tests (what a frontend writes): a small DSL over the HIR records.
/// </summary>
internal sealed class HirProgram
{
    public readonly HirAssembly Assembly;
    public static readonly HirType I = HirType.I32;
    public static readonly HirType B = HirType.Bool;

    public HirProgram(string name) => Assembly = new HirAssembly { Name = name };

    public (TypeToken Token, TypeDef Def) Type(string name, TypeKind kind, TypeToken? baseType = null, TypeFlags flags = TypeFlags.None)
    {
        var def = new TypeDef { Namespace = Assembly.Name, Name = name, Kind = kind, Flags = flags, Base = baseType is { } b ? HirType.Named(b) : null };
        return (Assembly.Add(def), def);
    }

    public static HirType T(TypeToken token) => HirType.Named(token);

    public static MemberToken Field(TypeToken type, TypeDef def, string name, HirType fieldType, FieldFlags flags = FieldFlags.None)
    {
        def.Fields.Add(new FieldDef(name, fieldType) { Flags = flags, Access = Access.Public });
        return MemberToken.Def(type.Index, def.Fields.Count - 1);
    }

    /// <summary>A method; its body is filled later with <see cref="MethodBuilder"/>.</summary>
    public static MemberToken Method(TypeToken type, TypeDef def, string name, HirType result, MethodFlags flags, MethodKind kind = MethodKind.Normal, params Parameter[] parameters)
    {
        def.Methods.Add(new MethodDef(name, result, parameters) { Flags = flags, Kind = kind, Access = Access.Public });
        return MemberToken.Def(type.Index, def.Methods.Count - 1);
    }

    public static Parameter P(string name, HirType type) => new(name, type);
}

/// <summary>A method body under construction: locals, then statements.</summary>
internal sealed class MethodBuilder(MethodDef method)
{
    private readonly List<HirLocal> locals = [];
    private readonly List<Stmt> statements = [];

    public LocalExpr Local(string name, HirType type)
    {
        locals.Add(new HirLocal(name, type));
        return new LocalExpr(locals.Count - 1, type);
    }

    public MethodBuilder Do(params Stmt[] body)
    {
        statements.AddRange(body);
        return this;
    }

    public void Done() => method.Body = new HirBody([.. locals], new BlockStmt([.. statements]));
}

/// <summary>Expression and statement shorthands.</summary>
internal static class E
{
    public static readonly HirType I = HirType.I32;
    public static readonly HirType Bool = HirType.Bool;

    public static LiteralExpr Int(long v, HirType? type = null) => new(HirConstant.Int(v, ((type ?? I) as PrimitiveType)!.Kind), type ?? I);
    public static LiteralExpr True => new(new BoolConstant(true), Bool);
    public static LiteralExpr Null(HirType type) => new(new NullConstant(), type);
    public static ParamExpr Arg(int index, HirType? type = null) => new(index, type ?? I);
    public static ThisExpr This(HirType type) => new(type);
    public static BinaryExpr Add(Expr a, Expr b) => new(BinaryOp.Add, a, b, a.Type);
    public static BinaryExpr Sub(Expr a, Expr b) => new(BinaryOp.Subtract, a, b, a.Type);
    public static BinaryExpr Mul(Expr a, Expr b) => new(BinaryOp.Multiply, a, b, a.Type);
    public static BinaryExpr Rem(Expr a, Expr b) => new(BinaryOp.Remainder, a, b, a.Type);
    public static BinaryExpr Lt(Expr a, Expr b) => new(BinaryOp.Less, a, b, Bool);
    public static BinaryExpr Eq(Expr a, Expr b) => new(BinaryOp.Equal, a, b, Bool);
    public static BinaryExpr Ne(Expr a, Expr b) => new(BinaryOp.NotEqual, a, b, Bool);
    public static UnaryExpr Not(Expr a) => new(UnaryOp.Not, a, Bool);
    public static AssignExpr Set(Expr target, Expr value) => new(target, value, target.Type);
    public static AssignExpr AddTo(Expr target, Expr value) => new(target, value, target.Type) { Compound = BinaryOp.Add };
    public static AssignExpr MulTo(Expr target, Expr value) => new(target, value, target.Type) { Compound = BinaryOp.Multiply };
    public static UnaryExpr Inc(Expr target) => new(UnaryOp.PostIncrement, target, target.Type);
    public static FieldExpr F(Expr? instance, MemberToken field, HirType type) => new(instance, field, type);
    public static CallExpr Call(MemberToken method, HirType result, params Expr[] args) => new(null, method, [], args, result);
    public static CallExpr CallOn(Expr receiver, MemberToken method, HirType result, params Expr[] args) => new(receiver, method, [], args, result);
    public static NewExpr New(HirType type, MemberToken? ctor, params Expr[] args) => new(ctor, args, [], type);
    public static IsExpr Is(Expr value, HirType type) => new(value, new TypePattern(type), Bool);

    public static ExprStmt S(Expr e) => new(e);
    public static ReturnStmt Ret(Expr? e = null) => new(e);
    public static LocalStmt Let(LocalExpr local, Expr? value) => new(local.Local, value);
    public static BlockStmt Blk(params Stmt[] statements) => new(statements);
    public static IfStmt If(Expr condition, Stmt then, Stmt? otherwise = null) => new(condition, then, otherwise);
    public static WhileStmt While(Expr condition, params Stmt[] body) => new(condition, new BlockStmt(body));
    public static ThrowStmt Throw(Expr? value) => new(value);
    public static TryStmt Try(BlockStmt body, CatchClause[] catches, BlockStmt? fin = null) => new(body, catches, fin);
}

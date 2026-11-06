namespace Larva.Hir.Encoding
{
    /// <summary>
    /// The <c>.lva</c> file: <c>LVA</c> + a format version, a string table, a type table (every distinct type once, its
    /// parts before it), the metadata, then the method bodies. Bodies are read only when asked for, so referencing a large
    /// library costs only its metadata.
    /// </summary>
    public static class LvaFormat
    {
        internal static ReadOnlySpan<byte> Magic => "LVA"u8;
        public const int Version = 5;

        public static byte[] Write(HirAssembly assembly) => new LvaWriter(assembly).Write();

        /// <summary>Saved as a <c>.lva</c>, or as a library DLL when the path ends in <c>.dll</c> (<see cref="LvaContainer"/>).</summary>
        public static void Save(HirAssembly assembly, string path) =>
            File.WriteAllBytes(path, path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? LvaContainer.ToDll(Write(assembly)) : Write(assembly));

        /// <summary>Reads an assembly; method bodies are decoded the first time each one is used.</summary>
        public static HirAssembly Read(byte[] data) => LvaReader.Read(data);

        public static HirAssembly Load(string path) => Read(File.ReadAllBytes(path));
    }

    internal enum TypeTag : byte { Primitive, Named, Array, InlineArray, Nullable, Reference, Pointer, Func, NativeFunc, GenericParam, Pack }

    internal enum ConstantTag : byte { Integer, Float, Bool, Char, String, Null, Type, Enum, Array, EnumText }

    internal enum StmtTag : byte
    {
        Block, Local, Expr, If, While, DoWhile, For, Foreach, Switch, Break, Continue, Return, Throw, Try, Using, Delete,
        Region, Unsafe, Yield, Checked,
    }

    internal enum ExprTag : byte
    {
        Literal, Local, Param, This, Field, Property, Element, Call, Invoke, New, NewArray, ArrayLiteral, Unary, Binary,
        Assign, Convert, Is, As, Switch, Conditional, Coalesce, ConditionalAccess, Receiver, Lambda, MethodRef, Await,
        TypeOf, SizeOf, Default, With, Range, Interpolated, Borrow, AddressOf, Deref, Block, ArrayLength, Checked, Resource,
    }

    internal enum PatternTag : byte { Discard, Constant, Relational, Range, Type, Binding, And, Or, Not, Property, Positional, List }

    internal sealed class LvaWriter(HirAssembly assembly)
    {
        private readonly Dictionary<string, int> strings = [];
        private readonly List<string> stringList = [];
        private readonly Dictionary<HirType, int> types = [];
        private readonly ByteSink typeTable = new();
        private readonly ByteSink meta = new();
        private readonly ByteSink bodies = new();

        public byte[] Write()
        {
            Metadata(meta);
            var file = new ByteSink();
            file.Raw(LvaFormat.Magic);
            file.UInt(LvaFormat.Version);
            file.UInt(stringList.Count);
            foreach (var s in stringList)
                file.Utf8(s);
            file.UInt(types.Count);
            file.Raw(typeTable.Span);
            file.UInt(meta.Length);
            file.Raw(meta.Span);
            file.UInt(bodies.Length);
            file.Raw(bodies.Span);
            return file.Span.ToArray();
        }

        // ── Tables ─────────────────────────────────────────

        private int StringIndex(string value)
        {
            if (!strings.TryGetValue(value, out int index))
            {
                index = stringList.Count;
                strings[value] = index;
                stringList.Add(value);
            }
            return index;
        }

        private void Str(ByteSink sink, string value) => sink.UInt(StringIndex(value));

        private void OptStr(ByteSink sink, string? value) => sink.UInt(value is null ? 0 : StringIndex(value) + 1);

        private int TypeIndex(HirType type)
        {
            if (types.TryGetValue(type, out int index))
                return index;
            // Parts first: the reader builds each type from ones it already has.
            switch (type)
            {
                case NamedType n:
                    foreach (var a in n.Arguments) TypeIndex(a);
                    break;
                case ArrayType a: TypeIndex(a.Element); break;
                case InlineArrayType i: TypeIndex(i.Element); if (i.SizeOf is not null) TypeIndex(i.SizeOf); break;
                case NullableType n: TypeIndex(n.Inner); break;
                case ReferenceType r: TypeIndex(r.Target); break;
                case PointerType p: TypeIndex(p.Target); break;
                case FuncType f:
                    foreach (var p in f.Parameters) TypeIndex(p);
                    TypeIndex(f.Result);
                    break;
                case NativeFuncType f:
                    foreach (var p in f.Parameters) TypeIndex(p);
                    TypeIndex(f.Result);
                    break;
                case PackExpansionType e: TypeIndex(e.Pattern); break;
            }
            var t = typeTable;
            switch (type)
            {
                case PrimitiveType p:
                    t.Byte((int)TypeTag.Primitive); t.Byte((int)p.Kind);
                    break;
                case NamedType n:
                    t.Byte((int)TypeTag.Named); t.Int(n.Type.Value); Types(t, n.Arguments);
                    break;
                case ArrayType a:
                    t.Byte((int)TypeTag.Array); t.UInt(types[a.Element]); t.UInt(a.Rank);
                    break;
                case InlineArrayType i:
                    t.Byte((int)TypeTag.InlineArray); t.UInt(types[i.Element]); t.UInt(i.Count); t.Bool(i.SizeOf is not null);
                    if (i.SizeOf is not null)
                        t.UInt(types[i.SizeOf]);
                    break;
                case NullableType n:
                    t.Byte((int)TypeTag.Nullable); t.UInt(types[n.Inner]);
                    break;
                case ReferenceType r:
                    t.Byte((int)TypeTag.Reference); t.UInt(types[r.Target]); t.Bool(r.Mutable);
                    break;
                case PointerType p:
                    t.Byte((int)TypeTag.Pointer); t.UInt(types[p.Target]); t.Bool(p.Mutable);
                    break;
                case FuncType f:
                    t.Byte((int)TypeTag.Func); Types(t, f.Parameters); t.UInt(types[f.Result]);
                    break;
                case NativeFuncType f:
                    t.Byte((int)TypeTag.NativeFunc); Types(t, f.Parameters); t.UInt(types[f.Result]);
                    break;
                case GenericParamType g:
                    t.Byte((int)TypeTag.GenericParam); t.Byte((int)g.Owner); t.UInt(g.Index);
                    break;
                case PackExpansionType e:
                    t.Byte((int)TypeTag.Pack); t.UInt(types[e.Pattern]);
                    break;
                default:
                    throw new InvalidOperationException($"cannot write type {type}");
            }
            index = types.Count;
            types[type] = index;
            return index;
        }

        private void Types(ByteSink sink, IReadOnlyList<HirType> list)
        {
            sink.UInt(list.Count);
            foreach (var type in list)
                sink.UInt(types.TryGetValue(type, out int i) ? i : TypeIndex(type));
        }

        private void Type(ByteSink sink, HirType type) => sink.UInt(TypeIndex(type));

        private void OptType(ByteSink sink, HirType? type) => sink.UInt(type is null ? 0 : TypeIndex(type) + 1);

        private static void Ints(ByteSink sink, int[] values)
        {
            sink.UInt(values.Length);
            foreach (int v in values)
                sink.Int(v);
        }

        private static void Member(ByteSink sink, MemberToken token)
        {
            sink.Int(token.Type);
            sink.UInt(token.Member);
        }

        private static void OptMember(ByteSink sink, MemberToken? token)
        {
            sink.Bool(token is not null);
            if (token is { } t)
                Member(sink, t);
        }

        private static void Location(ByteSink sink, SourceLocation at)
        {
            sink.UInt(at.IsKnown ? at.File + 1 : 0);
            if (at.IsKnown)
            {
                sink.UInt(at.Line);
                sink.UInt(at.Column);
            }
        }

        // ── Metadata ───────────────────────────────────────

        private void Metadata(ByteSink m)
        {
            Str(m, assembly.Name);
            Ints(m, assembly.Version);
            m.UInt(assembly.References.Count);
            foreach (var r in assembly.References)
            {
                Str(m, r.Name);
                Ints(m, r.Version);
            }
            m.UInt(assembly.TypeRefs.Count);
            foreach (var r in assembly.TypeRefs)
            {
                m.UInt(r.Assembly);
                Str(m, r.Namespace);
                Str(m, r.Name);
                m.UInt(r.Arity);
            }
            m.UInt(assembly.MemberRefs.Count);
            foreach (var r in assembly.MemberRefs)
            {
                m.Byte((int)r.Kind);
                m.Int(r.Parent.Value);
                Str(m, r.Name);
                m.UInt(r.GenericArity);
                Types(m, r.Parameters);
                Type(m, r.Result);
            }
            m.UInt(assembly.Files.Count);
            foreach (var f in assembly.Files)
                Str(m, f);
            Attributes(m, assembly.Attributes);
            m.UInt(assembly.Resources.Count);
            foreach (var r in assembly.Resources)
            {
                Str(m, r.Name);
                m.UInt(r.Data.Length);
                m.Raw(r.Data);
            }
            OptMember(m, assembly.EntryPoint);
            m.UInt(assembly.Types.Count);
            foreach (var type in assembly.Types)
                TypeDefinition(m, type);
            // Optional at the end of the metadata (a reader that stops before it reads the rest the same).
            if (assembly.LanguageData.Count > 0)
            {
                m.UInt(assembly.LanguageData.Count);
                foreach (var d in assembly.LanguageData)
                {
                    Str(m, d.Name);
                    m.UInt(d.Data.Length);
                    m.Raw(d.Data);
                }
            }
        }

        private void Attributes(ByteSink m, IReadOnlyList<HirAttribute> attributes)
        {
            m.UInt(attributes.Count);
            foreach (var a in attributes)
            {
                Member(m, a.Constructor);
                m.UInt(a.Arguments.Length);
                foreach (var c in a.Arguments)
                    Constant(m, c);
                m.UInt(a.Named.Length);
                foreach (var (name, value) in a.Named)
                {
                    Str(m, name);
                    Constant(m, value);
                }
            }
        }

        private void Generics(ByteSink m, List<GenericParam> generics)
        {
            m.UInt(generics.Count);
            foreach (var g in generics)
            {
                Str(m, g.Name);
                m.Bool(g.Variadic);
                m.UInt((int)g.Kinds);
                Types(m, g.Types);
                Types(m, g.OneOf);
                m.UInt(g.Operators.Length);
                foreach (var o in g.Operators)
                {
                    Str(m, o.Symbol);
                    Types(m, o.Operands);
                    Type(m, o.Result);
                }
                Types(m, g.ConvertsTo);
                Types(m, g.ConvertsFrom);
                m.UInt(g.Constructors.Length);
                foreach (var c in g.Constructors)
                    Types(m, c);
                m.UInt(g.FixedSize);
                OptType(m, g.Default);
            }
        }

        private void TypeDefinition(ByteSink m, TypeDef type)
        {
            Str(m, type.Namespace);
            Str(m, type.Name);
            m.Byte((int)type.Kind);
            m.UInt((int)type.Flags);
            m.Byte((int)type.Access);
            m.Int(type.DeclaringType);
            Generics(m, type.Generics);
            OptType(m, type.Base);
            Types(m, type.Interfaces);
            OptType(m, type.Underlying);
            m.UInt(type.Fields.Count);
            foreach (var f in type.Fields)
            {
                Str(m, f.Name);
                Type(m, f.Type);
                m.Byte((int)f.Flags);
                m.Byte((int)f.Access);
                OptConstant(m, f.Value);
                Attributes(m, f.Attributes);
                Location(m, f.Location);
                OptStr(m, f.Doc);
            }
            m.UInt(type.Methods.Count);
            foreach (var method in type.Methods)
                Method(m, method);
            m.UInt(type.Properties.Count);
            foreach (var p in type.Properties)
            {
                Str(m, p.Name);
                Type(m, p.Type);
                Types(m, p.Indices);
                m.Int(p.Getter);
                m.Int(p.Setter);
                m.Byte((int)p.Flags);
                m.Byte((int)p.Access);
                Attributes(m, p.Attributes);
                Location(m, p.Location);
                OptStr(m, p.Doc);
            }
            m.UInt(type.Events.Count);
            foreach (var e in type.Events)
            {
                Str(m, e.Name);
                Type(m, e.Type);
                m.Int(e.Adder);
                m.Int(e.Remover);
                m.Byte((int)e.Flags);
                m.Byte((int)e.Access);
                Attributes(m, e.Attributes);
            }
            m.UInt(type.Operators.Count);
            foreach (var o in type.Operators)
            {
                Str(m, o.Symbol);
                m.Byte((int)o.Fixity);
                m.UInt(o.Method);
                m.Int(o.Precedence);
                m.Byte((int)o.Associativity);
            }
            Attributes(m, type.Attributes);
            Location(m, type.Location);
            OptStr(m, type.Doc);
        }

        private void Method(ByteSink m, MethodDef method)
        {
            Str(m, method.Name);
            m.Byte((int)method.Kind);
            m.UInt((int)method.Flags);
            m.Byte((int)method.Access);
            Generics(m, method.Generics);
            m.UInt(method.Parameters.Count);
            foreach (var p in method.Parameters)
            {
                Str(m, p.Name);
                Type(m, p.Type);
                m.Byte((int)p.Flags);
                OptConstant(m, p.Default);
                Attributes(m, p.Attributes);
            }
            Type(m, method.Result);
            OptStr(m, method.ExternName);
            OptStr(m, method.Library);
            Attributes(m, method.Attributes);
            Location(m, method.Location);
            OptStr(m, method.Doc);
            if (method.Body is not { } body)
            {
                m.UInt(0);
                return;
            }
            int start = bodies.Length;
            Body(bodies, body);
            m.UInt(bodies.Length - start + 1);
            m.UInt(start);
        }

        private void OptConstant(ByteSink m, HirConstant? constant)
        {
            m.Bool(constant is not null);
            if (constant is not null)
                Constant(m, constant);
        }

        private void Constant(ByteSink m, HirConstant constant)
        {
            switch (constant)
            {
                case IntegerConstant i:
                    m.Byte((int)ConstantTag.Integer); m.Byte((int)i.Type); m.Big(i.Value);
                    break;
                case FloatConstant f:
                    m.Byte((int)ConstantTag.Float); m.Byte((int)f.Type); m.Big(f.Bits);
                    break;
                case BoolConstant b:
                    m.Byte((int)ConstantTag.Bool); m.Bool(b.Value);
                    break;
                case CharConstant c:
                    m.Byte((int)ConstantTag.Char); m.UInt(c.Value);
                    break;
                case StringConstant s:
                    m.Byte((int)ConstantTag.String); Str(m, s.Value);
                    break;
                case NullConstant:
                    m.Byte((int)ConstantTag.Null);
                    break;
                case TypeConstant t:
                    m.Byte((int)ConstantTag.Type); Type(m, t.Type);
                    break;
                case EnumConstant { Text: { } text } e:
                    m.Byte((int)ConstantTag.EnumText); Type(m, e.Type); m.Big(e.Value); Str(m, text);
                    break;
                case EnumConstant e:
                    m.Byte((int)ConstantTag.Enum); Type(m, e.Type); m.Big(e.Value);
                    break;
                case ArrayConstant a:
                    m.Byte((int)ConstantTag.Array); Type(m, a.Element); m.UInt(a.Items.Length);
                    foreach (var item in a.Items)
                        Constant(m, item);
                    break;
                default:
                    throw new InvalidOperationException($"cannot write constant {constant}");
            }
        }

        // ── Bodies ─────────────────────────────────────────

        private void Body(ByteSink b, HirBody body)
        {
            b.UInt(body.Locals.Length);
            foreach (var local in body.Locals)
            {
                Str(b, local.Name);
                Type(b, local.Type);
                b.Bool(local.Mutable);
            }
            Statement(b, body.Block);
        }

        private void Statements(ByteSink b, Stmt[] statements)
        {
            b.UInt(statements.Length);
            foreach (var s in statements)
                Statement(b, s);
        }

        private void OptStatement(ByteSink b, Stmt? statement)
        {
            b.Bool(statement is not null);
            if (statement is not null)
                Statement(b, statement);
        }

        private void Statement(ByteSink b, Stmt statement)
        {
            switch (statement)
            {
                case BlockStmt s: b.Byte((int)StmtTag.Block); Statements(b, s.Statements); break;
                case LocalStmt s: b.Byte((int)StmtTag.Local); b.UInt(s.Local); OptExpr(b, s.Value); break;
                case ExprStmt s: b.Byte((int)StmtTag.Expr); Expr(b, s.Expression); break;
                case IfStmt s: b.Byte((int)StmtTag.If); Expr(b, s.Condition); Statement(b, s.Then); OptStatement(b, s.Else); break;
                case WhileStmt s: b.Byte((int)StmtTag.While); Expr(b, s.Condition); Statement(b, s.Body); b.Int(s.Label); break;
                case DoWhileStmt s: b.Byte((int)StmtTag.DoWhile); Statement(b, s.Body); Expr(b, s.Condition); b.Int(s.Label); break;
                case ForStmt s:
                    b.Byte((int)StmtTag.For); Statements(b, s.Initializers); OptExpr(b, s.Condition); Exprs(b, s.Steps);
                    Statement(b, s.Body); b.Int(s.Label);
                    break;
                case ForeachStmt s: b.Byte((int)StmtTag.Foreach); b.UInt(s.Local); Expr(b, s.Collection); Statement(b, s.Body); b.Int(s.Label); break;
                case SwitchStmt s:
                    b.Byte((int)StmtTag.Switch); Expr(b, s.Value); b.UInt(s.Sections.Length);
                    foreach (var section in s.Sections)
                    {
                        Patterns(b, section.Patterns);
                        OptExpr(b, section.Guard);
                        Statement(b, section.Body);
                    }
                    b.Int(s.Label);
                    break;
                case BreakStmt s: b.Byte((int)StmtTag.Break); b.Int(s.Label); break;
                case ContinueStmt s: b.Byte((int)StmtTag.Continue); b.Int(s.Label); break;
                case ReturnStmt s: b.Byte((int)StmtTag.Return); OptExpr(b, s.Value); break;
                case ThrowStmt s: b.Byte((int)StmtTag.Throw); OptExpr(b, s.Value); break;
                case TryStmt s:
                    b.Byte((int)StmtTag.Try); Statement(b, s.Body); b.UInt(s.Catches.Length);
                    foreach (var c in s.Catches)
                    {
                        OptType(b, c.Type);
                        b.Int(c.Local);
                        OptExpr(b, c.Filter);
                        Statement(b, c.Body);
                    }
                    OptStatement(b, s.Finally);
                    break;
                case UsingStmt s: b.Byte((int)StmtTag.Using); b.UInt(s.Local); Expr(b, s.Resource); Statement(b, s.Body); break;
                case DeleteStmt s: b.Byte((int)StmtTag.Delete); Expr(b, s.Value); break;
                case RegionStmt s: b.Byte((int)StmtTag.Region); Statement(b, s.Body); break;
                case UnsafeStmt s: b.Byte((int)StmtTag.Unsafe); Statement(b, s.Body); break;
                case YieldStmt s: b.Byte((int)StmtTag.Yield); OptExpr(b, s.Value); break;
                case CheckedStmt s: b.Byte((int)StmtTag.Checked); Statement(b, s.Body); b.Bool(s.Checked); break;
                default: throw new InvalidOperationException($"cannot write statement {statement.GetType().Name}");
            }
            Location(b, statement.At);
        }

        private void Exprs(ByteSink b, Expr[] expressions)
        {
            b.UInt(expressions.Length);
            foreach (var e in expressions)
                Expr(b, e);
        }

        private void OptExpr(ByteSink b, Expr? expression)
        {
            b.Bool(expression is not null);
            if (expression is not null)
                Expr(b, expression);
        }

        private void Inits(ByteSink b, MemberInit[] inits)
        {
            b.UInt(inits.Length);
            foreach (var init in inits)
            {
                Member(b, init.Member);
                b.Byte((int)init.Kind);
                Expr(b, init.Value);
            }
        }

        private void Expr(ByteSink b, Expr e)
        {
            b.Byte((int)Tag(e));
            Type(b, e.Type);
            switch (e)
            {
                case LiteralExpr x: Constant(b, x.Value); break;
                case LocalExpr x: b.UInt(x.Local); break;
                case ParamExpr x: b.UInt(x.Parameter); break;
                case ThisExpr or ReceiverExpr or DefaultExpr: break;
                case FieldExpr x:
                    OptExpr(b, x.Instance); Member(b, x.Field); b.Bool(x.TypeArguments is not null);
                    if (x.TypeArguments is { } fieldArguments)
                        Types(b, fieldArguments);
                    break;
                case PropertyExpr x: OptExpr(b, x.Instance); Member(b, x.Property); Exprs(b, x.Indices); break;
                case ElementExpr x: Expr(b, x.Array); Exprs(b, x.Indices); break;
                case CallExpr x: OptExpr(b, x.Receiver); Member(b, x.Method); Types(b, x.TypeArguments); Exprs(b, x.Arguments); b.Bool(x.NonVirtual); break;
                case InvokeExpr x: Expr(b, x.Function); Exprs(b, x.Arguments); break;
                case NewExpr x: OptMember(b, x.Constructor); Exprs(b, x.Arguments); Inits(b, x.Initializers); break;
                case NewArrayExpr x: Type(b, x.Element); Exprs(b, x.Lengths); break;
                case ArrayLiteralExpr x: Type(b, x.Element); Exprs(b, x.Items); break;
                case UnaryExpr x: b.Byte((int)x.Op); Expr(b, x.Operand); break;
                case BinaryExpr x: b.Byte((int)x.Op); Expr(b, x.Left); Expr(b, x.Right); break;
                case AssignExpr x: Expr(b, x.Target); Expr(b, x.Value); b.Byte(x.Compound is { } op ? (int)op + 1 : 0); break;
                case ConvertExpr x: Expr(b, x.Value); b.Byte((int)x.Kind); OptMember(b, x.Method); break;
                case IsExpr x: Expr(b, x.Value); Pattern(b, x.Pattern); break;
                case AsExpr x: Expr(b, x.Value); break;
                case SwitchExpr x:
                    Expr(b, x.Value); b.UInt(x.Arms.Length);
                    foreach (var arm in x.Arms)
                    {
                        Pattern(b, arm.Pattern);
                        OptExpr(b, arm.Guard);
                        Expr(b, arm.Value);
                    }
                    break;
                case ConditionalExpr x: Expr(b, x.Condition); Expr(b, x.Then); Expr(b, x.Else); break;
                case CoalesceExpr x: Expr(b, x.Left); Expr(b, x.Right); break;
                case ConditionalAccessExpr x: Expr(b, x.Receiver); Expr(b, x.Access); break;
                case LambdaExpr x:
                    b.UInt(x.Parameters.Length);
                    foreach (int p in x.Parameters)
                        b.UInt(p);
                    Statement(b, x.Body);
                    b.Bool(x.Async);
                    break;
                case MethodRefExpr x: OptExpr(b, x.Target); Member(b, x.Method); Types(b, x.TypeArguments); break;
                case AwaitExpr x: Expr(b, x.Task); break;
                case TypeOfExpr x: Type(b, x.Operand); break;
                case SizeOfExpr x: Type(b, x.Operand); break;
                case ArrayLengthExpr x: Expr(b, x.Array); OptExpr(b, x.Dimension); break;
                case WithExpr x: Expr(b, x.Value); Inits(b, x.Changes); break;
                case RangeExpr x: OptExpr(b, x.Low); OptExpr(b, x.High); b.Bool(x.Inclusive); break;
                case InterpolatedExpr x:
                    b.UInt(x.Parts.Length);
                    foreach (var part in x.Parts)
                    {
                        Expr(b, part.Value);
                        OptStr(b, part.Format);
                        b.Int(part.Alignment);
                    }
                    break;
                case BorrowExpr x: Expr(b, x.Place); b.Bool(x.Mutable); break;
                case AddressOfExpr x: Expr(b, x.Place); break;
                case DerefExpr x: Expr(b, x.Pointer); break;
                case BlockExpr x: Statements(b, x.Statements); Expr(b, x.Value); break;
                case CheckedExpr x: Expr(b, x.Value); b.Bool(x.Checked); break;
                case ResourceExpr x: Str(b, x.Name); break;
                default: throw new InvalidOperationException($"cannot write expression {e.GetType().Name}");
            }
        }

        private static ExprTag Tag(Expr e) => e switch
        {
            LiteralExpr => ExprTag.Literal, LocalExpr => ExprTag.Local, ParamExpr => ExprTag.Param, ThisExpr => ExprTag.This,
            FieldExpr => ExprTag.Field, PropertyExpr => ExprTag.Property, ElementExpr => ExprTag.Element, CallExpr => ExprTag.Call,
            InvokeExpr => ExprTag.Invoke, NewExpr => ExprTag.New, NewArrayExpr => ExprTag.NewArray, ArrayLiteralExpr => ExprTag.ArrayLiteral,
            UnaryExpr => ExprTag.Unary, BinaryExpr => ExprTag.Binary, AssignExpr => ExprTag.Assign, ConvertExpr => ExprTag.Convert,
            IsExpr => ExprTag.Is, AsExpr => ExprTag.As, SwitchExpr => ExprTag.Switch, ConditionalExpr => ExprTag.Conditional,
            CoalesceExpr => ExprTag.Coalesce, ConditionalAccessExpr => ExprTag.ConditionalAccess, ReceiverExpr => ExprTag.Receiver,
            LambdaExpr => ExprTag.Lambda, MethodRefExpr => ExprTag.MethodRef, AwaitExpr => ExprTag.Await, TypeOfExpr => ExprTag.TypeOf,
            SizeOfExpr => ExprTag.SizeOf, DefaultExpr => ExprTag.Default, WithExpr => ExprTag.With, RangeExpr => ExprTag.Range,
            InterpolatedExpr => ExprTag.Interpolated, BorrowExpr => ExprTag.Borrow, AddressOfExpr => ExprTag.AddressOf,
            DerefExpr => ExprTag.Deref, BlockExpr => ExprTag.Block, ArrayLengthExpr => ExprTag.ArrayLength, CheckedExpr => ExprTag.Checked,
            ResourceExpr => ExprTag.Resource,
            _ => throw new InvalidOperationException($"cannot write expression {e.GetType().Name}"),
        };

        private void Patterns(ByteSink b, Pattern[] patterns)
        {
            b.UInt(patterns.Length);
            foreach (var p in patterns)
                Pattern(b, p);
        }

        private void Pattern(ByteSink b, Pattern pattern)
        {
            switch (pattern)
            {
                case DiscardPattern: b.Byte((int)PatternTag.Discard); break;
                case ConstantPattern p: b.Byte((int)PatternTag.Constant); Constant(b, p.Value); break;
                case RelationalPattern p: b.Byte((int)PatternTag.Relational); b.Byte((int)p.Op); Constant(b, p.Value); break;
                case RangePattern p: b.Byte((int)PatternTag.Range); Constant(b, p.Low); Constant(b, p.High); b.Bool(p.Inclusive); break;
                case TypePattern p: b.Byte((int)PatternTag.Type); Type(b, p.Type); break;
                case BindingPattern p:
                    b.Byte((int)PatternTag.Binding); b.UInt(p.Local); b.Bool(p.Inner is not null);
                    if (p.Inner is { } inner)
                        Pattern(b, inner);
                    break;
                case AndPattern p: b.Byte((int)PatternTag.And); Pattern(b, p.Left); Pattern(b, p.Right); break;
                case OrPattern p: b.Byte((int)PatternTag.Or); Pattern(b, p.Left); Pattern(b, p.Right); break;
                case NotPattern p: b.Byte((int)PatternTag.Not); Pattern(b, p.Operand); break;
                case PropertyPattern p:
                    b.Byte((int)PatternTag.Property); OptType(b, p.Type); b.UInt(p.Members.Length);
                    foreach (var m in p.Members)
                    {
                        Member(b, m.Member);
                        b.Byte((int)m.Kind);
                        Pattern(b, m.Pattern);
                    }
                    break;
                case PositionalPattern p: b.Byte((int)PatternTag.Positional); OptType(b, p.Type); OptMember(b, p.Deconstructor); Patterns(b, p.Items); break;
                case ListPattern p: b.Byte((int)PatternTag.List); Patterns(b, p.Items); b.Int(p.Slice); b.Int(p.SliceLocal); break;
                default: throw new InvalidOperationException($"cannot write pattern {pattern.GetType().Name}");
            }
        }
    }
}

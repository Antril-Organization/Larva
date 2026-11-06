namespace Larva.Hir.Encoding
{
    /// <summary>Reads what <see cref="LvaWriter"/> writes. The bodies stay in the file's bytes until a method's body is used.</summary>
    /// <summary>
    /// How tokens read from one assembly become tokens of another (linking): its types and members as they are numbered
    /// in the assembly being made, and its source files after the files of the assemblies before it. A mapped read keeps
    /// no type or member references: they are what the map resolves.
    /// </summary>
    internal sealed record TokenMap(Func<TypeToken, TypeToken> Type, Func<MemberToken, MemberToken> Member, int FileOffset);

    internal sealed class LvaReader
    {
        private readonly byte[] data;
        private readonly TokenMap? map;
        private string[] strings = [];
        private HirType[] types = [];
        private int bodiesStart;

        private LvaReader(byte[] data, TokenMap? map)
        {
            this.data = data;
            this.map = map;
        }

        public static HirAssembly Read(byte[] data, TokenMap? map = null) => new LvaReader(LvaContainer.Unwrap(data), map).Assembly();

        private HirAssembly Assembly()
        {
            if (data.Length < 3 || !data.AsSpan(0, 3).SequenceEqual(LvaFormat.Magic))
                throw new HirFormatException("not a Larva HIR assembly (.lva)");
            var s = new ByteSource(data, 3);
            int version = s.UInt();
            if (version != LvaFormat.Version)
                throw new HirFormatException($"format version {version}; this Larva reads version {LvaFormat.Version}");
            strings = new string[s.UInt()];
            for (int i = 0; i < strings.Length; i++)
                strings[i] = s.Utf8();
            types = new HirType[s.UInt()];
            for (int i = 0; i < types.Length; i++)
                types[i] = TypeEntry(ref s);
            int metaLength = s.UInt();
            int metaStart = s.Position;
            s.Position += metaLength;
            s.UInt(); // bodies' length
            bodiesStart = s.Position;
            var m = new ByteSource(data, metaStart);
            var assembly = Metadata(ref m);
            // The language's data, when the metadata goes on past the types.
            if (m.Position < metaStart + metaLength)
            {
                int count = m.UInt();
                for (int i = 0; i < count; i++)
                    assembly.LanguageData.Add(new HirResource(Str(ref m), m.Raw(m.UInt()).ToArray()));
            }
            return assembly;
        }

        private HirType TypeEntry(ref ByteSource s)
        {
            var tag = (TypeTag)s.Byte();
            return tag switch
            {
                TypeTag.Primitive => HirType.Of((Primitive)s.Byte()),
                TypeTag.Named => new NamedType(MapType(new TypeToken((int)s.Int())), Types(ref s)),
                TypeTag.Array => new ArrayType(Type(ref s), s.UInt()),
                TypeTag.InlineArray => InlineArray(ref s),
                TypeTag.Nullable => new NullableType(Type(ref s)),
                TypeTag.Reference => new ReferenceType(Type(ref s), s.Bool()),
                TypeTag.Pointer => new PointerType(Type(ref s), s.Bool()),
                TypeTag.Func => new FuncType(Types(ref s), Type(ref s)),
                TypeTag.NativeFunc => new NativeFuncType(Types(ref s), Type(ref s)),
                TypeTag.GenericParam => new GenericParamType((GenericOwner)s.Byte(), s.UInt()),
                TypeTag.Pack => new PackExpansionType(Type(ref s)),
                _ => throw new HirFormatException($"unknown type tag {tag}"),
            };
        }

        // ── Tables ─────────────────────────────────────────

        private string Str(ref ByteSource s)
        {
            int i = s.UInt();
            return i < strings.Length ? strings[i] : throw new HirFormatException("string index out of range");
        }

        private string? OptStr(ref ByteSource s)
        {
            int i = s.UInt();
            return i == 0 ? null : i - 1 < strings.Length ? strings[i - 1] : throw new HirFormatException("string index out of range");
        }

        private HirType Type(ref ByteSource s)
        {
            int i = s.UInt();
            return i < types.Length && types[i] is { } type ? type : throw new HirFormatException("type index out of range");
        }

        private HirType? OptType(ref ByteSource s)
        {
            int i = s.UInt();
            return i == 0 ? null : i - 1 < types.Length && types[i - 1] is { } type ? type : throw new HirFormatException("type index out of range");
        }

        private HirType[] Types(ref ByteSource s)
        {
            int count = s.UInt();
            if (count == 0)
                return [];
            var list = new HirType[count];
            for (int i = 0; i < count; i++)
                list[i] = Type(ref s);
            return list;
        }

        private static int[] Ints(ref ByteSource s)
        {
            var values = new int[s.UInt()];
            for (int i = 0; i < values.Length; i++)
                values[i] = (int)s.Int();
            return values;
        }

        private TypeToken MapType(TypeToken token) => map is null ? token : map.Type(token);

        private MemberToken Member(ref ByteSource s)
        {
            var token = new MemberToken((int)s.Int(), s.UInt());
            return map is null ? token : map.Member(token);
        }

        private MemberToken? OptMember(ref ByteSource s) => s.Bool() ? Member(ref s) : null;

        private InlineArrayType InlineArray(ref ByteSource s)
        {
            var element = Type(ref s);
            int count = s.UInt();
            return new InlineArrayType(element, count, s.Bool() ? Type(ref s) : null);
        }

        private SourceLocation Location(ref ByteSource s)
        {
            int file = s.UInt() - 1;
            return file < 0 ? SourceLocation.None : new SourceLocation(file + (map?.FileOffset ?? 0), s.UInt(), s.UInt());
        }

        // ── Metadata ───────────────────────────────────────

        private HirAssembly Metadata(ref ByteSource m)
        {
            var assembly = new HirAssembly { Name = Str(ref m), Version = Ints(ref m) };
            int count = m.UInt();
            for (int i = 0; i < count; i++)
                assembly.References.Add(new AssemblyRef(Str(ref m), Ints(ref m)));
            count = m.UInt();
            for (int i = 0; i < count; i++)
            {
                var reference = new TypeRef(m.UInt(), Str(ref m), Str(ref m), m.UInt());
                if (map is null)
                    assembly.TypeRefs.Add(reference);
            }
            count = m.UInt();
            for (int i = 0; i < count; i++)
            {
                var reference = new MemberRef((MemberKind)m.Byte(), new TypeToken((int)m.Int()), Str(ref m), m.UInt(), Types(ref m), Type(ref m));
                if (map is null)
                    assembly.MemberRefs.Add(reference);
            }
            count = m.UInt();
            for (int i = 0; i < count; i++)
                assembly.Files.Add(Str(ref m));
            assembly.Attributes.AddRange(Attributes(ref m));
            count = m.UInt();
            for (int i = 0; i < count; i++)
                assembly.Resources.Add(new HirResource(Str(ref m), m.Raw(m.UInt()).ToArray()));
            assembly.EntryPoint = OptMember(ref m);
            count = m.UInt();
            assembly.Types.Capacity = count;
            for (int i = 0; i < count; i++)
                assembly.Types.Add(TypeDefinition(ref m));
            return assembly;
        }

        private HirAttribute[] Attributes(ref ByteSource m)
        {
            int count = m.UInt();
            if (count == 0)
                return [];
            var list = new HirAttribute[count];
            for (int i = 0; i < count; i++)
            {
                var constructor = Member(ref m);
                var arguments = new HirConstant[m.UInt()];
                for (int a = 0; a < arguments.Length; a++)
                    arguments[a] = Constant(ref m);
                var named = new (string, HirConstant)[m.UInt()];
                for (int n = 0; n < named.Length; n++)
                    named[n] = (Str(ref m), Constant(ref m));
                list[i] = new HirAttribute(constructor, arguments) { Named = named };
            }
            return list;
        }

        private void Generics(ref ByteSource m, List<GenericParam> into)
        {
            int count = m.UInt();
            for (int i = 0; i < count; i++)
            {
                string name = Str(ref m);
                bool variadic = m.Bool();
                var kinds = (ConstraintKinds)m.UInt();
                var bases = Types(ref m);
                var oneOf = Types(ref m);
                var operators = new OperatorRequirement[m.UInt()];
                for (int o = 0; o < operators.Length; o++)
                    operators[o] = new OperatorRequirement(Str(ref m), Types(ref m), Type(ref m));
                var convertsTo = Types(ref m);
                var convertsFrom = Types(ref m);
                var constructors = new HirType[m.UInt()][];
                for (int c = 0; c < constructors.Length; c++)
                    constructors[c] = Types(ref m);
                int fixedSize = (int)m.UInt();
                into.Add(new GenericParam(name, variadic)
                {
                    Kinds = kinds, Types = bases, OneOf = oneOf, Operators = operators,
                    ConvertsTo = convertsTo, ConvertsFrom = convertsFrom, Constructors = constructors, FixedSize = fixedSize, Default = OptType(ref m),
                });
            }
        }

        private TypeDef TypeDefinition(ref ByteSource m)
        {
            var type = new TypeDef
            {
                Namespace = Str(ref m),
                Name = Str(ref m),
                Kind = (TypeKind)m.Byte(),
                Flags = (TypeFlags)m.UInt(),
                Access = (Access)m.Byte(),
                DeclaringType = (int)m.Int(),
            };
            Generics(ref m, type.Generics);
            type.Base = OptType(ref m);
            type.Interfaces.AddRange(Types(ref m));
            type.Underlying = OptType(ref m);
            int count = m.UInt();
            for (int i = 0; i < count; i++)
            {
                type.Fields.Add(new FieldDef(Str(ref m), Type(ref m))
                {
                    Flags = (FieldFlags)m.Byte(), Access = (Access)m.Byte(), Value = OptConstant(ref m), Attributes = Attributes(ref m),
                    Location = Location(ref m), Doc = OptStr(ref m),
                });
            }
            count = m.UInt();
            type.Methods.Capacity = count;
            for (int i = 0; i < count; i++)
                type.Methods.Add(Method(ref m));
            count = m.UInt();
            for (int i = 0; i < count; i++)
            {
                type.Properties.Add(new PropertyDef(Str(ref m), Type(ref m))
                {
                    Indices = Types(ref m), Getter = (int)m.Int(), Setter = (int)m.Int(), Flags = (MemberFlags)m.Byte(),
                    Access = (Access)m.Byte(), Attributes = Attributes(ref m), Location = Location(ref m), Doc = OptStr(ref m),
                });
            }
            count = m.UInt();
            for (int i = 0; i < count; i++)
            {
                type.Events.Add(new EventDef(Str(ref m), Type(ref m))
                {
                    Adder = (int)m.Int(), Remover = (int)m.Int(), Flags = (MemberFlags)m.Byte(), Access = (Access)m.Byte(),
                    Attributes = Attributes(ref m),
                });
            }
            count = m.UInt();
            for (int i = 0; i < count; i++)
            {
                type.Operators.Add(new OperatorDef(Str(ref m), (Fixity)m.Byte(), m.UInt())
                {
                    Precedence = (int)m.Int(), Associativity = (Associativity)m.Byte(),
                });
            }
            type.Attributes.AddRange(Attributes(ref m));
            type.Location = Location(ref m);
            type.Doc = OptStr(ref m);
            return type;
        }

        private MethodDef Method(ref ByteSource m)
        {
            string name = Str(ref m);
            var kind = (MethodKind)m.Byte();
            var flags = (MethodFlags)m.UInt();
            var access = (Access)m.Byte();
            var generics = new List<GenericParam>();
            Generics(ref m, generics);
            var parameters = new Parameter[m.UInt()];
            for (int i = 0; i < parameters.Length; i++)
            {
                parameters[i] = new Parameter(Str(ref m), Type(ref m))
                {
                    Flags = (ParameterFlags)m.Byte(), Default = OptConstant(ref m), Attributes = Attributes(ref m),
                };
            }
            var method = new MethodDef(name, Type(ref m), parameters)
            {
                Kind = kind,
                Flags = flags,
                Access = access,
                ExternName = OptStr(ref m),
                Library = OptStr(ref m),
            };
            method.Generics.AddRange(generics);
            method.Attributes.AddRange(Attributes(ref m));
            method.Location = Location(ref m);
            method.Doc = OptStr(ref m);
            int length = m.UInt();
            if (length > 0)
            {
                int start = bodiesStart + m.UInt();
                if (start + length - 1 > data.Length)
                    throw new HirFormatException("method body out of range");
                method.LoadBodyLater(() =>
                {
                    var b = new ByteSource(data, start);
                    return Body(ref b);
                });
            }
            return method;
        }

        private HirConstant? OptConstant(ref ByteSource m) => m.Bool() ? Constant(ref m) : null;

        private HirConstant Constant(ref ByteSource m)
        {
            var tag = (ConstantTag)m.Byte();
            switch (tag)
            {
                case ConstantTag.Integer: { var type = (Primitive)m.Byte(); return new IntegerConstant(m.Big(), type); }
                case ConstantTag.Float: { var type = (Primitive)m.Byte(); return new FloatConstant(m.Big(), type); }
                case ConstantTag.Bool: return new BoolConstant(m.Bool());
                case ConstantTag.Char: return new CharConstant(m.UInt());
                case ConstantTag.String: return new StringConstant(Str(ref m));
                case ConstantTag.Null: return new NullConstant();
                case ConstantTag.Type: return new TypeConstant(Type(ref m));
                case ConstantTag.Enum: { var type = Type(ref m); return new EnumConstant(type, m.Big()); }
                case ConstantTag.EnumText: { var type = Type(ref m); var value = m.Big(); return new EnumConstant(type, value, Str(ref m)); }
                case ConstantTag.Array:
                {
                    var element = Type(ref m);
                    var items = new HirConstant[m.UInt()];
                    for (int i = 0; i < items.Length; i++)
                        items[i] = Constant(ref m);
                    return new ArrayConstant(element, items);
                }
                default: throw new HirFormatException($"unknown constant tag {tag}");
            }
        }

        // ── Bodies ─────────────────────────────────────────

        private HirBody Body(ref ByteSource b)
        {
            var locals = new HirLocal[b.UInt()];
            for (int i = 0; i < locals.Length; i++)
                locals[i] = new HirLocal(Str(ref b), Type(ref b), b.Bool());
            return new HirBody(locals, (BlockStmt)Statement(ref b));
        }

        private Stmt[] Statements(ref ByteSource b)
        {
            int count = b.UInt();
            if (count == 0)
                return [];
            var list = new Stmt[count];
            for (int i = 0; i < count; i++)
                list[i] = Statement(ref b);
            return list;
        }

        private Stmt? OptStatement(ref ByteSource b) => b.Bool() ? Statement(ref b) : null;

        private BlockStmt Block(ref ByteSource b) => Statement(ref b) as BlockStmt ?? throw new HirFormatException("expected a block");

        private BlockStmt? OptBlock(ref ByteSource b) => b.Bool() ? Block(ref b) : null;

        private Stmt Statement(ref ByteSource b)
        {
            var tag = (StmtTag)b.Byte();
            Stmt statement;
            switch (tag)
            {
                case StmtTag.Block: statement = new BlockStmt(Statements(ref b)); break;
                case StmtTag.Local: statement = new LocalStmt(b.UInt(), OptExpr(ref b)); break;
                case StmtTag.Expr: statement = new ExprStmt(Expr(ref b)); break;
                case StmtTag.If: statement = new IfStmt(Expr(ref b), Statement(ref b), OptStatement(ref b)); break;
                case StmtTag.While: statement = new WhileStmt(Expr(ref b), Statement(ref b), (int)b.Int()); break;
                case StmtTag.DoWhile: statement = new DoWhileStmt(Statement(ref b), Expr(ref b), (int)b.Int()); break;
                case StmtTag.For: statement = new ForStmt(Statements(ref b), OptExpr(ref b), Exprs(ref b), Statement(ref b), (int)b.Int()); break;
                case StmtTag.Foreach: statement = new ForeachStmt(b.UInt(), Expr(ref b), Statement(ref b), (int)b.Int()); break;
                case StmtTag.Switch:
                {
                    var value = Expr(ref b);
                    var sections = new SwitchSection[b.UInt()];
                    for (int i = 0; i < sections.Length; i++)
                        sections[i] = new SwitchSection(Patterns(ref b), OptExpr(ref b), Statement(ref b));
                    statement = new SwitchStmt(value, sections, (int)b.Int());
                    break;
                }
                case StmtTag.Break: statement = new BreakStmt((int)b.Int()); break;
                case StmtTag.Continue: statement = new ContinueStmt((int)b.Int()); break;
                case StmtTag.Return: statement = new ReturnStmt(OptExpr(ref b)); break;
                case StmtTag.Throw: statement = new ThrowStmt(OptExpr(ref b)); break;
                case StmtTag.Try:
                {
                    var body = Block(ref b);
                    var catches = new CatchClause[b.UInt()];
                    for (int i = 0; i < catches.Length; i++)
                        catches[i] = new CatchClause(OptType(ref b), (int)b.Int(), OptExpr(ref b), Block(ref b));
                    statement = new TryStmt(body, catches, OptBlock(ref b));
                    break;
                }
                case StmtTag.Using: statement = new UsingStmt(b.UInt(), Expr(ref b), Statement(ref b)); break;
                case StmtTag.Delete: statement = new DeleteStmt(Expr(ref b)); break;
                case StmtTag.Region: statement = new RegionStmt(Block(ref b)); break;
                case StmtTag.Unsafe: statement = new UnsafeStmt(Block(ref b)); break;
                case StmtTag.Yield: statement = new YieldStmt(OptExpr(ref b)); break;
                case StmtTag.Checked:
                {
                    var body = Block(ref b);
                    statement = new CheckedStmt(body, b.Bool());
                    break;
                }
                default: throw new HirFormatException($"unknown statement tag {tag}");
            }
            var at = Location(ref b);
            return at.IsKnown ? statement with { At = at } : statement;
        }

        private Expr[] Exprs(ref ByteSource b)
        {
            int count = b.UInt();
            if (count == 0)
                return [];
            var list = new Expr[count];
            for (int i = 0; i < count; i++)
                list[i] = Expr(ref b);
            return list;
        }

        private Expr? OptExpr(ref ByteSource b) => b.Bool() ? Expr(ref b) : null;

        private MemberInit[] Inits(ref ByteSource b)
        {
            int count = b.UInt();
            if (count == 0)
                return [];
            var list = new MemberInit[count];
            for (int i = 0; i < count; i++)
            {
                var member = Member(ref b);
                var kind = (MemberKind)b.Byte();
                list[i] = new MemberInit(member, Expr(ref b), kind);
            }
            return list;
        }

        private Expr Expr(ref ByteSource b)
        {
            var tag = (ExprTag)b.Byte();
            var type = Type(ref b);
            switch (tag)
            {
                case ExprTag.Literal: return new LiteralExpr(Constant(ref b), type);
                case ExprTag.Local: return new LocalExpr(b.UInt(), type);
                case ExprTag.Param: return new ParamExpr(b.UInt(), type);
                case ExprTag.This: return new ThisExpr(type);
                case ExprTag.Receiver: return new ReceiverExpr(type);
                case ExprTag.Default: return new DefaultExpr(type);
                case ExprTag.Field:
                {
                    var instance = OptExpr(ref b);
                    var member = Member(ref b);
                    var arguments = b.Bool() ? Types(ref b) : null;
                    return new FieldExpr(instance, member, type, arguments);
                }
                case ExprTag.Property: return new PropertyExpr(OptExpr(ref b), Member(ref b), Exprs(ref b), type);
                case ExprTag.Element: return new ElementExpr(Expr(ref b), Exprs(ref b), type);
                case ExprTag.Call: return new CallExpr(OptExpr(ref b), Member(ref b), Types(ref b), Exprs(ref b), type) { NonVirtual = b.Bool() };
                case ExprTag.Invoke: return new InvokeExpr(Expr(ref b), Exprs(ref b), type);
                case ExprTag.New: return new NewExpr(OptMember(ref b), Exprs(ref b), Inits(ref b), type);
                case ExprTag.NewArray: return new NewArrayExpr(Type(ref b), Exprs(ref b), type);
                case ExprTag.ArrayLiteral: return new ArrayLiteralExpr(Type(ref b), Exprs(ref b), type);
                case ExprTag.Unary: return new UnaryExpr((UnaryOp)b.Byte(), Expr(ref b), type);
                case ExprTag.Binary: return new BinaryExpr((BinaryOp)b.Byte(), Expr(ref b), Expr(ref b), type);
                case ExprTag.Assign:
                {
                    var target = Expr(ref b);
                    var value = Expr(ref b);
                    int op = b.Byte();
                    return new AssignExpr(target, value, type) { Compound = op == 0 ? null : (BinaryOp)(op - 1) };
                }
                case ExprTag.Convert: return new ConvertExpr(Expr(ref b), (ConversionKind)b.Byte(), type) { Method = OptMember(ref b) };
                case ExprTag.Is: return new IsExpr(Expr(ref b), Pattern(ref b), type);
                case ExprTag.As: return new AsExpr(Expr(ref b), type);
                case ExprTag.Switch:
                {
                    var value = Expr(ref b);
                    var arms = new SwitchArm[b.UInt()];
                    for (int i = 0; i < arms.Length; i++)
                        arms[i] = new SwitchArm(Pattern(ref b), OptExpr(ref b), Expr(ref b));
                    return new SwitchExpr(value, arms, type);
                }
                case ExprTag.Conditional: return new ConditionalExpr(Expr(ref b), Expr(ref b), Expr(ref b), type);
                case ExprTag.Coalesce: return new CoalesceExpr(Expr(ref b), Expr(ref b), type);
                case ExprTag.ConditionalAccess: return new ConditionalAccessExpr(Expr(ref b), Expr(ref b), type);
                case ExprTag.Lambda:
                {
                    var parameters = new int[b.UInt()];
                    for (int i = 0; i < parameters.Length; i++)
                        parameters[i] = b.UInt();
                    return new LambdaExpr(parameters, Statement(ref b), type) { Async = b.Bool() };
                }
                case ExprTag.MethodRef: return new MethodRefExpr(OptExpr(ref b), Member(ref b), Types(ref b), type);
                case ExprTag.Await: return new AwaitExpr(Expr(ref b), type);
                case ExprTag.TypeOf: return new TypeOfExpr(Type(ref b), type);
                case ExprTag.SizeOf: return new SizeOfExpr(Type(ref b), type);
                case ExprTag.ArrayLength:
                {
                    var array = Expr(ref b);
                    return new ArrayLengthExpr(array, type) { Dimension = OptExpr(ref b) };
                }
                case ExprTag.With: return new WithExpr(Expr(ref b), Inits(ref b), type);
                case ExprTag.Range: return new RangeExpr(OptExpr(ref b), OptExpr(ref b), b.Bool(), type);
                case ExprTag.Interpolated:
                {
                    var parts = new InterpolationPart[b.UInt()];
                    for (int i = 0; i < parts.Length; i++)
                        parts[i] = new InterpolationPart(Expr(ref b), OptStr(ref b), (int)b.Int());
                    return new InterpolatedExpr(parts, type);
                }
                case ExprTag.Borrow: return new BorrowExpr(Expr(ref b), b.Bool(), type);
                case ExprTag.AddressOf: return new AddressOfExpr(Expr(ref b), type);
                case ExprTag.Deref: return new DerefExpr(Expr(ref b), type);
                case ExprTag.Block: return new BlockExpr(Statements(ref b), Expr(ref b), type);
                case ExprTag.Resource: return new ResourceExpr(Str(ref b), type);
                case ExprTag.Checked:
                {
                    var value = Expr(ref b);
                    return new CheckedExpr(value, b.Bool(), type);
                }
                default: throw new HirFormatException($"unknown expression tag {tag}");
            }
        }

        private Pattern[] Patterns(ref ByteSource b)
        {
            int count = b.UInt();
            if (count == 0)
                return [];
            var list = new Pattern[count];
            for (int i = 0; i < count; i++)
                list[i] = Pattern(ref b);
            return list;
        }

        private Pattern Pattern(ref ByteSource b)
        {
            var tag = (PatternTag)b.Byte();
            switch (tag)
            {
                case PatternTag.Discard: return new DiscardPattern();
                case PatternTag.Constant: return new ConstantPattern(Constant(ref b));
                case PatternTag.Relational: return new RelationalPattern((BinaryOp)b.Byte(), Constant(ref b));
                case PatternTag.Range: return new RangePattern(Constant(ref b), Constant(ref b), b.Bool());
                case PatternTag.Type: return new TypePattern(Type(ref b));
                case PatternTag.Binding: return new BindingPattern(b.UInt(), b.Bool() ? Pattern(ref b) : null);
                case PatternTag.And: return new AndPattern(Pattern(ref b), Pattern(ref b));
                case PatternTag.Or: return new OrPattern(Pattern(ref b), Pattern(ref b));
                case PatternTag.Not: return new NotPattern(Pattern(ref b));
                case PatternTag.Property:
                {
                    var type = OptType(ref b);
                    var members = new PropertySubpattern[b.UInt()];
                    for (int i = 0; i < members.Length; i++)
                    {
                        var member = Member(ref b);
                        var kind = (MemberKind)b.Byte();
                        members[i] = new PropertySubpattern(member, Pattern(ref b), kind);
                    }
                    return new PropertyPattern(type, members);
                }
                case PatternTag.Positional: return new PositionalPattern(OptType(ref b), OptMember(ref b), Patterns(ref b));
                case PatternTag.List: return new ListPattern(Patterns(ref b), (int)b.Int(), (int)b.Int());
                default: throw new HirFormatException($"unknown pattern tag {tag}");
            }
        }
    }
}

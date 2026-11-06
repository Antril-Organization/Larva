using System.Diagnostics;
using Larva.Hir.Encoding;
using Larva.Hir.Text;

namespace Larva.Hir.Tests;

/// <summary>Larva HIR: the model, the text form and the <c>.lva</c> format (round trips, lazy bodies, speed).</summary>
public sealed class HirTests : CheckedTest
{
    [Fact]
    public void Lva_round_trip_keeps_metadata_text_and_bytes()
    {
        var sample = Sample();
        string printed = HirPrinter.Print(sample, types: true);
        var bytes = LvaFormat.Write(sample);
        var read = LvaFormat.Read(bytes);
        var main = read.Types[^1].Methods[^1];
        Check("bodies stay in the file until used", main.BodyPending);
        Check("metadata reads back (types, members, refs, attributes, resources, entry)",
            read.Name == "Sample" && read.Types.Count == sample.Types.Count && read.MemberRefs.Count == sample.MemberRefs.Count
            && read.EntryPoint == sample.EntryPoint && read.Resources[0].Data.SequenceEqual(sample.Resources[0].Data));
        string reprinted = HirPrinter.Print(read, types: true);
        Check($"text form is the same after .lva ({bytes.Length} bytes)", printed == reprinted, FirstDifference(printed, reprinted));
        Check("writing what was read gives the same bytes", LvaFormat.Write(read).AsSpan().SequenceEqual(bytes));
        Check("types are structural values", new NamedType(TypeToken.Def(1), [HirType.I32]) == new NamedType(TypeToken.Def(1), [new PrimitiveType(Primitive.I32)]));
        string plain = HirPrinter.Print(sample);
        Check("the text form names things", plain.Contains("class Sample.Box<T : value numeric") && plain.Contains("[System.Core]System.String::Concat") && plain.Contains("extern \"time.sleep\""), plain.Length > 400 ? plain[..400] : plain);
    }

    [Fact]
    public void Damaged_or_newer_lva_files_are_refused()
    {
        static bool Refused(byte[] data)
        {
            try
            {
                var a = LvaFormat.Read(data);
                foreach (var t in a.Types)
                    foreach (var m in t.Methods)
                        _ = m.Body;
                return false;
            }
            catch (HirFormatException)
            {
                return true;
            }
        }
        var bytes = LvaFormat.Write(Sample());
        Check("not an .lva file is refused", Refused([1, 2, 3, 4]));
        Check("a cut file is refused", Refused(bytes[..(bytes.Length / 2)]));
        var newer = (byte[])bytes.Clone();
        newer[3] = 99;
        Check("a newer format version is refused", Refused(newer));
    }

    /// <summary>
    /// A library the size of a big base library (4 000 types, 40 000 methods): writing it, reading its metadata (what a
    /// reference costs), and reading every body.
    /// </summary>
    [Fact]
    public void A_40000_method_library_writes_and_reads_fast()
    {
        var watch = Stopwatch.StartNew();
        var big = Synthetic(4000, 10);
        long build = watch.ElapsedMilliseconds;
        watch.Restart();
        var bytes = LvaFormat.Write(big);
        long write = watch.ElapsedMilliseconds;
        // Warm the reader once (the first run includes the JIT).
        LvaFormat.Read(bytes);
        watch.Restart();
        var read = LvaFormat.Read(bytes);
        long meta = watch.ElapsedMilliseconds;
        watch.Restart();
        int statements = 0;
        foreach (var t in read.Types)
            foreach (var m in t.Methods)
                statements += m.Body!.Block.Statements.Length;
        long allBodies = watch.ElapsedMilliseconds;
        TestContext.Current.TestOutputHelper?.WriteLine($"40 000 methods: build {build} ms, write {write} ms, {bytes.Length / 1024} KB; read metadata {meta} ms, then every body {allBodies} ms");
        Check("every body reads back", statements == 40000 * 6);
        Check("reading the metadata of a 40 000-method library takes under 150 ms", meta < 150, $"{meta} ms");
        Check("writing it takes under 1 s", write < 1000, $"{write} ms");
    }

    /// <summary>An assembly that uses every kind of declaration, statement, expression, pattern, constant and type.</summary>
    public static HirAssembly Sample()
    {
        var a = new HirAssembly { Name = "Sample", Version = [1, 2, 3] };
        a.Files.Add("Sample/Main.ant");
        var str = a.Reference("System.Core", "System", "String");
        var obj = a.Reference("System.Core", "System", "Any");
        var list = a.Reference("System.Collections", "System.Collections", "List", 1);
        var strType = HirType.Named(str);
        var objType = HirType.Named(obj);
        var concat = a.Reference(new MemberRef(MemberKind.Method, str, "Concat", 0, [strType, strType], strType));
        var add = a.Reference(new MemberRef(MemberKind.Method, list, "Add", 0, [new GenericParamType(GenericOwner.Type, 0)], HirType.Void));
        var count = a.Reference(new MemberRef(MemberKind.Property, list, "Count", 0, [], HirType.I32));
        var objCtor = a.Reference(new MemberRef(MemberKind.Method, obj, ".new", 0, [], HirType.Void));

        // An attribute type, an enum, a struct, an interface, a class with everything, a func type.
        var attr = new TypeDef { Namespace = "Sample", Name = "Note", Kind = TypeKind.Class, Flags = TypeFlags.Final, Base = objType };
        var attrToken = a.Add(attr);
        attr.AddField(new FieldDef("Text", strType) { Access = Access.Public });
        int attrCtor = attr.AddMethod(new MethodDef(".new", HirType.Void, new Parameter("text", strType)) { Kind = MethodKind.Constructor, Body = new HirBody([], new BlockStmt([])) });
        var note = new HirAttribute(MemberToken.Def(attrToken.Index, attrCtor), [new StringConstant("hello \"world\"\n")])
        {
            Named = [("Level", HirConstant.Int(3))],
        };

        var color = new TypeDef { Namespace = "Sample", Name = "Color", Kind = TypeKind.Enum, Underlying = HirType.U8 };
        var colorToken = a.Add(color);
        var colorType = HirType.Named(colorToken);
        color.AddField(new FieldDef("Red", colorType) { Flags = FieldFlags.Static | FieldFlags.Const, Value = new EnumConstant(colorType, 0) });
        color.AddField(new FieldDef("Green", colorType) { Flags = FieldFlags.Static | FieldFlags.Const, Value = new EnumConstant(colorType, 1) });

        var point = new TypeDef { Namespace = "Sample", Name = "Point", Kind = TypeKind.Struct, Flags = TypeFlags.Record, Location = new SourceLocation(0, 3, 1) };
        var pointToken = a.Add(point);
        var pointType = HirType.Named(pointToken);
        int px = point.AddField(new FieldDef("X", HirType.I32) { Access = Access.Public });
        point.AddField(new FieldDef("Y", HirType.I32) { Access = Access.Public, Attributes = [note] });
        point.AddField(new FieldDef("Cells", new InlineArrayType(HirType.U8, 4)));

        var shape = new TypeDef { Namespace = "Sample", Name = "IShape", Kind = TypeKind.Interface };
        var shapeToken = a.Add(shape);
        shape.AddMethod(new MethodDef("Area", HirType.F64) { Flags = MethodFlags.Abstract | MethodFlags.Virtual, Access = Access.Public });

        var callback = new TypeDef { Namespace = "Sample", Name = "Callback", Kind = TypeKind.Func };
        a.Add(callback);
        callback.AddMethod(new MethodDef("Invoke", HirType.Bool, new Parameter("value", HirType.I32)) { Flags = MethodFlags.Abstract });

        var box = new TypeDef { Namespace = "Sample", Name = "Box", Kind = TypeKind.Class, Base = objType, Access = Access.Public };
        var boxToken = a.Add(box);
        box.Interfaces.Add(HirType.Named(shapeToken));
        box.Generics.Add(new GenericParam("T") { Kinds = ConstraintKinds.Value | ConstraintKinds.Numeric, OneOf = [HirType.I32, HirType.I64], Operators = [new OperatorRequirement("+", [new GenericParamType(GenericOwner.Type, 0), new GenericParamType(GenericOwner.Type, 0)], new GenericParamType(GenericOwner.Type, 0))], ConvertsTo = [HirType.F64], Default = HirType.I32 });
        box.Generics.Add(new GenericParam("Rest", Variadic: true) { Types = [objType] });
        box.Attributes.Add(note);
        var t0 = new GenericParamType(GenericOwner.Type, 0);
        var boxOfT = HirType.Named(boxToken, t0, new PackExpansionType(new GenericParamType(GenericOwner.Type, 1)));
        int value = box.AddField(new FieldDef("value", t0) { Access = Access.Private });
        int counter = box.AddField(new FieldDef("counter", HirType.I64) { Flags = FieldFlags.Static, Value = HirConstant.Int(0, Primitive.I64) });
        box.AddField(new FieldDef("Pi", HirType.F64) { Flags = FieldFlags.Static | FieldFlags.Const, Value = FloatConstant.Of(Math.PI), Access = Access.Public });
        box.AddField(new FieldDef("Big", new PrimitiveType(Primitive.I256)) { Flags = FieldFlags.Static | FieldFlags.Const, Value = new IntegerConstant(System.Numerics.BigInteger.Pow(2, 200), Primitive.I256) });
        box.AddField(new FieldDef("Table", new ArrayType(HirType.I32)) { Flags = FieldFlags.Static, Value = new ArrayConstant(HirType.I32, [HirConstant.Int(1), HirConstant.Int(-2)]) });
        int getter = box.AddMethod(new MethodDef("get_Value", t0) { Kind = MethodKind.Getter, Access = Access.Public, Body = new HirBody([], new BlockStmt([new ReturnStmt(new FieldExpr(new ThisExpr(boxOfT), MemberToken.Def(boxToken.Index, value), t0))])) });
        box.Properties.Add(new PropertyDef("Value", t0) { Getter = getter, Access = Access.Public });
        box.Events.Add(new EventDef("Changed", new FuncType([t0], HirType.Void)) { Adder = getter, Remover = getter });
        box.Operators.Add(new OperatorDef("<+>", Fixity.Binary, getter) { Precedence = 131, Associativity = Associativity.Right });
        box.AddMethod(new MethodDef("Area", HirType.F64) { Flags = MethodFlags.Override | MethodFlags.Virtual, Access = Access.Public, Body = new HirBody([], new BlockStmt([new ReturnStmt(new LiteralExpr(FloatConstant.Of(1.5), HirType.F64))])) });
        box.AddMethod(new MethodDef("Sleep", HirType.Void, new Parameter("ms", HirType.I64)) { Flags = MethodFlags.Static | MethodFlags.Extern, ExternName = "time.sleep" });
        box.AddMethod(new MethodDef("MessageBox", HirType.I32, new Parameter("text", new PointerType(HirType.U8, false))) { Flags = MethodFlags.Static | MethodFlags.Import, ExternName = "MessageBoxA", Library = "user32.dll" });

        // The big body: every statement, expression and pattern.
        var locals = new List<HirLocal>();
        int Local(string name, HirType type, bool mutable = true) { locals.Add(new HirLocal(name, type, mutable)); return locals.Count - 1; }
        int i = Local("i", HirType.I32), s = Local("s", strType), o = Local("o", objType), arr = Local("arr", new ArrayType(HirType.I32, 2));
        int p = Local("p", pointType, mutable: false), lambdaArg = Local("x", HirType.I32), f = Local("f", new FuncType([HirType.I32], HirType.Bool));
        int e = Local("e", objType), r = Local("r", objType), bound = Local("n", HirType.I32), rest = Local("rest", new ArrayType(HirType.I32));
        int ptr = Local("ptr", new PointerType(HirType.I32)), maybe = Local("maybe", new NullableType(HirType.I32)), each = Local("each", HirType.I32);
        var I = HirType.I32;
        LiteralExpr Int(int v) => new(HirConstant.Int(v), I);
        LocalExpr L(int l) => new(l, locals[l].Type);
        var at = new SourceLocation(0, 10, 5);
        var statements = new List<Stmt>
        {
            new LocalStmt(i, Int(0)) { At = at },
            new LocalStmt(s, new LiteralExpr(new StringConstant("héllo ✓"), strType)),
            new LocalStmt(o, new NewExpr(objCtor, [], [], objType)),
            new LocalStmt(arr, new NewArrayExpr(I, [Int(2), Int(3)], locals[arr].Type)),
            new LocalStmt(p, new NewExpr(null, [], [new MemberInit(MemberToken.Def(pointToken.Index, px), Int(4))], pointType)),
            new LocalStmt(f, new LambdaExpr([lambdaArg], new ReturnStmt(new BinaryExpr(BinaryOp.Greater, L(lambdaArg), Int(1), HirType.Bool)), locals[f].Type)),
            new LocalStmt(maybe, new ConvertExpr(Int(7), ConversionKind.Wrap, locals[maybe].Type)),
            new ExprStmt(new AssignExpr(L(i), Int(2), I) { Compound = BinaryOp.Add }),
            new ExprStmt(new UnaryExpr(UnaryOp.PostIncrement, L(i), I)),
            new ExprStmt(new AssignExpr(new ElementExpr(L(arr), [Int(0), Int(1)], I), new InvokeExpr(L(f), [Int(3)], HirType.Bool) is var call ? new ConditionalExpr(call, Int(1), Int(0), I) : null!, I)),
            new ExprStmt(new CallExpr(null, concat, [], [L(s), new InterpolatedExpr([new InterpolationPart(L(i), "x4", -6), new InterpolationPart(new LiteralExpr(new StringConstant("!"), strType))], strType)], strType)),
            new ExprStmt(new CallExpr(new LiteralExpr(new NullConstant(), objType), add, [I], [Int(1)], HirType.Void) { NonVirtual = true }),
            new ExprStmt(new PropertyExpr(L(o), count, [], I)),
            new ExprStmt(new FieldExpr(null, MemberToken.Def(boxToken.Index, counter), HirType.I64)),
            new ExprStmt(new MethodRefExpr(null, MemberToken.Def(boxToken.Index, 2), [], new FuncType([], HirType.F64))),
            new ExprStmt(new CoalesceExpr(L(maybe), Int(0), I)),
            new ExprStmt(new ConditionalAccessExpr(L(o), new PropertyExpr(new ReceiverExpr(objType), count, [], I), new NullableType(I))),
            new ExprStmt(new AsExpr(L(o), strType)),
            new ExprStmt(new ConvertExpr(L(o), ConversionKind.User, strType) { Method = concat }),
            new ExprStmt(new TypeOfExpr(boxOfT, objType)),
            new ExprStmt(new SizeOfExpr(pointType, HirType.I64)),
            new ExprStmt(new DefaultExpr(pointType)),
            new ExprStmt(new WithExpr(L(p), [new MemberInit(MemberToken.Def(pointToken.Index, px), Int(9))], pointType)),
            new ExprStmt(new RangeExpr(Int(1), null, Inclusive: true, objType)),
            new ExprStmt(new ArrayLiteralExpr(I, [Int(1), Int(2)], new ArrayType(I))),
            new ExprStmt(new AwaitExpr(L(o), I)),
            new ExprStmt(new BorrowExpr(L(p), true, new ReferenceType(pointType, true))),
            new ExprStmt(new BlockExpr([new ExprStmt(Int(1))], Int(2), I)),
            new ExprStmt(new LiteralExpr(new CharConstant('λ'), HirType.Char)),
            new ExprStmt(new LiteralExpr(new TypeConstant(new NativeFuncType([I], I)), objType)),
            new ExprStmt(new LiteralExpr(new BoolConstant(true), HirType.Bool)),
            new ExprStmt(new ParamExpr(0, I)),
            new ExprStmt(new SwitchExpr(L(i), [
                new SwitchArm(new RelationalPattern(BinaryOp.Less, HirConstant.Int(0)), null, Int(-1)),
                new SwitchArm(new RangePattern(HirConstant.Int(0), HirConstant.Int(9), true), new BinaryExpr(BinaryOp.NotEqual, L(i), Int(5), HirType.Bool), Int(1)),
                new SwitchArm(new DiscardPattern(), null, Int(2)),
            ], I)),
            new ExprStmt(new IsExpr(L(o), new AndPattern(new TypePattern(strType), new NotPattern(new ConstantPattern(new StringConstant("")))), HirType.Bool)),
            new ExprStmt(new IsExpr(L(p), new PropertyPattern(pointType, [new PropertySubpattern(MemberToken.Def(pointToken.Index, px), new BindingPattern(bound, new OrPattern(new ConstantPattern(HirConstant.Int(1)), new ConstantPattern(HirConstant.Int(2)))))]), HirType.Bool)),
            new ExprStmt(new IsExpr(L(p), new PositionalPattern(pointType, null, [new BindingPattern(bound, null), new DiscardPattern()]), HirType.Bool)),
            new ExprStmt(new IsExpr(L(arr), new ListPattern([new ConstantPattern(HirConstant.Int(1)), new DiscardPattern()], 1, rest), HirType.Bool)),
            new IfStmt(new BinaryExpr(BinaryOp.And, new LiteralExpr(new BoolConstant(true), HirType.Bool), new LiteralExpr(new BoolConstant(false), HirType.Bool), HirType.Bool),
                new BlockStmt([new BreakStmt()]), new ContinueStmt(0)),
            new WhileStmt(new BinaryExpr(BinaryOp.Less, L(i), Int(10), HirType.Bool), new BlockStmt([new ContinueStmt()]), 0),
            new DoWhileStmt(new BlockStmt([]), new LiteralExpr(new BoolConstant(false), HirType.Bool)),
            new ForStmt([new LocalStmt(bound, Int(0))], new BinaryExpr(BinaryOp.Less, L(bound), Int(3), HirType.Bool), [new UnaryExpr(UnaryOp.PreIncrement, L(bound), I)], new BlockStmt([]), 1),
            new ForeachStmt(each, new RangeExpr(Int(0), Int(3), false, objType), new BlockStmt([new YieldStmt(L(each)), new YieldStmt(null)])),
            new SwitchStmt(L(i), [new SwitchSection([new ConstantPattern(HirConstant.Int(1)), new ConstantPattern(HirConstant.Int(2))], null, new BreakStmt()), new SwitchSection([new DiscardPattern()], new LiteralExpr(new BoolConstant(true), HirType.Bool), new BlockStmt([]))]),
            new TryStmt(new BlockStmt([new ThrowStmt(L(o))]),
                [new CatchClause(strType, e, new BinaryExpr(BinaryOp.Equal, Int(1), Int(1), HirType.Bool), new BlockStmt([new ThrowStmt(null)])), new CatchClause(null, -1, null, new BlockStmt([]))],
                new BlockStmt([new DeleteStmt(L(o))])),
            new UsingStmt(r, L(o), new BlockStmt([])),
            new RegionStmt(new BlockStmt([new ExprStmt(new NewExpr(objCtor, [], [], objType))])),
            new UnsafeStmt(new BlockStmt([
                new LocalStmt(ptr, new AddressOfExpr(L(i), locals[ptr].Type)),
                new ExprStmt(new AssignExpr(new DerefExpr(L(ptr), I), Int(5), I)),
                new ExprStmt(new ConvertExpr(L(ptr), ConversionKind.Pointer, HirType.I64)),
            ])),
            new ReturnStmt(Int(0)) { At = new SourceLocation(0, 40, 9) },
        };
        var main = new MethodDef("Main", HirType.I32, new Parameter("argc", HirType.I32) { Flags = ParameterFlags.Optional, Default = HirConstant.Int(0), Attributes = [note] })
        {
            Flags = MethodFlags.Static | MethodFlags.Async,
            Access = Access.Public,
            Location = new SourceLocation(0, 9, 1),
            Body = new HirBody([.. locals], new BlockStmt([.. statements])),
        };
        main.Generics.Add(new GenericParam("U") { Kinds = ConstraintKinds.New });
        main.Attributes.Add(note);
        int mainIndex = box.AddMethod(main);
        a.EntryPoint = MemberToken.Def(boxToken.Index, mainIndex);
        a.Attributes.Add(note);
        a.Resources.Add(new HirResource("data/blob.bin", [1, 2, 3, 250]));
        return a;
    }

    private static string FirstDifference(string a, string b)
    {
        var x = a.Split('\n');
        var y = b.Split('\n');
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            if (x[i] != y[i])
                return $"line {i + 1}:\n    {x[i]}\n    {y[i]}";
        }
        return x.Length == y.Length ? "" : $"{x.Length} lines vs {y.Length}";
    }

    public static HirAssembly Synthetic(int typeCount, int methodsPerType)
    {
        var a = new HirAssembly { Name = "Big" };
        var str = HirType.Named(a.Reference("System.Core", "System", "String"));
        var I = HirType.I32;
        for (int t = 0; t < typeCount; t++)
        {
            var type = new TypeDef { Namespace = $"Big.N{t % 50}", Name = $"Type{t}", Kind = t % 3 == 0 ? TypeKind.Struct : TypeKind.Class };
            var token = a.Add(type);
            int field = type.AddField(new FieldDef("value", I));
            type.AddField(new FieldDef("name", str));
            for (int m = 0; m < methodsPerType; m++)
            {
                var self = new ThisExpr(HirType.Named(token));
                var x = new ParamExpr(0, I);
                var locals = new[] { new HirLocal("acc", I), new HirLocal("k", I) };
                var acc = new LocalExpr(0, I);
                var k = new LocalExpr(1, I);
                var body = new BlockStmt([
                    new LocalStmt(0, new FieldExpr(self, MemberToken.Def(t, field), I)),
                    new LocalStmt(1, new LiteralExpr(HirConstant.Int(m), I)),
                    new WhileStmt(new BinaryExpr(BinaryOp.Less, k, x, HirType.Bool), new BlockStmt([
                        new ExprStmt(new AssignExpr(acc, new BinaryExpr(BinaryOp.Multiply, k, new LiteralExpr(HirConstant.Int(3), I), I), I) { Compound = BinaryOp.Add }),
                        new ExprStmt(new UnaryExpr(UnaryOp.PreIncrement, k, I)),
                    ])),
                    new IfStmt(new BinaryExpr(BinaryOp.Greater, acc, new LiteralExpr(HirConstant.Int(1000), I), HirType.Bool), new ReturnStmt(new LiteralExpr(HirConstant.Int(1000), I)), null),
                    new ExprStmt(new CallExpr(self, MemberToken.Def(t, (m + 1) % methodsPerType), [], [acc], I)),
                    new ReturnStmt(acc),
                ]);
                type.AddMethod(new MethodDef($"M{m}", I, new Parameter("x", I)) { Body = new HirBody(locals, body), Location = new SourceLocation(0, m * 10, 1) });
            }
        }
        a.Files.Add("Big.ant");
        return a;
    }
}

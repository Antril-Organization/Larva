using Larva.Hir;
using static Larva.Hir.Tests.E;
using H = Larva.Hir;

namespace Larva.Hir.Tests;

/// <summary>
/// The HIR programs of the lowering tests: phase 2 (classes, structs, methods, control flow and exceptions), a large
/// synthetic program, and the benchmarks (the same programs as <see cref="Benchmarks.Reference"/>).
/// </summary>
internal static class HirPrograms
{
    public static HirProgram Phase2()
    {
        var p = new HirProgram("Phase2");

        // class Error { i32 Code; new(i32 code) { Code = code; } }
        var (error, errorDef) = p.Type("Error", TypeKind.Class);
        var code = HirProgram.Field(error, errorDef, "Code", I);
        var errorNew = HirProgram.Method(error, errorDef, ".new", HirType.Void, MethodFlags.None, MethodKind.Constructor, HirProgram.P("code", I));
        new MethodBuilder(errorDef.Methods[^1]).Do(S(Set(F(This(HirProgram.T(error)), code, I), Arg(0)))).Done();

        // abstract class Animal { i32 Legs; new(i32 legs); virtual Speak() => 1; abstract Kind(); }
        var (animal, animalDef) = p.Type("Animal", TypeKind.Class, flags: TypeFlags.Abstract);
        var A = HirProgram.T(animal);
        var legs = HirProgram.Field(animal, animalDef, "Legs", I);
        var animalNew = HirProgram.Method(animal, animalDef, ".new", HirType.Void, MethodFlags.None, MethodKind.Constructor, HirProgram.P("legs", I));
        new MethodBuilder(animalDef.Methods[^1]).Do(S(Set(F(This(A), legs, I), Arg(0)))).Done();
        var speak = HirProgram.Method(animal, animalDef, "Speak", I, MethodFlags.Virtual);
        new MethodBuilder(animalDef.Methods[^1]).Do(Ret(Int(1))).Done();
        var kind = HirProgram.Method(animal, animalDef, "Kind", I, MethodFlags.Virtual | MethodFlags.Abstract);

        // class Dog : Animal { new() : base(4); override Speak() => 10 + base.Speak(); override Kind() => 2; }
        var (dog, dogDef) = p.Type("Dog", TypeKind.Class, animal);
        var D = HirProgram.T(dog);
        var dogNew = HirProgram.Method(dog, dogDef, ".new", HirType.Void, MethodFlags.None, MethodKind.Constructor);
        new MethodBuilder(dogDef.Methods[^1]).Do(S(new CallExpr(This(D), animalNew, [], [Int(4)], HirType.Void) { NonVirtual = true })).Done();
        var dogSpeak = HirProgram.Method(dog, dogDef, "Speak", I, MethodFlags.Virtual | MethodFlags.Override);
        new MethodBuilder(dogDef.Methods[^1]).Do(Ret(Add(Int(10), new CallExpr(This(D), speak, [], [], I) { NonVirtual = true }))).Done();
        HirProgram.Method(dog, dogDef, "Kind", I, MethodFlags.Virtual | MethodFlags.Override);
        new MethodBuilder(dogDef.Methods[^1]).Do(Ret(Int(2))).Done();

        // class Puppy : Dog { new() : base(); override Speak() => 100 + base.Speak(); }
        var (puppy, puppyDef) = p.Type("Puppy", TypeKind.Class, dog);
        var Pu = HirProgram.T(puppy);
        var puppyNew = HirProgram.Method(puppy, puppyDef, ".new", HirType.Void, MethodFlags.None, MethodKind.Constructor);
        new MethodBuilder(puppyDef.Methods[^1]).Do(S(new CallExpr(This(Pu), dogNew, [], [], HirType.Void) { NonVirtual = true })).Done();
        HirProgram.Method(puppy, puppyDef, "Speak", I, MethodFlags.Virtual | MethodFlags.Override);
        new MethodBuilder(puppyDef.Methods[^1]).Do(Ret(Add(Int(100), new CallExpr(This(Pu), dogSpeak, [], [], I) { NonVirtual = true }))).Done();

        // struct Point { i32 X, Y; new(x, y); Dot() => X*X + Y*Y; Scale(k) { X *= k; Y *= k; } }
        var (point, pointDef) = p.Type("Point", TypeKind.Struct);
        var Pt = HirProgram.T(point);
        var x = HirProgram.Field(point, pointDef, "X", I);
        var y = HirProgram.Field(point, pointDef, "Y", I);
        var pointNew = HirProgram.Method(point, pointDef, ".new", HirType.Void, MethodFlags.None, MethodKind.Constructor, HirProgram.P("x", I), HirProgram.P("y", I));
        new MethodBuilder(pointDef.Methods[^1]).Do(S(Set(F(This(Pt), x, I), Arg(0))), S(Set(F(This(Pt), y, I), Arg(1)))).Done();
        var dot = HirProgram.Method(point, pointDef, "Dot", I, MethodFlags.None);
        new MethodBuilder(pointDef.Methods[^1]).Do(Ret(Add(Mul(F(This(Pt), x, I), F(This(Pt), x, I)), Mul(F(This(Pt), y, I), F(This(Pt), y, I))))).Done();
        var scale = HirProgram.Method(point, pointDef, "Scale", HirType.Void, MethodFlags.None, MethodKind.Normal, HirProgram.P("k", I));
        new MethodBuilder(pointDef.Methods[^1]).Do(S(MulTo(F(This(Pt), x, I), Arg(0))), S(MulTo(F(This(Pt), y, I), Arg(0)))).Done();

        // static class Program
        var (prog, progDef) = p.Type("Program", TypeKind.Class, flags: TypeFlags.Static | TypeFlags.Abstract);
        var counter = HirProgram.Field(prog, progDef, "Counter", I, FieldFlags.Static);
        var C = F(null, counter, I);
        MethodBuilder M(string name, params Parameter[] parameters)
        {
            HirProgram.Method(prog, progDef, name, I, MethodFlags.Static, MethodKind.Normal, parameters);
            return new MethodBuilder(progDef.Methods[^1]);
        }
        MemberToken Last() => MemberToken.Def(prog.Index, progDef.Methods.Count - 1);
        var n = HirProgram.P("n", I);

        // Sum(n): i = 0, s = 0; while (i < n) { s += i; i++; } return s;
        {
            var m = M("Sum", n);
            var i = m.Local("i", I);
            var s = m.Local("s", I);
            m.Do(Let(i, Int(0)), Let(s, Int(0)), While(Lt(i, Arg(0)), S(AddTo(s, i)), S(Inc(i))), Ret(s)).Done();
        }
        // Fib(n): n < 2 ? n : Fib(n - 1) + Fib(n - 2)
        {
            var m = M("Fib", n);
            var fib = Last();
            m.Do(If(Lt(Arg(0), Int(2)), Ret(Arg(0))), Ret(Add(Call(fib, I, Sub(Arg(0), Int(1))), Call(fib, I, Sub(Arg(0), Int(2)))))).Done();
        }
        // Loops(): for (k = 0; k < 10; k++) { if (k == 3) continue; if (k == 7) break; s += k; } foreach (j in 0..=3) s += j * 100;
        {
            var m = M("Loops");
            var s = m.Local("s", I);
            var k = m.Local("k", I);
            var j = m.Local("j", I);
            m.Do(Let(s, Int(0)),
                new ForStmt([Let(k, Int(0))], Lt(k, Int(10)), [Inc(k)], Blk(
                    If(Eq(k, Int(3)), new ContinueStmt()),
                    If(Eq(k, Int(7)), new BreakStmt()),
                    S(AddTo(s, k)))),
                new ForeachStmt(j.Local, new RangeExpr(Int(0), Int(3), true, I), S(AddTo(s, Mul(j, Int(100))))),
                Ret(s)).Done();
        }
        // Classify(n): switch (n) { case 0: return 10; case 1 or 2: return 20; case < 0: return 30; default: return 40; }
        {
            var m = M("Classify", n);
            m.Do(new SwitchStmt(Arg(0), [
                new SwitchSection([new ConstantPattern(HirConstant.Int(0))], null, Ret(Int(10))),
                new SwitchSection([new ConstantPattern(HirConstant.Int(1)), new ConstantPattern(HirConstant.Int(2))], null, Ret(Int(20))),
                new SwitchSection([new RelationalPattern(BinaryOp.Less, HirConstant.Int(0))], null, Ret(Int(30))),
                new SwitchSection([new DiscardPattern()], null, Ret(Int(40))),
            ]), Ret(Int(-1))).Done();
        }
        // Grade(n) => n switch { >= 90 => 4, 80..90 => 3, 70..90 => 2, _ => 0 }
        {
            var m = M("Grade", n);
            m.Do(Ret(new SwitchExpr(Arg(0), [
                new SwitchArm(new RelationalPattern(BinaryOp.GreaterEqual, HirConstant.Int(90)), null, Int(4)),
                new SwitchArm(new RangePattern(HirConstant.Int(80), HirConstant.Int(90), false), null, Int(3)),
                new SwitchArm(new RangePattern(HirConstant.Int(70), HirConstant.Int(90), false), null, Int(2)),
                new SwitchArm(new DiscardPattern(), null, Int(0)),
            ], I))).Done();
        }
        // Structs(): p = new Point(3, 4); p.Scale(2); q = p; q.X = 0; return p.Dot() * 1000 + q.Dot();
        {
            var m = M("Structs");
            var a = m.Local("p", Pt);
            var b = m.Local("q", Pt);
            m.Do(Let(a, New(Pt, pointNew, Int(3), Int(4))),
                S(CallOn(a, scale, HirType.Void, Int(2))),
                Let(b, a),
                S(Set(F(b, x, I), Int(0))),
                Ret(Add(Mul(CallOn(a, dot, I), Int(1000)), CallOn(b, dot, I)))).Done();
        }
        // Animals(): Animal a = new Dog(); Animal b = new Puppy(); return a.Speak()*1000 + b.Speak() + a.Kind()*1000000 + a.Legs;
        {
            var m = M("Animals");
            var a = m.Local("a", A);
            var b = m.Local("b", A);
            m.Do(Let(a, new ConvertExpr(New(D, dogNew), ConversionKind.Upcast, A)),
                Let(b, new ConvertExpr(New(Pu, puppyNew), ConversionKind.Upcast, A)),
                Ret(Add(Add(Add(Mul(CallOn(a, speak, I), Int(1000)), CallOn(b, speak, I)), Mul(CallOn(a, kind, I), Int(1000000))), F(a, legs, I)))).Done();
        }
        // Types(): Animal a = new Puppy(); r = 0; if (a is Dog) r += 1; if (a is Puppy) r += 10; d = a as Dog; if (d != null) r += 100;
        //          Animal c = new Dog(); if (!(c is Puppy)) r += 1000; return r;
        {
            var m = M("Types");
            var a = m.Local("a", A);
            var c = m.Local("c", A);
            var d = m.Local("d", new NullableType(D));
            var r = m.Local("r", I);
            m.Do(Let(r, Int(0)),
                Let(a, new ConvertExpr(New(Pu, puppyNew), ConversionKind.Upcast, A)),
                If(Is(a, D), S(AddTo(r, Int(1)))),
                If(Is(a, Pu), S(AddTo(r, Int(10)))),
                Let(d, new AsExpr(a, new NullableType(D))),
                If(Ne(d, Null(new NullableType(D))), S(AddTo(r, Int(100)))),
                Let(c, new ConvertExpr(New(D, dogNew), ConversionKind.Upcast, A)),
                If(Not(Is(c, Pu)), S(AddTo(r, Int(1000)))),
                Ret(r)).Done();
        }
        // Throws(n): if (n < 0) throw new Error(n); return n * 2;
        var throws = default(MemberToken);
        {
            var m = M("Throws", n);
            throws = Last();
            m.Do(If(Lt(Arg(0), Int(0)), Throw(New(HirProgram.T(error), errorNew, Arg(0)))), Ret(Mul(Arg(0), Int(2)))).Done();
        }
        var E_ = HirProgram.T(error);
        // Catches(n): try { return Throws(n); } catch (Error e) { return e.Code - 1000; }
        {
            var m = M("Catches", n);
            var e = m.Local("e", E_);
            m.Do(Try(Blk(Ret(Call(throws, I, Arg(0)))), [new H.CatchClause(E_, e.Local, null, Blk(Ret(Sub(F(e, code, I), Int(1000)))))]), Ret(Int(0))).Done();
        }
        // Finally(n): Counter = 0; try { try { if (n < 0) throw new Error(1); Counter += 1; } finally { Counter += 10; } } catch { Counter += 100; } return Counter;
        {
            var m = M("Finally", n);
            m.Do(S(Set(C, Int(0))),
                Try(Blk(Try(Blk(If(Lt(Arg(0), Int(0)), Throw(New(E_, errorNew, Int(1)))), S(AddTo(C, Int(1)))), [], Blk(S(AddTo(C, Int(10)))))),
                    [new H.CatchClause(null, -1, null, Blk(S(AddTo(C, Int(100)))))]),
                Ret(C)).Done();
        }
        // ReturnFinally(): Counter = 0; r = Inner(); return r * 10 + Counter;  Inner(): try { return 5; } finally { Counter = 7; }
        {
            var inner = M("ReturnFinallyInner");
            var innerToken = Last();
            inner.Do(Try(Blk(Ret(Int(5))), [], Blk(S(Set(C, Int(7))))), Ret(Int(0))).Done();
            var m = M("ReturnFinally");
            var r = m.Local("r", I);
            m.Do(S(Set(C, Int(0))), Let(r, Call(innerToken, I)), Ret(Add(Mul(r, Int(10)), C))).Done();
        }
        // BreakFinally(): s = 0; while (true) { try { s += 1; if (s == 3) break; } finally { s += 100; } } return s;
        {
            var m = M("BreakFinally");
            var s = m.Local("s", I);
            m.Do(Let(s, Int(0)),
                While(True, Try(Blk(S(AddTo(s, Int(1))), If(Eq(s, Int(203)), new BreakStmt())), [], Blk(S(AddTo(s, Int(100)))))),
                Ret(s)).Done();
        }
        // Rethrow(n): try { try { Throws(n); } catch (Error e) { Counter = e.Code; throw; } } catch (Error e2) { return e2.Code * 2 + Counter; } return 0;
        {
            var m = M("Rethrow", n);
            var e = m.Local("e", E_);
            var e2 = m.Local("e2", E_);
            m.Do(Try(Blk(Try(Blk(S(Call(throws, I, Arg(0)))), [new H.CatchClause(E_, e.Local, null, Blk(S(Set(C, F(e, code, I))), Throw(null)))])),
                    [new H.CatchClause(E_, e2.Local, null, Blk(Ret(Add(Mul(F(e2, code, I), Int(2)), C))))]),
                Ret(Int(0))).Done();
        }
        // Nulls(): Animal? a = null; r = 0; if (a == null) r += 1; Animal x = a ?? new Dog(); r += x.Speak(); return r;
        {
            var m = M("Nulls");
            var nullable = new NullableType(A);
            var a = m.Local("a", nullable);
            var r = m.Local("r", I);
            var xs = m.Local("x", A);
            m.Do(Let(a, Null(nullable)), Let(r, Int(0)),
                If(Eq(a, Null(nullable)), S(AddTo(r, Int(1)))),
                Let(xs, new CoalesceExpr(a, new ConvertExpr(New(D, dogNew), ConversionKind.Upcast, A), A)),
                S(AddTo(r, CallOn(xs, speak, I))),
                Ret(r)).Done();
        }
        // Statics(): Counter = 5; Counter += 3; return Counter;
        M("Statics").Do(S(Set(C, Int(5))), S(AddTo(C, Int(3))), Ret(C)).Done();
        return p;
    }

    /// <summary>A program of <paramref name="classes"/> classes in chains of three, each with virtual methods that loop and call.</summary>
    public static HirProgram Synthetic(int classes, int methodsPerClass)
    {
        var p = new HirProgram("Big");
        TypeToken? previous = null;
        var virtuals = new List<MemberToken>();
        for (int c = 0; c < classes; c++)
        {
            bool root = c % 3 == 0;
            var (type, def) = p.Type($"C{c}", TypeKind.Class, root ? null : previous);
            var T = HirProgram.T(type);
            var value = HirProgram.Field(type, def, "value", I);
            if (root)
                virtuals.Clear();
            for (int m = 0; m < methodsPerClass; m++)
            {
                bool overrides = !root && m < virtuals.Count && m % 2 == 0;
                var flags = overrides ? MethodFlags.Virtual | MethodFlags.Override : m % 2 == 0 ? MethodFlags.Virtual : MethodFlags.None;
                var token = HirProgram.Method(type, def, overrides ? def.Methods.Count == m ? $"M{m}" : $"M{m}" : $"M{m}", I, flags, MethodKind.Normal, HirProgram.P("x", I));
                if (root && m % 2 == 0)
                    virtuals.Add(token);
                var mb = new MethodBuilder(def.Methods[^1]);
                var acc = mb.Local("acc", I);
                var k = mb.Local("k", I);
                mb.Do(Let(acc, F(This(T), value, I)), Let(k, Int(m)),
                    While(Lt(k, Arg(0)), S(AddTo(acc, Mul(k, Int(3)))), S(Inc(k))),
                    If(Lt(Int(1000), acc), Ret(Int(1000))),
                    S(Set(F(This(T), value, I), acc)),
                    Ret(m + 1 < methodsPerClass && virtuals.Count > 0 ? Add(acc, CallOn(This(T), virtuals[0], I, acc)) : acc)).Done();
            }
            previous = type;
        }
        return p;
    }

    /// <summary>The benchmarks as HIR (the same programs as <see cref="Reference"/>).</summary>
    public static HirProgram Bench()
    {
        var p = new HirProgram("Bench");
        var (shape, shapeDef) = p.Type("Shape", TypeKind.Class, flags: TypeFlags.Abstract);
        var Sh = HirProgram.T(shape);
        var area = HirProgram.Method(shape, shapeDef, "Area", I, MethodFlags.Virtual | MethodFlags.Abstract);
        var (square, squareDef) = p.Type("Square", TypeKind.Class, shape, TypeFlags.Final);
        var side = HirProgram.Field(square, squareDef, "S", I);
        HirProgram.Method(square, squareDef, "Area", I, MethodFlags.Virtual | MethodFlags.Override);
        new MethodBuilder(squareDef.Methods[^1]).Do(Ret(Mul(F(This(HirProgram.T(square)), side, I), F(This(HirProgram.T(square)), side, I)))).Done();
        var (triangle, triangleDef) = p.Type("Triangle", TypeKind.Class, shape, TypeFlags.Final);
        var tb = HirProgram.Field(triangle, triangleDef, "B", I);
        HirProgram.Method(triangle, triangleDef, "Area", I, MethodFlags.Virtual | MethodFlags.Override);
        new MethodBuilder(triangleDef.Methods[^1]).Do(Ret(Mul(F(This(HirProgram.T(triangle)), tb, I), Int(3)))).Done();
        var (cell, cellDef) = p.Type("Cell", TypeKind.Class, flags: TypeFlags.Final);
        var cv = HirProgram.Field(cell, cellDef, "V", I);
        var cellNew = HirProgram.Method(cell, cellDef, ".new", HirType.Void, MethodFlags.None, MethodKind.Constructor, HirProgram.P("v", I));
        new MethodBuilder(cellDef.Methods[^1]).Do(S(Set(F(This(HirProgram.T(cell)), cv, I), Arg(0)))).Done();
        var (point, pointDef) = p.Type("Point", TypeKind.Struct);
        var Pt = HirProgram.T(point);
        var px = HirProgram.Field(point, pointDef, "X", I);
        var py = HirProgram.Field(point, pointDef, "Y", I);
        var dot = HirProgram.Method(point, pointDef, "Dot", I, MethodFlags.None);
        new MethodBuilder(pointDef.Methods[^1]).Do(Ret(Add(Mul(F(This(Pt), px, I), F(This(Pt), px, I)), Mul(F(This(Pt), py, I), F(This(Pt), py, I))))).Done();
        var (failure, failureDef) = p.Type("Failure", TypeKind.Class, flags: TypeFlags.Final);

        var (prog, progDef) = p.Type("Program", TypeKind.Class, flags: TypeFlags.Static | TypeFlags.Abstract);
        MethodBuilder M(string name)
        {
            HirProgram.Method(prog, progDef, name, I, MethodFlags.Static, MethodKind.Normal, HirProgram.P("n", I));
            return new MethodBuilder(progDef.Methods[^1]);
        }
        {
            var m = M("Fib");
            var fib = MemberToken.Def(prog.Index, progDef.Methods.Count - 1);
            m.Do(If(Lt(Arg(0), Int(2)), Ret(Arg(0))), Ret(Add(Call(fib, I, Sub(Arg(0), Int(1))), Call(fib, I, Sub(Arg(0), Int(2)))))).Done();
        }
        {
            var m = M("Dispatch");
            var a = m.Local("a", Sh);
            var b = m.Local("b", Sh);
            var s = m.Local("s", I);
            var i = m.Local("i", I);
            m.Do(Let(a, new ConvertExpr(new NewExpr(null, [], [new MemberInit(side, Int(3))], HirProgram.T(square)), ConversionKind.Upcast, Sh)),
                Let(b, new ConvertExpr(new NewExpr(null, [], [new MemberInit(tb, Int(5))], HirProgram.T(triangle)), ConversionKind.Upcast, Sh)),
                Let(s, Int(0)), Let(i, Int(0)),
                While(Lt(i, Arg(0)),
                    S(AddTo(s, CallOn(new ConditionalExpr(Eq(new BinaryExpr(BinaryOp.BitAnd, i, Int(1), I), Int(0)), a, b, Sh), area, I))),
                    S(Inc(i))),
                Ret(s)).Done();
        }
        {
            var m = M("Alloc");
            var s = m.Local("s", I);
            var i = m.Local("i", I);
            m.Do(Let(s, Int(0)), Let(i, Int(0)),
                While(Lt(i, Arg(0)), S(AddTo(s, F(New(HirProgram.T(cell), cellNew, i), cv, I))), S(Inc(i))),
                Ret(s)).Done();
        }
        {
            var m = M("Structs");
            var pt = m.Local("p", Pt);
            var s = m.Local("s", I);
            var i = m.Local("i", I);
            m.Do(Let(pt, new NewExpr(null, [], [new MemberInit(px, Int(1)), new MemberInit(py, Int(2))], Pt)), Let(s, Int(0)), Let(i, Int(0)),
                While(Lt(i, Arg(0)),
                    S(Set(F(pt, px, I), new BinaryExpr(BinaryOp.BitAnd, i, Int(1023), I))),
                    S(AddTo(s, CallOn(pt, dot, I))),
                    S(Inc(i))),
                Ret(s)).Done();
        }
        var throwIndex = progDef.Methods.Count;
        {
            var m = M("Throw");
            var s = m.Local("s", I);
            var i = m.Local("i", I);
            var F_ = HirProgram.T(failure);
            m.Do(Let(s, Int(0)), Let(i, Int(0)),
                While(Lt(i, Arg(0)),
                    Try(Blk(Throw(New(F_, null))), [new H.CatchClause(F_, -1, null, Blk(S(Inc(s))))]),
                    S(Inc(i))),
                Ret(s)).Done();
        }
        {
            var m = M("Mono");
            var a = m.Local("a", Sh);
            var s = m.Local("s", I);
            var i = m.Local("i", I);
            m.Do(Let(a, new ConvertExpr(new NewExpr(null, [], [new MemberInit(side, Int(3))], HirProgram.T(square)), ConversionKind.Upcast, Sh)),
                Let(s, Int(0)), Let(i, Int(0)),
                While(Lt(i, Arg(0)), S(AddTo(s, CallOn(a, area, I))), S(Inc(i))),
                Ret(s)).Done();
        }
        // Main(which, n): the benchmark `which` (0 Fib … 5 Mono) on n, for executables.
        {
            HirProgram.Method(prog, progDef, "Main", I, MethodFlags.Static, MethodKind.Normal, HirProgram.P("which", I), HirProgram.P("n", I));
            var main = new MethodBuilder(progDef.Methods[^1]);
            var sections = Enumerable.Range(0, 6).Select(k => new SwitchSection([new ConstantPattern(HirConstant.Int(k))], null,
                Ret(Call(MemberToken.Def(prog.Index, k), I, Arg(1))))).ToArray();
            main.Do(new SwitchStmt(Arg(0), sections), Ret(Int(-1))).Done();
        }
        return p;
    }
}

using Larva.Hir.Lowering;
using Larva.Runtime.Vm;
using static Larva.Hir.Tests.E;

namespace Larva.Hir.Tests;

/// <summary>
/// Lowering only what the program reaches of a library (<see cref="LoweringOptions.ReachedOnly"/>): the methods the
/// program runs are lowered, through direct calls, virtual calls (the vtable of a class made), interface calls (its
/// itable) and a generic's requirement met by a type's implementation; the others are stubs; the program's own methods
/// are all lowered, reached or not. It runs on the VM as the whole lowering does.
/// </summary>
public sealed class ReachedTests : CheckedTest
{
    [Fact]
    public void Only_what_the_program_reaches_of_a_library_is_lowered_and_it_runs_the_same()
    {
        var (program, names) = Program();
        var whole = HirLowerer.Lower(program.Assembly);
        var reached = HirLowerer.Lower(program.Assembly, new LoweringOptions { ReachedOnly = new HashSet<string> { "Lib" } });
        Check("the program lowers whole", whole.Success, string.Join("; ", whole.Errors));
        Check("the program lowers with only what it reaches of Lib", reached.Success, string.Join("; ", reached.Errors));
        Require();
        Check("lowered whole, nothing is a stub", whole.Unlowered.Count == 0);
        bool Lowered(string name) => !reached.Unlowered.Contains(reached.Functions[names[name]]);
        foreach (var name in new[] { "Dog.Speak", "Num.Value", "Boxed.Value", "Lib.Get", "Lib.Twice", "Program.Unused" })
            Check($"{name} is lowered", Lowered(name));
        foreach (var name in new[] { "Cat.Speak", "Animal.Speak", "Animal.Unused", "Lib.Never" })
            Check($"{name} is a stub", !Lowered(name));
        Check("the vtable maker of Cat (never made) is a stub", reached.Unlowered.Any(f => reached.Module!.Functions[f].Name == "Lib.Cat.$vtable"));
        Check("Num's implementation of IValue.Value (met through Get<Num>) is lowered",
            reached.Module!.Functions.Where((_, f) => !reached.Unlowered.Contains(f)).Any(f => f.Name.StartsWith("Lib.Num.$impl.", StringComparison.Ordinal) && f.Name.EndsWith("IValue.Value", StringComparison.Ordinal)));

        object? Run(LoweringResult lowered)
        {
            var vm = Larva.IR.Compiler.Compile(lowered.Module!);
            Check("the IR passes the type and borrow checks", vm.Il is not null, string.Join("; ", vm.TypeErrors.Concat<object>(vm.Diagnostics).Take(6)));
            return vm.Il is null ? null : new Interpreter(vm.Il).Run("App.Program.Main");
        }
        const long expected = 2 + 70 + 50 + 1000;
        var onWhole = Run(whole);
        var onReached = Run(reached);
        Check($"Main() = {expected} lowered whole", Equals(onWhole, expected), $"{onWhole}");
        Check($"Main() = {expected} with only what it reaches", Equals(onReached, expected), $"{onReached}");
    }

    /// <summary>A library (assembly Lib) and a program (assembly App) linked into one HIR assembly.</summary>
    private static (HirProgram Program, Dictionary<string, MemberToken> Names) Program()
    {
        var names = new Dictionary<string, MemberToken>();
        var lib = new HirProgram("Lib");
        var p = lib;

        // class Animal { new(); virtual Speak() => 1 (overridden by every class made); Unused() => 99; }
        var (animal, animalDef) = p.Type("Animal", TypeKind.Class);
        var A = HirProgram.T(animal);
        var animalNew = HirProgram.Method(animal, animalDef, ".new", HirType.Void, MethodFlags.None, MethodKind.Constructor);
        new MethodBuilder(animalDef.Methods[^1]).Do().Done();
        var speak = names["Animal.Speak"] = HirProgram.Method(animal, animalDef, "Speak", I, MethodFlags.Virtual);
        new MethodBuilder(animalDef.Methods[^1]).Do(Ret(Int(1))).Done();
        names["Animal.Unused"] = HirProgram.Method(animal, animalDef, "Unused", I, MethodFlags.None);
        new MethodBuilder(animalDef.Methods[^1]).Do(Ret(Int(99))).Done();

        // class Dog : Animal { new() : base(); override Speak() => 2; }   class Cat : Animal { … override Speak() => 3; } (never made)
        TypeToken Derived(string name, int says)
        {
            var (type, def) = p.Type(name, TypeKind.Class, animal);
            HirProgram.Method(type, def, ".new", HirType.Void, MethodFlags.None, MethodKind.Constructor);
            new MethodBuilder(def.Methods[^1]).Do(S(new CallExpr(This(HirProgram.T(type)), animalNew, [], [], HirType.Void) { NonVirtual = true })).Done();
            names[$"{name}.new"] = MemberToken.Def(type.Index, def.Methods.Count - 1);
            names[$"{name}.Speak"] = HirProgram.Method(type, def, "Speak", I, MethodFlags.Virtual | MethodFlags.Override);
            new MethodBuilder(def.Methods[^1]).Do(Ret(Int(says))).Done();
            return type;
        }
        var dog = Derived("Dog", 2);
        Derived("Cat", 3);

        // interface IValue { Value() : i32; }
        var (value, valueDef) = p.Type("IValue", TypeKind.Interface);
        var IV = HirProgram.T(value);
        var valueOf = HirProgram.Method(value, valueDef, "Value", I, MethodFlags.Virtual | MethodFlags.Abstract);

        // struct Num : IValue { i32 N; Value() => N * 10; }
        var (num, numDef) = p.Type("Num", TypeKind.Struct);
        numDef.Interfaces.Add(IV);
        var N = HirProgram.T(num);
        var n = HirProgram.Field(num, numDef, "N", I);
        names["Num.Value"] = HirProgram.Method(num, numDef, "Value", I, MethodFlags.None);
        new MethodBuilder(numDef.Methods[^1]).Do(Ret(Mul(F(This(N), n, I), Int(10)))).Done();

        // class Boxed : IValue { new(); Value() => 50; }
        var (boxed, boxedDef) = p.Type("Boxed", TypeKind.Class);
        boxedDef.Interfaces.Add(IV);
        var boxedNew = HirProgram.Method(boxed, boxedDef, ".new", HirType.Void, MethodFlags.None, MethodKind.Constructor);
        new MethodBuilder(boxedDef.Methods[^1]).Do().Done();
        names["Boxed.Value"] = HirProgram.Method(boxed, boxedDef, "Value", I, MethodFlags.None);
        new MethodBuilder(boxedDef.Methods[^1]).Do(Ret(Int(50))).Done();

        // static class Lib { Get<T : IValue>(T x) => x.Value(); Twice(i32 x) => x * 2; Never() => 5; }
        var (statics, staticsDef) = p.Type("Lib", TypeKind.Class, flags: TypeFlags.Static | TypeFlags.Abstract);
        var T = new GenericParamType(GenericOwner.Method, 0);
        var get = names["Lib.Get"] = HirProgram.Method(statics, staticsDef, "Get", I, MethodFlags.Static, MethodKind.Normal, HirProgram.P("x", T));
        staticsDef.Methods[^1].Generics.Add(new GenericParam("T") { Types = [IV] });
        new MethodBuilder(staticsDef.Methods[^1]).Do(Ret(CallOn(Arg(0, T), valueOf, I))).Done();
        var twice = names["Lib.Twice"] = HirProgram.Method(statics, staticsDef, "Twice", I, MethodFlags.Static, MethodKind.Normal, HirProgram.P("x", I));
        new MethodBuilder(staticsDef.Methods[^1]).Do(Ret(Mul(Arg(0), Int(2)))).Done();
        names["Lib.Never"] = HirProgram.Method(statics, staticsDef, "Never", I, MethodFlags.Static);
        new MethodBuilder(staticsDef.Methods[^1]).Do(Ret(Int(5))).Done();

        int libTypes = p.Assembly.Types.Count;
        // The program (assembly App): static class Program { Main(); Unused() => 7; }
        var (prog, progDef) = p.Type("Program", TypeKind.Class, flags: TypeFlags.Static | TypeFlags.Abstract);
        progDef.Namespace = "App";
        // Main() : i32 { Animal a = new Dog(); IValue v = new Boxed(); Num k; k.N = 7; return a.Speak() + Get<Num>(k) + v.Value() + Twice(500); }
        var main = HirProgram.Method(prog, progDef, "Main", I, MethodFlags.Static);
        {
            var m = new MethodBuilder(progDef.Methods[^1]);
            var a = m.Local("a", A);
            var v = m.Local("v", IV);
            var k = m.Local("k", N);
            m.Do(Let(a, new ConvertExpr(New(HirProgram.T(dog), names["Dog.new"]), ConversionKind.Upcast, A)),
                Let(v, new ConvertExpr(New(HirProgram.T(boxed), boxedNew), ConversionKind.Upcast, IV)),
                Let(k, new DefaultExpr(N)),
                S(Set(F(k, n, I), Int(7))),
                Ret(Add(Add(Add(CallOn(a, speak, I), new CallExpr(null, get, [N], [k], I)), CallOn(v, valueOf, I)), Call(twice, I, Int(500))))).Done();
        }
        names["Program.Unused"] = HirProgram.Method(prog, progDef, "Unused", I, MethodFlags.Static);
        new MethodBuilder(progDef.Methods[^1]).Do(Ret(Int(7))).Done();
        p.Assembly.EntryPoint = main;
        p.Assembly.Origins.Add(new HirOrigin("Lib", 0, libTypes));
        p.Assembly.Origins.Add(new HirOrigin("App", libTypes, p.Assembly.Types.Count - libTypes));
        return (p, names);
    }
}

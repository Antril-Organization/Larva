using Larva.IR;
using Larva.IR.Linking;
using Larva.Runtime.Vm;
using static Larva.IR.Operand;

namespace Larva.Tests;

/// <summary>
/// The two-module program of the linker tests (geom exports Point, make, norm1, max, identity; app imports them:
/// app::main() = 7), objects, and running a linked program on every back end.
/// </summary>
internal static unsafe class LinkerPrograms
{
    /// <summary>geom + app, linked (also compiled natively by the LLVM and AOT tests).</summary>
    public static LinkResult LinkedProgram() => Linker.Link([Object(Geom()), Object(App())]);

    /// <summary>geom.lvo and app.lvo saved in a new folder: their paths.</summary>
    public static (string Geom, string App) SaveObjects(string folder)
    {
        string geom = Path.Combine(folder, "geom" + ObjectFile.Extension), app = Path.Combine(folder, "app" + ObjectFile.Extension);
        ObjectFile.Save(Object(Geom()), geom);
        ObjectFile.Save(Object(App()), app);
        return (geom, app);
    }

    /// <summary>The module compiled to an object for <paramref name="gc"/> (throws when it does not compile).</summary>
    public static LarvaObject Object(IrModule module, GcConfig? gc = null)
    {
        var result = Compiler.CompileObject(module, new CompilationOptions { Gc = gc ?? GcConfig.Default });
        if (!result.Success)
            throw new InvalidOperationException(string.Join("\n", result.TypeErrors.Select(e => e.ToString()).Concat(result.Diagnostics.Select(d => d.ToString()))));
        return result.Object!;
    }

    // ── Modules ──────────────────────────────────────────────────

    public const int PointId = 0;
    public static readonly IrType Point = IrType.Struct(PointId);

    public static StructDef PointDef(Linkage linkage, string? symbol = null) =>
        new("Point", IrType.I32, IrType.I32) { FieldNames = ["x", "y"], Linkage = linkage, Symbol = symbol };

    /// <summary>module geom: export Point, make, norm1, max&lt;T: numeric&gt;, identity&lt;T&gt;; internal abs.</summary>
    public static IrModule Geom()
    {
        var make = new FunctionBuilder("make", Point) { Linkage = Linkage.Export };
        int x = make.Argument(IrType.I32, "x"), y = make.Argument(IrType.I32, "y");
        make.Assign(FunctionBuilder.ReturnPlace.Field(0), Copy(x)).Assign(FunctionBuilder.ReturnPlace.Field(1), Copy(y)).Return();

        // fn norm1(p: &Point) -> i32 { abs(p.x) + abs(p.y) }
        var norm = new FunctionBuilder("norm1", IrType.I32) { Linkage = Linkage.Export };
        int p = norm.Argument(IrType.Ref(Point), "p");
        int ax = norm.Local(IrType.I32, "ax"), ay = norm.Local(IrType.I32, "ay");
        int b1 = norm.NewBlock(), b2 = norm.NewBlock();
        norm.Call(2, [Copy(new Place(p).Deref().Field(0))], ax, b1);
        norm.At(b1).Call(2, [Copy(new Place(p).Deref().Field(1))], ay, b2);
        norm.At(b2).Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(ax), Copy(ay))).Return();

        var abs = new FunctionBuilder("abs", IrType.I32);
        int v = abs.Argument(IrType.I32, "v");
        int negative = abs.Temp(IrType.Bool);
        int neg = abs.NewBlock(), pos = abs.NewBlock();
        abs.Assign(negative, Rvalue.Binary(BinaryOp.Lt, Copy(v), Int(0))).If(Move(negative), neg, pos);
        abs.At(neg).Assign(FunctionBuilder.ReturnPlace, Rvalue.Unary(UnaryOp.Neg, Copy(v))).Return();
        abs.At(pos).Assign(FunctionBuilder.ReturnPlace, Copy(v)).Return();

        var max = GenericsPrograms.GenericBuilder("max", out var t, ("T", TypeConstraint.Numeric));
        max.Linkage = Linkage.Export;
        int a = max.Argument(t[0], "a"), bb = max.Argument(t[0], "b");
        int gt = max.Temp(IrType.Bool);
        int takeA = max.NewBlock(), takeB = max.NewBlock();
        max.Assign(gt, Rvalue.Binary(BinaryOp.Gt, Copy(a), Copy(bb))).If(Move(gt), takeA, takeB);
        max.At(takeA).Assign(FunctionBuilder.ReturnPlace, Copy(a)).Return();
        max.At(takeB).Assign(FunctionBuilder.ReturnPlace, Copy(bb)).Return();

        var identity = GenericsPrograms.GenericBuilder("identity", out var u, ("T", TypeConstraint.Any));
        identity.Linkage = Linkage.Export;
        int value = identity.Argument(u[0], "value");
        identity.Assign(FunctionBuilder.ReturnPlace, Move(value)).Return();

        return new IrModule([PointDef(Linkage.Export)], [make.Build(), norm.Build(), abs.Build(), max.Build(), identity.Build()]) { Name = "geom" };
    }

    public const int MakeImport = 0, NormImport = 1, MaxImport = 2, IdentityImport = 3, AppAbs = 4;

    /// <summary>module app: imports geom's API; has its own internal abs and an unused function.</summary>
    public static IrModule App(Func<IrFunction>? makeDeclaration = null, StructDef? pointDeclaration = null, IrFunction? extraImport = null)
    {
        var make = new FunctionBuilder("make", Point).ImportFrom("geom::make");
        make.Argument(IrType.I32, "x");
        make.Argument(IrType.I32, "y");

        var norm = new FunctionBuilder("norm1", IrType.I32).ImportFrom("geom::norm1");
        norm.Argument(IrType.Ref(Point), "p");

        var max = GenericsPrograms.GenericBuilder("max", out var t, ("T", TypeConstraint.Numeric)).ImportFrom("geom::max");
        max.Argument(t[0], "a");
        max.Argument(t[0], "b");

        var identity = GenericsPrograms.GenericBuilder("identity", out var u, ("T", TypeConstraint.Any)).ImportFrom("geom::identity");
        identity.Argument(u[0], "value");

        // An app-local abs with different behavior: must not be confused with geom's.
        var abs = new FunctionBuilder("abs", IrType.I32);
        abs.Argument(IrType.I32, "v");
        abs.Assign(FunctionBuilder.ReturnPlace, Int(1000)).Return();

        var dead = new FunctionBuilder("dead", IrType.I32);
        dead.Assign(FunctionBuilder.ReturnPlace, Int(0)).Return();

        // fn main() -> i32 { let p = make(3, -4); let n = norm1(&p); let m = max(n, 5); max::<i64>(10, 20);
        //                    let k = abs(1); identity(m) + (k - 1000) }
        var main = new FunctionBuilder("main", IrType.I32);
        int p = main.Local(Point, "p");
        int r = main.Local(IrType.Ref(Point), "r");
        int n = main.Local(IrType.I32, "n");
        int m = main.Local(IrType.I32, "m");
        int w = main.Local(IrType.I64, "w");
        int k = main.Local(IrType.I32, "k");
        int i = main.Local(IrType.I32, "i");
        int d = main.Temp(IrType.I32);
        int[] next = [.. Enumerable.Range(0, 6).Select(_ => main.NewBlock())];
        main.Call(MakeImport, [Int(3), Int(-4)], p, next[0]);
        main.At(next[0]).Assign(r, Rvalue.Ref(p)).Call(NormImport, [Move(r)], n, next[1]);
        main.At(next[1]).Call(MaxImport, [IrType.I32], [Copy(n), Int(5)], m, next[2]);
        main.At(next[2]).Call(MaxImport, [IrType.I64], [Int(10, IrType.I64), Int(20, IrType.I64)], w, next[3]);
        main.At(next[3]).Call(AppAbs, [Int(1)], k, next[4]);
        main.At(next[4]).Call(IdentityImport, [IrType.I32], [Copy(m)], i, next[5]);
        main.At(next[5]).Assign(d, Rvalue.Binary(BinaryOp.Sub, Copy(k), Int(1000)))
            .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(i), Copy(d))).Return();

        IrFunction[] functions = [makeDeclaration?.Invoke() ?? make.Build(), norm.Build(), max.Build(), identity.Build(), abs.Build(), dead.Build(), main.Build()];
        if (extraImport is not null)
            functions = [.. functions, extraImport];
        return new IrModule([pointDeclaration ?? PointDef(Linkage.Import, "geom::Point")], functions) { Name = "app" };
    }

    /// <summary>Runs a linked program's entry on the VM, the LLVM JIT and the x64 JIT; the three results.</summary>
    public static (long Vm, int Llvm, int X64, string Detail) RunEverywhere(LinkResult linked, string entry, GcConfig? programGc = null)
    {
        var compilation = new CompilationOptions { Gc = programGc ?? GcConfig.Default };
        long vm = -1; int llvm = -1, x64 = -1; string detail = "";
        try { vm = Convert.ToInt64(new Interpreter(linked.Il!).Run(entry)); } catch (Exception e) { detail += $"VM: {e.Message}; "; }
        global::Larva.Native.NativeRuntime.Reset();
        var compiled = global::Larva.Llvm.LlvmBackend.Compile(linked, new global::Larva.Llvm.LlvmOptions { Compilation = compilation });
        if (compiled.Success)
        {
            using var program = compiled.Program!;
            using var jit = program.Jit();
            if (Environment.GetEnvironmentVariable("LARVA_LINK_IR") == entry)
                TestContext.Current.TestOutputHelper?.WriteLine(program.IrText); // the whole program's LLVM IR, to look at a case
            llvm = ((delegate* unmanaged<int>)jit.Address(entry))();
            int retains = global::Larva.Native.NativeRuntime.Retains, releases = global::Larva.Native.NativeRuntime.Releases, errors = global::Larva.Native.NativeRuntime.Errors;
            detail += $"[llvm retains {retains} releases {releases} errors {errors}] ";
        }
        else
            detail += $"LLVM: {compiled}; ";
        global::Larva.Native.NativeRuntime.Reset();
        var native = global::Larva.X64.X64Backend.Compile(linked, new global::Larva.X64.X64Options { Compilation = compilation });
        if (native.Success)
        {
            using var image = native.Program!.Load();
            x64 = ((delegate* unmanaged<int>)image.Address(entry))();
        }
        else
            detail += $"x64: {native}; ";
        global::Larva.Native.NativeRuntime.Reset();
        return (vm, llvm, x64, detail);
    }
}

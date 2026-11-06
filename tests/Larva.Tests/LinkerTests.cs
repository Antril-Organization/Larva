using Larva.IR;
using Larva.IR.Analysis;
using Larva.IR.Linking;
using Larva.IR.Lowering;
using Larva.Runtime.Vm;
using static Larva.IR.Operand;
using static Larva.Tests.LinkerPrograms;
using static Larva.Tests.MixedDomainPrograms;

namespace Larva.Tests;

/// <summary>Multi-module programs: objects, symbol resolution, cross-module generics, dead code, link errors, CLI.</summary>
public sealed unsafe class LinkerTests : CheckedTest
{
    [Fact]
    public void Object_files_round_trip()
    {
        var app = Object(App());
        var roundTrip = ObjectFile.Read(ObjectFile.Write(app));
        Check("object file round trip", roundTrip.Module.ToString() == app.Module.ToString() && roundTrip.Gc == app.Gc);
    }

    [Fact]
    public void Geom_and_app_link_specialize_and_drop_dead_code()
    {
        var result = Linker.Link([Object(Geom()), Object(App())]);
        Check("geom + app link", result.Success, string.Join(" | ", result.Errors));
        if (result.Success)
        {
            var names = result.Il!.Functions.Select(f => f.Name).ToHashSet();
            Check("main() = 7 across modules", Equals(new Interpreter(result.Il).Run("app::main"), 7L));
            Check("internal names do not clash", names.Contains("geom::abs") && names.Contains("app::abs"));
            Check("dead code is dropped", !names.Contains("app::dead"));
            Check("generic from geom specialized for app (i32 and i64)", names.Contains("geom::max<i32>") && names.Contains("geom::max<i64>"));
            Check("shared generic compiled once", names.Contains("geom::identity") && names.Count(n => n.StartsWith("geom::identity")) == 1);
            Check("entry is app::main", result.Il.Entry >= 0 && result.Il.Functions[result.Il.Entry].Name == "app::main");
        }
    }

    // ── Errors ─────────────────────────────────────────────────────

    [Fact]
    public void Link_errors_name_the_symbol_and_the_problem()
    {
        var geom = Object(Geom());
        var missing = new FunctionBuilder("missing", IrType.Unit).ImportFrom("geom::missing").Build();
        ExpectLink([geom, Object(App(extraImport: missing))], "unresolved symbol `geom::missing`");

        var privateAbs = new FunctionBuilder("abs", IrType.I32).ImportFrom("geom::abs");
        privateAbs.Argument(IrType.I32, "v");
        ExpectLink([geom, Object(App(extraImport: privateAbs.Build()))], "`geom::abs` exists but is not exported");

        static IrFunction WrongMake()
        {
            var make = new FunctionBuilder("make", Point).ImportFrom("geom::make");
            make.Argument(IrType.I64, "x");
            make.Argument(IrType.I32, "y");
            return make.Build();
        }
        ExpectLink([geom, Object(App(makeDeclaration: WrongMake))], "imported `make` does not match `geom::make`");

        var widePoint = new StructDef("Point", IrType.I32, IrType.I32, IrType.I32) { Linkage = Linkage.Import, Symbol = "geom::Point" };
        ExpectLink([geom, Object(App(pointDeclaration: widePoint))], "imported struct `Point` does not match `geom::Point`");

        ExpectLink([geom, geom], "module `geom` is linked more than once");

        var rcApp = Object(App(), GcConfig.RefCount);
        ExpectLink([geom, rcApp], "was checked for GC mode");
    }

    [Fact]
    public void Entries_libraries_and_exports_follow_the_linkage_rules()
    {
        var geom = Object(Geom());
        ExpectLink([geom], "no entry function `main`");
        Check("geom alone links as a library", Linker.Link([geom], new LinkOptions { Library = true }) is { Success: true, Il: var lib }
            && lib!.Functions.Any(f => f.Name == "geom::norm1"));

        var origin = new FunctionBuilder("origin", Point) { Linkage = Linkage.Export };
        origin.Assign(FunctionBuilder.ReturnPlace.Field(0), Int(0)).Assign(FunctionBuilder.ReturnPlace.Field(1), Int(0)).Return();
        var leak = new IrModule([PointDef(Linkage.Internal)], [origin.Build()]) { Name = "leaky" };
        var leakResult = Compiler.CompileObject(leak);
        Check("export exposing an internal struct is rejected", leakResult.TypeErrors.Any(e => e.Kind == TypeErrorKind.LinkageViolation),
            string.Join(" | ", leakResult.TypeErrors.Select(e => e.Message)));

        var direct = Compiler.Compile(App());
        Check("compiling a module with imports directly is rejected", !direct.Success && direct.TypeErrors[0].Kind == TypeErrorKind.LinkageViolation);
    }

    // ── GC domains (stage 1 of the GC mixing design): modules grouped by GC mode, every function and struct tagged ──

    [Fact]
    public void Objects_of_different_gc_modes_link_as_domains()
    {
        var geom = Object(Geom());
        var rcApp = Object(App(), GcConfig.RefCount);
        var one = Linker.Link([geom, Object(App())]);
        Check("one domain: the program's GC, everything in domain 0", one.Success && one.Merged!.Domains is [{ Mode: GcMode.Default }]
            && one.Merged.Functions.All(f => f.Domain == 0) && one.Merged.Structs.All(s => s.Domain == 0));
        var mixed = Linker.Link([geom, rcApp], new LinkOptions { MixedDomains = true, LowerToIl = false });
        Check("MixedDomains links objects of different GC modes", mixed.Success, string.Join(" | ", mixed.Errors));
        if (!mixed.Success)
            return;
        var merged = mixed.Merged!;
        Check("two domains: the program's (Default) first, then RefCount", merged.Domains is [{ Mode: GcMode.Default }, { Mode: GcMode.RefCount }]);
        Check("geom's functions and structs are in domain 0", merged.Functions.Where(f => f.Name.StartsWith("geom::")).All(f => f.Domain == 0)
            && merged.Structs.Where(s => s.Name.StartsWith("geom::")).All(s => s.Domain == 0));
        Check("app's functions are in domain 1", merged.Functions.Where(f => f.Name.StartsWith("app::")).Any() && merged.Functions.Where(f => f.Name.StartsWith("app::")).All(f => f.Domain == 1));
        var lowered = Linker.Link([geom, rcApp], new LinkOptions { MixedDomains = true });
        Check("…and the mixed program still lowers and runs (the back ends use domain 0 until stage 2)", lowered.Success && Equals(new Interpreter(lowered.Il!).Run("app::main"), 7L),
            string.Join(" | ", lowered.Errors.Select(e => e.Message)));
    }

    // ── Mixed GC domains (stage 2 on): each pair run on the VM, LLVM and x64 ──

    [Fact]
    public void Tracing_and_manual_domains_mix()
    {
        var tracingManual = Linker.Link([Object(GcLib()), Object(ManualApp(), GcConfig.Manual)], new LinkOptions { MixedDomains = true });
        Check("Tracing + Manual links (a Manual module holds `gc` objects of the program's domain)", tracingManual.Success, string.Join(" | ", tracingManual.Errors));
        if (tracingManual.Success)
        {
            var (vm, llvm, x64, detail) = RunEverywhere(tracingManual, "mapp::main");
            Check("Tracing + Manual: a gc Cell kept only in Manual memory survives two collections (raw memory is a root); domain 0 → 5",
                vm == 5 && llvm == 5 && x64 == 5, $"vm {vm}, llvm {llvm}, x64 {x64} {detail}");
        }
    }

    [Fact]
    public void Object_headers_carry_their_domain()
    {
        var domains = Linker.Link([Object(GcLib()), Object(RcLib(), GcConfig.RefCount), Object(DomainApp())], new LinkOptions { MixedDomains = true });
        Check("Default + RefCount + app links", domains.Success, string.Join(" | ", domains.Errors));
        if (domains.Success)
        {
            var (vm, llvm, x64, detail) = RunEverywhere(domains, "dapp::main");
            Check("the header's domain byte: gclib's object in 0, rclib's in 1 (gc.domain) → 10", vm == 10 && llvm == 10 && x64 == 10, $"vm {vm}, llvm {llvm}, x64 {x64} {detail}");
        }
    }

    [Fact]
    public void Tracing_code_counts_the_refcounted_cells_it_holds()
    {
        var tracingRc = Linker.Link([Object(RcFin(), GcConfig.RefCount), Object(TracingRcApp())], new LinkOptions { MixedDomains = true });
        Check("Tracing + RefCount links", tracingRc.Success, string.Join(" | ", tracingRc.Errors));
        if (tracingRc.Success)
        {
            var (vm, llvm, x64, detail) = RunEverywhere(tracingRc, "tapp::main");
            Check("Tracing + RefCount: tracing code counts the counted cells it stores, overwrites and copies (release runs the finalizer once; the tracing box itself is uncounted) → 211",
                vm == 211 && llvm == 211 && x64 == 211, $"vm {vm}, llvm {llvm}, x64 {x64} {detail}");
        }
    }

    [Fact]
    public void Arena_cells_cross_only_from_outside_regions()
    {
        var arena = Linker.Link([Object(ArenaApp()), Object(ArenaLib(leak: false), GcConfig.Arena)], new LinkOptions { MixedDomains = true });
        Check("Tracing + Arena links", arena.Success, string.Join(" | ", arena.Errors));
        if (arena.Success)
        {
            var (vm, llvm, x64, detail) = RunEverywhere(arena, "aapp::main");
            Check("Tracing + Arena: a cell made outside any region crosses to the tracing app; a region's cells are summed and freed with it → 37",
                vm == 37 && llvm == 37 && x64 == 37, $"vm {vm}, llvm {llvm}, x64 {x64} {detail}");
        }
        var leak = Linker.Link([Object(ArenaApp()), Object(ArenaLib(leak: true), GcConfig.Arena), Object(StoreLib())], new LinkOptions { MixedDomains = true });
        string leakMessages = string.Join(" | ", leak.Errors.Select(e => e.Message));
        Check("Tracing + Arena: a region's cell handed to another domain's function is an escape (link-time region check)",
            !leak.Success && leakMessages.Contains("another GC domain"), leakMessages);
    }

    [Fact]
    public void Freeing_manual_memory_releases_the_counted_cells_it_held()
    {
        var manualFree = Linker.Link([Object(RcFin(), GcConfig.RefCount), Object(ManualFree(), GcConfig.Manual)], new LinkOptions { MixedDomains = true });
        Check("Manual + RefCount links", manualFree.Success, string.Join(" | ", manualFree.Errors));
        if (manualFree.Success)
        {
            var (vm, llvm, x64, detail) = RunEverywhere(manualFree, "mfree::main");
            Check("Manual + RefCount: freeing raw memory releases the counted cell it held (its finalizer runs once) → 1",
                vm == 1 && llvm == 1 && x64 == 1, $"vm {vm}, llvm {llvm}, x64 {x64} {detail}");
        }
        var inlineFree = Linker.Link([Object(RcFin(), GcConfig.RefCount), Object(ManualFreeInline(), GcConfig.Manual)], new LinkOptions { MixedDomains = true });
        Check("Manual + RefCount (inline) links", inlineFree.Success, string.Join(" | ", inlineFree.Errors.Select(e => e.Message)));
        if (inlineFree.Success)
        {
            var (vm, llvm, x64, detail) = RunEverywhere(inlineFree, "mfree2::main");
            Check("native moves transfer like the IL: the cell dies at the free, not at the function's end → 1",
                vm == 1 && llvm == 1 && x64 == 1, $"vm {vm}, llvm {llvm}, x64 {x64} {detail}");
        }
    }

    [Fact]
    public void A_refcount_program_runs_a_tracing_library_collector()
    {
        var rcRoot = Linker.Link([Object(RcApp(), GcConfig.RefCount), Object(GcLib())],
            new LinkOptions { MixedDomains = true, Compilation = new CompilationOptions { Gc = GcConfig.RefCount } });
        Check("RefCount root + tracing library links", rcRoot.Success, string.Join(" | ", rcRoot.Errors));
        if (rcRoot.Success)
        {
            var (vm, llvm, x64, detail) = RunEverywhere(rcRoot, "rcapp::main", GcConfig.RefCount);
            Check("RefCount root + tracing library: the tracing collector runs (the library's churn polls) and the kept cell survives → 107",
                vm == 107 && llvm == 107 && x64 == 107, $"vm {vm}, llvm {llvm}, x64 {x64} {detail}");
        }
    }

    // ── Static data ────────────────────────────────────────────────

    /// <summary>
    /// lib has two static arrays and exports <c>pick(i) = static#1[i]</c>; app has its own <c>static#0</c>. Linked in that
    /// order, app's array becomes <c>static#2</c>: main = app.static#0[1] + pick(2) = 200 + 9.
    /// </summary>
    [Fact]
    public void Static_data_is_renumbered_per_object_and_read_only()
    {
        var table = IrType.Ref(IrType.ArrayOf(IrType.I32));

        var pick = new FunctionBuilder("pick", IrType.I32) { Linkage = Linkage.Export };
        int index = pick.Argument(IrType.U64, "i");
        int t = pick.Local(table, "t");
        pick.Assign(t, Rvalue.Static(1)).Assign(FunctionBuilder.ReturnPlace, Copy(new Place(t).Deref().Index(index))).Return();
        var lib = new IrModule([], [pick.Build()]) { Name = "lib", Data = [new StaticData(IrType.I8, [1, 2]), new StaticData(IrType.I32, [7, 8, 9])] };

        var imported = new FunctionBuilder("pick", IrType.I32).ImportFrom("lib::pick");
        imported.Argument(IrType.U64, "i");
        var main = new FunctionBuilder("main", IrType.I32);
        int own = main.Local(table, "own"), got = main.Local(IrType.I32, "got"), k = main.Local(IrType.U64, "k");
        int next = main.NewBlock();
        main.Assign(own, Rvalue.Static(0)).Call(0, [Int(2, IrType.U64)], got, next);
        main.At(next).Assign(k, Int(1, IrType.U64))
            .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(new Place(own).Deref().Index(k)), Copy(got))).Return();
        var program = new IrModule([], [imported.Build(), main.Build()]) { Name = "app", Data = [new StaticData(IrType.I32, [100, 200])] };

        var libObject = Object(lib);
        var appObject = Object(program);
        var reread = ObjectFile.Read(ObjectFile.Write(appObject));
        Check("static data survives the object file", reread.Module.Data is [{ Element: PrimitiveType { Kind: Primitive.I32 }, Values: [var a, var b] }] && a == 100 && b == 200);

        var linked = Linker.Link([libObject, appObject]);
        Check("objects with static data link", linked.Success, string.Join("; ", linked.Errors));
        if (linked.Success)
        {
            Check("data is concatenated (lib's two, then app's)", linked.Merged!.Data.Length == 3 && linked.Merged.Data[2].Values[1] == 200);
            long vm = System.Convert.ToInt64(new Interpreter(linked.Il!).Run("app::main"));
            var x64 = global::Larva.X64.X64Backend.Compile(linked);
            long viaX64 = -1, viaLlvm = -1;
            if (x64.Success)
            {
                using var image = x64.Program!.Load();
                viaX64 = ((delegate* unmanaged<int>)image.Address("app::main"))();
            }
            var llvm = global::Larva.Llvm.LlvmBackend.Compile(linked);
            if (llvm.Success)
            {
                using var native = llvm.Program!;
                using var jit = native.Jit();
                viaLlvm = ((delegate* unmanaged<int>)jit.Address("app::main"))();
            }
            Check("static#N is renumbered per object (VM · LLVM · x64) = 209", vm == 209 && viaLlvm == 209 && viaX64 == 209, $"vm {vm}, llvm {viaLlvm}, x64 {viaX64}");
        }

        var writes = new FunctionBuilder("main", IrType.I32);
        int s = writes.Local(table, "s"), zero = writes.Local(IrType.U64, "z");
        writes.Assign(s, Rvalue.Static(0)).Assign(zero, Int(0, IrType.U64)).Assign(new Place(s).Deref().Index(zero), Int(5))
            .Assign(FunctionBuilder.ReturnPlace, Int(0)).Return();
        var rejected = Compiler.CompileObject(new IrModule([], [writes.Build()]) { Data = [new StaticData(IrType.I32, [1])] });
        Check("static data is read-only (a write through `&[T]` is rejected)", !rejected.Success,
            string.Join("; ", rejected.TypeErrors.Select(e => e.Message).Concat(rejected.Diagnostics.Select(d => d.Message))));
    }

    // ── CLI ────────────────────────────────────────────────────────

    [Fact]
    public void Larva_link_links_object_files_and_reports_unresolved_symbols()
    {
        string directory = TestEnvironment.TempFolder("link");
        var (geomPath, appPath) = SaveObjects(directory);
        string output = Path.Combine(directory, "program" + ModuleFile.Extension);

        var (code, text) = Repository.Cli("link", "-o", output, geomPath, appPath);
        Check("larva link geom.lvo app.lvo", code == 0 && File.Exists(output), text.Trim());
        (code, text) = Repository.Cli("run", output);
        Check("larva run program.lvm prints 7", code == 0 && text.Trim() == "7", $"exit {code}: {text.Trim()}");
        (code, text) = Repository.Cli("link", "-o", output, appPath);
        Check("missing module: larva link fails with the unresolved symbols", code == 1 && text.Contains("unresolved symbol `geom::make`"));
    }

    private void ExpectLink(LarvaObject[] objects, string fragment)
    {
        var result = Linker.Link(objects);
        string messages = string.Join(" | ", result.Errors.Select(e => e.Message));
        Check($"link error: {fragment}", !result.Success && messages.Contains(fragment), messages);
    }
}

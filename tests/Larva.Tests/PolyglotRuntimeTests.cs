using System.Diagnostics;
using Larva.IR;
using Larva.IR.Linking;
using Larva.Native;
using Larva.Runtime.Vm;

namespace Larva.Tests;

/// <summary>
/// The runtime's side of Larva.Polyglot: handles (objects another language holds) on the VM, the in-process runtime and
/// larva_rt.c, where a minor collection moves young objects and must update the handles' slots.
/// </summary>
public sealed unsafe class PolyglotRuntimeTests : CheckedTest
{
    private static readonly Lazy<string> Folder = new(() => TestEnvironment.TempFolder("polyglot"));

    private const long Expected = 597002000; // n = 200: (Σ 3i × 1000 + 200 held) × 10 + 0 held after

    [Fact]
    public void Handles_keep_their_objects_on_the_vm()
    {
        var linked = Linker.Link([LinkerPrograms.Object(PolyglotPrograms.Handles())], new LinkOptions { Entry = "handles" });
        Check("the program links", linked.Success, string.Join(" | ", linked.Errors));
        var vm = new Interpreter(linked.Il!, options: new InterpreterOptions { CollectEvery = 16 });
        long result = Convert.ToInt64(vm.Run("handles::handles", 200L));
        Check("vm: every object read back through its handle, every handle freed", result == Expected, $"{result}");
        Check("vm: collections happened", vm.Stats.Collections > 0, $"{vm.Stats.Collections}");
    }

    [Fact]
    public void Handles_keep_their_objects_in_process()
    {
        InProcess.RequireWindows();
        var linked = Linker.Link([LinkerPrograms.Object(PolyglotPrograms.Handles())], new LinkOptions { Entry = "handles" });
        Check("the program links", linked.Success, string.Join(" | ", linked.Errors));
        foreach (var backend in new[] { "llvm", "x64" })
        {
            NativeRuntime.Reset();
            NativeRuntime.CollectionThreshold = 64;
            long result;
            if (backend == "llvm")
            {
                var compiled = Larva.Llvm.LlvmBackend.Compile(linked, new Larva.Llvm.LlvmOptions());
                Check("llvm: compiles", compiled.Success, compiled.ToString());
                if (!compiled.Success)
                    continue;
                using var program = compiled.Program!;
                using var jit = program.Jit();
                result = ((delegate* unmanaged<long, long>)jit.Address("handles::handles"))(200);
            }
            else
            {
                var compiled = Larva.X64.X64Backend.Compile(linked, new Larva.X64.X64Options());
                Check("x64: compiles", compiled.Success, compiled.ToString());
                if (!compiled.Success)
                    continue;
                using var image = compiled.Program!.Load();
                result = ((delegate* unmanaged<long, long>)image.Address("handles::handles"))(200);
            }
            Check($"{backend} (in process): every object read back through its handle, every handle freed", result == Expected, $"{result}");
            Check($"{backend} (in process): collections happened, no misuse", NativeRuntime.Collections > 0 && NativeRuntime.Errors == 0,
                $"collections {NativeRuntime.Collections}, errors {NativeRuntime.Errors}");
        }
        NativeRuntime.Reset();
        NativeRuntime.CollectionThreshold = 4096;
    }

    [Fact]
    public void Llvm_handles_follow_objects_the_nursery_moves() => Executables("llvm");

    [Fact]
    public void X64_handles_keep_their_objects() => Executables("x64");

    /// <summary>
    /// larva_rt.c: the program as an executable under GC stress (a collection at every safepoint): with LLVM the Points
    /// are young and a minor collection copies them out, so a handle whose slot were not updated would read a dead copy.
    /// </summary>
    private void Executables(string backend)
    {
        Assert.SkipWhen(Toolchain.Find() is null, Toolchain.Missing);
        string module = Path.Combine(Folder.Value, $"handles-{backend}{ObjectFile.Extension}");
        ObjectFile.Save(LinkerPrograms.Object(PolyglotPrograms.Handles()), module);
        string exe = Path.Combine(Folder.Value, $"handles-{backend}.exe");
        var (code, log) = Repository.Cli(new Dictionary<string, string> { ["LARVA_DIAGNOSTICS"] = "1" }, 300_000,
            "native", "--exe", "--backend", backend, "--entry", "handles", "-o", exe, module);
        Check($"{backend}: larva native --exe", code == 0, log.Trim());
        if (code != 0)
            return;
        foreach (bool stress in new[] { false, true })
        {
            var (exit, output, errors) = Execute(exe, ["200"], stress);
            Check($"{backend}{(stress ? " under GC stress" : "")}: every object read back through its handle, every handle freed",
                exit == 0 && output.Trim() == Expected.ToString(), $"exit {exit}: {output.Trim()} {errors.Trim()}");
            Check($"{backend}{(stress ? " under GC stress" : "")}: collections happened, no misuse", errors.Contains("errors 0"), errors.Trim());
        }

        string stale = Path.Combine(Folder.Value, $"stale-{backend}.exe");
        (code, log) = Repository.Cli(new Dictionary<string, string> { ["LARVA_DIAGNOSTICS"] = "1" }, 300_000,
            "native", "--exe", "--backend", backend, "--entry", "stale", "-o", stale, module);
        Check($"{backend}: larva native --exe (stale)", code == 0, log.Trim());
        if (code == 0)
        {
            var (exit, _, errors) = Execute(stale, [], stress: false);
            Check($"{backend}: a handle used after it was freed stops the program (exit 101)", exit == 101 && errors.Contains("was freed"), $"exit {exit}: {errors.Trim()}");
        }
    }

    /// <summary>
    /// A Larva DLL (LLVM: young objects that move) loaded by a C program whose five threads call into it at once: each
    /// thread is attached on its first call, stopped for collections while it runs C, and detached at the end; every call
    /// to pg_churn collects (gc.request), so collections run while the other threads come and go. Every handle must read
    /// back its own object and be freed.
    /// </summary>
    [Fact]
    public void Threads_of_another_language_enter_leave_and_detach()
    {
        var toolchain = Toolchain.Find();
        Assert.SkipWhen(toolchain is null, Toolchain.Missing);
        string folder = Path.Combine(Folder.Value, "threads");
        Directory.CreateDirectory(folder);
        string dll = Path.Combine(folder, "pg.dll");
        {
            var linked = Linker.Link([LinkerPrograms.Object(PolyglotPrograms.ThreadLibrary())], new LinkOptions { CLibrary = true, LowerToIl = false });
            Check("the library links", linked.Success, string.Join(" | ", linked.Errors));
            if (!linked.Success)
                return;
            var (built, message) = Larva.Driver.ExecutableBuilder.BuildLibrary(linked, dll);
            Check("pg.dll builds (LLVM)", built, message);
            if (!built)
                return;
        }
        string host = Path.Combine(folder, "host.c"), exe = Path.Combine(folder, "host.exe");
        File.WriteAllText(host, PolyglotPrograms.ThreadHost);
        var (compiled, log) = toolchain!.CompileHost(host, exe, [Path.ChangeExtension(dll, ".lib")]);
        Check("the C host compiles against pg.lib", compiled, log);
        if (!compiled)
            return;
        var (exit, output, errors) = Execute(exe, [], stress: false, timeout: 120_000);
        string[] results = output.Trim().Split(' ');
        Check("five threads: every handle read back its own object (0 wrong), none left (0)",
            exit == 0 && results is ["0", "0", _], $"exit {exit}: {output.Trim()} {errors.Trim()}");
        Check("collections ran while the threads came and went (one per pg_churn: 200 asked)",
            results is [_, _, var ran] && long.TryParse(ran, out long n) && n >= 100, output.Trim());
    }

    private static (int Exit, string Output, string Errors) Execute(string exe, string[] arguments, bool stress, int timeout = 60_000)
    {
        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["LARVA_STATS"] = "1";
        if (stress)
            start.Environment["LARVA_GC_STRESS"] = "1";
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            return (-1, output.Result, errors.Result + " [killed]");
        }
        return (process.ExitCode, output.Result, errors.Result);
    }
}

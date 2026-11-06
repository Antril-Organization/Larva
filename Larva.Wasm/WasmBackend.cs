using Larva.IR;
using Larva.IR.Analysis;
using Larva.IR.Linking;
using Larva.IR.Lowering;

namespace Larva.Wasm
{
    public sealed record WasmOptions
    {
        public static readonly WasmOptions Default = new();

        /// <summary>GC mode and checks. Generics are always specialized.</summary>
        public CompilationOptions Compilation { get; init; } = CompilationOptions.Default;

        /// <summary>Drop the bounds checks <see cref="BoundsAnalysis"/> proves unnecessary.</summary>
        public bool BoundsCheckElimination { get; init; } = true;
    }

    public sealed record WasmResult(WasmProgram? Program, IReadOnlyList<TypeError> TypeErrors, IReadOnlyList<BorrowError> Diagnostics, string? Error)
    {
        public bool Success => Program is not null;

        public override string ToString() => Success ? "ok" : string.Join("\n",
            TypeErrors.Select(e => e.ToString()).Concat(Diagnostics.Select(d => d.ToString())).Append(Error ?? ""));
    }

    /// <summary>A compiled module: the <c>.wasm</c> bytes, and what the host needs to know about it.</summary>
    public sealed class WasmProgram(byte[] module)
    {
        public byte[] Module { get; } = module;
        public GcConfig Gc { get; init; } = GcConfig.Default;

        /// <summary>How the module's functions are called (see <see cref="WasmBackend.Glue"/>).</summary>
        public string Abi { get; init; } = "uniform";
        public int BoundsChecks { get; init; }
        public int BoundsChecksEliminated { get; init; }

        /// <summary>
        /// Writes <c>NAME.wasm</c>, <c>NAME.js</c> (the runtime and loader: <c>node NAME.js [args]</c> runs it, a page
        /// imports it) and <c>NAME.html</c> (runs it in a browser, output on the page), next to each other.
        /// </summary>
        public IReadOnlyList<string> Write(string wasmPath)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(wasmPath))!;
            string name = Path.GetFileNameWithoutExtension(wasmPath);
            Directory.CreateDirectory(folder);
            string wasm = Path.Combine(folder, name + ".wasm");
            string js = Path.Combine(folder, name + ".js");
            string html = Path.Combine(folder, name + ".html");
            File.WriteAllBytes(wasm, Module);
            File.WriteAllText(js, WasmBackend.Glue(name + ".wasm", Gc, Abi));
            File.WriteAllText(html, WasmBackend.Page(name));
            return [wasm, js, html];
        }
    }

    /// <summary>
    /// WebAssembly through the <see cref="WasmCodegen"/> (no LLVM): IR → monomorphization (all generics specialized) →
    /// a <c>.wasm</c> module that imports its runtime from JavaScript (<c>larva_rt.js</c>, shared by Node and browsers).
    /// </summary>
    public static class WasmBackend
    {
        public static WasmResult Compile(IrModule module, WasmOptions? options = null)
        {
            options ??= WasmOptions.Default;
            if (module.HasImports)
                return new WasmResult(null, [], [], "the module has imports: link it first");
            var compilation = options.Compilation with { Generics = GenericsMode.Specialize };
            var (typeErrors, diagnostics, ok) = Compiler.Verify(module, compilation);
            if (!ok)
                return new WasmResult(null, typeErrors, diagnostics, null);
            return Generate(Monomorphizer.Run(module, GenericsMode.Specialize), options);
        }

        /// <summary>Compiles a linked program (with its entry wrappers, see <see cref="ExecutableEntry"/>).</summary>
        public static WasmResult Compile(LinkResult linked, WasmOptions? options = null)
        {
            options ??= WasmOptions.Default;
            if (!linked.Success || linked.Merged is null)
                return new WasmResult(null, [], [], "the link failed");
            LinkResult program;
            try
            {
                program = ExecutableEntry.AddWrappers(linked);
            }
            catch (InvalidOperationException e)
            {
                return new WasmResult(null, [], [], e.Message);
            }
            return Generate(Monomorphizer.Run(program.Merged!, GenericsMode.Specialize, program.Roots), options);
        }

        private static WasmResult Generate(IrModule concrete, WasmOptions options)
        {
            try
            {
                // First pass: which runtime functions the code calls (only those are imported); second: the module.
                var used = new HashSet<string>();
                new WasmCodegen(concrete, options.Compilation.Gc, options.BoundsCheckElimination, used, null).Generate();
                var codegen = new WasmCodegen(concrete, options.Compilation.Gc, options.BoundsCheckElimination, null, used);
                codegen.Generate();
                return new WasmResult(new WasmProgram(codegen.Wasm.ToArray())
                {
                    Gc = options.Compilation.Gc,
                    BoundsChecks = codegen.BoundsChecks,
                    BoundsChecksEliminated = codegen.BoundsChecksEliminated,
                }, [], [], null);
            }
            catch (WasmBackendException e)
            {
                return new WasmResult(null, [], [], e.Message);
            }
        }

        /// <summary>The JavaScript runtime and loader for a module (see <c>Runtime/larva_rt.js</c>).</summary>
        /// <param name="abi"><c>uniform</c> (this back end) or <c>c64</c> (LLVM, 64-bit memory, the C convention).</param>
        public static string Glue(string wasmFile, GcConfig gc, string abi = "uniform")
        {
            using var stream = typeof(WasmBackend).Assembly.GetManifestResourceStream("Larva.Wasm.Runtime.larva_rt.js")
                ?? throw new InvalidOperationException("the JavaScript runtime is not embedded");
            using var reader = new StreamReader(stream);
            string text = reader.ReadToEnd()
                .Replace("__LARVA_WASM__", wasmFile)
                .Replace("__LARVA_GC__", gc.Mode.ToString().ToLowerInvariant())
                .Replace("__LARVA_ABI__", abi);
            // Shipped small: without comment lines and indentation (the code has no multi-line strings).
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal));
            return string.Join("\n", lines) + "\n";
        }

        /// <summary>A page that runs the program and shows what it prints.</summary>
        public static string Page(string name) => $$$"""
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{{{name}}}</title>
            <style>body{font:14px/1.5 ui-monospace,monospace;margin:16px;background:#fff;color:#111}@media(prefers-color-scheme:dark){body{background:#111;color:#eee}}pre{white-space:pre-wrap}</style></head>
            <body><pre id="out"></pre>
            <script type="module">
            import { run } from "./{{{name}}}.js";
            const out = document.getElementById("out");
            const result = await run({ write: text => out.textContent += text });
            if (result.error) out.textContent += "\n" + result.error;
            else if (result.value !== undefined) out.textContent += result.value + "\n";
            </script>
            </body>
            </html>
            """;
    }
}

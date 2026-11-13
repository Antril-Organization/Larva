using Larva.IR;
using LLVMSharp.Interop;

namespace Larva.Llvm
{
    /// <summary>
    /// Code for a large program on several threads. The program's functions are shared out in partitions of about the
    /// same size (by IR statements); each thread generates LLVM IR in its own context for the whole program but with the
    /// bodies of its partition only (the other functions are declared: another object defines them), then optimizes it
    /// and writes an object file. The linker puts the objects together.
    /// <para>
    /// The pieces fit because every Larva function is already a symbol other objects can call; Larva globals become
    /// link-once (each object defines the ones it uses, the linker keeps one); constant data and the helpers code
    /// generation writes (exception filters, the throw function) stay private to each object. Each object has its own
    /// stack map table, and the runtime reads every table in the image.
    /// </para>
    /// <para>
    /// Inlining crosses partitions through copies: a small function another partition defines, called from this one's code
    /// (directly or through other such copies), also gets its body here as <c>available_externally</c>. LLVM may inline it
    /// and drops the copy after optimization; the one real definition stays in its owner's object (see <see cref="Copies"/>).
    /// </para>
    /// </summary>
    public static class ParallelCodegen
    {
        /// <summary>Functions per partition below which a program is not worth splitting.</summary>
        private const int MinimumFunctions = 150;

        /// <summary>How many partitions: <c>LARVA_LLVM_THREADS</c>, else the processors, fewer for small programs.</summary>
        public static int Partitions(int definedFunctions)
        {
            int threads = int.TryParse(Environment.GetEnvironmentVariable("LARVA_LLVM_THREADS"), out int asked) && asked > 0
                ? asked : Math.Min(Environment.ProcessorCount, 16);
            return Math.Max(1, Math.Min(threads, definedFunctions / MinimumFunctions));
        }

        /// <summary>The partition of each function (-1 for declarations): the largest first, each to the least loaded.</summary>
        public static int[] Assign(IrModule module, int partitions)
        {
            var owner = new int[module.Functions.Length];
            var load = new long[partitions];
            var sizes = module.Functions.Select((f, i) => (Index: i, Size: f.IsDeclaration ? -1 : f.Blocks.Sum(b => b.Statements.Length + 1L))).ToList();
            foreach (var (index, _) in sizes.Where(s => s.Size < 0))
                owner[index] = -1;
            foreach (var (index, size) in sizes.Where(s => s.Size >= 0).OrderByDescending(s => s.Size))
            {
                int least = 0;
                for (int p = 1; p < partitions; p++)
                {
                    if (load[p] < load[least])
                        least = p;
                }
                owner[index] = least;
                load[least] += size;
            }
            return owner;
        }

        /// <summary>IR size (statements and terminators) up to which a function is copied into the partitions that call it.</summary>
        public const int CopySize = 40;

        /// <summary>
        /// Whether a copy of <paramref name="function"/> can stand in other objects: small, with a body, not generic, and no
        /// exception handlers (their filters reach the frame through <c>llvm.localescape</c>, which a copy dropped after
        /// optimization would leave dangling). Functions never inlined (cold, noinline) are copied too: LLVM still learns
        /// from the body what the callers may assume (a failure path that never returns, a call that does not unwind or
        /// write memory), which is what lets their loops be simplified and vectorized.
        /// </summary>
        internal static bool Copyable(IrFunction function)
        {
            if (function.IsDeclaration || function.IsGeneric)
                return false;
            long size = 0;
            foreach (var block in function.Blocks)
            {
                if (block.Terminator is IUnwinding { Handlers.Length: > 0 })
                    return false;
                size += block.Statements.Length + 1;
                if (size > CopySize)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Per partition, the functions it copies (see the class remarks): the copyable functions another partition owns
        /// that its own functions call directly, and, from those, the copyable ones they call in turn.
        /// </summary>
        public static HashSet<int>[] Copies(IrModule module, int[] owner, int partitions)
        {
            var copyable = new bool[module.Functions.Length];
            var callees = new int[module.Functions.Length][];
            for (int f = 0; f < module.Functions.Length; f++)
            {
                copyable[f] = Copyable(module.Functions[f]);
                callees[f] = [.. module.Functions[f].Blocks.Select(b => b.Terminator).OfType<CallTerminator>()
                    .Where(c => c.Callee is null).Select(c => c.Function).Distinct()];
            }
            var copies = new HashSet<int>[partitions];
            Parallel.For(0, partitions, p =>
            {
                var set = new HashSet<int>();
                var pending = new Stack<int>();
                for (int f = 0; f < owner.Length; f++)
                {
                    if (owner[f] == p)
                        pending.Push(f);
                }
                while (pending.Count > 0)
                {
                    foreach (int callee in callees[pending.Pop()])
                    {
                        if (owner[callee] != p && owner[callee] >= 0 && copyable[callee] && set.Add(callee))
                            pending.Push(callee);
                    }
                }
                copies[p] = set;
            });
            return copies;
        }

        /// <summary>
        /// Generates, optimizes and emits <paramref name="module"/> as one object per partition in <paramref name="directory"/>:
        /// their paths, in partition order. <paramref name="generate"/> makes a code generator for a context and a filter of
        /// the functions to define.
        /// </summary>
        internal static List<string> Emit(IrModule module, int partitions, Func<LLVMContextRef, Func<int, bool>, Func<int, bool>?, LlvmCodegen> generate,
            string triple, int optimization, string directory, string name, string? cpu = null)
        {
            var owner = Assign(module, partitions);
            // LARVA_LLVM_COPIES=0: no copies (each partition inlines only its own functions, to measure the difference).
            var copies = Environment.GetEnvironmentVariable("LARVA_LLVM_COPIES") == "0" ? null : Copies(module, owner, partitions);
            var objects = Enumerable.Range(0, partitions).Select(p => Path.Combine(directory, $"{name}.{p}.obj")).ToList();
            var failures = new Exception?[partitions];
            Parallel.For(0, partitions, new ParallelOptions { MaxDegreeOfParallelism = partitions }, p =>
            {
                var context = LLVMContextRef.Create();
                try
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var copied = copies?[p];
                    var codegen = generate(context, f => owner[f] == p, copied is null ? null : copied.Contains);
                    codegen.Generate();
                    long generated = watch.ElapsedMilliseconds;
                    using var program = new LlvmProgram(codegen.Module, triple, optimization, cpu: cpu);
                    long optimized = watch.ElapsedMilliseconds;
                    // LARVA_PARTITION_DUMP=folder: each partition's optimized LLVM IR (what crossed partitions and what was inlined).
                    if (Environment.GetEnvironmentVariable("LARVA_PARTITION_DUMP") is { Length: > 0 } dump)
                        File.WriteAllText(Path.Combine(dump, $"{name}.{p}.ll"), program.IrText);
                    program.EmitObject(objects[p]);
                    if (PhaseTimer.Enabled)
                        Console.Error.WriteLine($"[timing]   partition {p} ({copied?.Count ?? 0} copies): generate {generated} ms, optimize {optimized - generated} ms, emit {watch.ElapsedMilliseconds - optimized} ms");
                }
                catch (Exception e) when (e is LlvmBackendException or InvalidOperationException)
                {
                    failures[p] = e;
                }
                finally
                {
                    context.Dispose();
                }
            });
            if (failures.FirstOrDefault(f => f is not null) is { } failure)
                throw failure is LlvmBackendException backend ? backend : new LlvmBackendException(failure.Message);
            return objects;
        }
    }
}

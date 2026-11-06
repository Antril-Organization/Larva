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
    /// stack map table, and the runtime reads every table in the image. Inlining does not cross partitions.
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

        /// <summary>
        /// Generates, optimizes and emits <paramref name="module"/> as one object per partition in <paramref name="directory"/>:
        /// their paths, in partition order. <paramref name="generate"/> makes a code generator for a context and a filter of
        /// the functions to define.
        /// </summary>
        internal static List<string> Emit(IrModule module, int partitions, Func<LLVMContextRef, Func<int, bool>, LlvmCodegen> generate,
            string triple, int optimization, string directory, string name, string? cpu = null)
        {
            var owner = Assign(module, partitions);
            var objects = Enumerable.Range(0, partitions).Select(p => Path.Combine(directory, $"{name}.{p}.obj")).ToList();
            var failures = new Exception?[partitions];
            Parallel.For(0, partitions, new ParallelOptions { MaxDegreeOfParallelism = partitions }, p =>
            {
                var context = LLVMContextRef.Create();
                try
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var codegen = generate(context, f => owner[f] == p);
                    codegen.Generate();
                    long generated = watch.ElapsedMilliseconds;
                    using var program = new LlvmProgram(codegen.Module, triple, optimization, cpu: cpu);
                    long optimized = watch.ElapsedMilliseconds;
                    program.EmitObject(objects[p]);
                    if (PhaseTimer.Enabled)
                        Console.Error.WriteLine($"[timing]   partition {p}: generate {generated} ms, optimize {optimized - generated} ms, emit {watch.ElapsedMilliseconds - optimized} ms");
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

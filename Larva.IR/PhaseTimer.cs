using System.Diagnostics;

namespace Larva.IR
{
    /// <summary>
    /// Phase timings of the toolchain, written to stderr when <c>LARVA_TIMINGS=1</c>: what a slow start spends its time on.
    /// <c>using (PhaseTimer.Start("name")) { … }</c> around a phase; nested phases are indented.
    /// </summary>
    public static class PhaseTimer
    {
        public static bool Enabled { get; } = Environment.GetEnvironmentVariable("LARVA_TIMINGS") is { Length: > 0 } value && value != "0";

        [ThreadStatic]
        private static int depth; // per thread: phases may run on several at once

        public static IDisposable? Start(string phase) => Enabled ? new Phase(phase) : null;

        private sealed class Phase : IDisposable
        {
            private readonly string name;
            private readonly Stopwatch watch = Stopwatch.StartNew();
            private readonly int level;

            public Phase(string name)
            {
                this.name = name;
                level = depth++;
            }

            public void Dispose()
            {
                depth--;
                Console.Error.WriteLine($"[timing] {new string(' ', level * 2)}{name}: {watch.Elapsed.TotalMilliseconds:N0} ms");
            }
        }
    }
}

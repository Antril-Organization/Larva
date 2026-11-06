using System.Diagnostics;

namespace Larva.Hir.Tests;

/// <summary>
/// The benchmarks: each one's name, what it measures, its input on the native backends and on the LVM, and the same
/// program written for .NET (<see cref="Reference"/>) as the reference for results and speed; and how executables of
/// them are run and timed (also used by the language tests built on Larva).
/// </summary>
internal static class Benchmarks
{
    public sealed record Benchmark(string Name, string What, int Native, int Vm, Func<int, int> Reference);

    /// <summary>In the order of the benchmark program's <c>Main(which, n)</c>.</summary>
    public static readonly Benchmark[] All =
    [
        new("Fib", "recursive calls", 32, 22, Reference.Fib),
        new("Dispatch", "virtual calls on two classes", 50_000_000, 200_000, Reference.Dispatch),
        new("Alloc", "objects made and read", 10_000_000, 100_000, Reference.Alloc),
        new("Structs", "struct method calls", 50_000_000, 200_000, Reference.Structs),
        new("Throw", "exceptions thrown and caught", 20_000, 20_000, Reference.Throw),
        new("Mono", "virtual calls on one class", 50_000_000, 200_000, Reference.Mono),
    ];

    public static (string Output, double Ms) Execute(string exe, int which, int n)
    {
        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(which.ToString());
        start.ArgumentList.Add(n.ToString());
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(120_000);
        return (output.Trim(), watch.Elapsed.TotalMilliseconds);
    }

    public static (int Result, double Ms) Time(Func<int> run)
    {
        var watch = Stopwatch.StartNew();
        int result = run();
        return (result, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>The same programs written for .NET, as the reference for results and speed.</summary>
    public static class Reference
    {
        private abstract class Shape { public abstract int Area(); }
        private sealed class Square(int s) : Shape { public int S = s; public override int Area() => S * S; }
        private sealed class Triangle(int b) : Shape { public int B = b; public override int Area() => B * 3; }
        private sealed class Cell(int v) { public int V = v; }
        private struct Point(int x, int y)
        {
            public int X = x, Y = y;
            public readonly int Dot() => X * X + Y * Y;
        }
        private sealed class Failure : Exception;

        public static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);

        public static int Dispatch(int n)
        {
            Shape a = new Square(3), b = new Triangle(5);
            int s = 0;
            for (int i = 0; i < n; i++)
                s += ((i & 1) == 0 ? a : b).Area();
            return s;
        }

        public static int Mono(int n)
        {
            Shape a = new Square(3);
            int s = 0;
            for (int i = 0; i < n; i++)
                s += a.Area();
            return s;
        }

        public static int Alloc(int n)
        {
            int s = 0;
            for (int i = 0; i < n; i++)
                s += new Cell(i).V;
            return s;
        }

        public static int Structs(int n)
        {
            var p = new Point(1, 2);
            int s = 0;
            for (int i = 0; i < n; i++)
            {
                p.X = i & 1023;
                s += p.Dot();
            }
            return s;
        }

        public static int Throw(int n)
        {
            int s = 0;
            for (int i = 0; i < n; i++)
            {
                try { throw new Failure(); }
                catch (Failure) { s++; }
            }
            return s;
        }
    }
}

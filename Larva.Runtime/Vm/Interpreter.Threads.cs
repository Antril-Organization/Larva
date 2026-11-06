using Larva.IR;

namespace Larva.Runtime.Vm
{
    /// <summary>
    /// Threads (System.Threading) in the interpreter: each is an interpreter of its own (its call stack, its regions) on
    /// a host thread, sharing the heap and the module. They take turns, one interpreting at a time: a thread gives its
    /// turn up at a safepoint when another one waits for it (first come, first served) and while it waits in the runtime
    /// (sleeping, joining, for a lock or a condition). No collection runs while more than one thread is alive (the collector
    /// knows the call stack of the interpreter it runs on).
    /// </summary>
    public sealed partial class Interpreter
    {
        /// <summary>What the interpreters of one program share once it has started a thread.</summary>
        private sealed class Threading
        {
            private readonly object gate = new();
            private long next, serving;
            public int Alive = 1;
            public long NextNumber = 1;
            public readonly Dictionary<long, Thread> Threads = [];
            public readonly Dictionary<long, object> Handles = [];
            public long NextHandle = 1;

            public bool Wanted => Volatile.Read(ref next) - Volatile.Read(ref serving) > 1;

            public void Take()
            {
                lock (gate)
                {
                    long ticket = next++;
                    while (serving != ticket)
                        Monitor.Wait(gate);
                }
            }

            public void Give()
            {
                lock (gate)
                {
                    serving++;
                    Monitor.PulseAll(gate);
                }
            }
        }

        private Threading? threading;
        [ThreadStatic] private static long threadNumber;

        /// <summary>The interpreter of a new thread: the parent's module, heap, options and statistics, a call stack of its own.</summary>
        private Interpreter(Interpreter parent)
        {
            il = parent.il;
            options = parent.options;
            Runtime = parent.Runtime;
            shapes = parent.shapes;
            Heap = parent.Heap;
            Stats = parent.Stats;
            threading = parent.threading;
            globals = parent.globals; // one program: the same globals (static fields) and static data
            statics = parent.statics;
            streams = parent.Streams; // and the same open streams (a thread accepts on the listener another opened)
        }

        private bool ManyThreads => threading is { } t && Volatile.Read(ref t.Alive) > 1;

        /// <summary>At a safepoint: the turn goes to a waiting thread first.</summary>
        private void TakeTurns()
        {
            if (threading is { Wanted: true } t)
            {
                t.Give();
                t.Take();
            }
        }

        /// <summary>Gives the turn up while <paramref name="wait"/> runs (it must not touch the heap), then takes it back.</summary>
        private T Blocking<T>(Func<T> wait)
        {
            var t = threading;
            if (t is null)
                return wait();
            t.Give();
            try
            {
                return wait();
            }
            finally
            {
                t.Take();
            }
        }

        private long NewHandle(object value)
        {
            var t = threading ??= StartThreading();
            lock (t.Handles)
            {
                long handle = t.NextHandle++;
                t.Handles[handle] = value;
                return handle;
            }
        }

        private T HandleOf<T>(long handle) where T : class
        {
            var t = threading ?? throw new VmException("no such handle");
            lock (t.Handles)
                return (T)t.Handles[handle];
        }

        private void FreeHandle(long handle)
        {
            if (threading is { } t)
                lock (t.Handles)
                    t.Handles.Remove(handle);
        }

        /// <summary>The first thread (or lock): this interpreter holds the turn from now on.</summary>
        private Threading StartThreading()
        {
            var t = new Threading();
            t.Take();
            return t;
        }

        private sealed class ConditionBox
        {
            public readonly List<SemaphoreSlim> Waiters = [];
        }

        /// <summary>The thread, lock and atomic services (ids 63 to 87); false for others.</summary>
        private bool RunThreadIntrinsic(Frame frame, int id)
        {
            var stack = frame.Stack;
            switch (id)
            {
                case 63:
                {
                    var environment = Pop(stack);
                    var code = Pop(stack);
                    int function = code.Kind == ValueKind.Function ? code.Function : FunctionOf(Heap.AsInteger(code));
                    var t = threading ??= StartThreading();
                    long number = Interlocked.Increment(ref t.NextNumber);
                    Interlocked.Increment(ref t.Alive);
                    var child = new Interpreter(this);
                    var output = options.Output;
                    var thread = new Thread(() =>
                    {
                        threadNumber = number;
                        t.Take();
                        try
                        {
                            child.Call(function, [], [environment]);
                        }
                        catch (LarvaException)
                        {
                            (output ?? Console.Error).WriteLine($"unhandled exception in thread {number}");
                        }
                        finally
                        {
                            Interlocked.Decrement(ref t.Alive);
                            t.Give();
                        }
                    }, 64 << 20) { IsBackground = true, Name = $"Larva VM thread {number}" };
                    lock (t.Threads)
                        t.Threads[number] = thread;
                    thread.Start();
                    stack.Add(Value.FromInt(number, IrType.I64));
                    return true;
                }
                case 64:
                {
                    long milliseconds = PopLong(stack), handle = PopLong(stack);
                    Thread? thread = null;
                    if (threading is { } t)
                        lock (t.Threads)
                            t.Threads.TryGetValue(handle, out thread);
                    bool done = thread is null || Blocking(() => thread.Join(milliseconds < 0 ? Timeout.Infinite : (int)Math.Min(milliseconds, int.MaxValue)));
                    if (done && threading is { } owner)
                        lock (owner.Threads)
                            owner.Threads.Remove(handle);
                    stack.Add(Value.FromInt(done ? 1 : 0, IrType.I64));
                    return true;
                }
                case 65:
                    stack.Add(Value.FromInt(threadNumber == 0 ? 1 : threadNumber, IrType.I64));
                    return true;
                case 66:
                    Blocking(() => Thread.Yield());
                    return true;
                case 67:
                    stack.Add(Value.FromInt(Environment.ProcessorCount, IrType.I64));
                    return true;
                case 68:
                    stack.Add(Value.FromInt(NewHandle(new object()), IrType.I64));
                    return true;
                case 69:
                {
                    var sync = HandleOf<object>(PopLong(stack));
                    if (!Monitor.TryEnter(sync))
                        Blocking(() => { Monitor.Enter(sync); return 0; });
                    return true;
                }
                case 70:
                    stack.Add(Value.FromInt(Monitor.TryEnter(HandleOf<object>(PopLong(stack))) ? 1 : 0, IrType.I64));
                    return true;
                case 71:
                    Monitor.Exit(HandleOf<object>(PopLong(stack)));
                    return true;
                case 72 or 77:
                    FreeHandle(PopLong(stack));
                    return true;
                case 73:
                    stack.Add(Value.FromInt(NewHandle(new ConditionBox()), IrType.I64));
                    return true;
                case 74:
                {
                    long milliseconds = PopLong(stack);
                    var sync = HandleOf<object>(PopLong(stack));
                    var box = HandleOf<ConditionBox>(PopLong(stack));
                    var woken = new SemaphoreSlim(0);
                    lock (box.Waiters)
                        box.Waiters.Add(woken);
                    Monitor.Exit(sync);
                    bool signaled = Blocking(() =>
                    {
                        bool got = woken.Wait(milliseconds < 0 ? Timeout.Infinite : (int)Math.Min(milliseconds, int.MaxValue));
                        if (!got)
                            lock (box.Waiters)
                                got = !box.Waiters.Remove(woken);
                        Monitor.Enter(sync);
                        return got;
                    });
                    stack.Add(Value.FromInt(signaled ? 1 : 0, IrType.I64));
                    return true;
                }
                case 75:
                {
                    var box = HandleOf<ConditionBox>(PopLong(stack));
                    lock (box.Waiters)
                    {
                        if (box.Waiters.Count > 0)
                        {
                            box.Waiters[0].Release();
                            box.Waiters.RemoveAt(0);
                        }
                    }
                    return true;
                }
                case 76:
                {
                    var box = HandleOf<ConditionBox>(PopLong(stack));
                    lock (box.Waiters)
                    {
                        foreach (var waiter in box.Waiters)
                            waiter.Release();
                        box.Waiters.Clear();
                    }
                    return true;
                }
                case >= 78 and <= 87:
                {
                    // Atomics: one interpreter runs at a time, so plain reads and writes are atomic here.
                    var type = id >= 83 ? IrType.I32 : IrType.I64;
                    int op = id >= 83 ? id - 83 : id - 78;
                    long value = 0, expected = 0;
                    if (op == 4)
                    {
                        value = PopLong(stack);
                        expected = PopLong(stack);
                    }
                    else if (op != 0)
                    {
                        value = PopLong(stack);
                    }
                    var at = Heap.AsAddress(Pop(stack));
                    long before = (long)Heap.AsInteger(Heap.Coerce(Heap.Read(type, at), IrType.I64));
                    long after = op switch { 1 => value, 2 => before + value, 3 => value, 4 => before == expected ? value : before, _ => before };
                    if (type == IrType.I32)
                        after = (int)after;
                    if (op != 0)
                        Heap.Write(type, at, Value.FromInt(after, type));
                    if (op != 1)
                        stack.Add(Value.FromInt(before, type));
                    return true;
                }
            }
            return false;
        }
    }
}

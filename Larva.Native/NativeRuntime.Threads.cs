using System.Runtime.InteropServices;

namespace Larva.Native
{
    /// <summary>
    /// Threads in the host process (System.Threading): each Larva thread is a host thread, and they take turns, one running
    /// Larva code at a time (this runtime's tables are not shared safely). A thread gives its turn up at a safepoint when
    /// another one waits for it (turns are first come, first served), and while it waits in the runtime: sleeping,
    /// joining, for a lock or a condition. No collection runs while more than one thread is alive (the collector walks the
    /// stack of the thread it runs on); executables run threads in parallel instead (larva_rt.c).
    /// </summary>
    public static unsafe partial class NativeRuntime
    {
        /// <summary>A ticket lock: the threads waiting for a turn get it in the order they asked.</summary>
        private sealed class Turns
        {
            private readonly object gate = new();
            private long next, serving;

            /// <summary>Whether a thread waits for the turn (the holder should give it up at its next safepoint).</summary>
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

        private static Turns? turns;
        private static int threadsAlive = 1;
        private static long nextThread = 1;
        [ThreadStatic] private static long threadNumber;
        private static readonly Dictionary<long, Thread> threads = [];

        private static void ResetThreads()
        {
            turns = null;
            threadsAlive = 1;
            nextThread = 1;
            threadNumber = 0;
            lock (threads)
                threads.Clear();
        }

        /// <summary>Gives the turn up while <paramref name="wait"/> runs (it must not touch Larva state), then takes it back.</summary>
        private static T Blocking<T>(Func<T> wait)
        {
            var held = turns;
            if (held is null)
                return wait();
            held.Give();
            try
            {
                return wait();
            }
            finally
            {
                held.Take();
            }
        }

        /// <summary>At a safepoint: the turn goes to a waiting thread first.</summary>
        private static void TakeTurns()
        {
            if (turns is { Wanted: true } held)
            {
                held.Give();
                held.Take();
            }
        }

        /// <summary>Whether collections must wait: more than one thread is alive.</summary>
        private static bool Threaded => Volatile.Read(ref threadsAlive) > 1;

        [UnmanagedCallersOnly]
        private static long ThreadStart(void* code, void* environment)
        {
            if (turns is null)
            {
                turns = new Turns();
                turns.Take(); // the calling thread runs Larva code: it holds the turn from now on
            }
            var held = turns;
            long number = Interlocked.Increment(ref nextThread);
            Interlocked.Increment(ref threadsAlive);
            nint function = (nint)code, argument = (nint)environment;
            var thread = new Thread(() =>
            {
                threadNumber = number;
                held.Take();
                try
                {
                    ((delegate* unmanaged<void*, void>)function)((void*)argument);
                }
                finally
                {
                    Interlocked.Decrement(ref threadsAlive);
                    held.Give();
                }
            }, 16 << 20) { IsBackground = true, Name = $"Larva thread {number}" };
            lock (threads)
                threads[number] = thread;
            thread.Start();
            return number;
        }

        [UnmanagedCallersOnly]
        private static long ThreadJoin(long handle, long milliseconds)
        {
            Thread? thread;
            lock (threads)
                threads.TryGetValue(handle, out thread);
            if (thread is null)
                return 1;
            bool done = Blocking(() => thread.Join(milliseconds < 0 ? Timeout.Infinite : (int)Math.Min(milliseconds, int.MaxValue)));
            if (done)
                lock (threads)
                    threads.Remove(handle);
            return done ? 1 : 0;
        }

        [UnmanagedCallersOnly]
        private static long ThreadId() => threadNumber == 0 ? 1 : threadNumber;

        [UnmanagedCallersOnly]
        private static void ThreadYield() => Blocking(() => Thread.Yield());

        [UnmanagedCallersOnly]
        private static long CpuCount() => Environment.ProcessorCount;

        // ── Locks and conditions (handles of host objects) ──

        private sealed class ConditionBox
        {
            public readonly List<SemaphoreSlim> Waiters = [];
        }

        private static long NewHandle(object value) => GCHandle.ToIntPtr(GCHandle.Alloc(value));

        private static T Of<T>(long handle) where T : class => (T)GCHandle.FromIntPtr((nint)handle).Target!;

        [UnmanagedCallersOnly]
        private static long MutexNew() => NewHandle(new object());

        [UnmanagedCallersOnly]
        private static void MutexLock(long handle)
        {
            var sync = Of<object>(handle);
            if (Monitor.TryEnter(sync))
                return;
            Blocking(() => { Monitor.Enter(sync); return 0; });
        }

        [UnmanagedCallersOnly]
        private static long MutexTryLock(long handle) => Monitor.TryEnter(Of<object>(handle)) ? 1 : 0;

        [UnmanagedCallersOnly]
        private static void MutexUnlock(long handle) => Monitor.Exit(Of<object>(handle));

        [UnmanagedCallersOnly]
        private static void HandleFree(long handle) => GCHandle.FromIntPtr((nint)handle).Free();

        [UnmanagedCallersOnly]
        private static long CondNew() => NewHandle(new ConditionBox());

        [UnmanagedCallersOnly]
        private static long CondWait(long condition, long mutex, long milliseconds)
        {
            var box = Of<ConditionBox>(condition);
            var sync = Of<object>(mutex);
            var woken = new SemaphoreSlim(0);
            lock (box.Waiters)
                box.Waiters.Add(woken);
            Monitor.Exit(sync);
            bool signaled = Blocking(() =>
            {
                bool got = woken.Wait(milliseconds < 0 ? Timeout.Infinite : (int)Math.Min(milliseconds, int.MaxValue));
                if (!got)
                    lock (box.Waiters)
                        got = !box.Waiters.Remove(woken); // signaled just as the time ran out
                Monitor.Enter(sync);
                return got;
            });
            return signaled ? 1 : 0;
        }

        [UnmanagedCallersOnly]
        private static void CondSignal(long condition)
        {
            var box = Of<ConditionBox>(condition);
            lock (box.Waiters)
            {
                if (box.Waiters.Count == 0)
                    return;
                var first = box.Waiters[0];
                box.Waiters.RemoveAt(0);
                first.Release();
            }
        }

        [UnmanagedCallersOnly]
        private static void CondBroadcast(long condition)
        {
            var box = Of<ConditionBox>(condition);
            lock (box.Waiters)
            {
                foreach (var waiter in box.Waiters)
                    waiter.Release();
                box.Waiters.Clear();
            }
        }

        // ── Atomics (the x64 back end calls these) ──

        [UnmanagedCallersOnly] private static long AtomicLoad64(long* at) => Interlocked.Read(ref *at);
        [UnmanagedCallersOnly] private static void AtomicStore64(long* at, long value) => Interlocked.Exchange(ref *at, value);
        [UnmanagedCallersOnly] private static long AtomicAdd64(long* at, long value) => Interlocked.Add(ref *at, value) - value;
        [UnmanagedCallersOnly] private static long AtomicExchange64(long* at, long value) => Interlocked.Exchange(ref *at, value);
        [UnmanagedCallersOnly] private static long AtomicCompare64(long* at, long expected, long value) => Interlocked.CompareExchange(ref *at, value, expected);
        [UnmanagedCallersOnly] private static int AtomicLoad32(int* at) => Volatile.Read(ref *at);
        [UnmanagedCallersOnly] private static void AtomicStore32(int* at, int value) => Interlocked.Exchange(ref *at, value);
        [UnmanagedCallersOnly] private static int AtomicAdd32(int* at, int value) => Interlocked.Add(ref *at, value) - value;
        [UnmanagedCallersOnly] private static int AtomicExchange32(int* at, int value) => Interlocked.Exchange(ref *at, value);
        [UnmanagedCallersOnly] private static int AtomicCompare32(int* at, int expected, int value) => Interlocked.CompareExchange(ref *at, value, expected);

        private static void AddThreadSymbols(Dictionary<string, nint> symbols)
        {
            symbols["larva_thread_start"] = (nint)(delegate* unmanaged<void*, void*, long>)&ThreadStart;
            symbols["larva_thread_join"] = (nint)(delegate* unmanaged<long, long, long>)&ThreadJoin;
            symbols["larva_thread_id"] = (nint)(delegate* unmanaged<long>)&ThreadId;
            symbols["larva_thread_yield"] = (nint)(delegate* unmanaged<void>)&ThreadYield;
            symbols["larva_cpu_count"] = (nint)(delegate* unmanaged<long>)&CpuCount;
            symbols["larva_mutex_new"] = (nint)(delegate* unmanaged<long>)&MutexNew;
            symbols["larva_mutex_lock"] = (nint)(delegate* unmanaged<long, void>)&MutexLock;
            symbols["larva_mutex_trylock"] = (nint)(delegate* unmanaged<long, long>)&MutexTryLock;
            symbols["larva_mutex_unlock"] = (nint)(delegate* unmanaged<long, void>)&MutexUnlock;
            symbols["larva_mutex_free"] = (nint)(delegate* unmanaged<long, void>)&HandleFree;
            symbols["larva_cond_new"] = (nint)(delegate* unmanaged<long>)&CondNew;
            symbols["larva_cond_wait"] = (nint)(delegate* unmanaged<long, long, long, long>)&CondWait;
            symbols["larva_cond_signal"] = (nint)(delegate* unmanaged<long, void>)&CondSignal;
            symbols["larva_cond_broadcast"] = (nint)(delegate* unmanaged<long, void>)&CondBroadcast;
            symbols["larva_cond_free"] = (nint)(delegate* unmanaged<long, void>)&HandleFree;
            symbols["larva_atomic_load_i64"] = (nint)(delegate* unmanaged<long*, long>)&AtomicLoad64;
            symbols["larva_atomic_store_i64"] = (nint)(delegate* unmanaged<long*, long, void>)&AtomicStore64;
            symbols["larva_atomic_add_i64"] = (nint)(delegate* unmanaged<long*, long, long>)&AtomicAdd64;
            symbols["larva_atomic_xchg_i64"] = (nint)(delegate* unmanaged<long*, long, long>)&AtomicExchange64;
            symbols["larva_atomic_cas_i64"] = (nint)(delegate* unmanaged<long*, long, long, long>)&AtomicCompare64;
            symbols["larva_atomic_load_i32"] = (nint)(delegate* unmanaged<int*, int>)&AtomicLoad32;
            symbols["larva_atomic_store_i32"] = (nint)(delegate* unmanaged<int*, int, void>)&AtomicStore32;
            symbols["larva_atomic_add_i32"] = (nint)(delegate* unmanaged<int*, int, int>)&AtomicAdd32;
            symbols["larva_atomic_xchg_i32"] = (nint)(delegate* unmanaged<int*, int, int>)&AtomicExchange32;
            symbols["larva_atomic_cas_i32"] = (nint)(delegate* unmanaged<int*, int, int, int>)&AtomicCompare32;
        }
    }
}

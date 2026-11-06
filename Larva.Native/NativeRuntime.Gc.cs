using System.Runtime.InteropServices;

namespace Larva.Native
{
    /// <summary>
    /// Tracing collection for natively compiled code: a non-moving mark-sweep.
    /// <list type="bullet">
    /// <item>Stack roots are precise: the unwinder walks from the safepoint through the JIT frames above the host, and each
    /// frame's root slots come from its function's stack map (<see cref="StackMapFormat"/>).</item>
    /// <item>Heap cells (<c>box</c>, exception payloads) are owned, so they are roots too; pinned objects as well.</item>
    /// <item>Objects and heap cells are scanned word by word; a word counts only if it is the payload address of a live
    /// <c>gc</c> object, so stale or uninitialized slots can at worst retain an object, never corrupt one. Exact
    /// per-type pointer maps can replace this scan without touching the stack side.</item>
    /// </list>
    /// </summary>
    public static unsafe partial class NativeRuntime
    {
        private static readonly Dictionary<nint, int> pinned = [];
        private static readonly Dictionary<int, CodeRange> code = [];
        private static int nextCode;
        private static long sinceCollection;

        private sealed record CodeRange(nint Start, nint End, Dictionary<nint, StackRoot[]> Roots);

        /// <summary>Collect at every safepoint (tests).</summary>
        public static bool StressGc { get; set; }

        /// <summary><c>gc</c> objects allocated between automatic collections.</summary>
        public static long CollectionThreshold { get; set; } = 4096;

        public static int Collections { get; private set; }

        /// <summary>Objects reclaimed by tracing collections.</summary>
        public static int Collected { get; private set; }

        /// <summary>Stack roots scanned by the last collection (frames with a stack map × their slots).</summary>
        public static int LastStackRoots { get; private set; }

        /// <summary>Larva frames found on the stack by the last collection.</summary>
        public static int LastFrames { get; private set; }

        public static int RegisterCode(nint start, nint end, Dictionary<nint, StackRoot[]> roots)
        {
            int id = nextCode++;
            code[id] = new CodeRange(start, end, roots);
            return id;
        }

        /// <summary>The code is unloaded: finalizers that point into it can never run (their objects outlived the program).</summary>
        public static void UnregisterCode(int id)
        {
            if (!code.Remove(id, out var range))
                return;
            foreach (var payload in finalizers.Where(kv => kv.Value >= range.Start && kv.Value < range.End).Select(kv => kv.Key).ToList())
                finalizers.Remove(payload);
        }

        private static CodeRange? CodeAt(nint pc)
        {
            foreach (var range in code.Values)
                if (pc >= range.Start && pc < range.End)
                    return range;
            return null;
        }

        /// <summary>
        /// Collects from host code, while no Larva code is running: only heap cells and pins are roots, so every object
        /// referenced only from finished frames is reclaimed.
        /// </summary>
        public static void Collect() => Collect(walkStack: false);

        private static void CollectFromSafepoint() => Collect(walkStack: true);

        private static void Collect(bool walkStack)
        {
            collecting++;
            try
            {
                CollectNow(walkStack);
            }
            finally
            {
                collecting--;
                customAsked = -1;
            }
            if (collecting == 0)
                ReleasePending();
        }

        private static void CollectNow(bool walkStack)
        {
            var marked = new HashSet<nint>();
            var pending = new Stack<nint>();

            void Consider(nint word)
            {
                if (live.TryGetValue(word, out var kind) && kind == AllocationKind.Gc && marked.Add(word))
                    pending.Push(word);
            }

            void Scan(byte* start, long size)
            {
                for (long offset = 0; offset + 8 <= size; offset += 8)
                    Consider(*(nint*)(start + offset));
            }

            LastFrames = LastStackRoots = 0;
            if (walkStack)
                WalkStack((slot, size) => Scan(slot, size));
            foreach (var (pointer, kind) in live)
                if (kind == AllocationKind.Heap)
                    Scan((byte*)pointer, sizes[pointer]);
            foreach (var handle in pinned.Keys)
                Consider(handle);

            void Drain()
            {
                while (pending.Count > 0)
                {
                    var payload = pending.Pop();
                    Scan((byte*)payload, sizes[payload]);
                }
            }
            Drain();

            // Unreachable objects with a finalizer survive this collection (with what they reach): their finalizers run
            // after the sweep, and a later collection frees them once they are unreachable again.
            var dying = finalizers.Keys.Where(p => !marked.Contains(p) && !regionOf.ContainsKey(p)).ToList();
            foreach (var payload in dying)
                Consider(payload);
            Drain();

            var dead = live.Where(kv => kv.Value == AllocationKind.Gc && !marked.Contains(kv.Key) && !regionOf.ContainsKey(kv.Key))
                           .Select(kv => kv.Key).ToList();
            foreach (var payload in dead)
            {
                if (StressGc)
                    NativeMemory.Fill((void*)payload, (nuint)Math.Max(1, sizes[payload]), 0xDD); // make use-after-free visible
                FreeGc(payload);
            }
            Collected += dead.Count;
            Collections++;
            sinceCollection = 0;
            foreach (var payload in dying)
                Finalize(payload);
        }

        // ── Stack walking (Windows x64) ─────────────────────────────

        private const int ContextSize = 1232, ContextRsp = 0x98, ContextRbp = 0xA0, ContextRip = 0xF8;

        [StructLayout(LayoutKind.Sequential)]
        private struct RuntimeFunction
        {
            public uint Begin, End, UnwindInfo;
        }

        /// <summary>
        /// Unwinds from here: the runtime's own frames, then the JIT frames (Larva code), stopping at the first frame past
        /// them, which belongs to the host that called into Larva.
        /// </summary>
        private delegate void RootVisitor(byte* slot, long size);

        private static void WalkStack(RootVisitor visit)
        {
            var context = (byte*)NativeMemory.AlignedAlloc(ContextSize, 16);
            try
            {
                NativeMemory.Clear(context, ContextSize);
                RtlCaptureContext(context);
                bool inLarva = false;
                for (int frame = 0; frame < 100_000; frame++)
                {
                    ulong pc = *(ulong*)(context + ContextRip);
                    if (pc == 0)
                        break;
                    var range = CodeAt((nint)pc);
                    if (range is null && inLarva)
                        break;

                    ulong imageBase;
                    var function = RtlLookupFunctionEntry(pc, &imageBase, null);
                    if (range is not null)
                    {
                        inLarva = true;
                        LastFrames++;
                        if (function != null && range.Roots.TryGetValue((nint)(imageBase + function->Begin), out var roots))
                        {
                            foreach (var root in roots)
                            {
                                ulong register = root.Register switch
                                {
                                    7 => *(ulong*)(context + ContextRsp),
                                    6 => *(ulong*)(context + ContextRbp),
                                    _ => 0,
                                };
                                if (register == 0)
                                {
                                    Errors++;
                                    continue;
                                }
                                visit((byte*)(register + (ulong)(long)root.Offset), root.Size);
                                LastStackRoots++;
                            }
                        }
                    }

                    if (function == null)
                    {
                        // Leaf function: the return address is on top of the stack.
                        ulong rsp = *(ulong*)(context + ContextRsp);
                        *(ulong*)(context + ContextRip) = *(ulong*)rsp;
                        *(ulong*)(context + ContextRsp) = rsp + 8;
                    }
                    else
                    {
                        void* handlerData;
                        ulong establisher;
                        RtlVirtualUnwind(0, imageBase, pc, function, context, &handlerData, &establisher, null);
                    }
                }
            }
            finally
            {
                NativeMemory.AlignedFree(context);
            }
        }

        [DllImport("kernel32")] private static extern void RtlCaptureContext(void* context);
        [DllImport("kernel32")] private static extern RuntimeFunction* RtlLookupFunctionEntry(ulong pc, ulong* imageBase, void* history);
        [DllImport("kernel32")]
        private static extern void* RtlVirtualUnwind(uint type, ulong imageBase, ulong pc, RuntimeFunction* function, void* context,
            void** handlerData, ulong* establisherFrame, void* contextPointers);
    }
}

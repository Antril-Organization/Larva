using System.Runtime.InteropServices;

namespace Larva.Native
{
    /// <summary>
    /// Handles and threads of other languages (Larva.Polyglot), as <c>larva_rt.c</c> has them: a handle is a slot's index
    /// + 1 and the slot's generation, the slot a root of the collector, and a freed handle used again stops the program.
    /// The collector here never moves objects, so a slot only keeps its object alive. Code in this process runs one thread
    /// at a time (<see cref="TakeTurns"/>), so entering and leaving only count how deep a thread is.
    /// </summary>
    public static unsafe partial class NativeRuntime
    {
        private struct HandleSlot
        {
            public nint Object;
            public uint Generation;
            public uint NextFree;
        }

        private static HandleSlot[] handleSlots = new HandleSlot[64];
        private static uint handlesMade, handleFreeList;
        private static readonly object handleLock = new();
        [ThreadStatic] private static int depth;

        /// <summary>How many handles are held.</summary>
        public static long HandleCount { get; private set; }

        private static void ResetHandles()
        {
            lock (handleLock)
            {
                handleSlots = new HandleSlot[64];
                handlesMade = handleFreeList = 0;
                HandleCount = 0;
            }
        }

        /// <summary>The objects handles hold (the collector's roots).</summary>
        private static IEnumerable<nint> HandleObjects()
        {
            for (uint i = 0; i < handlesMade; i++)
                if (handleSlots[i].Object != 0)
                    yield return handleSlots[i].Object;
        }

        [UnmanagedCallersOnly]
        private static ulong HandleNew(void* handle)
        {
            if (handle == null)
                return 0;
            uint index;
            ulong made;
            lock (handleLock)
            {
                if (handleFreeList != 0)
                {
                    index = handleFreeList - 1;
                    handleFreeList = handleSlots[index].NextFree;
                }
                else
                {
                    if (handlesMade == handleSlots.Length)
                        Array.Resize(ref handleSlots, handleSlots.Length * 2);
                    index = handlesMade++;
                }
                ref var slot = ref handleSlots[index];
                slot.Object = (nint)handle;
                slot.NextFree = 0;
                HandleCount++;
                made = (ulong)slot.Generation << 32 | index + 1;
            }
            if (live.ContainsKey((nint)handle))
                ((delegate* unmanaged<void*, void>)&GcRetain)(handle);
            return made;
        }

        /// <summary>A handle that was freed, or never made: the program stops, as larva_rt.c's does.</summary>
        private static void Misuse(ulong handle)
        {
            LastPanic = $"handle {handle:x} is not one the runtime gave, or was freed";
            Environment.FailFast($"larva panic: {LastPanic}");
        }

        [UnmanagedCallersOnly]
        private static void* HandleGet(ulong handle)
        {
            if ((uint)handle == 0)
                return null;
            lock (handleLock)
            {
                if (!TrySlot(handle, out nint target))
                {
                    Misuse(handle);
                    return null;
                }
                return (void*)target;
            }
        }

        [UnmanagedCallersOnly]
        private static void HandleFree(ulong handle)
        {
            if ((uint)handle == 0)
                return;
            nint target;
            lock (handleLock)
            {
                if (!TrySlot(handle, out target))
                {
                    Misuse(handle);
                    return;
                }
                ref var slot = ref handleSlots[(uint)handle - 1];
                slot.Object = 0;
                slot.Generation++;
                slot.NextFree = handleFreeList;
                handleFreeList = (uint)handle;
                HandleCount--;
            }
            if (live.ContainsKey(target))
                ((delegate* unmanaged<void*, void>)&GcRelease)((void*)target);
        }

        private static bool TrySlot(ulong handle, out nint target)
        {
            uint low = (uint)handle;
            target = 0;
            if (low == 0 || low > handlesMade || handleSlots[low - 1].Object == 0 || handleSlots[low - 1].Generation != (uint)(handle >> 32))
                return false;
            target = handleSlots[low - 1].Object;
            return true;
        }

        [UnmanagedCallersOnly]
        private static long HandleCountNow() => HandleCount;

        [UnmanagedCallersOnly]
        private static void RuntimeEnter() => depth++;

        [UnmanagedCallersOnly]
        private static void RuntimeLeave()
        {
            if (depth <= 0)
                Errors++;
            else
                depth--;
        }

        [UnmanagedCallersOnly]
        private static void ThreadDetach() => depth = 0;

        /// <summary>The static constructors' lock: nothing to do, code here runs one thread at a time.</summary>
        [UnmanagedCallersOnly]
        private static void InitLock() { }

        [UnmanagedCallersOnly]
        private static void InitUnlock() { }

        private static void AddHandleSymbols(Dictionary<string, nint> symbols)
        {
            symbols["larva_handle_new"] = (nint)(delegate* unmanaged<void*, ulong>)&HandleNew;
            symbols["larva_handle_get"] = (nint)(delegate* unmanaged<ulong, void*>)&HandleGet;
            symbols["larva_handle_free"] = (nint)(delegate* unmanaged<ulong, void>)&HandleFree;
            symbols["larva_handle_count"] = (nint)(delegate* unmanaged<long>)&HandleCountNow;
            symbols["larva_runtime_enter"] = (nint)(delegate* unmanaged<void>)&RuntimeEnter;
            symbols["larva_runtime_leave"] = (nint)(delegate* unmanaged<void>)&RuntimeLeave;
            symbols["larva_thread_detach"] = (nint)(delegate* unmanaged<void>)&ThreadDetach;
            symbols["larva_init_lock"] = (nint)(delegate* unmanaged<void>)&InitLock;
            symbols["larva_init_unlock"] = (nint)(delegate* unmanaged<void>)&InitUnlock;
            symbols["larva_runtime_callout"] = (nint)(delegate* unmanaged<void>)&InitLock;
            symbols["larva_runtime_callin"] = (nint)(delegate* unmanaged<void>)&InitUnlock;
        }
    }
}

using Larva.IR;

namespace Larva.Runtime.Vm
{
    /// <summary>
    /// Handles and threads of other languages (Larva.Polyglot), as the native runtimes have them: a handle is a slot's
    /// index + 1 and the slot's generation, the slot a root of the collector; a freed handle used again is a
    /// <see cref="VmException"/>. Entering and leaving only count how deep a thread is (the interpreter runs its threads
    /// itself).
    /// </summary>
    public sealed partial class Interpreter
    {
        private struct HandleSlot
        {
            public Address Object;
            public uint Generation;
        }

        private readonly List<HandleSlot> handleSlots = [];
        private readonly Stack<int> freeHandles = new();
        private readonly Lock handleLock = new();
        [ThreadStatic] private static int enterDepth;

        /// <summary>The lock static constructors run under (re-entrant).</summary>
        private readonly object initLock = new();

        /// <summary>How many handles are held.</summary>
        public long HandleCount { get; private set; }

        /// <summary>The objects handles hold (the collector's roots).</summary>
        private IEnumerable<Address> HandleObjects()
        {
            lock (handleLock)
                return [.. handleSlots.Where(s => !s.Object.IsNull).Select(s => s.Object)];
        }

        private bool RunHandleIntrinsic(Frame frame, int id)
        {
            var stack = frame.Stack;
            switch (id)
            {
                case 97:
                {
                    var target = Heap.AsAddress(Pop(stack));
                    if (target.IsNull)
                    {
                        stack.Add(Value.FromInt(0, IrType.U64));
                        return true;
                    }
                    ulong handle;
                    lock (handleLock)
                    {
                        int index;
                        if (freeHandles.Count > 0)
                            index = freeHandles.Pop();
                        else
                        {
                            index = handleSlots.Count;
                            handleSlots.Add(default);
                        }
                        var slot = handleSlots[index];
                        slot.Object = target;
                        handleSlots[index] = slot;
                        HandleCount++;
                        handle = (ulong)slot.Generation << 32 | (uint)(index + 1);
                    }
                    stack.Add(Value.FromInt((long)handle, IrType.U64));
                    return true;
                }
                case 98:
                {
                    ulong handle = (ulong)PopLong(stack);
                    stack.Add(Value.FromPointer((uint)handle == 0 ? default : Slot(handle).Object, IrType.PtrMut(IrType.U8)));
                    return true;
                }
                case 99:
                {
                    ulong handle = (ulong)PopLong(stack);
                    if ((uint)handle == 0)
                        return true;
                    lock (handleLock)
                    {
                        var slot = Slot(handle);
                        int index = (int)(uint)handle - 1;
                        slot.Object = default;
                        slot.Generation++;
                        handleSlots[index] = slot;
                        freeHandles.Push(index);
                        HandleCount--;
                    }
                    return true;
                }
                case 100:
                    stack.Add(Value.FromInt(HandleCount, IrType.I64));
                    return true;
                case 101:
                    enterDepth++;
                    return true;
                case 102:
                    if (enterDepth <= 0)
                        throw new VmException("runtime.leave without runtime.enter");
                    enterDepth--;
                    return true;
                case 103:
                    enterDepth = 0;
                    return true;
                case 104:
                    Monitor.Enter(initLock);
                    return true;
                case 105:
                    if (Monitor.IsEntered(initLock))
                        Monitor.Exit(initLock);
                    return true;
                case 106 or 107:
                    return true; // a call out: the interpreter's threads are its own
                default:
                    return false;
            }
        }

        private HandleSlot Slot(ulong handle)
        {
            lock (handleLock)
            {
                int index = (int)(uint)handle - 1;
                if (index < 0 || index >= handleSlots.Count || handleSlots[index].Object.IsNull || handleSlots[index].Generation != (uint)(handle >> 32))
                    throw new VmException($"handle {handle:x} is not one the runtime gave, or was freed");
                return handleSlots[index];
            }
        }
    }
}

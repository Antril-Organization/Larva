using Larva.IL;
using Larva.IR;
using Larva.IR.Lowering;

namespace Larva.Runtime.Vm
{
    public sealed partial class Interpreter
    {
        private void GcOperation(Frame frame, Larva.IL.Operation op)
        {
            var stack = frame.Stack;
            long a0 = op.Operands.Length > 0 ? op.Operands[0] : 0;
            switch ((GcOp)op.SubOp)
            {
                case GcOp.New:
                {
                    var type = frame.Resolve(a0);
                    var allocation = Heap.Allocate(AllocationKind.Gc, type);
                    Heap.PlaceInRegion(allocation, regions.Count > 0 ? regions.Peek().Instance : null);
                    allocation.Domain = frame.Function.Domain;
                    allocation.Counted = il.Counts(frame.Function.Domain);
                    Heap.Write(type, new Address(allocation.Id, 0), Pop(stack));
                    allocatedSinceCollect++;
                    stack.Add(Value.FromPointer(new Address(allocation.Id, 0), IrType.Gc(type)));
                    break;
                }
                case GcOp.NewFinalized:
                {
                    var type = frame.Resolve(a0);
                    var finalizer = Pop(stack);
                    var allocation = Heap.Allocate(AllocationKind.Gc, type);
                    allocation.Domain = frame.Function.Domain;
                    allocation.Counted = il.Counts(frame.Function.Domain);
                    Heap.PlaceInRegion(allocation, regions.Count > 0 ? regions.Peek().Instance : null);
                    allocation.Finalizer = finalizer.Kind == ValueKind.Function ? finalizer.Function : FunctionOf(Heap.AsInteger(finalizer));
                    Heap.Write(type, new Address(allocation.Id, 0), Pop(stack));
                    allocatedSinceCollect++;
                    stack.Add(Value.FromPointer(new Address(allocation.Id, 0), IrType.Gc(type)));
                    break;
                }
                case GcOp.Alloc:
                {
                    var type = frame.Resolve(a0);
                    var allocation = Heap.Allocate(AllocationKind.Heap, type);
                    Heap.Write(type, new Address(allocation.Id, 0), Pop(stack));
                    stack.Add(Value.FromPointer(new Address(allocation.Id, 0), IrType.PtrMut(type)));
                    break;
                }
                case GcOp.Free:
                    Heap.Free(Heap.AsAddress(Pop(stack)), "gc.free");
                    break;
                case GcOp.WriteBarrier:
                {
                    var value = Pop(stack);
                    Heap.Write(frame.Resolve(a0), Heap.AsAddress(Pop(stack)), value);
                    Stats.Barriers++;
                    break;
                }
                case GcOp.Pin:
                    Heap.Get(Heap.AsAddress(Pop(stack)), "gc.pin").Pins++;
                    Stats.Pins++;
                    break;
                case GcOp.Unpin:
                {
                    var handle = Heap.AsAddress(Pop(stack));
                    if (handle.IsNull)
                        break; // the pin slot was never reached on this path
                    var allocation = Heap.Get(handle, "gc.unpin");
                    allocation.Pins = Math.Max(0, allocation.Pins - 1);
                    break;
                }
                case GcOp.Retain:
                    Retain(Heap.AsAddress(stack[^1]));
                    break;
                case GcOp.Release:
                    Release(Heap.AsAddress(Pop(stack)));
                    break;
                case GcOp.NewArray:
                {
                    var element = frame.Resolve(a0);
                    var array = Heap.AllocateArray(AllocationKind.Gc, element, Heap.AsInteger(Pop(stack)));
                    Heap.PlaceInRegion(array, regions.Count > 0 ? regions.Peek().Instance : null);
                    array.Domain = frame.Function.Domain;
                    array.Counted = il.Counts(frame.Function.Domain);
                    allocatedSinceCollect++;
                    stack.Add(Value.FromPointer(new Address(array.Id, 0), IrType.Gc(new ArrayType(element))));
                    break;
                }
                case GcOp.NewTrailing:
                {
                    var owner = frame.Resolve(a0) as StructType ?? throw new VmException("newtrail needs a struct");
                    var made = Heap.AllocateTrailing(AllocationKind.Gc, owner, Heap.AsInteger(Pop(stack)));
                    Heap.PlaceInRegion(made, regions.Count > 0 ? regions.Peek().Instance : null);
                    made.Domain = frame.Function.Domain;
                    made.Counted = il.Counts(frame.Function.Domain);
                    allocatedSinceCollect++;
                    stack.Add(Value.FromPointer(new Address(made.Id, 0), IrType.Gc(owner)));
                    break;
                }
                case GcOp.AllocTrailing:
                {
                    var owner = frame.Resolve(a0) as StructType ?? throw new VmException("alloctrail needs a struct");
                    var made = Heap.AllocateTrailing(AllocationKind.Heap, owner, Heap.AsInteger(Pop(stack)));
                    stack.Add(Value.FromPointer(new Address(made.Id, 0), IrType.PtrMut(owner)));
                    break;
                }
                case GcOp.RegionEnter:
                    // The number is the region's id within its function; the activation gets an instance of its own, so
                    // that a callee closing its region 0 never frees what the caller allocated in *its* region 0.
                    regions.Push(new OpenRegion((int)a0, Interlocked.Increment(ref lastRegionInstance)));
                    break;
                case GcOp.RegionExit:
                    if (regions.Count == 0 || regions.Peek().Id != (int)a0)
                        throw new VmException($"gc.rexit {a0} does not close the innermost region");
                    ExitRegion();
                    break;
            }
        }

        /// <summary>An open arena region: its id in the function that opened it, and the instance objects are tagged with.</summary>
        private readonly record struct OpenRegion(int Id, int Instance);

        /// <summary>Process-wide: the interpreters of a program's threads share one heap.</summary>
        private static int lastRegionInstance;

        /// <summary>Closes the innermost region, freeing everything allocated in it (that activation's objects only).</summary>
        private void ExitRegion()
        {
            int region = regions.Pop().Instance;
            var dying = Heap.TakeRegion(region);
            // Finalizers first, while everything in the region is still there for them to read.
            foreach (var allocation in dying)
                Finalize(allocation);
            foreach (var allocation in dying.Where(a => !a.Freed))
                Heap.Free(new Address(allocation.Id, 0), "region exit");
        }

        /// <summary>Runs an object's finalizer, once: it receives the object as an argument (a reference, under counting).</summary>
        private void Finalize(Allocation allocation)
        {
            if (allocation.Finalizer is not int finalizer || allocation.Freed)
                return;
            allocation.Finalizer = null;
            Stats.Finalized++;
            Call(finalizer, [], [Value.FromPointer(new Address(allocation.Id, 0), IrType.Gc(allocation.Type))]);
        }

        private void Retain(Address handle)
        {
            if (handle.IsNull)
                return;
            var allocation = Heap.Get(handle, "gc.retain");
            if (!allocation.Counted)
                return;
            allocation.RefCount++;
            Stats.Retains++;
        }

        private void Release(Address handle)
        {
            if (handle.IsNull)
                return;
            var allocation = Heap.Get(handle, "gc.release");
            if (!allocation.Counted)
                return;
            Stats.Releases++;
            if (--allocation.RefCount == 0)
            {
                if (allocation.Finalizer is not null)
                {
                    // The finalizer's argument is the last reference: its own release frees the object (unless it resurrects it).
                    allocation.RefCount = 1;
                    Finalize(allocation);
                    return;
                }
                Heap.Free(handle, "gc.release");
            }
        }

        /// <summary><c>gen.drop</c>: releases what a value of <paramref name="type"/> owns (boxes; handles under reference counting).</summary>
        private void DropValue(Value value, IrType type)
        {
            value = Heap.Coerce(value, type);
            switch (type)
            {
                case BoxType or OptionalType { Inner: BoxType }:
                    Heap.Free(value.Address, "drop");
                    break;
                case GcType or OptionalType { Inner: GcType } when Has(GcCapabilities.RefCounting):
                    Release(value.Address);
                    break;
                case StructType st:
                    var offsets = Heap.FieldOffsets(st);
                    for (int f = 0; f < offsets.Length; f++)
                    {
                        var fieldType = Heap.FieldType(st, f);
                        DropValue(Heap.DecodeBytes(fieldType, value.Bytes.AsSpan(offsets[f], Heap.LayoutOf(fieldType).Size)), fieldType);
                    }
                    break;
            }
        }

        // ── Tracing collection ─────────────────────────────────────

        /// <summary>A safepoint (call or poll): tracing GCs may collect here, when stressed or after enough allocations.</summary>
        private void Safepoint(Frame frame)
        {
            Stats.Safepoints++;
            TakeTurns();
            // The collector knows this interpreter's call stack only: none runs while other threads are alive.
            if (ManyThreads)
                return;
            if (il.Traces && (options.GcStress || collectRequested || allocatedSinceCollect >= options.CollectEvery))
            {
                collectRequested = false;
                Collect();
            }
        }

        /// <summary>
        /// Mark and sweep. Roots: for every frame, the precise root map recorded for the safepoint it is stopped at (the
        /// conservative frame map if none), plus handles on operand stacks, pinned objects and the contents of live boxes /
        /// raw allocations (owned memory the GC does not manage but must scan).
        /// </summary>
        public void Collect()
        {
            Stats.Collections++;
            allocatedSinceCollect = 0;
            // Only the live gc objects and the memory scanned for handles are visited, never every allocation.
            var objects = Heap.GcObjects;
            foreach (var allocation in objects)
                allocation.Marked = false;

            foreach (var frame in callStack)
            {
                var roots = frame.Function.Safepoints.FirstOrDefault(s => s.Operation == frame.Pc).Roots ?? frame.Function.GcRoots;
                foreach (var root in roots)
                {
                    var type = root.IsArgument ? frame.ArgumentTypes[root.Slot] : frame.LocalTypes[root.Slot];
                    var offsets = root.Offsets ?? Heap.LayoutOf(type).GcOffsets;
                    foreach (int offset in offsets)
                        Mark(Address.Unpack(frame.SlotWord(root.IsArgument, root.Slot, offset)));
                }
                foreach (var value in frame.Stack)
                    MarkValue(value);
                if (frame.Exception is { } exception)
                    MarkValue(exception);
            }

            foreach (var held in HandleObjects())
                Mark(held);
            foreach (var allocation in objects)
            {
                if (allocation.Pins > 0 && !allocation.Marked)
                {
                    allocation.Marked = true;
                    markWork.Push(allocation);
                }
            }
            Drain();
            foreach (var allocation in Heap.Scanned)
            {
                if (allocation.Kind is AllocationKind.Heap || allocation.Kind == AllocationKind.Static && !allocation.Sealed)
                    ScanContents(allocation); // heap cells and globals (invoke buffers are untyped bytes)
            }
            Drain();

            // Unreachable objects with a finalizer survive this collection (with what they reach): their finalizers run
            // after the sweep, and a later collection frees them once they are unreachable again.
            var finalizing = objects.Where(a => !a.Marked && a.Region is null && a.Finalizer is not null).OrderBy(a => a.Id).ToList();
            foreach (var allocation in finalizing)
                Mark(new Address(allocation.Id, 0));

            var dead = objects.Where(a => !a.Marked && a.Region is null).ToList();
            foreach (var allocation in dead)
            {
                Heap.Free(new Address(allocation.Id, 0), "collect");
                Stats.Reclaimed++;
            }

            foreach (var allocation in finalizing)
                Finalize(allocation);
        }

        private void MarkValue(Value value)
        {
            switch (value)
            {
                case { Kind: ValueKind.Pointer, Type: GcType or OptionalType { Inner: GcType } }:
                    Mark(value.Address);
                    break;
                case { Kind: ValueKind.Struct, Type: { } type }:
                    foreach (int offset in Heap.LayoutOf(type).GcOffsets)
                        Mark(Address.Unpack(BitConverter.ToUInt64(value.Bytes!, offset)));
                    break;
            }
        }

        /// <summary>Objects marked but not yet scanned: marking is iterative (a long linked list must not recurse per node).</summary>
        private readonly Stack<Allocation> markWork = new();

        private void Mark(Address address)
        {
            Push(address);
            Drain();
        }

        /// <summary>Marks the object (if it is a live gc object not yet marked) and queues its contents for scanning.</summary>
        private void Push(Address address)
        {
            if (address.IsNull)
                return;
            var allocation = Heap.Get(address, "mark");
            if (allocation.Kind != AllocationKind.Gc || allocation.Marked)
                return;
            allocation.Marked = true;
            markWork.Push(allocation);
        }

        private void Drain()
        {
            while (markWork.Count > 0)
                ScanContents(markWork.Pop());
        }

        /// <summary>Where an allocation of <paramref name="type"/> holds handles: offsets, and for an array its header and stride (0 otherwise).</summary>
        private (int[] Offsets, int Header, int Stride) ScanShape(IrType type)
        {
            if (type is ArrayType array)
            {
                var (header, stride) = Heap.ArrayLayout(array.Element);
                return (Heap.LayoutOf(array.Element).GcOffsets, header, stride);
            }
            return (Heap.LayoutOf(type).GcOffsets, 0, 0);
        }

        /// <summary>
        /// Queues every handle stored in <paramref name="allocation"/> (an object, array, heap cell or global); the caller
        /// drains the queue, so marking never recurses (a long chain or a large array of objects uses the work stack).
        /// </summary>
        private void ScanContents(Allocation allocation)
        {
            if (allocation.Kind is AllocationKind.Buffer or AllocationKind.Native)
                return; // untyped bytes, or memory outside the interpreter (no handles in it)
            var (offsets, header, stride) = allocation.Scan ??= ScanShape(allocation.Type);
            if (offsets.Length == 0)
                return;
            var data = allocation.Data;
            if (stride == 0)
            {
                foreach (int offset in offsets)
                    Push(Address.Unpack(BitConverter.ToUInt64(data, offset)));
                return;
            }
            ulong length = BitConverter.ToUInt64(data, 0);
            for (ulong i = 0; i < length; i++)
            {
                int element = header + (int)i * stride;
                foreach (int offset in offsets)
                    Push(Address.Unpack(BitConverter.ToUInt64(data, element + offset)));
            }
        }
    }
}

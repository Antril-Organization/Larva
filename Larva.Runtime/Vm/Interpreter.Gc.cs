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
                    allocation.Region = regions.Count > 0 ? regions.Peek() : null;
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
                    allocation.Region = regions.Count > 0 ? regions.Peek() : null;
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
                    array.Region = regions.Count > 0 ? regions.Peek() : null;
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
                    made.Region = regions.Count > 0 ? regions.Peek() : null;
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
                    regions.Push((int)a0);
                    break;
                case GcOp.RegionExit:
                    if (regions.Count == 0 || regions.Peek() != (int)a0)
                        throw new VmException($"gc.rexit {a0} does not close the innermost region");
                    ExitRegion((int)a0);
                    break;
            }
        }

        /// <summary>Closes the innermost region, freeing everything allocated in it.</summary>
        private void ExitRegion(int region)
        {
            regions.Pop();
            var dying = Heap.Live.Where(x => x.Kind == AllocationKind.Gc && x.Region == region).ToList();
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
            foreach (var allocation in Heap.Live)
                allocation.Marked = false;

            foreach (var frame in callStack)
            {
                var roots = frame.Function.Safepoints.FirstOrDefault(s => s.Operation == frame.Pc).Roots ?? frame.Function.GcRoots;
                foreach (var root in roots)
                {
                    var slot = root.IsArgument ? frame.Arguments[root.Slot] : frame.Locals[root.Slot];
                    var type = root.IsArgument ? frame.ArgumentTypes[root.Slot] : frame.LocalTypes[root.Slot];
                    var offsets = root.Offsets ?? Heap.LayoutOf(type).GcOffsets;
                    foreach (int offset in offsets)
                        Mark(Heap.AsAddress(Heap.Read(IrType.U64, slot.Plus(offset))));
                }
                foreach (var value in frame.Stack)
                    MarkValue(value);
                if (frame.Exception is { } exception)
                    MarkValue(exception);
            }

            foreach (var allocation in Heap.Live.ToList())
            {
                if (allocation.Kind == AllocationKind.Gc && allocation.Pins > 0)
                    Mark(new Address(allocation.Id, 0));
                else if (allocation.Kind is AllocationKind.Heap or AllocationKind.Buffer || allocation.Kind == AllocationKind.Static && !allocation.Sealed)
                    ScanContents(allocation); // heap cells and globals
            }

            // Unreachable objects with a finalizer survive this collection (with what they reach): their finalizers run
            // after the sweep, and a later collection frees them once they are unreachable again.
            var finalizing = Heap.Live.Where(a => a.Kind == AllocationKind.Gc && !a.Marked && a.Region is null && a.Finalizer is not null).ToList();
            foreach (var allocation in finalizing)
                Mark(new Address(allocation.Id, 0));

            foreach (var allocation in Heap.Live.Where(a => a.Kind == AllocationKind.Gc && !a.Marked && a.Region is null).ToList())
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

        private void Mark(Address address)
        {
            if (address.IsNull)
                return;
            var allocation = Heap.Get(address, "mark");
            if (allocation.Kind != AllocationKind.Gc || allocation.Marked)
                return;
            allocation.Marked = true;
            ScanContents(allocation);
        }

        private void ScanContents(Allocation allocation)
        {
            if (allocation.Kind is AllocationKind.Buffer or AllocationKind.Native)
                return; // untyped bytes, or memory outside the interpreter (no handles in it)
            if (allocation.Type is ArrayType array)
            {
                var offsets = Heap.LayoutOf(array.Element).GcOffsets;
                if (offsets.Length == 0)
                    return;
                var (header, stride) = Heap.ArrayLayout(array.Element);
                ulong length = BitConverter.ToUInt64(allocation.Data, 0);
                for (ulong i = 0; i < length; i++)
                {
                    foreach (int offset in offsets)
                        Mark(Address.Unpack(BitConverter.ToUInt64(allocation.Data, header + (int)i * stride + offset)));
                }
                return;
            }
            foreach (int offset in Heap.LayoutOf(allocation.Type).GcOffsets)
                Mark(Address.Unpack(BitConverter.ToUInt64(allocation.Data, offset)));
        }
    }
}

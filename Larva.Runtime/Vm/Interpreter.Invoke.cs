using Larva.IR;
using Larva.IR.Reflection;

namespace Larva.Runtime.Vm
{
    public sealed partial class Interpreter
    {
        /// <summary>A call from the host: an exception nobody caught surfaces as <see cref="UnhandledException"/>.</summary>
        private Value CallHost(int function, IrType[] typeArguments, Value[] arguments)
        {
            try
            {
                return Call(function, typeArguments, arguments);
            }
            catch (LarvaException e)
            {
                throw new UnhandledException(e.Value);
            }
        }

        /// <summary>
        /// Runs a reflective call: materializes the plan's slots and buffers in memory, calls the invoke stub with the target
        /// function, and decodes the result according to <paramref name="method"/>'s return type. A by-pointer result is also
        /// copied back into the plan's result buffer.
        /// </summary>
        public object? Invoke(MethodInfo method, InvokePlan plan)
        {
            int open = plan.Shape.IndexOf('('), close = plan.Shape.IndexOf(')');
            string classes = plan.Shape[..open] + plan.Shape[(open + 1)..close];
            char resultClass = plan.Shape[^1];

            var buffers = plan.Buffers.Select(bytes =>
            {
                var buffer = Heap.Allocate(AllocationKind.Buffer, IrType.U8, bytes.Length);
                bytes.CopyTo(buffer.Data, 0);
                return buffer;
            }).ToArray();

            var slots = Heap.Allocate(AllocationKind.Buffer, IrType.U64, 8 * Math.Max(1, plan.Slots.Length));
            for (int s = 0; s < plan.Slots.Length; s++)
            {
                ulong raw = classes[s] == 'x' ? new Address(buffers[plan.Slots[s]].Id, 0).Packed : plan.Slots[s];
                BitConverter.TryWriteBytes(slots.Data.AsSpan(8 * s), raw);
            }

            var result = Heap.Allocate(AllocationKind.Buffer, IrType.U64, 8);
            if (plan.ResultBuffer is int r)
                BitConverter.TryWriteBytes(result.Data, new Address(buffers[r].Id, 0).Packed);

            try
            {
                var slotPointer = IrType.PtrMut(IrType.U64);
                CallHost(plan.Stub, [],
                [
                    Value.FromFunction(plan.Function),
                    Value.FromPointer(new Address(slots.Id, 0), slotPointer),
                    Value.FromPointer(new Address(result.Id, 0), slotPointer),
                ]);

                switch (resultClass)
                {
                    case 'v':
                        return null;
                    case 'x':
                        var bytes = buffers[plan.ResultBuffer!.Value].Data;
                        bytes.CopyTo(plan.Buffers[plan.ResultBuffer.Value], 0);
                        return ToObject(Heap.DecodeBytes(method.Return, bytes));
                    default:
                        var raw = BitConverter.ToUInt64(result.Data);
                        return resultClass == 'i'
                            ? ToObject(Heap.Coerce(Value.FromInt(raw, IrType.U64), method.Return))
                            : ToObject(Value.FromFloatBits(raw, method.Return));
                }
            }
            finally
            {
                foreach (var buffer in buffers.Append(slots).Append(result))
                    Heap.Free(new Address(buffer.Id, 0), "invoke cleanup");
            }
        }
    }
}

using Larva.Native;
using LLVMSharp.Interop;

namespace Larva.Llvm
{

    /// <summary>
    /// Precise stack roots for tracing GC modes.
    /// <para>
    /// Code generation keeps every local whose type can hold a <c>gc</c> handle in its own stack slot, marked with a call to
    /// <see cref="Marker"/>. The marker makes the slot escape, so optimization (inlining included) cannot promote it to a
    /// register or reuse its value across a call: at any call, the handles of the frame are in those slots. After
    /// optimization, <see cref="Lower"/> removes the markers and gives each function one <c>llvm.experimental.stackmap</c>
    /// listing its slots and their sizes. Slots have fixed frame offsets, so that single record describes every safepoint of
    /// the function; LLVM writes the offsets to the <c>.llvm_stackmaps</c> section, which <see cref="StackMapFormat.Parse"/> reads back.
    /// </para>
    /// </summary>
    internal static unsafe class StackMaps
    {
        public const string Marker = "larva.gc.root";

        /// <summary>
        /// WebAssembly has no stack maps: the marked slots stay in linear memory (LLVM's shadow stack), where the collector
        /// scans for roots. The marker becomes an empty function that is never inlined, so its calls still make the slots
        /// escape when the code is generated.
        /// </summary>
        public static void KeepInMemory(LLVMModuleRef module)
        {
            var marker = module.GetNamedFunction(Marker);
            if (marker.Handle == IntPtr.Zero || marker.BasicBlocksCount > 0)
                return;
            using var builder = LLVMBuilderRef.Create(module.Context);
            builder.PositionAtEnd(marker.AppendBasicBlock("entry"));
            builder.BuildRetVoid();
            marker.Linkage = LLVMLinkage.LLVMInternalLinkage;
            var name = "noinline"u8.ToArray();
            fixed (byte* text = name)
            {
                uint kind = LLVM.GetEnumAttributeKindForName((sbyte*)text, (nuint)name.Length);
                LLVM.AddAttributeAtIndex(marker, LLVMAttributeIndex.LLVMAttributeFunctionIndex, LLVM.CreateEnumAttribute(module.Context, kind, 0));
            }
        }

        /// <summary>
        /// Before optimization (WebAssembly): tells LLVM that root slots and the heap never overlap. A marked slot has
        /// escaped, so without this every store to it may change any object or global, and a loop that keeps a handle in
        /// one reloads everything it reads on each pass (nothing is hoisted or vectorized). It holds because a slot's
        /// address goes only to the marker (an empty function): a slot used any other way (passed to a call, cast to an
        /// integer, copied) is not tagged. Accesses through a slot get type "larva.root"; those through a pointer loaded
        /// from memory, a global, an argument or a call's result get "larva.data"; the rest stay untagged (may alias all).
        /// </summary>
        public static void SeparateRootsFromHeap(LLVMModuleRef module)
        {
            var marker = module.GetNamedFunction(Marker);
            if (marker.Handle == IntPtr.Zero)
                return;
            var context = module.Context;
            LLVMOpaqueMetadata* Text(string text)
            {
                var bytes = System.Text.Encoding.ASCII.GetBytes(text);
                fixed (byte* b = bytes)
                    return LLVM.MDStringInContext2(context, (sbyte*)b, (nuint)bytes.Length);
            }
            LLVMOpaqueMetadata* Node(params LLVMOpaqueMetadata*[] items)
            {
                fixed (LLVMOpaqueMetadata** p = items)
                    return LLVM.MDNodeInContext2(context, p, (nuint)items.Length);
            }
            var zero = LLVM.ValueAsMetadata(LLVMValueRef.CreateConstInt(context.Int64Type, 0));
            var top = Node(Text("larva tbaa"));
            var rootType = Node(Text("larva.root"), top, zero);
            var dataType = Node(Text("larva.data"), top, zero);
            var rootTag = LLVM.MetadataAsValue(context, Node(rootType, rootType, zero));
            var dataTag = LLVM.MetadataAsValue(context, Node(dataType, dataType, zero));
            uint kind;
            var name = "tbaa"u8.ToArray();
            fixed (byte* n = name)
                kind = LLVM.GetMDKindIDInContext(context, (sbyte*)n, (uint)name.Length);

            // Whether every use of `pointer` reads or writes through it (or is the marker, or a GEP used the same way).
            bool OnlyAccessed(LLVMValueRef pointer, bool allowMarker)
            {
                for (var use = LLVM.GetFirstUse(pointer); use != null; use = LLVM.GetNextUse(use))
                {
                    LLVMValueRef user = LLVM.GetUser(use);
                    if (user.IsALoadInst.Handle != IntPtr.Zero)
                        continue;
                    if (user.IsAStoreInst.Handle != IntPtr.Zero && user.GetOperand(0) != pointer)
                        continue;
                    if (allowMarker && user.IsACallInst.Handle != IntPtr.Zero && LLVM.GetCalledValue(user) == marker)
                        continue;
                    if (user.IsAGetElementPtrInst.Handle != IntPtr.Zero && user.GetOperand(0) == pointer && OnlyAccessed(user, false))
                        continue;
                    return false;
                }
                return true;
            }

            var roots = new HashSet<LLVMValueRef>();
            for (var function = module.FirstFunction; function.Handle != IntPtr.Zero; function = function.NextFunction)
            {
                if (function.BasicBlocksCount == 0)
                    continue;
                roots.Clear();
                foreach (var block in function.GetBasicBlocks())
                    for (var instruction = block.FirstInstruction; instruction.Handle != IntPtr.Zero; instruction = instruction.NextInstruction)
                        if (instruction.IsACallInst.Handle != IntPtr.Zero && LLVM.GetCalledValue(instruction) == marker)
                        {
                            var slot = instruction.GetOperand(0);
                            if (slot.IsAAllocaInst.Handle != IntPtr.Zero && OnlyAccessed(slot, true))
                                roots.Add(slot);
                        }
                if (roots.Count == 0)
                    continue;
                foreach (var block in function.GetBasicBlocks())
                    for (var instruction = block.FirstInstruction; instruction.Handle != IntPtr.Zero; instruction = instruction.NextInstruction)
                    {
                        LLVMValueRef pointer;
                        if (instruction.IsALoadInst.Handle != IntPtr.Zero)
                            pointer = instruction.GetOperand(0);
                        else if (instruction.IsAStoreInst.Handle != IntPtr.Zero)
                            pointer = instruction.GetOperand(1);
                        else
                            continue;
                        var source = Base(pointer);
                        if (source.IsAAllocaInst.Handle != IntPtr.Zero)
                        {
                            if (roots.Contains(source))
                                LLVM.SetMetadata(instruction, kind, rootTag);
                        }
                        else if (source.IsALoadInst.Handle != IntPtr.Zero || source.IsAGlobalVariable.Handle != IntPtr.Zero
                            || source.IsAArgument.Handle != IntPtr.Zero || source.IsACallInst.Handle != IntPtr.Zero)
                        {
                            LLVM.SetMetadata(instruction, kind, dataTag);
                        }
                    }
            }

            static LLVMValueRef Base(LLVMValueRef pointer)
            {
                for (int steps = 0; steps < 64; steps++)
                {
                    if (pointer.IsAGetElementPtrInst.Handle != IntPtr.Zero || pointer.IsABitCastInst.Handle != IntPtr.Zero)
                        pointer = pointer.GetOperand(0);
                    else if (pointer.IsAConstantExpr.Handle != IntPtr.Zero && pointer.ConstOpcode is LLVMOpcode.LLVMGetElementPtr or LLVMOpcode.LLVMBitCast)
                        pointer = pointer.GetOperand(0);
                    else
                        break;
                }
                return pointer;
            }
        }

        /// <summary>Replaces the root markers with one stack map per function. Returns the number of functions with roots.</summary>
        public static int Lower(LLVMModuleRef module)
        {
            var marker = module.GetNamedFunction(Marker);
            if (marker.Handle == IntPtr.Zero)
                return 0;

            // The module's own context (code may be generated on several threads, each in its context).
            var context = module.Context;
            var i64 = context.Int64Type;
            var i32 = context.Int32Type;
            var stackmapType = LLVMTypeRef.CreateFunction(context.VoidType, [i64, i32], true);
            var stackmap = module.GetNamedFunction("llvm.experimental.stackmap");
            if (stackmap.Handle == IntPtr.Zero)
                stackmap = module.AddFunction("llvm.experimental.stackmap", stackmapType);
            var layout = LLVM.GetModuleDataLayout(module);
            using var builder = LLVMBuilderRef.Create(module.Context);
            var nothingType = LLVMTypeRef.CreateFunction(context.VoidType, [], false);
            LLVMValueRef Nothing()
            {
                var existing = module.GetNamedFunction("llvm.donothing");
                return existing.Handle != IntPtr.Zero ? existing : module.AddFunction("llvm.donothing", nothingType);
            }

            int count = 0;
            for (var function = module.FirstFunction; function.Handle != IntPtr.Zero; function = function.NextFunction)
            {
                if (function.BasicBlocksCount == 0)
                    continue;
                var roots = new List<LLVMValueRef>();
                var markers = new List<LLVMValueRef>();
                foreach (var block in function.GetBasicBlocks())
                    for (var instruction = block.FirstInstruction; instruction.Handle != IntPtr.Zero; instruction = instruction.NextInstruction)
                        if (instruction.InstructionOpcode is LLVMOpcode.LLVMCall or LLVMOpcode.LLVMInvoke && LLVM.GetCalledValue(instruction) == marker)
                        {
                            markers.Add(instruction);
                            var slot = instruction.GetOperand(0);
                            if (slot.IsAAllocaInst.Handle != IntPtr.Zero && !roots.Contains(slot))
                                roots.Add(slot);
                        }
                foreach (var call in markers)
                {
                    // Inlined inside a try region the marker became an invoke. A jump in its place would leave the unwind
                    // block's phis naming an edge that no longer exists, so it becomes an invoke of `llvm.donothing`
                    // (no code, same edges).
                    if (call.InstructionOpcode == LLVMOpcode.LLVMInvoke)
                    {
                        builder.PositionBefore(call);
                        builder.BuildInvoke2(nothingType, Nothing(), Array.Empty<LLVMValueRef>(), LLVM.GetNormalDest(call), LLVM.GetUnwindDest(call), "");
                    }
                    call.InstructionEraseFromParent();
                }
                if (roots.Count == 0)
                    continue;

                var entry = function.EntryBasicBlock;
                var leading = new HashSet<LLVMValueRef>();
                for (var i = entry.FirstInstruction; i.Handle != IntPtr.Zero && i.IsAAllocaInst.Handle != IntPtr.Zero; i = i.NextInstruction)
                    leading.Add(i);
                var arguments = new List<LLVMValueRef> { LLVMValueRef.CreateConstInt(i64, (ulong)count), LLVMValueRef.CreateConstInt(i32, 0) };
                foreach (var slot in roots)
                {
                    // The stack map goes right after the allocas that open the entry block, so every slot must be one of
                    // them (inlined slots, or unoptimized code that stores before its last alloca, are moved up).
                    if (!leading.Contains(slot))
                    {
                        LLVM.InstructionRemoveFromParent(slot);
                        builder.PositionBefore(entry.FirstInstruction);
                        builder.Insert(slot);
                    }
                    if (LLVM.GetAlignment(slot) < 8)
                        LLVM.SetAlignment(slot, 8);
                    arguments.Add(slot);
                    arguments.Add(LLVMValueRef.CreateConstInt(i64, LLVM.ABISizeOfType(layout, LLVM.GetAllocatedType(slot))));
                }
                // Right after the slots: nothing before it can make it unreachable (code after a noreturn call is dropped).
                var first = entry.FirstInstruction;
                while (first.IsAAllocaInst.Handle != IntPtr.Zero)
                    first = first.NextInstruction;
                builder.PositionBefore(first);
                builder.BuildCall2(stackmapType, stackmap, arguments.ToArray(), "");
                count++;
            }
            marker.DeleteFunction();
            return count;
        }
    }
}

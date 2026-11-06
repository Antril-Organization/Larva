using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LLVMSharp.Interop;
using Larva.Native;

namespace Larva.Llvm
{
    /// <summary>
    /// Memory manager for MCJIT on Windows x64. All sections of a module are placed in one contiguous executable block, so
    /// RuntimeDyld's image base (the lowest section address) is the block start; on finalization the module's <c>.pdata</c>
    /// is registered with <c>RtlAddFunctionTable</c>. Without that, the OS cannot unwind through JIT frames and any SEH
    /// exception (Larva exceptions included) crashes the process. The module's <c>.llvm_stackmaps</c> section, if any, is
    /// handed to <see cref="NativeRuntime"/> so the collector can find the roots of JIT frames.
    /// <para>
    /// Note: Windows' CET shadow stack rejects exception continuations into JIT code that are not registered as EH
    /// continuation targets; hosts that run JIT code with exceptions must opt out (<c>CETCompat=false</c>).
    /// </para>
    /// </summary>
    internal sealed unsafe class JitMemory
    {
        private const nuint Size = 64 << 20;
        private const uint MemCommit = 0x1000, MemReserve = 0x2000, MemRelease = 0x8000, PageExecuteReadWrite = 0x40;

        private readonly byte* region;
        private nuint used;
        private byte* pdata;
        private nuint pdataSize;
        private bool registered;
        private byte* stackMaps;
        private nuint stackMapsSize;
        private int codeId = -1;
        private GCHandle self;

        private JitMemory()
        {
            region = (byte*)VirtualAlloc(null, Size, MemCommit | MemReserve, PageExecuteReadWrite);
            if (region == null)
                throw new LlvmBackendException("cannot reserve memory for JIT code");
        }

        /// <summary>A memory manager for one execution engine; the engine owns it and destroys it on dispose.</summary>
        public static LLVMOpaqueMCJITMemoryManager* Create()
        {
            var memory = new JitMemory();
            memory.self = GCHandle.Alloc(memory);
            return LLVM.CreateSimpleMCJITMemoryManager((void*)GCHandle.ToIntPtr(memory.self), &AllocateCode, &AllocateData, &Finalize, &Destroy);
        }

        private static JitMemory From(void* opaque) => (JitMemory)GCHandle.FromIntPtr((nint)opaque).Target!;

        private byte* Bump(nuint size, uint alignment)
        {
            nuint align = Math.Max(alignment, 16u);
            nuint start = (used + align - 1) / align * align;
            if (start + size > Size)
                return null;
            used = start + Math.Max(size, 1);
            return region + start;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static byte* AllocateCode(void* opaque, nuint size, uint alignment, uint id, sbyte* name) => From(opaque).Bump(size, alignment);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static byte* AllocateData(void* opaque, nuint size, uint alignment, uint id, sbyte* name, int readOnly)
        {
            var memory = From(opaque);
            var block = memory.Bump(size, alignment);
            if (name != null && new string(name) == ".pdata")
            {
                memory.pdata = block;
                memory.pdataSize = size;
            }
            else if (name != null && new string(name) == StackMapFormat.Section)
            {
                memory.stackMaps = block;
                memory.stackMapsSize = size;
            }
            return block;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int Finalize(void* opaque, sbyte** error)
        {
            var memory = From(opaque);
            if (memory.pdata != null && !memory.registered)
            {
                // RuntimeDyld reserves stub space after a section's contents: register only the real entries.
                uint count = 0;
                while (count < memory.pdataSize / 12 && ((uint*)(memory.pdata + count * 12))[1] != 0)
                    count++;
                memory.registered = RtlAddFunctionTable(memory.pdata, count, (ulong)memory.region) != 0;
            }
            // Relocations are applied by now: the stack maps hold the final function addresses.
            if (memory.codeId < 0)
                memory.codeId = NativeRuntime.RegisterCode((nint)memory.region, (nint)(memory.region + Size), StackMapFormat.Parse(memory.stackMaps, memory.stackMapsSize));
            FlushInstructionCache(GetCurrentProcess(), memory.region, memory.used);
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Destroy(void* opaque)
        {
            var memory = From(opaque);
            if (memory.registered)
                RtlDeleteFunctionTable(memory.pdata);
            if (memory.codeId >= 0)
                NativeRuntime.UnregisterCode(memory.codeId);
            VirtualFree(memory.region, 0, MemRelease);
            memory.self.Free();
        }

        [DllImport("kernel32")] private static extern void* VirtualAlloc(void* address, nuint size, uint type, uint protect);
        [DllImport("kernel32")] private static extern int VirtualFree(void* address, nuint size, uint type);
        [DllImport("kernel32")] private static extern byte RtlAddFunctionTable(void* table, uint count, ulong baseAddress);
        [DllImport("kernel32")] private static extern byte RtlDeleteFunctionTable(void* table);
        [DllImport("kernel32")] private static extern int FlushInstructionCache(nint process, void* address, nuint size);
        [DllImport("kernel32")] private static extern nint GetCurrentProcess();
    }
}

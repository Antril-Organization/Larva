using System.Runtime.InteropServices;
using Larva.Native;

namespace Larva.X64
{
    /// <summary>
    /// A program loaded into this process, the way a linker and loader would: code, a jump stub per runtime function (so
    /// calls reach it with a 32-bit displacement), resolved relocations, the function table registered with
    /// <c>RtlAddFunctionTable</c> and the stack roots registered with <see cref="NativeRuntime"/>.
    /// </summary>
    public sealed unsafe class X64Image : IDisposable
    {
        private const uint MemCommit = 0x1000, MemReserve = 0x2000, MemRelease = 0x8000, PageExecuteReadWrite = 0x40;

        private readonly byte* image;
        private readonly byte* functionTable;
        private readonly Dictionary<string, int> offsets = [];
        private readonly int codeId;
        private bool disposed;

        internal X64Image(X64Program program)
        {
            var externals = program.Externals;
            int stubs = Align(program.Code.Length, 16);
            int xdata = Align(stubs + 16 * externals.Count, 4);
            int xdataSize = program.Functions.Sum(f => f.UnwindInfo.Length);
            int pdata = Align(xdata + xdataSize, 4);
            int data = Align(pdata + 12 * program.Functions.Count, 16);
            var dataOffsets = new int[program.Data.Count];
            int size = data;
            for (int d = 0; d < program.Data.Count; d++)
            {
                dataOffsets[d] = size;
                size = Align(size + program.Data[d].Bytes.Length, 16);
            }

            image = (byte*)VirtualAlloc(null, (nuint)size, MemCommit | MemReserve, PageExecuteReadWrite);
            if (image == null)
                throw new X64BackendException("cannot allocate executable memory");
            program.Code.CopyTo(new Span<byte>(image, program.Code.Length));
            foreach (var function in program.Functions)
                offsets[function.Name] = function.Offset;
            for (int d = 0; d < program.Data.Count; d++)
            {
                program.Data[d].Bytes.CopyTo(new Span<byte>(image + dataOffsets[d], program.Data[d].Bytes.Length));
                offsets[program.Data[d].Name] = dataOffsets[d];
            }

            // Stubs: jmp [rip + 0]; .quad target
            for (int e = 0; e < externals.Count; e++)
            {
                if (!NativeRuntime.Symbols.TryGetValue(externals[e], out var target) && !SystemSymbol(externals[e], out target))
                {
                    VirtualFree(image, 0, MemRelease);
                    throw new X64BackendException($"unresolved symbol `{externals[e]}`");
                }
                byte* stub = image + stubs + 16 * e;
                stub[0] = 0xFF; stub[1] = 0x25; *(int*)(stub + 2) = 0;
                if (externals[e].StartsWith("__imp_", StringComparison.Ordinal))
                {
                    // A pointer code loads (the address of runtime data, as an import would give it): kept in the stub's
                    // address field, which is within reach of a rip-relative load.
                    *(long*)(stub + 6) = *(long*)target;
                    offsets[externals[e]] = stubs + 16 * e + 6;
                    continue;
                }
                *(long*)(stub + 6) = target;
                offsets[externals[e]] = stubs + 16 * e;
            }
            foreach (var relocation in program.Relocations)
                *(int*)(image + relocation.Offset) = offsets[relocation.Symbol] - (relocation.Offset + 4);

            // Unwind info and function table (RVAs relative to the image start).
            int unwind = xdata;
            functionTable = image + pdata;
            for (int f = 0; f < program.Functions.Count; f++)
            {
                var function = program.Functions[f];
                function.UnwindInfo.CopyTo(new Span<byte>(image + unwind, function.UnwindInfo.Length));
                foreach (var fixup in function.UnwindFixups)
                    *(uint*)(image + unwind + fixup.At) = (uint)(fixup.Symbol is null ? fixup.CodeOffset : offsets[fixup.Symbol]);
                var entry = (uint*)(functionTable + 12 * f);
                entry[0] = (uint)function.Offset;
                entry[1] = (uint)(function.Offset + function.Size);
                entry[2] = (uint)unwind;
                unwind += function.UnwindInfo.Length;
            }
            if (program.Functions.Count > 0 && RtlAddFunctionTable(functionTable, (uint)program.Functions.Count, (ulong)image) == 0)
                throw new X64BackendException("RtlAddFunctionTable failed");

            var roots = program.Functions.Where(f => f.Roots.Length > 0).ToDictionary(f => (nint)(image + f.Offset), f => f.Roots);
            codeId = NativeRuntime.RegisterCode((nint)image, (nint)(image + size), roots);
            FlushInstructionCache(GetCurrentProcess(), image, (nuint)size);
        }

        /// <summary>What a linker would take from kernel32.lib / the CRT: SEH support for Larva exceptions.</summary>
        private static bool SystemSymbol(string name, out nint address)
        {
            string? library = name switch
            {
                "RaiseException" => "kernel32.dll",
                "__C_specific_handler" => "ntdll.dll",
                _ => null,
            };
            address = library is null ? 0 : NativeLibrary.GetExport(NativeLibrary.Load(library), name);
            return library is not null;
        }

        private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

        /// <summary>Entry point of a function, to call through a function pointer.</summary>
        public nint Address(string name)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return offsets.TryGetValue(name, out int offset) ? (nint)(image + offset) : throw new KeyNotFoundException($"no function `{name}`");
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            RtlDeleteFunctionTable(functionTable);
            NativeRuntime.UnregisterCode(codeId);
            VirtualFree(image, 0, MemRelease);
        }

        [DllImport("kernel32")] private static extern void* VirtualAlloc(void* address, nuint size, uint type, uint protect);
        [DllImport("kernel32")] private static extern int VirtualFree(void* address, nuint size, uint type);
        [DllImport("kernel32")] private static extern byte RtlAddFunctionTable(void* table, uint count, ulong baseAddress);
        [DllImport("kernel32")] private static extern byte RtlDeleteFunctionTable(void* table);
        [DllImport("kernel32")] private static extern int FlushInstructionCache(nint process, void* address, nuint size);
        [DllImport("kernel32")] private static extern nint GetCurrentProcess();
    }
}

using System.Numerics;
﻿using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Larva.Native
{
    /// <summary>
    /// The runtime that natively compiled Larva code calls into (C ABI, <c>larva_*</c> symbols). This implementation lives
    /// in the host process and backs JIT execution; AOT executables link an equivalent C runtime instead.
    /// <para>
    /// Heap cells (<c>box</c>, <c>alloc</c>) are plain aligned blocks. <c>gc</c> objects carry a 16-byte header before
    /// the payload — reference count and arena region — so the same entry points serve every GC mode. Tracing modes are
    /// collected at <c>larva_poll</c> safepoints by a non-moving mark-sweep (see <c>NativeRuntime.Gc.cs</c>).
    /// </para>
    /// Statistics are per process and reset with <see cref="Reset"/>; misuse (double free, freeing an unknown pointer) is
    /// counted instead of thrown, because exceptions cannot cross native frames.
    /// </summary>
    public static unsafe partial class NativeRuntime
    {
        private const int GcHeader = 16;

        private static readonly Dictionary<nint, AllocationKind> live = [];
        private static readonly Dictionary<nint, long> sizes = [];
        private static readonly Dictionary<nint, int> regionOf = [];
        private static readonly Stack<int> regions = new();
        /// <summary>Objects with a finalizer that has not run yet → the function (<c>fn(gc T)</c>).</summary>
        private static readonly Dictionary<nint, nint> finalizers = [];
        /// <summary>Finalizers running: collections wait (the stack walk stops at the first host frame).</summary>
        private static int finalizing;

        public enum AllocationKind : byte { Heap, Gc }

        public static int Allocations { get; private set; }
        public static int Frees { get; private set; }
        public static int Errors { get; private set; }
        public static int Retains { get; private set; }
        public static int Releases { get; private set; }
        public static int Barriers { get; private set; }
        public static int Pins { get; private set; }
        public static int Polls { get; private set; }
        public static int Finalized { get; private set; }
        public static string? LastPanic { get; private set; }

        public static int LiveHeap => live.Count(kv => kv.Value == AllocationKind.Heap);
        public static int LiveGc => live.Count(kv => kv.Value == AllocationKind.Gc);

        public static void Reset()
        {
            foreach (var (pointer, kind) in live)
                if (!customBlocks.Contains(pointer))
                    NativeMemory.AlignedFree((void*)(kind == AllocationKind.Gc ? pointer - GcHeader : pointer));
            live.Clear();
            customBlocks.Clear();
            customPending.Clear();
            customAllocate = null;
            customRelease = null;
            customPoll = null;
            inCustom = collecting = 0;
            customAsked = -1;
            streams.CloseAll();
            sizes.Clear();
            pinned.Clear();
            regionOf.Clear();
            regions.Clear();
            finalizers.Clear();
            finalizing = 0;
            Finalized = 0;
            Allocations = Frees = Errors = Retains = Releases = Barriers = Pins = Polls = 0;
            Collections = Collected = 0;
            sinceCollection = 0;
            StressGc = false;
            LastPanic = null;
            ResetThreads();
        }

        /// <summary>
        /// The safepoint flag compiled code reads before calling <c>larva_poll</c>, and the pointer to it code loads it through
        /// (<c>__imp_larva_poll_pending</c>). The test host keeps it set: every safepoint asks, as before the flag existed.
        /// </summary>
        /// <summary>
        /// What compiled code reads to allocate inline (<c>larva_heap</c>): this runtime keeps its own tables, so the flag
        /// that allows it stays clear and every object comes from <c>larva_gc_new</c>.
        /// </summary>
        private static class Heap
        {
            public static readonly nint State = (nint)System.Runtime.InteropServices.NativeMemory.AllocZeroed(256);
            public static readonly nint Cell = MakeCell();

            private static nint MakeCell()
            {
                var cell = (nint*)System.Runtime.InteropServices.NativeMemory.Alloc(8);
                *cell = State;
                return (nint)cell;
            }
        }

        private static class PollFlag
        {
            public static readonly nint Pending = Make();
            public static readonly nint Cell = MakeCell();

            private static nint Make()
            {
                var flag = (int*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(4);
                *flag = 1;
                return (nint)flag;
            }

            private static nint MakeCell()
            {
                var cell = (nint*)System.Runtime.InteropServices.NativeMemory.Alloc(8);
                *cell = Pending;
                return (nint)cell;
            }
        }

        /// <summary>Symbol name → entry point, for binding the JIT's external declarations.</summary>
        public static IReadOnlyDictionary<string, nint> Symbols { get; } = WithThreads(new Dictionary<string, nint>
        {
            ["larva_alloc"] = (nint)(delegate* unmanaged<long, long, void*>)&Alloc,
            ["larva_raw_alloc"] = (nint)(delegate* unmanaged<long, long, void*>)&Alloc,
            ["larva_exception_cell"] = (nint)(delegate* unmanaged<long, long, void*>)&Alloc,
            ["larva_global"] = (nint)(delegate* unmanaged<void**, long, void*>)&Global,
            ["larva_free"] = (nint)(delegate* unmanaged<void*, void>)&Free,
            // In-process code raises with the system's RaiseException (the same signature).
            ["larva_raise"] = System.Runtime.InteropServices.NativeLibrary.GetExport(System.Runtime.InteropServices.NativeLibrary.Load("kernel32.dll"), "RaiseException"),
            ["larva_gc_new"] = (nint)(delegate* unmanaged<long, long, void*>)&GcNew,
            ["larva_gc_new_in"] = (nint)(delegate* unmanaged<long, long, long, void*>)&GcNewIn,
            ["larva_gc_domain"] = (nint)(delegate* unmanaged<void*, long>)&GcDomain,
            ["larva_gc_alloc"] = (nint)(delegate* unmanaged<uint*, long, void*>)&GcAlloc,
            ["larva_gc_array"] = (nint)(delegate* unmanaged<uint*, long, void*>)&GcArray,
            ["larva_gc_retain"] = (nint)(delegate* unmanaged<void*, void>)&GcRetain,
            ["larva_wide_udiv"] = (nint)(delegate* unmanaged<ulong*, ulong*, ulong*, int, void>)&WideUDiv,
            ["larva_wide_urem"] = (nint)(delegate* unmanaged<ulong*, ulong*, ulong*, int, void>)&WideURem,
            ["larva_wide_sdiv"] = (nint)(delegate* unmanaged<ulong*, ulong*, ulong*, int, void>)&WideSDiv,
            ["larva_wide_srem"] = (nint)(delegate* unmanaged<ulong*, ulong*, ulong*, int, void>)&WideSRem,
            ["larva_wide_shl"] = (nint)(delegate* unmanaged<ulong*, ulong*, ulong*, int, void>)&WideShl,
            ["larva_wide_lshr"] = (nint)(delegate* unmanaged<ulong*, ulong*, ulong*, int, void>)&WideLShr,
            ["larva_wide_ashr"] = (nint)(delegate* unmanaged<ulong*, ulong*, ulong*, int, void>)&WideAShr,
            ["larva_gc_release"] = (nint)(delegate* unmanaged<void*, void>)&GcRelease,
            ["larva_rc_dead"] = (nint)(delegate* unmanaged<void*, void>)&RcDead,
            ["larva_gc_finalizer"] = (nint)(delegate* unmanaged<void*, void*, void>)&GcFinalizer,
            ["larva_array_new"] = (nint)(delegate* unmanaged<long, long, long, int, void*>)&ArrayNew,
            ["larva_region_enter"] = (nint)(delegate* unmanaged<int, void>)&RegionEnter,
            ["larva_region_exit"] = (nint)(delegate* unmanaged<int, void>)&RegionExit,
            ["larva_region_depth"] = (nint)(delegate* unmanaged<int>)&RegionDepth,
            ["larva_region_unwind"] = (nint)(delegate* unmanaged<int, void>)&RegionUnwind,
            ["larva_gc_wb"] = (nint)(delegate* unmanaged<void*, void*, long, void>)&WriteBarrier,
            ["larva_pin"] = (nint)(delegate* unmanaged<void*, void>)&Pin,
            ["larva_unpin"] = (nint)(delegate* unmanaged<void*, void>)&Unpin,
            ["larva_poll"] = (nint)(delegate* unmanaged<void>)&Poll,
            ["larva_poll_pending"] = PollFlag.Pending,
            ["__imp_larva_poll_pending"] = PollFlag.Cell,
            ["larva_heap"] = Heap.State,
            ["__imp_larva_heap"] = Heap.Cell,
            ["larva_panic"] = (nint)(delegate* unmanaged<int, long, long, void>)&Panic,
            ["larva_print_i64"] = (nint)(delegate* unmanaged<long, void>)&PrintI64,
            ["larva_print_u64"] = (nint)(delegate* unmanaged<ulong, void>)&PrintU64,
            ["larva_print_f64"] = (nint)(delegate* unmanaged<long, void>)&PrintF64,
            ["larva_print_bool"] = (nint)(delegate* unmanaged<int, void>)&PrintBool,
            ["larva_print_str"] = (nint)(delegate* unmanaged<long*, void>)&PrintStr,
            ["larva_print_line"] = (nint)(delegate* unmanaged<void>)&PrintLine,
            ["larva_print_utf8"] = (nint)(delegate* unmanaged<long*, void>)&PrintUtf8,
            ["larva_read_line"] = (nint)(delegate* unmanaged<long>)&ReadLine,
            ["larva_read_unit"] = (nint)(delegate* unmanaged<long, long>)&ReadUnit,
            ["larva_args_count"] = (nint)(delegate* unmanaged<long>)&ArgsCount,
            ["larva_args_length"] = (nint)(delegate* unmanaged<long, long>)&ArgsLength,
            ["larva_args_unit"] = (nint)(delegate* unmanaged<long, long, long>)&ArgsUnit,
            ["larva_format_f64"] = (nint)(delegate* unmanaged<long, long>)&FormatF64,
            ["larva_format_unit"] = (nint)(delegate* unmanaged<long, long>)&FormatUnit,
            ["larva_gc_request"] = (nint)(delegate* unmanaged<void>)&GcRequest,
            ["larva_gc_collections"] = (nint)(delegate* unmanaged<long>)&GcCollections,
            ["larva_gc_objects"] = (nint)(delegate* unmanaged<long>)&GcObjects,
            ["larva_gc_custom"] = (nint)(delegate* unmanaged<void*, void*, void*, void>)&GcCustom,
            ["larva_time_now"] = (nint)(delegate* unmanaged<long>)&TimeNow,
            ["larva_time_sleep"] = (nint)(delegate* unmanaged<long, void>)&TimeSleep,
            ["larva_time_utc"] = (nint)(delegate* unmanaged<long>)&TimeUtc,
            ["larva_time_offset"] = (nint)(delegate* unmanaged<long, long>)&TimeOffset,
            ["larva_time_precise"] = (nint)(delegate* unmanaged<long>)&TimePrecise,
            ["larva_stream_open"] = (nint)(delegate* unmanaged<long*, long, long>)&StreamOpen,
            ["larva_stream_close"] = (nint)(delegate* unmanaged<long, long>)&StreamClose,
            ["larva_stream_read"] = (nint)(delegate* unmanaged<long, long*, long, long, long>)&StreamRead,
            ["larva_stream_write"] = (nint)(delegate* unmanaged<long, long*, long, long, long>)&StreamWrite,
            ["larva_stream_seek"] = (nint)(delegate* unmanaged<long, long, long, long>)&StreamSeek,
            ["larva_stream_length"] = (nint)(delegate* unmanaged<long, long>)&StreamLength,
            ["larva_stream_flush"] = (nint)(delegate* unmanaged<long, long>)&StreamFlush,
            ["larva_net_connect"] = (nint)(delegate* unmanaged<long*, long, long>)&NetConnect,
            ["larva_net_listen"] = (nint)(delegate* unmanaged<long, long>)&NetListen,
            ["larva_net_accept"] = (nint)(delegate* unmanaged<long, long>)&NetAccept,
            ["larva_net_port"] = (nint)(delegate* unmanaged<long, long>)&NetPort,
            ["larva_proc_start"] = (nint)(delegate* unmanaged<long*, long*, long>)&ProcStart,
            ["larva_proc_wait"] = (nint)(delegate* unmanaged<long, long>)&ProcWait,
            ["larva_math_sqrt"] = (nint)(delegate* unmanaged<long, long>)&MathSqrt,
            ["larva_math_sin"] = (nint)(delegate* unmanaged<long, long>)&MathSin,
            ["larva_math_cos"] = (nint)(delegate* unmanaged<long, long>)&MathCos,
            ["larva_math_tan"] = (nint)(delegate* unmanaged<long, long>)&MathTan,
            ["larva_math_asin"] = (nint)(delegate* unmanaged<long, long>)&MathAsin,
            ["larva_math_acos"] = (nint)(delegate* unmanaged<long, long>)&MathAcos,
            ["larva_math_atan"] = (nint)(delegate* unmanaged<long, long>)&MathAtan,
            ["larva_math_exp"] = (nint)(delegate* unmanaged<long, long>)&MathExp,
            ["larva_math_log"] = (nint)(delegate* unmanaged<long, long>)&MathLog,
            ["larva_math_log10"] = (nint)(delegate* unmanaged<long, long>)&MathLog10,
            ["larva_math_floor"] = (nint)(delegate* unmanaged<long, long>)&MathFloor,
            ["larva_math_ceil"] = (nint)(delegate* unmanaged<long, long>)&MathCeil,
            ["larva_math_atan2"] = (nint)(delegate* unmanaged<long, long, long>)&MathAtan2,
            ["larva_math_pow"] = (nint)(delegate* unmanaged<long, long, long>)&MathPow,
            ["larva_f64_bits"] = (nint)(delegate* unmanaged<long, long>)&F64Bits,
            ["larva_f64_from_bits"] = (nint)(delegate* unmanaged<long, long>)&F64FromBits,
            ["larva_mem_alloc"] = (nint)(delegate* unmanaged<long, void*>)&MemAlloc,
            ["larva_mem_free"] = (nint)(delegate* unmanaged<void*, void>)&MemFree,
            ["larva_mem_copy"] = (nint)(delegate* unmanaged<void*, void*, long, void>)&MemCopy,
            ["larva_mem_cmp"] = (nint)(delegate* unmanaged<void*, void*, long, long>)&MemCmp,
            ["larva_native_symbol"] = (nint)(delegate* unmanaged<long*, long*, long>)&NativeSymbol,
            ["larva_file_read"] = (nint)(delegate* unmanaged<long*, long>)&FileRead,
            ["larva_file_take"] = (nint)(delegate* unmanaged<long*, void>)&FileTake,
            ["larva_file_write"] = (nint)(delegate* unmanaged<long*, long*, long, long>)&FileWrite,
            ["larva_file_kind"] = (nint)(delegate* unmanaged<long*, long>)&FileKind,
            ["larva_file_delete"] = (nint)(delegate* unmanaged<long*, long>)&FileDelete,
            ["larva_file_time"] = (nint)(delegate* unmanaged<long*, long>)&FileTime,
            ["larva_dir_create"] = (nint)(delegate* unmanaged<long*, long>)&DirCreate,
            ["larva_dir_list"] = (nint)(delegate* unmanaged<long*, long>)&DirList,
            ["larva_dir_length"] = (nint)(delegate* unmanaged<long, long>)&DirLength,
            ["larva_dir_unit"] = (nint)(delegate* unmanaged<long, long, long>)&DirUnit,
            ["larva_env_get"] = (nint)(delegate* unmanaged<long*, long>)&EnvGet,
            ["larva_env_unit"] = (nint)(delegate* unmanaged<long, long>)&EnvUnit,
            ["larva_process_run"] = (nint)(delegate* unmanaged<long*, long>)&ProcessRun,
            ["larva_parse_f64"] = (nint)(delegate* unmanaged<long*, long>)&ParseF64,
        });

        private static Dictionary<string, nint> WithThreads(Dictionary<string, nint> symbols)
        {
            AddThreadSymbols(symbols);
            AddWideFloatSymbols(symbols);
            return symbols;
        }

        // ── System.IO ──────────────────────────────────────────────

        private static byte[] fileBuffer = [];
        private static string[] folderEntries = [];
        private static string? environmentValue;

        private static string Text(long* text) => new((char*)(text + 1), 0, (int)text[0]);

        private static long Status(Action action)
        {
            try
            {
                action();
                return 0;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return -1;
            }
        }

        [UnmanagedCallersOnly]
        private static long FileRead(long* path)
        {
            try
            {
                fileBuffer = File.ReadAllBytes(Text(path));
                return fileBuffer.Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                fileBuffer = [];
                return -1;
            }
        }

        [UnmanagedCallersOnly]
        private static void FileTake(long* target) =>
            fileBuffer.AsSpan(0, (int)Math.Min(target[0], fileBuffer.Length)).CopyTo(new Span<byte>(target + 1, (int)target[0]));

        [UnmanagedCallersOnly]
        private static long FileWrite(long* path, long* bytes, long append)
        {
            string name = Text(path);
            var data = new ReadOnlySpan<byte>(bytes + 1, (int)bytes[0]).ToArray();
            return Status(() =>
            {
                using var file = new FileStream(name, append != 0 ? FileMode.Append : FileMode.Create, FileAccess.Write);
                file.Write(data);
            });
        }

        [UnmanagedCallersOnly]
        private static long FileKind(long* path)
        {
            string name = Text(path);
            return File.Exists(name) ? 1 : Directory.Exists(name) ? 2 : 0;
        }

        [UnmanagedCallersOnly]
        private static long FileDelete(long* path)
        {
            string name = Text(path);
            return Status(() =>
            {
                if (Directory.Exists(name))
                    Directory.Delete(name);
                else if (File.Exists(name))
                    File.Delete(name);
                else
                    throw new IOException("missing");
            });
        }

        [UnmanagedCallersOnly]
        private static long FileTime(long* path)
        {
            string name = Text(path);
            return File.Exists(name) || Directory.Exists(name) ? new DateTimeOffset(File.GetLastWriteTimeUtc(name)).ToUnixTimeMilliseconds() : -1;
        }

        [UnmanagedCallersOnly]
        private static long DirCreate(long* path)
        {
            string name = Text(path);
            return Status(() => Directory.CreateDirectory(name));
        }

        [UnmanagedCallersOnly]
        private static long DirList(long* path)
        {
            try
            {
                folderEntries = [.. Directory.EnumerateFileSystemEntries(Text(path)).Select(e => Path.GetFileName(e)).Order(StringComparer.Ordinal)];
                return folderEntries.Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                folderEntries = [];
                return -1;
            }
        }

        [UnmanagedCallersOnly]
        private static long DirLength(long index) => index >= 0 && index < folderEntries.Length ? folderEntries[index].Length : 0;

        [UnmanagedCallersOnly]
        private static long DirUnit(long index, long at) =>
            index >= 0 && index < folderEntries.Length && at >= 0 && at < folderEntries[index].Length ? folderEntries[index][(int)at] : 0;

        [UnmanagedCallersOnly]
        private static long EnvGet(long* name)
        {
            environmentValue = Environment.GetEnvironmentVariable(Text(name));
            return environmentValue?.Length ?? -1;
        }

        [UnmanagedCallersOnly]
        private static long ParseF64(long* text) => BitConverter.DoubleToInt64Bits(
            double.TryParse(Text(text), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : double.NaN);

        [UnmanagedCallersOnly]
        private static long EnvUnit(long index) =>
            environmentValue is not null && index >= 0 && index < environmentValue.Length ? environmentValue[(int)index] : 0;

        [UnmanagedCallersOnly]
        private static long ProcessRun(long* command)
        {
            Out.Flush();
            try
            {
                string line = Text(command);
                var start = OperatingSystem.IsWindows()
                    ? new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/d", "/s", "/c", line])
                    : new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", line]);
                start.UseShellExecute = false;
                using var process = System.Diagnostics.Process.Start(start);
                if (process is null)
                    return -1;
                process.WaitForExit();
                return process.ExitCode;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return -1;
            }
        }

        [UnmanagedCallersOnly]
        private static void* Alloc(long size, long align) => AllocateHeap(size, align);

        /// <summary>The cell of a global that holds handles: a heap cell (scanned by the collector), made on first use.</summary>
        [UnmanagedCallersOnly]
        private static void* Global(void** slot, long size)
        {
            if (*slot == null)
                *slot = AllocateHeap(size, 16);
            return *slot;
        }

        private static void* AllocateHeap(long size, long align)
        {
            void* block = NativeMemory.AlignedAlloc((nuint)Math.Max(1, size), (nuint)Math.Max(8, align));
            NativeMemory.Clear(block, (nuint)Math.Max(1, size));
            live[(nint)block] = AllocationKind.Heap;
            sizes[(nint)block] = size;
            Allocations++;
            return block;
        }

        [UnmanagedCallersOnly]
        private static void Free(void* pointer)
        {
            if (pointer == null)
                return;
            if (!live.Remove((nint)pointer, out var kind) || kind != AllocationKind.Heap)
            {
                Errors++;
                return;
            }
            sizes.Remove((nint)pointer);
            NativeMemory.AlignedFree(pointer);
            Frees++;
        }

        [UnmanagedCallersOnly]
        private static void* GcNew(long size, long align) => AllocateGc(size);

        /// <summary>A gc object for code of GC domain <paramref name="domain"/> (the header's byte says so).</summary>
        [UnmanagedCallersOnly]
        private static void* GcNewIn(long size, long align, long domain) => AllocateGc(size, (int)domain);

        /// <summary>The GC domain of a gc object: the byte before its age byte.</summary>
        [UnmanagedCallersOnly]
        private static long GcDomain(void* handle) => handle == null ? 0 : ((byte*)handle)[-2] & 0x3F;

        /// <summary>Whether retain and release skip the object: the uncounted bit of its domain byte (a tracing or manual domain in a mixed program).</summary>
        private static bool Uncounted(void* handle) => (((byte*)handle)[-2] & 0x40) != 0;

        /// <summary>A gc object made from a type descriptor (<c>{ u32 size, u32 stride, u32 flags, … }</c>); the JIT's heap ignores the map but takes the domain in the flags.</summary>
        [UnmanagedCallersOnly]
        private static void* GcAlloc(uint* type, long size) => AllocateGc(size, (int)(type[2] >> 8 & 0xFF));

        /// <summary>A gc array from its descriptor: the header is the descriptor's size, the element stride its stride.</summary>
        [UnmanagedCallersOnly]
        private static void* GcArray(uint* type, long length)
        {
            long size = type[0] + Math.Max(0, length) * type[1];
            void* array = AllocateGc(size, (int)(type[2] >> 8 & 0xFF));
            // An object ending with an array in place (flag 8): the length is the word before the elements.
            *(long*)((byte*)array + ((type[2] & 8) != 0 ? type[0] - 8 : 0)) = length;
            return array;
        }

        private static void* AllocateGc(long size, int domain = 0)
        {
            var bytes = (nuint)(GcHeader + Math.Max(1, size));
            bool custom = customAllocate != null && inCustom == 0;
            byte* block;
            if (custom)
            {
                inCustom++;
                block = (byte*)customAllocate(bytes);
                inCustom--;
                if (block == null)
                    throw new OutOfMemoryException("the program's collector gave no memory");
            }
            else
            {
                block = (byte*)NativeMemory.AlignedAlloc(bytes, 16);
            }
            NativeMemory.Clear(block, bytes);
            *(int*)(block + 8) = 1; // reference count (the C runtime's header: compiled code counts in place)
            // The byte before the payload is the age compiled write barriers read: 2 (remembered) never calls them; the one
            // before it the GC domain.
            block[GcHeader - 1] = 2;
            block[GcHeader - 2] = (byte)domain;
            var payload = (nint)(block + GcHeader);
            if (custom)
                customBlocks.Add(payload);
            live[payload] = AllocationKind.Gc;
            sizes[payload] = size;
            sinceCollection++;
            if (regions.Count > 0)
                regionOf[payload] = regions.Peek();
            Allocations++;
            return (void*)payload;
        }

        private static void FreeGc(nint payload)
        {
            finalizers.Remove(payload);
            live.Remove(payload);
            sizes.Remove(payload);
            regionOf.Remove(payload);
            if (!customBlocks.Remove(payload))
                NativeMemory.AlignedFree((void*)(payload - GcHeader));
            else if (collecting == 0)
                ReleaseCustom(payload - GcHeader);
            else
                customPending.Add(payload - GcHeader);
            Frees++;
        }

        // ── The program's own collector (`gc: custom`, System.GC.Install) ──
        // Where objects' memory comes from and goes back to, and when to collect. Its functions are the program's own code: while one
        // runs, objects it allocates take memory from the runtime and no collection starts; memory freed during a
        // collection goes back once the collection is done.

        private static delegate* unmanaged<ulong, void*> customAllocate;
        private static delegate* unmanaged<void*, void> customRelease;
        private static delegate* unmanaged<long, long, int> customPoll;
        private static int inCustom, collecting;
        private static long customAsked = -1;
        private static readonly HashSet<nint> customBlocks = [];
        private static readonly List<nint> customPending = [];

        // ── Streams (System.IO) ─────────────────────────────────────

        private static readonly Larva.IR.HostStreams streams = new(() => Input ?? Console.In, () => Output ?? Console.Out, () => Console.Error);

        [UnmanagedCallersOnly]
        private static long StreamOpen(long* path, long mode) => streams.Open(Text(path), mode);

        [UnmanagedCallersOnly]
        private static long StreamClose(long handle) => streams.Close(handle);

        [UnmanagedCallersOnly]
        private static long StreamRead(long handle, long* bytes, long offset, long count)
        {
            if (offset < 0 || count < 0 || offset + count > bytes[0])
                return -1;
            if (!streams.IsSocket(handle))
                return streams.Read(handle, new Span<byte>((byte*)(bytes + 1) + offset, (int)count));
            // A connection's read waits for the network: the turn goes to the program's other threads meanwhile.
            nint at = (nint)((byte*)(bytes + 1) + offset);
            int n = (int)count;
            return Blocking(() => streams.Read(handle, new Span<byte>((void*)at, n)));
        }

        [UnmanagedCallersOnly]
        private static long StreamWrite(long handle, long* bytes, long offset, long count) =>
            offset < 0 || count < 0 || offset + count > bytes[0] ? -1 : streams.Write(handle, new ReadOnlySpan<byte>((byte*)(bytes + 1) + offset, (int)count));

        [UnmanagedCallersOnly]
        private static long StreamSeek(long handle, long offset, long origin) => streams.Seek(handle, offset, origin);

        [UnmanagedCallersOnly]
        private static long StreamLength(long handle) => streams.Length(handle);

        [UnmanagedCallersOnly]
        private static long StreamFlush(long handle) => streams.Flush(handle);

        [UnmanagedCallersOnly]
        private static long NetConnect(long* host, long port)
        {
            string name = Text(host);
            return Blocking(() => streams.Connect(name, port));
        }

        [UnmanagedCallersOnly]
        private static long NetListen(long port) => streams.Listen(port);

        [UnmanagedCallersOnly]
        private static long NetAccept(long listener) => Blocking(() => streams.Accept(listener));

        [UnmanagedCallersOnly]
        private static long NetPort(long handle) => streams.Port(handle);

        [UnmanagedCallersOnly]
        private static long ProcStart(long* command, long* arguments)
        {
            string program = Text(command), args = Text(arguments);
            return streams.Start(program, args);
        }

        [UnmanagedCallersOnly]
        private static long ProcWait(long handle) => Blocking(() => streams.Wait(handle));

        // System.Math: floats as their bits.
        [UnmanagedCallersOnly]
        private static long MathSqrt(long x) => BitConverter.DoubleToInt64Bits(Math.Sqrt(BitConverter.Int64BitsToDouble(x)));

        [UnmanagedCallersOnly]
        private static long MathSin(long x) => BitConverter.DoubleToInt64Bits(Math.Sin(BitConverter.Int64BitsToDouble(x)));

        [UnmanagedCallersOnly]
        private static long MathCos(long x) => BitConverter.DoubleToInt64Bits(Math.Cos(BitConverter.Int64BitsToDouble(x)));

        [UnmanagedCallersOnly]
        private static long MathTan(long x) => BitConverter.DoubleToInt64Bits(Math.Tan(BitConverter.Int64BitsToDouble(x)));

        [UnmanagedCallersOnly]
        private static long MathAsin(long x) => BitConverter.DoubleToInt64Bits(Math.Asin(BitConverter.Int64BitsToDouble(x)));

        [UnmanagedCallersOnly]
        private static long MathAcos(long x) => BitConverter.DoubleToInt64Bits(Math.Acos(BitConverter.Int64BitsToDouble(x)));

        [UnmanagedCallersOnly]
        private static long MathAtan(long x) => BitConverter.DoubleToInt64Bits(Math.Atan(BitConverter.Int64BitsToDouble(x)));

        [UnmanagedCallersOnly]
        private static long MathExp(long x) => BitConverter.DoubleToInt64Bits(Math.Exp(BitConverter.Int64BitsToDouble(x)));

        [UnmanagedCallersOnly]
        private static long MathLog(long x) => BitConverter.DoubleToInt64Bits(Math.Log(BitConverter.Int64BitsToDouble(x)));

        [UnmanagedCallersOnly]
        private static long MathLog10(long x) => BitConverter.DoubleToInt64Bits(Math.Log10(BitConverter.Int64BitsToDouble(x)));

        [UnmanagedCallersOnly]
        private static long MathFloor(long x) => BitConverter.DoubleToInt64Bits(Math.Floor(BitConverter.Int64BitsToDouble(x)));

        [UnmanagedCallersOnly]
        private static long MathCeil(long x) => BitConverter.DoubleToInt64Bits(Math.Ceiling(BitConverter.Int64BitsToDouble(x)));

        [UnmanagedCallersOnly]
        private static long MathAtan2(long x, long y) => BitConverter.DoubleToInt64Bits(Math.Atan2(BitConverter.Int64BitsToDouble(x), BitConverter.Int64BitsToDouble(y)));

        [UnmanagedCallersOnly]
        private static long MathPow(long x, long y) => BitConverter.DoubleToInt64Bits(Math.Pow(BitConverter.Int64BitsToDouble(x), BitConverter.Int64BitsToDouble(y)));

        [UnmanagedCallersOnly]
        private static long F64Bits(long bits) => bits;

        [UnmanagedCallersOnly]
        private static long F64FromBits(long bits) => bits;

        [UnmanagedCallersOnly]
        private static long TimeNow() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;

        [UnmanagedCallersOnly]
        private static long TimeUtc() => DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;

        [UnmanagedCallersOnly]
        private static long TimeOffset(long seconds)
        {
            long min = (DateTime.MinValue.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond;
            long max = (DateTime.MaxValue.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond;
            var moment = new DateTime(DateTime.UnixEpoch.Ticks + Math.Clamp(seconds, min, max) * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            return (long)TimeZoneInfo.Local.GetUtcOffset(moment).TotalSeconds;
        }

        [UnmanagedCallersOnly]
        private static long TimePrecise()
        {
            long stamp = System.Diagnostics.Stopwatch.GetTimestamp(), frequency = System.Diagnostics.Stopwatch.Frequency;
            return stamp / frequency * 1_000_000_000 + stamp % frequency * 1_000_000_000 / frequency;
        }

        [UnmanagedCallersOnly]
        private static void TimeSleep(long milliseconds)
        {
            if (milliseconds > 0)
                Blocking(() => { Thread.Sleep(TimeSpan.FromMilliseconds(milliseconds)); return 0; });
        }

        [UnmanagedCallersOnly]
        private static void GcCustom(void* allocate, void* release, void* poll)
        {
            customAllocate = (delegate* unmanaged<ulong, void*>)allocate;
            customRelease = (delegate* unmanaged<void*, void>)release;
            customPoll = (delegate* unmanaged<long, long, int>)poll;
            customAsked = -1;
        }

        private static void ReleaseCustom(nint block)
        {
            if (customRelease == null)
                return; // the collector was removed: its memory stays where it is
            inCustom++;
            customRelease((void*)block);
            inCustom--;
        }

        private static void ReleasePending()
        {
            var blocks = customPending.ToArray();
            customPending.Clear();
            foreach (var block in blocks)
                ReleaseCustom(block);
        }

        // ── Wide integers (128 and 256 bits) for the direct x86-64 back end: limbs of 64 bits, lowest first ──

        private static BigInteger WideRead(ulong* limbs, int count, bool signed)
        {
            var value = BigInteger.Zero;
            for (int i = count - 1; i >= 0; i--)
                value = (value << 64) | limbs[i];
            return signed && (limbs[count - 1] >> 63) != 0 ? value - (BigInteger.One << (64 * count)) : value;
        }

        private static void WideWrite(ulong* limbs, int count, BigInteger value)
        {
            var bits = value & ((BigInteger.One << (64 * count)) - 1);
            for (int i = 0; i < count; i++)
                limbs[i] = (ulong)((bits >> (64 * i)) & ulong.MaxValue);
        }

        private static void WideDivide(ulong* a, ulong* b, ulong* result, int limbs, bool signed, bool remainder)
        {
            var divisor = WideRead(b, limbs, signed);
            if (divisor.IsZero)
            {
                ((delegate* unmanaged<int, long, long, void>)&Panic)(2, 0, 0);
                return;
            }
            var dividend = WideRead(a, limbs, signed);
            WideWrite(result, limbs, remainder ? BigInteger.Remainder(dividend, divisor) : BigInteger.Divide(dividend, divisor));
        }

        [UnmanagedCallersOnly]
        private static void WideUDiv(ulong* a, ulong* b, ulong* result, int limbs) => WideDivide(a, b, result, limbs, false, false);

        [UnmanagedCallersOnly]
        private static void WideURem(ulong* a, ulong* b, ulong* result, int limbs) => WideDivide(a, b, result, limbs, false, true);

        [UnmanagedCallersOnly]
        private static void WideSDiv(ulong* a, ulong* b, ulong* result, int limbs) => WideDivide(a, b, result, limbs, true, false);

        [UnmanagedCallersOnly]
        private static void WideSRem(ulong* a, ulong* b, ulong* result, int limbs) => WideDivide(a, b, result, limbs, true, true);

        /// <summary>The shift amount is the low limb of the right operand, masked to the width (as x86 masks its shifts).</summary>
        [UnmanagedCallersOnly]
        private static void WideShl(ulong* a, ulong* b, ulong* result, int limbs)
        {
            int amount = (int)(b[0] & (ulong)(64 * limbs - 1));
            WideWrite(result, limbs, WideRead(a, limbs, false) << amount);
        }

        [UnmanagedCallersOnly]
        private static void WideLShr(ulong* a, ulong* b, ulong* result, int limbs)
        {
            int amount = (int)(b[0] & (ulong)(64 * limbs - 1));
            WideWrite(result, limbs, WideRead(a, limbs, false) >> amount);
        }

        [UnmanagedCallersOnly]
        private static void WideAShr(ulong* a, ulong* b, ulong* result, int limbs)
        {
            int amount = (int)(b[0] & (ulong)(64 * limbs - 1));
            WideWrite(result, limbs, WideRead(a, limbs, true) >> amount);
        }

        [UnmanagedCallersOnly]
        private static void GcRetain(void* handle)
        {
            if (handle == null)
                return;
            if (!live.ContainsKey((nint)handle))
            {
                Errors++;
                return;
            }
            if (Uncounted(handle))
                return;
            (*(int*)((byte*)handle - 8))++;
            Retains++;
        }

        [UnmanagedCallersOnly]
        private static void GcRelease(void* handle)
        {
            if (handle == null)
                return;
            if (!live.ContainsKey((nint)handle))
            {
                Errors++;
                return;
            }
            if (Uncounted(handle))
                return;
            Releases++;
            if (--*(int*)((byte*)handle - 8) == 0)
                Dead(handle);
        }

        /// <summary>A count compiled code keeps in place reached 0.</summary>
        [UnmanagedCallersOnly]
        private static void RcDead(void* handle)
        {
            if (!live.ContainsKey((nint)handle))
            {
                Errors++;
                return;
            }
            Releases++;
            Dead(handle);
        }

        private static void Dead(void* handle)
        {
            if (finalizers.ContainsKey((nint)handle))
            {
                // The finalizer borrows the object (handles go at +0): one reference for the call, given up after it.
                *(int*)((byte*)handle - 8) = 1;
                Finalize((nint)handle);
                if (--*(int*)((byte*)handle - 8) != 0)
                    return;
            }
            FreeGc((nint)handle);
        }

        /// <summary>Gives a new object the function its death calls.</summary>
        [UnmanagedCallersOnly]
        private static void GcFinalizer(void* handle, void* finalizer)
        {
            if (handle != null && finalizer != null)
                finalizers[(nint)handle] = (nint)finalizer;
        }

        /// <summary>Runs an object's finalizer, once (it is forgotten first).</summary>
        private static void Finalize(nint payload)
        {
            if (!finalizers.Remove(payload, out var finalizer))
                return;
            Finalized++;
            finalizing++;
            try
            {
                ((delegate* unmanaged<void*, void>)finalizer)((void*)payload);
            }
            finally
            {
                finalizing--;
            }
        }

        /// <summary>Closes a region: finalizers first (everything in it is still there for them), then frees.</summary>
        private static void FreeRegion(int region)
        {
            var dying = regionOf.Where(kv => kv.Value == region).Select(kv => kv.Key).ToList();
            foreach (var payload in dying)
                Finalize(payload);
            foreach (var payload in dying)
                if (live.ContainsKey(payload))
                    FreeGc(payload);
        }

        /// <summary>A zeroed array with its length in the first 8 bytes; elements start at <paramref name="header"/>.</summary>
        [UnmanagedCallersOnly]
        private static void* ArrayNew(long length, long header, long stride, int managed)
        {
            long size = header + Math.Max(0, length) * stride;
            void* array = (managed & 1) != 0 ? AllocateGc(size, managed >> 8 & 0xFF) : AllocateHeap(size, Math.Min(header, 16));
            // `managed` 4: an object ending with the array in place, its length the word before the elements.
            *(long*)((byte*)array + ((managed & 4) != 0 ? header - 8 : 0)) = length;
            return array;
        }

        [UnmanagedCallersOnly]
        private static void RegionEnter(int region) => regions.Push(region);

        [UnmanagedCallersOnly]
        private static void RegionExit(int region)
        {
            if (regions.Count == 0 || regions.Pop() != region)
                Errors++;
            FreeRegion(region);
        }

        [UnmanagedCallersOnly]
        private static int RegionDepth() => regions.Count;

        /// <summary>An exception leaves a frame: the regions it opened (above <paramref name="depth"/>) close.</summary>
        [UnmanagedCallersOnly]
        private static void RegionUnwind(int depth)
        {
            while (regions.Count > depth)
            {
                FreeRegion(regions.Pop());
            }
        }

        [UnmanagedCallersOnly]
        private static void WriteBarrier(void* owner, void* slot, long size) => Barriers++;

        /// <summary>A borrow into a gc object: the object stays alive (and, for a moving collector, in place) until unpinned.</summary>
        [UnmanagedCallersOnly]
        private static void Pin(void* handle)
        {
            Pins++;
            pinned[(nint)handle] = pinned.GetValueOrDefault((nint)handle) + 1;
        }

        [UnmanagedCallersOnly]
        private static void Unpin(void* handle)
        {
            if (handle != null && pinned.TryGetValue((nint)handle, out int count))
            {
                if (count > 1)
                    pinned[(nint)handle] = count - 1;
                else
                    pinned.Remove((nint)handle);
            }
        }

        /// <summary>Safepoint: collects when enough objects were allocated since the last collection (always under stress).</summary>
        [UnmanagedCallersOnly]
        private static void Poll()
        {
            Polls++;
            TakeTurns();
            // The collector walks the stack of the thread it runs on: none runs while others are alive.
            if (finalizing != 0 || inCustom != 0 || Threaded)
                return;
            bool due;
            if (StressGc || collectRequested)
                due = true;
            else if (customPoll != null)
            {
                // The program's collector decides, asked again only once something was allocated since its last answer.
                due = false;
                if (sinceCollection != customAsked)
                {
                    inCustom++;
                    due = customPoll(sinceCollection, live.Count(kv => kv.Value == AllocationKind.Gc)) != 0;
                    inCustom--;
                    customAsked = due ? -1 : sinceCollection;
                }
            }
            else
                due = sinceCollection >= CollectionThreshold;
            if (due)
            {
                collectRequested = false;
                CollectFromSafepoint();
            }
        }

        /// <summary><c>GC.Collect()</c> asked for a collection: the next safepoint runs one.</summary>
        private static bool collectRequested;

        [UnmanagedCallersOnly]
        private static void GcRequest() => collectRequested = true;

        [UnmanagedCallersOnly]
        private static void* MemAlloc(long size) => NativeMemory.AllocZeroed((nuint)Math.Max(size, 1));

        [UnmanagedCallersOnly]
        private static void MemFree(void* block) => NativeMemory.Free(block);

        [UnmanagedCallersOnly]
        private static void MemCopy(void* target, void* source, long count)
        {
            if (count > 0)
                Buffer.MemoryCopy(source, target, count, count);
        }

        [UnmanagedCallersOnly]
        private static long MemCmp(void* left, void* right, long count)
        {
            long order = 0;
            for (long done = 0; done < count && order == 0; done += int.MaxValue)
            {
                int piece = (int)Math.Min(count - done, int.MaxValue);
                order = new ReadOnlySpan<byte>((byte*)left + done, piece).SequenceCompareTo(new ReadOnlySpan<byte>((byte*)right + done, piece));
            }
            return order;
        }

        private static readonly Dictionary<string, nint> libraries = [];

        /// <summary>The address of C function `name` in `library` (each a str: its length, then UTF-16 units).</summary>
        [UnmanagedCallersOnly]
        private static long NativeSymbol(long* library, long* name)
        {
            string file = new((char*)(library + 1), 0, (int)library[0]);
            string entry = new((char*)(name + 1), 0, (int)name[0]);
            if (!libraries.TryGetValue(file, out var handle))
                libraries[file] = handle = System.Runtime.InteropServices.NativeLibrary.Load(file);
            return System.Runtime.InteropServices.NativeLibrary.TryGetExport(handle, entry, out var address) ? address
                : throw new InvalidOperationException($"`{entry}` not found in `{file}`");
        }

        [UnmanagedCallersOnly]
        private static long GcCollections() => Collections;

        [UnmanagedCallersOnly]
        private static long GcObjects() => live.Count(kv => kv.Value == AllocationKind.Gc);

        /// <summary>Where the print intrinsics write (the console when null).</summary>
        public static TextWriter? Output { get; set; }

        /// <summary>Where <c>read.line</c> reads (the console when null).</summary>
        public static TextReader? Input { get; set; }

        /// <summary>The command line <c>args.*</c> reads (without the program's name).</summary>
        public static string[] Arguments { get; set; } = [];

        private static string? line;

        [UnmanagedCallersOnly]
        private static long ReadLine()
        {
            line = (Input ?? Console.In).ReadLine();
            return line?.Length ?? -1;
        }

        [UnmanagedCallersOnly]
        private static long ReadUnit(long index) => line is not null && index >= 0 && index < line.Length ? line[(int)index] : 0;

        private static string formatted = "";

        [UnmanagedCallersOnly]
        private static long FormatF64(long bits)
        {
            double value = BitConverter.Int64BitsToDouble(bits);
            formatted = double.IsNaN(value) ? "nan" : double.IsInfinity(value) ? (value > 0 ? "inf" : "-inf")
                : value.ToString("G15", System.Globalization.CultureInfo.InvariantCulture).Replace("E+", "e+").Replace("E-", "e-");
            return formatted.Length;
        }

        [UnmanagedCallersOnly]
        private static long FormatUnit(long index) => index >= 0 && index < formatted.Length ? formatted[(int)index] : 0;

        [UnmanagedCallersOnly]
        private static long ArgsCount() => Arguments.Length;

        [UnmanagedCallersOnly]
        private static long ArgsLength(long index) => index >= 0 && index < Arguments.Length ? Arguments[index].Length : 0;

        [UnmanagedCallersOnly]
        private static long ArgsUnit(long index, long at) =>
            index >= 0 && index < Arguments.Length && at >= 0 && at < Arguments[index].Length ? Arguments[index][(int)at] : 0;

        private static TextWriter Out => Output ?? Console.Out;

        [UnmanagedCallersOnly]
        private static void PrintI64(long value) => Out.Write(value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        [UnmanagedCallersOnly]
        private static void PrintU64(ulong value) => Out.Write(value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        [UnmanagedCallersOnly]
        private static void PrintF64(long bits)
        {
            double value = BitConverter.Int64BitsToDouble(bits);
            Out.Write(double.IsNaN(value) ? "nan" : double.IsInfinity(value) ? (value > 0 ? "inf" : "-inf")
                : value.ToString("G15", System.Globalization.CultureInfo.InvariantCulture).Replace("E+", "e+").Replace("E-", "e-"));
        }

        [UnmanagedCallersOnly]
        private static void PrintBool(int value) => Out.Write(value != 0 ? "true" : "false");

        /// <summary>A str: its length, then UTF-16 code units.</summary>
        [UnmanagedCallersOnly]
        private static void PrintStr(long* text) => Out.Write(new string((char*)(text + 1), 0, (int)text[0]));

        [UnmanagedCallersOnly]
        private static void PrintLine() => Out.Write('\n');

        [UnmanagedCallersOnly]
        private static void PrintUtf8(long* text) => Out.Write(System.Text.Encoding.UTF8.GetString((byte*)(text + 1), (int)text[0]));

        /// <summary>Bounds, division by zero or <c>unreachable</c>: cannot unwind into the host, so the process stops.</summary>
        [UnmanagedCallersOnly]
        private static void Panic(int code, long a, long b)
        {
            LastPanic = code switch
            {
                1 => $"index {a} out of bounds for length {b}",
                2 => "division by zero",
                4 => $"invalid array length {a}",
                5 => "null reference",
                _ => $"trap {code}",
            };
            Environment.FailFast($"larva panic: {LastPanic}");
        }
    }
}

namespace Larva.IR
{
    /// <summary>
    /// Operations the runtime provides (input/output), reached with <see cref="IntrinsicRvalue"/>. Each has a fixed
    /// signature, an id (the operand of the IL's <c>sys.intrinsic</c>) and the C function native code calls
    /// (<c>larva_print_i64</c>…): the VM and both native runtimes implement the same table.
    /// <para>
    /// Input never allocates in the runtime: <c>read.line</c> reads a line into the runtime's buffer and gives its length
    /// in UTF-16 code units (-1 at the end of the input), and <c>read.unit(i)</c> reads unit i of it. The command line is
    /// read the same way (<c>args.count</c>, <c>args.length(i)</c>, <c>args.unit(i, j)</c>); the language builds its own
    /// strings from the units. Those intrinsics take and give <c>i64</c> only. <c>format.f64</c> writes a float as text (what
    /// <c>print.f64</c> prints) into another buffer, read with <c>format.unit</c>.
    /// </para>
    /// </summary>
    public static class Intrinsics
    {
        public sealed record Info(int Id, string Name, IrType[] Parameters, IrType Result, string Symbol);

        private static readonly IrType Str = new ReferenceType(new ArrayType(IrType.U16), Mutable: false);

        /// <summary>Bytes the runtime reads (<c>&amp;[u8]</c>) and bytes it fills (<c>&amp;mut [u8]</c>): a <c>u8[]</c> in the language.</summary>
        public static readonly IrType Bytes = new ReferenceType(new ArrayType(IrType.U8), Mutable: false);
        public static readonly IrType MutableBytes = new ReferenceType(new ArrayType(IrType.U8), Mutable: true);

        /// <summary>
        /// A function value's code and environment (<c>thread.start</c>): in the IR any <c>fn(env) -&gt; ()</c> and the
        /// handle it takes, which the type checker matches; the runtime calls <c>code(environment)</c> on the new thread.
        /// </summary>
        public static readonly IrType Callback = new FunctionType([IrType.U8], IrType.Unit);
        public static readonly IrType CallbackEnvironment = new PointerType(IrType.Unit, Mutable: false);

        private static readonly IrType I64Ptr = IrType.PtrMut(IrType.I64);
        private static readonly IrType I32Ptr = IrType.PtrMut(IrType.I32);

        public static readonly Info[] All =
        [
            new(0, "print.i64", [IrType.I64], IrType.Unit, "larva_print_i64"),
            new(1, "print.u64", [IrType.U64], IrType.Unit, "larva_print_u64"),
            new(2, "print.f64", [IrType.F64], IrType.Unit, "larva_print_f64"),
            new(3, "print.bool", [IrType.Bool], IrType.Unit, "larva_print_bool"),
            new(4, "print.str", [Str], IrType.Unit, "larva_print_str"),
            new(5, "print.line", [], IrType.Unit, "larva_print_line"),
            new(6, "read.line", [], IrType.I64, "larva_read_line"),
            new(7, "read.unit", [IrType.I64], IrType.I64, "larva_read_unit"),
            new(8, "args.count", [], IrType.I64, "larva_args_count"),
            new(9, "args.length", [IrType.I64], IrType.I64, "larva_args_length"),
            new(10, "args.unit", [IrType.I64, IrType.I64], IrType.I64, "larva_args_unit"),
            new(11, "format.f64", [IrType.F64], IrType.I64, "larva_format_f64"),
            new(12, "format.unit", [IrType.I64], IrType.I64, "larva_format_unit"),
            // The collector (System.GC): ask for a collection at the next safepoint, and what it has done.
            new(13, "gc.request", [], IrType.Unit, "larva_gc_request"),
            new(14, "gc.collections", [], IrType.I64, "larva_gc_collections"),
            new(15, "gc.objects", [], IrType.I64, "larva_gc_objects"),
            // Native memory and code (interop): zeroed memory C can use, freed explicitly; a C function's address.
            new(16, "mem.alloc", [IrType.I64], IrType.PtrMut(IrType.U8), "larva_mem_alloc"),
            new(17, "mem.free", [IrType.PtrMut(IrType.U8)], IrType.Unit, "larva_mem_free"),
            new(18, "native.symbol", [Str, Str], IrType.U64, "larva_native_symbol"),
            // Files, folders, the environment and processes (System.IO). A file is read whole into the runtime's buffer
            // (`file.read` gives its size, -1 when it cannot be read) and copied out with `file.take`; a folder's entries
            // are listed into another buffer (`dir.list` gives how many) and read a unit at a time, like the command line.
            // Paths are text; results are 0 for done and -1 for failed unless said otherwise.
            new(19, "file.read", [Str], IrType.I64, "larva_file_read"),
            new(20, "file.take", [MutableBytes], IrType.Unit, "larva_file_take"),
            new(21, "file.write", [Str, Bytes, IrType.I64], IrType.I64, "larva_file_write"),
            new(22, "file.kind", [Str], IrType.I64, "larva_file_kind"),
            new(23, "file.delete", [Str], IrType.I64, "larva_file_delete"),
            new(24, "file.time", [Str], IrType.I64, "larva_file_time"),
            new(25, "dir.create", [Str], IrType.I64, "larva_dir_create"),
            new(26, "dir.list", [Str], IrType.I64, "larva_dir_list"),
            new(27, "dir.length", [IrType.I64], IrType.I64, "larva_dir_length"),
            new(28, "dir.unit", [IrType.I64, IrType.I64], IrType.I64, "larva_dir_unit"),
            new(29, "env.get", [Str], IrType.I64, "larva_env_get"),
            new(30, "env.unit", [IrType.I64], IrType.I64, "larva_env_unit"),
            new(31, "process.run", [Str], IrType.I64, "larva_process_run"),
            // Text to a float, correctly rounded (the library checks the syntax first): its bits.
            new(32, "parse.f64", [Str], IrType.I64, "larva_parse_f64"),
            // The program's own collector (System.GC.Install, `gc: custom`): the addresses of its allocate(u64 size) : u8*,
            // release(u8* block) and poll(i64 allocated, i64 live) : i32 functions, all 0 to put the runtime's back. The
            // interpreter and WebAssembly keep their own heaps and ignore it.
            new(33, "gc.custom", [IrType.U64, IrType.U64, IrType.U64], IrType.Unit, "larva_gc_custom"),
            // Time (System.Threading.Tasks): a monotonic clock in milliseconds (from an arbitrary start), and waiting.
            new(34, "time.now", [], IrType.I64, "larva_time_now"),
            new(35, "time.sleep", [IrType.I64], IrType.Unit, "larva_time_sleep"),
            // Streams (System.IO): files opened by handle (0, 1 and 2 are standard input, output and error). `stream.open`
            // takes a path and mode bits (1 read, 2 write, 4 create, 8 truncate, 16 append, 32 only a new file) and gives a
            // handle or -1; reads and writes take a range of a u8[] and give the bytes moved (0 at the end), or -1.
            new(36, "stream.open", [Str, IrType.I64], IrType.I64, "larva_stream_open"),
            new(37, "stream.close", [IrType.I64], IrType.I64, "larva_stream_close"),
            new(38, "stream.read", [IrType.I64, MutableBytes, IrType.I64, IrType.I64], IrType.I64, "larva_stream_read"),
            new(39, "stream.write", [IrType.I64, Bytes, IrType.I64, IrType.I64], IrType.I64, "larva_stream_write"),
            new(40, "stream.seek", [IrType.I64, IrType.I64, IrType.I64], IrType.I64, "larva_stream_seek"),
            new(41, "stream.length", [IrType.I64], IrType.I64, "larva_stream_length"),
            new(42, "stream.flush", [IrType.I64], IrType.I64, "larva_stream_flush"),
            // Floating-point functions (System.Math): floats travel to the C functions as their bits (an i64), and
            // come back the same way; the back ends reinterpret them. f64.bits and f64.from_bits are that reinterpretation.
            new(43, "math.sqrt", [IrType.F64], IrType.F64, "larva_math_sqrt"),
            new(44, "math.sin", [IrType.F64], IrType.F64, "larva_math_sin"),
            new(45, "math.cos", [IrType.F64], IrType.F64, "larva_math_cos"),
            new(46, "math.tan", [IrType.F64], IrType.F64, "larva_math_tan"),
            new(47, "math.asin", [IrType.F64], IrType.F64, "larva_math_asin"),
            new(48, "math.acos", [IrType.F64], IrType.F64, "larva_math_acos"),
            new(49, "math.atan", [IrType.F64], IrType.F64, "larva_math_atan"),
            new(50, "math.exp", [IrType.F64], IrType.F64, "larva_math_exp"),
            new(51, "math.log", [IrType.F64], IrType.F64, "larva_math_log"),
            new(52, "math.log10", [IrType.F64], IrType.F64, "larva_math_log10"),
            new(53, "math.floor", [IrType.F64], IrType.F64, "larva_math_floor"),
            new(54, "math.ceil", [IrType.F64], IrType.F64, "larva_math_ceil"),
            new(55, "math.atan2", [IrType.F64, IrType.F64], IrType.F64, "larva_math_atan2"),
            new(56, "math.pow", [IrType.F64, IrType.F64], IrType.F64, "larva_math_pow"),
            new(57, "f64.bits", [IrType.F64], IrType.I64, "larva_f64_bits"),
            new(58, "f64.from_bits", [IrType.I64], IrType.F64, "larva_f64_from_bits"),
            // Text in UTF-8 (str is String<u8>): its bytes written as they are.
            new(59, "print.utf8", [Bytes], IrType.Unit, "larva_print_utf8"),
            // The calendar clock (System.DateTime): now in 100-nanosecond ticks since 1970-01-01 UTC; the local time zone's
            // offset from UTC in seconds (east positive) at a moment given in seconds since 1970 UTC; and a monotonic clock
            // in nanoseconds (System.Diagnostics.Stopwatch).
            new(60, "time.utc", [], IrType.I64, "larva_time_utc"),
            new(61, "time.offset", [IrType.I64], IrType.I64, "larva_time_offset"),
            new(62, "time.precise", [], IrType.I64, "larva_time_precise"),
            // Threads (System.Threading). `thread.start` runs a function value on a new thread and gives its handle;
            // `thread.join` waits for it (a timeout in milliseconds, -1 for none) and gives 1 when it has finished, 0 on a
            // timeout (a joined handle is freed); `thread.id` is the current thread's number (1: the first).
            new(63, "thread.start", [Callback, CallbackEnvironment], IrType.I64, "larva_thread_start"),
            new(64, "thread.join", [IrType.I64, IrType.I64], IrType.I64, "larva_thread_join"),
            new(65, "thread.id", [], IrType.I64, "larva_thread_id"),
            new(66, "thread.yield", [], IrType.Unit, "larva_thread_yield"),
            new(67, "cpu.count", [], IrType.I64, "larva_cpu_count"),
            // Locks and conditions, by handle: a lock is held by one thread at a time (not re-entrant); a condition
            // releases its lock while it waits (a timeout in milliseconds, -1 for none; 1 woken, 0 timed out).
            new(68, "mutex.new", [], IrType.I64, "larva_mutex_new"),
            new(69, "mutex.lock", [IrType.I64], IrType.Unit, "larva_mutex_lock"),
            new(70, "mutex.trylock", [IrType.I64], IrType.I64, "larva_mutex_trylock"),
            new(71, "mutex.unlock", [IrType.I64], IrType.Unit, "larva_mutex_unlock"),
            new(72, "mutex.free", [IrType.I64], IrType.Unit, "larva_mutex_free"),
            new(73, "cond.new", [], IrType.I64, "larva_cond_new"),
            new(74, "cond.wait", [IrType.I64, IrType.I64, IrType.I64], IrType.I64, "larva_cond_wait"),
            new(75, "cond.signal", [IrType.I64], IrType.Unit, "larva_cond_signal"),
            new(76, "cond.broadcast", [IrType.I64], IrType.Unit, "larva_cond_broadcast"),
            new(77, "cond.free", [IrType.I64], IrType.Unit, "larva_cond_free"),
            // Atomic operations on an i64 or i32 in memory (sequentially consistent): add, exchange and
            // compare-exchange give the value before.
            new(78, "atomic.load.i64", [I64Ptr], IrType.I64, "larva_atomic_load_i64"),
            new(79, "atomic.store.i64", [I64Ptr, IrType.I64], IrType.Unit, "larva_atomic_store_i64"),
            new(80, "atomic.add.i64", [I64Ptr, IrType.I64], IrType.I64, "larva_atomic_add_i64"),
            new(81, "atomic.xchg.i64", [I64Ptr, IrType.I64], IrType.I64, "larva_atomic_xchg_i64"),
            new(82, "atomic.cas.i64", [I64Ptr, IrType.I64, IrType.I64], IrType.I64, "larva_atomic_cas_i64"),
            new(83, "atomic.load.i32", [I32Ptr], IrType.I32, "larva_atomic_load_i32"),
            new(84, "atomic.store.i32", [I32Ptr, IrType.I32], IrType.Unit, "larva_atomic_store_i32"),
            new(85, "atomic.add.i32", [I32Ptr, IrType.I32], IrType.I32, "larva_atomic_add_i32"),
            new(86, "atomic.xchg.i32", [I32Ptr, IrType.I32], IrType.I32, "larva_atomic_xchg_i32"),
            new(87, "atomic.cas.i32", [I32Ptr, IrType.I32, IrType.I32], IrType.I32, "larva_atomic_cas_i32"),
            // mem.copy(target, source, count): `count` bytes moved (the ranges may overlap).
            new(88, "mem.copy", [IrType.PtrMut(IrType.U8), IrType.PtrMut(IrType.U8), IrType.I64], IrType.Unit, "larva_mem_copy"),
            // mem.cmp(a, b, count): the first `count` bytes compared as unsigned values: < 0, 0 or > 0 (as C's memcmp).
            new(89, "mem.cmp", [IrType.PtrMut(IrType.U8), IrType.PtrMut(IrType.U8), IrType.I64], IrType.I64, "larva_mem_cmp"),
            // gc.domain(object): the GC domain the gc object belongs to (its header's byte; 0 in the program's own domain).
            new(90, "gc.domain", [IrType.PtrMut(IrType.U8)], IrType.I64, "larva_gc_domain"),
            // TCP (System.Net): connections are stream handles (stream.read/write/close work on them; no seek, no length).
            // connect(host, port) and accept(listener) give a connection's handle, listen(port) a listener's (0: any port,
            // net.port says which); -1 when it fails.
            new(91, "net.connect", [Str, IrType.I64], IrType.I64, "larva_net_connect"),
            new(92, "net.listen", [IrType.I64], IrType.I64, "larva_net_listen"),
            new(93, "net.accept", [IrType.I64], IrType.I64, "larva_net_accept"),
            new(94, "net.port", [IrType.I64], IrType.I64, "larva_net_port"),
            // Processes (System.Diagnostics.Process): start(command, arguments) runs a program with its standard output
            // captured, as a stream handle to read it from (-1 when it cannot start); wait(handle) waits for it to end and
            // gives its exit code, closing the handle.
            new(95, "proc.start", [Str, Str], IrType.I64, "larva_proc_start"),
            new(96, "proc.wait", [IrType.I64], IrType.I64, "larva_proc_wait"),
        ];

        private static readonly Dictionary<string, Info> byName = All.ToDictionary(i => i.Name);

        public static Info? Find(string name) => byName.GetValueOrDefault(name);

        /// <summary>
        /// Whether a service takes arguments of these types: its parameters exactly, except that a <see cref="Callback"/>
        /// takes any <c>fn(env) -&gt; ()</c> and the <see cref="CallbackEnvironment"/> after it that function's env.
        /// </summary>
        public static bool Accepts(Info info, IrType[] arguments)
        {
            if (arguments.Length != info.Parameters.Length)
                return false;
            for (int i = 0; i < arguments.Length; i++)
            {
                var want = info.Parameters[i];
                if (ReferenceEquals(want, Callback))
                {
                    if (arguments[i] is not FunctionType { Parameters: [var environment], Result.IsUnit: true } || i + 1 >= arguments.Length || arguments[i + 1] != environment)
                        return false;
                    i++;
                    continue;
                }
                if (want != arguments[i])
                    return false;
            }
            return true;
        }

        /// <summary>Whether a program that calls this service runs more than one thread (every loop then polls).</summary>
        public const string ThreadStart = "thread.start";

        /// <summary>
        /// The attribute on an entry that reads its command line as text (<c>args.*</c>) and returns the process's exit
        /// code: executables pass it no numbers and do not print its result.
        /// </summary>
        public const string ProgramAttribute = "Larva.Program";

        /// <summary>Floats print with up to 15 significant digits, the same in the VM and native code.</summary>
        public const int FloatDigits = 15;
    }

    /// <summary><c>intrinsic "print.i64"(x)</c>: a call into the runtime (see <see cref="Intrinsics"/>).</summary>
    public sealed record IntrinsicRvalue(string Name, Operand[] Arguments) : Rvalue;
}

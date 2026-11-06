using System.Globalization;
using Larva.IR;

namespace Larva.Runtime.Vm
{
    public sealed partial class Interpreter
    {
        /// <summary><c>sys.intrinsic N</c>: the runtime operation N of <see cref="Intrinsics"/>, on the operand stack.</summary>
        private void RunIntrinsic(Frame frame, int id)
        {
            if ((id >= 19 && id < 43 || id >= 91 && id <= 96) && RunIoIntrinsic(frame, id))
                return;
            if (id >= 63 && id <= 87 && RunThreadIntrinsic(frame, id))
                return;
            var output = options.Output ?? Console.Out;
            var stack = frame.Stack;
            switch (id)
            {
                case 0:
                    output.Write(Heap.AsInteger(Heap.Coerce(Pop(stack), IrType.I64)).ToString(CultureInfo.InvariantCulture));
                    break;
                case 1:
                    output.Write(((ulong)Heap.AsInteger(Heap.Coerce(Pop(stack), IrType.U64))).ToString(CultureInfo.InvariantCulture));
                    break;
                case 2:
                    output.Write(FormatFloat(Heap.Coerce(Pop(stack), IrType.F64).Float));
                    break;
                case 3:
                    output.Write(Pop(stack).IsTrue ? "true" : "false");
                    break;
                case 4:
                {
                    var text = Heap.AsAddress(Pop(stack));
                    var (header, stride) = Heap.ArrayLayout(IrType.U16);
                    long length = (long)Heap.AsInteger(Heap.Read(IrType.U64, text));
                    var chars = new char[length];
                    for (long i = 0; i < length; i++)
                        chars[i] = (char)Heap.AsInteger(Heap.Read(IrType.U16, text.Plus(header + (int)i * stride)));
                    output.Write(chars);
                    break;
                }
                case 5:
                    output.Write('\n');
                    break;
                case 59:
                {
                    var text = Heap.AsAddress(Pop(stack));
                    var (header, stride) = Heap.ArrayLayout(IrType.U8);
                    long length = (long)Heap.AsInteger(Heap.Read(IrType.U64, text));
                    var bytes = new byte[length];
                    for (long i = 0; i < length; i++)
                        bytes[i] = (byte)Heap.AsInteger(Heap.Read(IrType.U8, text.Plus(header + (int)i * stride)));
                    output.Write(System.Text.Encoding.UTF8.GetString(bytes));
                    break;
                }
                case 6:
                    line = (options.Input ?? Console.In).ReadLine();
                    stack.Add(Value.FromInt(line?.Length ?? -1, IrType.I64));
                    break;
                case 7:
                {
                    long index = PopLong(stack);
                    stack.Add(Value.FromInt(line is not null && index >= 0 && index < line.Length ? line[(int)index] : 0, IrType.I64));
                    break;
                }
                case 8:
                    stack.Add(Value.FromInt(options.Arguments.Length, IrType.I64));
                    break;
                case 9:
                {
                    long index = PopLong(stack);
                    stack.Add(Value.FromInt(index >= 0 && index < options.Arguments.Length ? options.Arguments[index].Length : 0, IrType.I64));
                    break;
                }
                case 10:
                {
                    long at = PopLong(stack), index = PopLong(stack);
                    var arguments = options.Arguments;
                    stack.Add(Value.FromInt(index >= 0 && index < arguments.Length && at >= 0 && at < arguments[index].Length ? arguments[index][(int)at] : 0, IrType.I64));
                    break;
                }
                case 11:
                    formatted = FormatFloat(Heap.Coerce(Pop(stack), IrType.F64).Float);
                    stack.Add(Value.FromInt(formatted.Length, IrType.I64));
                    break;
                case 12:
                {
                    long index = PopLong(stack);
                    stack.Add(Value.FromInt(index >= 0 && index < formatted.Length ? formatted[(int)index] : 0, IrType.I64));
                    break;
                }
                case 13:
                    collectRequested = true;
                    break;
                case 14:
                    stack.Add(Value.FromInt(Stats.Collections, IrType.I64));
                    break;
                case 15:
                    stack.Add(Value.FromInt(Heap.Live.Count(a => a.Kind == AllocationKind.Gc), IrType.I64));
                    break;
                case 43:
                    stack.Add(Value.FromFloat(Math.Sqrt(PopDouble(stack)), IrType.F64));
                    break;
                case 44:
                    stack.Add(Value.FromFloat(Math.Sin(PopDouble(stack)), IrType.F64));
                    break;
                case 45:
                    stack.Add(Value.FromFloat(Math.Cos(PopDouble(stack)), IrType.F64));
                    break;
                case 46:
                    stack.Add(Value.FromFloat(Math.Tan(PopDouble(stack)), IrType.F64));
                    break;
                case 47:
                    stack.Add(Value.FromFloat(Math.Asin(PopDouble(stack)), IrType.F64));
                    break;
                case 48:
                    stack.Add(Value.FromFloat(Math.Acos(PopDouble(stack)), IrType.F64));
                    break;
                case 49:
                    stack.Add(Value.FromFloat(Math.Atan(PopDouble(stack)), IrType.F64));
                    break;
                case 50:
                    stack.Add(Value.FromFloat(Math.Exp(PopDouble(stack)), IrType.F64));
                    break;
                case 51:
                    stack.Add(Value.FromFloat(Math.Log(PopDouble(stack)), IrType.F64));
                    break;
                case 52:
                    stack.Add(Value.FromFloat(Math.Log10(PopDouble(stack)), IrType.F64));
                    break;
                case 53:
                    stack.Add(Value.FromFloat(Math.Floor(PopDouble(stack)), IrType.F64));
                    break;
                case 54:
                    stack.Add(Value.FromFloat(Math.Ceiling(PopDouble(stack)), IrType.F64));
                    break;
                case 55:
                {
                    double y = PopDouble(stack), x = PopDouble(stack);
                    stack.Add(Value.FromFloat(Math.Atan2(x, y), IrType.F64));
                    break;
                }
                case 56:
                {
                    double y = PopDouble(stack), x = PopDouble(stack);
                    stack.Add(Value.FromFloat(Math.Pow(x, y), IrType.F64));
                    break;
                }
                case 57:
                    stack.Add(Value.FromInt(BitConverter.DoubleToInt64Bits(PopDouble(stack)), IrType.I64));
                    break;
                case 58:
                    stack.Add(Value.FromFloat(BitConverter.Int64BitsToDouble(PopLong(stack)), IrType.F64));
                    break;
                case 60:
                    stack.Add(Value.FromInt(DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks, IrType.I64));
                    break;
                case 61:
                    stack.Add(Value.FromInt(Larva.IR.TimeZones.OffsetSeconds(PopLong(stack)), IrType.I64));
                    break;
                case 62:
                    stack.Add(Value.FromInt(Larva.IR.TimeZones.PreciseNanoseconds(), IrType.I64));
                    break;
                case 34:
                    stack.Add(Value.FromInt(System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency, IrType.I64));
                    break;
                case 35:
                {
                    long milliseconds = PopLong(stack);
                    if (milliseconds > 0)
                        Blocking(() => { Thread.Sleep(TimeSpan.FromMilliseconds(milliseconds)); return 0; });
                    break;
                }
                case 33:
                    // The program's own collector: the interpreter keeps its own heap.
                    Pop(stack);
                    Pop(stack);
                    Pop(stack);
                    break;
                case 16:
                    stack.Add(Value.FromPointer(Heap.AllocateNative(PopLong(stack)), IrType.PtrMut(IrType.U8)));
                    break;
                case 17:
                    Heap.Free(Heap.AsAddress(Pop(stack)), "mem.free");
                    break;
                case 88:
                {
                    long count = PopLong(stack);
                    var source = Heap.AsAddress(Pop(stack));
                    var target = Heap.AsAddress(Pop(stack));
                    if (count > 0)
                    {
                        // Through a copy: the ranges may overlap.
                        var moved = new Value[count];
                        for (long i = 0; i < count; i++)
                            moved[i] = Heap.Read(IrType.U8, source.Plus(i));
                        for (long i = 0; i < count; i++)
                            Heap.Write(IrType.U8, target.Plus(i), moved[i]);
                    }
                    break;
                }
                case 90:
                {
                    var handle = Pop(stack);
                    stack.Add(Value.FromInt(handle.Kind == ValueKind.Pointer ? Heap.Get(handle.Address, "gc.domain").Domain : 0, IrType.I64));
                    break;
                }
                case 89:
                {
                    long count = PopLong(stack);
                    var right = Heap.AsAddress(Pop(stack));
                    var left = Heap.AsAddress(Pop(stack));
                    long order = 0;
                    for (long i = 0; i < count && order == 0; i++)
                        order = (long)Heap.Read(IrType.U8, left.Plus(i)).Int - (long)Heap.Read(IrType.U8, right.Plus(i)).Int;
                    stack.Add(Value.FromInt(order, IrType.I64));
                    break;
                }
                case 18:
                {
                    string name = TextOf(Pop(stack)), library = TextOf(Pop(stack));
                    var handle = System.Runtime.InteropServices.NativeLibrary.Load(library);
                    if (!System.Runtime.InteropServices.NativeLibrary.TryGetExport(handle, name, out var address))
                        throw new VmException($"`{name}` not found in `{library}`");
                    stack.Add(Value.FromInt((ulong)address, IrType.U64));
                    break;
                }
                default:
                    throw new VmException($"unknown intrinsic {id}");
            }
        }

        private string? line;

        // System.IO: the file read last, a folder's entries, an environment variable.
        private byte[] fileBuffer = [];

        /// <summary>Open streams (stream.*): standard output goes where the interpreter prints.</summary>
        private HostStreams Streams => streams ??= new HostStreams(() => options.Input ?? Console.In, () => options.Output ?? Console.Out, () => Console.Error);
        private HostStreams? streams;
        private string[] folderEntries = [];
        private string? environmentValue;

        /// <summary>A <c>str</c>'s text, from its units in memory.</summary>
        private string TextAt(Address text)
        {
            var (header, stride) = Heap.ArrayLayout(IrType.U16);
            long length = (long)Heap.AsInteger(Heap.Read(IrType.U64, text));
            var chars = new char[length];
            for (long i = 0; i < length; i++)
                chars[i] = (char)Heap.AsInteger(Heap.Read(IrType.U16, text.Plus(header + (int)i * stride)));
            return new string(chars);
        }

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

        /// <summary>The System.IO operations (ids 19 to 31; see <c>Intrinsics</c>).</summary>
        private bool RunIoIntrinsic(Frame frame, int id)
        {
            var stack = frame.Stack;
            switch (id)
            {
                case 19:
                {
                    string path = TextAt(Heap.AsAddress(Pop(stack)));
                    try
                    {
                        fileBuffer = File.ReadAllBytes(path);
                        stack.Add(Value.FromInt(fileBuffer.Length, IrType.I64));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        fileBuffer = [];
                        stack.Add(Value.FromInt(-1, IrType.I64));
                    }
                    return true;
                }
                case 20:
                {
                    var target = Heap.AsAddress(Pop(stack));
                    var (header, stride) = Heap.ArrayLayout(IrType.U8);
                    long length = (long)Heap.AsInteger(Heap.Read(IrType.U64, target));
                    for (long i = 0; i < Math.Min(length, fileBuffer.Length); i++)
                        Heap.Write(IrType.U8, target.Plus(header + (int)i * stride), Value.FromInt(fileBuffer[i], IrType.U8));
                    return true;
                }
                case 21:
                {
                    bool append = PopLong(stack) != 0;
                    var source = Heap.AsAddress(Pop(stack));
                    string path = TextAt(Heap.AsAddress(Pop(stack)));
                    var (header, stride) = Heap.ArrayLayout(IrType.U8);
                    long length = (long)Heap.AsInteger(Heap.Read(IrType.U64, source));
                    var data = new byte[length];
                    for (long i = 0; i < length; i++)
                        data[i] = (byte)Heap.AsInteger(Heap.Read(IrType.U8, source.Plus(header + (int)i * stride)));
                    stack.Add(Value.FromInt(Status(() =>
                    {
                        using var file = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write);
                        file.Write(data);
                    }), IrType.I64));
                    return true;
                }
                case 22:
                {
                    string path = TextAt(Heap.AsAddress(Pop(stack)));
                    stack.Add(Value.FromInt(File.Exists(path) ? 1 : Directory.Exists(path) ? 2 : 0, IrType.I64));
                    return true;
                }
                case 23:
                {
                    string path = TextAt(Heap.AsAddress(Pop(stack)));
                    stack.Add(Value.FromInt(Status(() =>
                    {
                        if (Directory.Exists(path))
                            Directory.Delete(path);
                        else if (File.Exists(path))
                            File.Delete(path);
                        else
                            throw new IOException("missing");
                    }), IrType.I64));
                    return true;
                }
                case 24:
                {
                    string path = TextAt(Heap.AsAddress(Pop(stack)));
                    long time = File.Exists(path) || Directory.Exists(path)
                        ? new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds() : -1;
                    stack.Add(Value.FromInt(time, IrType.I64));
                    return true;
                }
                case 25:
                {
                    string path = TextAt(Heap.AsAddress(Pop(stack)));
                    stack.Add(Value.FromInt(Status(() => Directory.CreateDirectory(path)), IrType.I64));
                    return true;
                }
                case 26:
                {
                    string path = TextAt(Heap.AsAddress(Pop(stack)));
                    try
                    {
                        folderEntries = [.. Directory.EnumerateFileSystemEntries(path).Select(e => Path.GetFileName(e)).Order(StringComparer.Ordinal)];
                        stack.Add(Value.FromInt(folderEntries.Length, IrType.I64));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        folderEntries = [];
                        stack.Add(Value.FromInt(-1, IrType.I64));
                    }
                    return true;
                }
                case 27:
                {
                    long index = PopLong(stack);
                    stack.Add(Value.FromInt(index >= 0 && index < folderEntries.Length ? folderEntries[index].Length : 0, IrType.I64));
                    return true;
                }
                case 28:
                {
                    long at = PopLong(stack), index = PopLong(stack);
                    stack.Add(Value.FromInt(index >= 0 && index < folderEntries.Length && at >= 0 && at < folderEntries[index].Length
                        ? folderEntries[index][(int)at] : 0, IrType.I64));
                    return true;
                }
                case 29:
                {
                    environmentValue = Environment.GetEnvironmentVariable(TextAt(Heap.AsAddress(Pop(stack))));
                    stack.Add(Value.FromInt(environmentValue?.Length ?? -1, IrType.I64));
                    return true;
                }
                case 30:
                {
                    long index = PopLong(stack);
                    stack.Add(Value.FromInt(environmentValue is not null && index >= 0 && index < environmentValue.Length ? environmentValue[(int)index] : 0, IrType.I64));
                    return true;
                }
                case 32:
                {
                    string text = TextAt(Heap.AsAddress(Pop(stack)));
                    double value = double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                        ? parsed : double.NaN;
                    stack.Add(Value.FromInt(BitConverter.DoubleToInt64Bits(value), IrType.I64));
                    return true;
                }
                case 36:
                {
                    long mode = PopLong(stack);
                    string path = TextAt(Heap.AsAddress(Pop(stack)));
                    stack.Add(Value.FromInt(Streams.Open(path, mode), IrType.I64));
                    return true;
                }
                case 37:
                    stack.Add(Value.FromInt(Streams.Close(PopLong(stack)), IrType.I64));
                    return true;
                case 91:
                {
                    long port = PopLong(stack);
                    string host = TextAt(Heap.AsAddress(Pop(stack)));
                    stack.Add(Value.FromInt(Blocking(() => Streams.Connect(host, port)), IrType.I64));
                    return true;
                }
                case 92:
                    stack.Add(Value.FromInt(Streams.Listen(PopLong(stack)), IrType.I64));
                    return true;
                case 93:
                {
                    long listener = PopLong(stack);
                    stack.Add(Value.FromInt(Blocking(() => Streams.Accept(listener)), IrType.I64));
                    return true;
                }
                case 94:
                    stack.Add(Value.FromInt(Streams.Port(PopLong(stack)), IrType.I64));
                    return true;
                case 95:
                {
                    string arguments = TextAt(Heap.AsAddress(Pop(stack)));
                    string command = TextAt(Heap.AsAddress(Pop(stack)));
                    stack.Add(Value.FromInt(Streams.Start(command, arguments), IrType.I64));
                    return true;
                }
                case 96:
                {
                    long handle = PopLong(stack);
                    stack.Add(Value.FromInt(Blocking(() => Streams.Wait(handle)), IrType.I64));
                    return true;
                }
                case 38 or 39:
                {
                    long count = PopLong(stack), offset = PopLong(stack);
                    var array = Heap.AsAddress(Pop(stack));
                    long handle = PopLong(stack);
                    var (header, stride) = Heap.ArrayLayout(IrType.U8);
                    long length = (long)Heap.AsInteger(Heap.Read(IrType.U64, array));
                    if (offset < 0 || count < 0 || offset + count > length)
                    {
                        stack.Add(Value.FromInt(-1, IrType.I64));
                        return true;
                    }
                    var buffer = new byte[count];
                    long moved;
                    if (id == 38)
                    {
                        // A connection's read waits for the network: other threads of the program run meanwhile.
                        moved = Streams.IsSocket(handle) ? Blocking(() => Streams.Read(handle, buffer)) : Streams.Read(handle, buffer);
                        for (long i = 0; i < moved; i++)
                            Heap.Write(IrType.U8, array.Plus(header + (int)(offset + i) * stride), Value.FromInt(buffer[i], IrType.U8));
                    }
                    else
                    {
                        for (long i = 0; i < count; i++)
                            buffer[i] = (byte)Heap.AsInteger(Heap.Read(IrType.U8, array.Plus(header + (int)(offset + i) * stride)));
                        moved = Streams.Write(handle, buffer);
                    }
                    stack.Add(Value.FromInt(moved, IrType.I64));
                    return true;
                }
                case 40:
                {
                    long origin = PopLong(stack), offset = PopLong(stack), handle = PopLong(stack);
                    stack.Add(Value.FromInt(Streams.Seek(handle, offset, origin), IrType.I64));
                    return true;
                }
                case 41:
                    stack.Add(Value.FromInt(Streams.Length(PopLong(stack)), IrType.I64));
                    return true;
                case 42:
                    stack.Add(Value.FromInt(Streams.Flush(PopLong(stack)), IrType.I64));
                    return true;
                case 31:
                {
                    string command = TextAt(Heap.AsAddress(Pop(stack)));
                    (options.Output ?? Console.Out).Flush();
                    stack.Add(Value.FromInt(RunCommand(command), IrType.I64));
                    return true;
                }
            }
            return false;
        }

        /// <summary>Runs a command line through the system's shell; its exit code (-1 when it cannot start).</summary>
        internal static int RunCommand(string command)
        {
            try
            {
                var start = OperatingSystem.IsWindows()
                    ? new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/d", "/s", "/c", command])
                    : new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", command]);
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

        /// <summary>A str argument (its length, then UTF-16 units) as text.</summary>
        private string TextOf(Value value)
        {
            var text = Heap.AsAddress(value);
            var (header, stride) = Heap.ArrayLayout(IrType.U16);
            long length = (long)Heap.AsInteger(Heap.Read(IrType.U64, text));
            var chars = new char[length];
            for (long i = 0; i < length; i++)
                chars[i] = (char)Heap.AsInteger(Heap.Read(IrType.U16, text.Plus(header + (int)i * stride)));
            return new string(chars);
        }

        /// <summary><c>GC.Collect()</c> asked for a collection: the next safepoint runs one.</summary>
        private bool collectRequested;
        private string formatted = "";

        private long PopLong(List<Value> stack) => (long)Heap.AsInteger(Heap.Coerce(Pop(stack), IrType.I64));

        private double PopDouble(List<Value> stack) => Heap.Coerce(Pop(stack), IrType.F64).Float;

        /// <summary>Up to 15 significant digits, written like C's <c>%.15g</c> (what native code prints).</summary>
        internal static string FormatFloat(double value)
        {
            if (double.IsNaN(value))
                return "nan";
            if (double.IsInfinity(value))
                return value > 0 ? "inf" : "-inf";
            return value.ToString("G" + Intrinsics.FloatDigits, CultureInfo.InvariantCulture).Replace("E+", "e+").Replace("E-", "e-");
        }
    }
}

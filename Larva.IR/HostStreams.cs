using System.Text;

namespace Larva.IR
{
    /// <summary>
    /// The stream intrinsics (<c>stream.*</c>, see <see cref="Intrinsics"/>) for the runtimes that run on .NET (the VM and
    /// the in-process native runtime): open files by handle, 0, 1 and 2 being standard input, output and error. Standard
    /// output and error go to text writers (so a host can capture them), decoding UTF-8 as it comes; standard input is read
    /// from a text reader, encoded as UTF-8.
    /// </summary>
#if LARVA_NATIVE
    internal
#else
    public
#endif
    sealed class HostStreams(Func<TextReader> input, Func<TextWriter> output, Func<TextWriter> error)
    {
        private readonly Dictionary<long, Stream> files = [];
        private readonly Dictionary<long, System.Net.Sockets.TcpListener> listeners = [];
        private long next = 3;
        // Threads of a program open, read and close handles at once (the waits on the network run without the program's
        // turn): the tables are touched under this lock, the waits outside it.
        private readonly object sync = new();

        private bool Lookup(long handle, out Stream stream)
        {
            lock (sync)
                return files.TryGetValue(handle, out stream!);
        }

        private long Register(Stream stream)
        {
            lock (sync)
            {
                files[next] = stream;
                return next++;
            }
        }

        private readonly Dictionary<long, System.Diagnostics.Process> processes = [];

        /// <summary>Whether a handle's reads wait on something outside (a connection, a listener, a program's output).</summary>
        public bool IsSocket(long handle)
        {
            lock (sync)
                return Lookup(handle, out var stream) ? stream is System.Net.Sockets.NetworkStream || processes.ContainsKey(handle) : listeners.ContainsKey(handle);
        }

        /// <summary>Starts a program with its standard output captured: the handle to read it from, or -1.</summary>
        public long Start(string command, string arguments)
        {
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo(command, arguments) { UseShellExecute = false, RedirectStandardOutput = true };
                var process = System.Diagnostics.Process.Start(info);
                if (process is null)
                    return -1;
                lock (sync)
                {
                    processes[next] = process;
                    files[next] = process.StandardOutput.BaseStream;
                    return next++;
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or ArgumentException)
            {
                return -1;
            }
        }

        /// <summary>Waits for a started program: its exit code (-1 when it is no program); the handle is closed.</summary>
        public long Wait(long handle)
        {
            System.Diagnostics.Process? process;
            Stream? stream;
            lock (sync)
            {
                if (!processes.Remove(handle, out process))
                    return -1;
                files.Remove(handle, out stream);
            }
            process.WaitForExit();
            long code = process.ExitCode;
            stream?.Dispose();
            process.Dispose();
            return code;
        }

        /// <summary>A TCP connection to <paramref name="host"/> at <paramref name="port"/>: its handle, or -1.</summary>
        public long Connect(string host, long port)
        {
            try
            {
                var client = new System.Net.Sockets.TcpClient();
                client.NoDelay = true;
                client.Connect(host, (int)port);
                return Register(client.GetStream());
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException or ArgumentException or InvalidOperationException)
            {
                return -1;
            }
        }

        /// <summary>A listener on <paramref name="port"/> (0: any): its handle, or -1.</summary>
        public long Listen(long port)
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, (int)port);
                listener.Start(16);
                lock (sync)
                {
                    listeners[next] = listener;
                    return next++;
                }
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or ArgumentException)
            {
                return -1;
            }
        }

        /// <summary>Waits for a connection on a listener: its handle, or -1.</summary>
        public long Accept(long handle)
        {
            System.Net.Sockets.TcpListener? listener;
            lock (sync)
            {
                if (!listeners.TryGetValue(handle, out listener))
                    return -1;
            }
            try
            {
                var socket = listener.AcceptSocket();
                socket.NoDelay = true;
                return Register(new System.Net.Sockets.NetworkStream(socket, ownsSocket: true));
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return -1;
            }
        }

        /// <summary>The local port of a listener or a connection, or -1.</summary>
        public long Port(long handle)
        {
            lock (sync)
            {
                if (listeners.TryGetValue(handle, out var listener))
                    return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                if (Lookup(handle, out var stream) && stream is System.Net.Sockets.NetworkStream network)
                    return ((System.Net.IPEndPoint?)network.Socket.LocalEndPoint)?.Port ?? -1;
                return -1;
            }
        }
        private readonly Decoder outputDecoder = new UTF8Encoding(false).GetDecoder();
        private readonly Decoder errorDecoder = new UTF8Encoding(false).GetDecoder();
        private byte[] pendingInput = [];
        private int pendingAt;

        /// <summary>Mode bits: 1 read, 2 write, 4 create, 8 truncate, 16 append, 32 only a new file. A handle, or -1.</summary>
        public long Open(string path, long mode)
        {
            bool read = (mode & 1) != 0, write = (mode & 2) != 0 || (mode & 16) != 0;
            bool exists = File.Exists(path);
            if ((mode & 32) != 0 && exists || !exists && (mode & 4) == 0)
                return -1;
            var fileMode = (mode & 16) != 0 ? FileMode.Append
                : (mode & 8) != 0 ? (exists ? FileMode.Truncate : FileMode.Create)
                : exists ? FileMode.Open : FileMode.CreateNew;
            var access = read && write ? FileAccess.ReadWrite : write ? FileAccess.Write : FileAccess.Read;
            if (fileMode == FileMode.Append && read)
            {
                fileMode = FileMode.OpenOrCreate;
                access = FileAccess.ReadWrite;
            }
            try
            {
                var stream = new FileStream(path, fileMode, access, FileShare.ReadWrite);
                if ((mode & 16) != 0)
                    stream.Seek(0, SeekOrigin.End);
                return Register(stream);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return -1;
            }
        }

        public long Close(long handle)
        {
            if (handle < 3)
                return 0;
            System.Net.Sockets.TcpListener? listener;
            System.Diagnostics.Process? process;
            Stream? stream;
            lock (sync)
            {
                if (listeners.Remove(handle, out listener))
                {
                    listener.Stop();
                    return 0;
                }
                processes.Remove(handle, out process);
                if (!files.Remove(handle, out stream))
                    return -1;
            }
            process?.Dispose();
            stream.Dispose();
            return 0;
        }

        /// <summary>Reads up to <paramref name="target"/>'s length: how many (0 at the end), or -1.</summary>
        public long Read(long handle, Span<byte> target)
        {
            if (handle == 0)
            {
                output().Flush();
                int count = 0;
                while (count < target.Length)
                {
                    if (pendingAt >= pendingInput.Length)
                    {
                        // A character at a time, as UTF-8 (a pair of surrogates together).
                        int c = input().Read();
                        if (c < 0)
                            break;
                        string text = char.IsHighSurrogate((char)c) && input().Peek() >= 0 ? new string([(char)c, (char)input().Read()]) : ((char)c).ToString();
                        pendingInput = System.Text.Encoding.UTF8.GetBytes(text);
                        pendingAt = 0;
                    }
                    target[count++] = pendingInput[pendingAt++];
                    if (pendingAt >= pendingInput.Length && target[count - 1] == (byte)'\n')
                        break; // a line at a time, as a console gives it
                }
                return count;
            }
            if (!Lookup(handle, out var stream) || !stream.CanRead)
                return -1;
            try
            {
                return stream.Read(target);
            }
            catch (IOException)
            {
                return -1;
            }
        }

        /// <summary>Writes <paramref name="source"/>: how many bytes, or -1.</summary>
        public long Write(long handle, ReadOnlySpan<byte> source)
        {
            if (handle is 1 or 2)
            {
                var decoder = handle == 1 ? outputDecoder : errorDecoder;
                var chars = new char[decoder.GetCharCount(source, flush: false)];
                decoder.GetChars(source, chars, flush: false);
                (handle == 1 ? output() : error()).Write(chars);
                return source.Length;
            }
            if (!Lookup(handle, out var stream) || !stream.CanWrite)
                return -1;
            try
            {
                stream.Write(source);
                return source.Length;
            }
            catch (IOException)
            {
                return -1;
            }
        }

        /// <summary>Moves to <paramref name="offset"/> from the start (0), the position (1) or the end (2): the new position, or -1.</summary>
        public long Seek(long handle, long offset, long origin)
        {
            if (!Lookup(handle, out var stream) || !stream.CanSeek)
                return -1;
            try
            {
                return stream.Seek(offset, origin == 0 ? SeekOrigin.Begin : origin == 1 ? SeekOrigin.Current : SeekOrigin.End);
            }
            catch (IOException)
            {
                return -1;
            }
        }

        public long Length(long handle) => Lookup(handle, out var stream) && stream.CanSeek ? stream.Length : -1;

        public long Flush(long handle)
        {
            if (handle is 1 or 2)
            {
                (handle == 1 ? output() : error()).Flush();
                return 0;
            }
            if (handle == 0)
                return 0;
            if (!Lookup(handle, out var stream))
                return -1;
            stream.Flush();
            return 0;
        }

        /// <summary>Closes every file still open (when the program ends).</summary>
        public void CloseAll()
        {
            foreach (var stream in files.Values)
                stream.Dispose();
            files.Clear();
            next = 3;
        }
    }
}

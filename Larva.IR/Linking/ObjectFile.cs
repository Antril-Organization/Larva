using System.IO.Compression;
using Larva.IL.Encoding;
using Larva.IR.Encoding;

namespace Larva.IR.Linking
{
    /// <summary>A verified module waiting to be linked, with the GC mode it was checked for.</summary>
    public sealed record LarvaObject(IrModule Module, GcConfig Gc)
    {
        /// <summary>
        /// Source declarations (no bodies) a front end compiles other code against, one per file: what makes the object a
        /// library for its language. Empty for a plain object.
        /// </summary>
        public IReadOnlyList<DeclarationFile> Declarations { get; init; } = [];

        /// <summary>Modules this one needs at link time (its imports), by name: <c>System.lvo</c> for <c>System</c>.</summary>
        public IReadOnlyList<string> Dependencies { get; init; } = [];

        /// <summary>
        /// When the module was also built as a native library (a DLL): the ids its code reads from slots, in slot order
        /// (<c>kind type</c>, see <see cref="NativeImages"/>). Programs that load the DLL fill them in.
        /// </summary>
        public IReadOnlyList<string>? NativeIds { get; init; }
    }

    public sealed record DeclarationFile(string Path, string Text);

    /// <summary>
    /// The on-disk form of a <see cref="LarvaObject"/> (<c>.lvo</c>): verified IR, so the linker can instantiate generics
    /// across modules and re-check signatures, plus the declarations other code compiles against.
    /// <code>
    /// file    = "LRVO" version:byte flags:byte payload            (flags bit 0: payload is Brotli-compressed)
    /// payload = gcMode:4 capabilities:uv plugin:name module         (module: IrEncoder format)
    ///           count:uv { path:name text:name }  count:uv { dependency:name }  [native:uv count:uv { id:name }]
    /// </code>
    /// Version 1 (no flags, declarations or dependencies) is still read.
    /// </summary>
    public static class ObjectFile
    {
        public const string Extension = ".lvo";
        public const byte Version = 3;
        private const byte Compressed = 1;
        private static readonly byte[] Magic = "LRVO"u8.ToArray();

        public static byte[] Write(LarvaObject obj, bool compress = true)
        {
            NibbleWriter writer = new();
            writer.WriteNibble((int)obj.Gc.Mode);
            writer.WriteUnsigned((ulong)obj.Gc.Capabilities);
            IrEncoder.WriteName(writer, obj.Gc.PluginName ?? "");
            IrEncoder.Encode(writer, obj.Module);
            writer.WriteUnsigned((ulong)obj.Declarations.Count);
            foreach (var file in obj.Declarations)
            {
                IrEncoder.WriteName(writer, file.Path);
                IrEncoder.WriteName(writer, file.Text);
            }
            writer.WriteUnsigned((ulong)obj.Dependencies.Count);
            foreach (string dependency in obj.Dependencies)
                IrEncoder.WriteName(writer, dependency);
            if (obj.NativeIds is { } ids)
            {
                writer.WriteUnsigned(1);
                writer.WriteUnsigned((ulong)ids.Count);
                foreach (string id in ids)
                    IrEncoder.WriteName(writer, id);
            }
            byte[] payload = writer.ToArray();
            // Libraries are shipped: compressed when that makes them smaller (Brotli at its densest).
            if (compress && Brotli(payload) is { } packed && packed.Length < payload.Length)
                return [.. Magic, Version, Compressed, .. packed];
            return [.. Magic, Version, 0, .. payload];
        }

        private static byte[]? Brotli(byte[] data)
        {
            var output = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length)];
            return BrotliEncoder.TryCompress(data, output, out int written, quality: 11, window: 24) ? output[..written] : null;
        }

        public static LarvaObject Read(ReadOnlySpan<byte> file)
        {
            if (file.Length < Magic.Length + 1 || !file[..Magic.Length].SequenceEqual(Magic))
                throw new InvalidDataException("not a Larva object (missing LRVO header)");
            byte version = file[Magic.Length];
            if (version is not (1 or 2 or Version))
                throw new InvalidDataException($"unsupported object version {version} (expected {Version})");
            var payload = file[(Magic.Length + 1)..];
            byte[]? unpacked = null;
            if (version >= 2)
            {
                if (payload.Length < 1 || payload[0] > Compressed)
                    throw new InvalidDataException("corrupt Larva object (bad flags)");
                bool compressed = payload[0] == Compressed;
                payload = payload[1..];
                if (compressed)
                {
                    using var input = new BrotliStream(new MemoryStream(payload.ToArray()), CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    input.CopyTo(output);
                    unpacked = output.ToArray();
                    payload = unpacked;
                }
            }

            NibbleReader reader = new(payload);
            var mode = (GcMode)reader.ReadNibble();
            var capabilities = (GcCapabilities)reader.ReadUnsigned();
            string plugin = IrDecoder.ReadName(ref reader);
            var module = IrDecoder.Decode(ref reader, attributes: version >= 3);
            var obj = new LarvaObject(module, new GcConfig(mode, capabilities, plugin.Length == 0 ? null : plugin));
            if (version == 1)
                return obj;
            var declarations = new DeclarationFile[IrDecoder.ReadCount(ref reader)];
            for (int i = 0; i < declarations.Length; i++)
                declarations[i] = new DeclarationFile(IrDecoder.ReadName(ref reader), IrDecoder.ReadName(ref reader));
            var dependencies = new string[IrDecoder.ReadCount(ref reader)];
            for (int i = 0; i < dependencies.Length; i++)
                dependencies[i] = IrDecoder.ReadName(ref reader);
            string[]? nativeIds = null;
            if (reader.Length - reader.Position >= 2 && reader.ReadUnsigned() == 1)
            {
                nativeIds = new string[IrDecoder.ReadCount(ref reader)];
                for (int i = 0; i < nativeIds.Length; i++)
                    nativeIds[i] = IrDecoder.ReadName(ref reader);
            }
            return obj with { Declarations = declarations, Dependencies = dependencies, NativeIds = nativeIds };
        }

        public static void Save(LarvaObject obj, string path) => File.WriteAllBytes(path, Write(obj));

        public static LarvaObject Load(string path) => Read(File.ReadAllBytes(path));

        /// <summary>The modules <paramref name="module"/> imports from (the <c>module</c> of each <c>module::name</c> symbol).</summary>
        public static string[] ImportedModules(IrModule module) =>
            [.. module.Structs.Where(s => s.Linkage == Linkage.Import).Select(s => s.Symbol)
                .Concat(module.Functions.Where(f => f.Linkage == Linkage.Import).Select(f => f.Symbol))
                .Concat(module.Globals.Where(g => g.Linkage == Linkage.Import).Select(g => g.Symbol))
                .OfType<string>().Select(s => s[..s.IndexOf("::", StringComparison.Ordinal)]).Distinct().Order(StringComparer.Ordinal)];
    }
}

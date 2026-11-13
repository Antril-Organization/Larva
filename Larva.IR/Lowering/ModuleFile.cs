using Larva.IL;
using Larva.IL.Encoding;
using Larva.IR.Encoding;
using Larva.IR.Reflection;

namespace Larva.IR.Lowering
{
    /// <summary>
    /// The on-disk form of a compiled module (<c>.lvm</c>): everything a runtime needs, nothing from the compiler.
    /// <code>
    /// file      = "LRVA" version:byte body
    /// body      = gc entry:sv types fields constants witnesses functions metadata      (nibble stream)
    /// gc        = mode:4 capabilities:uv plugin:name
    /// types     = n:uv type*                       fields    = n:uv {owner:type field:uv}*
    /// constants = n:uv {type bits:sv}*             witnesses = n:uv {type:uv layout}*
    /// layout    = size:uv align:uv flags:4 gc:uv offset:uv*
    /// function  = name args:uv witnesses:uv argTypes localTypes roots safepoints handlers body:bytes
    /// roots     = n:uv {isArg:4 slot:uv hasOffsets:4 [offsets]}*
    /// safepoint = op:uv roots            handler = start:uv end:uv hasType:4 [type:uv] target:uv
    /// metadata  = bytes (see MetadataEncoding)           bytes = n:uv byte*
    /// </code>
    /// </summary>
    public static class ModuleFile
    {
        public const string Extension = ".lvm";
        public const byte Version = 1;
        private static readonly byte[] Magic = "LRVA"u8.ToArray();

        public static byte[] Write(IlModule module)
        {
            NibbleWriter writer = new();

            writer.WriteNibble((int)module.Gc.Mode);
            writer.WriteUnsigned((ulong)module.Gc.Capabilities);
            IrEncoder.WriteName(writer, module.Gc.PluginName ?? "");
            writer.WriteSigned(module.Entry);

            writer.WriteUnsigned((ulong)module.Types.Length);
            foreach (var type in module.Types)
                IrEncoder.WriteType(writer, type);

            writer.WriteUnsigned((ulong)module.Fields.Length);
            foreach (var field in module.Fields)
            {
                IrEncoder.WriteType(writer, field.Owner);
                writer.WriteUnsigned((ulong)field.Field);
            }

            writer.WriteUnsigned((ulong)module.Constants.Length);
            foreach (var constant in module.Constants)
            {
                IrEncoder.WriteType(writer, constant.Type);
                writer.WriteSigned(constant.Bits);
            }

            writer.WriteUnsigned((ulong)module.Witnesses.Length);
            foreach (var witness in module.Witnesses)
            {
                writer.WriteUnsigned((ulong)witness.Type);
                WriteLayout(writer, witness.Layout);
            }

            writer.WriteUnsigned((ulong)module.Functions.Length);
            foreach (var function in module.Functions)
                WriteFunction(writer, function);

            WriteBytes(writer, MetadataEncoding.Encode(module.Metadata));
            writer.WriteUnsigned((ulong)module.Data.Length);
            foreach (var data in module.Data)
                IrEncoder.WriteStaticData(writer, data);
            writer.WriteUnsigned((ulong)module.Globals.Length);
            foreach (var global in module.Globals)
                IrEncoder.WriteGlobal(writer, global);
            return [.. Magic, Version, .. writer.ToArray()];
        }

        /// <summary>Reads a module file; a damaged one is one <see cref="InvalidDataException"/> naming the problem.</summary>
        public static IlModule Read(ReadOnlySpan<byte> file)
        {
            try
            {
                var module = ReadChecked(file);
                Validate(module);
                return module;
            }
            catch (Exception e) when (IrDecoder.IsIncidental(e))
            {
                throw IrDecoder.Corrupt(e);
            }
        }

        /// <summary>
        /// Checks every token and index the module holds against its tables (types, fields, constants, functions, locals,
        /// arguments, labels, handlers, safepoints, roots, the entry), so the VM never runs off a table of a damaged file.
        /// </summary>
        public static void Validate(IlModule module)
        {
            static int Index(int id, int count, string what) =>
                id >= 0 && id < count ? id : throw new InvalidDataException($"{what} {id} is out of range (the module has {count}).");
            int types = module.Types.Length, structs = module.Metadata.Structs.Length;
            // Struct ids inside IL types index the metadata's structs (the runtime's shapes).
            var ids = new Linking.IdMap(s => Index(s, structs, "struct"), f => f, g => g, d => d);
            foreach (var type in module.Types)
                ids.Type(type);
            foreach (var field in module.Fields)
                ids.Type(field.Owner);
            foreach (var constant in module.Constants)
                ids.Type(constant.Type);
            foreach (var witness in module.Witnesses)
                Index(witness.Type, types, "type");
            foreach (var data in module.Data)
                ids.Type(data.Element);
            foreach (var global in module.Globals)
                ids.Global(global);
            if (module.Entry != -1)
                Index(module.Entry, module.Functions.Length, "entry function");
            foreach (var function in module.Functions)
            {
                string name = $"`{function.Name}`:";
                if (function.ArgumentTypes.Length != function.ArgumentCount)
                    throw new InvalidDataException($"{name} {function.ArgumentCount} arguments but {function.ArgumentTypes.Length} argument types.");
                foreach (int t in function.ArgumentTypes)
                    Index(t, types, $"{name} type");
                foreach (int t in function.LocalTypes)
                    Index(t, types, $"{name} type");
                var operations = function.Body.Operations;
                foreach (var handler in function.Handlers)
                {
                    Index(handler.Start, operations.Length + 1, $"{name} handler start");
                    Index(handler.End, operations.Length + 1, $"{name} handler end");
                    Index(handler.Handler, operations.Length, $"{name} handler target");
                    if (handler.CatchType is int caught)
                        Index(caught, types, $"{name} type");
                }
                foreach (var root in function.GcRoots)
                    Index(root.Slot, root.IsArgument ? function.ArgumentCount : function.LocalTypes.Length, $"{name} root slot");
                foreach (var safepoint in function.Safepoints)
                {
                    Index(safepoint.Operation, operations.Length, $"{name} safepoint");
                    foreach (var root in safepoint.Roots)
                        Index(root.Slot, root.IsArgument ? function.ArgumentCount : function.LocalTypes.Length, $"{name} root slot");
                }
                foreach (var operation in operations)
                {
                    var info = OpCodes.Get(operation.OpCode);
                    var shape = info.IsFamily && info.TryGetSubOp(operation.SubOp, out var sub) ? sub.Operands : info.Operands;
                    for (int i = 0, o = 0; i < shape.Length && o < operation.Operands.Length; i++)
                    {
                        if (shape[i] == OperandKind.LabelTable)
                        {
                            for (; o < operation.Operands.Length; o++)
                                Index(checked((int)operation.Operands[o]), operations.Length + 1, $"{name} label");
                            break;
                        }
                        long value = operation.Operands[o++];
                        int count = shape[i] switch
                        {
                            OperandKind.Local => function.LocalTypes.Length,
                            OperandKind.Argument => function.ArgumentCount,
                            OperandKind.Field => module.Fields.Length,
                            OperandKind.Function => module.Functions.Length,
                            OperandKind.Type => types,
                            OperandKind.Constant => module.Constants.Length,
                            OperandKind.Label => operations.Length + 1,
                            _ => -1,
                        };
                        if (count >= 0)
                            Index(value is >= int.MinValue and <= int.MaxValue ? (int)value : -1, count, $"{name} {shape[i].Prefix()}operand");
                    }
                }
            }
        }

        private static IlModule ReadChecked(ReadOnlySpan<byte> file)
        {
            if (file.Length < Magic.Length + 1 || !file[..Magic.Length].SequenceEqual(Magic))
                throw new InvalidDataException("not a Larva module (missing LRVA header)");
            if (file[Magic.Length] != Version)
                throw new InvalidDataException($"unsupported module version {file[Magic.Length]} (expected {Version})");

            NibbleReader reader = new(file[(Magic.Length + 1)..]);
            var mode = (GcMode)reader.ReadNibble();
            var capabilities = (GcCapabilities)reader.ReadUnsigned();
            string plugin = IrDecoder.ReadName(ref reader);
            var gc = new GcConfig(mode, capabilities, plugin.Length == 0 ? null : plugin);
            int entry = checked((int)reader.ReadSigned());

            var types = new IrType[Count(ref reader)];
            for (int i = 0; i < types.Length; i++)
                types[i] = IrDecoder.ReadType(ref reader);

            var fields = new FieldRef[Count(ref reader)];
            for (int i = 0; i < fields.Length; i++)
            {
                var owner = IrDecoder.ReadType(ref reader) as StructType ?? throw new InvalidDataException("field owner is not a struct");
                fields[i] = new FieldRef(owner, Count(ref reader));
            }

            var constants = new PoolConstant[Count(ref reader)];
            for (int i = 0; i < constants.Length; i++)
            {
                var type = IrDecoder.ReadType(ref reader);
                constants[i] = new PoolConstant(reader.ReadSignedBig(type.BitWidth ?? 512), type);
            }

            var witnesses = new WitnessInfo[Count(ref reader)];
            for (int i = 0; i < witnesses.Length; i++)
            {
                int type = Count(ref reader);
                witnesses[i] = new WitnessInfo(type, ReadLayout(ref reader));
            }

            var functions = new IlFunction[Count(ref reader)];
            for (int i = 0; i < functions.Length; i++)
                functions[i] = ReadFunction(ref reader);

            var metadata = MetadataEncoding.Decode(ReadBytes(ref reader));
            var data = new StaticData[IrDecoder.ReadCount(ref reader)];
            for (int i = 0; i < data.Length; i++)
                data[i] = IrDecoder.ReadStaticData(ref reader);
            var globals = new GlobalDef[IrDecoder.ReadCount(ref reader)];
            for (int i = 0; i < globals.Length; i++)
                globals[i] = IrDecoder.ReadGlobal(ref reader);
            return new IlModule(types, fields, constants, witnesses, functions) { Metadata = metadata, Gc = gc, Entry = entry, Data = data, Globals = globals };
        }

        public static void Save(IlModule module, string path) => File.WriteAllBytes(path, Write(module));

        public static IlModule Load(string path) => Read(File.ReadAllBytes(path));

        // ── Functions ──────────────────────────────────────────────

        private static void WriteFunction(NibbleWriter writer, IlFunction function)
        {
            IrEncoder.WriteName(writer, function.Name);
            writer.WriteUnsigned((ulong)function.ArgumentCount);
            writer.WriteUnsigned((ulong)function.TypeParameterCount);
            WriteInts(writer, function.ArgumentTypes);
            WriteInts(writer, function.LocalTypes);
            WriteRoots(writer, function.GcRoots);

            writer.WriteUnsigned((ulong)function.Safepoints.Length);
            foreach (var safepoint in function.Safepoints)
            {
                writer.WriteUnsigned((ulong)safepoint.Operation);
                WriteRoots(writer, safepoint.Roots);
            }

            writer.WriteUnsigned((ulong)function.Handlers.Length);
            foreach (var handler in function.Handlers)
            {
                writer.WriteUnsigned((ulong)handler.Start);
                writer.WriteUnsigned((ulong)handler.End);
                writer.WriteNibble(handler.CatchType is null ? 0 : 1);
                if (handler.CatchType is int type)
                    writer.WriteUnsigned((ulong)type);
                writer.WriteUnsigned((ulong)handler.Handler);
            }

            writer.WriteNibble(function.Native is null ? 0 : function.Native.Library is null ? 1 : 2);
            if (function.Native is { Library: { } library, Symbol: { } symbol })
            {
                IrEncoder.WriteName(writer, library);
                IrEncoder.WriteName(writer, symbol);
            }

            WriteBytes(writer, function.Body.Encode());
        }

        private static IlFunction ReadFunction(ref NibbleReader reader)
        {
            string name = IrDecoder.ReadName(ref reader);
            int arguments = Count(ref reader);
            int typeParameters = Count(ref reader);
            var argumentTypes = ReadInts(ref reader);
            var localTypes = ReadInts(ref reader);
            var roots = ReadRoots(ref reader);

            var safepoints = new Safepoint[Count(ref reader)];
            for (int i = 0; i < safepoints.Length; i++)
            {
                int operation = Count(ref reader);
                safepoints[i] = new Safepoint(operation, ReadRoots(ref reader));
            }

            var handlers = new ExceptionHandler[Count(ref reader)];
            for (int i = 0; i < handlers.Length; i++)
            {
                int start = Count(ref reader), end = Count(ref reader);
                int? type = reader.ReadNibble() != 0 ? Count(ref reader) : null;
                handlers[i] = new ExceptionHandler(start, end, type, Count(ref reader));
            }

            int nativeKind = reader.ReadNibble();
            NativeCall? native = nativeKind switch
            {
                1 => new NativeCall(null, null),
                2 => new NativeCall(IrDecoder.ReadName(ref reader), IrDecoder.ReadName(ref reader)),
                _ => null,
            };
            var body = OperationBlock.Decode(name, ReadBytes(ref reader));
            return new IlFunction(name, arguments, localTypes, body)
            {
                TypeParameterCount = typeParameters,
                ArgumentTypes = argumentTypes,
                GcRoots = roots,
                Safepoints = safepoints,
                Handlers = handlers,
                Native = native,
            };
        }

        // ── Pieces ─────────────────────────────────────────────────

        private static void WriteRoots(NibbleWriter writer, GcRoot[] roots)
        {
            writer.WriteUnsigned((ulong)roots.Length);
            foreach (var root in roots)
            {
                writer.WriteNibble(root.IsArgument ? 1 : 0);
                writer.WriteUnsigned((ulong)root.Slot);
                writer.WriteNibble(root.Offsets is null ? 0 : 1);
                if (root.Offsets is { } offsets)
                    WriteInts(writer, offsets);
            }
        }

        private static GcRoot[] ReadRoots(ref NibbleReader reader)
        {
            var roots = new GcRoot[Count(ref reader)];
            for (int i = 0; i < roots.Length; i++)
            {
                bool isArgument = reader.ReadNibble() != 0;
                int slot = Count(ref reader);
                int[]? offsets = reader.ReadNibble() != 0 ? ReadValues(ref reader) : null;
                roots[i] = new GcRoot(isArgument, slot, offsets);
            }
            return roots;
        }

        private static void WriteLayout(NibbleWriter writer, TypeLayout layout)
        {
            writer.WriteUnsigned((ulong)layout.Size);
            writer.WriteUnsigned((ulong)layout.Align);
            writer.WriteNibble((int)layout.Flags);
            WriteInts(writer, layout.GcOffsets);
        }

        private static TypeLayout ReadLayout(ref NibbleReader reader)
        {
            int size = IrDecoder.ReadInt(ref reader, "layout size"), align = IrDecoder.ReadInt(ref reader, "layout alignment");
            var flags = (WitnessFlags)reader.ReadNibble();
            return new TypeLayout(size, align, flags, ReadValues(ref reader));
        }

        private static void WriteInts(NibbleWriter writer, int[] values)
        {
            writer.WriteUnsigned((ulong)values.Length);
            foreach (int value in values)
                writer.WriteUnsigned((ulong)value);
        }

        /// <summary>Indexes (type tokens).</summary>
        private static int[] ReadInts(ref NibbleReader reader)
        {
            var values = new int[Count(ref reader)];
            for (int i = 0; i < values.Length; i++)
                values[i] = Count(ref reader);
            return values;
        }

        /// <summary>Values (byte offsets): not bounded by the stream's size.</summary>
        private static int[] ReadValues(ref NibbleReader reader)
        {
            var values = new int[Count(ref reader)];
            for (int i = 0; i < values.Length; i++)
                values[i] = IrDecoder.ReadInt(ref reader, "offset");
            return values;
        }

        private static void WriteBytes(NibbleWriter writer, byte[] bytes)
        {
            writer.WriteUnsigned((ulong)bytes.Length);
            foreach (byte b in bytes)
                writer.WriteByte(b);
        }

        private static byte[] ReadBytes(ref NibbleReader reader)
        {
            var bytes = new byte[Count(ref reader)];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = reader.ReadByte();
            return bytes;
        }

        private static int Count(ref NibbleReader reader) => IrDecoder.ReadCount(ref reader);
    }
}

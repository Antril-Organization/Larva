using Larva.IL.Encoding;
using Larva.IR.Encoding;
using Larva.IR.Lowering;

namespace Larva.IR.Reflection
{
    /// <summary>
    /// Nibble encoding of <see cref="ModuleMetadata"/>, reusing the IR's type / generic / name encodings:
    /// <code>
    /// metadata = structs:uv struct* methods:uv method*
    /// struct   = name level:4 generics fields:uv type* names:uv name* instanceOf:4 [type] layout:4 [size:uv align:uv flags:4 gc:uv uv*] offsets:uv uv*
    /// method   = name generics params:uv type* return:type shared:4 function:sv stub:sv shape instances:uv {args:uv type* function:uv stub:uv shape}*
    /// </code>
    /// </summary>
    public static class MetadataEncoding
    {
        public static byte[] Encode(ModuleMetadata metadata)
        {
            NibbleWriter writer = new();
            writer.WriteUnsigned((ulong)metadata.Structs.Length);
            foreach (var def in metadata.Structs)
                WriteStruct(writer, def);
            writer.WriteUnsigned((ulong)metadata.Methods.Length);
            foreach (var method in metadata.Methods)
                WriteMethod(writer, method);
            return writer.ToArray();
        }

        public static ModuleMetadata Decode(ReadOnlySpan<byte> bytes)
        {
            NibbleReader reader = new(bytes);
            var structs = new StructMetadata[IrDecoder.ReadCount(ref reader)];
            for (int i = 0; i < structs.Length; i++)
                structs[i] = ReadStruct(ref reader);
            var methods = new MethodMetadata[IrDecoder.ReadCount(ref reader)];
            for (int i = 0; i < methods.Length; i++)
                methods[i] = ReadMethod(ref reader);
            return new ModuleMetadata(structs, methods);
        }

        // ── Structs ────────────────────────────────────────────────

        private static void WriteStruct(NibbleWriter writer, StructMetadata def)
        {
            IrEncoder.WriteName(writer, def.Name);
            writer.WriteNibble((int)def.Level);
            IrEncoder.WriteGenerics(writer, def.Generics);
            WriteTypes(writer, def.Fields);
            writer.WriteUnsigned((ulong)def.FieldNames.Length);
            foreach (var name in def.FieldNames)
                IrEncoder.WriteName(writer, name);

            writer.WriteNibble(def.InstanceOf is null ? 0 : 1);
            if (def.InstanceOf is { } instance)
                IrEncoder.WriteType(writer, instance);

            writer.WriteNibble(def.Layout is null ? 0 : 1);
            if (def.Layout is { } layout)
            {
                writer.WriteUnsigned((ulong)layout.Size);
                writer.WriteUnsigned((ulong)layout.Align);
                writer.WriteNibble((int)layout.Flags);
                WriteInts(writer, layout.GcOffsets);
            }
            WriteInts(writer, def.FieldOffsets);
        }

        private static StructMetadata ReadStruct(ref NibbleReader reader)
        {
            string name = IrDecoder.ReadName(ref reader);
            var level = ReadLevel(ref reader);
            var generics = IrDecoder.ReadGenerics(ref reader);
            var fields = ReadTypes(ref reader);
            var names = new string[IrDecoder.ReadCount(ref reader)];
            for (int i = 0; i < names.Length; i++)
                names[i] = IrDecoder.ReadName(ref reader);

            StructType? instanceOf = null;
            if (reader.ReadNibble() != 0)
                instanceOf = IrDecoder.ReadType(ref reader) as StructType
                    ?? throw new InvalidDataException($"Struct instance origin is not a struct type (nibble {reader.Position}).");

            TypeLayout? layout = null;
            if (reader.ReadNibble() != 0)
            {
                int size = IrDecoder.ReadCount(ref reader);
                int align = IrDecoder.ReadCount(ref reader);
                var flags = (WitnessFlags)reader.ReadNibble();
                layout = new TypeLayout(size, align, flags, ReadInts(ref reader));
            }

            return new StructMetadata(name, level, fields)
            {
                Generics = generics,
                FieldNames = names,
                InstanceOf = instanceOf,
                Layout = layout,
                FieldOffsets = ReadInts(ref reader),
            };
        }

        // ── Methods ────────────────────────────────────────────────

        private static void WriteMethod(NibbleWriter writer, MethodMetadata method)
        {
            IrEncoder.WriteName(writer, method.Name);
            IrEncoder.WriteGenerics(writer, method.Generics);
            WriteTypes(writer, method.Parameters);
            IrEncoder.WriteType(writer, method.Return);
            writer.WriteNibble(method.Shared ? 1 : 0);
            writer.WriteSigned(method.Function);
            writer.WriteSigned(method.Stub);
            IrEncoder.WriteName(writer, method.Shape);
            writer.WriteUnsigned((ulong)method.Instances.Length);
            foreach (var instance in method.Instances)
            {
                WriteTypes(writer, instance.Arguments);
                writer.WriteUnsigned((ulong)instance.Function);
                writer.WriteUnsigned((ulong)instance.Stub);
                IrEncoder.WriteName(writer, instance.Shape);
            }
        }

        private static MethodMetadata ReadMethod(ref NibbleReader reader)
        {
            string name = IrDecoder.ReadName(ref reader);
            var generics = IrDecoder.ReadGenerics(ref reader);
            var parameters = ReadTypes(ref reader);
            var result = IrDecoder.ReadType(ref reader);
            bool shared = reader.ReadNibble() != 0;
            int function = checked((int)reader.ReadSigned());
            int stub = checked((int)reader.ReadSigned());
            string shape = IrDecoder.ReadName(ref reader);
            var instances = new MethodInstance[IrDecoder.ReadCount(ref reader)];
            for (int i = 0; i < instances.Length; i++)
            {
                var arguments = ReadTypes(ref reader);
                int instanceFunction = IrDecoder.ReadCount(ref reader);
                int instanceStub = IrDecoder.ReadCount(ref reader);
                instances[i] = new MethodInstance(arguments, instanceFunction, instanceStub, IrDecoder.ReadName(ref reader));
            }
            return new MethodMetadata(name, parameters, result)
            {
                Generics = generics,
                Shared = shared,
                Function = function,
                Stub = stub,
                Shape = shape,
                Instances = instances,
            };
        }

        // ── Helpers ────────────────────────────────────────────────

        private static ReflectionLevel ReadLevel(ref NibbleReader reader)
        {
            int level = reader.ReadNibble();
            return level <= (int)ReflectionLevel.Full
                ? (ReflectionLevel)level
                : throw new InvalidDataException($"Unknown reflection level {level} at nibble {reader.Position}.");
        }

        private static void WriteTypes(NibbleWriter writer, IrType[] types)
        {
            writer.WriteUnsigned((ulong)types.Length);
            foreach (var type in types)
                IrEncoder.WriteType(writer, type);
        }

        private static IrType[] ReadTypes(ref NibbleReader reader)
        {
            var types = new IrType[IrDecoder.ReadCount(ref reader)];
            for (int i = 0; i < types.Length; i++)
                types[i] = IrDecoder.ReadType(ref reader);
            return types;
        }

        private static void WriteInts(NibbleWriter writer, int[] values)
        {
            writer.WriteUnsigned((ulong)values.Length);
            foreach (int value in values)
                writer.WriteUnsigned((ulong)value);
        }

        private static int[] ReadInts(ref NibbleReader reader)
        {
            var values = new int[IrDecoder.ReadCount(ref reader)];
            for (int i = 0; i < values.Length; i++)
                values[i] = IrDecoder.ReadCount(ref reader);
            return values;
        }
    }
}
